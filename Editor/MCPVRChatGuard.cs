using System;
using System.Collections.Generic;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Shared safety guard for operations that silently break VRChat projects:
    /// player settings, quality level, physics settings, reserved layer assignment (0-22),
    /// collision matrix changes on reserved layers, and standalone player builds.
    /// Supports a per-call override parameter (override: true).
    /// </summary>
    public static class MCPVRChatGuard
    {
        public const int ReservedLayerMin = 0;
        public const int ReservedLayerMax = 22;

        public static bool IsReservedLayer(int layer)
        {
            return layer >= ReservedLayerMin && layer <= ReservedLayerMax;
        }

        /// <summary>
        /// Only the documented "override" parameter bypasses a guard. No aliases: an
        /// undocumented second spelling (such as "force", which other routes already use
        /// for unrelated purposes) becomes an accidental bypass.
        /// </summary>
        public static bool HasOverride(Dictionary<string, object> args)
        {
            if (args == null) return false;
            if (!args.TryGetValue("override", out var o) || o == null) return false;
            if (o is bool b) return b;
            // A non-boolean value must not throw out of a guard check — anything that is
            // not recognisably true leaves the guard armed.
            return bool.TryParse(o.ToString(), out bool parsed) && parsed;
        }

        public static bool ShouldGuard(Dictionary<string, object> args, out object refusalResult, string reason)
        {
            refusalResult = null;
            if (!MCPVRChatDetect.IsVRChatProject())
                return false;

            if (HasOverride(args))
                return false;

            refusalResult = CreateRefusal(reason);
            return true;
        }

        public static object CreateRefusal(string reason)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "refused", true },
                { "error", reason },
                { "reason", reason },
                { "hint", "This project is a detected VRChat project. To bypass this safety guard for this call only, pass override: true." }
            };
        }

        public static Dictionary<string, object> AnnotateOverride(Dictionary<string, object> result)
        {
            if (result != null && MCPVRChatDetect.IsVRChatProject())
            {
                result["guardOverridden"] = true;
                result["warning"] = "VRChat safety guard was overridden for this operation.";
            }
            return result;
        }
    }
}
