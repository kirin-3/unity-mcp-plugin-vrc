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
    /// vrc/avatar/outfit/attach: put a clothing prefab (or scene object) under the avatar and merge its
    /// armature with Modular Avatar (Setup Outfit → Merge Armature) or VRCFury (Armature Link), then
    /// report the bones that did not match. Prefix/suffix naming differences are the usual failure, so
    /// unmatched bones that look like avatar bones are called out with a suggested fix.
    ///
    /// Also hosts the bone-matching helpers shared with vrc/avatar/vrcfury/armature-link. They mirror the
    /// matching rules of the tools themselves (VRCFury ArmatureLinkService.GetLinks, MA
    /// ModularAvatarMergeArmature.GetBonesMapping) so the report predicts what the build will do.
    /// </summary>
    public static class MCPVRChatOutfitCommands
    {
        private const string MergeArmatureType = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        private const string AvatarObjectReferenceType = "nadena.dev.modular_avatar.core.AvatarObjectReference";
        private const string SetupOutfitType = "nadena.dev.modular_avatar.core.editor.SetupOutfit";
        private const string SetupOutfitErrorWindowType = "nadena.dev.modular_avatar.core.editor.ESOErrorWindow";
        private const string HaveObjReferencesType = "nadena.dev.modular_avatar.core.IHaveObjReferences";

        private const int MaxReportedBones = 30;

        public static object Attach(Dictionary<string, object> args)
        {
            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");
            Transform root = avatar.transform;

            var animator = avatar.GetComponent<Animator>();
            Transform avatarHips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (avatarHips == null)
                return MCPVRChatUtil.Fail($"'{avatar.name}' has no humanoid Hips bone (its Animator needs a Humanoid rig); Modular Avatar and VRCFury both merge clothing onto it.");

            string outfitPath = MCPVRChatUtil.GetString(args, "outfitPath")?.Trim();
            if (string.IsNullOrEmpty(outfitPath))
                return MCPVRChatUtil.Fail("'outfitPath' is required: a prefab asset path (e.g. 'Assets/Outfits/Hoodie.prefab') or a GameObject in the scene.");

            if (!MCPVRChatUtil.TryReadOptionalBool(args, "resetTransform", out bool? resetTransform, out err))
                return MCPVRChatUtil.Fail(err);

            // ── Resolve the outfit source; nothing is changed until everything below validates ──
            GameObject asset = null;
            GameObject sceneOutfit = null;
            if (LooksLikeAssetPath(outfitPath))
            {
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(outfitPath);
                if (asset == null)
                    return MCPVRChatUtil.Fail($"No prefab or model asset at '{outfitPath}'.");
            }
            else
            {
                sceneOutfit = MCPVRChatUtil.FindUnder(root, outfitPath)?.gameObject
                              ?? MCPVRChatWorldCommands.ResolveGameObject(outfitPath);
                if (sceneOutfit == null)
                    return MCPVRChatUtil.Fail($"Outfit '{outfitPath}' was not found in the scene. Pass a scene object path or a prefab asset path.");
                if (sceneOutfit == avatar)
                    return MCPVRChatUtil.Fail("The outfit is the avatar itself.");
                if (root.IsChildOf(sceneOutfit.transform))
                    return MCPVRChatUtil.Fail($"The avatar is inside '{sceneOutfit.name}'; pass the clothing object, not one of the avatar's parents.");
            }

            GameObject source = asset != null ? asset : sceneOutfit;
            if (source.GetComponentsInChildren<Component>(true).Any(c => c != null && IsAvatarDescriptor(c)))
            {
                return MCPVRChatUtil.Fail(
                    $"'{source.name}' carries its own VRCAvatarDescriptor. Nesting avatars breaks both Modular Avatar and VRCFury; " +
                    "use the clothing-only prefab or remove the descriptor from the outfit first.");
            }
            if (root.parent != null && root.parent.GetComponentsInParent<Component>(true).Any(IsAvatarDescriptor))
                return MCPVRChatUtil.Fail($"'{avatar.name}' sits inside another avatar; attach the outfit to the outermost avatar.");

            Transform sourceHips = FindOutfitHips(source.transform, avatarHips, root);
            if (sourceHips == null)
            {
                return MCPVRChatUtil.Fail(
                    $"Could not find the outfit's hips bone in '{source.name}'. Expected something like '<outfit>/Armature/{avatarHips.name}' " +
                    "(a bone whose name contains the avatar's hips name, under an armature object).");
            }

            // ── Pick the merge tool ──
            bool hasMA = MCPVRChatAuthoringCommands.CheckModularAvatarInstalled(out _);
            bool hasVF = MCPVRChatAuthoringCommands.CheckVRCFuryInstalled(out _);
            Type mergeType = MCPVRChatUtil.FindType(MergeArmatureType);
            Type vfType = MCPVRChatUtil.FindType(MCPVRChatVRCFuryCommands.VRCFuryComponentType);
            Type linkType = MCPVRChatUtil.FindType(MCPVRChatVRCFuryCommands.ArmatureLinkType);

            bool outfitHasMerge = mergeType != null && source.GetComponentsInChildren(mergeType, true).Length > 0;
            bool outfitHasLink = vfType != null && linkType != null
                                 && MCPVRChatVRCFuryCommands.FindFeatures(source.transform, vfType, linkType).Count > 0;

            string requested = (MCPVRChatUtil.GetString(args, "method") ?? "auto").Trim().ToLowerInvariant();
            string method;
            string reason;
            switch (requested)
            {
                case "auto":
                    if (outfitHasMerge && hasMA) { method = "modularAvatar"; reason = "the outfit already carries a Modular Avatar Merge Armature"; }
                    else if (outfitHasLink && hasVF) { method = "vrcfury"; reason = "the outfit already carries a VRCFury Armature Link"; }
                    else if (hasMA && !hasVF) { method = "modularAvatar"; reason = "Modular Avatar is installed (VRCFury is not)"; }
                    else if (hasVF && !hasMA) { method = "vrcfury"; reason = "VRCFury is installed (Modular Avatar is not)"; }
                    else if (hasMA)
                    {
                        int maUses = mergeType != null ? root.GetComponentsInChildren(mergeType, true).Length : 0;
                        int vfUses = vfType != null && linkType != null ? MCPVRChatVRCFuryCommands.FindFeatures(root, vfType, linkType).Count : 0;
                        if (vfUses > maUses)
                        {
                            method = "vrcfury";
                            reason = $"both are installed and the avatar already links {vfUses} item(s) with VRCFury Armature Link";
                        }
                        else
                        {
                            method = "modularAvatar";
                            reason = maUses > 0
                                ? $"both are installed and the avatar already merges {maUses} item(s) with Modular Avatar"
                                : "both are installed; Modular Avatar Setup Outfit is the default";
                        }
                    }
                    else
                    {
                        return MCPVRChatUtil.Fail("Neither Modular Avatar (nadena.dev.modular-avatar) nor VRCFury (com.vrcfury.vrcfury) is installed; one of them is needed to merge the outfit's armature.");
                    }
                    break;
                case "modularavatar": case "modular-avatar": case "ma":
                    if (!hasMA) return MCPVRChatUtil.Fail("Modular Avatar is not installed in the project (package 'nadena.dev.modular-avatar' missing).");
                    method = "modularAvatar"; reason = "requested";
                    break;
                case "vrcfury": case "vf":
                    if (!hasVF) return MCPVRChatUtil.Fail("VRCFury is not installed in the project (package 'com.vrcfury.vrcfury' missing).");
                    method = "vrcfury"; reason = "requested";
                    break;
                default:
                    return MCPVRChatUtil.Fail($"method '{requested}' must be 'auto', 'modularAvatar' or 'vrcfury'.");
            }

            // Merging one armature with both tools applies it twice.
            if (method == "modularAvatar" && outfitHasLink)
                return MCPVRChatUtil.Fail("The outfit already has a VRCFury Armature Link; attaching it with Modular Avatar as well would merge it twice. Use method 'vrcfury' or remove the link.");
            if (method == "vrcfury" && outfitHasMerge)
                return MCPVRChatUtil.Fail("The outfit already has a Modular Avatar Merge Armature; linking it with VRCFury as well would merge it twice. Use method 'modularAvatar' or remove the Merge Armature.");
            if (method == "modularAvatar")
            {
                if (MCPVRChatAuthoringCommands.FindDescriptorComponent(avatar) == null)
                    return MCPVRChatUtil.Fail($"'{avatar.name}' has no VRCAvatarDescriptor; Modular Avatar only merges into a VRChat avatar.");
                if (sourceHips.parent == null || sourceHips.parent == source.transform)
                    return MCPVRChatUtil.Fail($"The outfit's hips ('{sourceHips.name}') sit directly under the outfit root; Modular Avatar merges an armature object, so it needs '<outfit>/Armature/Hips'. Use method 'vrcfury' or add the armature object.");
            }

            // ── Mutate: every change below is undone as one step, and rolled back if merging fails ──
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            var warnings = new List<string>();
            GameObject outfit;
            bool reparented = false;
            if (asset != null)
            {
                outfit = PrefabUtility.InstantiatePrefab(asset, root) as GameObject;
                if (outfit == null)
                    return MCPVRChatUtil.Fail($"'{outfitPath}' could not be instantiated as a prefab.");
                Undo.RegisterCreatedObjectUndo(outfit, "Attach Outfit");
                if (resetTransform ?? true)
                {
                    outfit.transform.localPosition = Vector3.zero;
                    outfit.transform.localRotation = Quaternion.identity;
                }
            }
            else
            {
                outfit = sceneOutfit;
                if (!outfit.transform.IsChildOf(root))
                {
                    Undo.SetTransformParent(outfit.transform, root, "Attach Outfit");
                    reparented = true;
                }
                if (resetTransform == true)
                {
                    Undo.RecordObject(outfit.transform, "Attach Outfit");
                    outfit.transform.localPosition = Vector3.zero;
                    outfit.transform.localRotation = Quaternion.identity;
                }
                else if (outfit.transform.parent == root && outfit.transform.localPosition.magnitude > 0.01f)
                {
                    var p = outfit.transform.localPosition;
                    warnings.Add($"The outfit is offset ({p.x:0.###}, {p.y:0.###}, {p.z:0.###}) from the avatar root; pass resetTransform:true if it should line up with the avatar.");
                }
            }

            Transform outfitHips = asset != null
                ? outfit.transform.Find(MCPVRChatUtil.RelPath(source.transform, sourceHips))
                : sourceHips;
            if (outfitHips == null)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return MCPVRChatUtil.Fail("The outfit's hips bone could not be located on the instantiated prefab.");
            }

            Dictionary<string, object> merged;
            try
            {
                merged = method == "modularAvatar"
                    ? MergeWithModularAvatar(root, outfit.transform, outfitHips, avatarHips, mergeType)
                    : MCPVRChatVRCFuryCommands.ApplyArmatureLink(root, outfit.transform,
                        MCPVRChatUtil.RelPath(outfit.transform, outfitHips), "Hips", null, null, null);
            }
            catch (Exception ex)
            {
                merged = MCPVRChatUtil.Fail($"{(method == "modularAvatar" ? "Modular Avatar" : "VRCFury")} setup failed: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}");
            }

            if (!(merged.TryGetValue("success", out var ok) && ok is bool b && b))
            {
                // Fail closed: no half-attached outfit is left behind.
                Undo.RevertAllDownToGroup(undoGroup);
                merged["rolledBack"] = true;
                return merged;
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "method", method },
                { "methodReason", reason },
                { "outfitObjectPath", MCPVRChatAuthoringCommands.GetRelativePath(root, outfit.transform) },
                { "instantiatedFrom", asset != null ? AssetDatabase.GetAssetPath(asset) : null },
                { "reparented", reparented },
                { "outfitHips", MCPVRChatUtil.RelPath(root, outfitHips) }
            };
            foreach (var kv in merged)
            {
                if (kv.Key == "success" || kv.Key == "objectPath") continue;
                result[kv.Key] = kv.Value;
            }
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // Modular Avatar
        // ─────────────────────────────────────────────────────────────

        private static Dictionary<string, object> MergeWithModularAvatar(Transform root, Transform outfit, Transform outfitHips,
            Transform avatarHips, Type mergeType)
        {
            if (mergeType == null)
                return MCPVRChatUtil.Fail("Modular Avatar's ModularAvatarMergeArmature type was not found; this Modular Avatar version is not one this route knows how to drive.");

            Transform outfitArmature = outfitHips.parent;
            var namesBefore = outfit.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => t.name);
            var componentsBefore = new HashSet<Component>(outfit.GetComponents<Component>());

            // Setup Outfit is what users run by hand: Merge Armature with inferred prefix/suffix, heuristic
            // bone renames, Mesh Settings and the A-pose fix. It reports failures in a modal window, which
            // would block an unattended editor. So it runs only when that window can be suppressed, or when
            // its own hips search is certain to succeed (the other checks it makes were done in Attach).
            MethodInfo setupOutfit = MCPVRChatUtil.FindType(SetupOutfitType)?.GetMethod("SetupOutfitUI",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            FieldInfo suppress = MCPVRChatUtil.FindType(SetupOutfitErrorWindowType)?.GetField("Suppress",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var notes = new List<string>();
            bool usedSetupOutfit = false;
            if (setupOutfit != null && (suppress != null || SetupOutfitFindsHips(outfit, outfitHips, avatarHips)))
            {
                object previous = suppress?.GetValue(null);
                try
                {
                    suppress?.SetValue(null, true);
                    setupOutfit.Invoke(null, new object[] { outfit.gameObject });
                }
                finally
                {
                    suppress?.SetValue(null, previous is bool p && p);
                }
                usedSetupOutfit = outfitArmature.GetComponent(mergeType) != null;
                if (!usedSetupOutfit)
                    notes.Add("Modular Avatar's Setup Outfit declined this outfit (see the Unity console); a Merge Armature was added directly instead.");
            }

            Component merge = outfitArmature.GetComponent(mergeType);
            if (merge == null)
            {
                if (!TryAddMergeArmature(root, outfitArmature, avatarHips.parent, mergeType, out merge, out string addError))
                    return MCPVRChatUtil.Fail(addError);
            }
            FillObjectReferences(outfit);

            var renamed = new List<string>();
            foreach (var kv in namesBefore)
            {
                if (kv.Key != null && kv.Key.name != kv.Value)
                {
                    if (renamed.Count >= MaxReportedBones) break;
                    renamed.Add($"{kv.Value} → {kv.Key.name}");
                }
            }

            var added = outfit.GetComponents<Component>()
                .Where(c => c != null && !componentsBefore.Contains(c))
                .Select(c => c.GetType().Name).ToList();

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "component", "ModularAvatarMergeArmature" },
                { "componentPath", MCPVRChatUtil.RelPath(root, merge.transform) },
                { "usedSetupOutfit", usedSetupOutfit },
                { "prefix", MCPVRChatUtil.GetFieldValue(merge, "prefix") as string ?? "" },
                { "suffix", MCPVRChatUtil.GetFieldValue(merge, "suffix") as string ?? "" },
                { "boneMatch", BuildMergeArmatureReport(root, merge, outfit) }
            };
            if (renamed.Count > 0) result["renamedBones"] = renamed;
            if (added.Count > 0) result["addedToOutfitRoot"] = added;
            if (notes.Count > 0) result["notes"] = notes;
            return result;
        }

        /// <summary>
        /// Whether Modular Avatar's own hips search (SetupOutfit.FindBones) finds the same hips: the outfit's
        /// humanoid rig, or a bone two or three levels down whose name contains the avatar's hips name.
        /// </summary>
        private static bool SetupOutfitFindsHips(Transform outfit, Transform outfitHips, Transform avatarHips)
        {
            var outfitAnimator = outfit.GetComponent<Animator>();
            if (outfitAnimator != null && outfitAnimator.isHuman)
            {
                Transform rigHips = null;
                try { rigHips = outfitAnimator.GetBoneTransform(HumanBodyBones.Hips); }
                catch { rigHips = null; }
                if (rigHips != null && rigHips.parent != outfit) return rigHips == outfitHips;
            }

            int depth = 0;
            for (var t = outfitHips; t != null && t != outfit; t = t.parent) depth++;
            return (depth == 2 || depth == 3) && outfitHips.name.Contains(avatarHips.name);
        }

        /// <summary>
        /// Modular Avatar resolves each AvatarObjectReference's object on a later editor frame
        /// (ObjectReferenceFixer) and records that as an undo step of its own, so a single Ctrl+Z after the
        /// attach only reverted that. Resolving them now keeps them in the attach's undo step, and the fixer
        /// then finds nothing to change.
        /// </summary>
        private static void FillObjectReferences(Transform outfit)
        {
            Type withReferences = MCPVRChatUtil.FindType(HaveObjReferencesType);
            MethodInfo getReferences = withReferences?.GetMethod("GetObjectReferences");
            if (getReferences == null) return;

            foreach (var component in outfit.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !withReferences.IsInstanceOfType(component)) continue;
                if (!(getReferences.Invoke(component, null) is IEnumerable references)) continue;

                bool recorded = false;
                foreach (var reference in references)
                {
                    if (reference == null) continue;
                    if (MCPVRChatUtil.GetFieldValue(reference, "targetObject") is GameObject existing && existing != null) continue;
                    var type = reference.GetType();
                    var target = type.GetMethod("Get", new[] { typeof(Component) })?.Invoke(reference, new object[] { component }) as GameObject;
                    MethodInfo set = type.GetMethod("Set", new[] { typeof(GameObject) });
                    if (target == null || set == null) continue;
                    if (!recorded)
                    {
                        Undo.RecordObject(component, "Attach Outfit");
                        recorded = true;
                    }
                    set.Invoke(reference, new object[] { target });
                }
                if (recorded) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
        }

        /// <summary>Fallback for Modular Avatar versions without the SetupOutfit API.</summary>
        private static bool TryAddMergeArmature(Transform root, Transform outfitArmature, Transform avatarArmature, Type mergeType,
            out Component merge, out string error)
        {
            merge = null;
            error = null;
            Type referenceType = MCPVRChatUtil.FindType(AvatarObjectReferenceType);
            if (referenceType == null || MCPVRChatUtil.GetField(mergeType, "mergeTarget") == null)
            {
                error = "This Modular Avatar version has neither the SetupOutfit API nor the AvatarObjectReference merge target this route knows how to set.";
                return false;
            }

            merge = Undo.AddComponent(outfitArmature.gameObject, mergeType);
            object reference = Activator.CreateInstance(referenceType);
            string referencePath = MCPVRChatUtil.RelPath(root, avatarArmature);
            if (string.IsNullOrEmpty(referencePath))
                referencePath = referenceType.GetField("AVATAR_ROOT", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "";
            MCPVRChatUtil.SetFieldValue(reference, "referencePath", referencePath);
            MCPVRChatUtil.SetFieldValue(merge, "mergeTarget", reference);

            FieldInfo lockField = MCPVRChatUtil.GetField(mergeType, "LockMode");
            if (lockField != null && lockField.FieldType.IsEnum && Enum.IsDefined(lockField.FieldType, "BaseToMerge"))
                lockField.SetValue(merge, Enum.Parse(lockField.FieldType, "BaseToMerge"));

            mergeType.GetMethod("InferPrefixSuffix", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(merge, null);
            EditorUtility.SetDirty(merge);
            return true;
        }

        /// <summary>Bones Merge Armature will and won't merge, from its own GetBonesMapping().</summary>
        internal static Dictionary<string, object> BuildMergeArmatureReport(Transform root, Component merge, Transform outfit)
        {
            var matched = new HashSet<Transform> { merge.transform };
            if (merge.GetType().GetMethod("GetBonesMapping", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                    ?.Invoke(merge, null) is IEnumerable mapping)
            {
                foreach (var pair in mapping)
                {
                    // (Transform baseBone, Transform mergeBone) value tuples
                    if (pair?.GetType().GetField("Item2")?.GetValue(pair) is Transform mergeSide && mergeSide != null)
                        matched.Add(mergeSide);
                }
            }

            string prefix = MCPVRChatUtil.GetFieldValue(merge, "prefix") as string ?? "";
            string suffix = MCPVRChatUtil.GetFieldValue(merge, "suffix") as string ?? "";
            Transform mergeTarget = (merge.GetType().GetProperty("mergeTargetObject")?.GetValue(merge, null) as GameObject)?.transform;
            var avatarBones = mergeTarget != null ? mergeTarget.GetComponentsInChildren<Transform>(true) : new Transform[0];

            string Strip(string name)
            {
                if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal)) name = name.Substring(prefix.Length);
                if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal)) name = name.Substring(0, name.Length - suffix.Length);
                return name;
            }

            var report = BuildMatchReport(root, merge.transform, matched, avatarBones, Strip, "prefix/suffix on the Merge Armature",
                t => t != merge.transform && t.GetComponent(merge.GetType()) != null);

            // Humanoid bones of a rigged outfit that failed to merge are the clearest sign of a naming problem.
            var outfitAnimator = outfit.GetComponent<Animator>();
            if (outfitAnimator != null && outfitAnimator.isHuman)
            {
                var missed = new List<string>();
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    Transform t;
                    try { t = outfitAnimator.GetBoneTransform(bone); }
                    catch { continue; }
                    if (t != null && t.IsChildOf(merge.transform) && !matched.Contains(t))
                        missed.Add($"{bone} ({MCPVRChatUtil.RelPath(root, t)})");
                }
                if (missed.Count > 0) report["unmatchedHumanoidBones"] = missed;
            }
            return report;
        }

        // ─────────────────────────────────────────────────────────────
        // VRCFury matching (mirrors ArmatureLinkService.GetLinks)
        // ─────────────────────────────────────────────────────────────

        private static readonly string[] VRCFuryMidBoneHacks = { "ChestUp", "TopFut_L", "TopFut_R", "HeadGRP" };

        internal static Dictionary<string, object> BuildVRCFuryLinkReport(Transform root, Transform propBone, Transform avatarBone,
            string removeBoneSuffix, bool recursive)
        {
            // An empty setting makes VRCFury predict the suffix from the root bone names.
            string suffix = removeBoneSuffix;
            bool predicted = false;
            if (string.IsNullOrWhiteSpace(suffix) && propBone.name.Contains(avatarBone.name) && propBone.name != avatarBone.name)
            {
                suffix = propBone.name.Replace(avatarBone.name, "");
                predicted = true;
            }

            if (!recursive)
            {
                return new Dictionary<string, object>
                {
                    { "matchedCount", 1 },
                    { "unmatchedCount", 0 },
                    { "unmatched", new List<object>() },
                    { "effectiveSuffix", suffix ?? "" },
                    { "note", "recursive is off: the Link From object is attached to the target bone and its children move with it; nothing below it is merged by name." }
                };
            }

            var matched = new HashSet<Transform> { propBone };
            var stack = new Stack<(Transform prop, Transform avatar)>();
            stack.Push((propBone, avatarBone));
            while (stack.Count > 0)
            {
                var (propParent, avatarParent) = stack.Pop();
                foreach (Transform child in propParent)
                {
                    string search = string.IsNullOrWhiteSpace(suffix) ? child.name : child.name.Replace(suffix, "");
                    Transform match = avatarParent.Find(search);

                    // VRCFury's workarounds for mid-bones that exist on only one side (Rexouium ChestUp
                    // etc.). A clothing-only mid-bone is passed through to the same avatar bone; it is
                    // not linked itself but its children are, so it counts as handled.
                    foreach (var hack in VRCFuryMidBoneHacks)
                    {
                        if (match != null) break;
                        if (child.name == hack) { match = avatarParent; break; }
                        match = avatarParent.Find(hack + "/" + search);
                        if (match != null) break;
                        if (avatarParent.name == hack && avatarParent.parent != null)
                        {
                            match = avatarParent.parent.Find(search);
                            if (match != null) break;
                        }
                    }

                    if (match == null) continue;
                    matched.Add(child);
                    stack.Push((child, match));
                }
            }

            string suffixForStrip = suffix;
            var report = BuildMatchReport(root, propBone, matched, avatarBone.GetComponentsInChildren<Transform>(true),
                name => string.IsNullOrWhiteSpace(suffixForStrip) ? name : name.Replace(suffixForStrip, ""),
                "removeBoneSuffix on the Armature Link", null);
            report["effectiveSuffix"] = suffix ?? "";
            if (predicted) report["suffixPredicted"] = true;
            return report;
        }

        // ─────────────────────────────────────────────────────────────
        // Report
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Walk the prop/outfit side: an "unmatched" bone is the first unmatched bone under a matched parent
        /// (its whole subtree stays unmerged, which is expected for skirt or hair chains). Unmatched bones whose
        /// names look like avatar bones point at a naming problem, and a common prefix/suffix among them is
        /// offered as the fix.
        /// </summary>
        private static Dictionary<string, object> BuildMatchReport(Transform root, Transform scanRoot, HashSet<Transform> matched,
            IEnumerable<Transform> avatarBones, Func<string, string> stripConfiguredAffix, string affixSettingName,
            Func<Transform, bool> skipSubtree)
        {
            var avatarNames = new Dictionary<string, string>();
            foreach (var bone in avatarBones)
            {
                if (bone == null) continue;
                string n = MCPVRChatUtil.NormalizeName(bone.name);
                if (n.Length > 0 && !avatarNames.ContainsKey(n)) avatarNames[n] = bone.name;
            }
            var avatarRawNames = avatarNames.Values.Where(n => n.Length >= 3).OrderByDescending(n => n.Length).ToList();

            var unmatched = new List<object>();
            var likelyMismatches = new List<string>();
            var affixVotes = new Dictionary<string, int>();
            var caseOnly = new List<string>();
            int unmatchedCount = 0;

            var stack = new Stack<Transform>();
            stack.Push(scanRoot);
            while (stack.Count > 0)
            {
                var parent = stack.Pop();
                foreach (Transform child in parent)
                {
                    if (matched.Contains(child)) { stack.Push(child); continue; }
                    if (skipSubtree != null && skipSubtree(child)) continue;

                    unmatchedCount++;
                    string stripped = stripConfiguredAffix(child.name);
                    string lookalike = null;
                    if (avatarNames.TryGetValue(MCPVRChatUtil.NormalizeName(stripped), out string sameName))
                    {
                        lookalike = sameName;
                        if (sameName != stripped) caseOnly.Add($"'{child.name}' vs avatar '{sameName}'");
                    }
                    else
                    {
                        foreach (var avatarName in avatarRawNames)
                        {
                            int idx = child.name.IndexOf(avatarName, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) continue;
                            lookalike = avatarName;
                            string before = child.name.Substring(0, idx);
                            string after = child.name.Substring(idx + avatarName.Length);
                            if (before.Length > 0) Vote(affixVotes, "prefix:" + before);
                            if (after.Length > 0) Vote(affixVotes, "suffix:" + after);
                            break;
                        }
                    }

                    if (lookalike != null && likelyMismatches.Count < MaxReportedBones)
                        likelyMismatches.Add($"{MCPVRChatUtil.RelPath(root, child)} (avatar has '{lookalike}')");

                    if (unmatched.Count < MaxReportedBones)
                    {
                        var entry = new Dictionary<string, object>
                        {
                            { "path", MCPVRChatUtil.RelPath(root, child) },
                            { "subtreeSize", child.GetComponentsInChildren<Transform>(true).Length }
                        };
                        if (lookalike != null) entry["looksLike"] = lookalike;
                        unmatched.Add(entry);
                    }
                }
            }

            var report = new Dictionary<string, object>
            {
                { "matchedCount", matched.Count },
                { "unmatchedCount", unmatchedCount },
                { "unmatched", unmatched }
            };
            if (likelyMismatches.Count > 0) report["likelyNameMismatches"] = likelyMismatches;

            var suggestions = new List<string>();
            var best = affixVotes.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best.Key != null && (best.Value >= 2 || best.Value == likelyMismatches.Count))
            {
                string kind = best.Key.StartsWith("prefix:", StringComparison.Ordinal) ? "prefix" : "suffix";
                string affix = best.Key.Substring(kind.Length + 1);
                suggestions.Add($"{best.Value} unmatched bone(s) carry the {kind} '{affix}' around an avatar bone name; set the {kind} '{affix}' via {affixSettingName}.");
            }
            if (caseOnly.Count > 0)
                suggestions.Add($"{caseOnly.Count} bone name(s) differ from the avatar's only in case or punctuation (e.g. {caseOnly[0]}); rename them to match exactly.");
            if (suggestions.Count > 0) report["suggestions"] = suggestions;
            return report;
        }

        private static void Vote(Dictionary<string, int> votes, string key)
        {
            votes[key] = votes.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        // ─────────────────────────────────────────────────────────────
        // Bone discovery (shared with vrc/avatar/vrcfury/armature-link)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The outfit's hips: its humanoid rig if it has one, else the avatar's hips path, else a bone
        /// containing the avatar's hips name one or two levels below an armature-like child (Modular
        /// Avatar's Setup Outfit heuristic), else an armature child's "hip" child (VRCFury's guess).
        /// </summary>
        internal static Transform FindOutfitHips(Transform outfitRoot, Transform avatarHips, Transform avatarRoot)
        {
            var outfitAnimator = outfitRoot.GetComponent<Animator>();
            if (outfitAnimator != null && outfitAnimator.isHuman)
            {
                Transform hips = null;
                try { hips = outfitAnimator.GetBoneTransform(HumanBodyBones.Hips); }
                catch { hips = null; }
                if (hips != null && hips.parent != outfitRoot && hips.IsChildOf(outfitRoot)) return hips;
            }

            string hipsPath = MCPVRChatUtil.RelPath(avatarRoot, avatarHips);
            if (!string.IsNullOrEmpty(hipsPath))
            {
                var samePath = outfitRoot.Find(hipsPath);
                if (samePath != null) return samePath;
            }

            foreach (Transform child in outfitRoot)
                foreach (Transform grandchild in child)
                    if (grandchild.name.Contains(avatarHips.name)) return grandchild;

            foreach (Transform child in outfitRoot)
                foreach (Transform grandchild in child)
                    foreach (Transform deeper in grandchild)
                        if (deeper.name.Contains(avatarHips.name)) return deeper;

            foreach (Transform child in outfitRoot)
            {
                if (!IsArmatureName(child.name)) continue;
                foreach (Transform grandchild in child)
                    if (grandchild.name.ToLowerInvariant().Contains("hip")) return grandchild;
            }
            return null;
        }

        /// <summary>VRCFury's Link From guess (ArmatureLinkBuilder.GuessLinkFrom).</summary>
        internal static Transform GuessLinkFrom(Transform host, Transform avatarRoot)
        {
            var animator = avatarRoot.GetComponent<Animator>();
            Transform avatarHips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (avatarHips != null && host != avatarRoot)
            {
                string path = MCPVRChatUtil.RelPath(avatarRoot, avatarHips);
                var found = string.IsNullOrEmpty(path) ? null : host.Find(path);
                if (found != null) return found;
            }

            var armatures = new List<Transform>();
            if (IsArmatureName(host.name)) armatures.Add(host);
            foreach (Transform child in host)
                if (IsArmatureName(child.name)) armatures.Add(child);
            foreach (var armature in armatures)
                foreach (Transform child in armature)
                    if (child.name.ToLowerInvariant().Contains("hip")) return child;

            return host;
        }

        /// <summary>True when <paramref name="t"/> is, or contains, one of the avatar's humanoid bones.</summary>
        internal static bool ContainsAvatarBone(Transform t, Animator avatarAnimator)
        {
            if (t == null || avatarAnimator == null || !avatarAnimator.isHuman) return false;
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var boneTransform = avatarAnimator.GetBoneTransform(bone);
                if (boneTransform != null && boneTransform.IsChildOf(t)) return true;
            }
            return false;
        }

        /// <summary>
        /// VRCFury's recursive/align default (ArmatureLink.HasExternalSkinBoneReference): true when a skinned
        /// mesh outside the linked bone is weighted to bones inside it — i.e. clothing, not a rigid prop.
        /// </summary>
        internal static bool HasExternalSkinBoneReference(Transform propBone, Transform avatarRoot)
        {
            if (propBone == null) return false;
            foreach (var skin in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin == null || skin.transform.IsChildOf(propBone)) continue;
                if (skin.rootBone != null && skin.rootBone.IsChildOf(propBone)) return true;
                if (skin.bones.Any(b => b != null && b.IsChildOf(propBone))) return true;
            }
            return false;
        }

        private static bool IsArmatureName(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.Contains("armature") || lower.Contains("skeleton");
        }

        private static bool IsAvatarDescriptor(Component c)
        {
            if (c == null) return false;
            string n = c.GetType().Name;
            return n == "VRCAvatarDescriptor" || n == "VRC_AvatarDescriptor";
        }

        private static bool LooksLikeAssetPath(string path)
        {
            string p = path.Replace('\\', '/');
            return p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }
    }
}
