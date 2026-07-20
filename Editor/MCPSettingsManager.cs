using System.Collections.Generic;
using UnityEditor;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Persistent settings for the MCP Bridge Server, stored via EditorPrefs.
    /// </summary>
    public static class MCPSettingsManager
    {
        private const string Prefix = "UnityMCP_";

        // ─── Categories ───
        private static readonly string[] AllCategories = new[]
        {
            "amplify", "animation", "asmdef", "asset", "audio", "build", "component", "console",
            "debugger", "editor", "gameobject", "input", "lighting", "physics", "prefab",
            "profiler", "project", "renderer", "scene", "script", "selection", "shadergraph", "taglayer",
            "terrain"
        };

        private static Dictionary<string, bool> _enabledCategories;

        // ─── Port ───

        public static int Port
        {
            get => EditorPrefs.GetInt(Prefix + "Port", 7890);
            set => EditorPrefs.SetInt(Prefix + "Port", value);
        }

        /// <summary>
        /// When true, uses the manually configured Port value instead of auto-selecting.
        /// Default is false (auto-select from port range 7890-7899).
        /// </summary>
        public static bool UseManualPort
        {
            get => EditorPrefs.GetBool(Prefix + "UseManualPort", false);
            set => EditorPrefs.SetBool(Prefix + "UseManualPort", value);
        }

        // ─── Auto-Start ───

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(Prefix + "AutoStart", true);
            set => EditorPrefs.SetBool(Prefix + "AutoStart", value);
        }

        // ─── AI Agent Configuration ───

        /// <summary>The client selected in the dashboard's AI agent setup section.</summary>
        public static string AgentClient
        {
            get => EditorPrefs.GetString(Prefix + "AgentClient", "OpenCode");
            set => EditorPrefs.SetString(Prefix + "AgentClient", value);
        }

        /// <summary>The identity the selected client should provide in its X-Agent-Id header.</summary>
        public static string AgentId
        {
            get => EditorPrefs.GetString(Prefix + "AgentId", "opencode");
            set => EditorPrefs.SetString(Prefix + "AgentId", value);
        }

        /// <summary>Git repository URL for the companion MCP server.</summary>
        public static string McpServerRepository
        {
            get => EditorPrefs.GetString(Prefix + "McpServerRepository", "");
            set => EditorPrefs.SetString(Prefix + "McpServerRepository", value);
        }

        /// <summary>Whether agents launch a custom local server or the AutoVRse cloud registry package.</summary>
        public static string McpServerSource
        {
            get => EditorPrefs.GetString(Prefix + "McpServerSource", "Custom");
            set => EditorPrefs.SetString(Prefix + "McpServerSource", value);
        }

        /// <summary>Local directory containing the companion MCP server package.</summary>
        public static string McpServerPath
        {
            get => EditorPrefs.GetString(Prefix + "McpServerPath", "");
            set => EditorPrefs.SetString(Prefix + "McpServerPath", value);
        }

        /// <summary>Command used by agent clients to launch the companion MCP server.</summary>
        public static string McpServerCommand
        {
            get => EditorPrefs.GetString(Prefix + "McpServerCommand", "node");
            set => EditorPrefs.SetString(Prefix + "McpServerCommand", value);
        }

        /// <summary>Server entry point relative to McpServerPath, unless absolute.</summary>
        public static string McpServerEntryPoint
        {
            get => EditorPrefs.GetString(Prefix + "McpServerEntryPoint", "src/index.js");
            set => EditorPrefs.SetString(Prefix + "McpServerEntryPoint", value);
        }

        /// <summary>Existing agent JSON configuration file to update with this MCP server.</summary>
        public static string AgentConfigPath
        {
            get => EditorPrefs.GetString(Prefix + "AgentConfigPath", "");
            set => EditorPrefs.SetString(Prefix + "AgentConfigPath", value);
        }

        /// <summary>Packaged skill IDs selected for installation into the configured agent.</summary>
        public static string[] SelectedSkillIds
        {
            get
            {
                string value = EditorPrefs.GetString(Prefix + "SelectedSkillIds", "");
                return string.IsNullOrEmpty(value) ? new string[0] : value.Split('|');
            }
            set => EditorPrefs.SetString(Prefix + "SelectedSkillIds", string.Join("|", value ?? new string[0]));
        }

        // ─── Project Context ───

        public static bool ContextEnabled
        {
            get => EditorPrefs.GetBool(Prefix + "ContextEnabled", true);
            set => EditorPrefs.SetBool(Prefix + "ContextEnabled", value);
        }

        public static string ContextPath
        {
            get => EditorPrefs.GetString(Prefix + "ContextPath", "Assets/MCP/Context");
            set => EditorPrefs.SetString(Prefix + "ContextPath", value);
        }

        // ─── Action History ───

        public static bool ActionHistoryPersistence
        {
            get => EditorPrefs.GetBool(Prefix + "ActionHistoryPersistence", false);
            set => EditorPrefs.SetBool(Prefix + "ActionHistoryPersistence", value);
        }

        public static int ActionHistoryMaxEntries
        {
            get => EditorPrefs.GetInt(Prefix + "ActionHistoryMaxEntries", 500);
            set => EditorPrefs.SetInt(Prefix + "ActionHistoryMaxEntries", value);
        }

        // ─── Category Management ───

        public static string[] GetAllCategoryNames() => AllCategories;

        public static Dictionary<string, bool> GetEnabledCategories()
        {
            if (_enabledCategories != null) return _enabledCategories;

            _enabledCategories = new Dictionary<string, bool>();
            foreach (var cat in AllCategories)
                _enabledCategories[cat] = true;

            string saved = EditorPrefs.GetString(Prefix + "EnabledCategories", "");
            if (!string.IsNullOrEmpty(saved))
            {
                var parts = saved.Split(',');
                foreach (var part in parts)
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && _enabledCategories.ContainsKey(kv[0]))
                    {
                        bool.TryParse(kv[1], out bool enabled);
                        _enabledCategories[kv[0]] = enabled;
                    }
                }
            }

            return _enabledCategories;
        }

        public static bool IsCategoryEnabled(string category)
        {
            var cats = GetEnabledCategories();
            string lower = category.ToLower();
            return !cats.ContainsKey(lower) || cats[lower];
        }

        public static void SetCategoryEnabled(string category, bool enabled)
        {
            var cats = GetEnabledCategories();
            string lower = category.ToLower();
            if (cats.ContainsKey(lower))
            {
                cats[lower] = enabled;
                SaveEnabledCategories();
            }
        }

        private static void SaveEnabledCategories()
        {
            var parts = new List<string>();
            foreach (var kv in _enabledCategories)
                parts.Add($"{kv.Key}:{kv.Value}");
            EditorPrefs.SetString(Prefix + "EnabledCategories", string.Join(",", parts));
        }

        /// <summary>
        /// Reset all settings to defaults.
        /// </summary>
        public static void ResetToDefaults()
        {
            Port = 7890;
            AutoStart = true;
            AgentClient = "OpenCode";
            AgentId = "opencode";
            McpServerSource = "Custom";
            McpServerRepository = "";
            McpServerPath = "";
            McpServerCommand = "node";
            McpServerEntryPoint = "src/index.js";
            AgentConfigPath = "";
            SelectedSkillIds = new string[0];
            ContextEnabled = true;
            ContextPath = "Assets/MCP/Context";
            ActionHistoryPersistence = false;
            ActionHistoryMaxEntries = 500;
            _enabledCategories = null;
            EditorPrefs.DeleteKey(Prefix + "EnabledCategories");
        }
    }
}
