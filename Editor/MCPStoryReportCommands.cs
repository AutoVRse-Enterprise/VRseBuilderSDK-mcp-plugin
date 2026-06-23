using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP command handler for the vrse/story/report route.
    /// Bridges the MCP server to StoryReportEditor (Assets assembly) via reflection
    /// to avoid hard assembly coupling.
    /// </summary>
    public static class MCPStoryReportCommands
    {
        // ══════════════════════════════════════════════
        // TOOL: vrse/story/report
        // ══════════════════════════════════════════════
        public static object GetReport(Dictionary<string, object> args)
        {
            try
            {
                var reportType = FindTypeAcrossAssemblies("VRseBuilder.Tools.Editor.StoryReportEditor");
                if (reportType == null)
                    return new Dictionary<string, object> { { "error", "StoryReportEditor not found" } };

                var method = reportType.GetMethod("GetReportJson", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return new Dictionary<string, object> { { "error", "GetReportJson method not found" } };

                string json = (string)method.Invoke(null, null);
                var parsed = MiniJson.Deserialize(json);
                return parsed ?? new Dictionary<string, object> { { "error", "GetReportJson returned null" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", ex.Message }, { "stackTrace", ex.StackTrace } };
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static Type FindTypeAcrossAssemblies(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
