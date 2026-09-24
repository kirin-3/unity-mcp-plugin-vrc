using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Harness for running non-destructive avatar build tooling (NDMF, VRCFury, Modular Avatar, d4rk optimizer)
    /// on an avatar clone to measure the built avatar rather than the authored scene state.
    /// Manages clone lifecycle, cleanup of leftover clones from interrupted sessions,
    /// and ensures failure paths never return scene-derived figures.
    /// </summary>
    public static class MCPVRChatBakeHarness
    {
        public const string CloneSuffix = "__MCP_Bake_Clone";

        // Test hooks for unit testing
        public static Action<GameObject> TestPreprocessOverride = null;
        public static Func<GameObject, List<string>> TestDetectBuildToolingOverride = null;

        public static void ResetTestOverrides()
        {
            TestPreprocessOverride = null;
            TestDetectBuildToolingOverride = null;
        }

        /// <summary>
        /// Detect and remove any leftover clones from a previously interrupted or crashed bake session.
        /// </summary>
        /// <param name="scene">Optional scene to check. Defaults to every loaded scene.</param>
        /// <returns>Number of leftover clones cleaned up.</returns>
        public static int CleanLeftoverClones(Scene scene = default)
        {
            // Clones are always created as roots of the avatar's scene, so scanning scene
            // roots finds every one this harness can produce. Resources.FindObjectsOfTypeAll
            // would also reach prefab stages, preview scenes and hidden editor objects —
            // DestroyImmediate on those is not worth the reach.
            int cleaned = 0;

            if (scene.IsValid())
                return CleanSceneRoots(scene);

            for (int i = 0; i < SceneManager.sceneCount; i++)
                cleaned += CleanSceneRoots(SceneManager.GetSceneAt(i));

            return cleaned;
        }

        private static int CleanSceneRoots(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;

            int cleaned = 0;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go != null && IsBakeClone(go))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    cleaned++;
                }
            }
            return cleaned;
        }

        public static bool IsBakeClone(GameObject go)
        {
            return go != null && go.name.EndsWith(CloneSuffix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Detect non-destructive build tooling components present on the avatar.
        /// </summary>
        public static List<string> DetectBuildTooling(GameObject avatar)
        {
            if (TestDetectBuildToolingOverride != null)
                return TestDetectBuildToolingOverride(avatar) ?? new List<string>();

            var tooling = new HashSet<string>();
            if (avatar == null) return new List<string>();

            var components = avatar.GetComponentsInChildren<Component>(true);
            foreach (var comp in components)
            {
                if (comp == null) continue;
                Type type = comp.GetType();
                string fullName = type.FullName ?? "";
                string typeName = type.Name;

                // Modular Avatar (nadena.dev.modular-avatar)
                if (fullName.IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fullName.IndexOf("modularavatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.StartsWith("ModularAvatar", StringComparison.OrdinalIgnoreCase))
                {
                    tooling.Add("Modular Avatar");
                }
                // VRCFury (com.vrcfury.vrcfury)
                else if (fullName.StartsWith("VF.", StringComparison.OrdinalIgnoreCase) ||
                         fullName.IndexOf("vrcfury", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tooling.Add("VRCFury");
                }
                // d4rk Avatar Optimizer
                else if (fullName.IndexOf("d4rk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("d4rk", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tooling.Add("d4rk Avatar Optimizer");
                }
                // General NDMF
                else if (fullName.IndexOf("ndmf", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tooling.Add("NDMF");
                }
            }

            return tooling.OrderBy(s => s).ToList();
        }

        /// <summary>
        /// Runs non-destructive build preprocessing on an avatar clone (if non-destructive tooling is present)
        /// and applies the analysis function to the built clone.
        ///
        /// - If no non-destructive tooling is present: analyzes authored avatar directly without cloning.
        /// - If non-destructive tooling is present: cleans leftovers, clones avatar, runs build pipeline,
        ///   measures clone, and destroys clone in a finally block.
        /// - On build failure: surfaces exception directly and never falls back to scene-derived figures.
        /// </summary>
        public static T RunBakeAndAnalyze<T>(GameObject authoredAvatar, Func<GameObject, List<string>, T> analyzeFunc)
        {
            if (authoredAvatar == null)
                throw new ArgumentNullException(nameof(authoredAvatar));

            List<string> tooling = DetectBuildTooling(authoredAvatar);

            // If no non-destructive components, measure directly from scene (Task 5.4)
            if (tooling.Count == 0)
            {
                return analyzeFunc(authoredAvatar, tooling);
            }

            // Clean any leftover clone from an interrupted previous session (Task 5.3)
            CleanLeftoverClones(authoredAvatar.scene);

            // Clone avatar and configure clone (Task 5.1)
            GameObject clone = UnityEngine.Object.Instantiate(authoredAvatar);
            clone.name = authoredAvatar.name + CloneSuffix;
            clone.transform.SetParent(null);
            clone.SetActive(true);

            IDisposable generatedAssets = null;
            try
            {
                generatedAssets = RedirectNdmfAssets();

                // Run build pipeline on the clone
                if (TestPreprocessOverride != null)
                {
                    TestPreprocessOverride(clone);
                }
                else
                {
                    ExecuteBuildPipeline(clone);
                }

                // Measure the baked avatar clone
                return analyzeFunc(clone, tooling);
            }
            finally
            {
                // Destroy clone on both success and failure paths (Task 5.2)
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                if (generatedAssets != null)
                {
                    generatedAssets.Dispose();
                    DeleteBakeAssets();
                }
            }
        }

        // ─── Generated assets ───
        // NDMF saves every build's generated assets (about 200 MB for a full avatar) under a folder named
        // after the avatar, and clears that only after an upload, which a bake never reaches. Bakes save
        // them in a folder of their own instead, deleted once the clone has been measured.

        internal const string NdmfGeneratedRoot = "Packages/nadena.dev.ndmf/__Generated";
        internal const string BakeAssetFolder = NdmfGeneratedRoot + "/__MCP_Bake";

        /// <summary>NDMF's OverrideTemporaryDirectoryScope for <see cref="BakeAssetFolder"/>, or null without NDMF.</summary>
        private static IDisposable RedirectNdmfAssets()
        {
            Type scope = FindType("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
            return scope?.GetConstructor(new[] { typeof(string) })?.Invoke(new object[] { BakeAssetFolder }) as IDisposable;
        }

        /// <summary>
        /// Deletes the bake's generated assets, and the per-avatar folders earlier versions of this harness
        /// left behind ("&lt;avatar&gt;__MCP_Bake_Clone"). Nothing else under NDMF's folder is touched.
        /// </summary>
        internal static void DeleteBakeAssets()
        {
            var folders = new List<string> { BakeAssetFolder };
            if (System.IO.Directory.Exists(NdmfGeneratedRoot))
            {
                folders.AddRange(System.IO.Directory.GetDirectories(NdmfGeneratedRoot, "*" + CloneSuffix)
                    .Select(d => d.Replace('\\', '/')));
            }
            foreach (var folder in folders)
            {
                if (!System.IO.Directory.Exists(folder)) continue;
                AssetDatabase.DeleteAsset(folder);
                FileUtil.DeleteFileOrDirectory(folder);
                FileUtil.DeleteFileOrDirectory(folder + ".meta");
            }
        }

        /// <summary>
        /// Run the SDK's avatar build hook chain (VRCBuildPipelineCallbacks.OnPreprocessAvatar) on
        /// the clone — the same call VRCFury's "Build a Test Copy", Av3Emulator and Gesture Manager
        /// make, so the clone ends up as close to the uploaded avatar as an editor bake gets.
        ///
        /// Not NDMF's AvatarProcessor alone: VRCFury and d4rk are not NDMF plugins, they register
        /// only as SDK preprocess callbacks, so an NDMF-only bake measured the avatar without them
        /// (and required NDMF in projects that only use VRCFury). NDMF registers on this chain too.
        ///
        /// A failing hook never opens a modal dialog here: the SDK's loop and VRCFury's hooks each answer a
        /// failure with one, which would block the editor until someone clicked OK. The same hooks run in
        /// the same order (see <see cref="RunHooks"/>), and the failure comes back as the exception.
        /// </summary>
        public static void ExecuteBuildPipeline(GameObject clone)
        {
            // VRCFury answers a second preprocess of a test copy with a modal dialog; refuse first.
            if (clone.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "VRCFuryTest"))
            {
                throw new InvalidOperationException(
                    "This avatar is a VRCFury test copy, so its build hooks have already run. " +
                    "Analyze the original avatar instead.");
            }

            Type callbacksType = FindType("VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks");
            MethodInfo preprocess = callbacksType?.GetMethod("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(GameObject) }, null);
            if (preprocess == null)
            {
                throw new InvalidOperationException(
                    "VRCBuildPipelineCallbacks.OnPreprocessAvatar(GameObject) was not found. The VRChat " +
                    "SDK version in this project is not one this harness knows how to drive.");
            }

            // A hook that returns false usually only logged why, so keep what the hooks logged.
            var errors = new List<string>();
            Application.LogCallback collect = (message, stackTrace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception) errors.Add(message);
            };
            Application.logMessageReceived += collect;
            string failedHook;
            try
            {
                var hooks = callbacksType.GetField("_preprocessAvatarCallbacks", BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) as IList;
                if (hooks != null)
                {
                    failedHook = RunHooks(hooks, clone);
                }
                else
                {
                    // An SDK without the hook list this mirrors: its own loop (with its dialog) is the fallback.
                    failedHook = (bool)preprocess.Invoke(null, new object[] { clone }) ? null : "a VRCSDK preprocess hook";
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Exception cause = ex;
                while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;
                Debug.LogException(cause);
                throw new InvalidOperationException("Avatar build hooks failed: " + cause.Message, cause);
            }
            finally
            {
                Application.logMessageReceived -= collect;
            }

            if (failedHook != null)
            {
                throw new InvalidOperationException($"Avatar build hooks failed ({failedHook} reported a failure)" + (errors.Count > 0
                    ? ": " + string.Join(" | ", errors.Take(3))
                    : " without logging a reason; see the Unity console."));
            }
        }

        /// <summary>
        /// The SDK's own loop (VRCBuildPipelineCallbacks.OnPreprocessAvatar): every hook in callbackOrder, stopping
        /// at the first that returns false or throws. Returns the failing hook's name, or null. A throwing hook
        /// surfaces as TargetInvocationException.
        /// </summary>
        private static string RunHooks(IList hooks, GameObject clone)
        {
            MethodInfo onPreprocess = FindType("VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback")
                ?.GetMethod("OnPreprocessAvatar", new[] { typeof(GameObject) });
            if (onPreprocess == null)
                throw new InvalidOperationException("IVRCSDKPreprocessAvatarCallback.OnPreprocessAvatar was not found in this VRChat SDK.");

            var ordered = hooks.Cast<object>().Where(h => h != null)
                .OrderBy(h => h is UnityEditor.Build.IOrderedCallback o ? o.callbackOrder : 0)
                .ToList();
            foreach (var hook in ordered)
            {
                bool ok = TryRunVRCFuryHook(hook, clone) || (bool)onPreprocess.Invoke(hook, new object[] { clone });
                if (!ok) return hook.GetType().Name;
            }
            return null;
        }

        /// <summary>
        /// VRCFury wraps each hook in an error boundary that shows a modal "VRCFury Error" dialog and returns
        /// false. This runs the hook the way that wrapper does (VrcfAvatarPreprocessor.OnPreprocessAvatar: the
        /// hook's Process inside a VRCFuryBuildContext) minus the boundary, so the error reaches the caller.
        /// True when it ran the hook; false when the hook is not VRCFury's or VRCFury's internals differ, and the
        /// caller then runs it normally. Edit mode only: in play mode the wrapper also tracks which objects were
        /// already processed.
        /// </summary>
        private static bool TryRunVRCFuryHook(object hook, GameObject clone)
        {
            if (Application.isPlaying) return false;
            Type baseType = FindType("VF.Hooks.VrcfAvatarPreprocessor");
            if (baseType == null || !baseType.IsInstanceOfType(hook)) return false;
            MethodInfo process = baseType.GetMethod("Process", BindingFlags.NonPublic | BindingFlags.Instance);
            Type contextType = FindType("VF.Utils.VRCFuryBuildContext");
            Type objectType = process?.GetParameters().FirstOrDefault()?.ParameterType;
            MethodInfo wrap = objectType?.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            if (contextType == null || wrap == null || contextType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
                return false;

            using ((IDisposable)Activator.CreateInstance(contextType, true))
            {
                process.Invoke(hook, new[] { wrap.Invoke(null, new object[] { clone }) });
            }
            return true;
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
