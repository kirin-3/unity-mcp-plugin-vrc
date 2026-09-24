using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Detects VRChat project type (avatar, world, none) and installed VRChat SDKs.
    /// </summary>
    public static class MCPVRChatDetect
    {
        public const string TypeAvatar = "avatar";
        public const string TypeWorld = "world";
        public const string TypeNone = "none";

        public const string AvatarsPackage = "com.vrchat.avatars";
        public const string WorldsPackage = "com.vrchat.worlds";
        public const string BasePackage = "com.vrchat.base";

        private static string _testOverrideProjectType = null;

        public static void SetTestOverride(string projectType)
        {
            _testOverrideProjectType = projectType;
        }

        public static void ClearTestOverride()
        {
            _testOverrideProjectType = null;
        }

        public static bool IsVRChatProject()
        {
            string type = GetProjectType();
            return type == TypeAvatar || type == TypeWorld;
        }

        public static bool IsAvatarProject()
        {
            return GetProjectType() == TypeAvatar;
        }

        public static bool IsWorldProject()
        {
            return GetProjectType() == TypeWorld;
        }

        /// <summary>
        /// True for a GameObject in a real loaded scene. Resources.FindObjectsOfTypeAll also returns assets and
        /// preview-scene objects (Scene view lights, prefab-stage clones); scene scans must skip both.
        /// </summary>
        public static bool IsLiveSceneObject(GameObject go)
        {
            return go != null && !EditorUtility.IsPersistent(go) && go.scene.IsValid()
                && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(go.scene);
        }

        public static string GetProjectType()
        {
            if (_testOverrideProjectType != null)
                return _testOverrideProjectType;

            return DetectProjectTypeForPath(GetProjectPath());
        }

        public static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath))
                return Directory.GetCurrentDirectory();

            if (dataPath.EndsWith("/Assets") || dataPath.EndsWith("\\Assets"))
                return dataPath.Substring(0, dataPath.Length - "/Assets".Length);

            return Directory.GetCurrentDirectory();
        }

        public static string DetectProjectTypeForPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
            {
                return DetectFromAssemblies();
            }

            string packagesDir = Path.Combine(projectPath, "Packages");
            if (Directory.Exists(packagesDir))
            {
                // Check vpm-manifest.json
                string vpmManifest = Path.Combine(packagesDir, "vpm-manifest.json");
                if (File.Exists(vpmManifest))
                {
                    try
                    {
                        string content = File.ReadAllText(vpmManifest);
                        if (content.Contains($"\"{AvatarsPackage}\""))
                            return TypeAvatar;
                        if (content.Contains($"\"{WorldsPackage}\""))
                            return TypeWorld;
                    }
                    catch { }
                }

                // Check manifest.json
                string manifest = Path.Combine(packagesDir, "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        string content = File.ReadAllText(manifest);
                        if (content.Contains($"\"{AvatarsPackage}\""))
                            return TypeAvatar;
                        if (content.Contains($"\"{WorldsPackage}\""))
                            return TypeWorld;
                    }
                    catch { }
                }

                // Check packages-lock.json
                string lockFile = Path.Combine(packagesDir, "packages-lock.json");
                if (File.Exists(lockFile))
                {
                    try
                    {
                        string content = File.ReadAllText(lockFile);
                        if (content.Contains($"\"{AvatarsPackage}\""))
                            return TypeAvatar;
                        if (content.Contains($"\"{WorldsPackage}\""))
                            return TypeWorld;
                    }
                    catch { }
                }

                // Check package directories in Packages/
                if (Directory.Exists(Path.Combine(packagesDir, AvatarsPackage)))
                    return TypeAvatar;
                if (Directory.Exists(Path.Combine(packagesDir, WorldsPackage)))
                    return TypeWorld;
            }

            // Also check current loaded assemblies if this is the open project
            string openPath = GetProjectPath();
            if (string.Equals(Path.GetFullPath(projectPath), Path.GetFullPath(openPath), StringComparison.OrdinalIgnoreCase))
            {
                string asmResult = DetectFromAssemblies();
                if (asmResult != TypeNone)
                    return asmResult;
            }

            return TypeNone;
        }

        public static string DetectFromAssemblies()
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name;
                    if (name.StartsWith("VRC.SDK3A") || name.Contains("VRCSDK3A") || name == "VRC.SDK3.Avatars" || name == "VRC.SDK3.Avatars.Editor")
                        return TypeAvatar;
                    if (name.StartsWith("VRC.SDK3W") || name.Contains("VRCSDK3W") || name == "VRC.SDK3.Worlds" || name == "VRC.SDK3.Worlds.Editor")
                        return TypeWorld;
                }
            }
            catch { }

            return TypeNone;
        }

        public static string GetSdkVersion(string projectPath = null)
        {
            string path = projectPath ?? GetProjectPath();
            string projType = GetProjectType();
            string pkgName = projType == TypeAvatar ? AvatarsPackage : (projType == TypeWorld ? WorldsPackage : BasePackage);

            // Try reading package.json of the package
            string pkgJsonPath = Path.Combine(path, "Packages", pkgName, "package.json");
            if (File.Exists(pkgJsonPath))
            {
                try
                {
                    string text = File.ReadAllText(pkgJsonPath);
                    var match = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
                catch { }
            }

            // Try reading vpm-manifest.json
            string vpmManifest = Path.Combine(path, "Packages", "vpm-manifest.json");
            if (File.Exists(vpmManifest))
            {
                try
                {
                    string text = File.ReadAllText(vpmManifest);
                    var match = Regex.Match(text, $"\"{Regex.Escape(pkgName)}\"\\s*:\\s*\\{{\\s*\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
                catch { }
            }

            return null;
        }

        private static Dictionary<string, object> _testOverrideEcosystem = null;

        public static void SetTestEcosystem(Dictionary<string, object> packages)
        {
            _testOverrideEcosystem = packages;
        }

        public static void ClearTestEcosystem()
        {
            _testOverrideEcosystem = null;
        }

        public static Dictionary<string, object> GetProjectContext(string projectPath = null)
        {
            string path = projectPath ?? GetProjectPath();
            string projType = GetProjectType();
            string sdkVer = projType != TypeNone ? GetSdkVersion(path) : null;

            Dictionary<string, object> packages;
            if (_testOverrideEcosystem != null)
            {
                packages = new Dictionary<string, object>(_testOverrideEcosystem);
            }
            else
            {
                packages = GetEcosystemPackages(path);
            }

            return new Dictionary<string, object>
            {
                { "projectType", projType },
                { "sdkVersion", sdkVer },
                { "packages", packages }
            };
        }

        public static Dictionary<string, object> GetEcosystemPackages(string projectPath = null)
        {
            string path = projectPath ?? GetProjectPath();
            var result = new Dictionary<string, object>();

            var packageDefinitions = new (string key, string packageId, string asmName)[]
            {
                ("modularAvatar", "nadena.dev.modular-avatar", "nadena.dev.modular-avatar.core"),
                ("ndmf", "nadena.dev.ndmf", "nadena.dev.ndmf"),
                ("vrcfury", "com.vrcfury.vrcfury", "VRCFury"),
                ("d4rkOptimizer", "d4rkpl4y3r.d4rkavataroptimizer", "d4rkpl4y3r.d4rkavataroptimizer"),
                ("vrWorldToolkit", "dev.onevr.vrworldtoolkit", "VRWorldToolkit")
            };

            foreach (var (key, packageId, asmName) in packageDefinitions)
            {
                bool available = DetectPackage(path, packageId, asmName, out string version);
                result[key] = new Dictionary<string, object>
                {
                    { "available", available },
                    { "version", available ? version : null }
                };
            }

            bool poiAvailable = DetectPoiyomi(path, out string poiVersion);
            result["poiyomi"] = new Dictionary<string, object>
            {
                { "available", poiAvailable },
                { "version", poiAvailable ? poiVersion : null }
            };

            return result;
        }

        public static bool DetectPackage(string projectPath, string packageId, string asmName, out string version)
        {
            version = null;

            if (!string.IsNullOrEmpty(projectPath) && Directory.Exists(projectPath))
            {
                string packagesDir = Path.Combine(projectPath, "Packages");
                if (Directory.Exists(packagesDir))
                {
                    // 1. Check Packages/<packageId>/package.json
                    string pkgJson = Path.Combine(packagesDir, packageId, "package.json");
                    if (File.Exists(pkgJson))
                    {
                        try
                        {
                            string text = File.ReadAllText(pkgJson);
                            var m = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success) version = m.Groups[1].Value;
                            return true;
                        }
                        catch { }
                    }

                    // 2. Check vpm-manifest.json
                    string vpmManifest = Path.Combine(packagesDir, "vpm-manifest.json");
                    if (File.Exists(vpmManifest))
                    {
                        try
                        {
                            string text = File.ReadAllText(vpmManifest);
                            var m = Regex.Match(text, $"\"{Regex.Escape(packageId)}\"\\s*:\\s*\\{{\\s*\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success)
                            {
                                version = m.Groups[1].Value;
                                return true;
                            }
                            if (text.Contains($"\"{packageId}\""))
                                return true;
                        }
                        catch { }
                    }

                    // 3. Check packages-lock.json
                    string lockFile = Path.Combine(packagesDir, "packages-lock.json");
                    if (File.Exists(lockFile))
                    {
                        try
                        {
                            string text = File.ReadAllText(lockFile);
                            var m = Regex.Match(text, $"\"{Regex.Escape(packageId)}\"[^}}]*\"version\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success)
                            {
                                version = m.Groups[1].Value;
                                return true;
                            }
                            if (text.Contains($"\"{packageId}\""))
                                return true;
                        }
                        catch { }
                    }

                    // 4. Check manifest.json
                    string manifest = Path.Combine(packagesDir, "manifest.json");
                    if (File.Exists(manifest))
                    {
                        try
                        {
                            string text = File.ReadAllText(manifest);
                            if (text.Contains($"\"{packageId}\""))
                                return true;
                        }
                        catch { }
                    }

                    // 5. Check directory in Packages/
                    if (Directory.Exists(Path.Combine(packagesDir, packageId)))
                        return true;
                }
            }

            // 6. Check loaded assemblies
            if (!string.IsNullOrEmpty(asmName))
            {
                try
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string name = assembly.GetName().Name;
                        if (string.Equals(name, asmName, StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith(asmName + ".", StringComparison.OrdinalIgnoreCase))
                        {
                            var ver = assembly.GetName().Version;
                            if (ver != null && (ver.Major > 0 || ver.Minor > 0))
                                version = ver.ToString();
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        public static bool DetectPoiyomi(string projectPath, out string version)
        {
            version = null;

            // 1. Runtime shader lookup
            try
            {
                string[] candidateShaders = new[]
                {
                    ".poiyomi/Poiyomi Toon",
                    ".poiyomi/Poiyomi Pro",
                    "poiyomi/Poiyomi Toon",
                    "Poiyomi/Poiyomi Toon",
                    "Poiyomi Toon"
                };

                Shader foundShader = null;
                foreach (var name in candidateShaders)
                {
                    var s = Shader.Find(name);
                    if (s != null)
                    {
                        foundShader = s;
                        break;
                    }
                }

                if (foundShader != null)
                {
                    version = GetPoiyomiVersionFromShader(foundShader);
                    return true;
                }
            }
            catch { }

            // 2. Filesystem check under Assets/
            string path = projectPath ?? GetProjectPath();
            if (!string.IsNullOrEmpty(path))
            {
                string poiDir = Path.Combine(path, "Assets", "_PoiyomiShaders");
                if (!Directory.Exists(poiDir))
                    poiDir = Path.Combine(path, "Assets", "Poiyomi");

                if (Directory.Exists(poiDir))
                {
                    version = TryGetPoiyomiVersionFromDirectory(poiDir);
                    return true;
                }
            }

            return false;
        }

        public static string GetPoiyomiVersionFromShader(Shader shader)
        {
            if (shader == null) return null;
            try
            {
#if UNITY_6000_0_OR_NEWER
                int count = shader.GetPropertyCount();
#else
                int count = ShaderUtil.GetPropertyCount(shader);
#endif
                for (int i = 0; i < count; i++)
                {
#if UNITY_6000_0_OR_NEWER
                    string propName = shader.GetPropertyName(i);
#else
                    string propName = ShaderUtil.GetPropertyName(shader, i);
#endif
                    if (propName == "shader_master_label")
                    {
#if UNITY_6000_0_OR_NEWER
                        string desc = shader.GetPropertyDescription(i);
#else
                        string desc = ShaderUtil.GetPropertyDescription(shader, i);
#endif
                        var match = Regex.Match(desc, @"(?:Poiyomi\s*(?:Toon\s*)?V?|V)\s*([0-9]+(?:\.[0-9]+)+)", RegexOptions.IgnoreCase);
                        if (match.Success)
                            return match.Groups[1].Value;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryGetPoiyomiVersionFromDirectory(string poiDir)
        {
            try
            {
                string versionTxt = Path.Combine(poiDir, "TPS", "VERSION.txt");
                if (File.Exists(versionTxt))
                {
                    string v = File.ReadAllText(versionTxt).Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }

                var shaderFiles = Directory.GetFiles(poiDir, "*.shader", SearchOption.AllDirectories);
                foreach (var sf in shaderFiles)
                {
                    string text = File.ReadAllText(sf);
                    var match = Regex.Match(text, @"shader_master_label\s*\([^,]*,\s*""(?:[^""]*?)(?:Poiyomi\s*(?:Toon\s*)?V?|V)\s*([0-9]+(?:\.[0-9]+)+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }
    }
}
