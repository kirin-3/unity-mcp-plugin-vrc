using System.Collections.Generic;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for VRChat tooling and project context.
    /// </summary>
    public static class MCPVRChatCommands
    {
        /// <summary>
        /// Get project context including project type (avatar/world/none), SDK version,
        /// and ecosystem package availability/versions.
        /// </summary>
        public static object GetProjectContext()
        {
            return MCPVRChatDetect.GetProjectContext();
        }
    }
}
