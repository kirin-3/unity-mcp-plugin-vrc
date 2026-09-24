using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for Poiyomi shader tooling:
    /// - vrc/poiyomi/status: discovery of Poiyomi materials with asset path and lock state
    /// - vrc/poiyomi/lock: locks materials individually or in batch across an avatar
    /// - vrc/poiyomi/unlock: unlocks materials individually or in batch across an avatar
    /// - vrc/poiyomi/get-property: reads material properties, keywords, and shader parameters
    /// - vrc/poiyomi/set-property: sets material properties with lock safety (auto-unlock vs refusal)
    /// </summary>
    public static class MCPVRChatPoiyomiCommands
    {
        public const string OptimizerTypeName = "Thry.ThryEditor.ShaderOptimizer";
        public const string AltOptimizerTypeName = "Thry.ShaderOptimizer";

        // Test hooks for unit testing
        public static bool? TestPoiyomiInstalledOverride = null;
        public static string TestMissingMemberOverride = null;
        public static Func<Material, bool?> TestIsLockedOverride = null;
        public static Func<IEnumerable<Material>, bool, Dictionary<string, object>> TestLockActionOverride = null;

        public static void ResetTestOverrides()
        {
            TestPoiyomiInstalledOverride = null;
            TestMissingMemberOverride = null;
            TestIsLockedOverride = null;
            TestLockActionOverride = null;
        }

        // ─────────────────────────────────────────────────────────────
        // 1. Discovery & Status (vrc/poiyomi/status)
        // ─────────────────────────────────────────────────────────────

        public static object GetStatus(Dictionary<string, object> args)
        {
            if (!CheckPoiyomiInstalled(out string version, out string notInstalledError))
            {
                return new Dictionary<string, object>
                {
                    { "error", notInstalledError },
                    { "installed", false }
                };
            }

            Type optType = ResolveOptimizerType(out string optError);

            var materials = ResolveTargetMaterials(args, out string resolveError);
            if (materials == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", resolveError ?? "No materials found." }
                };
            }

            // Filter for Poiyomi materials
            var poiMaterials = materials.Where(IsPoiyomiMaterial).Distinct().ToList();

            var materialReports = new List<Dictionary<string, object>>();
            int lockedCount = 0;
            int unlockedCount = 0;

            foreach (var mat in poiMaterials)
            {
                bool isLocked = CheckIsMaterialLocked(mat, optType);
                if (isLocked) lockedCount++;
                else unlockedCount++;

                materialReports.Add(new Dictionary<string, object>
                {
                    { "name", mat.name },
                    { "assetPath", AssetDatabase.GetAssetPath(mat) },
                    { "shaderName", mat.shader != null ? mat.shader.name : "" },
                    { "isLocked", isLocked }
                });
            }

            return new Dictionary<string, object>
            {
                { "installed", true },
                { "version", version },
                { "totalCount", poiMaterials.Count },
                { "lockedCount", lockedCount },
                { "unlockedCount", unlockedCount },
                { "materials", materialReports }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Lock & Unlock (vrc/poiyomi/lock & vrc/poiyomi/unlock)
        // ─────────────────────────────────────────────────────────────

        public static object Lock(Dictionary<string, object> args)
        {
            return ExecuteLockStateChange(args, true);
        }

        public static object Unlock(Dictionary<string, object> args)
        {
            return ExecuteLockStateChange(args, false);
        }

        private static object ExecuteLockStateChange(Dictionary<string, object> args, bool targetLockState)
        {
            string actionName = targetLockState ? "lock" : "unlock";
            string pastActionName = targetLockState ? "locked" : "unlocked";

            if (!CheckPoiyomiInstalled(out string version, out string notInstalledError))
            {
                return new Dictionary<string, object>
                {
                    { "error", notInstalledError },
                    { "installed", false }
                };
            }

            Type optType = ResolveOptimizerType(out string optError);
            if (optType == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", optError ?? "Poiyomi ShaderOptimizer type could not be resolved." }
                };
            }

            // Resolve required method on optimizer
            string methodName = targetLockState ? "LockMaterials" : "UnlockMaterials";
            if (!ResolveOptimizerMethod(optType, methodName, out MethodInfo method, out string methodError))
            {
                return new Dictionary<string, object>
                {
                    { "error", methodError }
                };
            }

            var materials = ResolveTargetMaterials(args, out string resolveError);
            if (materials == null || materials.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    { "error", resolveError ?? "No materials found to process." }
                };
            }

            var poiMaterials = materials.Where(IsPoiyomiMaterial).Distinct().ToList();
            if (poiMaterials.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    { "error", "None of the specified materials use a Poiyomi shader." }
                };
            }

            // Test hook override
            if (TestLockActionOverride != null)
            {
                return TestLockActionOverride(poiMaterials, targetLockState);
            }

            var results = new List<Dictionary<string, object>>();
            var toChange = new List<Material>();
            int unchangedCount = 0;

            foreach (var mat in poiMaterials)
            {
                bool currentlyLocked = CheckIsMaterialLocked(mat, optType);
                if (currentlyLocked == targetLockState)
                {
                    unchangedCount++;
                    results.Add(new Dictionary<string, object>
                    {
                        { "material", mat.name },
                        { "assetPath", AssetDatabase.GetAssetPath(mat) },
                        { "status", "unchanged" },
                        { "reason", $"Material is already {pastActionName}" }
                    });
                }
                else
                {
                    toChange.Add(mat);
                }
            }

            int changedCount = 0;
            int failedCount = 0;

            // Execute state change on materials requiring modification
            if (toChange.Count > 0)
            {
                // Process per-material so partial batch failure can be tracked with reasons
                foreach (var mat in toChange)
                {
                    try
                    {
                        // Some materials might not be editable (read-only packages)
                        string path = AssetDatabase.GetAssetPath(mat);
                        if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsOpenForEdit(path) && path.StartsWith("Packages/"))
                        {
                            failedCount++;
                            results.Add(new Dictionary<string, object>
                            {
                                { "material", mat.name },
                                { "assetPath", path },
                                { "status", "failed" },
                                { "reason", "Material asset is in an immutable Package folder and cannot be locked/unlocked." }
                            });
                            continue;
                        }

                        // Call optimizer method: LockMaterials(IEnumerable<Material>, ProgressBar)
                        // Note: ProgressBar enum value 0 = None
                        object[] invokeArgs = method.GetParameters().Length == 2
                            ? new object[] { new[] { mat }, 0 }
                            : new object[] { new[] { mat } };

                        object res = method.Invoke(null, invokeArgs);
                        bool ok = res is bool b ? b : true;

                        if (ok)
                        {
                            changedCount++;
                            results.Add(new Dictionary<string, object>
                            {
                                { "material", mat.name },
                                { "assetPath", path },
                                { "status", pastActionName }
                            });
                        }
                        else
                        {
                            failedCount++;
                            results.Add(new Dictionary<string, object>
                            {
                                { "material", mat.name },
                                { "assetPath", path },
                                { "status", "failed" },
                                { "reason", $"Poiyomi {methodName} reported failure." }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        results.Add(new Dictionary<string, object>
                        {
                            { "material", mat.name },
                            { "assetPath", AssetDatabase.GetAssetPath(mat) },
                            { "status", "failed" },
                            { "reason", ex.InnerException != null ? ex.InnerException.Message : ex.Message }
                        });
                    }
                }
            }

            bool overallSuccess = failedCount == 0;
            string summaryMsg;
            if (toChange.Count == 0)
            {
                summaryMsg = $"No materials modified; all {poiMaterials.Count} material(s) were already {pastActionName}.";
            }
            else if (failedCount == 0)
            {
                summaryMsg = $"Successfully {pastActionName} {changedCount} material(s)" + (unchangedCount > 0 ? $" ({unchangedCount} already {pastActionName})." : ".");
            }
            else
            {
                summaryMsg = $"Batch {actionName} partially failed: {changedCount} {pastActionName}, {failedCount} failed, {unchangedCount} unchanged.";
            }

            return new Dictionary<string, object>
            {
                { "success", overallSuccess },
                { "action", actionName },
                { "total", poiMaterials.Count },
                { "changed", changedCount },
                { "unchanged", unchangedCount },
                { "failed", failedCount },
                { "message", summaryMsg },
                { "results", results }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 3. Property Read & Write (vrc/poiyomi/get-property & set-property)
        // ─────────────────────────────────────────────────────────────

        public static object GetProperty(Dictionary<string, object> args)
        {
            if (!CheckPoiyomiInstalled(out string version, out string notInstalledError))
            {
                return new Dictionary<string, object> { { "error", notInstalledError }, { "installed", false } };
            }

            Material mat = ResolveSingleMaterial(args, out string resolveError);
            if (mat == null)
            {
                return new Dictionary<string, object> { { "error", resolveError ?? "Material not found." } };
            }

            if (!IsPoiyomiMaterial(mat))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Material '{mat.name}' does not use a Poiyomi shader (shader: {mat.shader?.name})." }
                };
            }

            Type optType = ResolveOptimizerType(out _);
            bool isLocked = CheckIsMaterialLocked(mat, optType);

            string propName = null;
            if (args != null)
            {
                if (args.TryGetValue("propertyName", out var pn) && pn != null) propName = pn.ToString();
                else if (args.TryGetValue("property", out var p) && p != null) propName = p.ToString();
            }

            if (!string.IsNullOrEmpty(propName))
            {
                if (!mat.HasProperty(propName))
                {
                    // Check if it might be a keyword
                    bool hasKeyword = mat.IsKeywordEnabled(propName);
                    return new Dictionary<string, object>
                    {
                        { "material", mat.name },
                        { "assetPath", AssetDatabase.GetAssetPath(mat) },
                        { "propertyName", propName },
                        { "hasProperty", false },
                        { "isKeyword", true },
                        { "keywordEnabled", hasKeyword },
                        { "isLocked", isLocked }
                    };
                }

                object propVal = ReadMaterialProperty(mat, propName, out string propType);
                return new Dictionary<string, object>
                {
                    { "material", mat.name },
                    { "assetPath", AssetDatabase.GetAssetPath(mat) },
                    { "propertyName", propName },
                    { "propertyType", propType },
                    { "value", propVal },
                    { "isLocked", isLocked }
                };
            }

            // Enumerate all properties
            var propDict = new Dictionary<string, object>();
            Shader shader = mat.shader;
            if (shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    string pName = ShaderUtil.GetPropertyName(shader, i);
                    var pType = ShaderUtil.GetPropertyType(shader, i);
                    object val = ReadMaterialProperty(mat, pName, out string _);
                    propDict[pName] = new Dictionary<string, object>
                    {
                        { "type", pType.ToString() },
                        { "value", val }
                    };
                }
            }

            return new Dictionary<string, object>
            {
                { "material", mat.name },
                { "assetPath", AssetDatabase.GetAssetPath(mat) },
                { "shaderName", shader != null ? shader.name : "" },
                { "isLocked", isLocked },
                { "keywords", mat.shaderKeywords },
                { "properties", propDict }
            };
        }

        public static object SetProperty(Dictionary<string, object> args)
        {
            if (!CheckPoiyomiInstalled(out string version, out string notInstalledError))
            {
                return new Dictionary<string, object> { { "error", notInstalledError }, { "installed", false } };
            }

            Material mat = ResolveSingleMaterial(args, out string resolveError);
            if (mat == null)
            {
                return new Dictionary<string, object> { { "error", resolveError ?? "Material not found." } };
            }

            if (!IsPoiyomiMaterial(mat))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Material '{mat.name}' does not use a Poiyomi shader." }
                };
            }

            string propName = null;
            if (args != null)
            {
                if (args.TryGetValue("propertyName", out var pn) && pn != null) propName = pn.ToString();
                else if (args.TryGetValue("property", out var p) && p != null) propName = p.ToString();
            }

            if (string.IsNullOrEmpty(propName))
            {
                return new Dictionary<string, object> { { "error", "Missing required 'propertyName' argument." } };
            }

            bool unlockIfLocked = false;
            if (args != null && args.TryGetValue("unlockIfLocked", out var uObj) && uObj != null)
            {
                if (bool.TryParse(uObj.ToString(), out bool b)) unlockIfLocked = b;
            }

            Type optType = ResolveOptimizerType(out _);
            bool isLocked = CheckIsMaterialLocked(mat, optType);
            bool unlockedFirst = false;

            // Locked material safety check (Task 6.6)
            if (isLocked)
            {
                if (!unlockIfLocked)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "refused", true },
                        { "unlockedFirst", false },
                        { "material", mat.name },
                        { "propertyName", propName },
                        { "error", $"Material '{mat.name}' is currently locked. Modifying properties on locked Poiyomi materials causes desynchronization with the baked shader. Set 'unlockIfLocked: true' to automatically unlock before writing, or unlock the material first." }
                    };
                }

                // Auto-unlock path
                var unlockResult = Unlock(new Dictionary<string, object>
                {
                    { "materialPath", AssetDatabase.GetAssetPath(mat) }
                }) as Dictionary<string, object>;

                if (unlockResult == null || (unlockResult.ContainsKey("success") && !(bool)unlockResult["success"]))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "refused", true },
                        { "unlockedFirst", false },
                        { "error", $"Failed to automatically unlock locked material: {unlockResult?.GetValueOrDefault("message") ?? "Unknown unlock failure"}" }
                    };
                }

                unlockedFirst = true;
            }

            // Apply property change
            try
            {
                Undo.RecordObject(mat, $"Set Poiyomi Property {propName}");
                object rawValue = args.TryGetValue("value", out var v) ? v : null;
                bool applied = ApplyMaterialProperty(mat, propName, rawValue, out string applyError);
                if (!applied)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "refused", false },
                        { "unlockedFirst", unlockedFirst },
                        { "error", applyError ?? "Failed to set property." }
                    };
                }

                EditorUtility.SetDirty(mat);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "refused", false },
                    { "unlockedFirst", unlockedFirst },
                    { "material", mat.name },
                    { "propertyName", propName },
                    { "value", rawValue },
                    { "message", unlockedFirst
                        ? $"Material '{mat.name}' was locked; automatically unlocked first and updated property '{propName}'."
                        : $"Successfully updated property '{propName}' on material '{mat.name}'." }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "refused", false },
                    { "unlockedFirst", unlockedFirst },
                    { "error", $"Exception setting property '{propName}': {ex.Message}" }
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers & Reflection
        // ─────────────────────────────────────────────────────────────

        public static bool CheckPoiyomiInstalled(out string version, out string error)
        {
            error = null;
            version = null;

            if (TestPoiyomiInstalledOverride.HasValue)
            {
                if (!TestPoiyomiInstalledOverride.Value)
                {
                    error = "Poiyomi is not installed in the project.";
                    return false;
                }
                version = "10.0.11";
                return true;
            }

            if (MCPVRChatDetect.DetectPoiyomi(MCPVRChatDetect.GetProjectPath(), out version))
            {
                return true;
            }

            // Check if Optimizer type exists as fallback
            Type optType = FindType(OptimizerTypeName) ?? FindType(AltOptimizerTypeName);
            if (optType != null)
            {
                version = "Installed";
                return true;
            }

            error = "Poiyomi is not installed in the project. Install Poiyomi Shaders to use vrc/poiyomi/* tools.";
            return false;
        }

        public static Type ResolveOptimizerType(out string errorMessage)
        {
            errorMessage = null;

            Type t = FindType(OptimizerTypeName) ?? FindType(AltOptimizerTypeName);
            if (t == null)
            {
                errorMessage = $"Poiyomi optimizer type '{OptimizerTypeName}' could not be found in loaded assemblies.";
            }
            return t;
        }

        public static bool ResolveOptimizerMethod(Type optType, string methodName, out MethodInfo method, out string errorMessage)
        {
            errorMessage = null;
            method = null;

            if (optType == null)
            {
                errorMessage = "Optimizer type is null.";
                return false;
            }

            if (TestMissingMemberOverride != null && TestMissingMemberOverride == methodName)
            {
                errorMessage = $"Method '{methodName}' not found on Poiyomi optimizer type '{optType.FullName}'.";
                return false;
            }

            method = optType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                errorMessage = $"Method '{methodName}' not found on Poiyomi optimizer type '{optType.FullName}'.";
                return false;
            }

            return true;
        }

        public static bool CheckIsMaterialLocked(Material mat, Type optType)
        {
            if (mat == null) return false;

            if (TestIsLockedOverride != null)
            {
                var overrideVal = TestIsLockedOverride(mat);
                if (overrideVal.HasValue) return overrideVal.Value;
            }

            if (optType != null)
            {
                var isLockedMethod = optType.GetMethod("IsMaterialLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Material) }, null);
                if (isLockedMethod != null)
                {
                    try
                    {
                        object res = isLockedMethod.Invoke(null, new object[] { mat });
                        if (res is bool b) return b;
                    }
                    catch { }
                }
            }

            // Fallback tag / shader check
            string original = mat.GetTag("OriginalShader", false, "");
            string guids = mat.GetTag("AllLockedGUIDS", false, "");
            if (!string.IsNullOrEmpty(original) || !string.IsNullOrEmpty(guids))
                return true;

            if (mat.shader != null && mat.shader.name.IndexOf("Optimized", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        public static bool IsPoiyomiMaterial(Material mat)
        {
            if (mat == null || mat.shader == null) return false;

            string sName = mat.shader.name;
            if (sName.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sName.StartsWith("Poi/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Locked Poiyomi shaders might have tag "OriginalShader" pointing to Poiyomi
            string original = mat.GetTag("OriginalShader", false, "");
            if (original.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                original.StartsWith("Poi/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static List<Material> ResolveTargetMaterials(Dictionary<string, object> args, out string errorMessage)
        {
            errorMessage = null;
            var list = new List<Material>();

            if (args != null)
            {
                // 1. Array of materials
                if (args.TryGetValue("materials", out var matsObj) && matsObj is IEnumerable<object> matEnumerable)
                {
                    foreach (var item in matEnumerable)
                    {
                        if (item == null) continue;
                        var m = LoadMaterialByString(item.ToString());
                        if (m != null) list.Add(m);
                    }
                    return list;
                }

                // 2. Single material
                string matStr = null;
                if (args.TryGetValue("materialPath", out var mp) && mp != null) matStr = mp.ToString();
                else if (args.TryGetValue("materialName", out var mn) && mn != null) matStr = mn.ToString();
                else if (args.TryGetValue("material", out var m) && m != null) matStr = m.ToString();

                if (!string.IsNullOrEmpty(matStr))
                {
                    var singleMat = LoadMaterialByString(matStr);
                    if (singleMat != null)
                    {
                        list.Add(singleMat);
                        return list;
                    }
                    errorMessage = $"Material '{matStr}' could not be loaded.";
                    return null;
                }

                // 3. Avatar target
                string avatarStr = null;
                if (args.TryGetValue("avatarPath", out var ap) && ap != null) avatarStr = ap.ToString();
                else if (args.TryGetValue("avatarName", out var an) && an != null) avatarStr = an.ToString();
                else if (args.TryGetValue("avatar", out var av) && av != null) avatarStr = av.ToString();

                if (!string.IsNullOrEmpty(avatarStr))
                {
                    var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string avError);
                    if (avatar == null)
                    {
                        errorMessage = avError ?? $"Avatar '{avatarStr}' could not be found.";
                        return null;
                    }
                    var renderers = avatar.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in renderers)
                    {
                        foreach (var sm in r.sharedMaterials)
                        {
                            if (sm != null && !list.Contains(sm)) list.Add(sm);
                        }
                    }
                    return list;
                }
            }

            // 4. Default: Find all materials in active scene renderers or Project Assets
            var allRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            foreach (var r in allRenderers)
            {
                foreach (var sm in r.sharedMaterials)
                {
                    if (sm != null && !list.Contains(sm)) list.Add(sm);
                }
            }

            if (list.Count == 0)
            {
                // Fallback to project assets
                string[] guids = AssetDatabase.FindAssets("t:Material");
                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat != null && !list.Contains(mat)) list.Add(mat);
                }
            }

            return list;
        }

        private static Material ResolveSingleMaterial(Dictionary<string, object> args, out string errorMessage)
        {
            errorMessage = null;
            string matStr = null;
            if (args != null)
            {
                if (args.TryGetValue("materialPath", out var mp) && mp != null) matStr = mp.ToString();
                else if (args.TryGetValue("materialName", out var mn) && mn != null) matStr = mn.ToString();
                else if (args.TryGetValue("material", out var m) && m != null) matStr = m.ToString();
                else if (args.TryGetValue("path", out var p) && p != null) matStr = p.ToString();
            }

            if (string.IsNullOrEmpty(matStr))
            {
                errorMessage = "Missing material identifier ('materialPath', 'materialName', or 'material').";
                return null;
            }

            var mat = LoadMaterialByString(matStr);
            if (mat == null)
            {
                errorMessage = $"Material '{matStr}' could not be loaded.";
                return null;
            }
            return mat;
        }

        private static Material LoadMaterialByString(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return null;

            // 1. Direct asset path
            if (identifier.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                identifier.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<Material>(identifier);
            }

            // 2. GUID
            if (identifier.Length == 32 && !identifier.Contains(" ") && !identifier.Contains("."))
            {
                string path = AssetDatabase.GUIDToAssetPath(identifier);
                if (!string.IsNullOrEmpty(path))
                    return AssetDatabase.LoadAssetAtPath<Material>(path);
            }

            // 3. Search by name
            string[] guids = AssetDatabase.FindAssets($"t:Material {identifier}");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m != null && m.name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    return m;
            }

            return null;
        }

        private static object ReadMaterialProperty(Material mat, string propName, out string propType)
        {
            propType = "Unknown";
            Shader shader = mat.shader;
            if (shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyName(shader, i) == propName)
                    {
                        var type = ShaderUtil.GetPropertyType(shader, i);
                        propType = type.ToString();
                        switch (type)
                        {
                            case ShaderUtil.ShaderPropertyType.Color:
                                var c = mat.GetColor(propName);
                                return new[] { c.r, c.g, c.b, c.a };
                            case ShaderUtil.ShaderPropertyType.Vector:
                                var v = mat.GetVector(propName);
                                return new[] { v.x, v.y, v.z, v.w };
                            case ShaderUtil.ShaderPropertyType.Float:
                            case ShaderUtil.ShaderPropertyType.Range:
                                return mat.GetFloat(propName);
                            case ShaderUtil.ShaderPropertyType.TexEnv:
                                var tex = mat.GetTexture(propName);
                                return tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                        }
                    }
                }
            }

            // Fallback checking
            if (mat.HasProperty(propName))
            {
                try { return mat.GetFloat(propName); } catch { }
            }
            return null;
        }

        private static bool ApplyMaterialProperty(Material mat, string propName, object rawValue, out string error)
        {
            error = null;
            if (rawValue == null)
            {
                error = "Value cannot be null.";
                return false;
            }

            Shader shader = mat.shader;
            ShaderUtil.ShaderPropertyType? propType = null;
            if (shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyName(shader, i) == propName)
                    {
                        propType = ShaderUtil.GetPropertyType(shader, i);
                        break;
                    }
                }
            }

            if (propType.HasValue)
            {
                switch (propType.Value)
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        if (float.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        {
                            mat.SetFloat(propName, f);
                            return true;
                        }
                        error = $"Cannot parse '{rawValue}' as a float for property '{propName}'.";
                        return false;

                    case ShaderUtil.ShaderPropertyType.Color:
                        if (TryParseColor(rawValue, out Color color))
                        {
                            mat.SetColor(propName, color);
                            return true;
                        }
                        error = $"Cannot parse '{rawValue}' as a Color (expected RGBA array or hex string) for property '{propName}'.";
                        return false;

                    case ShaderUtil.ShaderPropertyType.Vector:
                        if (TryParseVector4(rawValue, out Vector4 vec))
                        {
                            mat.SetVector(propName, vec);
                            return true;
                        }
                        error = $"Cannot parse '{rawValue}' as a Vector4 for property '{propName}'.";
                        return false;

                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        string texPath = rawValue.ToString();
                        Texture tex = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                        if (tex != null || string.IsNullOrEmpty(texPath))
                        {
                            mat.SetTexture(propName, tex);
                            return true;
                        }
                        error = $"Texture at '{texPath}' could not be loaded.";
                        return false;
                }
            }

            // Keyword check or generic float set
            if (bool.TryParse(rawValue.ToString(), out bool enableKeyword))
            {
                if (enableKeyword) mat.EnableKeyword(propName);
                else mat.DisableKeyword(propName);
                return true;
            }

            if (float.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float fallbackF))
            {
                mat.SetFloat(propName, fallbackF);
                return true;
            }

            error = $"Could not determine property type for '{propName}' on shader '{shader?.name}'.";
            return false;
        }

        private static bool TryParseColor(object raw, out Color color)
        {
            color = Color.white;
            if (raw is IEnumerable<object> list)
            {
                var floats = list.Select(o => Convert.ToSingle(o, CultureInfo.InvariantCulture)).ToList();
                if (floats.Count >= 3)
                {
                    float a = floats.Count >= 4 ? floats[3] : 1f;
                    color = new Color(floats[0], floats[1], floats[2], a);
                    return true;
                }
            }
            if (raw is string hex && hex.StartsWith("#"))
            {
                return ColorUtility.TryParseHtmlString(hex, out color);
            }
            return false;
        }

        private static bool TryParseVector4(object raw, out Vector4 vec)
        {
            vec = Vector4.zero;
            if (raw is IEnumerable<object> list)
            {
                var floats = list.Select(o => Convert.ToSingle(o, CultureInfo.InvariantCulture)).ToList();
                if (floats.Count >= 2)
                {
                    float z = floats.Count >= 3 ? floats[2] : 0f;
                    float w = floats.Count >= 4 ? floats[3] : 0f;
                    vec = new Vector4(floats[0], floats[1], z, w);
                    return true;
                }
            }
            return false;
        }

        private static Type FindType(string fullTypeName)
        {
            Type t = Type.GetType(fullTypeName);
            if (t != null) return t;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = assembly.GetType(fullTypeName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
