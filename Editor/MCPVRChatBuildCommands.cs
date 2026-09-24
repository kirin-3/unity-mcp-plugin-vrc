using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// vrc/build: build the avatar or world through the VRChat SDK's own builder API, as the control
    /// panel's Build button does: the build callbacks (VRCFury, NDMF, optimizers), the SDK's validation
    /// and the bundle export. test:true runs Build &amp; Test instead: an avatar joins VRChat's local test
    /// avatars, a world opens in a local VRChat client.
    ///
    /// Never uploads: Build and BuildAndTest are the only builder methods this calls.
    /// The SDK is reached by reflection so the plugin compiles without it.
    /// </summary>
    public static class MCPVRChatBuildCommands
    {
        private const string PanelType = "VRCSdkControlPanel";
        private const string BuilderApiType = "VRC.SDKBase.Editor.IVRCSdkBuilderApi";
        private const string AvatarBuilderType = "VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi";
        private const string WorldBuilderType = "VRC.SDK3.Editor.IVRCSdkWorldBuilderApi";
        private const string ShowPanelMenu = "VRChat SDK/Show Control Panel";
        private const int PanelOpenTimeoutMs = 30000;
        private const int MaxErrors = 50;
        private const int MaxErrorLength = 1000;

        private static bool _running;

        /// <summary>Route: vrc/build. Deferred: poll vrc/avatar/job with the returned jobId.</summary>
        public static object Build(Dictionary<string, object> args)
        {
            string projectType = MCPVRChatDetect.GetProjectType();
            bool isAvatar = projectType == "avatar";
            if (!isAvatar && projectType != "world")
                return MCPVRChatUtil.Fail("vrc/build needs a VRChat avatar or world project.");
            if (!MCPVRChatUtil.TryReadOptionalBool(args, "test", out bool? testArg, out string argError))
                return MCPVRChatUtil.Fail(argError);
            bool test = testArg == true;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return MCPVRChatUtil.Fail("Exit play mode first: the VRChat SDK does not build in play mode.");
            if (EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed)
                return MCPVRChatUtil.Fail("Scripts are compiling or have compile errors. Read them with unity_get_compilation_errors.");
            if (_running)
                return MCPVRChatUtil.Fail("A VRChat SDK build started through MCP is still running. Wait for it to finish.");

            GameObject avatar = null;
            if (isAvatar)
            {
                avatar = MCPVRChatAvatarCommands.ResolveAvatarGameObject(args, out string error);
                if (avatar == null) return MCPVRChatUtil.Fail(error ?? "No avatar found in the open scene.");
            }

            _running = true;
            return MCPVRChatJobs.StartAsync("vrc/build", async () =>
            {
                try { return await RunBuild(isAvatar, avatar, test); }
                finally { _running = false; }
            });
        }

        private static async Task<object> RunBuild(bool isAvatar, GameObject avatar, bool test)
        {
            var (builder, builderType, error) = await GetBuilder(isAvatar ? AvatarBuilderType : WorldBuilderType);
            if (builder == null) return MCPVRChatUtil.Fail(error);

            // Also refreshes the builder's avatar/scene list, which a world build reads.
            var validArgs = new object[] { null };
            var isValid = builder.GetType().GetMethod("IsValidBuilder", new[] { typeof(string).MakeByRefType() });
            if (isValid != null && !(bool)isValid.Invoke(builder, validArgs))
                return MCPVRChatUtil.Fail(validArgs[0] as string ?? "The VRChat SDK cannot build this scene.");

            var state = MCPVRChatUtil.FindType(BuilderApiType)?.GetProperty("BuildState")?.GetValue(builder)?.ToString();
            if (state == "Building")
                return MCPVRChatUtil.Fail("The VRChat SDK is already building. Wait for it to finish.");

            var method = builderType.GetMethod(test ? "BuildAndTest" : "Build", isAvatar ? new[] { typeof(GameObject) } : Type.EmptyTypes);
            if (method == null)
                return MCPVRChatUtil.Fail("This VRChat SDK version has no " + (test ? "BuildAndTest" : "Build") + " method in its builder API.");

            var result = new Dictionary<string, object>
            {
                { "projectType", isAvatar ? "avatar" : "world" },
                { "mode", test ? "buildAndTest" : "build" },
                { "platform", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "uploaded", false },
            };
            if (isAvatar) result["avatar"] = avatar.name;
            else result["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;

            // The SDK reports what failed through the console: avatar validation issues are only
            // logged (the exception says "Avatar validation failed"), and so are preprocessor errors.
            var errors = new List<string>();
            Application.LogCallback onLog = (message, stackTrace, type) =>
            {
                if (type == LogType.Log || type == LogType.Warning || errors.Count >= MaxErrors) return;
                errors.Add(message.Length > MaxErrorLength ? message.Substring(0, MaxErrorLength) + "..." : message);
            };

            var timer = Stopwatch.StartNew();
            Application.logMessageReceived += onLog;
            try
            {
                var task = (Task)method.Invoke(builder, isAvatar ? new object[] { avatar } : new object[0]);
                await task;

                result["success"] = true;
                // Task<string> for Build; BuildAndTest returns a plain Task.
                if (task.GetType().GetProperty("Result")?.GetValue(task) is string bundlePath && bundlePath.Length > 0)
                {
                    result["bundlePath"] = bundlePath.Replace('\\', '/');
                    if (File.Exists(bundlePath))
                        result["bundleSizeMB"] = Math.Round(new FileInfo(bundlePath).Length / (1024.0 * 1024.0), 2);
                }
                if (test)
                    result["hint"] = isAvatar
                        ? "Built for local testing: in VRChat on this machine, the avatar is listed with the local test avatars."
                        : "Built and launched in a local VRChat client.";
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
                result["success"] = false;
                result["error"] = ex.Message;
                result["errorType"] = ex.GetType().Name;
                // ValidationException.Errors (world builds throw it; avatar builds only log it).
                if (ex.GetType().GetField("Errors")?.GetValue(ex) is IEnumerable<string> validation)
                {
                    var list = validation.ToList();
                    if (list.Count > 0) result["validationErrors"] = list;
                }
            }
            finally
            {
                Application.logMessageReceived -= onLog;
            }

            result["elapsedMs"] = timer.ElapsedMilliseconds;
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        /// <summary>The SDK's builder, opening the control panel first when it is closed (TryGetBuilder needs it).</summary>
        private static async Task<(object builder, Type builderType, string error)> GetBuilder(string builderTypeName)
        {
            var panelType = MCPVRChatUtil.FindType(PanelType);
            var builderType = MCPVRChatUtil.FindType(builderTypeName);
            var windowField = panelType?.GetField("window", BindingFlags.Public | BindingFlags.Static);
            var tryGet = panelType?.GetMethod("TryGetBuilder", BindingFlags.Public | BindingFlags.Static);
            if (builderType == null || windowField == null || tryGet == null)
                return (null, null, "The VRChat SDK builder API was not found. Update the VRChat SDK (3.2 or later).");

            if (!(windowField.GetValue(null) is UnityEngine.Object panel) || panel == null)
            {
                EditorApplication.ExecuteMenuItem(ShowPanelMenu);
                var timer = Stopwatch.StartNew();
                // The panel opens once VRChat's remote config has loaded, which needs a connection.
                while (!(windowField.GetValue(null) is UnityEngine.Object opened) || opened == null)
                {
                    if (timer.ElapsedMilliseconds > PanelOpenTimeoutMs)
                        return (null, null, $"The VRChat SDK control panel did not open within {PanelOpenTimeoutMs / 1000}s (it loads VRChat's remote config first, which needs a connection). Open it from '{ShowPanelMenu}' and retry.");
                    await Task.Delay(250);
                }
            }

            var callArgs = new object[] { null };
            if (!(bool)tryGet.MakeGenericMethod(builderType).Invoke(null, callArgs) || callArgs[0] == null)
                return (null, null, "The VRChat SDK control panel has no builder for this project type.");
            return (callArgs[0], builderType, null);
        }
    }
}
