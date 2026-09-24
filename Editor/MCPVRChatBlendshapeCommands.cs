using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Blendshape inspection and editing:
    /// - vrc/avatar/blendshapes/list: a mesh's blendshape names and weights (the face mesh by default), with
    ///   optional face-tracking coverage for Unified Expressions, ARKit and SRanipal naming
    /// - vrc/avatar/blendshapes/set: set blendshape weights on a mesh (undoable)
    ///
    /// Viseme mapping, VRCFury toggle blendshape actions and face-tracking setups all need exact blendshape
    /// names, which nothing else in the tool surface lists.
    /// </summary>
    public static class MCPVRChatBlendshapeCommands
    {
        private const int MaxListedShapes = 1000;

        // Face-tracking blendshape standards. Names are compared normalized (case and punctuation ignored),
        // so "Jaw_Open", "jawOpen" and "JawOpen" all match. Unified Expressions and SRanipal come from
        // VRCFaceTracking's own enums; ARKit is Apple's 52-shape set (VRCFT reads "Perfect Sync" avatars).
        internal static readonly string[] UnifiedExpressions =
        {
            "EyeLookOutRight", "EyeLookInRight", "EyeLookUpRight", "EyeLookDownRight", "EyeLookOutLeft",
            "EyeLookInLeft", "EyeLookUpLeft", "EyeLookDownLeft", "EyeClosedRight", "EyeClosedLeft",
            "EyeDilationRight", "EyeDilationLeft", "EyeConstrictRight", "EyeConstrictLeft", "EyeSquintRight",
            "EyeSquintLeft", "EyeWideRight", "EyeWideLeft", "BrowPinchRight", "BrowPinchLeft", "BrowLowererRight",
            "BrowLowererLeft", "BrowInnerUpRight", "BrowInnerUpLeft", "BrowOuterUpRight", "BrowOuterUpLeft",
            "NasalDilationRight", "NasalDilationLeft", "NasalConstrictRight", "NasalConstrictLeft",
            "CheekSquintRight", "CheekSquintLeft", "CheekPuffRight", "CheekPuffLeft", "CheekSuckRight",
            "CheekSuckLeft", "JawOpen", "JawRight", "JawLeft", "JawForward", "JawBackward", "JawClench",
            "JawMandibleRaise", "MouthClosed", "LipSuckUpperRight", "LipSuckUpperLeft", "LipSuckLowerRight",
            "LipSuckLowerLeft", "LipSuckCornerRight", "LipSuckCornerLeft", "LipFunnelUpperRight",
            "LipFunnelUpperLeft", "LipFunnelLowerRight", "LipFunnelLowerLeft", "LipPuckerUpperRight",
            "LipPuckerUpperLeft", "LipPuckerLowerRight", "LipPuckerLowerLeft", "MouthUpperUpRight",
            "MouthUpperUpLeft", "MouthUpperDeepenRight", "MouthUpperDeepenLeft", "NoseSneerRight", "NoseSneerLeft",
            "MouthLowerDownRight", "MouthLowerDownLeft", "MouthUpperRight", "MouthUpperLeft", "MouthLowerRight",
            "MouthLowerLeft", "MouthCornerPullRight", "MouthCornerPullLeft", "MouthCornerSlantRight",
            "MouthCornerSlantLeft", "MouthFrownRight", "MouthFrownLeft", "MouthStretchRight", "MouthStretchLeft",
            "MouthDimpleRight", "MouthDimpleLeft", "MouthRaiserUpper", "MouthRaiserLower", "MouthPressRight",
            "MouthPressLeft", "MouthTightenerRight", "MouthTightenerLeft", "TongueOut", "TongueUp", "TongueDown",
            "TongueRight", "TongueLeft", "TongueRoll", "TongueBendDown", "TongueCurlUp", "TongueSquish",
            "TongueFlat", "TongueTwistRight", "TongueTwistLeft", "SoftPalateClose", "ThroatSwallow", "NeckFlexRight",
            "NeckFlexLeft"
        };

        internal static readonly string[] UnifiedBlendedExpressions =
        {
            "BrowUpRight", "BrowUpLeft", "BrowDownRight", "BrowDownLeft", "MouthSmileRight", "MouthSmileLeft",
            "MouthSadRight", "MouthSadLeft"
        };

        internal static readonly string[] ARKit =
        {
            "eyeBlinkLeft", "eyeLookDownLeft", "eyeLookInLeft", "eyeLookOutLeft", "eyeLookUpLeft", "eyeSquintLeft",
            "eyeWideLeft", "eyeBlinkRight", "eyeLookDownRight", "eyeLookInRight", "eyeLookOutRight",
            "eyeLookUpRight", "eyeSquintRight", "eyeWideRight", "jawForward", "jawLeft", "jawRight", "jawOpen",
            "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight", "mouthSmileLeft",
            "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight",
            "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower",
            "mouthShrugUpper", "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight",
            "mouthUpperUpLeft", "mouthUpperUpRight", "browDownLeft", "browDownRight", "browInnerUp",
            "browOuterUpLeft", "browOuterUpRight", "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
            "noseSneerLeft", "noseSneerRight", "tongueOut"
        };

        internal static readonly string[] SRanipalLip =
        {
            "JawRight", "JawLeft", "JawForward", "JawOpen", "MouthApeShape", "MouthUpperRight", "MouthUpperLeft",
            "MouthLowerRight", "MouthLowerLeft", "MouthUpperOverturn", "MouthLowerOverturn", "MouthPout",
            "MouthSmileRight", "MouthSmileLeft", "MouthSadRight", "MouthSadLeft", "CheekPuffRight", "CheekPuffLeft",
            "CheekSuck", "MouthUpperUpRight", "MouthUpperUpLeft", "MouthLowerDownRight", "MouthLowerDownLeft",
            "MouthUpperInside", "MouthLowerInside", "MouthLowerOverlay", "TongueLongStep1", "TongueLongStep2",
            "TongueDown", "TongueUp", "TongueRight", "TongueLeft", "TongueRoll", "TongueUpLeftMorph",
            "TongueUpRightMorph", "TongueDownLeftMorph", "TongueDownRightMorph"
        };

        // ─────────────────────────────────────────────────────────────
        // vrc/avatar/blendshapes/list
        // ─────────────────────────────────────────────────────────────

        public static object List(Dictionary<string, object> args)
        {
            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");

            var skin = ResolveMesh(avatar, args, out string how, out err);
            if (skin == null) return MCPVRChatUtil.Fail(err);

            if (!MCPVRChatUtil.TryReadOptionalBool(args, "faceTracking", out bool? faceTracking, out err))
                return MCPVRChatUtil.Fail(err);

            string filter = MCPVRChatUtil.GetString(args, "filter")?.Trim();
            Mesh mesh = skin.sharedMesh;
            var names = ShapeNames(mesh);

            var shapes = new List<object>();
            int matching = 0;
            for (int i = 0; i < names.Count; i++)
            {
                if (!string.IsNullOrEmpty(filter) && names[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matching++;
                if (shapes.Count >= MaxListedShapes) continue;
                shapes.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "name", names[i] },
                    { "weight", Math.Round(skin.GetBlendShapeWeight(i), 3) }
                });
            }

            Transform root = avatar.transform;
            var result = new Dictionary<string, object>
            {
                { "avatarName", avatar.name },
                { "meshPath", MCPVRChatUtil.RelPath(root, skin.transform) },
                { "selectedBy", how },
                { "blendShapeCount", names.Count },
                { "blendShapes", shapes }
            };
            if (!string.IsNullOrEmpty(filter)) result["matchingCount"] = matching;
            if (matching > MaxListedShapes) result["truncated"] = true;

            var others = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != null && s != skin && s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0)
                .Select(s => (object)new Dictionary<string, object>
                {
                    { "path", MCPVRChatUtil.RelPath(root, s.transform) },
                    { "blendShapeCount", s.sharedMesh.blendShapeCount }
                }).ToList();
            if (others.Count > 0) result["otherMeshes"] = others;

            if (faceTracking == true)
                result["faceTracking"] = FaceTrackingReport(avatar, skin, names);

            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // vrc/avatar/blendshapes/set
        // ─────────────────────────────────────────────────────────────

        public static object Set(Dictionary<string, object> args)
        {
            var avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string err);
            if (avatar == null) return MCPVRChatUtil.Fail(err ?? "Avatar not found.");

            var skin = ResolveMesh(avatar, args, out _, out err);
            if (skin == null) return MCPVRChatUtil.Fail(err);

            if (!(MCPVRChatUtil.Has(args, "weights") && args["weights"] is Dictionary<string, object> weights) || weights.Count == 0)
                return MCPVRChatUtil.Fail("'weights' is required: an object mapping blendshape names to weights (0-100), e.g. {\"Smile\": 100}.");

            // Validate everything first; nothing changes if any entry is wrong.
            Mesh mesh = skin.sharedMesh;
            var names = ShapeNames(mesh);
            var plan = new List<(int index, string name, float value)>();
            var errors = new List<string>();
            var warnings = new List<string>();
            foreach (var kv in weights)
            {
                int index = mesh.GetBlendShapeIndex(kv.Key);
                if (index < 0)
                {
                    var suggestions = MCPVRChatUtil.Suggest(kv.Key, names);
                    errors.Add($"'{kv.Key}' is not a blendshape on '{skin.name}'" +
                               (suggestions.Count > 0 ? $" (did you mean {string.Join(", ", suggestions)}?)" : "") + ".");
                    continue;
                }
                if (!MCPVRChatUtil.TryReadFloat(kv.Value, out float value))
                {
                    errors.Add($"Weight for '{kv.Key}' must be a number.");
                    continue;
                }
                if (value < 0f || value > 100f)
                    warnings.Add($"'{kv.Key}' set to {value}; blendshape weights normally run 0-100.");
                plan.Add((index, kv.Key, value));
            }
            if (errors.Count > 0)
            {
                var failed = MCPVRChatUtil.Fail(string.Join(" ", errors) + " Nothing was changed.");
                failed["errors"] = errors;
                return failed;
            }

            Undo.RecordObject(skin, "Set Blendshape Weights");
            var applied = new List<object>();
            foreach (var (index, name, value) in plan)
            {
                float previous = skin.GetBlendShapeWeight(index);
                skin.SetBlendShapeWeight(index, value);
                applied.Add(new Dictionary<string, object>
                {
                    { "name", name },
                    { "previous", Math.Round(previous, 3) },
                    { "value", Math.Round(skin.GetBlendShapeWeight(index), 3) }
                });
            }
            EditorUtility.SetDirty(skin);
            PrefabUtility.RecordPrefabInstancePropertyModifications(skin);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "meshPath", MCPVRChatUtil.RelPath(avatar.transform, skin.transform) },
                { "applied", applied }
            };
            if (EditorApplication.isPlaying)
                warnings.Add("Play mode is running: these weights are lost when it stops, and an emulator or animation may override them.");
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // Face tracking
        // ─────────────────────────────────────────────────────────────

        internal static Dictionary<string, object> FaceTrackingCoverage(IEnumerable<string> shapeNames)
        {
            var present = new HashSet<string>(shapeNames.Select(n => MCPVRChatUtil.NormalizeName(n)));
            var result = new Dictionary<string, object>();
            string bestStandard = null;
            double bestCoverage = 0d;

            foreach (var (key, standard) in new[]
                     {
                         ("unifiedExpressions", UnifiedExpressions),
                         ("unifiedBlended", UnifiedBlendedExpressions),
                         ("arkit", ARKit),
                         ("sranipal", SRanipalLip)
                     })
            {
                var missing = standard.Where(n => !present.Contains(MCPVRChatUtil.NormalizeName(n))).ToList();
                int found = standard.Length - missing.Count;
                double coverage = Math.Round((double)found / standard.Length, 3);
                result[key] = new Dictionary<string, object>
                {
                    { "found", found },
                    { "total", standard.Length },
                    { "coverage", coverage },
                    { "missing", missing }
                };
                // Blended shapes are a supplement to Unified Expressions, not a standard of their own.
                if (key != "unifiedBlended" && coverage > bestCoverage)
                {
                    bestCoverage = coverage;
                    bestStandard = key;
                }
            }

            result["detectedStandard"] = bestCoverage >= 0.3 ? bestStandard : "none";
            return result;
        }

        private static Dictionary<string, object> FaceTrackingReport(GameObject avatar, SkinnedMeshRenderer skin, List<string> names)
        {
            var report = FaceTrackingCoverage(names);

            // Face tracking sometimes lives on a separate face mesh; point at it when another mesh covers more.
            double Coverage(Dictionary<string, object> r, string key) =>
                r[key] is Dictionary<string, object> d && d["coverage"] is double c ? c : 0d;
            double own = Math.Max(Coverage(report, "unifiedExpressions"), Math.Max(Coverage(report, "arkit"), Coverage(report, "sranipal")));
            SkinnedMeshRenderer best = null;
            double bestCoverage = own;
            foreach (var other in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (other == null || other == skin || other.sharedMesh == null || other.sharedMesh.blendShapeCount == 0) continue;
                var otherReport = FaceTrackingCoverage(ShapeNames(other.sharedMesh));
                double c = Math.Max(Coverage(otherReport, "unifiedExpressions"), Math.Max(Coverage(otherReport, "arkit"), Coverage(otherReport, "sranipal")));
                if (c > bestCoverage + 0.05)
                {
                    bestCoverage = c;
                    best = other;
                }
            }
            if (best != null)
                report["betterMesh"] = MCPVRChatUtil.RelPath(avatar.transform, best.transform);
            return report;
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static SkinnedMeshRenderer ResolveMesh(GameObject avatar, Dictionary<string, object> args, out string how, out string error)
        {
            how = null;
            error = null;
            string meshPath = MCPVRChatUtil.GetString(args, "meshPath");
            if (!string.IsNullOrEmpty(meshPath))
            {
                var t = MCPVRChatUtil.FindUnder(avatar.transform, meshPath);
                var named = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (named == null || named.sharedMesh == null)
                {
                    var candidates = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .Where(s => s != null && s.sharedMesh != null)
                        .Select(s => MCPVRChatUtil.RelPath(avatar.transform, s.transform));
                    var suggestions = MCPVRChatUtil.Suggest(meshPath, candidates);
                    error = $"No SkinnedMeshRenderer with a mesh at '{meshPath}' under '{avatar.name}'." +
                            (suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", suggestions)}?" : "");
                    return null;
                }
                how = "meshPath";
                return named;
            }

            var face = DefaultFaceMesh(avatar, out how);
            if (face == null)
                error = $"'{avatar.name}' has no skinned mesh with blendshapes.";
            return face;
        }

        /// <summary>The descriptor's viseme mesh, else a mesh named Body/Face/Head, else the one with most blendshapes.</summary>
        internal static SkinnedMeshRenderer DefaultFaceMesh(GameObject avatar, out string how)
        {
            how = null;
            var descriptor = MCPVRChatAuthoringCommands.FindDescriptorComponent(avatar);
            if (descriptor != null)
            {
                var type = descriptor.GetType();
                object value = type.GetField("VisemeSkinnedMesh")?.GetValue(descriptor)
                               ?? type.GetProperty("VisemeSkinnedMesh")?.GetValue(descriptor, null);
                if (value is SkinnedMeshRenderer viseme && viseme != null && viseme.sharedMesh != null
                    && viseme.transform.IsChildOf(avatar.transform))
                {
                    how = "descriptor viseme mesh";
                    return viseme;
                }
            }

            var skins = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != null && s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0).ToList();
            foreach (var preferred in new[] { "Body", "Face", "Head" })
            {
                var byName = skins.FirstOrDefault(s => s.name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                {
                    how = $"named '{byName.name}'";
                    return byName;
                }
            }

            var most = skins.OrderByDescending(s => s.sharedMesh.blendShapeCount).FirstOrDefault();
            if (most != null) how = "most blendshapes";
            return most;
        }

        private static List<string> ShapeNames(Mesh mesh)
        {
            var names = new List<string>();
            if (mesh == null) return names;
            for (int i = 0; i < mesh.blendShapeCount; i++) names.Add(mesh.GetBlendShapeName(i));
            return names;
        }
    }
}
