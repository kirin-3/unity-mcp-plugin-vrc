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
    /// VRCFury feature configuration (requires com.vrcfury.vrcfury):
    /// - vrc/avatar/vrcfury/toggle: create or update a Toggle (objects on/off, blendshapes, material swaps,
    ///   menu path, saved, default on, slider, exclusive tags, global parameter)
    /// - vrc/avatar/vrcfury/armature-link: create or update an Armature Link and report which bones link
    ///
    /// A VRCFury component keeps its single feature in a [SerializeReference] field, which
    /// component/set-property cannot construct. These routes build the feature object graph the way
    /// VRCFury's own public API (com.vrcfury.api FuryComponents) does and assign it. VRCFury's model types
    /// are internal, so everything goes through reflection. Freshly constructed model objects serialize at
    /// VRCFury's latest model version, so its upgrade pass leaves them untouched.
    /// </summary>
    public static class MCPVRChatVRCFuryCommands
    {
        internal const string VRCFuryComponentType = "VF.Model.VRCFury";
        internal const string ToggleType = "VF.Model.Feature.Toggle";
        internal const string ArmatureLinkType = "VF.Model.Feature.ArmatureLink";
        private const string ObjectToggleActionType = "VF.Model.StateAction.ObjectToggleAction";
        private const string BlendShapeActionType = "VF.Model.StateAction.BlendShapeAction";
        private const string MaterialActionType = "VF.Model.StateAction.MaterialAction";

        private const string UnsupportedVersion =
            "This VRCFury version's model does not match what this route knows how to drive";

        // ─────────────────────────────────────────────────────────────
        // Toggle (vrc/avatar/vrcfury/toggle)
        // ─────────────────────────────────────────────────────────────

        public static object ConfigureToggle(Dictionary<string, object> args)
        {
            if (!MCPVRChatAuthoringCommands.CheckVRCFuryInstalled(out _))
                return MCPVRChatUtil.Fail("VRCFury is not installed in the project (package 'com.vrcfury.vrcfury' missing).");

            Type vfType = MCPVRChatUtil.FindType(VRCFuryComponentType);
            Type toggleType = MCPVRChatUtil.FindType(ToggleType);
            Type objectActionType = MCPVRChatUtil.FindType(ObjectToggleActionType);
            Type blendActionType = MCPVRChatUtil.FindType(BlendShapeActionType);
            Type materialActionType = MCPVRChatUtil.FindType(MaterialActionType);
            if (vfType == null || toggleType == null || objectActionType == null || blendActionType == null || materialActionType == null)
                return MCPVRChatUtil.Fail(UnsupportedVersion + " (VF.Model.Feature.Toggle or its state actions were not found).");

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");
            Transform root = avatar.transform;

            string menuPath = MCPVRChatUtil.GetString(args, "menuPath")?.Trim().Trim('/');
            if (string.IsNullOrEmpty(menuPath))
                return MCPVRChatUtil.Fail("'menuPath' is required (e.g. 'Clothing/Jacket'); VRCFury uses it for the menu item and its parameter.");

            // Everything is parsed and validated before the scene is touched.
            if (!MCPVRChatUtil.TryReadOptionalBool(args, "saved", out bool? saved, out err)
                || !MCPVRChatUtil.TryReadOptionalBool(args, "defaultOn", out bool? defaultOn, out err)
                || !MCPVRChatUtil.TryReadOptionalBool(args, "slider", out bool? slider, out err)
                || !MCPVRChatUtil.TryReadOptionalBool(args, "exclusiveOffState", out bool? exclusiveOffState, out err))
                return MCPVRChatUtil.Fail(err);

            string exclusiveTags = null;
            if (MCPVRChatUtil.Has(args, "exclusiveTags"))
            {
                if (!TryReadStringList(args["exclusiveTags"], out var tags))
                    return MCPVRChatUtil.Fail("'exclusiveTags' must be a string or an array of strings.");
                exclusiveTags = string.Join(",", tags);
            }
            string globalParam = MCPVRChatUtil.Has(args, "globalParam") ? MCPVRChatUtil.GetString(args, "globalParam").Trim() : null;

            var warnings = new List<string>();
            var newActions = new List<object>();
            if (!TryBuildObjectActions(args, root, objectActionType, newActions, out err)
                || !TryBuildBlendShapeActions(args, root, blendActionType, newActions, warnings, out err)
                || !TryBuildMaterialActions(args, root, materialActionType, newActions, out err))
                return MCPVRChatUtil.Fail(err);
            bool actionsGiven = MCPVRChatUtil.Has(args, "objects") || MCPVRChatUtil.Has(args, "blendShapes") || MCPVRChatUtil.Has(args, "materials");

            var fieldAssignments = CollectToggleAssignments(saved, defaultOn, slider, exclusiveOffState, exclusiveTags, globalParam);
            foreach (var (field, _) in fieldAssignments)
            {
                if (MCPVRChatUtil.GetField(toggleType, field) == null)
                    return MCPVRChatUtil.Fail($"{UnsupportedVersion} (Toggle has no '{field}' field).");
            }

            Transform host = null;
            string targetPath = MCPVRChatUtil.GetString(args, "targetPath");
            if (!string.IsNullOrEmpty(targetPath))
            {
                host = MCPVRChatUtil.FindUnder(root, targetPath);
                if (host == null)
                    return MCPVRChatUtil.Fail($"targetPath '{targetPath}' was not found under '{avatar.name}'. Paths are relative to the avatar root, e.g. 'Clothing/Jacket'.");
            }

            // A toggle with the same menu path anywhere on the avatar is the one being configured.
            var sameMenu = FindFeatures(root, vfType, toggleType)
                .Where(f => string.Equals(MCPVRChatUtil.GetFieldValue(f.feature, "name") as string, menuPath, StringComparison.Ordinal))
                .ToList();
            if (sameMenu.Count > 1)
            {
                var where = sameMenu.Select(f => MCPVRChatAuthoringCommands.GetRelativePath(root, f.comp.transform));
                return MCPVRChatUtil.Fail($"{sameMenu.Count} VRCFury toggles already use menu path '{menuPath}' ({string.Join(", ", where)}); remove the duplicate first.");
            }

            if (actionsGiven)
            {
                err = FindOnOffConflict(root, vfType, toggleType, objectActionType, newActions,
                    sameMenu.Count == 1 ? sameMenu[0].feature : null);
                if (err != null) return MCPVRChatUtil.Fail(err);
            }

            if (sameMenu.Count == 1)
            {
                var (comp, feature) = sameMenu[0];
                if (host != null && host != comp.transform)
                {
                    return MCPVRChatUtil.Fail(
                        $"A VRCFury toggle for '{menuPath}' already exists on '{MCPVRChatAuthoringCommands.GetRelativePath(root, comp.transform)}'. " +
                        "Omit targetPath to update it there, or use a different menuPath.");
                }

                var actionsList = GetActionsList(feature);
                if (actionsGiven && actionsList == null)
                    return MCPVRChatUtil.Fail($"{UnsupportedVersion} (Toggle.state.actions was not found).");

                Undo.RecordObject(comp, "Configure VRCFury Toggle");
                foreach (var (field, value) in fieldAssignments) MCPVRChatUtil.SetFieldValue(feature, field, value);
                int kept = 0;
                if (actionsGiven)
                    kept = ReplaceManagedActions(actionsList, newActions, objectActionType, blendActionType, materialActionType);
                EditorUtility.SetDirty(comp);
                PrefabUtility.RecordPrefabInstancePropertyModifications(comp);

                return ToggleResult("updated", comp, root, menuPath, kept, warnings);
            }

            // Create: a new Toggle, assigned as the content of a new VRCFury component.
            object toggle = Activator.CreateInstance(toggleType, true);
            if (!MCPVRChatUtil.SetFieldValue(toggle, "name", menuPath))
                return MCPVRChatUtil.Fail($"{UnsupportedVersion} (Toggle has no 'name' field).");
            foreach (var (field, value) in fieldAssignments) MCPVRChatUtil.SetFieldValue(toggle, field, value);

            var newList = GetActionsList(toggle);
            if (newList == null)
                return MCPVRChatUtil.Fail($"{UnsupportedVersion} (Toggle.state.actions was not found).");
            foreach (var action in newActions) newList.Add(action);

            var hostGo = (host != null ? host : root).gameObject;
            var created = Undo.AddComponent(hostGo, vfType);
            if (!TryAssignContent(created, toggle, out err))
            {
                Undo.DestroyObjectImmediate(created);
                return MCPVRChatUtil.Fail(err);
            }

            return ToggleResult("created", created, root, menuPath, 0, warnings);
        }

        private static List<(string field, object value)> CollectToggleAssignments(bool? saved, bool? defaultOn, bool? slider,
            bool? exclusiveOffState, string exclusiveTags, string globalParam)
        {
            var assignments = new List<(string, object)>();
            if (saved.HasValue) assignments.Add(("saved", saved.Value));
            if (defaultOn.HasValue) assignments.Add(("defaultOn", defaultOn.Value));
            if (slider.HasValue) assignments.Add(("slider", slider.Value));
            if (exclusiveOffState.HasValue) assignments.Add(("exclusiveOffState", exclusiveOffState.Value));
            if (exclusiveTags != null)
            {
                assignments.Add(("enableExclusiveTag", exclusiveTags.Length > 0));
                assignments.Add(("exclusiveTag", exclusiveTags));
            }
            if (globalParam != null)
            {
                assignments.Add(("useGlobalParam", globalParam.Length > 0));
                assignments.Add(("globalParam", globalParam));
            }
            return assignments;
        }

        private static Dictionary<string, object> ToggleResult(string action, Component comp, Transform root, string menuPath,
            int keptActions, List<string> warnings)
        {
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "action", action },
                { "objectPath", MCPVRChatAuthoringCommands.GetRelativePath(root, comp.transform) },
                { "menuPath", menuPath },
                { "toggle", MCPVRChatUtil.DumpManaged(MCPVRChatUtil.GetFieldValue(comp, "content"), root) }
            };
            if (keptActions > 0) result["keptOtherActions"] = keptActions;
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        private static bool TryBuildObjectActions(Dictionary<string, object> args, Transform root, Type actionType,
            List<object> output, out string error)
        {
            error = null;
            if (!MCPVRChatUtil.Has(args, "objects")) return true;
            if (!(args["objects"] is IList items))
            {
                error = "'objects' must be an array of paths or {path, mode} entries.";
                return false;
            }

            FieldInfo modeField = MCPVRChatUtil.GetField(actionType, "mode");
            if (MCPVRChatUtil.GetField(actionType, "obj") == null || modeField == null || !modeField.FieldType.IsEnum)
            {
                error = $"{UnsupportedVersion} (ObjectToggleAction.obj/mode not found).";
                return false;
            }

            foreach (var item in items)
            {
                string path;
                string mode = "on";
                if (item is string s) path = s;
                else if (item is Dictionary<string, object> entry)
                {
                    path = MCPVRChatUtil.GetString(entry, "path");
                    mode = MCPVRChatUtil.GetString(entry, "mode") ?? "on";
                }
                else
                {
                    error = "Each 'objects' entry must be a path string or {path, mode}.";
                    return false;
                }

                var target = MCPVRChatUtil.FindUnder(root, path);
                if (target == null)
                {
                    error = $"objects: '{path}' was not found under '{root.name}'. Paths are relative to the avatar root.";
                    return false;
                }

                string enumName;
                switch (mode.Trim().ToLowerInvariant())
                {
                    case "on": case "turnon": case "true": enumName = "TurnOn"; break;
                    case "off": case "turnoff": case "false": enumName = "TurnOff"; break;
                    case "toggle": case "flip":
                        error = $"objects: mode '{mode}' for '{path}' is VRCFury's deprecated Flip State; use 'on' or 'off'.";
                        return false;
                    default:
                        error = $"objects: mode '{mode}' for '{path}' must be 'on' or 'off'.";
                        return false;
                }
                if (!Enum.IsDefined(modeField.FieldType, enumName))
                {
                    error = $"{UnsupportedVersion} (ObjectToggleAction.Mode has no {enumName}).";
                    return false;
                }

                var action = Activator.CreateInstance(actionType, true);
                MCPVRChatUtil.SetFieldValue(action, "obj", target.gameObject);
                modeField.SetValue(action, Enum.Parse(modeField.FieldType, enumName));
                output.Add(action);
            }
            return true;
        }

        private static bool TryBuildBlendShapeActions(Dictionary<string, object> args, Transform root, Type actionType,
            List<object> output, List<string> warnings, out string error)
        {
            error = null;
            if (!MCPVRChatUtil.Has(args, "blendShapes")) return true;
            if (!(args["blendShapes"] is IList items))
            {
                error = "'blendShapes' must be an array of {name, value, rendererPath} entries.";
                return false;
            }

            foreach (var field in new[] { "blendShape", "blendShapeValue", "renderer", "allRenderers" })
            {
                if (MCPVRChatUtil.GetField(actionType, field) == null)
                {
                    error = $"{UnsupportedVersion} (BlendShapeAction has no '{field}' field).";
                    return false;
                }
            }

            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != null && s.sharedMesh != null).ToList();

            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> entry))
                {
                    error = "Each 'blendShapes' entry must be an object: {name, value, rendererPath}.";
                    return false;
                }

                string name = MCPVRChatUtil.GetString(entry, "name");
                if (string.IsNullOrEmpty(name))
                {
                    error = "blendShapes: each entry needs a 'name'.";
                    return false;
                }

                float value = 100f;
                if (MCPVRChatUtil.Has(entry, "value") && !MCPVRChatUtil.TryReadFloat(entry["value"], out value))
                {
                    error = $"blendShapes: value for '{name}' must be a number.";
                    return false;
                }
                if (value < 0f || value > 100f)
                    warnings.Add($"Blendshape '{name}' is set to {value}; VRChat blendshape weights normally run 0–100.");

                SkinnedMeshRenderer renderer = null;
                string rendererPath = MCPVRChatUtil.GetString(entry, "rendererPath");
                if (!string.IsNullOrEmpty(rendererPath))
                {
                    renderer = MCPVRChatUtil.FindUnder(root, rendererPath)?.GetComponent<SkinnedMeshRenderer>();
                    if (renderer == null || renderer.sharedMesh == null)
                    {
                        error = $"blendShapes: no SkinnedMeshRenderer with a mesh at '{rendererPath}'.";
                        return false;
                    }
                    if (renderer.sharedMesh.GetBlendShapeIndex(name) < 0)
                    {
                        error = $"blendShapes: '{rendererPath}' has no blendshape '{name}'." + DidYouMean(name, BlendShapeNames(renderer));
                        return false;
                    }
                }
                else if (!skins.Any(s => s.sharedMesh.GetBlendShapeIndex(name) >= 0))
                {
                    error = $"blendShapes: no mesh on the avatar has a blendshape named '{name}'." + DidYouMean(name, skins.SelectMany(s => BlendShapeNames(s)));
                    return false;
                }

                var action = Activator.CreateInstance(actionType, true);
                MCPVRChatUtil.SetFieldValue(action, "blendShape", name);
                MCPVRChatUtil.SetFieldValue(action, "blendShapeValue", value);
                MCPVRChatUtil.SetFieldValue(action, "renderer", renderer);
                MCPVRChatUtil.SetFieldValue(action, "allRenderers", renderer == null);
                output.Add(action);
            }
            return true;
        }

        private static bool TryBuildMaterialActions(Dictionary<string, object> args, Transform root, Type actionType,
            List<object> output, out string error)
        {
            error = null;
            if (!MCPVRChatUtil.Has(args, "materials")) return true;
            if (!(args["materials"] is IList items))
            {
                error = "'materials' must be an array of {rendererPath, slot, material} entries.";
                return false;
            }

            FieldInfo matField = MCPVRChatUtil.GetField(actionType, "mat");
            if (matField == null || MCPVRChatUtil.GetField(actionType, "renderer") == null || MCPVRChatUtil.GetField(actionType, "materialIndex") == null)
            {
                error = $"{UnsupportedVersion} (MaterialAction.renderer/materialIndex/mat not found).";
                return false;
            }

            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> entry))
                {
                    error = "Each 'materials' entry must be an object: {rendererPath, slot, material}.";
                    return false;
                }

                string rendererPath = MCPVRChatUtil.GetString(entry, "rendererPath");
                var renderer = string.IsNullOrEmpty(rendererPath) ? null : MCPVRChatUtil.FindUnder(root, rendererPath)?.GetComponent<Renderer>();
                if (renderer == null)
                {
                    error = $"materials: no Renderer at rendererPath '{rendererPath}'.";
                    return false;
                }

                int slot = 0;
                if (MCPVRChatUtil.Has(entry, "slot") && !MCPVRChatUtil.TryReadInt(entry["slot"], out slot))
                {
                    error = $"materials: slot for '{rendererPath}' must be an integer.";
                    return false;
                }
                int slotCount = renderer.sharedMaterials.Length;
                if (slot < 0 || slot >= slotCount)
                {
                    error = $"materials: '{rendererPath}' has {slotCount} material slot(s); slot {slot} is out of range.";
                    return false;
                }

                string materialPath = MCPVRChatUtil.GetString(entry, "material");
                var material = string.IsNullOrEmpty(materialPath) ? null : AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    error = $"materials: no Material asset at '{materialPath}' (give the asset path, e.g. 'Assets/Materials/Red.mat').";
                    return false;
                }

                if (!TryWrapMaterial(matField.FieldType, material, out object wrapped))
                {
                    error = $"{UnsupportedVersion} (cannot build a {matField.FieldType.Name} from a Material).";
                    return false;
                }

                var action = Activator.CreateInstance(actionType, true);
                MCPVRChatUtil.SetFieldValue(action, "renderer", renderer);
                MCPVRChatUtil.SetFieldValue(action, "materialIndex", slot);
                matField.SetValue(action, wrapped);
                output.Add(action);
            }
            return true;
        }

        /// <summary>
        /// MaterialAction.mat is a GuidMaterial wrapper in current VRCFury. Its implicit conversion from
        /// Material is what VRCFury itself uses: it stores the object and syncs the GUID id.
        /// </summary>
        private static bool TryWrapMaterial(Type fieldType, Material material, out object wrapped)
        {
            wrapped = null;
            if (fieldType.IsAssignableFrom(typeof(Material)))
            {
                wrapped = material;
                return true;
            }

            var implicitOp = fieldType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == fieldType
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Material)));
            if (implicitOp != null)
            {
                wrapped = implicitOp.Invoke(null, new object[] { material });
                return wrapped != null;
            }

            // Fallback: fill objRef directly; VRCFury re-syncs the id on its next upgrade pass.
            if (MCPVRChatUtil.GetField(fieldType, "objRef") == null) return false;
            wrapped = Activator.CreateInstance(fieldType, true);
            MCPVRChatUtil.SetFieldValue(wrapped, "objRef", material);
            return true;
        }

        /// <summary>
        /// Replaces the object/blendshape/material actions of a toggle and keeps any other action kinds
        /// (animation clips, FX floats…) the user set up by hand. Returns how many actions were kept.
        /// </summary>
        private static int ReplaceManagedActions(IList actions, List<object> newActions, params Type[] managedTypes)
        {
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var existing = actions[i];
                if (existing == null || managedTypes.Any(t => t.IsInstanceOfType(existing)))
                    actions.RemoveAt(i);
            }
            int kept = actions.Count;
            foreach (var action in newActions) actions.Add(action);
            return kept;
        }

        private static IList GetActionsList(object toggle)
        {
            var state = MCPVRChatUtil.GetFieldValue(toggle, "state");
            if (state == null)
            {
                var stateField = MCPVRChatUtil.GetField(toggle.GetType(), "state");
                if (stateField == null) return null;
                state = Activator.CreateInstance(stateField.FieldType, true);
                stateField.SetValue(toggle, state);
            }
            return MCPVRChatUtil.GetFieldValue(state, "actions") as IList;
        }

        /// <summary>
        /// One toggle turning an object on while another turns it off gives the object two resting states,
        /// and VRCFury aborts the whole avatar build over it (exclusive tags are the supported way). Checked
        /// before anything changes; <paramref name="thisToggle"/> is the toggle being replaced, if any.
        /// </summary>
        private static string FindOnOffConflict(Transform root, Type vfType, Type toggleType, Type objectActionType,
            List<object> actions, object thisToggle)
        {
            var turnsOn = ObjectActions(actions, objectActionType, "TurnOn");
            var turnsOff = ObjectActions(actions, objectActionType, "TurnOff");
            if (turnsOn.Count == 0 && turnsOff.Count == 0) return null;

            foreach (var (_, other) in FindFeatures(root, vfType, toggleType))
            {
                if (ReferenceEquals(other, thisToggle)) continue;
                var otherActions = GetActionsListReadOnly(other);
                foreach (var (mine, theirs, here, there) in new[] { (turnsOff, "TurnOn", "off", "on"), (turnsOn, "TurnOff", "on", "off") })
                {
                    var clash = mine.Intersect(ObjectActions(otherActions, objectActionType, theirs)).FirstOrDefault();
                    if (clash == null) continue;
                    return $"'{MCPVRChatUtil.RelPath(root, clash.transform)}' would be turned {here} here but the toggle " +
                           $"'{MCPVRChatUtil.GetFieldValue(other, "name")}' turns it {there}; VRCFury refuses to build two toggles " +
                           "that disagree on an object's resting state. Turn it on in both and give them a shared exclusiveTags entry instead. Nothing was changed.";
                }
            }
            return null;
        }

        private static List<GameObject> ObjectActions(IEnumerable actions, Type objectActionType, string mode)
        {
            var result = new List<GameObject>();
            if (actions == null) return result;
            foreach (var action in actions)
            {
                if (action == null || !objectActionType.IsInstanceOfType(action)) continue;
                if (MCPVRChatUtil.GetFieldValue(action, "mode")?.ToString() != mode) continue;
                if (MCPVRChatUtil.GetFieldValue(action, "obj") is GameObject go && go != null) result.Add(go);
            }
            return result;
        }

        private static IList GetActionsListReadOnly(object toggle)
        {
            return MCPVRChatUtil.GetFieldValue(MCPVRChatUtil.GetFieldValue(toggle, "state"), "actions") as IList;
        }

        // ─────────────────────────────────────────────────────────────
        // Armature Link (vrc/avatar/vrcfury/armature-link)
        // ─────────────────────────────────────────────────────────────

        public static object ConfigureArmatureLink(Dictionary<string, object> args)
        {
            if (!MCPVRChatAuthoringCommands.CheckVRCFuryInstalled(out _))
                return MCPVRChatUtil.Fail("VRCFury is not installed in the project (package 'com.vrcfury.vrcfury' missing).");

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");
            Transform root = avatar.transform;

            string targetPath = MCPVRChatUtil.GetString(args, "targetPath");
            if (string.IsNullOrEmpty(targetPath))
                return MCPVRChatUtil.Fail("'targetPath' is required: the prop or clothing object under the avatar that gets the Armature Link.");
            var host = MCPVRChatUtil.FindUnder(root, targetPath);
            if (host == null)
                return MCPVRChatUtil.Fail($"targetPath '{targetPath}' was not found under '{avatar.name}'. Paths are relative to the avatar root.");

            if (!MCPVRChatUtil.TryReadOptionalBool(args, "recursive", out bool? recursive, out err)
                || !MCPVRChatUtil.TryReadOptionalBool(args, "align", out bool? align, out err))
                return MCPVRChatUtil.Fail(err);

            string removeBoneSuffix = MCPVRChatUtil.Has(args, "removeBoneSuffix") ? MCPVRChatUtil.GetString(args, "removeBoneSuffix") : null;

            return ApplyArmatureLink(root, host, MCPVRChatUtil.GetString(args, "propBonePath"),
                MCPVRChatUtil.GetString(args, "linkTo"), recursive, align, removeBoneSuffix);
        }

        /// <summary>
        /// Create (or update, when the host already links the same bone) an Armature Link. Defaults mirror
        /// VRCFury's own Add Component path: Link From is guessed from the host's hierarchy, Link To is the
        /// avatar's Hips, and recursive/align follow whether a skinned mesh outside the linked bone uses it.
        /// Shared with vrc/avatar/outfit/attach.
        /// </summary>
        internal static Dictionary<string, object> ApplyArmatureLink(Transform root, Transform host, string propBonePath,
            string linkTo, bool? recursive, bool? align, string removeBoneSuffix)
        {
            Type vfType = MCPVRChatUtil.FindType(VRCFuryComponentType);
            Type linkType = MCPVRChatUtil.FindType(ArmatureLinkType);
            Type linkToType = MCPVRChatUtil.FindType(ArmatureLinkType + "+LinkTo");
            if (vfType == null || linkType == null || linkToType == null)
                return MCPVRChatUtil.Fail(UnsupportedVersion + " (VF.Model.Feature.ArmatureLink was not found).");
            foreach (var field in new[] { "propBone", "linkTo", "recursive", "alignPosition", "alignRotation", "alignScale", "removeBoneSuffix" })
            {
                if (MCPVRChatUtil.GetField(linkType, field) == null)
                    return MCPVRChatUtil.Fail($"{UnsupportedVersion} (ArmatureLink has no '{field}' field).");
            }

            if (host == root)
                return MCPVRChatUtil.Fail("Armature Link belongs on the prop or clothing object, not on the avatar root.");

            // Link From
            Transform propBone;
            if (!string.IsNullOrEmpty(propBonePath))
            {
                propBone = MCPVRChatUtil.FindUnder(host, propBonePath);
                if (propBone == null)
                    return MCPVRChatUtil.Fail($"propBonePath '{propBonePath}' was not found under '{host.name}'. It is relative to the targetPath object, e.g. 'Armature/Hips'.");
            }
            else
            {
                propBone = MCPVRChatOutfitCommands.GuessLinkFrom(host, root);
            }

            var animator = root.GetComponent<Animator>();
            if (MCPVRChatOutfitCommands.ContainsAvatarBone(propBone, animator))
            {
                return MCPVRChatUtil.Fail(
                    $"'{MCPVRChatUtil.RelPath(root, propBone)}' contains the avatar's own armature. Armature Link must start at the prop's or " +
                    "clothing's bone (for clothes, the clothing's Hips), not at an avatar bone.");
            }

            // Link To: a humanoid bone name or a path under the avatar
            string linkToName = string.IsNullOrWhiteSpace(linkTo) ? "Hips" : linkTo.Trim();
            object linkToEntry = Activator.CreateInstance(linkToType, true);
            Transform avatarBone;
            if (Enum.TryParse(linkToName, true, out HumanBodyBones bone) && bone != HumanBodyBones.LastBone
                && Enum.IsDefined(typeof(HumanBodyBones), bone) && !linkToName.Any(char.IsDigit))
            {
                avatarBone = animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
                if (avatarBone == null)
                    return MCPVRChatUtil.Fail($"The avatar has no humanoid '{bone}' bone (is its rig set to Humanoid?).");
                MCPVRChatUtil.SetFieldValue(linkToEntry, "useBone", true);
                MCPVRChatUtil.SetFieldValue(linkToEntry, "bone", bone);
                MCPVRChatUtil.SetFieldValue(linkToEntry, "useObj", false);
                MCPVRChatUtil.SetFieldValue(linkToEntry, "offset", "");
                linkToName = bone.ToString();
            }
            else
            {
                avatarBone = MCPVRChatUtil.FindUnder(root, linkToName);
                if (avatarBone == null)
                    return MCPVRChatUtil.Fail($"linkTo '{linkToName}' is neither a humanoid bone name (e.g. 'Hips', 'Head') nor a path under the avatar.");
                // Avatar root + offset path, as VRCFury's public LinkTo(string path) records it.
                MCPVRChatUtil.SetFieldValue(linkToEntry, "useBone", false);
                MCPVRChatUtil.SetFieldValue(linkToEntry, "useObj", false);
                MCPVRChatUtil.SetFieldValue(linkToEntry, "offset", MCPVRChatUtil.RelPath(root, avatarBone));
            }

            // An existing link on this object for the same Link From is updated, not duplicated.
            var existing = FindFeatures(host, vfType, linkType)
                .FirstOrDefault(f => f.comp.transform == host && MCPVRChatUtil.GetFieldValue(f.feature, "propBone") as GameObject == propBone.gameObject);

            Component comp = existing.comp;
            object link = existing.feature;
            bool creating = comp == null;
            if (creating)
            {
                link = Activator.CreateInstance(linkType, true);
            }
            else
            {
                Undo.RecordObject(comp, "Configure VRCFury Armature Link");
            }

            bool effectiveRecursive = recursive
                ?? (creating ? MCPVRChatOutfitCommands.HasExternalSkinBoneReference(propBone, root)
                             : MCPVRChatUtil.GetFieldValue(link, "recursive") is bool r && r);
            bool effectiveAlign = align
                ?? (creating ? effectiveRecursive
                             : MCPVRChatUtil.GetFieldValue(link, "alignPosition") is bool a && a);

            MCPVRChatUtil.SetFieldValue(link, "propBone", propBone.gameObject);
            var linkToList = MCPVRChatUtil.GetFieldValue(link, "linkTo") as IList;
            if (linkToList == null)
            {
                linkToList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(linkToType));
                MCPVRChatUtil.SetFieldValue(link, "linkTo", linkToList);
            }
            linkToList.Clear();
            linkToList.Add(linkToEntry);
            MCPVRChatUtil.SetFieldValue(link, "recursive", effectiveRecursive);
            MCPVRChatUtil.SetFieldValue(link, "alignPosition", effectiveAlign);
            MCPVRChatUtil.SetFieldValue(link, "alignRotation", effectiveAlign);
            MCPVRChatUtil.SetFieldValue(link, "alignScale", effectiveAlign);
            if (removeBoneSuffix != null) MCPVRChatUtil.SetFieldValue(link, "removeBoneSuffix", removeBoneSuffix);
            if (creating)
            {
                // VRCFury's own defaults for a new link (ArmatureLinkBuilder.UpdateOnLinkFromChange).
                MCPVRChatUtil.SetFieldValue(link, "autoScaleFactor", true);
                MCPVRChatUtil.SetFieldValue(link, "scalingFactorPowersOf10Only", true);
                MCPVRChatUtil.SetFieldValue(link, "skinRewriteScalingFactor", 1f);

                comp = Undo.AddComponent(host.gameObject, vfType);
                if (!TryAssignContent(comp, link, out string assignError))
                {
                    Undo.DestroyObjectImmediate(comp);
                    return MCPVRChatUtil.Fail(assignError);
                }
            }
            else
            {
                EditorUtility.SetDirty(comp);
                PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
            }

            string suffixSetting = MCPVRChatUtil.GetFieldValue(MCPVRChatUtil.GetFieldValue(comp, "content"), "removeBoneSuffix") as string ?? "";
            return new Dictionary<string, object>
            {
                { "success", true },
                { "action", creating ? "created" : "updated" },
                { "objectPath", MCPVRChatAuthoringCommands.GetRelativePath(root, host) },
                { "propBone", MCPVRChatUtil.RelPath(root, propBone) },
                { "linkTo", linkToName },
                { "avatarBone", MCPVRChatUtil.RelPath(root, avatarBone) },
                { "recursive", effectiveRecursive },
                { "align", effectiveAlign },
                { "removeBoneSuffix", suffixSetting },
                { "boneMatch", MCPVRChatOutfitCommands.BuildVRCFuryLinkReport(root, propBone, avatarBone, suffixSetting, effectiveRecursive) }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Shared
        // ─────────────────────────────────────────────────────────────

        /// <summary>VRCFury components under <paramref name="root"/> whose feature is of <paramref name="featureType"/>.</summary>
        internal static List<(Component comp, object feature)> FindFeatures(Transform root, Type vfType, Type featureType)
        {
            var result = new List<(Component, object)>();
            if (root == null || vfType == null || featureType == null) return result;
            foreach (var comp in root.GetComponentsInChildren(vfType, true))
            {
                if (comp == null) continue;
                var content = MCPVRChatUtil.GetFieldValue(comp, "content");
                if (content != null && featureType.IsInstanceOfType(content))
                    result.Add((comp, content));
            }
            return result;
        }

        /// <summary>
        /// Assign a feature as the component's [SerializeReference] content, the way VRCFury's Add Component
        /// hook does. Called right after Undo.AddComponent, so undoing the add removes the whole feature.
        /// </summary>
        internal static bool TryAssignContent(Component comp, object feature, out string error)
        {
            error = null;
            var so = new SerializedObject(comp);
            var prop = so.FindProperty("content");
            if (prop == null || prop.propertyType != SerializedPropertyType.ManagedReference)
            {
                error = UnsupportedVersion + " (the VRCFury component has no [SerializeReference] 'content' field).";
                return false;
            }
            prop.managedReferenceValue = feature;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static bool TryReadStringList(object value, out List<string> result)
        {
            result = new List<string>();
            if (value is string s)
            {
                result.AddRange(s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                return true;
            }
            if (value is IList list)
            {
                foreach (var item in list)
                {
                    if (!(item is string str)) return false;
                    if (str.Trim().Length > 0) result.Add(str.Trim());
                }
                return true;
            }
            return false;
        }

        private static IEnumerable<string> BlendShapeNames(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null) yield break;
            for (int i = 0; i < mesh.blendShapeCount; i++) yield return mesh.GetBlendShapeName(i);
        }

        private static string DidYouMean(string name, IEnumerable<string> candidates)
        {
            var suggestions = MCPVRChatUtil.Suggest(name, candidates);
            return suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "";
        }
    }
}
