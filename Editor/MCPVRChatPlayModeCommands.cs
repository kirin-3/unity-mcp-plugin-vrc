using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Play-mode avatar testing through the avatar emulators (Gesture Manager or Lyuma's Av3Emulator):
    /// - vrc/avatar/playmode/status: which emulator drives the avatar, and its parameter values
    /// - vrc/avatar/playmode/set: set expression parameters and hand gestures through the emulator
    /// - vrc/avatar/playmode/capture: render the avatar from a framed camera as a base64 PNG
    ///
    /// Parameters must go through the emulator: both drive the avatar's animator layers from their own
    /// playable graphs and write their own parameter values every frame, so setting the Animator component
    /// directly would be overwritten (or never reach the FX layer at all). Both emulators are reached by
    /// reflection so the plugin compiles without them.
    /// </summary>
    public static class MCPVRChatPlayModeCommands
    {
        private const string Av3RuntimeType = "Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime";
        private const string Av3ControlType = "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator";
        private const string GestureManagerType = "BlackStartX.GestureManager.GestureManager";

        private const string AddEmulatorHint =
            "Add one in edit mode (menu 'Tools/Gesture Manager Emulator' or 'Tools/Avatars 3.0 Emulator/Enable', e.g. with unity_execute_menu_item), " +
            "then enter play mode; the emulator attaches a few frames after play starts.";

        private static readonly string[] GestureNames =
            { "Neutral", "Fist", "HandOpen", "Fingerpoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };

        private const int MaxListedParameters = 400;

        // ─────────────────────────────────────────────────────────────
        // Routes
        // ─────────────────────────────────────────────────────────────

        public static object GetStatus(Dictionary<string, object> args)
        {
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "frame", Time.frameCount }
            };

            if (!EditorApplication.isPlaying)
            {
                // Which emulator will drive the avatar once play starts, so a caller can tell before paying
                // for a play-mode round trip that nothing would.
                var inScene = EmulatorsInScene();
                bool compileErrors = EditorUtility.scriptCompilationFailed;
                result["ready"] = false;
                result["emulatorsInScene"] = inScene;
                // Set right after a play request until Unity switches, or refuses to (e.g. on compile errors).
                result["enteringPlayMode"] = EditorApplication.isPlayingOrWillChangePlaymode;
                result["compileErrors"] = compileErrors;
                if (compileErrors)
                    result["hint"] = "Unity cannot enter play mode while scripts have compile errors. Read them with unity_get_compilation_errors.";
                else
                    result["hint"] = inScene.Count > 0
                        ? $"Not in play mode. {string.Join(" and ", inScene)} will drive the avatar once play mode starts."
                        : "Not in play mode, and the open scene has no active Gesture Manager or Av3Emulator. " + AddEmulatorHint;
                return result;
            }

            var emulator = ResolveEmulator(args, out string error);
            if (emulator == null)
            {
                result["ready"] = false;
                result["emulatorsInScene"] = EmulatorsInScene(); // empty: waiting longer will not help
                result["hint"] = error;
                return result;
            }

            var parameters = emulator.ListParameters();
            result["emulator"] = emulator.Name;
            result["avatar"] = emulator.Avatar.name;
            result["ready"] = parameters.Count > 0;

            var names = ReadNames(args);
            IEnumerable<ParameterValue> selected = parameters;
            if (names != null)
            {
                var byName = ByName(parameters);
                selected = names.Where(byName.ContainsKey).Select(n => byName[n]);
                var unknown = names.Where(n => !byName.ContainsKey(n)).ToList();
                if (unknown.Count > 0) result["unknownNames"] = unknown;
            }

            var list = selected.Take(MaxListedParameters).Select(p => p.ToDictionary()).ToList();
            result["parameterCount"] = parameters.Count;
            result["parameters"] = list;
            if (names == null && parameters.Count > MaxListedParameters)
                result["truncated"] = true;

            var left = parameters.FirstOrDefault(p => p.Name == "GestureLeft");
            var right = parameters.FirstOrDefault(p => p.Name == "GestureRight");
            if (left != null) result["gestureLeft"] = GestureLabel((int)left.Value);
            if (right != null) result["gestureRight"] = GestureLabel((int)right.Value);
            return result;
        }

        public static object Set(Dictionary<string, object> args)
        {
            if (!EditorApplication.isPlaying)
            {
                var notPlaying = MCPVRChatUtil.Fail("Not in play mode. Enter play mode (unity_play_mode action 'play') with Gesture Manager or Av3Emulator in the scene, then set parameters.");
                notPlaying["notReady"] = true;
                return notPlaying;
            }

            var emulator = ResolveEmulator(args, out string error);
            if (emulator == null)
            {
                var noEmulator = MCPVRChatUtil.Fail(error);
                noEmulator["notReady"] = true;
                return noEmulator;
            }

            var known = ByName(emulator.ListParameters());
            var requests = new List<(string name, object raw)>();

            if (MCPVRChatUtil.Has(args, "parameters"))
            {
                if (!(args["parameters"] is Dictionary<string, object> parameterArgs))
                    return MCPVRChatUtil.Fail("'parameters' must be an object mapping parameter names to values, e.g. {\"Jacket\": true}.");
                foreach (var kv in parameterArgs) requests.Add((kv.Key, kv.Value));
            }

            // Gestures are the built-in GestureLeft/GestureRight parameters (0-7), accepted by name too.
            foreach (var (argName, paramName) in new[] { ("gestureLeft", "GestureLeft"), ("gestureRight", "GestureRight") })
            {
                if (!MCPVRChatUtil.Has(args, argName)) continue;
                if (!TryParseGesture(args[argName], out int gesture))
                    return MCPVRChatUtil.Fail($"'{argName}' must be 0-7 or one of {string.Join(", ", GestureNames)}.");
                requests.Add((paramName, gesture));
            }
            foreach (var (argName, paramName) in new[] { ("gestureLeftWeight", "GestureLeftWeight"), ("gestureRightWeight", "GestureRightWeight") })
            {
                if (MCPVRChatUtil.Has(args, argName)) requests.Add((paramName, args[argName]));
            }

            if (requests.Count == 0)
                return MCPVRChatUtil.Fail("Nothing to set: pass 'parameters' and/or 'gestureLeft'/'gestureRight'.");

            // Validate every value first; nothing is applied if any entry is wrong.
            var typed = new List<(ParameterValue param, object value)>();
            var errors = new List<string>();
            foreach (var (name, raw) in requests)
            {
                if (!known.TryGetValue(name, out var param))
                {
                    param = emulator.BuiltinParameter(name);
                    if (param == null)
                    {
                        var suggestions = MCPVRChatUtil.Suggest(name, known.Keys);
                        errors.Add($"'{name}' is not a parameter of '{emulator.Avatar.name}'" +
                                   (suggestions.Count > 0 ? $" (did you mean {string.Join(", ", suggestions)}?)" : "") + ".");
                        continue;
                    }
                }

                if (!TryCoerce(param.Type, raw, out object value))
                {
                    errors.Add($"'{name}' is a {param.Type} parameter; '{raw}' is not a valid {param.Type} value.");
                    continue;
                }
                typed.Add((param, value));
            }
            if (errors.Count > 0)
            {
                var failed = MCPVRChatUtil.Fail(string.Join(" ", errors) + " Nothing was changed.");
                failed["errors"] = errors;
                return failed;
            }

            var applied = new List<object>();
            foreach (var (param, value) in typed)
            {
                if (!emulator.TrySet(param, value, out string setError))
                    return MCPVRChatUtil.Fail($"{emulator.Name} rejected '{param.Name}': {setError}");
                applied.Add(new Dictionary<string, object>
                {
                    { "name", param.Name },
                    { "type", param.Type },
                    { "previous", param.ValueForJson() },
                    { "value", value }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "emulator", emulator.Name },
                { "avatar", emulator.Avatar.name },
                { "frame", Time.frameCount },
                { "applied", applied }
            };
        }

        public static object Capture(Dictionary<string, object> args)
        {
            GameObject avatar = null;
            string emulatorName = null;
            if (EditorApplication.isPlaying && !MCPVRChatUtil.Has(args, "avatarPath"))
            {
                var emulator = ResolveEmulator(args, out _);
                if (emulator != null)
                {
                    avatar = emulator.Avatar;
                    emulatorName = emulator.Name;
                }
            }
            if (avatar == null)
            {
                avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
                if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");
            }

            int width = 512, height = 512;
            if (MCPVRChatUtil.Has(args, "width") && !MCPVRChatUtil.TryReadInt(args["width"], out width))
                return MCPVRChatUtil.Fail("'width' must be an integer.");
            if (MCPVRChatUtil.Has(args, "height") && !MCPVRChatUtil.TryReadInt(args["height"], out height))
                return MCPVRChatUtil.Fail("'height' must be an integer.");
            width = Mathf.Clamp(width, 64, 2048);
            height = Mathf.Clamp(height, 64, 2048);

            string view = (MCPVRChatUtil.GetString(args, "view") ?? "front").Trim().ToLowerInvariant();
            if (view != "front" && view != "back" && view != "left" && view != "right" && view != "face")
                return MCPVRChatUtil.Fail($"view '{view}' must be 'front', 'back', 'left', 'right' or 'face'.");

            if (!TryGetRenderBounds(avatar, out Bounds bounds))
                return MCPVRChatUtil.Fail($"'{avatar.name}' has no active mesh renderers to capture.");

            Transform t = avatar.transform;
            Vector3 target = bounds.center;
            float halfHeight = bounds.extents.y;
            float halfWidth = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float depth = Mathf.Max(bounds.extents.x, bounds.extents.z);
            if (view == "face")
            {
                target = GetHeadPosition(avatar, bounds);
                halfHeight = Mathf.Max(0.1f, bounds.size.y * 0.09f);
                halfWidth = halfHeight;
                depth = halfHeight;
            }

            Vector3 flatForward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward;
            flatForward.Normalize();
            Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);
            Vector3 direction;
            switch (view)
            {
                case "back": direction = -flatForward; break;
                case "left": direction = -flatRight; break;
                case "right": direction = flatRight; break;
                default: direction = flatForward; break; // front and face: the avatar faces the camera
            }

            const float fieldOfView = 30f;
            float aspect = (float)width / height;
            float tanHalf = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            float distance = Mathf.Max(halfHeight / tanHalf, halfWidth / (tanHalf * aspect)) * 1.08f + depth;

            var cameraObject = new GameObject("__MCP_AvatarCapture") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                var cam = cameraObject.AddComponent<Camera>();
                cam.enabled = false; // rendered by hand below; never becomes an emulator's "main camera"
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.32f, 0.34f, 1f);
                cam.fieldOfView = fieldOfView;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = distance + bounds.size.magnitude * 2f + 10f;
                // Leave out UI (5) and VRChat's MirrorReflection layer (18), where emulators park mirror clones.
                cam.cullingMask = ~((1 << 5) | (1 << 18));
                cam.transform.position = target + direction * distance;
                cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);

                rt = new RenderTexture(width, height, 24);
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "base64", Convert.ToBase64String(tex.EncodeToPNG()) },
                    { "width", width },
                    { "height", height },
                    { "view", view },
                    { "avatar", avatar.name },
                    { "isPlaying", EditorApplication.isPlaying },
                    { "frame", Time.frameCount }
                };
                if (emulatorName != null) result["emulator"] = emulatorName;
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Emulator discovery
        // ─────────────────────────────────────────────────────────────

        private static Emulator ResolveEmulator(Dictionary<string, object> args, out string error)
        {
            error = null;
            GameObject requested = null;
            if (MCPVRChatUtil.Has(args, "avatarPath"))
            {
                requested = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out error);
                if (requested == null) return null;
            }

            var candidates = new List<Emulator>();

            Type av3Type = MCPVRChatUtil.FindType(Av3RuntimeType);
            if (av3Type != null)
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(av3Type))
                {
                    if (obj is Component runtime && runtime != null && Av3Emulator.IsPrimary(runtime))
                        candidates.Add(new Av3Emulator(runtime));
                }
            }

            Type gmType = GestureManagerRuntimeType();
            if (gmType?.GetField("ControlledAvatars", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is IDictionary controlled)
            {
                foreach (DictionaryEntry entry in controlled)
                {
                    if (!(entry.Key is GameObject go) || go == null || entry.Value == null) continue;
                    if (candidates.Any(c => c.Avatar == go)) continue; // Av3Emulator owns an avatar it also runs
                    if (GestureManagerEmulator.TryCreate(go, entry.Value, out var gm)) candidates.Add(gm);
                }
            }

            if (requested != null)
            {
                var match = candidates.FirstOrDefault(c => c.Avatar == requested);
                if (match == null)
                {
                    // Waiting does not help once an emulator runs another avatar: say which one to ask for.
                    error = candidates.Count > 0
                        ? $"The emulator drives {string.Join(", ", candidates.Select(c => $"'{c.Avatar.name}'"))}, not '{requested.name}'. " +
                          "Pass that avatarPath, or exit play mode and leave only this avatar active (Gesture Manager drives one avatar at a time, chosen in its inspector)."
                        : $"No Gesture Manager or Av3Emulator is driving '{requested.name}'. " + NoEmulatorAdvice();
                }
                return match;
            }

            if (candidates.Count == 0)
            {
                error = "No Gesture Manager or Av3Emulator is driving an avatar. " + NoEmulatorAdvice();
                return null;
            }

            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                var bySelection = candidates.FirstOrDefault(c => selected.transform.IsChildOf(c.Avatar.transform));
                if (bySelection != null) return bySelection;
            }
            return candidates[0];
        }

        private static Type GestureManagerRuntimeType()
        {
            return MCPVRChatUtil.FindType(GestureManagerType)
                ?? MCPVRChatUtil.FindTypeBySimpleName("GestureManager",
                    t => t.GetField("ControlledAvatars", BindingFlags.Public | BindingFlags.Static) != null);
        }

        /// <summary>
        /// The emulators active in the open scenes: Av3Emulator's control object or a Gesture Manager. Either one
        /// takes over the avatars when play mode starts.
        /// </summary>
        private static List<string> EmulatorsInScene()
        {
            var found = new List<string>();
            foreach (var (type, label) in new[] { (MCPVRChatUtil.FindType(Av3ControlType), "Av3Emulator"), (GestureManagerRuntimeType(), "GestureManager") })
            {
                if (type != null && UnityEngine.Object.FindObjectsOfType(type).Length > 0) found.Add(label);
            }
            return found;
        }

        /// <summary>What to do when play mode runs but no emulator drives the avatar.</summary>
        private static string NoEmulatorAdvice()
        {
            var inScene = EmulatorsInScene();
            return inScene.Count > 0
                ? $"{string.Join(" and ", inScene)} is in the scene and attaches a few frames after play starts; retry shortly. " +
                  "Gesture Manager drives one avatar at a time, chosen in its inspector."
                : "The scene has no active emulator. Exit play mode, then " + char.ToLowerInvariant(AddEmulatorHint[0]) + AddEmulatorHint.Substring(1);
        }

        // ─────────────────────────────────────────────────────────────
        // Emulator adapters
        // ─────────────────────────────────────────────────────────────

        private sealed class ParameterValue
        {
            public string Name;
            public string Type; // Bool, Int or Float
            public float Value;
            public bool Builtin;
            public object Handle;

            public object ValueForJson()
            {
                if (Type == "Bool") return Value != 0f;
                if (Type == "Int") return (int)Value;
                return Value;
            }

            public Dictionary<string, object> ToDictionary()
            {
                var d = new Dictionary<string, object> { { "name", Name }, { "type", Type }, { "value", ValueForJson() } };
                if (Builtin) d["builtin"] = true;
                return d;
            }
        }

        private abstract class Emulator
        {
            public GameObject Avatar;
            public abstract string Name { get; }
            public abstract List<ParameterValue> ListParameters();
            public virtual ParameterValue BuiltinParameter(string name) => null;
            public abstract bool TrySet(ParameterValue param, object value, out string error);
        }

        /// <summary>Lyuma's Av3Emulator: LyumaAv3Runtime on the avatar keeps Bools/Ints/Floats lists.</summary>
        private sealed class Av3Emulator : Emulator
        {
            private readonly Component _runtime;

            public Av3Emulator(Component runtime)
            {
                _runtime = runtime;
                Avatar = runtime.gameObject;
            }

            public override string Name => "Av3Emulator";

            /// <summary>The local avatar, not the mirror/shadow copies or non-local clones.</summary>
            public static bool IsPrimary(Component runtime)
            {
                bool Flag(string field) => MCPVRChatUtil.GetFieldValue(runtime, field) is bool b && b;
                bool hasLocalFlag = MCPVRChatUtil.GetField(runtime.GetType(), "IsLocal") != null;
                return (!hasLocalFlag || Flag("IsLocal")) && !Flag("IsMirrorClone") && !Flag("IsShadowClone");
            }

            public override List<ParameterValue> ListParameters()
            {
                var result = new List<ParameterValue>();
                var builtins = BuiltinDefinitions();
                foreach (var (listField, type) in new[] { ("Bools", "Bool"), ("Ints", "Int"), ("Floats", "Float") })
                {
                    if (!(MCPVRChatUtil.GetFieldValue(_runtime, listField) is IList list)) continue;
                    foreach (var item in list)
                    {
                        if (item == null) continue;
                        string name = MCPVRChatUtil.GetFieldValue(item, "name") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        MCPVRChatUtil.TryReadFloat(MCPVRChatUtil.GetFieldValue(item, "value"), out float value);
                        result.Add(new ParameterValue
                        {
                            Name = name, Type = type, Value = value, Handle = item,
                            Builtin = builtins.ContainsKey(name)
                        });
                    }
                }
                return result;
            }

            public override ParameterValue BuiltinParameter(string name)
            {
                if (!BuiltinDefinitions().TryGetValue(name, out var definition)) return null;
                string type = MCPVRChatUtil.GetFieldValue(definition, "type")?.ToString() ?? "Float";
                if (type != "Bool" && type != "Int") type = "Float";
                float value = 0f;
                if (MCPVRChatUtil.GetFieldValue(definition, "valueGetter") is Delegate getter)
                {
                    try { MCPVRChatUtil.TryReadFloat(getter.DynamicInvoke(_runtime), out value); }
                    catch { value = 0f; }
                }
                return new ParameterValue { Name = name, Type = type, Value = value, Builtin = true, Handle = definition };
            }

            public override bool TrySet(ParameterValue param, object value, out string error)
            {
                error = null;

                // Built-ins (gestures, Seated, AFK…) are copied from runtime fields every frame, so they
                // are set through the emulator's own setter rather than in the parameter lists.
                if (BuiltinDefinitions().TryGetValue(param.Name, out var definition)
                    && MCPVRChatUtil.GetFieldValue(definition, "valueSetter") is Delegate setter)
                {
                    // The setter unboxes to the built-in's own type (int, float or bool).
                    string builtinType = MCPVRChatUtil.GetFieldValue(definition, "type")?.ToString();
                    if (builtinType != "Bool" && builtinType != "Int") builtinType = "Float";
                    if (!TryCoerce(builtinType, value, out object converted))
                    {
                        error = $"expects a {builtinType} value";
                        return false;
                    }
                    try
                    {
                        setter.DynamicInvoke(_runtime, converted);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
                        return false;
                    }
                }

                if (param.Handle == null)
                {
                    error = "parameter entry not found";
                    return false;
                }

                if (param.Type == "Float")
                {
                    // exportedValue sets value, expressionValue and the change marker together, the way
                    // the emulator's own radial menu does; older versions only have the fields.
                    var exported = param.Handle.GetType().GetProperty("exportedValue", BindingFlags.Public | BindingFlags.Instance);
                    if (exported != null && exported.CanWrite) exported.SetValue(param.Handle, value, null);
                    else
                    {
                        MCPVRChatUtil.SetFieldValue(param.Handle, "expressionValue", value);
                        MCPVRChatUtil.SetFieldValue(param.Handle, "value", value);
                    }
                    return true;
                }

                if (!MCPVRChatUtil.SetFieldValue(param.Handle, "value", value))
                {
                    error = "parameter entry has no 'value' field";
                    return false;
                }
                return true;
            }

            private Dictionary<string, object> BuiltinDefinitions()
            {
                var result = new Dictionary<string, object>();
                var field = _runtime.GetType().GetField("BUILTIN_PARAMETERS", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is IEnumerable definitions)
                {
                    foreach (var definition in definitions)
                    {
                        if (MCPVRChatUtil.GetFieldValue(definition, "name") is string name && !result.ContainsKey(name))
                            result[name] = definition;
                    }
                }
                return result;
            }
        }

        /// <summary>Gesture Manager: its ModuleVrc3 holds Params (name → Vrc3Param), set via Vrc3Param.Set(module, float).</summary>
        private sealed class GestureManagerEmulator : Emulator
        {
            private readonly object _module;
            private readonly IDictionary _params;

            private GestureManagerEmulator(GameObject avatar, object module, IDictionary parameters)
            {
                Avatar = avatar;
                _module = module;
                _params = parameters;
            }

            public static bool TryCreate(GameObject avatar, object module, out GestureManagerEmulator emulator)
            {
                emulator = null;
                if (!(MCPVRChatUtil.GetFieldValue(module, "Params") is IDictionary parameters)) return false;
                emulator = new GestureManagerEmulator(avatar, module, parameters);
                return true;
            }

            public override string Name => "GestureManager";

            public override List<ParameterValue> ListParameters()
            {
                var result = new List<ParameterValue>();
                foreach (DictionaryEntry entry in _params)
                {
                    if (!(entry.Key is string name) || entry.Value == null) continue;
                    var param = entry.Value;
                    string type = MCPVRChatUtil.GetFieldValue(param, "Type")?.ToString() ?? "Float";
                    if (type == "Trigger") type = "Bool";
                    float value = 0f;
                    try
                    {
                        var floatValue = param.GetType().GetMethod("FloatValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (floatValue != null) MCPVRChatUtil.TryReadFloat(floatValue.Invoke(param, null), out value);
                    }
                    catch { value = 0f; }
                    result.Add(new ParameterValue { Name = name, Type = type, Value = value, Handle = param });
                }
                return result;
            }

            public override bool TrySet(ParameterValue param, object value, out string error)
            {
                error = null;
                var target = param.Handle;
                var set = target?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType.IsInstanceOfType(_module)
                        && m.GetParameters()[1].ParameterType == typeof(float));
                if (set == null)
                {
                    error = "Vrc3Param.Set(module, float, source) was not found; this Gesture Manager version is not one this route knows how to drive.";
                    return false;
                }

                float numeric;
                if (value is bool b) numeric = b ? 1f : 0f;
                else if (value is int i) numeric = i;
                else numeric = (float)value;

                try
                {
                    set.Invoke(target, new object[] { _module, numeric, null });
                    return true;
                }
                catch (Exception ex)
                {
                    error = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
                    return false;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>First entry per name (Av3Emulator can list one name under two types).</summary>
        private static Dictionary<string, ParameterValue> ByName(IEnumerable<ParameterValue> parameters)
        {
            var result = new Dictionary<string, ParameterValue>();
            foreach (var p in parameters)
            {
                if (!result.ContainsKey(p.Name)) result[p.Name] = p;
            }
            return result;
        }

        private static bool TryCoerce(string type, object raw, out object value)
        {
            value = null;
            switch (type)
            {
                case "Bool":
                    if (!MCPVRChatUtil.TryReadBool(raw, out bool b)) return false;
                    value = b;
                    return true;
                case "Int":
                    if (!MCPVRChatUtil.TryReadInt(raw, out int i)) return false;
                    value = i;
                    return true;
                default:
                    if (!MCPVRChatUtil.TryReadFloat(raw, out float f)) return false;
                    value = f;
                    return true;
            }
        }

        private static bool TryParseGesture(object raw, out int gesture)
        {
            if (MCPVRChatUtil.TryReadInt(raw, out gesture)) return gesture >= 0 && gesture < GestureNames.Length;
            string text = MCPVRChatUtil.NormalizeName(raw as string);
            for (int i = 0; i < GestureNames.Length; i++)
            {
                if (MCPVRChatUtil.NormalizeName(GestureNames[i]) == text) { gesture = i; return true; }
            }
            switch (text)
            {
                case "idle": case "none": gesture = 0; return true;
                case "open": gesture = 2; return true;
                case "point": case "pointing": gesture = 3; return true;
                case "peace": case "vsign": gesture = 4; return true;
                case "rock": case "rocknroll": gesture = 5; return true;
                case "gun": gesture = 6; return true;
                case "thumbs": case "thumb": gesture = 7; return true;
            }
            gesture = 0;
            return false;
        }

        private static string GestureLabel(int index)
        {
            return index >= 0 && index < GestureNames.Length ? $"{index} ({GestureNames[index]})" : index.ToString();
        }

        private static List<string> ReadNames(Dictionary<string, object> args)
        {
            if (!MCPVRChatUtil.Has(args, "names")) return null;
            if (args["names"] is IList list) return list.OfType<object>().Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (args["names"] is string s) return s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return null;
        }

        /// <summary>
        /// The box to frame. Skinned meshes are measured from their posed vertices: their culling bounds are
        /// no framing box, since VRCFury's Bounding Box Fix inflates (and off-centres) them on every avatar.
        /// </summary>
        private static bool TryGetRenderBounds(GameObject avatar, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(false))
                {
                    if (renderer == null || !renderer.enabled) continue;
                    Bounds b;
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        if (skin.sharedMesh == null) continue;
                        skin.BakeMesh(baked, true);
                        baked.RecalculateBounds();
                        // Baked vertices are scaled but relative to the renderer's position and rotation.
                        var t = skin.transform;
                        b = TransformBounds(Matrix4x4.TRS(t.position, t.rotation, Vector3.one), baked.bounds);
                    }
                    else if (renderer is MeshRenderer) b = renderer.bounds;
                    else continue;
                    if (b.size.sqrMagnitude <= 0f) continue;
                    if (!any) { bounds = b; any = true; }
                    else bounds.Encapsulate(b);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
            return any;
        }

        private static Bounds TransformBounds(Matrix4x4 m, Bounds local)
        {
            var result = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                result.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign)));
            }
            return result;
        }

        private static Vector3 GetHeadPosition(GameObject avatar, Bounds bounds)
        {
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) return head.position + Vector3.up * (bounds.size.y * 0.03f);
            }
            return new Vector3(bounds.center.x, bounds.max.y - bounds.size.y * 0.08f, bounds.center.z);
        }
    }
}
