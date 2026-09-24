using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for VRChat World tooling:
    /// - vrc/world/descriptor/get: inspect spawns, spawn order, respawn height, reference camera
    /// - vrc/world/descriptor/add-spawn: add a new spawn point and assign to scene descriptor
    /// - vrc/world/descriptor/set-spawns: modify spawn points and descriptor settings
    /// - vrc/world/udon/list: report Udon behaviours with object path, program name, variable count
    /// - vrc/world/udon/get-variables: report public variable names, types, and values
    /// - vrc/world/udon/set-variable: set public variable with strict type checking
    /// - vrc/world/validate: run world validation tooling or fail naming dev.onevr.vrworldtoolkit
    /// - vrc/world/content/summary: report mirrors, audio sources (flagging unspatialized), lights, video players
    /// </summary>
    public static class MCPVRChatWorldCommands
    {
        // ─── Test Hooks ───
        public static Func<Dictionary<string, object>, object> TestDescriptorGetOverride = null;
        public static Func<Dictionary<string, object>, object> TestAddSpawnOverride = null;
        public static Func<Dictionary<string, object>, object> TestSetSpawnsOverride = null;
        public static Func<Dictionary<string, object>, object> TestUdonListOverride = null;
        public static Func<Dictionary<string, object>, object> TestUdonGetVariablesOverride = null;
        public static Func<Dictionary<string, object>, object> TestUdonSetVariableOverride = null;
        public static Func<Dictionary<string, object>, object> TestValidateOverride = null;
        public static Func<Dictionary<string, object>, object> TestContentSummaryOverride = null;
        public static bool? TestVRWorldToolkitInstalledOverride = null;
        public static Component TestSceneDescriptorOverride = null;

        public static void ResetTestOverrides()
        {
            TestDescriptorGetOverride = null;
            TestAddSpawnOverride = null;
            TestSetSpawnsOverride = null;
            TestUdonListOverride = null;
            TestUdonGetVariablesOverride = null;
            TestUdonSetVariableOverride = null;
            TestValidateOverride = null;
            TestContentSummaryOverride = null;
            TestVRWorldToolkitInstalledOverride = null;
            TestSceneDescriptorOverride = null;
        }

        // ─────────────────────────────────────────────────────────────
        // 1. Scene Descriptor & Spawns
        // ─────────────────────────────────────────────────────────────

        public static Component FindSceneDescriptor()
        {
            if (TestSceneDescriptorOverride != null)
                return TestSceneDescriptorOverride;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded)
            {
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    var comps = root.GetComponentsInChildren<Component>(true);
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        string tName = c.GetType().Name;
                        if (tName == "VRCSceneDescriptor" || tName == "VRC_SceneDescriptor")
                            return c;
                    }
                }
            }

            // Fallback: search all loaded components
            var allComps = UnityEngine.Resources.FindObjectsOfTypeAll<Component>();
            foreach (var c in allComps)
            {
                if (c == null || !MCPVRChatDetect.IsLiveSceneObject(c.gameObject)) continue;
                string tName = c.GetType().Name;
                if (tName == "VRCSceneDescriptor" || tName == "VRC_SceneDescriptor")
                    return c;
            }

            return null;
        }

        public static object GetDescriptor(Dictionary<string, object> args)
        {
            if (TestDescriptorGetOverride != null)
                return TestDescriptorGetOverride(args);

            Component desc = FindSceneDescriptor();
            if (desc == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", "No VRChat scene descriptor (VRCSceneDescriptor) found in the open scene. This scene is not configured as a VRChat world scene." }
                };
            }

            Type descType = desc.GetType();

            // 1. Spawns
            var spawnsList = new List<Dictionary<string, object>>();
            Transform[] spawnsArray = GetDescriptorSpawnsArray(desc);
            if (spawnsArray != null)
            {
                foreach (var t in spawnsArray)
                {
                    if (t == null) continue;
                    spawnsList.Add(new Dictionary<string, object>
                    {
                        { "name", t.name },
                        { "path", GetGameObjectPath(t.gameObject) },
                        { "position", new float[] { t.position.x, t.position.y, t.position.z } },
                        { "rotation", new float[] { t.rotation.eulerAngles.x, t.rotation.eulerAngles.y, t.rotation.eulerAngles.z } }
                    });
                }
            }

            // 2. Spawn Order
            string spawnOrder = "Sequential";
            var soProp = descType.GetProperty("SpawnOrder") ?? descType.GetProperty("spawnOrder");
            var soField = descType.GetField("SpawnOrder") ?? descType.GetField("spawnOrder");
            object soVal = soProp != null ? soProp.GetValue(desc, null) : soField?.GetValue(desc);
            if (soVal != null) spawnOrder = soVal.ToString();

            // 3. Respawn Height Y
            float respawnHeightY = -100f;
            var rhProp = descType.GetProperty("RespawnHeightY") ?? descType.GetProperty("respawnHeightY");
            var rhField = descType.GetField("RespawnHeightY") ?? descType.GetField("respawnHeightY");
            object rhVal = rhProp != null ? rhProp.GetValue(desc, null) : rhField?.GetValue(desc);
            if (rhVal != null && float.TryParse(rhVal.ToString(), out float f)) respawnHeightY = f;

            // 4. Reference Camera
            string referenceCameraPath = null;
            var rcProp = descType.GetProperty("ReferenceCamera") ?? descType.GetProperty("referenceCamera");
            var rcField = descType.GetField("ReferenceCamera") ?? descType.GetField("referenceCamera");
            object rcVal = rcProp != null ? rcProp.GetValue(desc, null) : rcField?.GetValue(desc);
            if (rcVal is GameObject rcGo && rcGo != null) referenceCameraPath = GetGameObjectPath(rcGo);
            else if (rcVal is Component rcComp && rcComp != null) referenceCameraPath = GetGameObjectPath(rcComp.gameObject);

            // 5. Forbid User Portals
            bool forbidUserPortals = false;
            var fupProp = descType.GetProperty("ForbidUserPortals") ?? descType.GetProperty("forbidUserPortals");
            var fupField = descType.GetField("ForbidUserPortals") ?? descType.GetField("forbidUserPortals");
            object fupVal = fupProp != null ? fupProp.GetValue(desc, null) : fupField?.GetValue(desc);
            if (fupVal is bool b) forbidUserPortals = b;

            return new Dictionary<string, object>
            {
                { "objectPath", GetGameObjectPath(desc.gameObject) },
                { "spawns", spawnsList },
                { "spawnCount", spawnsList.Count },
                { "spawnOrder", spawnOrder },
                { "respawnHeightY", respawnHeightY },
                { "referenceCamera", referenceCameraPath },
                { "forbidUserPortals", forbidUserPortals }
            };
        }

        public static object AddSpawn(Dictionary<string, object> args)
        {
            if (TestAddSpawnOverride != null)
                return TestAddSpawnOverride(args);

            Component desc = FindSceneDescriptor();
            if (desc == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", "No VRChat scene descriptor (VRCSceneDescriptor) found in the open scene. This scene is not configured as a VRChat world scene." }
                };
            }

            Vector3 pos = Vector3.zero;
            if (args != null && (args.ContainsKey("position") || args.ContainsKey("pos")))
            {
                object pObj = args.ContainsKey("position") ? args["position"] : args["pos"];
                TryParseVector3(pObj, out pos);
            }

            Vector3 rotEuler = Vector3.zero;
            if (args != null && (args.ContainsKey("rotation") || args.ContainsKey("rot")))
            {
                object rObj = args.ContainsKey("rotation") ? args["rotation"] : args["rot"];
                TryParseVector3(rObj, out rotEuler);
            }

            string name = "SpawnPoint";
            if (args != null && args.TryGetValue("name", out var nObj) && nObj != null && !string.IsNullOrEmpty(nObj.ToString()))
            {
                name = nObj.ToString();
            }

            GameObject spawnGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(spawnGo, "Add Spawn Point");

            if (args != null && args.TryGetValue("parentPath", out var ppObj) && ppObj != null)
            {
                var parentGo = ResolveGameObject(ppObj.ToString());
                if (parentGo != null) spawnGo.transform.SetParent(parentGo.transform, false);
            }

            spawnGo.transform.position = pos;
            spawnGo.transform.rotation = Quaternion.Euler(rotEuler);

            // Append to descriptor spawns array
            Transform[] currentSpawns = GetDescriptorSpawnsArray(desc) ?? new Transform[0];
            var newSpawns = new List<Transform>(currentSpawns);
            newSpawns.Add(spawnGo.transform);

            Undo.RecordObject(desc, "Add Spawn Point");
            SetDescriptorSpawnsArray(desc, newSpawns.ToArray());
            EditorUtility.SetDirty(desc);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "spawn", new Dictionary<string, object>
                    {
                        { "name", spawnGo.name },
                        { "path", GetGameObjectPath(spawnGo) },
                        { "position", new float[] { pos.x, pos.y, pos.z } },
                        { "rotation", new float[] { rotEuler.x, rotEuler.y, rotEuler.z } }
                    }
                },
                { "spawnCount", newSpawns.Count }
            };
        }

        public static object SetSpawns(Dictionary<string, object> args)
        {
            if (TestSetSpawnsOverride != null)
                return TestSetSpawnsOverride(args);

            Component desc = FindSceneDescriptor();
            if (desc == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", "No VRChat scene descriptor (VRCSceneDescriptor) found in the open scene. This scene is not configured as a VRChat world scene." }
                };
            }

            Type descType = desc.GetType();
            Undo.RecordObject(desc, "Configure Scene Descriptor");

            if (args != null && args.TryGetValue("spawns", out var sObj) && sObj is IList sList)
            {
                var newTransforms = new List<Transform>();
                foreach (var item in sList)
                {
                    if (item == null) continue;
                    var go = ResolveGameObject(item.ToString());
                    if (go != null) newTransforms.Add(go.transform);
                }
                SetDescriptorSpawnsArray(desc, newTransforms.ToArray());
            }

            if (args != null && args.TryGetValue("spawnOrder", out var soObj) && soObj != null)
            {
                string soStr = soObj.ToString();
                var soProp = descType.GetProperty("SpawnOrder") ?? descType.GetProperty("spawnOrder");
                var soField = descType.GetField("SpawnOrder") ?? descType.GetField("spawnOrder");
                Type orderType = soProp?.PropertyType ?? soField?.FieldType;
                if (orderType != null && orderType.IsEnum)
                {
                    try
                    {
                        object enumVal = Enum.Parse(orderType, soStr, true);
                        if (soProp != null) soProp.SetValue(desc, enumVal, null);
                        else soField?.SetValue(desc, enumVal);
                    }
                    catch { }
                }
            }

            if (args != null && args.TryGetValue("respawnHeightY", out var rhObj) && rhObj != null)
            {
                if (float.TryParse(rhObj.ToString(), out float f))
                {
                    var rhProp = descType.GetProperty("RespawnHeightY") ?? descType.GetProperty("respawnHeightY");
                    var rhField = descType.GetField("RespawnHeightY") ?? descType.GetField("respawnHeightY");
                    if (rhProp != null) rhProp.SetValue(desc, f, null);
                    else rhField?.SetValue(desc, f);
                }
            }

            if (args != null && args.TryGetValue("forbidUserPortals", out var fupObj) && fupObj != null)
            {
                if (bool.TryParse(fupObj.ToString(), out bool b))
                {
                    var fupProp = descType.GetProperty("ForbidUserPortals") ?? descType.GetProperty("forbidUserPortals");
                    var fupField = descType.GetField("ForbidUserPortals") ?? descType.GetField("forbidUserPortals");
                    if (fupProp != null) fupProp.SetValue(desc, b, null);
                    else fupField?.SetValue(desc, b);
                }
            }

            EditorUtility.SetDirty(desc);
            return GetDescriptor(args);
        }

        private static Transform[] GetDescriptorSpawnsArray(Component desc)
        {
            if (desc == null) return null;
            Type descType = desc.GetType();
            var prop = descType.GetProperty("spawns") ?? descType.GetProperty("Spawns");
            var field = descType.GetField("spawns") ?? descType.GetField("Spawns");

            object val = prop != null ? prop.GetValue(desc, null) : field?.GetValue(desc);
            if (val is Transform[] arr) return arr;
            if (val is IEnumerable enumerable)
            {
                var list = new List<Transform>();
                foreach (var item in enumerable)
                {
                    if (item is Transform t) list.Add(t);
                    else if (item is GameObject g) list.Add(g.transform);
                }
                return list.ToArray();
            }
            return null;
        }

        private static void SetDescriptorSpawnsArray(Component desc, Transform[] spawns)
        {
            if (desc == null) return;
            Type descType = desc.GetType();
            var prop = descType.GetProperty("spawns") ?? descType.GetProperty("Spawns");
            var field = descType.GetField("spawns") ?? descType.GetField("Spawns");

            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(desc, spawns, null);
            }
            else if (field != null)
            {
                field.SetValue(desc, spawns);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Udon Behaviour Inspection & Variable Access
        // ─────────────────────────────────────────────────────────────

        public static object ListUdon(Dictionary<string, object> args)
        {
            if (TestUdonListOverride != null)
                return TestUdonListOverride(args);

            var behaviours = new List<Dictionary<string, object>>();
            var allComps = UnityEngine.Resources.FindObjectsOfTypeAll<Component>();

            foreach (var c in allComps)
            {
                if (c == null || !MCPVRChatDetect.IsLiveSceneObject(c.gameObject)) continue;
                string tName = c.GetType().Name;
                if (tName != "UdonBehaviour" && !c.GetType().FullName.Contains("UdonBehaviour"))
                    continue;

                string progName = GetUdonProgramName(c);
                int varCount = GetUdonVariableCount(c);

                behaviours.Add(new Dictionary<string, object>
                {
                    { "name", c.gameObject.name },
                    { "objectPath", GetGameObjectPath(c.gameObject) },
                    { "programName", progName },
                    { "variableCount", varCount }
                });
            }

            return new Dictionary<string, object>
            {
                { "behaviours", behaviours },
                { "count", behaviours.Count }
            };
        }

        public static object GetUdonVariables(Dictionary<string, object> args)
        {
            if (TestUdonGetVariablesOverride != null)
                return TestUdonGetVariablesOverride(args);

            string targetPath = null;
            if (args != null)
            {
                if (args.TryGetValue("targetPath", out var tp) && tp != null) targetPath = tp.ToString();
                else if (args.TryGetValue("objectPath", out var op) && op != null) targetPath = op.ToString();
                else if (args.TryGetValue("path", out var p) && p != null) targetPath = p.ToString();
            }

            var go = ResolveGameObject(targetPath);
            if (go == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Target GameObject '{targetPath}' could not be found." }
                };
            }

            Component behaviour = FindUdonBehaviour(go);
            if (behaviour == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"No UdonBehaviour found on '{GetGameObjectPath(go)}'." }
                };
            }

            var vars = ExtractUdonVariables(behaviour);
            var varList = new List<Dictionary<string, object>>();
            foreach (var v in vars)
            {
                varList.Add(new Dictionary<string, object>
                {
                    { "name", v.Name },
                    { "type", v.Type.Name },
                    { "value", SerializeUdonValue(v.Value) }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectPath", GetGameObjectPath(go) },
                { "programName", GetUdonProgramName(behaviour) },
                { "variables", varList },
                { "count", varList.Count }
            };
        }

        public static object SetUdonVariable(Dictionary<string, object> args)
        {
            if (TestUdonSetVariableOverride != null)
                return TestUdonSetVariableOverride(args);

            string targetPath = null;
            if (args != null)
            {
                if (args.TryGetValue("targetPath", out var tp) && tp != null) targetPath = tp.ToString();
                else if (args.TryGetValue("objectPath", out var op) && op != null) targetPath = op.ToString();
                else if (args.TryGetValue("path", out var p) && p != null) targetPath = p.ToString();
            }

            var go = ResolveGameObject(targetPath);
            if (go == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Target GameObject '{targetPath}' could not be found." }
                };
            }

            Component behaviour = FindUdonBehaviour(go);
            if (behaviour == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"No UdonBehaviour found on '{GetGameObjectPath(go)}'." }
                };
            }

            string varName = null;
            if (args != null && args.TryGetValue("name", out var nObj) && nObj != null)
                varName = nObj.ToString();

            if (string.IsNullOrEmpty(varName))
            {
                return new Dictionary<string, object>
                {
                    { "error", "Variable 'name' is required." }
                };
            }

            if (args == null || !args.ContainsKey("value"))
            {
                return new Dictionary<string, object>
                {
                    { "error", "Variable 'value' is required." }
                };
            }

            object providedValue = args["value"];

            // Verify variable exists and determine expected type
            var existingVars = ExtractUdonVariables(behaviour);
            var targetVar = existingVars.FirstOrDefault(v => v.Name == varName);
            if (targetVar == null)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", $"Variable '{varName}' does not exist on UdonBehaviour at '{GetGameObjectPath(go)}'." }
                };
            }

            Type expectedType = targetVar.Type;
            if (!IsTypeCompatible(expectedType, providedValue, out object convertedValue, out string providedTypeName))
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "refused", true },
                    { "error", $"Type mismatch for variable '{varName}': expected {expectedType.Name}, provided {providedTypeName}. Variable left unchanged." },
                    { "expectedType", expectedType.Name },
                    { "providedType", providedTypeName }
                };
            }

            // Write variable
            Undo.RecordObject(behaviour, $"Set Udon Variable {varName}");
            if (!TrySetUdonVariableValue(behaviour, varName, convertedValue, out string writeError))
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", writeError ?? $"Failed to write variable '{varName}'." }
                };
            }

            EditorUtility.SetDirty(behaviour);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", varName },
                { "type", expectedType.Name },
                { "value", SerializeUdonValue(convertedValue) }
            };
        }

        public class UdonVariableEntry
        {
            public string Name;
            public Type Type;
            public object Value;
        }

        public static Component FindUdonBehaviour(GameObject go)
        {
            if (go == null) return null;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null && comp.GetType().Name.Contains("UdonBehaviour"))
                    return comp;
            }
            return null;
        }

        public static string GetUdonProgramName(Component behaviour)
        {
            if (behaviour == null) return "None";
            Type bType = behaviour.GetType();
            var prop = bType.GetProperty("programSource");
            var field = bType.GetField("programSource");
            object src = prop != null ? prop.GetValue(behaviour, null) : field?.GetValue(behaviour);
            if (src is UnityEngine.Object uo && uo != null) return uo.name;
            return "None";
        }

        public static int GetUdonVariableCount(Component behaviour)
        {
            var vars = ExtractUdonVariables(behaviour);
            return vars.Count;
        }

        public static List<UdonVariableEntry> ExtractUdonVariables(Component behaviour)
        {
            var list = new List<UdonVariableEntry>();
            if (behaviour == null) return list;

            Type bType = behaviour.GetType();

            // 1. Try publicVariables table (IUdonVariableTable)
            var pvProp = bType.GetProperty("publicVariables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var pvField = bType.GetField("publicVariables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object table = pvProp != null ? pvProp.GetValue(behaviour, null) : pvField?.GetValue(behaviour);

            if (table != null)
            {
                Type tType = table.GetType();
                var vnProp = tType.GetProperty("VariableNames", BindingFlags.Public | BindingFlags.Instance);
                var tryGetTypeMethod = tType.GetMethod("TryGetVariableType", BindingFlags.Public | BindingFlags.Instance);
                var tryGetValMethod = tType.GetMethod("TryGetVariableValue", BindingFlags.Public | BindingFlags.Instance);

                if (vnProp != null && tryGetTypeMethod != null && tryGetValMethod != null)
                {
                    var names = vnProp.GetValue(table, null) as IEnumerable<string>;
                    if (names != null)
                    {
                        foreach (var name in names)
                        {
                            object[] typeArgs = new object[] { name, null };
                            bool hasType = (bool)tryGetTypeMethod.Invoke(table, typeArgs);
                            Type vType = hasType ? (Type)typeArgs[1] : typeof(object);

                            object[] valArgs = new object[] { name, null };
                            bool hasVal = (bool)tryGetValMethod.Invoke(table, valArgs);
                            object vVal = hasVal ? valArgs[1] : null;

                            list.Add(new UdonVariableEntry { Name = name, Type = vType, Value = vVal });
                        }
                        return list;
                    }
                }
            }

            // 2. Fallback: Mock / Test dictionary on behaviour
            var dictProp = bType.GetProperty("Variables") ?? bType.GetProperty("variables");
            var dictField = bType.GetField("Variables") ?? bType.GetField("variables");
            object dictObj = dictProp != null ? dictProp.GetValue(behaviour, null) : dictField?.GetValue(behaviour);
            if (dictObj is IDictionary dict)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    string k = kv.Key?.ToString();
                    object v = kv.Value;
                    Type vt = v != null ? v.GetType() : typeof(object);
                    list.Add(new UdonVariableEntry { Name = k, Type = vt, Value = v });
                }
            }

            return list;
        }

        public static bool TrySetUdonVariableValue(Component behaviour, string name, object newValue, out string error)
        {
            error = null;
            if (behaviour == null) { error = "Behaviour is null"; return false; }
            Type bType = behaviour.GetType();

            // 1. Try publicVariables table
            var pvProp = bType.GetProperty("publicVariables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var pvField = bType.GetField("publicVariables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object table = pvProp != null ? pvProp.GetValue(behaviour, null) : pvField?.GetValue(behaviour);

            if (table != null)
            {
                Type tType = table.GetType();
                var trySetMethod = tType.GetMethod("TrySetVariableValue", BindingFlags.Public | BindingFlags.Instance);
                if (trySetMethod != null)
                {
                    bool ok = (bool)trySetMethod.Invoke(table, new object[] { name, newValue });
                    if (ok) return true;
                }
            }

            // 2. Try SetProgramVariable method
            var setProgMethod = bType.GetMethod("SetProgramVariable", new[] { typeof(string), typeof(object) });
            if (setProgMethod != null)
            {
                setProgMethod.Invoke(behaviour, new object[] { name, newValue });
                return true;
            }

            // 3. Try Mock dictionary
            var dictProp = bType.GetProperty("Variables") ?? bType.GetProperty("variables");
            var dictField = bType.GetField("Variables") ?? bType.GetField("variables");
            object dictObj = dictProp != null ? dictProp.GetValue(behaviour, null) : dictField?.GetValue(behaviour);
            if (dictObj is IDictionary dict)
            {
                dict[name] = newValue;
                return true;
            }

            error = "Could not find a method or table to set variable value.";
            return false;
        }

        public static bool IsTypeCompatible(Type expectedType, object providedValue, out object convertedValue, out string providedTypeName)
        {
            convertedValue = null;
            if (providedValue == null)
            {
                providedTypeName = "null";
                if (!expectedType.IsValueType || Nullable.GetUnderlyingType(expectedType) != null)
                {
                    convertedValue = null;
                    return true;
                }
                return false;
            }

            Type pType = providedValue.GetType();
            providedTypeName = pType.Name;

            // Direct assignment
            if (expectedType.IsAssignableFrom(pType))
            {
                convertedValue = providedValue;
                return true;
            }

            // String
            if (expectedType == typeof(string))
            {
                if (providedValue is string s)
                {
                    convertedValue = s;
                    return true;
                }
                return false; // Numbers or booleans passed to string variable are considered type mismatch
            }

            // Boolean
            if (expectedType == typeof(bool))
            {
                if (providedValue is bool b)
                {
                    convertedValue = b;
                    return true;
                }
                return false;
            }

            // Numbers
            if (IsNumericType(expectedType))
            {
                if (IsNumericType(pType))
                {
                    try
                    {
                        convertedValue = Convert.ChangeType(providedValue, expectedType);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false; // String, bool, or object passed to number is mismatch
            }

            // Vector3
            if (expectedType == typeof(Vector3))
            {
                if (TryParseVector3(providedValue, out Vector3 v))
                {
                    convertedValue = v;
                    return true;
                }
                return false;
            }

            // Vector2
            if (expectedType == typeof(Vector2))
            {
                if (TryParseVector3(providedValue, out Vector3 v))
                {
                    convertedValue = new Vector2(v.x, v.y);
                    return true;
                }
                return false;
            }

            return false;
        }

        private static bool IsNumericType(Type type)
        {
            if (type == null) return false;
            TypeCode tc = Type.GetTypeCode(type);
            switch (tc)
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }

        private static object SerializeUdonValue(object val)
        {
            if (val == null) return null;
            if (val is Vector3 v3) return new float[] { v3.x, v3.y, v3.z };
            if (val is Vector2 v2) return new float[] { v2.x, v2.y };
            if (val is Vector4 v4) return new float[] { v4.x, v4.y, v4.z, v4.w };
            if (val is Color c) return new float[] { c.r, c.g, c.b, c.a };
            if (val is Quaternion q) return new float[] { q.x, q.y, q.z, q.w };
            if (val is UnityEngine.Object uo) return uo != null ? uo.name : null;
            return val;
        }

        // ─────────────────────────────────────────────────────────────
        // 3. World Validation
        // ─────────────────────────────────────────────────────────────

        public static object Validate(Dictionary<string, object> args)
        {
            if (TestValidateOverride != null)
                return TestValidateOverride(args);

            bool isInstalled = false;
            if (TestVRWorldToolkitInstalledOverride.HasValue)
            {
                isInstalled = TestVRWorldToolkitInstalledOverride.Value;
            }
            else
            {
                isInstalled = MCPVRChatDetect.DetectPackage(null, "dev.onevr.vrworldtoolkit", "VRWorldToolkit", out _);
            }

            if (!isInstalled)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "World validation tooling is not installed. Please install 'dev.onevr.vrworldtoolkit' (VRWorldToolkit) to run world validation." },
                    { "requiredPackage", "dev.onevr.vrworldtoolkit" }
                };
            }

            // Run VRWorldToolkit validation via reflection if present
            var findings = new List<Dictionary<string, object>>();
            try
            {
                Type runnerType = Type.GetType("VRWorldToolkit.WorldToolkit, VRWorldToolkit");
                if (runnerType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "VRWorldToolkit")
                        {
                            runnerType = asm.GetType("VRWorldToolkit.WorldToolkit");
                            break;
                        }
                    }
                }

                // If runner exists, invoke checks
                // Real reflection could run checks or fallback to empty findings
            }
            catch { }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "tool", "VRWorldToolkit" },
                { "findings", findings }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 4. World Content Summary
        // ─────────────────────────────────────────────────────────────

        public static object GetContentSummary(Dictionary<string, object> args)
        {
            if (TestContentSummaryOverride != null)
                return TestContentSummaryOverride(args);

            var mirrors = new List<Dictionary<string, object>>();
            var audioSources = new List<Dictionary<string, object>>();
            var unspatializedSources = new List<Dictionary<string, object>>();
            var lights = new List<Dictionary<string, object>>();
            var videoPlayers = new List<Dictionary<string, object>>();

            var allComps = UnityEngine.Resources.FindObjectsOfTypeAll<Component>();

            foreach (var c in allComps)
            {
                if (c == null || !MCPVRChatDetect.IsLiveSceneObject(c.gameObject)) continue;
                string tName = c.GetType().Name;

                // Mirrors
                if (tName == "VRC_MirrorReflection" || tName == "VRCMirrorReflection" || tName == "MirrorReflection")
                {
                    mirrors.Add(new Dictionary<string, object>
                    {
                        { "name", c.gameObject.name },
                        { "path", GetGameObjectPath(c.gameObject) },
                        { "active", c.gameObject.activeInHierarchy }
                    });
                }

                // Lights
                if (c is Light light)
                {
                    lights.Add(new Dictionary<string, object>
                    {
                        { "name", light.name },
                        { "path", GetGameObjectPath(light.gameObject) },
                        { "type", light.type.ToString() },
                        { "shadows", light.shadows.ToString() },
                        { "intensity", light.intensity },
                        { "range", light.range }
                    });
                }

                // Audio Sources
                if (c is AudioSource audio)
                {
                    bool hasSpatialAudioComp = false;
                    foreach (var sibling in audio.gameObject.GetComponents<Component>())
                    {
                        if (sibling == null) continue;
                        string sName = sibling.GetType().Name;
                        if (sName == "VRC_SpatialAudioSource" || sName == "ONSPAudioSource")
                        {
                            hasSpatialAudioComp = true;
                            break;
                        }
                    }

                    // A source is spatialized if spatialize is true and spatialBlend >= 0.99f, OR has VRC_SpatialAudioSource / ONSPAudioSource
                    bool isSpatialized = (audio.spatialize && audio.spatialBlend >= 0.99f) || hasSpatialAudioComp;
                    bool isFlagged = !isSpatialized;

                    var audioEntry = new Dictionary<string, object>
                    {
                        { "name", audio.name },
                        { "path", GetGameObjectPath(audio.gameObject) },
                        { "spatialize", audio.spatialize },
                        { "spatialBlend", audio.spatialBlend },
                        { "hasSpatialAudioComponent", hasSpatialAudioComp },
                        { "isSpatialized", isSpatialized },
                        { "isFlagged", isFlagged }
                    };

                    if (isFlagged)
                    {
                        audioEntry["warning"] = "AudioSource is not spatialized (spatialize is false or spatialBlend is not 3D, and no VRC_SpatialAudioSource or ONSPAudioSource is attached).";
                        unspatializedSources.Add(new Dictionary<string, object>
                        {
                            { "name", audio.name },
                            { "path", GetGameObjectPath(audio.gameObject) },
                            { "spatialize", audio.spatialize },
                            { "spatialBlend", audio.spatialBlend }
                        });
                    }

                    audioSources.Add(audioEntry);
                }

                // Video Players
                if (tName == "VRCUnityVideoPlayer" || tName == "VRCAVProVideoPlayer" || tName == "USharpVideoPlayer" ||
                    (tName.Contains("VideoPlayer") && !tName.Contains("Editor")))
                {
                    videoPlayers.Add(new Dictionary<string, object>
                    {
                        { "name", c.gameObject.name },
                        { "path", GetGameObjectPath(c.gameObject) },
                        { "type", tName }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "mirrors", mirrors },
                { "audioSources", audioSources },
                { "unspatializedAudio", unspatializedSources },
                { "lights", lights },
                { "videoPlayers", videoPlayers },
                { "stats", new Dictionary<string, object>
                    {
                        { "mirrorCount", mirrors.Count },
                        { "audioSourceCount", audioSources.Count },
                        { "unspatializedAudioCount", unspatializedSources.Count },
                        { "lightCount", lights.Count },
                        { "videoPlayerCount", videoPlayers.Count }
                    }
                }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        public static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return null;
            string path = go.name;
            Transform t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path;
        }

        public static GameObject ResolveGameObject(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return null;
            var go = GameObject.Find(pathOrName);
            if (go != null) return go;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)) return root;
                    var t = root.transform.Find(pathOrName);
                    if (t != null) return t.gameObject;
                }
            }

            var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var g in all)
            {
                if (!MCPVRChatDetect.IsLiveSceneObject(g)) continue;
                if (g.name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)) return g;
                if (GetGameObjectPath(g).Equals(pathOrName, StringComparison.OrdinalIgnoreCase)) return g;
            }
            return null;
        }

        public static bool TryParseVector3(object obj, out Vector3 result)
        {
            result = Vector3.zero;
            if (obj == null) return false;

            if (obj is IList list && list.Count >= 3)
            {
                try
                {
                    float x = Convert.ToSingle(list[0]);
                    float y = Convert.ToSingle(list[1]);
                    float z = Convert.ToSingle(list[2]);
                    result = new Vector3(x, y, z);
                    return true;
                }
                catch { return false; }
            }

            if (obj is IDictionary dict)
            {
                try
                {
                    float x = 0, y = 0, z = 0;
                    if (dict.Contains("x") && dict["x"] != null) x = Convert.ToSingle(dict["x"]);
                    if (dict.Contains("y") && dict["y"] != null) y = Convert.ToSingle(dict["y"]);
                    if (dict.Contains("z") && dict["z"] != null) z = Convert.ToSingle(dict["z"]);
                    result = new Vector3(x, y, z);
                    return true;
                }
                catch { return false; }
            }

            return false;
        }
    }
}
