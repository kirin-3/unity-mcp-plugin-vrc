using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// The EditMode tests exercise internal helpers (bone matching, audit checks) directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AnkleBreaker.UnityMCP.Editor.Tests")]

namespace UnityMCP.Editor
{
    /// <summary>
    /// Shared helpers for the VRChat routes that drive third-party packages (VRCFury, Modular Avatar,
    /// Gesture Manager, Av3Emulator, UdonSharp). Those packages are reached through reflection so the
    /// plugin still compiles in projects that have none of them, which makes argument coercion, type
    /// lookup and read-back of reflected objects recurring work.
    /// </summary>
    internal static class MCPVRChatUtil
    {
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        /// <summary>Type by full name from any loaded assembly (null when the package is absent).</summary>
        internal static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_typeCache.TryGetValue(fullName, out var cached)) return cached;

            Type t = Type.GetType(fullName);
            if (t == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = assembly.GetType(fullName); }
                    catch { t = null; }
                    if (t != null) break;
                }
            }

            // Only hits are cached: a package installed later arrives with a domain reload anyway,
            // but a cached miss must never outlive the assembly that would have answered it.
            if (t != null) _typeCache[fullName] = t;
            return t;
        }

        /// <summary>First loaded type whose simple name matches (for types that moved namespaces between versions).</summary>
        internal static Type FindTypeBySimpleName(string simpleName, Func<Type, bool> predicate = null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t != null && t.Name == simpleName && (predicate == null || predicate(t)))
                        return t;
                }
            }
            return null;
        }

        // ─── Arguments ───

        internal static bool Has(Dictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out var v) && v != null;
        }

        internal static string GetString(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return null;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        internal static bool TryReadBool(object v, out bool result)
        {
            result = false;
            switch (v)
            {
                case null: return false;
                case bool b: result = b; return true;
                case int i when i == 0 || i == 1: result = i == 1; return true;
                case long l when l == 0 || l == 1: result = l == 1; return true;
                case double d when d == 0d || d == 1d: result = d == 1d; return true;
                case string s:
                    if (bool.TryParse(s.Trim(), out result)) return true;
                    if (s.Trim() == "1") { result = true; return true; }
                    if (s.Trim() == "0") { result = false; return true; }
                    return false;
                default: return false;
            }
        }

        internal static bool TryReadFloat(object v, out float result)
        {
            result = 0f;
            double d;
            switch (v)
            {
                case null: return false;
                case bool b: result = b ? 1f : 0f; return true;
                case int i: d = i; break;
                case long l: d = l; break;
                case float f: d = f; break;
                case double dd: d = dd; break;
                case string s:
                    if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
                    break;
                default: return false;
            }
            if (double.IsNaN(d) || double.IsInfinity(d) || d > float.MaxValue || d < float.MinValue) return false;
            result = (float)d;
            return true;
        }

        internal static bool TryReadInt(object v, out int result)
        {
            result = 0;
            switch (v)
            {
                case null: return false;
                case int i: result = i; return true;
                case long l when l >= int.MinValue && l <= int.MaxValue: result = (int)l; return true;
                case double d when d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue: result = (int)d; return true;
                case float f when f == Mathf.Floor(f) && f >= int.MinValue && f <= int.MaxValue: result = (int)f; return true;
                case string s: return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                default: return false;
            }
        }

        /// <summary>
        /// Optional boolean argument. Absent → true with a null value. Present but not a boolean → false
        /// with an error: a mistyped flag must not silently fall back to the default.
        /// </summary>
        internal static bool TryReadOptionalBool(Dictionary<string, object> args, string key, out bool? value, out string error)
        {
            value = null;
            error = null;
            if (!Has(args, key)) return true;
            if (TryReadBool(args[key], out bool b)) { value = b; return true; }
            error = $"'{key}' must be true or false (got '{args[key]}').";
            return false;
        }

        // ─── Hierarchy ───

        /// <summary>
        /// Path relative to <paramref name="root"/>. Also accepts the path with the root's own name in
        /// front ("Avatar/Armature/Hips"), since agents often copy full hierarchy paths.
        /// Returns null when not found; the root itself for "" is NOT returned (callers treat an empty
        /// path as "argument absent").
        /// </summary>
        internal static Transform FindUnder(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            path = path.Trim().Trim('/');
            if (path.Length == 0) return null;

            var found = root.Find(path);
            if (found != null) return found;

            if (path == root.name) return root;
            if (path.StartsWith(root.name + "/", StringComparison.Ordinal))
                return root.Find(path.Substring(root.name.Length + 1));
            return null;
        }

        /// <summary>Animation-style relative path ("" for the root itself).</summary>
        internal static string RelPath(Transform root, Transform target)
        {
            if (root == null || target == null || target == root) return "";
            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            if (current == null) return null; // not under root
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Lowercase letters and digits only: "Upper_Leg.L" → "upperlegl". Used for fuzzy name matching.</summary>
        internal static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = new List<char>(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

        /// <summary>Up to <paramref name="max"/> candidates that look like <paramref name="input"/> (for "did you mean").</summary>
        internal static List<string> Suggest(string input, IEnumerable<string> candidates, int max = 5)
        {
            string needle = NormalizeName(input);
            if (needle.Length == 0) return new List<string>();

            var scored = new List<(string name, int score)>();
            foreach (var candidate in candidates.Distinct())
            {
                if (candidate == null) continue;
                string c = NormalizeName(candidate);
                if (c.Length == 0) continue;
                int score;
                if (c == needle) score = 0;
                // Substring hits need 3+ letters on the short side, or a shape named "E" matches every typo.
                else if (Math.Min(c.Length, needle.Length) >= 3 && (c.Contains(needle) || needle.Contains(c)))
                    score = 1 + Math.Abs(c.Length - needle.Length);
                else
                {
                    int distance = Levenshtein(needle, c);
                    if (distance > Math.Max(2, needle.Length / 3)) continue;
                    score = 100 + distance;
                }
                scored.Add((candidate, score));
            }
            return scored.OrderBy(s => s.score).ThenBy(s => s.name, StringComparer.Ordinal)
                .Take(max).Select(s => s.name).ToList();
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                var swap = prev; prev = cur; cur = swap;
            }
            return prev[b.Length];
        }

        // ─── Reflection access ───

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static FieldInfo GetField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, InstanceFields | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        internal static object GetFieldValue(object obj, string name)
        {
            if (obj == null) return null;
            return GetField(obj.GetType(), name)?.GetValue(obj);
        }

        /// <summary>Sets a field, converting enums from their name. Returns false when the field does not exist.</summary>
        internal static bool SetFieldValue(object obj, string name, object value)
        {
            if (obj == null) return false;
            var field = GetField(obj.GetType(), name);
            if (field == null) return false;
            if (value is string s && field.FieldType.IsEnum)
                value = Enum.Parse(field.FieldType, s, true);
            field.SetValue(obj, value);
            return true;
        }

        // ─── Read-back of reflected objects ───

        /// <summary>
        /// JSON-friendly view of a plain serializable object (a VRCFury feature, an emulator parameter…):
        /// serialized fields only, obsolete and empty ones dropped, Unity objects as avatar-relative paths
        /// or asset paths, enums by name. Bounded in depth and list length.
        /// </summary>
        internal static object DumpManaged(object value, Transform avatarRoot, int depth = 0)
        {
            if (value == null) return null;
            if (depth > 8) return "…";

            switch (value)
            {
                case string s: return s;
                case bool b: return b;
                case int _: case long _: case short _: case byte _: case uint _: case ulong _: case ushort _: case sbyte _:
                    return value;
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? null : (object)f;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)d;
                case Enum e: return e.ToString();
                case UnityEngine.Object uo: return DescribeObjectRef(uo, avatarRoot);
                case Vector2 v2: return new List<object> { v2.x, v2.y };
                case Vector3 v3: return new List<object> { v3.x, v3.y, v3.z };
                case Vector4 v4: return new List<object> { v4.x, v4.y, v4.z, v4.w };
                case Quaternion q: return new List<object> { q.x, q.y, q.z, q.w };
                case Color c: return new List<object> { c.r, c.g, c.b, c.a };
            }

            if (value is IDictionary dictionary)
            {
                var outDict = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (outDict.Count >= 200) break;
                    outDict[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = DumpManaged(entry.Value, avatarRoot, depth + 1);
                }
                return outDict;
            }

            if (value is IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                {
                    if (list.Count >= 200) { list.Add("…"); break; }
                    list.Add(DumpManaged(item, avatarRoot, depth + 1));
                }
                return list;
            }

            var type = value.GetType();

            // VRCFury's Guid* wrappers (GuidMaterial, GuidAnimationClip…) carry the object in objRef.
            if (type.BaseType != null && type.BaseType.Name.StartsWith("GuidWrapper", StringComparison.Ordinal))
            {
                var objRef = GetFieldValue(value, "objRef") as UnityEngine.Object;
                return DescribeObjectRef(objRef, avatarRoot);
            }

            var result = new Dictionary<string, object> { { "$type", type.Name } };
            DumpSerializedFields(value, typeof(object), avatarRoot, depth, result);
            return result;
        }

        /// <summary>
        /// Serialized fields of a component (a Modular Avatar component, say), stopping at MonoBehaviour so
        /// Unity's own bookkeeping stays out.
        /// </summary>
        internal static Dictionary<string, object> DumpComponentFields(Component component, Transform avatarRoot)
        {
            var result = new Dictionary<string, object>();
            if (component == null) return result;
            DumpSerializedFields(component, typeof(MonoBehaviour), avatarRoot, 0, result);
            return result;
        }

        private static void DumpSerializedFields(object value, Type stopAt, Transform avatarRoot, int depth, Dictionary<string, object> result)
        {
            var seen = new HashSet<string>();
            for (var t = value.GetType(); t != null && t != stopAt && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
                {
                    if (!seen.Add(field.Name)) continue;
                    if (field.IsNotSerialized) continue;
                    if (!field.IsPublic
                        && field.GetCustomAttribute<SerializeField>() == null
                        && field.GetCustomAttribute<SerializeReference>() == null) continue;
                    if (field.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                    if (field.Name == "version") continue;

                    object fieldValue;
                    try { fieldValue = field.GetValue(value); }
                    catch { continue; }

                    if (fieldValue == null) continue;
                    if (fieldValue is UnityEngine.Object uo && uo == null) continue;
                    if (fieldValue is string str && str.Length == 0) continue;
                    if (fieldValue is ICollection collection && collection.Count == 0) continue;

                    result[field.Name] = DumpManaged(fieldValue, avatarRoot, depth + 1);
                }
            }
        }

        /// <summary>Scene objects as avatar-relative paths ("Armature/Hips" or "Body (SkinnedMeshRenderer)"), assets by path.</summary>
        internal static object DescribeObjectRef(UnityEngine.Object uo, Transform avatarRoot)
        {
            if (uo == null) return null;

            Transform t = null;
            string componentType = null;
            if (uo is GameObject go) t = go.transform;
            else if (uo is Component component)
            {
                t = component.transform;
                if (!(component is Transform)) componentType = component.GetType().Name;
            }

            if (t != null && !EditorUtility.IsPersistent(uo))
            {
                string path = avatarRoot != null ? RelPath(avatarRoot, t) : null;
                if (path == null) path = MCPVRChatWorldCommands.GetGameObjectPath(t.gameObject);
                else if (path.Length == 0) path = t.name;
                return componentType != null ? $"{path} ({componentType})" : path;
            }

            string assetPath = AssetDatabase.GetAssetPath(uo);
            if (string.IsNullOrEmpty(assetPath)) return uo.name;
            return AssetDatabase.IsMainAsset(uo) ? assetPath : $"{assetPath} ({uo.name})";
        }

        // ─── Results ───

        internal static Dictionary<string, object> Fail(string message)
        {
            return new Dictionary<string, object> { { "success", false }, { "error", message } };
        }
    }
}
