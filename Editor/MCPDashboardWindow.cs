using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Editor window providing an overview of VRseBuilder Unity MCP status, feature categories,
    /// server controls, queue monitoring, settings, and active agent sessions.
    /// Accessible via Window > VRseBuilder Unity MCP.
    /// </summary>
    public class MCPDashboardWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private bool _settingsFoldout = false;
        private bool _agentsFoldout = true;
        private bool _categoriesFoldout = true;
        private bool _queueFoldout = true;
        private bool _contextFoldout = true;
        private bool _agentConfigurationFoldout = false;
        private bool _testsFoldout = true;
        private bool _recentActionsFoldout = true;
        private string _expandedTestCategory = null;
        private System.Diagnostics.Process _cloneProcess;
        private string _cloneTargetPath;
        private string _cloneStatus;
        private string _agentConfigStatus;
        private bool _cloneFailed;
        private bool _agentConfigFailed;
        private bool _advancedServerFoldout;
        private bool _configPreviewFoldout;
        private bool _customServerSetupFoldout;
        private bool _agentTroubleshootingFoldout;

        private static readonly string[] AgentClients =
        {
            "OpenCode", "Claude Code", "Claude Desktop", "Codex", "Cursor",
            "GitHub Copilot", "Visual Studio Code", "Other"
        };

        private static readonly string[] McpServerSources = { "Custom", "Cloud" };
        private const string CloudMcpRegistry = "https://npm.autovrse.app";
        private const string CloudMcpPackage = "unity-mcp-server";

        private static readonly Color ColorGreen  = new Color(0.2f, 0.8f, 0.2f);
        private static readonly Color ColorRed    = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color ColorYellow = new Color(0.9f, 0.8f, 0.1f);
        private static readonly Color ColorGrey   = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color ColorBlue   = new Color(0.4f, 0.7f, 1.0f);

        private GUIStyle _headerStyle;
        private GUIStyle _subHeaderStyle;
        private GUIStyle _dotStyle;
        private bool _stylesInitialized;

        [MenuItem("Window/VRseBuilder Unity MCP")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPDashboardWindow>("VRseBuilder Unity MCP");
            window.minSize = new Vector2(340, 500);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.largeLabel)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
            };

            _subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
            };

            _dotStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                fixedWidth = 22,
            };

            _stylesInitialized = true;
        }

        private void OnInspectorUpdate()
        {
            UpdateCloneStatus();

            // Repaint periodically for live status
            Repaint();
        }

        private void OnEnable()
        {
            AutoLocateAgentConfig(MCPSettingsManager.AgentClient == "OpenCode");
        }

        private void OnGUI()
        {
            InitStyles();
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(6);
            DrawConnectionStatus();
            EditorGUILayout.Space(4);
            DrawServerControls();
            EditorGUILayout.Space(8);
            DrawQueueStatus();
            EditorGUILayout.Space(8);
            DrawProjectContext();
            EditorGUILayout.Space(8);
            DrawAgentConfiguration();
            EditorGUILayout.Space(8);
            DrawAgentSessions();
            EditorGUILayout.Space(8);
            DrawRecentActions();
            EditorGUILayout.Space(8);
            DrawCategoryStatus();
            EditorGUILayout.Space(8);
            DrawSettings();
            EditorGUILayout.Space(8);
            DrawVersionInfo();

            EditorGUILayout.EndScrollView();
        }

        // ─── Header ───

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("VRseBuilder Unity MCP", _headerStyle, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ─── Connection Status ───

        private void DrawConnectionStatus()
        {
            bool running = MCPBridgeServer.IsRunning;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Status dot
            var prevColor = GUI.color;
            GUI.color = running ? ColorGreen : ColorRed;
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = prevColor;

            EditorGUILayout.LabelField(
                running ? "Server Running" : "Server Stopped",
                EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            // Show actual active port when running, settings port when stopped
            int displayPort = running ? MCPBridgeServer.ActivePort : MCPSettingsManager.Port;
            string portLabel = running && !MCPSettingsManager.UseManualPort
                ? $"Port {displayPort} (auto)"
                : $"Port {displayPort}";
            EditorGUILayout.LabelField(portLabel, GUILayout.Width(100));

            // Cache values once per event to prevent Layout/Repaint mismatch.
            // Using local bools ensures the same controls exist in both passes.
            int agents = MCPRequestQueue.ActiveSessionCount;
            int queued = MCPRequestQueue.TotalQueuedCount;
            bool showAgents = agents > 0;
            bool showQueued = queued > 0;

            // Always draw the same number of controls regardless of state —
            // hide them with alpha when inactive to avoid IMGUI control count mismatch.
            var savedAlpha = GUI.color.a;

            // Agent count indicator
            GUI.color = showAgents ? ColorGreen : new Color(0, 0, 0, 0);
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = showAgents ? new Color(prevColor.r, prevColor.g, prevColor.b, savedAlpha) : new Color(0, 0, 0, 0);
            EditorGUILayout.LabelField(showAgents ? $"{agents} agent{(agents > 1 ? "s" : "")}" : "", GUILayout.Width(65));

            // Queue count indicator
            GUI.color = showQueued ? ColorYellow : new Color(0, 0, 0, 0);
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = showQueued ? new Color(prevColor.r, prevColor.g, prevColor.b, savedAlpha) : new Color(0, 0, 0, 0);
            EditorGUILayout.LabelField(showQueued ? $"{queued} queued" : "", GUILayout.Width(65));

            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            // ParrelSync clone indicator (shown below the main status bar)
            if (MCPInstanceRegistry.IsParrelSyncClone())
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(24);
                var cloneStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = ColorBlue },
                    fontStyle = FontStyle.Italic,
                };
                int cloneIdx = MCPInstanceRegistry.GetParrelSyncCloneIndex();
                EditorGUILayout.LabelField(
                    $"\u2937 ParrelSync Clone #{cloneIdx}",
                    cloneStyle);
                EditorGUILayout.EndHorizontal();
            }
        }

        // ─── Server Controls ───

        private void DrawServerControls()
        {
            EditorGUILayout.BeginHorizontal();

            bool running = MCPBridgeServer.IsRunning;

            GUI.enabled = !running;
            if (GUILayout.Button("Start", GUILayout.Height(24)))
                MCPBridgeServer.Start();

            GUI.enabled = running;
            if (GUILayout.Button("Stop", GUILayout.Height(24)))
                MCPBridgeServer.Stop();

            GUI.enabled = true;
            if (GUILayout.Button("Restart", GUILayout.Height(24)))
            {
                MCPBridgeServer.Stop();
                EditorApplication.delayCall += () => MCPBridgeServer.Start();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─── Queue Status (Multi-Agent) ───

        private void DrawQueueStatus()
        {
            _queueFoldout = EditorGUILayout.Foldout(_queueFoldout, "Request Queue", true, EditorStyles.foldoutHeader);
            if (!_queueFoldout) return;

            var queueInfo = MCPRequestQueue.GetQueueInfo();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Summary row
            EditorGUILayout.BeginHorizontal();

            int totalQueued = 0;
            if (queueInfo.ContainsKey("totalQueued"))
                int.TryParse(queueInfo["totalQueued"].ToString(), out totalQueued);

            int activeAgents = 0;
            if (queueInfo.ContainsKey("activeAgents"))
                int.TryParse(queueInfo["activeAgents"].ToString(), out activeAgents);

            int cacheSize = 0;
            if (queueInfo.ContainsKey("completedCacheSize"))
                int.TryParse(queueInfo["completedCacheSize"].ToString(), out cacheSize);

            var prevColor = GUI.color;
            GUI.color = totalQueued > 0 ? ColorYellow : ColorGreen;
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = prevColor;

            string statusText = totalQueued > 0
                ? $"{totalQueued} pending  |  {activeAgents} agents  |  {cacheSize} cached"
                : $"Idle  |  {activeAgents} agents  |  {cacheSize} cached";
            EditorGUILayout.LabelField(statusText, EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();

            // Per-agent breakdown (if any queued)
            if (queueInfo.ContainsKey("perAgentQueued") && queueInfo["perAgentQueued"] is Dictionary<string, object> perAgent)
            {
                if (perAgent.Count > 0)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Per-Agent Queue Depth:", EditorStyles.miniLabel);

                    foreach (var kvp in perAgent)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(24);

                        int depth = 0;
                        int.TryParse(kvp.Value.ToString(), out depth);

                        var agentColor = depth > 0 ? ColorYellow : ColorGreen;
                        GUI.color = agentColor;
                        GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                        GUI.color = prevColor;

                        EditorGUILayout.LabelField(kvp.Key, GUILayout.Width(160));
                        EditorGUILayout.LabelField($"{depth} pending", GUILayout.Width(80));

                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Project Context ───

        private void DrawProjectContext()
        {
            _contextFoldout = EditorGUILayout.Foldout(_contextFoldout, "Project Context", true, EditorStyles.foldoutHeader);
            if (!_contextFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Enabled toggle
            EditorGUILayout.BeginHorizontal();
            bool enabled = EditorGUILayout.Toggle("Enable Context", MCPSettingsManager.ContextEnabled);
            if (enabled != MCPSettingsManager.ContextEnabled)
                MCPSettingsManager.ContextEnabled = enabled;
            GUILayout.FlexibleSpace();

            // Buttons
            if (GUILayout.Button("Create Templates", GUILayout.Width(110), GUILayout.Height(18)))
            {
                int created = MCPContextManager.CreateDefaultTemplates();
                if (created > 0)
                    EditorUtility.DisplayDialog("Templates Created",
                        $"Created {created} template file(s) in:\n{MCPSettingsManager.ContextPath}", "OK");
                else
                    EditorUtility.DisplayDialog("Templates Exist",
                        "All template files already exist.", "OK");
            }

            if (GUILayout.Button("Open Folder", GUILayout.Width(90), GUILayout.Height(18)))
            {
                string folderPath = MCPContextManager.GetContextFolderPath();
                if (System.IO.Directory.Exists(folderPath))
                    EditorUtility.RevealInFinder(folderPath);
                else
                    EditorUtility.DisplayDialog("Folder Not Found",
                        $"Context folder does not exist yet.\nClick 'Create Templates' to set it up.\n\n{folderPath}", "OK");
            }

            EditorGUILayout.EndHorizontal();

            if (!enabled)
            {
                EditorGUILayout.HelpBox("Project context is disabled. Agents will not receive project documentation.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // Path display
            EditorGUILayout.LabelField("Path:", MCPSettingsManager.ContextPath, EditorStyles.miniLabel);

            // File list
            var files = MCPContextManager.GetContextFileList();
            bool anyFiles = false;

            foreach (var file in files)
            {
                if (!file.IsStandard && !file.Exists) continue; // Don't show missing custom files

                anyFiles = true;
                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                if (file.Exists && file.SizeBytes > 0)
                    GUI.color = ColorGreen;
                else if (file.Exists)
                    GUI.color = ColorYellow;
                else
                    GUI.color = ColorGrey;

                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                string displayName = file.Category;
                EditorGUILayout.LabelField(displayName, GUILayout.MinWidth(140));

                if (file.Exists)
                {
                    string sizeLabel = file.SizeBytes > 1024
                        ? $"{file.SizeBytes / 1024f:0.#} KB"
                        : $"{file.SizeBytes} B";
                    EditorGUILayout.LabelField(
                        file.SizeBytes == 0 ? "empty" : sizeLabel,
                        EditorStyles.miniLabel, GUILayout.Width(60));
                }
                else
                {
                    EditorGUILayout.LabelField("not created", EditorStyles.miniLabel, GUILayout.Width(60));
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            if (!anyFiles)
            {
                EditorGUILayout.HelpBox(
                    "No context files found. Click 'Create Templates' to get started.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        // ─── AI Agent Configuration ───

        private void DrawAgentConfiguration()
        {
            _agentConfigurationFoldout = EditorGUILayout.Foldout(
                _agentConfigurationFoldout, "AI Agent Configuration", true, EditorStyles.foldoutHeader);
            if (!_agentConfigurationFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool usingCloud = IsCloudMcpServer();
            bool serverLocated = usingCloud || HasMcpServerPackage();
            bool entryPointLocated = usingCloud || HasServerEntryPoint();
            bool agentConfigLocated = System.IO.File.Exists(MCPSettingsManager.AgentConfigPath);
            bool agentConfigured = IsSelectedAgentConfigured();
            DrawAgentConnection(agentConfigured);

            EditorGUILayout.Space(6);
            DrawAgentIdentity();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(MCPSettingsManager.AgentClient, _subHeaderStyle);
            GUILayout.FlexibleSpace();
            var previousColor = GUI.color;
            GUI.color = agentConfigured ? ColorGreen : ColorYellow;
            EditorGUILayout.LabelField(agentConfigured ? "Configured" : "Not configured", EditorStyles.miniLabel,
                GUILayout.Width(90));
            GUI.color = previousColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawServerSetup(serverLocated, entryPointLocated);
            EditorGUILayout.Space(6);
            DrawAgentConfigInstall(serverLocated, entryPointLocated, agentConfigLocated, agentConfigured);
            EditorGUILayout.Space(4);
            DrawAgentTroubleshooting();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndVertical();
        }

        private void DrawAgentConnection(bool agentConfigured)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Connection", _subHeaderStyle);
            GUILayout.FlexibleSpace();

            int sourceIndex = IsCloudMcpServer() ? 1 : 0;
            int newSourceIndex = GUILayout.Toolbar(sourceIndex, McpServerSources, GUILayout.Width(135));
            if (newSourceIndex != sourceIndex)
                MCPSettingsManager.McpServerSource = McpServerSources[newSourceIndex];
            EditorGUILayout.EndHorizontal();

            DrawAgentConnectionRow(
                MCPBridgeServer.IsRunning ? ColorGreen : ColorRed,
                MCPBridgeServer.IsRunning ? "Unity: Connected" : "Unity: Server stopped");
            DrawAgentConnectionRow(
                agentConfigured ? ColorGreen : ColorYellow,
                "AI agent: " + MCPSettingsManager.AgentClient +
                (agentConfigured ? " configured" : " needs configuration"));
        }

        private void DrawAgentConnectionRow(Color color, string label)
        {
            EditorGUILayout.BeginHorizontal();
            var previousColor = GUI.color;
            GUI.color = color;
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = previousColor;
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAgentIdentity()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("AI agent", _subHeaderStyle, GUILayout.Width(70));
            int selectedIndex = System.Array.IndexOf(AgentClients, MCPSettingsManager.AgentClient);
            if (selectedIndex < 0) selectedIndex = AgentClients.Length - 1;

            int newSelectedIndex = EditorGUILayout.Popup(selectedIndex, AgentClients);
            if (newSelectedIndex != selectedIndex)
            {
                string client = AgentClients[newSelectedIndex];
                MCPSettingsManager.AgentClient = client;

                string defaultAgentId = GetDefaultAgentId(client);
                if (!string.IsNullOrEmpty(defaultAgentId))
                    MCPSettingsManager.AgentId = defaultAgentId;

                AutoLocateAgentConfig(true);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawServerSetup(bool serverLocated, bool entryPointLocated)
        {
            EditorGUILayout.LabelField("Model Context Protocol (MCP)", EditorStyles.boldLabel);

            if (IsCloudMcpServer())
            {
                DrawCloudMcpServerSetup();
                return;
            }

            if (serverLocated)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Custom server", EditorStyles.miniLabel, GUILayout.Width(85));
                EditorGUILayout.SelectableLabel(MCPSettingsManager.McpServerPath, EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Change", GUILayout.Width(60)))
                {
                    string selectedPath = EditorUtility.OpenFolderPanel(
                        "Locate MCP Server", MCPSettingsManager.McpServerPath, "");
                    if (!string.IsNullOrEmpty(selectedPath))
                        MCPSettingsManager.McpServerPath = selectedPath;
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("Choose a local server folder or clone one from its repository.", MessageType.None);
                _customServerSetupFoldout = true;
            }

            _customServerSetupFoldout = EditorGUILayout.Foldout(
                _customServerSetupFoldout, "Custom server setup", true);
            if (_customServerSetupFoldout)
            {
                EditorGUILayout.LabelField("Repository URL", EditorStyles.miniLabel);
                string repository = EditorGUILayout.TextField(MCPSettingsManager.McpServerRepository);
                if (repository != MCPSettingsManager.McpServerRepository)
                    MCPSettingsManager.McpServerRepository = repository;

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Locate Server Folder", GUILayout.Height(20)))
                {
                    string selectedPath = EditorUtility.OpenFolderPanel(
                        "Locate MCP Server", MCPSettingsManager.McpServerPath, "");
                    if (!string.IsNullOrEmpty(selectedPath))
                        MCPSettingsManager.McpServerPath = selectedPath;
                }

                GUI.enabled = _cloneProcess == null && !string.IsNullOrWhiteSpace(MCPSettingsManager.McpServerRepository);
                if (GUILayout.Button(_cloneProcess == null ? "Clone Repository" : "Cloning...", GUILayout.Height(20)))
                    CloneMcpServer();
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();
            }

            int port = MCPBridgeServer.IsRunning ? MCPBridgeServer.ActivePort : MCPSettingsManager.Port;
            string endpoint = $"http://127.0.0.1:{port}";
            string bridgeStatus = MCPBridgeServer.IsRunning ? "Unity bridge running" : "Unity bridge stopped";
            EditorGUILayout.LabelField(bridgeStatus + " at " + endpoint, EditorStyles.miniLabel);

            _advancedServerFoldout = EditorGUILayout.Foldout(
                _advancedServerFoldout,
                "Advanced local launch settings",
                true);
            if (_advancedServerFoldout)
            {
                string command = EditorGUILayout.TextField("Launch Command", MCPSettingsManager.McpServerCommand);
                if (command != MCPSettingsManager.McpServerCommand)
                    MCPSettingsManager.McpServerCommand = command;

                string entryPoint = EditorGUILayout.TextField("Entry Point", MCPSettingsManager.McpServerEntryPoint);
                if (entryPoint != MCPSettingsManager.McpServerEntryPoint)
                    MCPSettingsManager.McpServerEntryPoint = entryPoint;
            }

            if (serverLocated && !entryPointLocated)
            {
                EditorGUILayout.HelpBox(
                    "The configured entry point was not found. Update it in Advanced launch settings before configuring an agent.",
                    MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(_cloneStatus))
                EditorGUILayout.HelpBox(_cloneStatus, _cloneFailed ? MessageType.Warning : MessageType.Info);

        }

        private void DrawCloudMcpServerSetup()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Cloud package", EditorStyles.miniLabel, GUILayout.Width(85));
            EditorGUILayout.SelectableLabel(
                "npx -y --registry=" + CloudMcpRegistry + " " + CloudMcpPackage,
                EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            int port = MCPBridgeServer.IsRunning ? MCPBridgeServer.ActivePort : MCPSettingsManager.Port;
            string bridgeStatus = MCPBridgeServer.IsRunning ? "Unity bridge running" : "Unity bridge stopped";
            EditorGUILayout.LabelField(bridgeStatus + " at http://127.0.0.1:" + port, EditorStyles.miniLabel);
        }

        private void DrawAgentConfigInstall(bool serverLocated, bool entryPointLocated, bool agentConfigLocated, bool agentConfigured)
        {
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            string configPath = EditorGUILayout.TextField(MCPSettingsManager.AgentConfigPath);
            if (configPath != MCPSettingsManager.AgentConfigPath)
            {
                MCPSettingsManager.AgentConfigPath = configPath;
                _agentConfigStatus = null;
                _agentConfigFailed = false;
            }

            if (GUILayout.Button("Find", GUILayout.Width(45)))
                AutoLocateAgentConfig(true);

            if (GUILayout.Button("Browse", GUILayout.Width(58)))
            {
                string configDirectory = GetSafeDirectoryName(MCPSettingsManager.AgentConfigPath);
                string selectedPath = EditorUtility.OpenFilePanel(
                    "Locate Agent Configuration", configDirectory, GetAgentConfigExtension());
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    MCPSettingsManager.AgentConfigPath = selectedPath;
                    _agentConfigStatus = null;
                    _agentConfigFailed = false;
                }
            }
            EditorGUILayout.EndHorizontal();

            bool hasConfigTarget = !string.IsNullOrEmpty(MCPSettingsManager.AgentConfigPath);
            bool canConfigure = serverLocated && entryPointLocated && hasConfigTarget;
            if (!canConfigure)
            {
                string message = !hasConfigTarget
                    ? "Click Find to detect this agent's configuration file."
                    : !serverLocated
                        ? "Locate the companion MCP server before configuring an agent."
                        : "Confirm the server entry point before configuring an agent.";
                EditorGUILayout.HelpBox(message, MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField(
                    agentConfigured ? "VRseBuilder MCP configuration found." :
                    agentConfigLocated ? "Configuration found, but VRseBuilder is not configured." :
                    "A new configuration file will be created.",
                    EditorStyles.miniLabel);
            }

            string agentConfig = BuildAgentConfiguration();
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = canConfigure;
            if (GUILayout.Button("Configure " + MCPSettingsManager.AgentClient, GUILayout.Height(24)))
                ConfigureAgentConfig();
            GUI.enabled = true;
            if (GUILayout.Button("Copy Config", GUILayout.Width(90), GUILayout.Height(24)))
                GUIUtility.systemCopyBuffer = agentConfig;
            EditorGUILayout.EndHorizontal();

            _configPreviewFoldout = EditorGUILayout.Foldout(_configPreviewFoldout, "Preview generated configuration", true);
            if (_configPreviewFoldout)
                EditorGUILayout.TextArea(agentConfig, GUILayout.MinHeight(72));

            if (!string.IsNullOrEmpty(_agentConfigStatus))
                EditorGUILayout.HelpBox(_agentConfigStatus, _agentConfigFailed ? MessageType.Warning : MessageType.Info);
        }

        private void DrawAgentTroubleshooting()
        {
            _agentTroubleshootingFoldout = EditorGUILayout.Foldout(
                _agentTroubleshootingFoldout, "Troubleshooting", true);
            if (!_agentTroubleshootingFoldout) return;

            EditorGUILayout.LabelField("- Verify that Node.js and npx are available from your terminal.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("- Restart the selected AI agent after changing its configuration.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("- Keep the Unity bridge running while the agent connects.", EditorStyles.wordWrappedMiniLabel);
        }

        private static bool HasMcpServerPackage()
        {
            try
            {
                return !string.IsNullOrEmpty(MCPSettingsManager.McpServerPath) &&
                    System.IO.File.Exists(System.IO.Path.Combine(MCPSettingsManager.McpServerPath, "package.json"));
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }

        private static bool HasServerEntryPoint()
        {
            try
            {
                string entryPoint = GetServerEntryPoint();
                return !string.IsNullOrEmpty(entryPoint) && System.IO.File.Exists(entryPoint);
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }

        private static string GetSafeDirectoryName(string path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? "" : System.IO.Path.GetDirectoryName(path) ?? "";
            }
            catch (System.ArgumentException)
            {
                return "";
            }
        }

        private void AutoLocateAgentConfig(bool replaceCurrent)
        {
            if (!replaceCurrent && !string.IsNullOrEmpty(MCPSettingsManager.AgentConfigPath))
                return;

            var candidates = GetAgentConfigCandidates(MCPSettingsManager.AgentClient);
            if (candidates.Count == 0) return;

            string selectedPath = candidates[0];
            bool found = false;
            foreach (string candidate in candidates)
            {
                if (!System.IO.File.Exists(candidate)) continue;

                selectedPath = candidate;
                found = true;
                break;
            }

            MCPSettingsManager.AgentConfigPath = selectedPath;
            _agentConfigStatus = found
                ? "Found the " + MCPSettingsManager.AgentClient + " configuration file."
                : "No existing configuration was found. Configure will create one at the suggested location.";
            _agentConfigFailed = false;
        }

        private static List<string> GetAgentConfigCandidates(string client)
        {
            var candidates = new List<string>();
            string projectRoot = GetProjectRoot();
            string userHome = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);

            switch (client)
            {
                case "OpenCode":
                    // OpenCode deep-merges project config over global config. Configure the global
                    // file by default so the server remains available across Unity projects.
                    AddConfigCandidate(candidates, userHome, ".config", "opencode", "opencode.json");
                    AddConfigCandidate(candidates, projectRoot, "opencode.json");
                    AddConfigCandidate(candidates, projectRoot, ".opencode", "opencode.json");
                    break;
                case "Claude Code":
                    AddConfigCandidate(candidates, projectRoot, ".mcp.json");
                    AddConfigCandidate(candidates, userHome, ".claude.json");
                    break;
                case "Claude Desktop":
                    AddConfigCandidate(candidates, appData, "Claude", "claude_desktop_config.json");
                    break;
                case "Codex":
                    AddConfigCandidate(candidates, userHome, ".codex", "config.toml");
                    break;
                case "Cursor":
                    AddConfigCandidate(candidates, projectRoot, ".cursor", "mcp.json");
                    AddConfigCandidate(candidates, userHome, ".cursor", "mcp.json");
                    break;
                case "GitHub Copilot":
                case "Visual Studio Code":
                    AddConfigCandidate(candidates, projectRoot, ".vscode", "mcp.json");
                    AddConfigCandidate(candidates, appData, "Code", "User", "mcp.json");
                    break;
                default:
                    AddConfigCandidate(candidates, projectRoot, ".mcp.json");
                    break;
            }

            return candidates;
        }

        private static void AddConfigCandidate(List<string> candidates, params string[] paths)
        {
            try
            {
                string path = System.IO.Path.Combine(paths);
                if (!candidates.Contains(path))
                    candidates.Add(path);
            }
            catch (System.ArgumentException)
            {
                // Ignore invalid environment paths and continue to the next known location.
            }
        }

        private static string GetProjectRoot()
        {
            var parent = System.IO.Directory.GetParent(Application.dataPath);
            return parent == null ? "" : parent.FullName;
        }

        private static string GetAgentConfigExtension()
        {
            return MCPSettingsManager.AgentClient == "Codex" ? "toml" : "json";
        }

        private static bool IsSelectedAgentConfigured()
        {
            try
            {
                string configPath = MCPSettingsManager.AgentConfigPath;
                if (string.IsNullOrEmpty(configPath) || !System.IO.File.Exists(configPath))
                    return false;

                if (MCPSettingsManager.AgentClient == "Codex")
                {
                    return CodexConfigMatchesSelectedServer(System.IO.File.ReadAllText(configPath));
                }

                var root = MiniJson.Deserialize(System.IO.File.ReadAllText(configPath)) as Dictionary<string, object>;
                if (root == null) return false;

                string serverKey = MCPSettingsManager.AgentClient == "OpenCode" ? "mcp" :
                    UsesVsCodeMcpFormat() ? "servers" : "mcpServers";
                return root.TryGetValue(serverKey, out object servers) &&
                    servers is Dictionary<string, object> serverEntries &&
                    serverEntries.TryGetValue("vrsebuilder", out object server) &&
                    server is Dictionary<string, object> serverConfig &&
                    ServerConfigMatchesSelectedServer(serverConfig);
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static bool ServerConfigMatchesSelectedServer(Dictionary<string, object> serverConfig)
        {
            var expected = GetServerLaunchCommand();
            if (!serverConfig.TryGetValue("command", out object command))
                return false;

            if (command is System.Collections.IList commandArray)
                return CommandArrayMatches(commandArray, expected, 0);

            if (!string.Equals(command?.ToString(), expected[0], System.StringComparison.Ordinal))
                return false;

            return serverConfig.TryGetValue("args", out object arguments) &&
                arguments is System.Collections.IList argumentArray &&
                CommandArrayMatches(argumentArray, expected, 1);
        }

        private static bool CommandArrayMatches(System.Collections.IList values, List<string> expected, int expectedStartIndex)
        {
            if (values.Count != expected.Count - expectedStartIndex)
                return false;

            for (int i = 0; i < values.Count; i++)
            {
                if (!string.Equals(values[i]?.ToString(), expected[i + expectedStartIndex], System.StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool CodexConfigMatchesSelectedServer(string config)
        {
            const string Header = "[mcp_servers.vrsebuilder]";
            int startIndex = config.IndexOf(Header, System.StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0) return false;

            int nextSection = config.IndexOf("\n[", startIndex + Header.Length, System.StringComparison.Ordinal);
            string section = nextSection >= 0
                ? config.Substring(startIndex, nextSection - startIndex)
                : config.Substring(startIndex);
            string expected = BuildAgentConfiguration();
            return section.IndexOf(expected, System.StringComparison.Ordinal) >= 0;
        }

        private void CloneMcpServer()
        {
            string parentDirectory = EditorUtility.OpenFolderPanel("Clone MCP Server Into", "", "");
            if (string.IsNullOrEmpty(parentDirectory)) return;

            string repository = MCPSettingsManager.McpServerRepository.Trim();
            string folderName = GetRepositoryFolderName(repository);
            if (string.IsNullOrEmpty(folderName))
            {
                _cloneStatus = "Enter a valid Git repository URL before cloning.";
                _cloneFailed = true;
                return;
            }

            _cloneTargetPath = System.IO.Path.Combine(parentDirectory, folderName);
            if (System.IO.Directory.Exists(_cloneTargetPath))
            {
                _cloneStatus = "The target folder already exists. Locate the server instead or choose another parent folder.";
                _cloneFailed = true;
                return;
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "clone \"" + repository + "\"",
                    WorkingDirectory = parentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _cloneProcess = System.Diagnostics.Process.Start(startInfo);
                _cloneStatus = "Cloning MCP server...";
                _cloneFailed = false;
            }
            catch (System.Exception exception)
            {
                _cloneStatus = "Could not start git clone: " + exception.Message;
                _cloneFailed = true;
            }
        }

        private void UpdateCloneStatus()
        {
            if (_cloneProcess == null || !_cloneProcess.HasExited) return;

            string output = _cloneProcess.StandardOutput.ReadToEnd();
            string error = _cloneProcess.StandardError.ReadToEnd();
            bool succeeded = _cloneProcess.ExitCode == 0;
            _cloneProcess.Dispose();
            _cloneProcess = null;

            if (succeeded && System.IO.File.Exists(System.IO.Path.Combine(_cloneTargetPath, "package.json")))
            {
                MCPSettingsManager.McpServerPath = _cloneTargetPath;
                _cloneStatus = "MCP server cloned and selected.";
                _cloneFailed = false;
            }
            else
            {
                string details = string.IsNullOrWhiteSpace(error) ? output : error;
                _cloneStatus = "MCP server clone failed" +
                    (string.IsNullOrWhiteSpace(details) ? "." : ": " + details.Trim());
                _cloneFailed = true;
            }
        }

        private static string GetRepositoryFolderName(string repository)
        {
            string trimmed = repository.TrimEnd('/', '\\');
            int separator = trimmed.LastIndexOf('/');
            if (separator < 0) separator = trimmed.LastIndexOf('\\');
            string name = separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
            return name.EndsWith(".git") ? name.Substring(0, name.Length - 4) : name;
        }

        private static bool IsCloudMcpServer()
        {
            return MCPSettingsManager.McpServerSource == "Cloud";
        }

        private static List<string> GetServerLaunchCommand()
        {
            if (IsCloudMcpServer())
            {
                return new List<string>
                {
                    "npx",
                    "-y",
                    "--registry=" + CloudMcpRegistry,
                    CloudMcpPackage,
                };
            }

            return new List<string>
            {
                MCPSettingsManager.McpServerCommand,
                GetServerEntryPoint(),
            };
        }

        private static string BuildJsonStringArray(List<string> values, int startIndex)
        {
            var escapedValues = new List<string>();
            for (int i = startIndex; i < values.Count; i++)
                escapedValues.Add("\"" + EscapeJson(values[i]) + "\"");
            return "[" + string.Join(", ", escapedValues.ToArray()) + "]";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string BuildAgentConfiguration()
        {
            var launchCommand = GetServerLaunchCommand();
            string command = EscapeJson(launchCommand[0]);
            string commandArguments = BuildJsonStringArray(launchCommand, 1);

            if (MCPSettingsManager.AgentClient == "Codex")
            {
                return "[mcp_servers.vrsebuilder]\n" +
                    "command = \"" + command + "\"\n" +
                    "args = " + commandArguments;
            }

            if (MCPSettingsManager.AgentClient == "OpenCode")
            {
                return "{\n" +
                    "  \"mcp\": {\n" +
                    "    \"vrsebuilder\": {\n" +
                    "      \"type\": \"local\",\n" +
                    "      \"command\": " + BuildJsonStringArray(launchCommand, 0) + ",\n" +
                    "      \"enabled\": true\n" +
                    "    }\n" +
                    "  }\n" +
                    "}";
            }

            if (UsesVsCodeMcpFormat())
            {
                return "{\n" +
                    "  \"servers\": {\n" +
                    "    \"vrsebuilder\": {\n" +
                    "      \"type\": \"stdio\",\n" +
                    "      \"command\": \"" + command + "\",\n" +
                    "      \"args\": " + commandArguments + "\n" +
                    "    }\n" +
                    "  }\n" +
                    "}";
            }

            return "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"vrsebuilder\": {\n" +
                "      \"command\": \"" + command + "\",\n" +
                "      \"args\": " + commandArguments + "\n" +
                "    }\n" +
                "  }\n" +
                "}";
        }

        private void ConfigureAgentConfig()
        {
            try
            {
                string configPath = MCPSettingsManager.AgentConfigPath;
                if (MCPSettingsManager.AgentClient == "Codex")
                {
                    ConfigureCodexConfig(configPath);
                    return;
                }

                var launchCommand = GetServerLaunchCommand();
                var launchArguments = new List<object>();
                for (int i = 1; i < launchCommand.Count; i++)
                    launchArguments.Add(launchCommand[i]);

                var root = System.IO.File.Exists(configPath)
                    ? MiniJson.Deserialize(System.IO.File.ReadAllText(configPath)) as Dictionary<string, object>
                    : new Dictionary<string, object>();
                if (root == null)
                {
                    _agentConfigStatus = "The selected file must contain a JSON object.";
                    _agentConfigFailed = true;
                    return;
                }

                var serverConfig = new Dictionary<string, object>
                {
                    { "command", launchCommand[0] },
                    { "args", launchArguments },
                };

                if (MCPSettingsManager.AgentClient == "OpenCode")
                {
                    var mcp = GetOrCreateObject(root, "mcp");
                    serverConfig["type"] = "local";
                    serverConfig["command"] = new List<object>
                    {
                        launchCommand[0],
                    };
                    for (int i = 1; i < launchCommand.Count; i++)
                        ((List<object>)serverConfig["command"]).Add(launchCommand[i]);
                    serverConfig["enabled"] = true;
                    serverConfig.Remove("args");
                    mcp["vrsebuilder"] = serverConfig;
                }
                else
                {
                    if (UsesVsCodeMcpFormat())
                    {
                        serverConfig["type"] = "stdio";
                        GetOrCreateObject(root, "servers")["vrsebuilder"] = serverConfig;
                    }
                    else
                    {
                        GetOrCreateObject(root, "mcpServers")["vrsebuilder"] = serverConfig;
                    }
                }

                EnsureConfigDirectoryExists(configPath);
                System.IO.File.WriteAllText(configPath, PrettyPrintJson(MiniJson.Serialize(root)) + "\n");
                _agentConfigStatus = "Added or updated the vrsebuilder MCP server in the selected agent configuration.";
                _agentConfigFailed = false;
            }
            catch (System.Exception exception)
            {
                _agentConfigStatus = "Could not configure the selected file: " + exception.Message;
                _agentConfigFailed = true;
            }
        }

        private void ConfigureCodexConfig(string configPath)
        {
            string existingConfig = System.IO.File.Exists(configPath)
                ? System.IO.File.ReadAllText(configPath)
                : "";
            string serverConfig = BuildAgentConfiguration();
            const string Header = "[mcp_servers.vrsebuilder]";
            int startIndex = existingConfig.IndexOf(Header, System.StringComparison.OrdinalIgnoreCase);

            if (startIndex >= 0)
            {
                int nextSection = existingConfig.IndexOf("\n[", startIndex + Header.Length, System.StringComparison.Ordinal);
                existingConfig = nextSection >= 0
                    ? existingConfig.Substring(0, startIndex) + existingConfig.Substring(nextSection)
                    : existingConfig.Substring(0, startIndex);
            }

            EnsureConfigDirectoryExists(configPath);
            System.IO.File.WriteAllText(configPath, existingConfig.TrimEnd() + "\n\n" + serverConfig + "\n");
            _agentConfigStatus = "Added or updated the vrsebuilder MCP server in the selected Codex configuration.";
            _agentConfigFailed = false;
        }

        private static bool UsesVsCodeMcpFormat()
        {
            return MCPSettingsManager.AgentClient == "GitHub Copilot" ||
                MCPSettingsManager.AgentClient == "Visual Studio Code";
        }

        private static void EnsureConfigDirectoryExists(string configPath)
        {
            string directory = GetSafeDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
                System.IO.Directory.CreateDirectory(directory);
        }

        private static string PrettyPrintJson(string json)
        {
            var formatted = new System.Text.StringBuilder();
            bool insideString = false;
            bool escaped = false;
            int indent = 0;

            foreach (char character in json)
            {
                if (insideString)
                {
                    formatted.Append(character);
                    if (character == '"' && !escaped)
                        insideString = false;
                    escaped = character == '\\' && !escaped;
                    continue;
                }

                switch (character)
                {
                    case '"':
                        insideString = true;
                        formatted.Append(character);
                        break;
                    case '{':
                    case '[':
                        formatted.Append(character).Append('\n');
                        indent++;
                        AppendJsonIndent(formatted, indent);
                        break;
                    case '}':
                    case ']':
                        formatted.Append('\n');
                        indent--;
                        AppendJsonIndent(formatted, indent);
                        formatted.Append(character);
                        break;
                    case ',':
                        formatted.Append(character).Append('\n');
                        AppendJsonIndent(formatted, indent);
                        break;
                    case ':':
                        formatted.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(character))
                            formatted.Append(character);
                        break;
                }
            }

            return formatted.ToString();
        }

        private static void AppendJsonIndent(System.Text.StringBuilder builder, int indent)
        {
            for (int i = 0; i < indent; i++)
                builder.Append("  ");
        }

        private static Dictionary<string, object> GetOrCreateObject(Dictionary<string, object> root, string key)
        {
            if (root.TryGetValue(key, out object existing) && existing is Dictionary<string, object> value)
                return value;

            var created = new Dictionary<string, object>();
            root[key] = created;
            return created;
        }

        private static string GetServerEntryPoint()
        {
            string entryPoint = MCPSettingsManager.McpServerEntryPoint;
            try
            {
                if (!System.IO.Path.IsPathRooted(entryPoint) && !string.IsNullOrEmpty(MCPSettingsManager.McpServerPath))
                    return System.IO.Path.Combine(MCPSettingsManager.McpServerPath, entryPoint);
            }
            catch (System.ArgumentException)
            {
                return entryPoint;
            }
            return entryPoint;
        }

        private static string GetDefaultAgentId(string client)
        {
            switch (client)
            {
                case "OpenCode": return "opencode";
                case "Claude Code": return "claude-code";
                case "Claude Desktop": return "claude-desktop";
                case "Codex": return "codex";
                case "Cursor": return "cursor";
                case "GitHub Copilot": return "github-copilot";
                case "Visual Studio Code": return "vscode";
                default: return "";
            }
        }

        // ─── Recent Actions ───

        private void DrawRecentActions()
        {
            _recentActionsFoldout = EditorGUILayout.Foldout(_recentActionsFoldout, "Recent Actions", true, EditorStyles.foldoutHeader);
            if (!_recentActionsFoldout) return;

            var recent = MCPActionHistory.GetRecent(8);

            if (recent.Count == 0)
            {
                EditorGUILayout.HelpBox("No actions recorded yet.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Show newest first
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var r = recent[i];
                EditorGUILayout.BeginHorizontal();

                // Status dot
                var prevColor = GUI.color;
                Color dotColor;
                switch (r.Status)
                {
                    case "Completed": dotColor = ColorGreen; break;
                    case "Failed":    dotColor = ColorRed;   break;
                    default:          dotColor = ColorYellow; break;
                }
                GUI.color = dotColor;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                // Timestamp
                EditorGUILayout.LabelField(r.Timestamp.ToString("HH:mm:ss"),
                    EditorStyles.miniLabel, GUILayout.Width(55));

                // Agent (short)
                string agent = r.AgentId ?? "?";
                if (agent.Length > 10) agent = agent.Substring(0, 8) + "..";
                prevColor = GUI.color;
                GUI.color = ColorBlue;
                EditorGUILayout.LabelField(agent, EditorStyles.miniLabel, GUILayout.Width(65));
                GUI.color = prevColor;

                // Action command
                string cmd = MCPActionRecord.ExtractCommand(r.ActionName);
                EditorGUILayout.LabelField(cmd, EditorStyles.miniLabel, GUILayout.Width(100));

                // Target (truncated)
                string target = r.TargetPath ?? "";
                if (target.Length > 25)
                    target = ".." + target.Substring(target.Length - 23);
                EditorGUILayout.LabelField(target, EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            // Open full history button
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            string btnLabel = MCPActionHistory.Count > 8
                ? $"Open Full History ({MCPActionHistory.Count} actions)"
                : "Open Full History";
            if (GUILayout.Button(btnLabel, GUILayout.Width(200), GUILayout.Height(20)))
            {
                MCPActionHistoryWindow.ShowWindow();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ─── Feature Categories + Test Status ───

        private void DrawCategoryStatus()
        {
            _categoriesFoldout = EditorGUILayout.Foldout(_categoriesFoldout, "Feature Categories", true, EditorStyles.foldoutHeader);
            if (!_categoriesFoldout) return;

            // Test controls bar
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Summary
            int passed = MCPSelfTest.PassedCount;
            int failed = MCPSelfTest.FailedCount;
            int warnings = MCPSelfTest.WarningCount;
            int total = MCPSettingsManager.GetAllCategoryNames().Length;

            if (MCPSelfTest.IsRunning)
            {
                EditorGUILayout.LabelField(
                    $"Testing: {MCPSelfTest.CurrentCategory}...",
                    EditorStyles.miniLabel);
                var rect = GUILayoutUtility.GetRect(100, 16, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, MCPSelfTest.Progress, $"{(int)(MCPSelfTest.Progress * 100)}%");
            }
            else if (MCPSelfTest.LastRunTime > System.DateTime.MinValue)
            {
                string summary = "";
                if (failed > 0)
                    summary += $"<color=#E63333>{failed} failed</color>  ";
                if (warnings > 0)
                    summary += $"<color=#E6CC11>{warnings} warn</color>  ";
                summary += $"<color=#33CC33>{passed}/{total} passed</color>";

                var richStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                EditorGUILayout.LabelField(summary, richStyle, GUILayout.ExpandWidth(true));
            }
            else
            {
                EditorGUILayout.LabelField("No tests run yet", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            GUI.enabled = !MCPSelfTest.IsRunning && MCPBridgeServer.IsRunning;
            if (GUILayout.Button("Run Tests", GUILayout.Width(80), GUILayout.Height(20)))
            {
                MCPSelfTest.RunAllAsync();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // Category rows
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string[] categories = MCPSettingsManager.GetAllCategoryNames();
            foreach (var cat in categories)
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);
                var testResult = MCPSelfTest.GetResult(cat);

                EditorGUILayout.BeginHorizontal();

                // Status dot — reflects test status when available, else enabled/disabled
                var prevColor = GUI.color;
                Color dotColor = GetCategoryDotColor(enabled, testResult);
                GUI.color = dotColor;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                // Pretty name
                string displayName = char.ToUpper(cat[0]) + cat.Substring(1);
                EditorGUILayout.LabelField(displayName, GUILayout.Width(100));

                // Test status label — always draw both controls to avoid IMGUI control count mismatch
                bool hasTested = testResult != null && testResult.Status != MCPTestResult.TestStatus.Untested;
                bool hasDetails = hasTested && (testResult.Status == MCPTestResult.TestStatus.Failed ||
                    testResult.Status == MCPTestResult.TestStatus.Warning);

                if (hasTested)
                {
                    string statusLabel = GetTestStatusText(testResult);
                    var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = dotColor },
                    };
                    EditorGUILayout.LabelField(statusLabel, statusStyle, GUILayout.Width(90));
                }
                else
                {
                    EditorGUILayout.LabelField("\u2014", EditorStyles.miniLabel, GUILayout.Width(90));
                }

                // Always draw the details button to keep control count stable
                if (hasDetails)
                {
                    if (GUILayout.Button("?", GUILayout.Width(20), GUILayout.Height(16)))
                    {
                        _expandedTestCategory = _expandedTestCategory == cat ? null : cat;
                    }
                }
                else
                {
                    // Invisible placeholder — same control, no visual
                    var transparent = GUI.color;
                    GUI.color = new Color(0, 0, 0, 0);
                    GUILayout.Button("", GUILayout.Width(20), GUILayout.Height(16));
                    GUI.color = transparent;
                }

                GUILayout.FlexibleSpace();

                bool newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(30));
                if (newEnabled != enabled)
                    MCPSettingsManager.SetCategoryEnabled(cat, newEnabled);

                EditorGUILayout.EndHorizontal();

                // Expanded error details
                if (_expandedTestCategory == cat && testResult != null &&
                    !string.IsNullOrEmpty(testResult.Details))
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.SelectableLabel(
                        testResult.Details,
                        EditorStyles.wordWrappedMiniLabel,
                        GUILayout.MinHeight(36));
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private Color GetCategoryDotColor(bool enabled, MCPTestResult result)
        {
            if (!enabled) return ColorGrey;
            if (result == null || result.Status == MCPTestResult.TestStatus.Untested)
                return enabled ? ColorGreen : ColorGrey;

            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed:  return ColorGreen;
                case MCPTestResult.TestStatus.Warning: return ColorYellow;
                case MCPTestResult.TestStatus.Failed:  return ColorRed;
                default: return ColorGrey;
            }
        }

        private string GetTestStatusText(MCPTestResult result)
        {
            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed:
                    return $"\u2713 {result.DurationMs:0}ms";
                case MCPTestResult.TestStatus.Warning:
                    return $"\u26A0 {result.Message}";
                case MCPTestResult.TestStatus.Failed:
                    return $"\u2717 {result.Message}";
                default:
                    return "\u2014";
            }
        }

        // ─── Agent Sessions ───

        private void DrawAgentSessions()
        {
            _agentsFoldout = EditorGUILayout.Foldout(_agentsFoldout, "Active Agent Sessions", true, EditorStyles.foldoutHeader);
            if (!_agentsFoldout) return;

            var sessions = MCPRequestQueue.GetActiveSessions();

            if (sessions.Count == 0)
            {
                EditorGUILayout.HelpBox("No active agent sessions.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            foreach (var session in sessions)
            {
                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                GUI.color = ColorGreen;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                string agentId = session.ContainsKey("agentId") ? session["agentId"].ToString() : "?";
                string action = session.ContainsKey("currentAction") ? session["currentAction"].ToString() : "idle";
                object totalObj = session.ContainsKey("totalActions") ? session["totalActions"] : 0;
                object queuedObj = session.ContainsKey("queuedRequests") ? session["queuedRequests"] : 0;
                object completedObj = session.ContainsKey("completedRequests") ? session["completedRequests"] : 0;
                object avgMs = session.ContainsKey("averageResponseTimeMs") ? session["averageResponseTimeMs"] : 0;

                EditorGUILayout.LabelField(agentId, EditorStyles.boldLabel, GUILayout.Width(160));
                EditorGUILayout.LabelField(action, GUILayout.MinWidth(80));
                GUILayout.FlexibleSpace();

                // Queue + completed stats
                int queuedInt = 0;
                int.TryParse(queuedObj.ToString(), out queuedInt);

                var richStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                string stats = $"{totalObj} total";
                if (queuedInt > 0)
                    stats += $"  <color=#E6CC11>{queuedInt}q</color>";
                stats += $"  <color=#33CC33>{completedObj}ok</color>";
                stats += $"  {avgMs}ms";

                EditorGUILayout.LabelField(stats, richStyle, GUILayout.Width(170));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Settings ───

        private void DrawSettings()
        {
            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings", true, EditorStyles.foldoutHeader);
            if (!_settingsFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Auto-start
            bool autoStart = EditorGUILayout.Toggle("Auto-start on Editor Load", MCPSettingsManager.AutoStart);
            if (autoStart != MCPSettingsManager.AutoStart)
                MCPSettingsManager.AutoStart = autoStart;

            EditorGUILayout.Space(2);

            // Port mode toggle
            bool useManual = EditorGUILayout.Toggle("Use Manual Port", MCPSettingsManager.UseManualPort);
            if (useManual != MCPSettingsManager.UseManualPort)
                MCPSettingsManager.UseManualPort = useManual;

            if (useManual)
            {
                // Manual port entry
                EditorGUILayout.BeginHorizontal();
                int port = EditorGUILayout.IntField("Server Port", MCPSettingsManager.Port);
                if (port != MCPSettingsManager.Port && port > 1024 && port < 65536)
                {
                    MCPSettingsManager.Port = port;
                }
                EditorGUILayout.EndHorizontal();

                if (MCPBridgeServer.IsRunning && MCPBridgeServer.ActivePort != MCPSettingsManager.Port)
                    EditorGUILayout.HelpBox("Restart server to apply port change.", MessageType.Info);
            }
            else
            {
                // Auto-select info
                string autoInfo = MCPBridgeServer.IsRunning
                    ? $"Auto-selected port {MCPBridgeServer.ActivePort} (range: {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd})"
                    : $"Will auto-select from range {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd}";
                EditorGUILayout.HelpBox(autoInfo, MessageType.None);
            }

            EditorGUILayout.Space(4);

            // Reset button
            if (GUILayout.Button("Reset All Settings to Defaults"))
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                    "Reset all MCP settings to defaults?", "Reset", "Cancel"))
                {
                    MCPSettingsManager.ResetToDefaults();
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Version Info ───

        private void DrawVersionInfo()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Plugin Version: 2.23.1", GUILayout.Width(155));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Check for Updates", GUILayout.Width(130)))
            {
                MCPUpdateChecker.CheckForUpdates((hasUpdate, latestVersion) =>
                {
                    if (hasUpdate)
                    {
                        EditorUtility.DisplayDialog("Update Available",
                            $"A new version ({latestVersion}) is available.\n" +
                            "Update via Unity Package Manager.",
                            "OK");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Up to Date",
                            "You are running the latest version.", "OK");
                    }
                });
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
