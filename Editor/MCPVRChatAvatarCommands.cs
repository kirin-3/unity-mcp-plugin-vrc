using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityMCP.Editor
{
    /// <summary>
    /// VRChat Avatar analysis commands:
    /// - vrc/avatar/performance: performance rank and contributing statistics
    /// - vrc/avatar/parameters: synced parameter memory budget and per-parameter cost
    /// - vrc/avatar/audit: Write Defaults consistency, missing scripts, texture memory, animation paths,
    ///   mesh bounds and anchor overrides (MCPVRChatAuditChecks)
    /// </summary>
    public static class MCPVRChatAvatarCommands
    {
        // Test hooks for unit testing
        public static Func<GameObject, List<string>, object> TestPerformanceStatsOverride = null;
        public static Func<GameObject, List<string>, object> TestParametersOverride = null;
        public static Func<GameObject, List<string>, object> TestAuditOverride = null;

        public static void ResetTestOverrides()
        {
            TestPerformanceStatsOverride = null;
            TestParametersOverride = null;
            TestAuditOverride = null;
            MCPVRChatBakeHarness.ResetTestOverrides();
        }

        // ─────────────────────────────────────────────────────────────
        // 1. Performance Rank (vrc/avatar/performance)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Performance rank of the NDMF-baked avatar.
        /// Deferred: this bakes the avatar, which blocks Unity's main thread for tens of
        /// seconds, so the route returns a jobId and the work runs on a later tick.
        /// Poll vrc/avatar/job for the result. See MCPVRChatJobs for why.
        /// </summary>
        public static object GetPerformance(Dictionary<string, object> args)
        {
            return MCPVRChatJobs.Start("vrc/avatar/performance", () => GetPerformanceSync(args));
        }

        /// <summary>Runs the analysis inline. Blocks for the duration of the bake.</summary>
        public static object GetPerformanceSync(Dictionary<string, object> args)
        {
            try
            {
                var avatar = ResolveAvatarGameObject(args, out string error);
                if (avatar == null)
                {
                    return new Dictionary<string, object> { { "error", error ?? "Avatar not found." } };
                }

                bool isMobile = false;
                if (args != null && args.TryGetValue("isMobile", out var mObj) && mObj != null)
                {
                    if (bool.TryParse(mObj.ToString(), out bool b)) isMobile = b;
                }

                return MCPVRChatBakeHarness.RunBakeAndAnalyze(avatar, (clone, tooling) =>
                {
                    if (TestPerformanceStatsOverride != null)
                    {
                        return TestPerformanceStatsOverride(clone, tooling);
                    }

                    return CalculatePerformance(avatar.name, clone, tooling, isMobile);
                });
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Avatar performance analysis failed: {ex.Message}" }
                };
            }
        }

        private static object CalculatePerformance(string authoredName, GameObject target, List<string> tooling, bool isMobile)
        {
            Type perfType = FindType("VRC.SDKBase.Validation.Performance.AvatarPerformance");
            Type statsType = FindType("VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats")
                          ?? FindType("VRC.SDKBase.Validation.Performance.AvatarPerformanceStats");
            Type catType = FindType("VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory");

            // No fallback estimate when the SDK cannot be driven: a rank computed any other way is
            // a confident wrong number, and this route promises the SDK's own rating.
            MethodInfo calcMethod = perfType?.GetMethod("CalculatePerformanceStats",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                statsType == null ? Type.EmptyTypes : new[] { typeof(string), typeof(GameObject), statsType, typeof(bool) },
                null);
            if (calcMethod == null || catType == null)
                throw new InvalidOperationException(
                    "VRChat SDK AvatarPerformance.CalculatePerformanceStats was not found; this SDK version is not one this tool knows how to drive.");

            object perfStats = Activator.CreateInstance(statsType, new object[] { isMobile });
            try
            {
                calcMethod.Invoke(null, new object[] { target.name, target, perfStats, isMobile });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException(
                    "VRChat SDK performance calculation failed: " + ex.InnerException.Message, ex.InnerException);
            }

            MethodInfo getRatingMethod = statsType.GetMethod("GetPerformanceRatingForCategory", new[] { catType });
            Type ratingType = getRatingMethod.ReturnType;
            // Returns the AvatarPerformanceStatsLevel (per-rating limits) for the platform.
            MethodInfo getStatLevelMethod = statsType.GetMethod("GetStatLevelForRating", new[] { ratingType, typeof(bool) });

            object overallRatingVal = getRatingMethod.Invoke(perfStats, new[] { Enum.Parse(catType, "Overall") });

            var categories = new Dictionary<string, object>();
            string limitingCategory = null;
            int worstRatingInt = -1;

            foreach (string catName in Enum.GetNames(catType))
            {
                if (catName == "None" || catName == "Overall" || catName == "AvatarPerformanceCategoryCount")
                    continue;

                object catRatingVal = getRatingMethod.Invoke(perfStats, new[] { Enum.Parse(catType, catName) });
                int ratingInt = (int)catRatingVal;

                // Threshold = limit of the next-better rating, i.e. the line the value crossed.
                // None (not rated) and Excellent (nothing better) have no such line.
                object thresholdVal = null;
                string thresholdRating = null;
                if (getStatLevelMethod != null && ratingInt > (int)Enum.Parse(ratingType, "Excellent"))
                {
                    object betterRating = Enum.ToObject(ratingType, ratingInt - 1);
                    thresholdVal = ReadStatValue(getStatLevelMethod.Invoke(null, new[] { betterRating, isMobile }), catName);
                    thresholdRating = betterRating.ToString();
                }

                categories[catName] = new Dictionary<string, object>
                {
                    { "rating", catRatingVal.ToString() },
                    { "value", ReadStatValue(perfStats, catName) },
                    { "threshold", thresholdVal },
                    { "thresholdRating", thresholdRating }
                };

                if (ratingInt > worstRatingInt)
                {
                    worstRatingInt = ratingInt;
                    limitingCategory = catName;
                }
            }

            return new Dictionary<string, object>
            {
                { "avatarName", authoredName },
                { "rank", overallRatingVal.ToString() },
                { "limitingCategory", limitingCategory ?? "None" },
                { "buildTooling", tooling },
                { "isMobile", isMobile },
                { "categories", categories }
            };
        }

        // Reads a category's stat from an AvatarPerformanceStats (the avatar's values) or an
        // AvatarPerformanceStatsLevel (one rating's limits); both use the same field names.
        private static object ReadStatValue(object instance, string categoryName)
        {
            if (instance == null) return null;
            Type type = instance.GetType();

            // PhysBone categories live in one struct: physBone.{componentCount, transformCount, colliderCount, collisionCheckCount}.
            // On the stats object it is a Nullable, so an unset value boxes to null.
            if (categoryName.StartsWith("PhysBone"))
            {
                object physBone = type.GetField("physBone")?.GetValue(instance);
                string sub = Char.ToLowerInvariant(categoryName[8]) + categoryName.Substring(9);
                return physBone?.GetType().GetField(sub)?.GetValue(physBone);
            }

            // Everything else maps category name to a lower-camel field / property (AABB -> aabb via IgnoreCase)
            string propName = Char.ToLowerInvariant(categoryName[0]) + categoryName.Substring(1);
            var field = type.GetField(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null) return JsonSafe(field.GetValue(instance));

            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null) return JsonSafe(prop.GetValue(instance, null));

            return null;
        }

        // The AABB stat is a Bounds; VRChat rates it by size, so report that as [x, y, z].
        private static object JsonSafe(object value) =>
            value is Bounds b ? new List<object> { b.size.x, b.size.y, b.size.z } : value;


        // ─────────────────────────────────────────────────────────────
        // 2. Expression Parameter Budget (vrc/avatar/parameters)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Expression parameter budget of the NDMF-baked avatar.
        /// Deferred: this bakes the avatar, which blocks Unity's main thread for tens of
        /// seconds, so the route returns a jobId and the work runs on a later tick.
        /// Poll vrc/avatar/job for the result. See MCPVRChatJobs for why.
        /// </summary>
        public static object GetParameters(Dictionary<string, object> args)
        {
            return MCPVRChatJobs.Start("vrc/avatar/parameters", () => GetParametersSync(args));
        }

        /// <summary>Runs the analysis inline. Blocks for the duration of the bake.</summary>
        public static object GetParametersSync(Dictionary<string, object> args)
        {
            try
            {
                var avatar = ResolveAvatarGameObject(args, out string error);
                if (avatar == null)
                {
                    return new Dictionary<string, object> { { "error", error ?? "Avatar not found." } };
                }

                return MCPVRChatBakeHarness.RunBakeAndAnalyze(avatar, (clone, tooling) =>
                {
                    if (TestParametersOverride != null)
                    {
                        return TestParametersOverride(clone, tooling);
                    }

                    return InspectParameters(avatar.name, clone, tooling);
                });
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Avatar parameter inspection failed: {ex.Message}" }
                };
            }
        }

        private static object InspectParameters(string authoredName, GameObject target, List<string> tooling)
        {
            var parameterList = new List<Dictionary<string, object>>();
            int totalCost = 0;
            const int limit = 256;

            // Find VRCAvatarDescriptor
            Component descriptor = FindComponentByName(target, "VRCAvatarDescriptor")
                               ?? FindComponentByName(target, "VRC_AvatarDescriptor");

            // Reporting "0 / 256 bits used" for an avatar we could not actually read is
            // worse than an error — it reads as a healthy budget.
            if (descriptor == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"'{authoredName}' has no VRCAvatarDescriptor, so expression parameters cannot be measured." }
                };
            }

            {
                Type descType = descriptor.GetType();
                var expProp = descType.GetProperty("expressionParameters")
                           ?? descType.GetProperty("customExpressions");
                var expField = descType.GetField("expressionParameters");

                object expParams = expProp != null ? expProp.GetValue(descriptor, null) : expField?.GetValue(descriptor);
                if (expParams != null)
                {
                    Type expType = expParams.GetType();
                    var paramsField = expType.GetField("parameters");
                    if (paramsField != null && paramsField.GetValue(expParams) is Array array)
                    {
                        foreach (object item in array)
                        {
                            if (item == null) continue;
                            Type itemType = item.GetType();
                            string pName = itemType.GetField("name")?.GetValue(item)?.ToString() ?? "";
                            if (string.IsNullOrEmpty(pName)) continue;

                            object valTypeObj = itemType.GetField("valueType")?.GetValue(item);
                            string typeStr = valTypeObj?.ToString() ?? "Bool";

                            bool synced = true;
                            var syncedField = itemType.GetField("networkSynced");
                            if (syncedField != null && syncedField.GetValue(item) is bool s) synced = s;

                            bool saved = true;
                            var savedField = itemType.GetField("saved");
                            if (savedField != null && savedField.GetValue(item) is bool sv) saved = sv;

                            float defVal = 0f;
                            var defField = itemType.GetField("defaultValue");
                            if (defField != null && float.TryParse(defField.GetValue(item)?.ToString(), out float df)) defVal = df;

                            // Bit cost
                            int cost = 0;
                            if (synced)
                            {
                                if (typeStr.Equals("Bool", StringComparison.OrdinalIgnoreCase)) cost = 1;
                                else if (typeStr.Equals("Int", StringComparison.OrdinalIgnoreCase)) cost = 8;
                                else if (typeStr.Equals("Float", StringComparison.OrdinalIgnoreCase)) cost = 8;
                            }

                            totalCost += cost;
                            parameterList.Add(new Dictionary<string, object>
                            {
                                { "name", pName },
                                { "type", typeStr },
                                { "cost", cost },
                                { "synced", synced },
                                { "saved", saved },
                                { "defaultValue", defVal }
                            });
                        }
                    }
                }
            }

            int remaining = Math.Max(0, limit - totalCost);
            int overage = Math.Max(0, totalCost - limit);
            bool isOverBudget = totalCost > limit;

            return new Dictionary<string, object>
            {
                { "avatarName", authoredName },
                { "totalUsed", totalCost },
                { "limit", limit },
                { "remaining", remaining },
                { "overage", overage },
                { "isOverBudget", isOverBudget },
                { "parameters", parameterList },
                { "buildTooling", tooling }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 3. Avatar Audits (vrc/avatar/audit)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Write Defaults / missing script / texture memory audit of the NDMF-baked avatar.
        /// Deferred: this bakes the avatar, which blocks Unity's main thread for tens of
        /// seconds, so the route returns a jobId and the work runs on a later tick.
        /// Poll vrc/avatar/job for the result. See MCPVRChatJobs for why.
        /// </summary>
        public static object GetAudit(Dictionary<string, object> args)
        {
            return MCPVRChatJobs.Start("vrc/avatar/audit", () => GetAuditSync(args));
        }

        /// <summary>Runs the analysis inline. Blocks for the duration of the bake.</summary>
        public static object GetAuditSync(Dictionary<string, object> args)
        {
            try
            {
                var avatar = ResolveAvatarGameObject(args, out string error);
                if (avatar == null)
                {
                    return new Dictionary<string, object> { { "error", error ?? "Avatar not found." } };
                }

                return MCPVRChatBakeHarness.RunBakeAndAnalyze(avatar, (clone, tooling) =>
                {
                    if (TestAuditOverride != null)
                    {
                        return TestAuditOverride(clone, tooling);
                    }

                    // 1. Write Defaults Audit
                    var wdResult = AuditWriteDefaults(clone);

                    // 2. Missing Scripts Audit
                    var missingScriptsResult = AuditMissingScripts(clone);

                    // 3. Texture Memory Audit
                    var textureResult = AuditTextureMemory(clone);

                    return new Dictionary<string, object>
                    {
                        { "avatarName", avatar.name },
                        { "buildTooling", tooling },
                        { "writeDefaults", wdResult },
                        { "missingScripts", missingScriptsResult },
                        { "missingScriptsCount", missingScriptsResult.Count },
                        { "hasMissingScripts", missingScriptsResult.Count > 0 },
                        { "textureMemory", textureResult },
                        // 4-6. Checked on the baked clone, after the tooling rewrote paths and meshes.
                        { "animationPaths", RunCheck(() => MCPVRChatAuditChecks.AuditAnimationPaths(clone)) },
                        { "meshBounds", RunCheck(() => MCPVRChatAuditChecks.AuditMeshBounds(clone)) },
                        { "anchorOverrides", RunCheck(() => MCPVRChatAuditChecks.AuditAnchorOverrides(clone)) }
                    };
                });
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Avatar audit failed: {ex.Message}" }
                };
            }
        }

        /// <summary>One failing check reports its own error instead of sinking the whole audit.</summary>
        private static object RunCheck(Func<Dictionary<string, object>> check)
        {
            try
            {
                return check();
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Check failed: {ex.Message}" } };
            }
        }

        private static Dictionary<string, object> AuditWriteDefaults(GameObject target)
        {
            var controllers = FindAnimatorControllers(target);
            var layerReports = new List<Dictionary<string, object>>();
            bool hasMixedSettings = false;
            int totalWdOn = 0;
            int totalWdOff = 0;

            foreach (var kv in controllers)
            {
                string controllerTag = kv.Key;
                AnimatorController controller = kv.Value;
                if (controller == null) continue;

                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine == null) continue;

                    var wdOnStates = new List<string>();
                    var wdOffStates = new List<string>();

                    CollectStateMachineStates(layer.stateMachine, wdOnStates, wdOffStates);

                    bool layerMixed = wdOnStates.Count > 0 && wdOffStates.Count > 0;
                    if (layerMixed) hasMixedSettings = true;

                    totalWdOn += wdOnStates.Count;
                    totalWdOff += wdOffStates.Count;

                    layerReports.Add(new Dictionary<string, object>
                    {
                        { "controller", controllerTag },
                        { "layerName", layer.name },
                        { "isMixed", layerMixed },
                        { "wdOnCount", wdOnStates.Count },
                        { "wdOffCount", wdOffStates.Count },
                        { "wdOnStates", wdOnStates },
                        { "wdOffStates", wdOffStates }
                    });
                }
            }

            // Cross-layer check: if some layers are pure WD ON and others are pure WD OFF
            if (totalWdOn > 0 && totalWdOff > 0)
            {
                hasMixedSettings = true;
            }

            string summary;
            if (layerReports.Count == 0)
            {
                summary = "No animator controller layers found to inspect.";
            }
            else if (hasMixedSettings)
            {
                summary = $"Inconsistent Write Defaults detected: {totalWdOn} states WD ON, {totalWdOff} states WD OFF across {layerReports.Count} layers.";
            }
            else
            {
                string standard = totalWdOn > 0 ? "ON" : "OFF";
                summary = $"Consistent Write Defaults: all states are WD {standard}.";
            }

            return new Dictionary<string, object>
            {
                { "consistent", !hasMixedSettings },
                { "hasMixedSettings", hasMixedSettings },
                { "summary", summary },
                { "totalWdOnStates", totalWdOn },
                { "totalWdOffStates", totalWdOff },
                { "layers", layerReports }
            };
        }

        private static void CollectStateMachineStates(AnimatorStateMachine sm, List<string> wdOn, List<string> wdOff)
        {
            if (sm == null) return;

            foreach (var childState in sm.states)
            {
                var state = childState.state;
                if (state == null) continue;
                if (state.writeDefaultValues)
                    wdOn.Add(state.name);
                else
                    wdOff.Add(state.name);
            }

            foreach (var childSm in sm.stateMachines)
            {
                CollectStateMachineStates(childSm.stateMachine, wdOn, wdOff);
            }
        }

        private static Dictionary<string, AnimatorController> FindAnimatorControllers(GameObject target)
        {
            var result = new Dictionary<string, AnimatorController>();

            // 1. Check Animator on target
            var anim = target.GetComponent<Animator>();
            if (anim != null && anim.runtimeAnimatorController is AnimatorController ac)
            {
                result["Animator"] = ac;
            }

            // 2. Check VRCAvatarDescriptor
            Component descriptor = FindComponentByName(target, "VRCAvatarDescriptor")
                               ?? FindComponentByName(target, "VRC_AvatarDescriptor");
            if (descriptor != null)
            {
                Type dt = descriptor.GetType();
                ExtractControllersFromArray(dt.GetField("baseAnimationLayers")?.GetValue(descriptor), result);
                ExtractControllersFromArray(dt.GetField("specialAnimationLayers")?.GetValue(descriptor), result);
            }

            return result;
        }

        private static void ExtractControllersFromArray(object layerArray, Dictionary<string, AnimatorController> result)
        {
            if (layerArray is Array arr)
            {
                foreach (object item in arr)
                {
                    if (item == null) continue;
                    Type it = item.GetType();
                    string typeName = it.GetField("type")?.GetValue(item)?.ToString() ?? "Layer";
                    object ctrlObj = it.GetField("animatorController")?.GetValue(item);
                    if (ctrlObj is AnimatorController ac)
                    {
                        result[$"VRC_{typeName}"] = ac;
                    }
                }
            }
        }

        private static List<Dictionary<string, object>> AuditMissingScripts(GameObject target)
        {
            var results = new List<Dictionary<string, object>>();
            var allTransforms = target.GetComponentsInChildren<Transform>(true);

            foreach (var t in allTransforms)
            {
                var components = t.GetComponents<Component>();
                int missingCount = 0;
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == null)
                    {
                        missingCount++;
                    }
                }

                if (missingCount > 0)
                {
                    string path = AnimationUtility.CalculateTransformPath(t, target.transform);
                    if (string.IsNullOrEmpty(path)) path = t.name;

                    results.Add(new Dictionary<string, object>
                    {
                        { "objectName", t.name },
                        { "objectPath", path },
                        { "missingCount", missingCount }
                    });
                }
            }

            return results;
        }

        private static Dictionary<string, object> AuditTextureMemory(GameObject target)
        {
            var textures = new HashSet<Texture>();
            var renderers = target.GetComponentsInChildren<Renderer>(true);

            foreach (var r in renderers)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    if (mat.mainTexture != null) textures.Add(mat.mainTexture);

                    Shader shader = mat.shader;
                    if (shader == null) continue;

                    int propCount = ShaderUtil.GetPropertyCount(shader);
                    for (int i = 0; i < propCount; i++)
                    {
                        if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            string propName = ShaderUtil.GetPropertyName(shader, i);
                            Texture tex = mat.GetTexture(propName);
                            if (tex != null) textures.Add(tex);
                        }
                    }
                }
            }

            long totalBytes = 0;
            var rankedList = new List<Dictionary<string, object>>();

            foreach (var tex in textures)
            {
                long mem = Profiler.GetRuntimeMemorySizeLong(tex);
                if (mem <= 0)
                {
                    // Fallback estimate based on dimensions (RGBA32 assumption)
                    mem = (long)tex.width * tex.height * 4;
                }
                totalBytes += mem;

                string assetPath = AssetDatabase.GetAssetPath(tex);
                double mb = Math.Round(mem / (1024.0 * 1024.0), 2);

                rankedList.Add(new Dictionary<string, object>
                {
                    { "name", tex.name },
                    { "assetPath", assetPath },
                    { "dimensions", $"{tex.width}x{tex.height}" },
                    { "memoryBytes", mem },
                    { "memoryMB", mb }
                });
            }

            rankedList.Sort((a, b) => ((long)b["memoryBytes"]).CompareTo((long)a["memoryBytes"]));

            return new Dictionary<string, object>
            {
                { "totalBytes", totalBytes },
                { "totalMB", Math.Round(totalBytes / (1024.0 * 1024.0), 2) },
                { "uniqueTextureCount", textures.Count },
                { "rankedTextures", rankedList }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        public static GameObject ResolveAvatarGameObject(Dictionary<string, object> args, out string errorMessage)
        {
            errorMessage = null;
            string target = null;
            if (args != null)
            {
                if (args.TryGetValue("avatarPath", out var ap) && ap != null) target = ap.ToString();
                else if (args.TryGetValue("avatarName", out var an) && an != null) target = an.ToString();
                else if (args.TryGetValue("path", out var p) && p != null) target = p.ToString();
                else if (args.TryGetValue("name", out var n) && n != null) target = n.ToString();
            }

            if (!string.IsNullOrEmpty(target))
            {
                var go = GameObject.Find(target);
                if (go != null) return go;

                var allGos = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var candidate in allGos)
                {
                    if (!MCPVRChatDetect.IsLiveSceneObject(candidate)) continue;
                    if (candidate.name.Equals(target, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }

                errorMessage = $"Avatar GameObject '{target}' could not be found in the current scene.";
                return null;
            }

            // Find avatar descriptors
            var descriptors = FindAvatarDescriptors();
            if (descriptors.Count == 1)
            {
                return descriptors[0];
            }
            if (descriptors.Count > 1)
            {
                if (Selection.activeGameObject != null)
                {
                    foreach (var d in descriptors)
                    {
                        if (Selection.activeGameObject == d || Selection.activeGameObject.transform.IsChildOf(d.transform))
                            return d;
                    }
                }
                foreach (var d in descriptors)
                {
                    if (d.activeInHierarchy) return d;
                }
                return descriptors[0];
            }

            if (Selection.activeGameObject != null)
            {
                return Selection.activeGameObject;
            }

            var animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            foreach (var anim in animators)
            {
                if (anim.gameObject.activeInHierarchy && anim.transform.parent == null)
                    return anim.gameObject;
            }

            errorMessage = "No avatar found in the active scene. Specify 'avatarName' or 'avatarPath', or select an avatar.";
            return null;
        }

        private static List<GameObject> FindAvatarDescriptors()
        {
            var list = new List<GameObject>();
            var allComps = UnityEngine.Resources.FindObjectsOfTypeAll<Component>();
            foreach (var c in allComps)
            {
                if (c == null || !MCPVRChatDetect.IsLiveSceneObject(c.gameObject)) continue;
                string typeName = c.GetType().Name;
                if (typeName == "VRCAvatarDescriptor" || typeName == "VRC_AvatarDescriptor")
                {
                    if (!list.Contains(c.gameObject))
                        list.Add(c.gameObject);
                }
            }
            return list;
        }

        private static Component FindComponentByName(GameObject target, string typeName)
        {
            if (target == null) return null;
            foreach (var comp in target.GetComponents<Component>())
            {
                if (comp != null && comp.GetType().Name == typeName)
                    return comp;
            }
            return null;
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
