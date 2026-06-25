using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP command handler for the vrse/ui/general-setup route.
    /// Bridges the MCP server to GeneralUISetupEditor (Assets assembly) via reflection
    /// to avoid hard assembly coupling.
    /// </summary>
    public static class MCPGeneralUISetupCommands
    {
        // ══════════════════════════════════════════════
        // TOOL: vrse/ui/general-setup
        // ══════════════════════════════════════════════
        public static object Execute(Dictionary<string, object> args)
        {
            try
            {
                var editorType = FindTypeAcrossAssemblies("VRseBuilder.Editor.MCPTools.GeneralUISetupEditor");
                if (editorType == null)
                    return new Dictionary<string, object> { { "error", "GeneralUISetupEditor not found" } };

                var method = editorType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return new Dictionary<string, object> { { "error", "Execute method not found" } };

                string body = MiniJson.Serialize(args);
                string json = (string)method.Invoke(null, new object[] { body });
                var parsed = MiniJson.Deserialize(json);
                return parsed ?? new Dictionary<string, object> { { "error", "Execute returned null" } };
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
