using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Extra vrc/avatar/audit checks, run on the baked avatar (after NDMF, Modular Avatar and VRCFury have
    /// rewritten it) so they judge what VRChat will actually receive:
    /// - animation paths: clip bindings that point at objects, components or blendshapes that are not there
    ///   (the classic breakage after renaming or moving something)
    /// - mesh bounds: skinned meshes whose bounds differ from the rest, so parts of the avatar cull at
    ///   different times (what VRCFury's Bounding Box Fix and Modular Avatar Mesh Settings unify)
    /// - anchor overrides: renderers probing light at different anchors, so pieces are lit differently
    ///   (what VRCFury's Anchor Override Fix and Modular Avatar Mesh Settings unify)
    /// </summary>
    internal static class MCPVRChatAuditChecks
    {
        private const int MaxIssues = 50;
        private const int MaxListed = 25;

        // ─────────────────────────────────────────────────────────────
        // Animation paths
        // ─────────────────────────────────────────────────────────────

        internal static Dictionary<string, object> AuditAnimationPaths(GameObject target)
        {
            return CheckClipBindings(target, CollectClips(target));
        }

        /// <summary>Clips from every playable layer on the descriptor plus the root Animator, tagged by layer.</summary>
        internal static List<(string source, AnimationClip clip)> CollectClips(GameObject target)
        {
            var controllers = new List<(string source, RuntimeAnimatorController controller)>();

            var animator = target.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
                controllers.Add(("Animator", animator.runtimeAnimatorController));

            var descriptor = MCPVRChatAuthoringCommands.FindDescriptorComponent(target);
            if (descriptor != null)
            {
                foreach (var fieldName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    if (!(MCPVRChatUtil.GetFieldValue(descriptor, fieldName) is Array layers)) continue;
                    foreach (var layer in layers)
                    {
                        if (layer == null) continue;
                        if (MCPVRChatUtil.GetFieldValue(layer, "animatorController") is RuntimeAnimatorController controller && controller != null)
                            controllers.Add((MCPVRChatUtil.GetFieldValue(layer, "type")?.ToString() ?? "Layer", controller));
                    }
                }
            }

            var clips = new List<(string, AnimationClip)>();
            foreach (var (source, controller) in controllers)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null) clips.Add((source, clip));
                }
            }
            return clips;
        }

        private sealed class BindingIssue
        {
            public string Problem;
            public string Path;
            public string ComponentType;
            public string BlendShape;
            public int BindingCount;
            public readonly HashSet<string> Clips = new HashSet<string>();
            public readonly HashSet<string> Sources = new HashSet<string>();
        }

        internal static Dictionary<string, object> CheckClipBindings(GameObject target, IEnumerable<(string source, AnimationClip clip)> clips)
        {
            Transform root = target.transform;
            var issues = new Dictionary<string, BindingIssue>();
            var clipSources = new Dictionary<AnimationClip, HashSet<string>>();
            foreach (var (source, clip) in clips)
            {
                if (clip == null) continue;
                if (!clipSources.TryGetValue(clip, out var sources)) clipSources[clip] = sources = new HashSet<string>();
                sources.Add(source);
            }

            int bindingCount = 0;
            foreach (var kv in clipSources)
            {
                var clip = kv.Key;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    bindingCount++;
                    CheckBinding(root, binding, clip, kv.Value, issues);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    bindingCount++;
                    CheckBinding(root, binding, clip, kv.Value, issues);
                }
            }

            var ordered = issues.Values.OrderByDescending(i => i.BindingCount).ThenBy(i => i.Path, StringComparer.Ordinal).ToList();
            var list = ordered.Take(MaxIssues).Select(i =>
            {
                var entry = new Dictionary<string, object>
                {
                    { "problem", i.Problem },
                    { "path", i.Path },
                    { "bindingCount", i.BindingCount },
                    { "clips", i.Clips.OrderBy(c => c, StringComparer.Ordinal).Take(5).ToList() },
                    { "layers", i.Sources.OrderBy(s => s, StringComparer.Ordinal).ToList() }
                };
                if (i.ComponentType != null) entry["componentType"] = i.ComponentType;
                if (i.BlendShape != null) entry["blendShape"] = i.BlendShape;
                if (i.Clips.Count > 5) entry["moreClips"] = i.Clips.Count - 5;
                return (object)entry;
            }).ToList();

            int missingObjects = ordered.Count(i => i.Problem == "missingObject");
            int missingComponents = ordered.Count(i => i.Problem == "missingComponent");
            int missingBlendShapes = ordered.Count(i => i.Problem == "missingBlendShape");

            var result = new Dictionary<string, object>
            {
                { "clipsChecked", clipSources.Count },
                { "bindingsChecked", bindingCount },
                { "brokenBindingCount", ordered.Sum(i => i.BindingCount) },
                { "missingObjectCount", missingObjects },
                { "missingComponentCount", missingComponents },
                { "missingBlendShapeCount", missingBlendShapes },
                { "issues", list }
            };
            if (ordered.Count > MaxIssues) result["truncated"] = true;

            if (ordered.Count == 0)
                result["summary"] = clipSources.Count == 0
                    ? "No animation clips found on the avatar's playable layers."
                    : $"All {bindingCount} bindings in {clipSources.Count} clips resolve on the baked avatar.";
            else
                result["summary"] =
                    $"{ordered.Sum(i => i.BindingCount)} binding(s) in {ordered.SelectMany(i => i.Clips).Distinct().Count()} clip(s) point at " +
                    $"{missingObjects} missing object path(s), {missingComponents} missing component(s) and {missingBlendShapes} missing blendshape(s). " +
                    "These animate nothing in VRChat — usually an object or blendshape that was renamed or moved after the clip was made.";
            return result;
        }

        private static void CheckBinding(Transform root, EditorCurveBinding binding, AnimationClip clip, HashSet<string> sources,
            Dictionary<string, BindingIssue> issues)
        {
            // Animator-typed curves are humanoid muscles and animator parameters (AAPs), not object paths.
            if (binding.type == typeof(Animator)) return;

            Transform target = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
            if (target == null)
            {
                Record(issues, "missingObject", binding.path, null, null, clip, sources);
                return;
            }

            if (binding.type == null || binding.type == typeof(GameObject) || binding.type == typeof(Transform)) return;
            if (!typeof(Component).IsAssignableFrom(binding.type)) return;

            var component = target.GetComponent(binding.type);
            if (component == null)
            {
                Record(issues, "missingComponent", binding.path, binding.type.Name, null, clip, sources);
                return;
            }

            const string blendShapePrefix = "blendShape.";
            if (component is SkinnedMeshRenderer skin && binding.propertyName.StartsWith(blendShapePrefix, StringComparison.Ordinal))
            {
                string shape = binding.propertyName.Substring(blendShapePrefix.Length);
                if (skin.sharedMesh == null || skin.sharedMesh.GetBlendShapeIndex(shape) < 0)
                    Record(issues, "missingBlendShape", binding.path, binding.type.Name, shape, clip, sources);
            }
        }

        private static void Record(Dictionary<string, BindingIssue> issues, string problem, string path, string componentType,
            string blendShape, AnimationClip clip, HashSet<string> sources)
        {
            string key = $"{problem}|{path}|{componentType}|{blendShape}";
            if (!issues.TryGetValue(key, out var issue))
            {
                issues[key] = issue = new BindingIssue
                {
                    Problem = problem,
                    Path = string.IsNullOrEmpty(path) ? "(avatar root)" : path,
                    ComponentType = componentType,
                    BlendShape = blendShape
                };
            }
            issue.BindingCount++;
            issue.Clips.Add(clip.name);
            foreach (var s in sources) issue.Sources.Add(s);
        }

        // ─────────────────────────────────────────────────────────────
        // Mesh bounds
        // ─────────────────────────────────────────────────────────────

        internal static Dictionary<string, object> AuditMeshBounds(GameObject target)
        {
            Transform root = target.transform;
            var skins = target.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != null && s.sharedMesh != null).ToList();

            var result = new Dictionary<string, object> { { "skinnedMeshCount", skins.Count } };
            if (skins.Count == 0)
            {
                result["consistent"] = true;
                result["summary"] = "No skinned meshes.";
                return result;
            }

            var boxes = skins.Select(s => (skin: s, box: WorldBounds(s))).ToList();
            Bounds avatarBox = boxes[0].box;
            foreach (var entry in boxes) avatarBox.Encapsulate(entry.box);

            Vector3 avatarSize = Vector3.Max(avatarBox.size, Vector3.one * 1e-4f);
            float tolerance = Mathf.Max(0.01f, avatarBox.size.magnitude * 0.05f);

            var mismatched = new List<(float coverage, Dictionary<string, object> entry)>();
            var offscreen = new List<string>();
            var configurations = new HashSet<string>();
            foreach (var (skin, box) in boxes)
            {
                string path = PathOf(root, skin.transform);
                if (skin.updateWhenOffscreen) offscreen.Add(path);
                configurations.Add(Quantize(box.center, tolerance) + "|" + Quantize(box.size, tolerance));

                bool matches = (box.center - avatarBox.center).magnitude <= tolerance
                               && Mathf.Abs(box.size.x - avatarBox.size.x) <= tolerance * 2f
                               && Mathf.Abs(box.size.y - avatarBox.size.y) <= tolerance * 2f
                               && Mathf.Abs(box.size.z - avatarBox.size.z) <= tolerance * 2f;
                if (matches) continue;

                var ratio = new Vector3(box.size.x / avatarSize.x, box.size.y / avatarSize.y, box.size.z / avatarSize.z);
                float coverage = Mathf.Clamp01(ratio.x) * Mathf.Clamp01(ratio.y) * Mathf.Clamp01(ratio.z);
                mismatched.Add((coverage, new Dictionary<string, object>
                {
                    { "path", path },
                    { "rootBone", skin.rootBone != null ? PathOf(root, skin.rootBone) : null },
                    { "sizeRatio", new List<object> { Round(ratio.x), Round(ratio.y), Round(ratio.z) } },
                    { "coverage", Round(coverage) }
                }));
            }

            bool consistent = mismatched.Count == 0;
            result["consistent"] = consistent;
            result["distinctBounds"] = configurations.Count;
            result["avatarBoundsSize"] = new List<object> { Round(avatarBox.size.x), Round(avatarBox.size.y), Round(avatarBox.size.z) };
            result["mismatchedCount"] = mismatched.Count;
            result["mismatched"] = mismatched.OrderBy(m => m.coverage).Take(MaxListed).Select(m => (object)m.entry).ToList();
            if (offscreen.Count > 0) result["updateWhenOffscreen"] = offscreen.Take(MaxListed).ToList();

            result["summary"] = consistent
                ? $"All {skins.Count} skinned mesh(es) share the avatar's bounds, so they cull together."
                : $"{mismatched.Count} of {skins.Count} skinned mesh(es) have bounds that differ from the avatar's overall box, so they can disappear " +
                  "at different times (smallest coverage first). VRCFury's automatic Bounding Box Fix or Modular Avatar Mesh Settings unify them.";
            return result;
        }

        /// <summary>
        /// The box a skinned mesh culls against: its localBounds, which live in the root bone's space (or the
        /// renderer's own when it has none). Computed from the serialized bounds so inactive meshes count too.
        /// </summary>
        internal static Bounds WorldBounds(SkinnedMeshRenderer skin)
        {
            Transform origin = skin.rootBone != null ? skin.rootBone : skin.transform;
            Bounds local = skin.localBounds;
            Matrix4x4 m = origin.localToWorldMatrix;
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                Vector3 world = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign));
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
            return new Bounds((min + max) * 0.5f, max - min);
        }

        // ─────────────────────────────────────────────────────────────
        // Anchor overrides
        // ─────────────────────────────────────────────────────────────

        internal static Dictionary<string, object> AuditAnchorOverrides(GameObject target)
        {
            Transform root = target.transform;
            var renderers = target.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && (r is SkinnedMeshRenderer || r is MeshRenderer) && !IsWorldDropped(r.transform, root))
                .ToList();

            var result = new Dictionary<string, object> { { "rendererCount", renderers.Count } };
            if (renderers.Count == 0)
            {
                result["consistent"] = true;
                result["summary"] = "No mesh renderers.";
                return result;
            }

            const string none = "(none)";
            var groups = renderers
                .GroupBy(r => r.probeAnchor != null ? PathOf(root, r.probeAnchor) : none)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            bool consistent = groups.Count <= 1;
            result["consistent"] = consistent;
            result["anchorCount"] = groups.Count;
            result["anchors"] = groups.Select(g => (object)new Dictionary<string, object>
            {
                { "anchor", g.Key },
                { "rendererCount", g.Count() },
                { "renderers", g.Take(5).Select(r => PathOf(root, r.transform)).ToList() }
            }).ToList();

            if (!consistent)
            {
                string majority = groups[0].Key;
                result["majorityAnchor"] = majority;
                result["mismatched"] = renderers
                    .Where(r => (r.probeAnchor != null ? PathOf(root, r.probeAnchor) : none) != majority)
                    .Take(MaxListed)
                    .Select(r => PathOf(root, r.transform))
                    .ToList();
            }

            if (consistent)
            {
                result["summary"] = groups[0].Key == none
                    ? $"None of the {renderers.Count} renderer(s) has an Anchor Override, so each samples light probes at its own bounds centre; pieces can be lit differently. Setting one shared anchor (e.g. the chest) fixes that."
                    : $"All {renderers.Count} renderer(s) share the anchor '{groups[0].Key}'.";
            }
            else
            {
                result["summary"] =
                    $"Renderers use {groups.Count} different anchors, so parts of the avatar are lit from different points. " +
                    "VRCFury's Anchor Override Fix or Modular Avatar Mesh Settings set one shared anchor.";
            }
            return result;
        }

        /// <summary>Renderers under a Rigidbody are dropped into the world; VRCFury's Anchor Override Fix skips them too.</summary>
        private static bool IsWorldDropped(Transform t, Transform root)
        {
            for (var current = t; current != null; current = current.parent)
            {
                if (current.GetComponent<Rigidbody>() != null) return true;
                if (current == root) break;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static string PathOf(Transform root, Transform t)
        {
            string path = MCPVRChatUtil.RelPath(root, t);
            if (path == null) return t.name;
            return path.Length == 0 ? "(avatar root)" : path;
        }

        private static string Quantize(Vector3 v, float step)
        {
            return $"{Mathf.Round(v.x / step)},{Mathf.Round(v.y / step)},{Mathf.Round(v.z / step)}";
        }

        private static double Round(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0d;
            return Math.Round(value, 3);
        }
    }
}
