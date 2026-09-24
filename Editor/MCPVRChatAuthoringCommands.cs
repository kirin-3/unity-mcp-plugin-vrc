using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for VRChat Avatar authoring:
    /// - vrc/avatar/descriptor/get: inspect view position, lip-sync, eye look, playable layers, expressions
    /// - vrc/avatar/descriptor/set-visemes: auto-assign visemes from blendshapes, report unmapped
    /// - vrc/avatar/descriptor/set-playable-layer: assign controller to a playable layer
    /// - vrc/avatar/parameters/create: add or modify expression parameter with 256-bit budget limit check
    /// - vrc/avatar/menu/get: inspect expression menu controls
    /// - vrc/avatar/menu/add-control: add control to expression menu with 8-control limit check
    /// - vrc/physbone/add & vrc/physbone/configure: attach and set parameters on VRCPhysBone
    /// - vrc/physbone/list: report all PhysBones with root, parameters, and affected transform count
    /// - vrc/contact/add: add contact sender or receiver with shape, tags, parameters
    /// - vrc/contact/list: report all contact senders and receivers
    /// - vrc/avatar/non-destructive/list: report Modular Avatar and VRCFury components with role
    /// - vrc/avatar/modular-avatar/add: add Modular Avatar component (gated on package availability)
    /// - vrc/avatar/vrcfury/add: add VRCFury component (gated on package availability)
    /// </summary>
    public static class MCPVRChatAuthoringCommands
    {
        public const int MaxParameterCost = 256;
        public const int MaxMenuControls = 8;

        public static readonly string[] StandardVisemes = new string[]
        {
            "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou"
        };

        // Test hooks for unit testing
        public static Func<Dictionary<string, object>, object> TestDescriptorGetOverride = null;
        public static Func<Dictionary<string, object>, object> TestSetVisemesOverride = null;
        public static Func<Dictionary<string, object>, object> TestSetPlayableLayerOverride = null;
        public static Func<Dictionary<string, object>, object> TestCreateParamOverride = null;
        public static Func<Dictionary<string, object>, object> TestGetMenuOverride = null;
        public static Func<Dictionary<string, object>, object> TestAddMenuControlOverride = null;
        public static Func<Dictionary<string, object>, object> TestAddPhysBoneOverride = null;
        public static Func<Dictionary<string, object>, object> TestConfigurePhysBoneOverride = null;
        public static Func<Dictionary<string, object>, object> TestListPhysBonesOverride = null;
        public static Func<Dictionary<string, object>, object> TestAddContactOverride = null;
        public static Func<Dictionary<string, object>, object> TestListContactsOverride = null;
        public static Func<Dictionary<string, object>, object> TestListNonDestructiveOverride = null;
        public static bool? TestModularAvatarInstalledOverride = null;
        public static bool? TestVRCFuryInstalledOverride = null;

        public static void ResetTestOverrides()
        {
            TestDescriptorGetOverride = null;
            TestSetVisemesOverride = null;
            TestSetPlayableLayerOverride = null;
            TestCreateParamOverride = null;
            TestGetMenuOverride = null;
            TestAddMenuControlOverride = null;
            TestAddPhysBoneOverride = null;
            TestConfigurePhysBoneOverride = null;
            TestListPhysBonesOverride = null;
            TestAddContactOverride = null;
            TestListContactsOverride = null;
            TestListNonDestructiveOverride = null;
            TestModularAvatarInstalledOverride = null;
            TestVRCFuryInstalledOverride = null;
        }

        // ─────────────────────────────────────────────────────────────
        // 1. Avatar Descriptor: Get (vrc/avatar/descriptor/get)
        // ─────────────────────────────────────────────────────────────

        public static object GetDescriptor(Dictionary<string, object> args)
        {
            if (TestDescriptorGetOverride != null)
                return TestDescriptorGetOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            Component desc = FindDescriptorComponent(avatar);
            if (desc == null)
            {
                return new Dictionary<string, object> { { "error", $"No VRCAvatarDescriptor found on '{avatar.name}'." } };
            }

            Type descType = desc.GetType();

            // 1. View Position
            Vector3 viewPos = Vector3.zero;
            var vpProp = descType.GetProperty("ViewPosition") ?? descType.GetProperty("viewPosition");
            var vpField = descType.GetField("ViewPosition") ?? descType.GetField("viewPosition");
            if (vpProp != null && vpProp.GetValue(desc, null) is Vector3 v1) viewPos = v1;
            else if (vpField != null && vpField.GetValue(desc) is Vector3 v2) viewPos = v2;

            // 2. LipSync Configuration
            string lipSyncMode = "Default";
            var lsProp = descType.GetProperty("lipSync") ?? descType.GetProperty("LipSync");
            var lsField = descType.GetField("lipSync") ?? descType.GetField("LipSync");
            object lsVal = lsProp != null ? lsProp.GetValue(desc, null) : lsField?.GetValue(desc);
            if (lsVal != null) lipSyncMode = lsVal.ToString();

            string visemeSkinnedMeshName = null;
            var smProp = descType.GetProperty("VisemeSkinnedMesh") ?? descType.GetProperty("visemeSkinnedMesh");
            var smField = descType.GetField("VisemeSkinnedMesh") ?? descType.GetField("visemeSkinnedMesh");
            if (smProp?.GetValue(desc, null) is SkinnedMeshRenderer smr1 && smr1 != null) visemeSkinnedMeshName = smr1.name;
            else if (smField?.GetValue(desc) is SkinnedMeshRenderer smr2 && smr2 != null) visemeSkinnedMeshName = smr2.name;

            List<string> visemeBlendShapes = new List<string>();
            var vbProp = descType.GetProperty("VisemeBlendShapes") ?? descType.GetProperty("visemeBlendShapes");
            var vbField = descType.GetField("VisemeBlendShapes") ?? descType.GetField("visemeBlendShapes");
            object vbVal = vbProp != null ? vbProp.GetValue(desc, null) : vbField?.GetValue(desc);
            if (vbVal is string[] vbArr)
            {
                visemeBlendShapes.AddRange(vbArr);
            }

            string jawFlapName = null;
            var jfProp = descType.GetProperty("jawFlapBlendShapeName");
            var jfField = descType.GetField("jawFlapBlendShapeName");
            if (jfProp?.GetValue(desc, null) is string jfs1) jawFlapName = jfs1;
            else if (jfField?.GetValue(desc) is string jfs2) jawFlapName = jfs2;

            // 3. Eye Look Settings
            bool enableEyeLook = false;
            var eyeProp = descType.GetProperty("enableEyeLook");
            var eyeField = descType.GetField("enableEyeLook");
            if (eyeProp != null && eyeProp.GetValue(desc, null) is bool e1) enableEyeLook = e1;
            else if (eyeField != null && eyeField.GetValue(desc) is bool e2) enableEyeLook = e2;

            // 4. Playable Layers
            var playableLayers = new List<Dictionary<string, object>>();
            ExtractAnimLayers(desc, descType, "baseAnimationLayers", playableLayers);
            ExtractAnimLayers(desc, descType, "specialAnimationLayers", playableLayers);

            // 5. Expression Assets
            string menuAssetPath = null;
            string paramsAssetPath = null;

            var menuProp = descType.GetProperty("expressionsMenu");
            var menuField = descType.GetField("expressionsMenu");
            object menuObj = menuProp != null ? menuProp.GetValue(desc, null) : menuField?.GetValue(desc);
            if (menuObj is UnityEngine.Object menuUObj && menuUObj != null)
                menuAssetPath = AssetDatabase.GetAssetPath(menuUObj);

            var paramProp = descType.GetProperty("expressionParameters");
            var paramField = descType.GetField("expressionParameters");
            object paramObj = paramProp != null ? paramProp.GetValue(desc, null) : paramField?.GetValue(desc);
            if (paramObj is UnityEngine.Object paramUObj && paramUObj != null)
                paramsAssetPath = AssetDatabase.GetAssetPath(paramUObj);

            return new Dictionary<string, object>
            {
                { "avatarName", avatar.name },
                { "viewPosition", new Dictionary<string, object> { { "x", viewPos.x }, { "y", viewPos.y }, { "z", viewPos.z } } },
                { "lipSync", new Dictionary<string, object>
                    {
                        { "mode", lipSyncMode },
                        { "visemeSkinnedMesh", visemeSkinnedMeshName },
                        { "visemeBlendShapes", visemeBlendShapes },
                        { "jawFlapBlendShapeName", jawFlapName }
                    }
                },
                { "eyeLook", new Dictionary<string, object>
                    {
                        { "enabled", enableEyeLook }
                    }
                },
                { "playableLayers", playableLayers },
                { "expressions", new Dictionary<string, object>
                    {
                        { "expressionsMenu", menuAssetPath },
                        { "expressionParameters", paramsAssetPath }
                    }
                }
            };
        }

        private static void ExtractAnimLayers(Component desc, Type descType, string fieldName, List<Dictionary<string, object>> outLayers)
        {
            var prop = descType.GetProperty(fieldName);
            var field = descType.GetField(fieldName);
            object val = prop != null ? prop.GetValue(desc, null) : field?.GetValue(desc);
            if (val is Array arr)
            {
                foreach (object item in arr)
                {
                    if (item == null) continue;
                    Type itemType = item.GetType();
                    string typeStr = itemType.GetField("type")?.GetValue(item)?.ToString() ?? "Unknown";
                    bool isDefault = true;
                    var defField = itemType.GetField("isDefault");
                    if (defField != null && defField.GetValue(item) is bool d) isDefault = d;

                    string controllerPath = null;
                    var ctrlField = itemType.GetField("animatorController");
                    if (ctrlField != null && ctrlField.GetValue(item) is RuntimeAnimatorController rac && rac != null)
                    {
                        controllerPath = AssetDatabase.GetAssetPath(rac);
                    }

                    outLayers.Add(new Dictionary<string, object>
                    {
                        { "type", typeStr },
                        { "isDefault", isDefault },
                        { "animatorController", controllerPath }
                    });
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Viseme Assignment (vrc/avatar/descriptor/set-visemes)
        // ─────────────────────────────────────────────────────────────

        public static object SetVisemes(Dictionary<string, object> args)
        {
            if (TestSetVisemesOverride != null)
                return TestSetVisemesOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            Component desc = FindDescriptorComponent(avatar);
            if (desc == null)
            {
                return new Dictionary<string, object> { { "error", $"No VRCAvatarDescriptor found on '{avatar.name}'." } };
            }

            // Find SkinnedMeshRenderer
            SkinnedMeshRenderer smr = null;
            string meshTarget = null;
            if (args != null && (args.TryGetValue("meshPath", out var mp) || args.TryGetValue("skinnedMeshPath", out mp)) && mp != null)
            {
                meshTarget = mp.ToString();
                Transform t = avatar.transform.Find(meshTarget);
                if (t != null) smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr == null)
                {
                    foreach (var s in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (s.name.Equals(meshTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            smr = s;
                            break;
                        }
                    }
                }
                // Named but not found: guessing a different mesh would write visemes onto it.
                if (smr == null)
                {
                    return new Dictionary<string, object> { { "error", $"SkinnedMeshRenderer '{meshTarget}' was not found on '{avatar.name}'." } };
                }
            }

            if (smr == null)
            {
                // Default to first SkinnedMeshRenderer with blendshapes or named Body/Face/Head
                var smrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                smr = smrs.FirstOrDefault(s => s.name.Equals("Body", StringComparison.OrdinalIgnoreCase) && s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0)
                   ?? smrs.FirstOrDefault(s => s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0);
            }

            if (smr == null || smr.sharedMesh == null)
            {
                return new Dictionary<string, object> { { "error", "No valid SkinnedMeshRenderer with blendshapes found on avatar." } };
            }

            Mesh mesh = smr.sharedMesh;
            int bsCount = mesh.blendShapeCount;
            var availableBlendShapes = new List<string>();
            for (int i = 0; i < bsCount; i++)
            {
                availableBlendShapes.Add(mesh.GetBlendShapeName(i));
            }

            // Manual mapping overrides if supplied
            Dictionary<string, string> manualMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args != null && args.TryGetValue("mapping", out var mObj) && mObj is Dictionary<string, object> mDict)
            {
                foreach (var kvp in mDict)
                {
                    if (kvp.Value != null) manualMappings[kvp.Key] = kvp.Value.ToString();
                }
            }

            string[] assignedVisemes = new string[StandardVisemes.Length];
            var unmapped = new List<string>();
            var mappingsReport = new Dictionary<string, string>();
            int mappedCount = 0;

            for (int i = 0; i < StandardVisemes.Length; i++)
            {
                string viseme = StandardVisemes[i];
                string match = null;

                // 1. Check manual mapping
                if (manualMappings.TryGetValue(viseme, out string manualName))
                {
                    if (availableBlendShapes.Contains(manualName, StringComparer.OrdinalIgnoreCase))
                    {
                        match = availableBlendShapes.First(b => b.Equals(manualName, StringComparison.OrdinalIgnoreCase));
                    }
                }

                // 2. Convention match
                if (match == null)
                {
                    match = FindMatchingVisemeBlendShape(viseme, availableBlendShapes);
                }

                if (match != null)
                {
                    assignedVisemes[i] = match;
                    mappingsReport[viseme] = match;
                    mappedCount++;
                }
                else
                {
                    // MUST NOT GUESS! Report as unmapped
                    assignedVisemes[i] = "";
                    mappingsReport[viseme] = null;
                    unmapped.Add(viseme);
                }
            }

            // Apply to descriptor
            Undo.RecordObject(desc, "Assign Viseme BlendShapes");

            Type descType = desc.GetType();
            // Set visemeSkinnedMesh
            var smProp = descType.GetProperty("VisemeSkinnedMesh") ?? descType.GetProperty("visemeSkinnedMesh");
            var smField = descType.GetField("VisemeSkinnedMesh") ?? descType.GetField("visemeSkinnedMesh");
            if (smProp != null && smProp.CanWrite) smProp.SetValue(desc, smr, null);
            else smField?.SetValue(desc, smr);

            // Set LipSync mode to VisemeBlendShape (value 3 in LipSyncStyle enum)
            Type lsEnumType = FindType("VRC.SDKBase.VRC_AvatarDescriptor+LipSyncStyle")
                           ?? FindType("VRCSDK2.VRC_AvatarDescriptor+LipSyncStyle");
            if (lsEnumType != null)
            {
                try
                {
                    object enumVal = Enum.Parse(lsEnumType, "VisemeBlendShape");
                    var lsProp = descType.GetProperty("lipSync") ?? descType.GetProperty("LipSync");
                    var lsField = descType.GetField("lipSync") ?? descType.GetField("LipSync");
                    if (lsProp != null && lsProp.CanWrite) lsProp.SetValue(desc, enumVal, null);
                    else lsField?.SetValue(desc, enumVal);
                }
                catch { }
            }

            // Set VisemeBlendShapes array
            var vbProp = descType.GetProperty("VisemeBlendShapes") ?? descType.GetProperty("visemeBlendShapes");
            var vbField = descType.GetField("VisemeBlendShapes") ?? descType.GetField("visemeBlendShapes");
            if (vbProp != null && vbProp.CanWrite) vbProp.SetValue(desc, assignedVisemes, null);
            else vbField?.SetValue(desc, assignedVisemes);

            EditorUtility.SetDirty(desc);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "meshName", smr.name },
                { "visemesMapped", mappedCount },
                { "visemesUnmapped", unmapped.Count },
                { "unmappedVisemes", unmapped },
                { "mappings", mappingsReport }
            };
        }

        private static string FindMatchingVisemeBlendShape(string viseme, List<string> blendShapes)
        {
            // Standard candidate prefixes and naming formats for each viseme
            List<string> candidates = new List<string>();

            // Canonical candidates based on viseme
            switch (viseme.ToLowerInvariant())
            {
                case "sil":
                    candidates.AddRange(new[] { "vrc.v_sil", "v_sil", "sil", "viseme_sil", "vrc_viseme_sil", "vrc.v-sil", "fcl_vrc_sil" });
                    break;
                case "pp":
                    candidates.AddRange(new[] { "vrc.v_pp", "v_pp", "pp", "viseme_pp", "vrc_viseme_pp", "vrc.v-pp", "fcl_vrc_pp" });
                    break;
                case "ff":
                    candidates.AddRange(new[] { "vrc.v_ff", "v_ff", "ff", "viseme_ff", "vrc_viseme_ff", "vrc.v-ff", "fcl_vrc_ff" });
                    break;
                case "th":
                    candidates.AddRange(new[] { "vrc.v_th", "v_th", "th", "viseme_th", "vrc_viseme_th", "vrc.v-th", "fcl_vrc_th" });
                    break;
                case "dd":
                    candidates.AddRange(new[] { "vrc.v_dd", "v_dd", "dd", "viseme_dd", "vrc_viseme_dd", "vrc.v-dd", "fcl_vrc_dd" });
                    break;
                case "kk":
                    candidates.AddRange(new[] { "vrc.v_kk", "v_kk", "kk", "viseme_kk", "vrc_viseme_kk", "vrc.v-kk", "k", "v_k", "fcl_vrc_kk" });
                    break;
                case "ch":
                    candidates.AddRange(new[] { "vrc.v_ch", "v_ch", "ch", "viseme_ch", "vrc_viseme_ch", "vrc.v-ch", "fcl_vrc_ch" });
                    break;
                case "ss":
                    candidates.AddRange(new[] { "vrc.v_ss", "v_ss", "ss", "viseme_ss", "vrc_viseme_ss", "vrc.v-ss", "fcl_vrc_ss" });
                    break;
                case "nn":
                    candidates.AddRange(new[] { "vrc.v_nn", "v_nn", "nn", "viseme_nn", "vrc_viseme_nn", "vrc.v-nn", "fcl_vrc_nn" });
                    break;
                case "rr":
                    candidates.AddRange(new[] { "vrc.v_rr", "v_rr", "rr", "viseme_rr", "vrc_viseme_rr", "vrc.v-rr", "fcl_vrc_rr" });
                    break;
                case "aa":
                    candidates.AddRange(new[] { "vrc.v_aa", "v_aa", "aa", "viseme_aa", "vrc_viseme_aa", "vrc.v-aa", "ah", "v_ah", "vrc.v_ah", "fcl_vrc_aa" });
                    break;
                case "e":
                    candidates.AddRange(new[] { "vrc.v_e", "v_e", "e", "viseme_e", "vrc_viseme_e", "vrc.v-e", "eh", "v_eh", "vrc.v_eh", "fcl_vrc_e" });
                    break;
                case "ih":
                    candidates.AddRange(new[] { "vrc.v_ih", "v_ih", "ih", "viseme_ih", "vrc_viseme_ih", "vrc.v-ih", "i", "v_i", "fcl_vrc_ih" });
                    break;
                case "oh":
                    candidates.AddRange(new[] { "vrc.v_oh", "v_oh", "oh", "viseme_oh", "vrc_viseme_oh", "vrc.v-oh", "o", "v_o", "fcl_vrc_oh" });
                    break;
                case "ou":
                    candidates.AddRange(new[] { "vrc.v_ou", "v_ou", "ou", "viseme_ou", "vrc_viseme_ou", "vrc.v-ou", "u", "v_u", "oo", "v_oo", "fcl_vrc_ou" });
                    break;
            }

            foreach (var cand in candidates)
            {
                foreach (var bs in blendShapes)
                {
                    if (bs.Equals(cand, StringComparison.OrdinalIgnoreCase))
                        return bs;
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // 3. Playable Layer Assignment (vrc/avatar/descriptor/set-playable-layer)
        // ─────────────────────────────────────────────────────────────

        public static object SetPlayableLayer(Dictionary<string, object> args)
        {
            if (TestSetPlayableLayerOverride != null)
                return TestSetPlayableLayerOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            Component desc = FindDescriptorComponent(avatar);
            if (desc == null)
            {
                return new Dictionary<string, object> { { "error", $"No VRCAvatarDescriptor found on '{avatar.name}'." } };
            }

            string layerType = null;
            if (args != null && args.TryGetValue("layerType", out var ltObj) && ltObj != null)
                layerType = ltObj.ToString();

            if (string.IsNullOrEmpty(layerType))
            {
                return new Dictionary<string, object> { { "error", "Missing required 'layerType' parameter (e.g. 'FX', 'Gesture', 'Action', 'Base')." } };
            }

            string controllerPath = null;
            if (args != null && args.TryGetValue("controllerPath", out var cpObj) && cpObj != null)
                controllerPath = cpObj.ToString();

            RuntimeAnimatorController controller = null;
            if (!string.IsNullOrEmpty(controllerPath))
            {
                controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
                if (controller == null)
                {
                    return new Dictionary<string, object> { { "error", $"AnimatorController could not be loaded from path '{controllerPath}'." } };
                }
            }

            Undo.RecordObject(desc, $"Set Playable Layer {layerType}");

            Type descType = desc.GetType();
            bool found = AssignLayerInArray(desc, descType, "baseAnimationLayers", layerType, controller, out bool isDefault);
            if (!found)
            {
                found = AssignLayerInArray(desc, descType, "specialAnimationLayers", layerType, controller, out isDefault);
            }

            if (!found)
            {
                return new Dictionary<string, object> { { "error", $"Playable layer type '{layerType}' not found in descriptor layers." } };
            }

            EditorUtility.SetDirty(desc);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "avatarName", avatar.name },
                { "layerType", layerType },
                { "controllerPath", controller != null ? AssetDatabase.GetAssetPath(controller) : null },
                { "isDefault", isDefault }
            };
        }

        private static bool AssignLayerInArray(Component desc, Type descType, string fieldName, string layerType, RuntimeAnimatorController controller, out bool isDefault)
        {
            isDefault = true;
            var prop = descType.GetProperty(fieldName);
            var field = descType.GetField(fieldName);
            object val = prop != null ? prop.GetValue(desc, null) : field?.GetValue(desc);
            if (val is Array arr)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    object item = arr.GetValue(i);
                    if (item == null) continue;
                    Type itemType = item.GetType();
                    string typeStr = itemType.GetField("type")?.GetValue(item)?.ToString() ?? "";
                    if (typeStr.Equals(layerType, StringComparison.OrdinalIgnoreCase))
                    {
                        var ctrlField = itemType.GetField("animatorController");
                        var defField = itemType.GetField("isDefault");

                        if (controller != null)
                        {
                            ctrlField?.SetValue(item, controller);
                            defField?.SetValue(item, false);
                            isDefault = false;
                        }
                        else
                        {
                            ctrlField?.SetValue(item, null);
                            defField?.SetValue(item, true);
                            isDefault = true;
                        }

                        arr.SetValue(item, i);
                        return true;
                    }
                }
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // 4. Expression Parameters (vrc/avatar/parameters/create)
        // ─────────────────────────────────────────────────────────────

        public static object CreateParameter(Dictionary<string, object> args)
        {
            if (TestCreateParamOverride != null)
                return TestCreateParamOverride(args);

            string name = null;
            if (args != null && args.TryGetValue("name", out var nObj) && nObj != null)
                name = nObj.ToString();

            if (string.IsNullOrEmpty(name))
            {
                return new Dictionary<string, object> { { "error", "Missing required 'name' parameter." } };
            }

            string type = "Bool";
            if (args != null && args.TryGetValue("type", out var tObj) && tObj != null)
                type = tObj.ToString();

            bool synced = true;
            if (args != null && args.TryGetValue("synced", out var sObj) && sObj != null)
            {
                if (bool.TryParse(sObj.ToString(), out bool b)) synced = b;
            }

            bool saved = true;
            if (args != null && args.TryGetValue("saved", out var svObj) && svObj != null)
            {
                if (bool.TryParse(svObj.ToString(), out bool b)) saved = b;
            }

            float defaultValue = 0f;
            if (args != null && args.TryGetValue("defaultValue", out var dvObj) && dvObj != null)
            {
                if (bool.TryParse(dvObj.ToString(), out bool bVal)) defaultValue = bVal ? 1f : 0f;
                else if (float.TryParse(dvObj.ToString(), out float fVal)) defaultValue = fVal;
            }

            // Calculate cost for this parameter
            int paramCost = 0;
            if (synced)
            {
                if (type.Equals("Bool", StringComparison.OrdinalIgnoreCase)) paramCost = 1;
                else if (type.Equals("Int", StringComparison.OrdinalIgnoreCase)) paramCost = 8;
                else if (type.Equals("Float", StringComparison.OrdinalIgnoreCase)) paramCost = 8;
            }

            // Resolve VRCExpressionParameters asset
            UnityEngine.Object expParamsAsset = ResolveParametersAsset(args, out string resolveError);
            if (expParamsAsset == null)
            {
                return new Dictionary<string, object> { { "error", resolveError ?? "Could not resolve VRCExpressionParameters asset." } };
            }

            Type expType = expParamsAsset.GetType();
            FieldInfo paramsField = expType.GetField("parameters");
            if (paramsField == null)
            {
                return new Dictionary<string, object> { { "error", "Invalid VRCExpressionParameters: 'parameters' field not found." } };
            }

            Array currentParams = paramsField.GetValue(expParamsAsset) as Array;
            int currentTotalCost = 0;
            int existingParamIndex = -1;
            int existingParamCost = 0;

            if (currentParams != null)
            {
                for (int i = 0; i < currentParams.Length; i++)
                {
                    object item = currentParams.GetValue(i);
                    if (item == null) continue;
                    Type itemType = item.GetType();
                    string pName = itemType.GetField("name")?.GetValue(item)?.ToString() ?? "";
                    if (string.IsNullOrEmpty(pName)) continue;

                    string pType = itemType.GetField("valueType")?.GetValue(item)?.ToString() ?? "Bool";
                    bool pSynced = true;
                    var synF = itemType.GetField("networkSynced");
                    if (synF != null && synF.GetValue(item) is bool s) pSynced = s;

                    int cost = 0;
                    if (pSynced)
                    {
                        if (pType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) cost = 1;
                        else if (pType.Equals("Int", StringComparison.OrdinalIgnoreCase)) cost = 8;
                        else if (pType.Equals("Float", StringComparison.OrdinalIgnoreCase)) cost = 8;
                    }

                    if (pName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        existingParamIndex = i;
                        existingParamCost = cost;
                    }
                    else
                    {
                        currentTotalCost += cost;
                    }
                }
            }

            // Check budget: current other params + new param cost
            int projectedTotalCost = currentTotalCost + paramCost;

            // Task 7.5: Refuse a parameter addition that would exceed the memory limit
            if (projectedTotalCost > MaxParameterCost)
            {
                int overage = projectedTotalCost - MaxParameterCost;
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "refused", true },
                    { "limit", MaxParameterCost },
                    { "currentUsed", currentTotalCost + existingParamCost },
                    { "paramCost", paramCost },
                    { "overage", overage },
                    { "error", $"Adding parameter '{name}' ({paramCost} bits) would exceed the {MaxParameterCost}-bit memory limit by {overage} bits (total would be {projectedTotalCost}/{MaxParameterCost}). Parameters asset was unchanged." }
                };
            }

            // Build new array or update existing
            Undo.RecordObject(expParamsAsset, $"Create/Modify Parameter {name}");

            Type paramItemType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters+Parameter");
            if (paramItemType == null)
            {
                return new Dictionary<string, object> { { "error", "VRCExpressionParameters.Parameter type could not be loaded." } };
            }

            Type valEnumType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters+ValueType");

            object newParamObj = Activator.CreateInstance(paramItemType);
            paramItemType.GetField("name")?.SetValue(newParamObj, name);
            if (valEnumType != null)
            {
                try
                {
                    object ev = Enum.Parse(valEnumType, type, true);
                    paramItemType.GetField("valueType")?.SetValue(newParamObj, ev);
                }
                catch { }
            }
            paramItemType.GetField("defaultValue")?.SetValue(newParamObj, defaultValue);
            paramItemType.GetField("saved")?.SetValue(newParamObj, saved);
            paramItemType.GetField("networkSynced")?.SetValue(newParamObj, synced);

            Array newArray;
            if (existingParamIndex >= 0 && currentParams != null)
            {
                newArray = Array.CreateInstance(paramItemType, currentParams.Length);
                Array.Copy(currentParams, newArray, currentParams.Length);
                newArray.SetValue(newParamObj, existingParamIndex);
            }
            else
            {
                int oldLen = currentParams != null ? currentParams.Length : 0;
                newArray = Array.CreateInstance(paramItemType, oldLen + 1);
                if (currentParams != null && oldLen > 0)
                {
                    Array.Copy(currentParams, newArray, oldLen);
                }
                newArray.SetValue(newParamObj, oldLen);
            }

            paramsField.SetValue(expParamsAsset, newArray);
            EditorUtility.SetDirty(expParamsAsset);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "refused", false },
                { "name", name },
                { "type", type },
                { "defaultValue", defaultValue },
                { "saved", saved },
                { "synced", synced },
                { "cost", paramCost },
                { "totalUsed", projectedTotalCost },
                { "limit", MaxParameterCost },
                { "remaining", MaxParameterCost - projectedTotalCost }
            };
        }

        private static UnityEngine.Object ResolveParametersAsset(Dictionary<string, object> args, out string error)
        {
            error = null;
            if (args != null && args.TryGetValue("parametersAssetPath", out var pObj) && pObj != null)
            {
                string path = pObj.ToString();
                var loaded = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (loaded != null) return loaded;
                error = $"No VRCExpressionParameters found at path '{path}'.";
                return null;
            }

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out error);
            if (avatar != null)
            {
                Component desc = FindDescriptorComponent(avatar);
                if (desc != null)
                {
                    Type descType = desc.GetType();
                    var pProp = descType.GetProperty("expressionParameters");
                    var pField = descType.GetField("expressionParameters");
                    object val = pProp != null ? pProp.GetValue(desc, null) : pField?.GetValue(desc);
                    if (val is UnityEngine.Object uo && uo != null) return uo;
                }
            }

            error = "Could not locate VRCExpressionParameters. Specify 'parametersAssetPath' or an avatar with an assigned parameters asset.";
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // 5. Expression Menus (vrc/avatar/menu/get & add-control)
        // ─────────────────────────────────────────────────────────────

        public static object GetMenu(Dictionary<string, object> args)
        {
            if (TestGetMenuOverride != null)
                return TestGetMenuOverride(args);

            UnityEngine.Object menuAsset = ResolveMenuAsset(args, out string resolveErr);
            if (menuAsset == null)
            {
                return new Dictionary<string, object> { { "error", resolveErr ?? "Expression menu asset not found." } };
            }

            Type menuType = menuAsset.GetType();
            FieldInfo controlsField = menuType.GetField("controls");
            var controlList = new List<Dictionary<string, object>>();

            if (controlsField?.GetValue(menuAsset) is IList list)
            {
                foreach (object ctrl in list)
                {
                    if (ctrl == null) continue;
                    Type ct = ctrl.GetType();
                    string cName = ct.GetField("name")?.GetValue(ctrl)?.ToString() ?? "";
                    string cType = ct.GetField("type")?.GetValue(ctrl)?.ToString() ?? "Button";
                    float val = 0f;
                    var vf = ct.GetField("value");
                    if (vf?.GetValue(ctrl) is float f) val = f;

                    string paramName = null;
                    var pf = ct.GetField("parameter");
                    object pObj = pf?.GetValue(ctrl);
                    if (pObj != null)
                    {
                        paramName = pObj.GetType().GetField("name")?.GetValue(pObj)?.ToString();
                    }

                    string subMenuPath = null;
                    var smf = ct.GetField("subMenu");
                    if (smf?.GetValue(ctrl) is UnityEngine.Object subObj && subObj != null)
                    {
                        subMenuPath = AssetDatabase.GetAssetPath(subObj);
                    }

                    controlList.Add(new Dictionary<string, object>
                    {
                        { "name", cName },
                        { "type", cType },
                        { "parameter", paramName },
                        { "value", val },
                        { "subMenu", subMenuPath }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "menuName", menuAsset.name },
                { "assetPath", AssetDatabase.GetAssetPath(menuAsset) },
                { "controlCount", controlList.Count },
                { "limit", MaxMenuControls },
                { "controls", controlList }
            };
        }

        public static object AddMenuControl(Dictionary<string, object> args)
        {
            if (TestAddMenuControlOverride != null)
                return TestAddMenuControlOverride(args);

            string name = null;
            if (args != null && args.TryGetValue("name", out var nObj) && nObj != null)
                name = nObj.ToString();

            if (string.IsNullOrEmpty(name))
            {
                return new Dictionary<string, object> { { "error", "Missing required 'name' parameter for menu control." } };
            }

            string type = "Button";
            if (args != null && args.TryGetValue("type", out var tObj) && tObj != null)
                type = tObj.ToString();

            string paramName = null;
            if (args != null && args.TryGetValue("parameter", out var pObj) && pObj != null)
                paramName = pObj.ToString();

            float value = 1f;
            if (args != null && args.TryGetValue("value", out var vObj) && vObj != null)
            {
                if (float.TryParse(vObj.ToString(), out float fv)) value = fv;
            }

            UnityEngine.Object menuAsset = ResolveMenuAsset(args, out string resolveErr);
            if (menuAsset == null)
            {
                return new Dictionary<string, object> { { "error", resolveErr ?? "Expression menu asset not found." } };
            }

            Type menuType = menuAsset.GetType();
            FieldInfo controlsField = menuType.GetField("controls");
            if (controlsField == null)
            {
                return new Dictionary<string, object> { { "error", "Invalid VRCExpressionsMenu: 'controls' field not found." } };
            }

            IList controlsList = controlsField.GetValue(menuAsset) as IList;
            int currentCount = controlsList != null ? controlsList.Count : 0;

            // Task 7.7: Refuse a control addition that would exceed the per-menu control limit
            if (currentCount >= MaxMenuControls)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "refused", true },
                    { "controlCount", currentCount },
                    { "limit", MaxMenuControls },
                    { "error", $"Expression menu already contains the maximum number of controls ({MaxMenuControls}). Addition refused. Menu asset was unchanged." }
                };
            }

            Undo.RecordObject(menuAsset, $"Add Control {name} to Menu");

            Type ctrlType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu+Control");
            if (ctrlType == null)
            {
                return new Dictionary<string, object> { { "error", "VRCExpressionsMenu.Control type not found." } };
            }

            object newCtrl = Activator.CreateInstance(ctrlType);
            ctrlType.GetField("name")?.SetValue(newCtrl, name);

            Type ctrlEnumType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu+Control+ControlType");
            if (ctrlEnumType != null)
            {
                try
                {
                    object ev = Enum.Parse(ctrlEnumType, type, true);
                    ctrlType.GetField("type")?.SetValue(newCtrl, ev);
                }
                catch { }
            }

            ctrlType.GetField("value")?.SetValue(newCtrl, value);

            if (!string.IsNullOrEmpty(paramName))
            {
                Type paramHolderType = FindType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu+Control+Parameter");
                if (paramHolderType != null)
                {
                    object ph = Activator.CreateInstance(paramHolderType);
                    paramHolderType.GetField("name")?.SetValue(ph, paramName);
                    ctrlType.GetField("parameter")?.SetValue(newCtrl, ph);
                }
            }

            // Submenu linking if provided
            if (args != null && args.TryGetValue("subMenuAssetPath", out var smPathObj) && smPathObj != null)
            {
                var subAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(smPathObj.ToString());
                if (subAsset != null)
                {
                    ctrlType.GetField("subMenu")?.SetValue(newCtrl, subAsset);
                }
            }

            if (controlsList != null)
            {
                controlsList.Add(newCtrl);
            }

            EditorUtility.SetDirty(menuAsset);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "refused", false },
                { "controlName", name },
                { "type", type },
                { "parameter", paramName },
                { "value", value },
                { "controlCount", controlsList.Count },
                { "limit", MaxMenuControls }
            };
        }

        private static UnityEngine.Object ResolveMenuAsset(Dictionary<string, object> args, out string error)
        {
            error = null;
            if (args != null && args.TryGetValue("menuAssetPath", out var mObj) && mObj != null)
            {
                string path = mObj.ToString();
                var loaded = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (loaded != null) return loaded;
                error = $"No VRCExpressionsMenu found at path '{path}'.";
                return null;
            }

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out error);
            if (avatar != null)
            {
                Component desc = FindDescriptorComponent(avatar);
                if (desc != null)
                {
                    Type descType = desc.GetType();
                    var mProp = descType.GetProperty("expressionsMenu");
                    var mField = descType.GetField("expressionsMenu");
                    object val = mProp != null ? mProp.GetValue(desc, null) : mField?.GetValue(desc);
                    if (val is UnityEngine.Object uo && uo != null) return uo;
                }
            }

            error = "Could not locate VRCExpressionsMenu. Specify 'menuAssetPath' or an avatar with an assigned menu asset.";
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // 6. PhysBones (vrc/physbone/add, configure, list)
        // ─────────────────────────────────────────────────────────────

        public static object AddPhysBone(Dictionary<string, object> args)
        {
            if (TestAddPhysBoneOverride != null)
                return TestAddPhysBoneOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            if (!TryResolvePathArg(avatar.transform, args, "targetPath", out var target, out err))
                return new Dictionary<string, object> { { "error", err } };
            GameObject targetGo = (target ?? avatar.transform).gameObject;
            if (!TryResolvePathArg(targetGo.transform, args, "rootTransformPath", out var chainRoot, out err))
                return new Dictionary<string, object> { { "error", err } };

            Type pbType = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
            if (pbType == null)
            {
                return new Dictionary<string, object> { { "error", "VRCPhysBone component type not found in loaded assemblies." } };
            }

            Component pb = Undo.AddComponent(targetGo, pbType);

            ApplyPhysBoneProperties(pb, pbType, chainRoot, args);

            Transform root = GetPhysBoneRoot(pb, pbType) ?? targetGo.transform;
            int affectedCount = CountDescendants(root);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectPath", GetRelativePath(avatar.transform, targetGo.transform) },
                { "root", GetRelativePath(avatar.transform, root) },
                { "affectedTransformCount", affectedCount }
            };
        }

        public static object ConfigurePhysBone(Dictionary<string, object> args)
        {
            if (TestConfigurePhysBoneOverride != null)
                return TestConfigurePhysBoneOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            if (!TryResolvePathArg(avatar.transform, args, "targetPath", out var target, out err))
                return new Dictionary<string, object> { { "error", err } };
            GameObject targetGo = (target ?? avatar.transform).gameObject;
            if (!TryResolvePathArg(targetGo.transform, args, "rootTransformPath", out var chainRoot, out err))
                return new Dictionary<string, object> { { "error", err } };

            Component pb = targetGo.GetComponent("VRCPhysBone");
            if (pb == null)
            {
                return new Dictionary<string, object> { { "error", $"No VRCPhysBone found on target '{targetGo.name}'." } };
            }

            Undo.RecordObject(pb, "Configure PhysBone");
            ApplyPhysBoneProperties(pb, pb.GetType(), chainRoot, args);

            Transform root = GetPhysBoneRoot(pb, pb.GetType()) ?? targetGo.transform;
            int affectedCount = CountDescendants(root);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectPath", GetRelativePath(avatar.transform, targetGo.transform) },
                { "root", GetRelativePath(avatar.transform, root) },
                { "affectedTransformCount", affectedCount }
            };
        }

        public static object ListPhysBones(Dictionary<string, object> args)
        {
            if (TestListPhysBonesOverride != null)
                return TestListPhysBonesOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            var results = new List<Dictionary<string, object>>();
            var comps = avatar.GetComponentsInChildren<Component>(true);

            foreach (var c in comps)
            {
                if (c == null) continue;
                if (c.GetType().Name == "VRCPhysBone")
                {
                    Type pbType = c.GetType();
                    Transform root = GetPhysBoneRoot(c, pbType) ?? c.transform;
                    int affectedCount = CountDescendants(root);

                    results.Add(new Dictionary<string, object>
                    {
                        { "objectPath", GetRelativePath(avatar.transform, c.transform) },
                        { "root", GetRelativePath(avatar.transform, root) },
                        { "affectedTransformCount", affectedCount },
                        { "parameters", ReadPhysBoneProperties(c, pbType) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "avatarName", avatar.name },
                { "physBoneCount", results.Count },
                { "physBones", results }
            };
        }

        private static void ApplyPhysBoneProperties(Component pb, Type pbType, Transform chainRoot, Dictionary<string, object> args)
        {
            if (args == null) return;

            // Resolved (and validated) by the caller before anything was mutated.
            if (chainRoot != null) SetFieldOrProp(pb, pbType, "rootTransform", chainRoot);

            // Dynamics properties
            SetNumericProp(pb, pbType, "pull", args);
            SetNumericProp(pb, pbType, "spring", args);
            SetNumericProp(pb, pbType, "damping", args);
            SetNumericProp(pb, pbType, "gravity", args);
            SetNumericProp(pb, pbType, "stiffness", args);
            SetNumericProp(pb, pbType, "immobility", args);
            SetNumericProp(pb, pbType, "radius", args);

            // Boolean properties
            SetBoolProp(pb, pbType, "allowGrabbing", args);
            SetBoolProp(pb, pbType, "allowPosing", args);

            EditorUtility.SetDirty(pb);
        }

        private static Dictionary<string, object> ReadPhysBoneProperties(Component pb, Type pbType)
        {
            return new Dictionary<string, object>
            {
                { "pull", GetNumericProp(pb, pbType, "pull") },
                { "spring", GetNumericProp(pb, pbType, "spring") },
                { "damping", GetNumericProp(pb, pbType, "damping") },
                { "gravity", GetNumericProp(pb, pbType, "gravity") },
                { "stiffness", GetNumericProp(pb, pbType, "stiffness") },
                { "immobility", GetNumericProp(pb, pbType, "immobility") },
                { "radius", GetNumericProp(pb, pbType, "radius") },
                { "allowGrabbing", GetBoolProp(pb, pbType, "allowGrabbing") },
                { "allowPosing", GetBoolProp(pb, pbType, "allowPosing") }
            };
        }

        private static Transform GetPhysBoneRoot(Component pb, Type pbType)
        {
            var p = pbType.GetProperty("rootTransform");
            var f = pbType.GetField("rootTransform");
            if (p?.GetValue(pb, null) is Transform t1 && t1 != null) return t1;
            if (f?.GetValue(pb) is Transform t2 && t2 != null) return t2;
            return pb.transform;
        }

        private static int CountDescendants(Transform root)
        {
            if (root == null) return 0;
            int count = 1;
            for (int i = 0; i < root.childCount; i++)
            {
                count += CountDescendants(root.GetChild(i));
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────
        // 7. Contacts (vrc/contact/add & vrc/contact/list)
        // ─────────────────────────────────────────────────────────────

        public static object AddContact(Dictionary<string, object> args)
        {
            if (TestAddContactOverride != null)
                return TestAddContactOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            if (!TryResolvePathArg(avatar.transform, args, "targetPath", out var target, out err))
                return new Dictionary<string, object> { { "error", err } };
            GameObject targetGo = (target ?? avatar.transform).gameObject;

            string contactType = "receiver";
            if (args != null && args.TryGetValue("type", out var ctObj) && ctObj != null)
                contactType = ctObj.ToString().ToLowerInvariant();

            Type compType = contactType == "sender"
                ? FindType("VRC.SDK3.Dynamics.Contact.Components.VRCContactSender")
                : FindType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver");

            if (compType == null)
            {
                return new Dictionary<string, object> { { "error", $"VRCContact{(contactType == "sender" ? "Sender" : "Receiver")} component type not found." } };
            }

            Component comp = Undo.AddComponent(targetGo, compType);

            // Configure tags
            if (args != null && args.TryGetValue("collisionTags", out var tagsObj) && tagsObj is IList tagList)
            {
                var strList = new List<string>();
                foreach (var t in tagList) if (t != null) strList.Add(t.ToString());
                SetFieldOrProp(comp, compType, "collisionTags", strList);
            }

            // Configure radius / height
            SetNumericProp(comp, compType, "radius", args);
            SetNumericProp(comp, compType, "height", args);

            // For receiver: parameter and receiverType
            if (contactType == "receiver")
            {
                if (args != null && args.TryGetValue("parameter", out var paramObj) && paramObj != null)
                {
                    SetFieldOrProp(comp, compType, "parameter", paramObj.ToString());
                }
            }

            EditorUtility.SetDirty(comp);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "type", contactType },
                { "objectPath", GetRelativePath(avatar.transform, targetGo.transform) }
            };
        }

        public static object ListContacts(Dictionary<string, object> args)
        {
            if (TestListContactsOverride != null)
                return TestListContactsOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            var results = new List<Dictionary<string, object>>();
            var comps = avatar.GetComponentsInChildren<Component>(true);

            foreach (var c in comps)
            {
                if (c == null) continue;
                string typeName = c.GetType().Name;
                if (typeName == "VRCContactSender" || typeName == "VRCContactReceiver")
                {
                    Type ct = c.GetType();
                    string kind = typeName == "VRCContactSender" ? "sender" : "receiver";

                    var tags = new List<string>();
                    var pTags = ct.GetProperty("collisionTags")?.GetValue(c, null) as IList
                             ?? ct.GetField("collisionTags")?.GetValue(c) as IList;
                    if (pTags != null)
                    {
                        foreach (var tag in pTags) if (tag != null) tags.Add(tag.ToString());
                    }

                    string param = null;
                    if (kind == "receiver")
                    {
                        param = ct.GetProperty("parameter")?.GetValue(c, null)?.ToString()
                             ?? ct.GetField("parameter")?.GetValue(c)?.ToString();
                    }

                    results.Add(new Dictionary<string, object>
                    {
                        { "type", kind },
                        { "objectPath", GetRelativePath(avatar.transform, c.transform) },
                        { "radius", GetNumericProp(c, ct, "radius") },
                        { "collisionTags", tags },
                        { "parameter", param }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "avatarName", avatar.name },
                { "contactCount", results.Count },
                { "contacts", results }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 8. Non-Destructive Authoring (Modular Avatar & VRCFury)
        // ─────────────────────────────────────────────────────────────

        public static object ListNonDestructive(Dictionary<string, object> args)
        {
            if (TestListNonDestructiveOverride != null)
                return TestListNonDestructiveOverride(args);

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            // includeDetails adds each component's configuration (for VRCFury, the feature inside it).
            bool includeDetails = args != null && args.TryGetValue("includeDetails", out var detailsArg)
                                  && MCPVRChatUtil.TryReadBool(detailsArg, out bool detailsWanted) && detailsWanted;

            var results = new List<Dictionary<string, object>>();
            var comps = avatar.GetComponentsInChildren<Component>(true);

            foreach (var c in comps)
            {
                if (c == null) continue;
                Type t = c.GetType();
                string fullName = t.FullName ?? "";
                string typeName = t.Name;

                // Check Modular Avatar
                if (fullName.IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fullName.IndexOf("modularavatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.StartsWith("ModularAvatar", StringComparison.OrdinalIgnoreCase))
                {
                    string role = DeriveModularAvatarRole(c, typeName);
                    var entry = new Dictionary<string, object>
                    {
                        { "objectPath", GetRelativePath(avatar.transform, c.transform) },
                        { "tool", "Modular Avatar" },
                        { "componentType", typeName },
                        { "role", role }
                    };
                    if (includeDetails) entry["details"] = MCPVRChatUtil.DumpComponentFields(c, avatar.transform);
                    results.Add(entry);
                }
                // Check VRCFury
                else if (fullName.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.StartsWith("VRCFury", StringComparison.OrdinalIgnoreCase))
                {
                    string role = DeriveVRCFuryRole(c);
                    var entry = new Dictionary<string, object>
                    {
                        { "objectPath", GetRelativePath(avatar.transform, c.transform) },
                        { "tool", "VRCFury" },
                        { "componentType", typeName },
                        { "role", role }
                    };
                    if (includeDetails)
                    {
                        object feature = MCPVRChatUtil.GetFieldValue(c, "content");
                        entry["details"] = feature != null
                            ? MCPVRChatUtil.DumpManaged(feature, avatar.transform)
                            : MCPVRChatUtil.DumpComponentFields(c, avatar.transform);
                    }
                    results.Add(entry);
                }
            }

            return new Dictionary<string, object>
            {
                { "avatarName", avatar.name },
                { "totalCount", results.Count },
                { "components", results }
            };
        }

        private static string DeriveModularAvatarRole(Component c, string typeName)
        {
            switch (typeName)
            {
                case "ModularAvatarMergeAnimator":
                    return "Merge Animator (merge into avatar FX/Gesture layer)";
                case "ModularAvatarParameters":
                    return "Synced Parameters definition";
                case "ModularAvatarMenuInstaller":
                    return "Menu Installer (injects menu items into avatar root menu)";
                case "ModularAvatarBoneProxy":
                    return "Bone Proxy (attaches bones to avatar armature)";
                case "ModularAvatarMergeArmature":
                    return "Merge Armature";
                default:
                    return $"Modular Avatar: {typeName}";
            }
        }

        private static string DeriveVRCFuryRole(Component c)
        {
            Type t = c.GetType();

            // Current VRCFury: one feature per component, in its [SerializeReference] content.
            object content = MCPVRChatUtil.GetFieldValue(c, "content");
            if (content != null)
            {
                string featureName = content.GetType().Name;
                if (featureName == "Toggle" && MCPVRChatUtil.GetFieldValue(content, "name") is string menu && menu.Length > 0)
                    return $"VRCFury Feature: Toggle ('{menu}')";
                return $"VRCFury Feature: {featureName}";
            }

            // Legacy components carry a list of features or a component type
            var prop = t.GetProperty("features") ?? t.GetProperty("config");
            var field = t.GetField("features") ?? t.GetField("config");
            object val = prop != null ? prop.GetValue(c, null) : field?.GetValue(c);
            if (val is IList list && list.Count > 0)
            {
                var featureNames = new List<string>();
                foreach (var f in list)
                {
                    if (f != null) featureNames.Add(f.GetType().Name);
                }
                return $"VRCFury Features: {string.Join(", ", featureNames)}";
            }
            return "VRCFury Feature";
        }

        public static object AddModularAvatarComponent(Dictionary<string, object> args)
        {
            // Task 7.10: Check if package is installed
            bool isInstalled = CheckModularAvatarInstalled(out string version);
            if (!isInstalled)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "Modular Avatar is not installed in the project (package 'nadena.dev.modular-avatar' missing)." }
                };
            }

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            if (!TryResolvePathArg(avatar.transform, args, "targetPath", out var target, out err))
                return new Dictionary<string, object> { { "error", err } };
            GameObject targetGo = (target ?? avatar.transform).gameObject;

            string compTypeStr = "ModularAvatarMergeAnimator";
            if (args != null && args.TryGetValue("componentType", out var ctObj) && ctObj != null)
                compTypeStr = ctObj.ToString();

            Type compType = FindType($"nadena.dev.modular_avatar.core.{compTypeStr}")
                         ?? FindType($"nadena.dev.modular_avatar.{compTypeStr}")
                         ?? FindType(compTypeStr);

            if (compType == null)
            {
                return new Dictionary<string, object> { { "error", $"Modular Avatar component type '{compTypeStr}' not found." } };
            }

            Component comp = Undo.AddComponent(targetGo, compType);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "componentType", compTypeStr },
                { "objectPath", GetRelativePath(avatar.transform, targetGo.transform) }
            };
        }

        public static object AddVRCFuryComponent(Dictionary<string, object> args)
        {
            // Task 7.10: Check if package is installed
            bool isInstalled = CheckVRCFuryInstalled(out string version);
            if (!isInstalled)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "VRCFury is not installed in the project (package 'com.vrcfury.vrcfury' missing)." }
                };
            }

            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null)
            {
                return new Dictionary<string, object> { { "error", err ?? "Avatar not found." } };
            }

            if (!TryResolvePathArg(avatar.transform, args, "targetPath", out var target, out err))
                return new Dictionary<string, object> { { "error", err } };
            GameObject targetGo = (target ?? avatar.transform).gameObject;

            // A VRCFury component carries exactly one feature in its [SerializeReference] `content`;
            // one without a feature does nothing and VRCFury treats it as corrupt. So the feature
            // is required, and is built the way VRCFury's own Add Component menu builds it.
            Type compType = FindType("VF.Model.VRCFury");
            Type featureBase = FindType("VF.Model.Feature.FeatureModel");
            if (compType == null || featureBase == null)
            {
                return new Dictionary<string, object> { { "error", "VRCFury types (VF.Model.VRCFury / VF.Model.Feature.FeatureModel) not found." } };
            }

            string featureName = args != null && args.TryGetValue("feature", out var fObj) && fObj != null ? fObj.ToString() : null;
            Type featureType = string.IsNullOrEmpty(featureName) ? null : FindType("VF.Model.Feature." + featureName);
            if (featureType == null || featureType.IsAbstract || !featureBase.IsAssignableFrom(featureType))
            {
                var valid = featureBase.Assembly.GetTypes()
                    .Where(t => !t.IsAbstract && featureBase.IsAssignableFrom(t) && t.GetCustomAttribute<ObsoleteAttribute>() == null)
                    .Select(t => t.Name).OrderBy(n => n);
                return new Dictionary<string, object>
                {
                    { "error", $"Unknown VRCFury feature '{featureName}'. Valid features: {string.Join(", ", valid)}." }
                };
            }

            Component comp = Undo.AddComponent(targetGo, compType);
            var so = new SerializedObject(comp);
            so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(featureType, true);
            so.ApplyModifiedPropertiesWithoutUndo();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "componentType", "VRCFury" },
                { "feature", featureType.Name },
                { "objectPath", GetRelativePath(avatar.transform, targetGo.transform) }
            };
        }

        public static bool CheckModularAvatarInstalled(out string version)
        {
            version = null;
            if (TestModularAvatarInstalledOverride.HasValue)
            {
                if (!TestModularAvatarInstalledOverride.Value) return false;
                version = "1.19.0";
                return true;
            }

            return MCPVRChatDetect.DetectPackage(MCPVRChatDetect.GetProjectPath(), "nadena.dev.modular-avatar", "nadena.dev.modular-avatar.core", out version);
        }

        public static bool CheckVRCFuryInstalled(out string version)
        {
            version = null;
            if (TestVRCFuryInstalledOverride.HasValue)
            {
                if (!TestVRCFuryInstalledOverride.Value) return false;
                version = "1.1429.0";
                return true;
            }

            return MCPVRChatDetect.DetectPackage(MCPVRChatDetect.GetProjectPath(), "com.vrcfury.vrcfury", "VRCFury", out version);
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve args[key] as a path under <paramref name="root"/>. Absent key → true with a null
        /// result (the caller picks its default). Present but not found → false with an error: a
        /// typo must never silently retarget the edit onto the default object.
        /// </summary>
        private static bool TryResolvePathArg(Transform root, Dictionary<string, object> args, string key,
            out Transform result, out string error)
        {
            result = null;
            error = null;
            if (args == null || !args.TryGetValue(key, out var raw) || raw == null || raw.ToString() == "")
                return true;

            result = root.Find(raw.ToString());
            if (result != null) return true;

            error = $"{key} '{raw}' was not found under '{root.name}'. Paths are relative to it, e.g. 'Armature/Hips/Spine'.";
            return false;
        }

        public static Component FindDescriptorComponent(GameObject avatar)
        {
            if (avatar == null) return null;
            foreach (var c in avatar.GetComponents<Component>())
            {
                if (c != null && (c.GetType().Name == "VRCAvatarDescriptor" || c.GetType().Name == "VRC_AvatarDescriptor"))
                    return c;
            }
            return null;
        }

        public static string GetRelativePath(Transform root, Transform target)
        {
            if (target == null || root == null) return "";
            if (target == root) return target.name;
            var parts = new List<string>();
            Transform curr = target;
            while (curr != null && curr != root)
            {
                parts.Add(curr.name);
                curr = curr.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void SetNumericProp(Component comp, Type type, string name, Dictionary<string, object> args)
        {
            if (args != null && args.TryGetValue(name, out var val) && val != null)
            {
                if (float.TryParse(val.ToString(), out float f))
                {
                    SetFieldOrProp(comp, type, name, f);
                }
            }
        }

        private static void SetBoolProp(Component comp, Type type, string name, Dictionary<string, object> args)
        {
            if (args != null && args.TryGetValue(name, out var val) && val != null)
            {
                if (bool.TryParse(val.ToString(), out bool b))
                {
                    SetFieldOrProp(comp, type, name, b);
                }
            }
        }

        private static float GetNumericProp(Component comp, Type type, string name)
        {
            var p = type.GetProperty(name);
            var f = type.GetField(name);
            object v = p?.GetValue(comp, null) ?? f?.GetValue(comp);
            if (v != null && float.TryParse(v.ToString(), out float res)) return res;
            return 0f;
        }

        private static bool GetBoolProp(Component comp, Type type, string name)
        {
            var p = type.GetProperty(name);
            var f = type.GetField(name);
            object v = p?.GetValue(comp, null) ?? f?.GetValue(comp);
            if (v is bool b) return b;
            return false;
        }

        private static void SetFieldOrProp(Component comp, Type type, string name, object val)
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite)
            {
                p.SetValue(comp, val, null);
                return;
            }
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(comp, val);
            }
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
