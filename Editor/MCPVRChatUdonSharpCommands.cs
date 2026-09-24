using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// UdonSharp behaviours for worlds:
    /// - vrc/world/udonsharp/create: write the .cs script and create the UdonSharpProgramAsset that points at
    ///   it (what UdonSharp's own "Create > U# Script" menu does)
    /// - vrc/world/udonsharp/attach: add the compiled behaviour to a GameObject through UdonSharpUndo, which
    ///   also creates the backing UdonBehaviour
    ///
    /// The two are separate calls because the script has to compile (and the domain reload) in between:
    /// the class does not exist until then. UdonSharp is reached through reflection so the plugin still
    /// compiles in avatar projects.
    /// </summary>
    public static class MCPVRChatUdonSharpCommands
    {
        private const string ProgramAssetType = "UdonSharp.UdonSharpProgramAsset";
        private const string BehaviourType = "UdonSharp.UdonSharpBehaviour";
        private const string UndoType = "UdonSharpEditor.UdonSharpUndo";
        private const string SettingsType = "UdonSharpEditor.UdonSharpSettings";
        private const string EditorUtilityType = "UdonSharpEditor.UdonSharpEditorUtility";

        private const string NotInstalled =
            "UdonSharp is not available in this project (UdonSharp.UdonSharpProgramAsset not found). It ships with the VRChat Worlds SDK 3.4 and later.";

        // Same as UdonSharp's built-in template (UdonSharpSettings.DefaultProgramTemplate).
        private const string DefaultTemplate =
            "using UdonSharp;\nusing UnityEngine;\nusing VRC.SDKBase;\nusing VRC.Udon;\n\n" +
            "public class <TemplateClassName> : UdonSharpBehaviour\n{\n    void Start()\n    {\n        \n    }\n}\n";

        // ─────────────────────────────────────────────────────────────
        // vrc/world/udonsharp/create
        // ─────────────────────────────────────────────────────────────

        public static object Create(Dictionary<string, object> args)
        {
            Type programAssetType = MCPVRChatUtil.FindType(ProgramAssetType);
            if (programAssetType == null) return MCPVRChatUtil.Fail(NotInstalled);

            string path = MCPVRChatUtil.GetString(args, "path")?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path))
                return MCPVRChatUtil.Fail("'path' is required, e.g. 'Assets/Scripts/Door.cs'.");
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return MCPVRChatUtil.Fail("'path' must end in .cs.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return MCPVRChatUtil.Fail("UdonSharp scripts must live under Assets/.");
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return MCPVRChatUtil.Fail(pathError);

            string fileName = Path.GetFileNameWithoutExtension(path);
            string className = MCPVRChatUtil.GetString(args, "className")?.Trim();
            if (string.IsNullOrEmpty(className)) className = fileName;
            if (!Regex.IsMatch(className, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return MCPVRChatUtil.Fail($"'{className}' is not a valid C# class name.");
            if (className != fileName)
                return MCPVRChatUtil.Fail($"The class name must match the file name ('{fileName}.cs' holds class {fileName}); Unity maps a script file only to the class of the same name.");

            if (!MCPVRChatUtil.TryReadOptionalBool(args, "overwrite", out bool? overwriteArg, out string err))
                return MCPVRChatUtil.Fail(err);
            bool overwrite = overwriteArg == true;

            string content = MCPVRChatUtil.GetString(args, "content");
            // An existing script given no new content is kept as it is and only gets its program asset
            // (a U# script written with unity_script_create has none). overwrite never turns that into a
            // template rewrite: it may be passed only to replace a conflicting program asset.
            bool keepScript = string.IsNullOrWhiteSpace(content) && File.Exists(fullPath);
            if (keepScript)
            {
                string declarationError = CheckDeclaration(File.ReadAllText(fullPath), className, $"The existing '{path}'");
                if (declarationError != null)
                    return MCPVRChatUtil.Fail(declarationError + " Pass content with overwrite:true to replace it.");
            }
            else if (string.IsNullOrWhiteSpace(content))
            {
                content = Template(className);
            }
            else
            {
                string declarationError = CheckDeclaration(content, className, "'content'");
                if (declarationError != null) return MCPVRChatUtil.Fail(declarationError);
            }

            if (!keepScript)
            {
                // Never silently overwrite a script (source-code loss).
                var overwriteError = MCPAssetSafety.OverwriteGuard(path, args);
                if (overwriteError != null) return overwriteError;
            }

            // The program asset sits next to the script with the class name, like UdonSharp's own menu.
            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
            string assetPath = $"{directory}/{className}.asset";
            var existingProgram = AssetDatabase.LoadAssetAtPath(assetPath, programAssetType);
            if (!overwrite)
            {
                if (existingProgram != null)
                {
                    var source = MCPVRChatUtil.GetFieldValue(existingProgram, "sourceCsScript") as MonoScript;
                    string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
                    if (!string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase))
                        return MCPVRChatUtil.Fail($"'{assetPath}' already exists for {(sourcePath ?? "no script")}. Pass overwrite:true to replace it.");
                }
                else if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                {
                    return MCPVRChatUtil.Fail($"'{assetPath}' already exists and is not an UdonSharp program asset. Pass overwrite:true to replace it.");
                }
            }

            if (!keepScript)
            {
                string scriptDirectory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(scriptDirectory)) Directory.CreateDirectory(scriptDirectory);
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            else if (AssetDatabase.LoadAssetAtPath<MonoScript>(path) == null)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport); // written outside Unity, not imported yet
            }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (script == null)
                return MCPVRChatUtil.Fail($"Unity did not import '{path}' as a script.");

            bool reused = false;
            var program = AssetDatabase.LoadAssetAtPath(assetPath, programAssetType);
            if (program != null && MCPVRChatUtil.GetFieldValue(program, "sourceCsScript") as MonoScript == script)
            {
                reused = true;
            }
            else
            {
                if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath); // only reachable with overwrite:true
                var created = ScriptableObject.CreateInstance(programAssetType);
                if (!MCPVRChatUtil.SetFieldValue(created, "sourceCsScript", script))
                {
                    UnityEngine.Object.DestroyImmediate(created);
                    return MCPVRChatUtil.Fail("UdonSharpProgramAsset has no 'sourceCsScript' field; this UdonSharp version is not one this route knows how to drive.");
                }
                AssetDatabase.CreateAsset(created, assetPath);
            }
            AssetDatabase.SaveAssets();

            // A kept script that already compiled is attachable right away.
            bool compilePending = !keepScript || script.GetClass() == null;
            return new Dictionary<string, object>
            {
                { "success", true },
                { "scriptPath", path },
                { "programAssetPath", assetPath },
                { "className", className },
                { "scriptKept", keepScript },
                { "programAssetReused", reused },
                { "compilePending", compilePending },
                { "hint", compilePending
                    ? "Unity compiles the script next; attach it with unity_vrc_udonsharp_attach once compilation has finished."
                    : "Attach it with unity_vrc_udonsharp_attach." }
            };
        }

        /// <summary>Null when the source declares the file's class as an UdonSharpBehaviour, else what is wrong.</summary>
        private static string CheckDeclaration(string content, string className, string subject)
        {
            string name = Regex.Escape(className);
            if (!Regex.IsMatch(content, $@"\bclass\s+{name}\b"))
                return $"{subject} must declare class {className} (the file name).";
            if (!Regex.IsMatch(content, $@"\bclass\s+{name}\s*:\s*[\w\.]*UdonSharpBehaviour\b"))
                return $"{subject} declares class {className}, which must derive from UdonSharpBehaviour.";
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // vrc/world/udonsharp/attach
        // ─────────────────────────────────────────────────────────────

        public static object Attach(Dictionary<string, object> args)
        {
            Type programAssetType = MCPVRChatUtil.FindType(ProgramAssetType);
            if (programAssetType == null) return MCPVRChatUtil.Fail(NotInstalled);

            MethodInfo addComponent = MCPVRChatUtil.FindType(UndoType)?.GetMethod("AddComponent",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(Type) }, null);
            if (addComponent == null)
                return MCPVRChatUtil.Fail("UdonSharpUndo.AddComponent(GameObject, Type) was not found; this UdonSharp version is not one this route knows how to drive.");

            var program = ResolveProgramAsset(args, programAssetType, out string error);
            if (program == null) return MCPVRChatUtil.Fail(error);
            string programPath = AssetDatabase.GetAssetPath(program);

            string targetPath = MCPVRChatUtil.GetString(args, "targetPath");
            if (string.IsNullOrEmpty(targetPath))
                return MCPVRChatUtil.Fail("'targetPath' is required: the GameObject that gets the behaviour.");
            var target = MCPVRChatWorldCommands.ResolveGameObject(targetPath);
            if (target == null)
                return MCPVRChatUtil.Fail($"GameObject '{targetPath}' was not found in the open scene.");

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return Pending("Scripts are still compiling.");

            var source = MCPVRChatUtil.GetFieldValue(program, "sourceCsScript") as MonoScript;
            if (source == null)
                return MCPVRChatUtil.Fail($"'{programPath}' has no source script assigned.");

            Type behaviourType = source.GetClass();
            if (behaviourType == null)
            {
                if (EditorUtility.scriptCompilationFailed)
                {
                    var failed = MCPVRChatUtil.Fail($"'{source.name}' has not compiled: the project has compile errors. Read them with unity_get_compilation_errors, fix the script, then attach again.");
                    failed["compileFailed"] = true;
                    return failed;
                }
                return Pending($"'{source.name}' has not been compiled yet.");
            }

            Type baseType = MCPVRChatUtil.FindType(BehaviourType);
            if (baseType == null || !baseType.IsAssignableFrom(behaviourType))
                return MCPVRChatUtil.Fail($"{behaviourType.Name} does not derive from UdonSharpBehaviour.");

            string objectPath = MCPVRChatWorldCommands.GetGameObjectPath(target);
            if (target.GetComponent(behaviourType) != null)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "alreadyAttached", true },
                    { "objectPath", objectPath },
                    { "className", behaviourType.Name },
                    { "programAssetPath", programPath }
                };
            }

            Component added;
            try
            {
                added = addComponent.Invoke(null, new object[] { target, behaviourType }) as Component;
            }
            catch (TargetInvocationException ex)
            {
                return MCPVRChatUtil.Fail("UdonSharp could not add the behaviour: " + (ex.InnerException?.Message ?? ex.Message));
            }
            if (added == null)
                return MCPVRChatUtil.Fail("UdonSharp did not add the behaviour.");

            bool hasBacking = false;
            var getBacking = MCPVRChatUtil.FindType(EditorUtilityType)?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetBackingUdonBehaviour" && m.GetParameters().Length == 1);
            if (getBacking != null)
            {
                try { hasBacking = getBacking.Invoke(null, new object[] { added }) is Component backing && backing != null; }
                catch { hasBacking = false; }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectPath", objectPath },
                { "className", behaviourType.Name },
                { "programAssetPath", programPath },
                { "backingUdonBehaviour", hasBacking }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static UnityEngine.Object ResolveProgramAsset(Dictionary<string, object> args, Type programAssetType, out string error)
        {
            error = null;
            string programAssetPath = MCPVRChatUtil.GetString(args, "programAssetPath");
            if (!string.IsNullOrEmpty(programAssetPath))
            {
                var program = AssetDatabase.LoadAssetAtPath(programAssetPath, programAssetType);
                if (program == null) error = $"No UdonSharpProgramAsset at '{programAssetPath}'.";
                return program;
            }

            var programs = AssetDatabase.FindAssets("t:" + programAssetType.Name)
                .Select(guid => AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), programAssetType))
                .Where(p => p != null)
                .ToList();

            string scriptPath = MCPVRChatUtil.GetString(args, "scriptPath");
            if (!string.IsNullOrEmpty(scriptPath))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (script == null)
                {
                    error = $"No script at '{scriptPath}'.";
                    return null;
                }
                var match = programs.FirstOrDefault(p => MCPVRChatUtil.GetFieldValue(p, "sourceCsScript") as MonoScript == script);
                if (match == null)
                    error = $"No UdonSharpProgramAsset points at '{scriptPath}'. Make one with unity_vrc_udonsharp_create and the same path, without content (the script is kept as it is).";
                return match;
            }

            string className = MCPVRChatUtil.GetString(args, "className");
            if (!string.IsNullOrEmpty(className))
            {
                var matches = programs.Where(p => (MCPVRChatUtil.GetFieldValue(p, "sourceCsScript") as MonoScript)?.name == className).ToList();
                if (matches.Count == 1) return matches[0];
                error = matches.Count == 0
                    ? $"No UdonSharpProgramAsset for class '{className}'."
                    : $"{matches.Count} program assets use a script named '{className}'; pass programAssetPath instead ({string.Join(", ", matches.Select(m => AssetDatabase.GetAssetPath(m)))}).";
                return null;
            }

            error = "Pass programAssetPath, scriptPath or className to say which UdonSharp behaviour to attach.";
            return null;
        }

        private static string Template(string className)
        {
            try
            {
                var template = MCPVRChatUtil.FindType(SettingsType)?.GetMethod("GetProgramTemplateString",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (template?.Invoke(null, new object[] { className }) is string fromSettings && fromSettings.Contains(className))
                    return fromSettings;
            }
            catch
            {
                // Fall back to the built-in template.
            }
            return DefaultTemplate.Replace("<TemplateClassName>", className);
        }

        private static Dictionary<string, object> Pending(string message)
        {
            var result = MCPVRChatUtil.Fail(message + " Retry in a few seconds.");
            result["pending"] = true;
            return result;
        }
    }
}
