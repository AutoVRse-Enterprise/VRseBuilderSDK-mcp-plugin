using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// HTTP server that runs inside the Unity Editor, enabling external MCP tools
    /// to control the editor via REST API calls.
    ///
    /// Supports two modes:
    ///   1. Queue mode (async):  POST /api/queue/submit → poll GET /api/queue/status
    ///   2. Legacy mode (sync):  POST /api/{command}    → blocks until done
    ///
    /// Both modes go through MCPRequestQueue for fair round-robin scheduling.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPBridgeServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static bool _isRunning;

        /// <summary>
        /// The actual port this server is running on.
        /// Resolved at startup via auto-selection or manual override.
        /// </summary>
        private static int _activePort;

        /// <summary>The port this server is currently bound to (0 if not running).</summary>
        public static int ActivePort => _isRunning ? _activePort : 0;

        // Legacy main-thread queue (kept for direct ExecuteOnMainThread calls)
        private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        // SessionState key to persist running state across domain reloads (Play Mode, recompile)
        private const string WasRunningKey = "UnityMCP_WasRunningBeforeReload";

        static MCPBridgeServer()
        {
            // Restart if: AutoStart is enabled OR server was running before a domain reload
            bool wasRunning = SessionState.GetBool(WasRunningKey, false);
            if (MCPSettingsManager.AutoStart || wasRunning)
            {
                Start();
                SessionState.SetBool(WasRunningKey, false);
            }
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// Handle Play Mode transitions to ensure the server stays alive.
        /// Unity triggers a domain reload when entering/exiting Play Mode,
        /// which is handled by the assembly reload callbacks and the SessionState flag.
        /// This callback provides additional resilience for edge cases.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                if (!_isRunning && (MCPSettingsManager.AutoStart || SessionState.GetBool(WasRunningKey, false)))
                {
                    Debug.Log("[MCP Bridge] Restarting server after Play Mode transition...");
                    Start();
                    SessionState.SetBool(WasRunningKey, false);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_isRunning)
            {
                // Persist that we were running, so we restart after reload
                SessionState.SetBool(WasRunningKey, true);
                Stop();
            }
        }

        private static void OnQuitting()
        {
            Stop();
            // Final cleanup of registry on quit
            MCPInstanceRegistry.Unregister();
        }

        /// <summary>Whether the server is currently running.</summary>
        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            if (_isRunning) return;

            // Ensure console log capture is active before anything else
            MCPConsoleCommands.EnsureListening();

            // Clean up stale entries before selecting a port
            MCPInstanceRegistry.CleanupStaleEntries();

            // Resolve port: use manual override if set, otherwise auto-select
            int port;
            if (MCPSettingsManager.UseManualPort)
            {
                port = MCPSettingsManager.Port;
            }
            else
            {
                port = MCPInstanceRegistry.FindAvailablePort();
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _isRunning = true;
                _activePort = port;

                // Update the settings so the UI reflects the actual port
                MCPSettingsManager.Port = port;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AB Unity MCP Server"
                };
                _listenerThread.Start();

                // Register in the shared instance registry
                MCPInstanceRegistry.Register(port);

                Debug.Log($"[AB-UMCP] Server started on port {port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AB-UMCP] Failed to start on port {port}: {ex.Message}");

                // If auto-port failed, try the next available one
                if (!MCPSettingsManager.UseManualPort && port < MCPInstanceRegistry.PortRangeEnd)
                {
                    Debug.Log("[AB-UMCP] Trying next available port...");
                    EditorApplication.delayCall += Start;
                }
            }
        }

        public static void Stop()
        {
            _isRunning = false;

            // Unregister from shared instance registry
            MCPInstanceRegistry.Unregister();

            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
            }
            catch { }
            _activePort = 0;
            Debug.Log("[AB-UMCP] Server stopped");
        }

        // ─── EditorApplication.update — processes both legacy queue AND ticket queue ───

        private static void OnEditorUpdate()
        {
            // 1. Process legacy main-thread actions
            ProcessMainThreadQueue();

            // 2. Process ticket-based queue (fair round-robin)
            MCPRequestQueue.ProcessNextRequests();
        }

        // ─── HTTP Listener ───

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_isRunning) { break; }
                catch (ThreadAbortException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError($"[AB-UMCP] Listener error: {ex.Message}");
                }
            }
        }

        // ─── Request Handler ───

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = request.Url.AbsolutePath.TrimStart('/');
                if (!path.StartsWith("api/"))
                {
                    SendJson(response, 404, new { error = "Not found" });
                    return;
                }

                string apiPath = path.Substring(4); // Remove "api/"
                string body = "";
                if (request.HasEntityBody)
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                        body = reader.ReadToEnd();
                }

                string agentId = request.Headers["X-Agent-Id"] ?? "anonymous";

                // ═══ Queue endpoints (async, non-blocking) ═══
                if (apiPath == "queue/submit")
                {
                    HandleQueueSubmit(response, agentId, body);
                    return;
                }
                if (apiPath == "queue/status")
                {
                    HandleQueueStatus(response, request);
                    return;
                }
                if (apiPath == "queue/info")
                {
                    SendJson(response, 200, MCPRequestQueue.GetQueueInfo());
                    return;
                }

                // ═══ Project Context endpoints (read-only, no queue needed) ═══
                if (apiPath == "context")
                {
                    SendJson(response, 200, MCPContextManager.GetContextResponse());
                    return;
                }
                if (apiPath.StartsWith("context/"))
                {
                    string category = apiPath.Substring("context/".Length);
                    SendJson(response, 200, MCPContextManager.GetContextResponse(category));
                    return;
                }

                // ═══ Legacy synchronous path (blocks until main thread processes) ═══
                var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                    () => ExecuteOnMainThread(() => RouteRequest(apiPath, request.HttpMethod, body)));
                SendJson(response, 200, result);
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        // ─── Queue Submit (async) ───

        private static void HandleQueueSubmit(HttpListenerResponse response, string agentId, string body)
        {
            try
            {
                var args = ParseJson(body);
                string apiPath = args.ContainsKey("apiPath") ? args["apiPath"].ToString() : "";
                string innerBody = args.ContainsKey("body") ? args["body"].ToString() : "";

                if (string.IsNullOrEmpty(apiPath))
                {
                    SendJson(response, 400, new { error = "Missing 'apiPath' in request body" });
                    return;
                }

                // Override agentId if provided in the body
                if (args.ContainsKey("agentId") && !string.IsNullOrEmpty(args["agentId"]?.ToString()))
                    agentId = args["agentId"].ToString();

                // Submit to queue — the action captures the routing logic
                var ticket = MCPRequestQueue.SubmitRequest(agentId, apiPath, () =>
                {
                    return RouteRequest(apiPath, "POST", innerBody);
                });

                // Return immediately with ticket info
                SendJson(response, 202, new Dictionary<string, object>
                {
                    { "ticketId",      ticket.TicketId },
                    { "status",        ticket.Status.ToString() },
                    { "queuePosition", ticket.QueuePosition },
                    { "agentId",       agentId },
                });
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = $"Queue submit failed: {ex.Message}" });
            }
        }

        // ─── Queue Status (polling) ───

        private static void HandleQueueStatus(HttpListenerResponse response, HttpListenerRequest request)
        {
            string ticketIdStr = request.QueryString["ticketId"];
            if (string.IsNullOrEmpty(ticketIdStr) || !long.TryParse(ticketIdStr, out long ticketId))
            {
                SendJson(response, 400, new { error = "Missing or invalid 'ticketId' query parameter" });
                return;
            }

            var status = MCPRequestQueue.GetTicketStatus(ticketId);
            if (status == null)
            {
                SendJson(response, 404, new { error = $"Ticket {ticketId} not found or expired" });
                return;
            }

            SendJson(response, 200, status);
        }

        // ─── Route Request (runs on main thread) ───

        private static string ExtractCategory(string path)
        {
            int slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        /// <summary>
        /// Returns all registered routes for dynamic tool discovery.
        /// Used by the MCP server's lazy loading system to discover tools
        /// added to the plugin without needing a server restart.
        /// </summary>
        private static object GetRegisteredRoutes()
        {
            // We collect routes by reflecting on the switch cases in RouteRequest.
            // Since C# doesn't easily let us introspect switch cases at runtime,
            // we maintain a static list of all registered route prefixes/categories.
            var routes = new List<string>
            {
                "ping",
                "editor/state", "editor/play-mode", "editor/execute-menu-item", "editor/undo", "editor/redo", "editor/undo-history",
                "scene/info", "scene/open", "scene/save", "scene/new", "scene/hierarchy", "scene/stats",
                "gameobject/create", "gameobject/delete", "gameobject/info", "gameobject/set-transform",
                "gameobject/duplicate", "gameobject/set-active", "gameobject/reparent",
                "component/add", "component/remove", "component/get-properties", "component/set-property",
                "component/set-reference", "component/batch-wire", "component/get-referenceable",
                "asset/list", "asset/import", "asset/delete", "asset/create-prefab", "asset/instantiate-prefab",
                "script/create", "script/read", "script/update", "script/execute-code",
                "material/create", "material/set-material",
                "build/build", "build/play-mode",
                "console/log", "console/clear",
                "compilation/errors",
                "selection/get", "selection/set", "selection/focus-scene-view", "selection/find-by-type",
                "search/by-component", "search/by-tag", "search/by-layer", "search/by-name",
                "search/assets", "search/missing-references",
                "screenshot/game", "screenshot/scene",
                "prefab/info", "prefab/set-object-reference",
                "packages/list", "packages/add", "packages/remove", "packages/search", "packages/info",
                "project/info",
                // VRseBuilder
                "vrse/status",
                "vrse/login",
                "vrse/list-projects",
                "vrse/select-project",
                "vrse/list-modules",
                "vrse/open-menu-scene",
                "vrse/open-module",
                "vrse/open-room-manager-config",
                "vrse/get-selected-project",
                "vrse/get-project-config",
                "vrse/ensure-project-settings",
                "vrse/apply-project-settings",
                "vrse/open-studio-project-window",
                "vrse/open-project-config-window",
                "vrse/open-build-tool",
                "vrse/create-evaluation-from-training",
                "vrse/story-remove-node-by-name",
                "vrse/apply-moment-weightage",
                "vrse/story-has-pending-vo",
                "vrse/create-experience",
                "vrse/get-experience-creation-status",
                "vrse/open-art-scene",
                "vrse/story-add-trigger-set",
                "vrse/story-add-action",
                "vrse/story-update-node",
                "vrse/story-apply-json",
                "vrse/story-patch",
                "vrse/story-get-info",
                "vrse/story-undo-write",
                "vrse/story-save",
                "vrse/story-validate",
                "vrse/story-move-action",
                "vrse/story-duplicate-action",
                "vrse/story-apply-action-to-multiple-moments",
                "vrse/list-story-backups",
                "vrse/create-story-backup",
                "vrse/restore-story-backup",
                "vrse/interactable_convert",
                // VRse create-rotator-from-mesh tools
                "vrse/create-rotator-from-mesh/analyze",
                "vrse/create-rotator-from-mesh/create",
                // VRse setup/place object tools
                "vrse/setup-objects",
                "vrse/place-objects",
                "vrse/harvest-ids",
                // VRse create-button-from-mesh tools
                "vrse/create-button-from-mesh/analyze",
                "vrse/create-button-from-mesh/create",
                // VRse story report
                "vrse/story/report",
                // VRse general UI setup
                "vrse/ui/general-setup",
                // Animation
                "animation/create-controller", "animation/get-controller", "animation/add-state",
                "animation/remove-state", "animation/add-transition", "animation/remove-transition",
                "animation/set-parameter", "animation/remove-parameter", "animation/get-parameters",
                "animation/create-clip", "animation/set-clip-curve", "animation/get-clip-info",
                "animation/set-state-motion", "animation/add-layer", "animation/remove-layer",
                "animation/get-layers", "animation/set-default-state", "animation/add-blend-tree",
                // Physics
                "physics/raycast", "physics/overlap-sphere", "physics/settings",
                "physics/add-joint", "physics/get-joint", "physics/set-joint",
                // Audio
                "audio/play", "audio/stop", "audio/get-info", "audio/set-property",
                // UI
                "ui/create-canvas", "ui/add-element", "ui/set-rect", "ui/set-text",
                "ui/set-image", "ui/set-button", "ui/get-hierarchy",
                // Lighting
                "lighting/create", "lighting/set-property", "lighting/bake", "lighting/get-settings",
                "lighting/set-settings", "lighting/get-probes",
                // Graphics
                "graphics/camera-info", "graphics/render-settings", "graphics/set-render-settings",
                "graphics/texture-info", "graphics/renderer-info", "graphics/lighting-summary",
                // Particle System
                "particle/create", "particle/info", "particle/set-main", "particle/set-emission",
                "particle/set-shape", "particle/set-velocity", "particle/set-color",
                "particle/set-size", "particle/set-renderer",
                // VRse Parity Layer (unity-mcp-pro compatibility for the VRse build pipeline)
                "vrse/parity/batch-execute", "vrse/parity/get-components",
                "vrse/parity/get-screenshot-inline", "vrse/parity/list-loaded-scenes",
                // VRse Spatial (no-marker spatial placement at Step 4.5)
                "vrse/spatial/analyze-scene", "vrse/spatial/get-bounds",
                "vrse/spatial/get-surface", "vrse/spatial/check-placement",
                "vrse/spatial/list-probe-surfaces",
            };

            // Group by category
            var grouped = new Dictionary<string, List<string>>();
            foreach (var route in routes)
            {
                string cat = ExtractCategory(route);
                if (!grouped.ContainsKey(cat)) grouped[cat] = new List<string>();
                grouped[cat].Add(route);
            }

            return new Dictionary<string, object>
            {
                { "routes", routes },
                { "categories", grouped },
                { "totalRoutes", routes.Count }
            };
        }

        /// <summary>
        /// Route API requests to the appropriate handler.
        /// NOTE: This entire method runs on the main thread (dispatched by HandleRequest
        /// or by MCPRequestQueue.ProcessNextRequests), so all Unity APIs work correctly.
        /// </summary>
        private static object RouteRequest(string path, string method, string body)
        {
            // ─── Meta endpoints (no category check) ───
            if (path == "_meta/routes")
            {
                return GetRegisteredRoutes();
            }

            // Check if category is enabled
            string category = ExtractCategory(path);
            if (category != "ping" && category != "agents" && category != "queue"
                && !MCPSettingsManager.IsCategoryEnabled(category))
            {
                return new { error = $"Category '{category}' is currently disabled. Enable it in Window > AB Unity MCP." };
            }

            switch (path)
            {
                // ─── Ping ───
                case "ping":
                    return new
                    {
                        status = "ok",
                        unityVersion = Application.unityVersion,
                        projectName = Application.productName,
                        projectPath = GetProjectPath(),
                        platform = Application.platform.ToString(),
                        isClone = MCPInstanceRegistry.IsParrelSyncClone(),
                        cloneIndex = MCPInstanceRegistry.GetParrelSyncCloneIndex(),
                        processId = System.Diagnostics.Process.GetCurrentProcess().Id
                    };

                // ─── Editor State ───
                case "editor/state":
                    return MCPEditorCommands.GetEditorState();
                case "editor/play-mode":
                    return MCPEditorCommands.SetPlayMode(ParseJson(body));
                case "editor/execute-menu-item":
                    return MCPEditorCommands.ExecuteMenuItem(ParseJson(body));
                case "editor/execute-code":
                    return MCPEditorCommands.ExecuteCode(ParseJson(body));

                // ─── Scene ───
                case "scene/info":
                    return MCPSceneCommands.GetSceneInfo();
                case "scene/open":
                    return MCPSceneCommands.OpenScene(ParseJson(body));
                case "scene/save":
                    return MCPSceneCommands.SaveScene();
                case "scene/new":
                    return MCPSceneCommands.NewScene();
                case "scene/hierarchy":
                    return MCPSceneCommands.GetHierarchy(ParseJson(body));

                // ─── GameObject ───
                case "gameobject/create":
                    return MCPGameObjectCommands.Create(ParseJson(body));
                case "gameobject/delete":
                    return MCPGameObjectCommands.Delete(ParseJson(body));
                case "gameobject/info":
                    return MCPGameObjectCommands.GetInfo(ParseJson(body));
                case "gameobject/set-transform":
                    return MCPGameObjectCommands.SetTransform(ParseJson(body));

                // ─── Component ───
                case "component/add":
                    return MCPComponentCommands.Add(ParseJson(body));
                case "component/remove":
                    return MCPComponentCommands.Remove(ParseJson(body));
                case "component/get-properties":
                    return MCPComponentCommands.GetProperties(ParseJson(body));
                case "component/set-property":
                    return MCPComponentCommands.SetProperty(ParseJson(body));
                case "component/set-reference":
                    return MCPComponentCommands.SetReference(ParseJson(body));
                case "component/batch-wire":
                    return MCPComponentCommands.BatchWireReferences(ParseJson(body));
                case "component/get-referenceable":
                    return MCPComponentCommands.GetReferenceableObjects(ParseJson(body));

                // ─── Assets ───
                case "asset/list":
                    return MCPAssetCommands.List(ParseJson(body));
                case "asset/import":
                    return MCPAssetCommands.Import(ParseJson(body));
                case "asset/delete":
                    return MCPAssetCommands.Delete(ParseJson(body));
                case "asset/create-prefab":
                    return MCPAssetCommands.CreatePrefab(ParseJson(body));
                case "asset/instantiate-prefab":
                    return MCPAssetCommands.InstantiatePrefab(ParseJson(body));
                case "asset/create-material":
                    return MCPAssetCommands.CreateMaterial(ParseJson(body));

                // ─── Scripts ───
                case "script/create":
                    return MCPScriptCommands.Create(ParseJson(body));
                case "script/read":
                    return MCPScriptCommands.Read(ParseJson(body));
                case "script/update":
                    return MCPScriptCommands.Update(ParseJson(body));

                // ─── Renderer ───
                case "renderer/set-material":
                    return MCPRendererCommands.SetMaterial(ParseJson(body));

                // ─── Build ───
                case "build/start":
                    return MCPBuildCommands.StartBuild(ParseJson(body));

                // ─── Console ───
                case "console/log":
                    return MCPConsoleCommands.GetLog(ParseJson(body));
                case "console/clear":
                    return MCPConsoleCommands.Clear();

                // ─── Compilation ───
                case "compilation/errors":
                    return MCPConsoleCommands.GetCompilationErrors(ParseJson(body));

                // ─── Project ───
                case "project/info":
                    return MCPProjectCommands.GetInfo();

                // ─── Animation ───
                case "animation/create-controller":
                    return MCPAnimationCommands.CreateController(ParseJson(body));
                case "animation/controller-info":
                    return MCPAnimationCommands.GetControllerInfo(ParseJson(body));
                case "animation/add-parameter":
                    return MCPAnimationCommands.AddParameter(ParseJson(body));
                case "animation/remove-parameter":
                    return MCPAnimationCommands.RemoveParameter(ParseJson(body));
                case "animation/add-state":
                    return MCPAnimationCommands.AddState(ParseJson(body));
                case "animation/remove-state":
                    return MCPAnimationCommands.RemoveState(ParseJson(body));
                case "animation/add-transition":
                    return MCPAnimationCommands.AddTransition(ParseJson(body));
                case "animation/create-clip":
                    return MCPAnimationCommands.CreateClip(ParseJson(body));
                case "animation/clip-info":
                    return MCPAnimationCommands.GetClipInfo(ParseJson(body));
                case "animation/set-clip-curve":
                    return MCPAnimationCommands.SetClipCurve(ParseJson(body));
                case "animation/add-layer":
                    return MCPAnimationCommands.AddLayer(ParseJson(body));
                case "animation/assign-controller":
                    return MCPAnimationCommands.AssignController(ParseJson(body));
                case "animation/get-curve-keyframes":
                    return MCPAnimationCommands.GetCurveKeyframes(ParseJson(body));
                case "animation/remove-curve":
                    return MCPAnimationCommands.RemoveCurve(ParseJson(body));
                case "animation/add-keyframe":
                    return MCPAnimationCommands.AddKeyframe(ParseJson(body));
                case "animation/remove-keyframe":
                    return MCPAnimationCommands.RemoveKeyframe(ParseJson(body));
                case "animation/add-event":
                    return MCPAnimationCommands.AddAnimationEvent(ParseJson(body));
                case "animation/remove-event":
                    return MCPAnimationCommands.RemoveAnimationEvent(ParseJson(body));
                case "animation/get-events":
                    return MCPAnimationCommands.GetAnimationEvents(ParseJson(body));
                case "animation/set-clip-settings":
                    return MCPAnimationCommands.SetClipSettings(ParseJson(body));
                case "animation/remove-transition":
                    return MCPAnimationCommands.RemoveTransition(ParseJson(body));
                case "animation/remove-layer":
                    return MCPAnimationCommands.RemoveLayer(ParseJson(body));
                case "animation/create-blend-tree":
                    return MCPAnimationCommands.CreateBlendTree(ParseJson(body));
                case "animation/get-blend-tree":
                    return MCPAnimationCommands.GetBlendTreeInfo(ParseJson(body));

                // ─── Prefab (Advanced) ───
                case "prefab/info":
                    return MCPPrefabCommands.GetPrefabInfo(ParseJson(body));
                case "prefab/create-variant":
                    return MCPPrefabCommands.CreateVariant(ParseJson(body));
                case "prefab/apply-overrides":
                    return MCPPrefabCommands.ApplyOverrides(ParseJson(body));
                case "prefab/revert-overrides":
                    return MCPPrefabCommands.RevertOverrides(ParseJson(body));
                case "prefab/unpack":
                    return MCPPrefabCommands.Unpack(ParseJson(body));
                case "prefab/set-object-reference":
                    return MCPPrefabCommands.SetObjectReference(ParseJson(body));
                case "prefab/duplicate":
                    return MCPPrefabCommands.Duplicate(ParseJson(body));
                case "prefab/set-active":
                    return MCPPrefabCommands.SetActive(ParseJson(body));
                case "prefab/reparent":
                    return MCPPrefabCommands.Reparent(ParseJson(body));

                // ─── Prefab Asset (Direct Editing) ───
                case "prefab-asset/hierarchy":
                    return MCPPrefabAssetCommands.GetHierarchy(ParseJson(body));
                case "prefab-asset/get-properties":
                    return MCPPrefabAssetCommands.GetComponentProperties(ParseJson(body));
                case "prefab-asset/set-property":
                    return MCPPrefabAssetCommands.SetComponentProperty(ParseJson(body));
                case "prefab-asset/add-component":
                    return MCPPrefabAssetCommands.AddComponent(ParseJson(body));
                case "prefab-asset/remove-component":
                    return MCPPrefabAssetCommands.RemoveComponent(ParseJson(body));
                case "prefab-asset/set-reference":
                    return MCPPrefabAssetCommands.SetReference(ParseJson(body));
                case "prefab-asset/add-gameobject":
                    return MCPPrefabAssetCommands.AddGameObject(ParseJson(body));
                case "prefab-asset/remove-gameobject":
                    return MCPPrefabAssetCommands.RemoveGameObject(ParseJson(body));

                // ─── Prefab Variant Management ───
                case "prefab-asset/variant-info":
                    return MCPPrefabAssetCommands.GetVariantInfo(ParseJson(body));
                case "prefab-asset/compare-variant":
                    return MCPPrefabAssetCommands.CompareVariantToBase(ParseJson(body));
                case "prefab-asset/apply-variant-override":
                    return MCPPrefabAssetCommands.ApplyVariantOverride(ParseJson(body));
                case "prefab-asset/revert-variant-override":
                    return MCPPrefabAssetCommands.RevertVariantOverride(ParseJson(body));
                case "prefab-asset/transfer-variant-overrides":
                    return MCPPrefabAssetCommands.TransferVariantOverrides(ParseJson(body));

                // ─── Physics ───
                case "physics/raycast":
                    return MCPPhysicsCommands.Raycast(ParseJson(body));
                case "physics/overlap-sphere":
                    return MCPPhysicsCommands.OverlapSphere(ParseJson(body));
                case "physics/overlap-box":
                    return MCPPhysicsCommands.OverlapBox(ParseJson(body));
                case "physics/collision-matrix":
                    return MCPPhysicsCommands.GetCollisionMatrix(ParseJson(body));
                case "physics/set-collision-layer":
                    return MCPPhysicsCommands.SetCollisionLayer(ParseJson(body));
                case "physics/set-gravity":
                    return MCPPhysicsCommands.SetGravity(ParseJson(body));

                // ─── Lighting ───
                case "lighting/info":
                    return MCPLightingCommands.GetLightingInfo(ParseJson(body));
                case "lighting/create":
                    return MCPLightingCommands.CreateLight(ParseJson(body));
                case "lighting/set-environment":
                    return MCPLightingCommands.SetEnvironment(ParseJson(body));
                case "lighting/create-reflection-probe":
                    return MCPLightingCommands.CreateReflectionProbe(ParseJson(body));
                case "lighting/create-light-probe-group":
                    return MCPLightingCommands.CreateLightProbeGroup(ParseJson(body));

                // ─── Audio ───
                case "audio/info":
                    return MCPAudioCommands.GetAudioInfo(ParseJson(body));
                case "audio/create-source":
                    return MCPAudioCommands.CreateAudioSource(ParseJson(body));
                case "audio/set-global":
                    return MCPAudioCommands.SetGlobalAudio(ParseJson(body));

                // ─── Tags & Layers ───
                case "taglayer/info":
                    return MCPTagLayerCommands.GetTagsAndLayers(ParseJson(body));
                case "taglayer/add-tag":
                    return MCPTagLayerCommands.AddTag(ParseJson(body));
                case "taglayer/set-tag":
                    return MCPTagLayerCommands.SetTag(ParseJson(body));
                case "taglayer/set-layer":
                    return MCPTagLayerCommands.SetLayer(ParseJson(body));
                case "taglayer/set-static":
                    return MCPTagLayerCommands.SetStatic(ParseJson(body));

                // ─── Selection & Scene View ───
                case "selection/get":
                    return MCPSelectionCommands.GetSelection(ParseJson(body));
                case "selection/set":
                    return MCPSelectionCommands.SetSelection(ParseJson(body));
                case "selection/focus-scene-view":
                    return MCPSelectionCommands.FocusSceneView(ParseJson(body));
                case "selection/find-by-type":
                    return MCPSelectionCommands.FindObjectsByType(ParseJson(body));

                // ─── Input Actions ───
                case "input/create":
                    return MCPInputCommands.CreateInputActions(ParseJson(body));
                case "input/info":
                    return MCPInputCommands.GetInputActionsInfo(ParseJson(body));
                case "input/add-map":
                    return MCPInputCommands.AddActionMap(ParseJson(body));
                case "input/remove-map":
                    return MCPInputCommands.RemoveActionMap(ParseJson(body));
                case "input/add-action":
                    return MCPInputCommands.AddAction(ParseJson(body));
                case "input/remove-action":
                    return MCPInputCommands.RemoveAction(ParseJson(body));
                case "input/add-binding":
                    return MCPInputCommands.AddBinding(ParseJson(body));
                case "input/add-composite-binding":
                    return MCPInputCommands.AddCompositeBinding(ParseJson(body));

                // ─── Assembly Definitions ───
                case "asmdef/create":
                    return MCPAssemblyDefCommands.CreateAssemblyDef(ParseJson(body));
                case "asmdef/info":
                    return MCPAssemblyDefCommands.GetAssemblyDefInfo(ParseJson(body));
                case "asmdef/list":
                    return MCPAssemblyDefCommands.ListAssemblyDefs(ParseJson(body));
                case "asmdef/add-references":
                    return MCPAssemblyDefCommands.AddReferences(ParseJson(body));
                case "asmdef/remove-references":
                    return MCPAssemblyDefCommands.RemoveReferences(ParseJson(body));
                case "asmdef/set-platforms":
                    return MCPAssemblyDefCommands.SetPlatforms(ParseJson(body));
                case "asmdef/update-settings":
                    return MCPAssemblyDefCommands.UpdateSettings(ParseJson(body));
                case "asmdef/create-ref":
                    return MCPAssemblyDefCommands.CreateAssemblyRef(ParseJson(body));

                // ─── Profiler ───
                case "profiler/enable":
                    return MCPProfilerCommands.EnableProfiler(ParseJson(body));
                case "profiler/stats":
                    return MCPProfilerCommands.GetRenderingStats(ParseJson(body));
                case "profiler/memory":
                    return MCPProfilerCommands.GetMemoryInfo(ParseJson(body));
                case "profiler/frame-data":
                    return MCPProfilerCommands.GetFrameData(ParseJson(body));
                case "profiler/analyze":
                    return MCPProfilerCommands.AnalyzePerformance(ParseJson(body));

                // ─── Frame Debugger ───
                case "debugger/enable":
                    return MCPProfilerCommands.EnableFrameDebugger(ParseJson(body));
                case "debugger/events":
                    return MCPProfilerCommands.GetFrameEvents(ParseJson(body));
                case "debugger/event-details":
                    return MCPProfilerCommands.GetFrameEventDetails(ParseJson(body));

                // ─── Memory Profiler ───
                case "profiler/memory-status":
                    return MCPMemoryProfilerCommands.GetStatus(ParseJson(body));
                case "profiler/memory-breakdown":
                    return MCPMemoryProfilerCommands.GetMemoryBreakdown(ParseJson(body));
                case "profiler/memory-top-assets":
                    return MCPMemoryProfilerCommands.GetTopMemoryConsumers(ParseJson(body));
                case "profiler/memory-snapshot":
                    return MCPMemoryProfilerCommands.TakeMemorySnapshot(ParseJson(body));

                // ─── Agent Management ───
                case "agents/list":
                    return MCPRequestQueue.GetActiveSessions();
                case "agents/log":
                {
                    var agentArgs = ParseJson(body);
                    string id = agentArgs.ContainsKey("agentId") ? agentArgs["agentId"].ToString() : "";
                    return new Dictionary<string, object>
                    {
                        { "agentId", id },
                        { "log", MCPRequestQueue.GetAgentLog(id) },
                    };
                }

                // ─── Search ───
                case "search/by-component":
                    return MCPSearchCommands.FindByComponent(ParseJson(body));
                case "search/by-tag":
                    return MCPSearchCommands.FindByTag(ParseJson(body));
                case "search/by-layer":
                    return MCPSearchCommands.FindByLayer(ParseJson(body));
                case "search/by-name":
                    return MCPSearchCommands.FindByName(ParseJson(body));
                case "search/by-shader":
                    return MCPSearchCommands.FindByShader(ParseJson(body));
                case "search/assets":
                    return MCPSearchCommands.SearchAssets(ParseJson(body));
                case "search/missing-references":
                    return MCPSearchCommands.FindMissingReferences(ParseJson(body));
                case "search/scene-stats":
                    return MCPSearchCommands.GetSceneStats(ParseJson(body));
                // VRseBuilder SDK
                case "vrse/status":
                    return MCPVRseBuilderCommands.GetStatus(ParseJson(body));
                case "vrse/login":
                    return MCPVRseBuilderCommands.Login(ParseJson(body));
                case "vrse/list-projects":
                    return MCPVRseBuilderCommands.ListProjects(ParseJson(body));
                case "vrse/select-project":
                    return MCPVRseBuilderCommands.SelectProject(ParseJson(body));
                case "vrse/list-modules":
                    return MCPVRseBuilderCommands.ListModules(ParseJson(body));
                case "vrse/open-menu-scene":
                    return MCPVRseBuilderCommands.OpenMenuScene(ParseJson(body));
                case "vrse/open-module":
                    return MCPVRseBuilderCommands.OpenModule(ParseJson(body));
                case "vrse/open-room-manager-config":
                    return MCPVRseBuilderCommands.OpenRoomManagerConfig(ParseJson(body));
                case "vrse/get-selected-project":
                    return MCPVRseBuilderCommands.GetSelectedProject(ParseJson(body));
                case "vrse/get-project-config":
                    return MCPVRseBuilderCommands.GetProjectConfig(ParseJson(body));
                case "vrse/ensure-project-settings":
                    return MCPVRseBuilderCommands.EnsureProjectSettings(ParseJson(body));
                case "vrse/apply-project-settings":
                    return MCPVRseBuilderCommands.ApplyProjectSettings(ParseJson(body));
                case "vrse/open-studio-project-window":
                    return MCPVRseBuilderCommands.OpenStudioProjectWindow(ParseJson(body));
                case "vrse/open-project-config-window":
                    return MCPVRseBuilderCommands.OpenProjectConfigWindow(ParseJson(body));
                case "vrse/open-build-tool":
                    return MCPVRseBuilderCommands.OpenBuildToolWindow(ParseJson(body));
                case "vrse/create-experience":
                    return MCPVRseBuilderCommands.CreateExperience(ParseJson(body));
                case "vrse/get-experience-creation-status":
                    return MCPVRseBuilderCommands.GetExperienceCreationStatus(ParseJson(body));
                case "vrse/open-art-scene":
                    return MCPVRseBuilderCommands.OpenArtScene(ParseJson(body));
                case "vrse/story-add-trigger-set":
                    return MCPVRseBuilderCommands.StoryAddTriggerSet(ParseJson(body));
                case "vrse/story-add-action":
                    return MCPVRseBuilderCommands.StoryAddAction(ParseJson(body));
                case "vrse/story-update-node":
                    return MCPVRseBuilderCommands.StoryUpdateNode(ParseJson(body));
                case "vrse/story-save":
                    return MCPVRseBuilderCommands.StorySave(ParseJson(body));
                case "vrse/story-validate":
                    return MCPVRseBuilderCommands.StoryValidate(ParseJson(body));
                case "vrse/story-remove-node-by-name":
                    return MCPVRseBuilderCommands.StoryRemoveNodeByName(ParseJson(body));
                case "vrse/story-apply-json":
                    return MCPVRseBuilderCommands.StoryApplyJson(ParseJson(body));
                case "vrse/story-patch":
                    return MCPVRseBuilderCommands.StoryPatch(ParseJson(body));
                case "vrse/story-get-info":
                    return MCPVRseBuilderCommands.StoryGetInfo(ParseJson(body));
                case "vrse/story-undo-write":
                    return MCPVRseBuilderCommands.StoryUndoWrite(ParseJson(body));
                case "vrse/apply-moment-weightage":
                    return MCPVRseBuilderCommands.ApplyMomentWeightage(ParseJson(body));
                case "vrse/story-has-pending-vo":
                    return MCPVRseBuilderCommands.StoryHasPendingVO(ParseJson(body));
                case "vrse/create-evaluation-from-training":
                    return MCPVRseBuilderCommands.CreateEvaluationFromTraining(ParseJson(body));
                case "vrse/story-read":
                    return MCPVRseBuilderCommands.StoryRead(ParseJson(body));
                case "vrse/story-list-node-templates":
                    return MCPVRseBuilderCommands.StoryListNodeTemplates(ParseJson(body));
                case "vrse/query-objects-list":
                    return MCPVRseBuilderCommands.QueryObjectsList(ParseJson(body));
                case "vrse/story-remove-action":
                    return MCPVRseBuilderCommands.StoryRemoveAction(ParseJson(body));
                case "vrse/story-add-chapter":
                    return MCPVRseBuilderCommands.StoryAddChapter(ParseJson(body));
                case "vrse/story-rename-chapter":
                    return MCPVRseBuilderCommands.StoryRenameChapter(ParseJson(body));
                case "vrse/story-remove-chapter":
                    return MCPVRseBuilderCommands.StoryRemoveChapter(ParseJson(body));
                case "vrse/story-add-moment":
                    return MCPVRseBuilderCommands.StoryAddMoment(ParseJson(body));
                case "vrse/story-rename-moment":
                    return MCPVRseBuilderCommands.StoryRenameMoment(ParseJson(body));
                case "vrse/story-remove-moment":
                    return MCPVRseBuilderCommands.StoryRemoveMoment(ParseJson(body));
                case "vrse/story-move-action":
                    return MCPVRseBuilderCommands.StoryMoveAction(ParseJson(body));
                case "vrse/story-duplicate-action":
                    return MCPVRseBuilderCommands.StoryDuplicateAction(ParseJson(body));
                case "vrse/story-defaults-get":
                    return MCPVRseBuilderCommands.StoryDefaultsGet(ParseJson(body));
                case "vrse/building-blocks-list":
                    return MCPVRseBuilderCommands.BuildingBlocksList(ParseJson(body));
                case "vrse/building-blocks-instantiate":
                    return MCPVRseBuilderCommands.BuildingBlocksInstantiate(ParseJson(body));
                case "vrse/scene-hierarchy-checkup":
                    return MCPVRseBuilderCommands.SceneHierarchyCheckup(ParseJson(body));
                case "vrse/module-set-include-in-build":
                    return MCPVRseBuilderCommands.ModuleSetIncludeInBuild(ParseJson(body));
                case "vrse/build-start":
                    return MCPVRseBuilderCommands.BuildStart(ParseJson(body));
                case "vrse/build-status":
                    return MCPVRseBuilderCommands.BuildStatus(ParseJson(body));
                case "vrse/story-apply-action-to-multiple-moments":
                    return MCPVRseBuilderCommands.StoryApplyActionToMultipleMoments(ParseJson(body));
                case "vrse/list-story-backups":
                    return MCPVRseBuilderCommands.ListStoryBackups(ParseJson(body));
                case "vrse/create-story-backup":
                    return MCPVRseBuilderCommands.CreateStoryBackup(ParseJson(body));
                case "vrse/restore-story-backup":
                    return MCPVRseBuilderCommands.RestoreStoryBackup(ParseJson(body));
                case "vrse/story-search-node-templates":
                    return MCPVRseBuilderCommands.StorySearchNodeTemplates(ParseJson(body));
                case "vrse/story-generate-vo":
                    return MCPVRseBuilderCommands.StoryGenerateVO(ParseJson(body));
                case "vrse/interactable_convert":
                    return MCPInteractableCommands.InteractableConvert(ParseJson(body));
                case "vrse/create-rotator-from-mesh/analyze":
                    return MCPVRseBuilderCommands.PivotRotateLimiterAnalyze(ParseJson(body));
                case "vrse/create-rotator-from-mesh/create":
                    return MCPVRseBuilderCommands.PivotRotateLimiterCreate(ParseJson(body));
                case "vrse/setup-objects":
                    return MCPSetupObjectsCommands.SetupObjects(ParseJson(body));
                case "vrse/place-objects":
                    return MCPSetupObjectsCommands.PlaceObjects(ParseJson(body));
                case "vrse/harvest-ids":
                    return MCPSetupObjectsCommands.HarvestIds(ParseJson(body));
                case "vrse/create-button-from-mesh/analyze":
                    return MCPPhysicalButtonCommands.Analyze(ParseJson(body));
                case "vrse/create-button-from-mesh/create":
                    return MCPPhysicalButtonCommands.Create(ParseJson(body));
                case "vrse/story/report":
                    return MCPStoryReportCommands.GetReport(ParseJson(body));
                case "vrse/ui/general-setup":
                    return MCPGeneralUISetupCommands.Execute(ParseJson(body));

                // ─── Project Settings ───
                case "settings/quality":
                    return MCPProjectSettingsCommands.GetQualitySettings(ParseJson(body));
                case "settings/quality-level":
                    return MCPProjectSettingsCommands.SetQualityLevel(ParseJson(body));
                case "settings/physics":
                    return MCPProjectSettingsCommands.GetPhysicsSettings(ParseJson(body));
                case "settings/set-physics":
                    return MCPProjectSettingsCommands.SetPhysicsSettings(ParseJson(body));
                case "settings/time":
                    return MCPProjectSettingsCommands.GetTimeSettings(ParseJson(body));
                case "settings/set-time":
                    return MCPProjectSettingsCommands.SetTimeSettings(ParseJson(body));
                case "settings/player":
                    return MCPProjectSettingsCommands.GetPlayerSettings(ParseJson(body));
                case "settings/set-player":
                    return MCPProjectSettingsCommands.SetPlayerSettings(ParseJson(body));
                case "settings/render-pipeline":
                    return MCPProjectSettingsCommands.GetRenderPipelineInfo(ParseJson(body));

                // ─── Undo ───
                case "undo/perform":
                    return MCPUndoCommands.PerformUndo(ParseJson(body));
                case "undo/redo":
                    return MCPUndoCommands.PerformRedo(ParseJson(body));
                case "undo/history":
                    return MCPUndoCommands.GetUndoHistory(ParseJson(body));
                case "undo/clear":
                    return MCPUndoCommands.ClearUndo(ParseJson(body));

                // ─── Screenshot / Scene View ───
                case "screenshot/game":
                    return MCPScreenshotCommands.CaptureGameView(ParseJson(body));
                case "screenshot/scene":
                    return MCPScreenshotCommands.CaptureSceneView(ParseJson(body));
                case "sceneview/info":
                    return MCPScreenshotCommands.GetSceneViewInfo(ParseJson(body));
                case "sceneview/set-camera":
                    return MCPScreenshotCommands.SetSceneViewCamera(ParseJson(body));

                // ─── Graphics & Visuals ───
                case "graphics/asset-preview":
                    return MCPGraphicsCommands.CaptureAssetPreview(ParseJson(body));
                case "graphics/scene-capture":
                    return MCPGraphicsCommands.CaptureSceneView(ParseJson(body));
                case "graphics/game-capture":
                    return MCPGraphicsCommands.CaptureGameView(ParseJson(body));
                case "graphics/prefab-render":
                    return MCPGraphicsCommands.RenderPrefabPreview(ParseJson(body));
                case "graphics/mesh-info":
                    return MCPGraphicsCommands.GetMeshInfo(ParseJson(body));
                case "graphics/material-info":
                    return MCPGraphicsCommands.GetMaterialInfo(ParseJson(body));
                case "graphics/texture-info":
                    return MCPGraphicsCommands.GetTextureInfo(ParseJson(body));
                case "graphics/renderer-info":
                    return MCPGraphicsCommands.GetRendererInfo(ParseJson(body));
                case "graphics/lighting-summary":
                    return MCPGraphicsCommands.GetLightingSummary(ParseJson(body));

                // ─── Particle System ───
                case "particle/create":
                    return MCPParticleCommands.CreateParticleSystem(ParseJson(body));
                case "particle/info":
                    return MCPParticleCommands.GetParticleSystemInfo(ParseJson(body));
                case "particle/set-main":
                    return MCPParticleCommands.SetMainModule(ParseJson(body));
                case "particle/set-emission":
                    return MCPParticleCommands.SetEmission(ParseJson(body));
                case "particle/set-shape":
                    return MCPParticleCommands.SetShape(ParseJson(body));
                case "particle/playback":
                    return MCPParticleCommands.PlaybackControl(ParseJson(body));

                // ─── ScriptableObject ───
                case "scriptableobject/create":
                    return MCPScriptableObjectCommands.CreateScriptableObject(ParseJson(body));
                case "scriptableobject/info":
                    return MCPScriptableObjectCommands.GetScriptableObjectInfo(ParseJson(body));
                case "scriptableobject/set-field":
                    return MCPScriptableObjectCommands.SetScriptableObjectField(ParseJson(body));
                case "scriptableobject/list-types":
                    return MCPScriptableObjectCommands.ListScriptableObjectTypes(ParseJson(body));

                // ─── Texture ───
                case "texture/info":
                    return MCPTextureCommands.GetTextureInfo(ParseJson(body));
                case "texture/set-import":
                    return MCPTextureCommands.SetTextureImportSettings(ParseJson(body));
                case "texture/reimport":
                    return MCPTextureCommands.ReimportTexture(ParseJson(body));
                case "texture/set-sprite":
                    return MCPTextureCommands.SetAsSprite(ParseJson(body));
                case "texture/set-normalmap":
                    return MCPTextureCommands.SetAsNormalMap(ParseJson(body));

                // ─── UI ───
                case "ui/create-canvas":
                    return MCPUICommands.CreateCanvas(ParseJson(body));
                case "ui/create-element":
                    return MCPUICommands.CreateUIElement(ParseJson(body));
                case "ui/info":
                    return MCPUICommands.GetUIInfo(ParseJson(body));
                case "ui/set-text":
                    return MCPUICommands.SetUIText(ParseJson(body));
                case "ui/set-image":
                    return MCPUICommands.SetUIImage(ParseJson(body));

                // ─── Package Manager ───
                case "packages/list":
                    return MCPPackageManagerCommands.ListPackages(ParseJson(body));
                case "packages/add":
                    return MCPPackageManagerCommands.AddPackage(ParseJson(body));
                case "packages/remove":
                    return MCPPackageManagerCommands.RemovePackage(ParseJson(body));
                case "packages/search":
                    return MCPPackageManagerCommands.SearchPackage(ParseJson(body));
                case "packages/info":
                    return MCPPackageManagerCommands.GetPackageInfo(ParseJson(body));

                // ─── Constraints & LOD ───
                case "constraint/add":
                    return MCPConstraintCommands.AddConstraint(ParseJson(body));
                case "constraint/info":
                    return MCPConstraintCommands.GetConstraintInfo(ParseJson(body));
                case "lod/create":
                    return MCPConstraintCommands.CreateLODGroup(ParseJson(body));
                case "lod/info":
                    return MCPConstraintCommands.GetLODGroupInfo(ParseJson(body));

                // ─── Prefs ───
                case "editorprefs/get":
                    return MCPPrefsCommands.GetEditorPref(ParseJson(body));
                case "editorprefs/set":
                    return MCPPrefsCommands.SetEditorPref(ParseJson(body));
                case "editorprefs/delete":
                    return MCPPrefsCommands.DeleteEditorPref(ParseJson(body));
                case "playerprefs/get":
                    return MCPPrefsCommands.GetPlayerPref(ParseJson(body));
                case "playerprefs/set":
                    return MCPPrefsCommands.SetPlayerPref(ParseJson(body));
                case "playerprefs/delete":
                    return MCPPrefsCommands.DeletePlayerPref(ParseJson(body));
                case "playerprefs/delete-all":
                    return MCPPrefsCommands.DeleteAllPlayerPrefs(ParseJson(body));

                // ─── MPPM Scenario Management ───
                case "scenario/list":
                    return MCPScenarioCommands.ListScenarios(ParseJson(body));
                case "scenario/status":
                    return MCPScenarioCommands.GetScenarioStatus(ParseJson(body));
                case "scenario/activate":
                    return MCPScenarioCommands.ActivateScenario(ParseJson(body));
                case "scenario/start":
                    return MCPScenarioCommands.StartScenario(ParseJson(body));
                case "scenario/stop":
                    return MCPScenarioCommands.StopScenario(ParseJson(body));
                case "scenario/info":
                    return MCPScenarioCommands.GetMultiplayerInfo(ParseJson(body));

                // ─── VRse Parity Layer (replaces unity-mcp-pro tools the build pipeline depends on) ───
                case "vrse/parity/batch-execute":
                    return MCPParityCommands.BatchExecute(ParseJson(body));
                case "vrse/parity/get-components":
                    return MCPParityCommands.GetAllComponents(ParseJson(body));
                case "vrse/parity/get-screenshot-inline":
                    return MCPParityCommands.CaptureSceneViewInline(ParseJson(body));
                case "vrse/parity/list-loaded-scenes":
                    return MCPParityCommands.SearchAllLoadedScenes(ParseJson(body));

                // ─── VRse Spatial (ported from unity-mcp-pro for no-marker placement) ───
                case "vrse/spatial/analyze-scene":
                    return MCPSpatialCommands.AnalyzeScene(ParseJson(body));
                case "vrse/spatial/get-bounds":
                    return MCPSpatialCommands.GetBounds(ParseJson(body));
                case "vrse/spatial/get-surface":
                    return MCPSpatialCommands.FindSurface(ParseJson(body));
                case "vrse/spatial/check-placement":
                    return MCPSpatialCommands.CheckPlacement(ParseJson(body));
                case "vrse/spatial/list-probe-surfaces":
                    return MCPSpatialCommands.ProbeSurfaces(ParseJson(body));

                default:
                    return new { error = $"Unknown API endpoint: {path}" };
            }
        }

        /// <summary>
        /// Public dispatcher used by VRse parity commands (notably batch-execute) to invoke
        /// other API routes in-process on the main thread. Serializes args to JSON, then
        /// delegates to RouteRequest. Returns the same shape as a direct HTTP call.
        ///
        /// This is internal to the assembly — only VRse parity commands use it.
        /// External callers must go through the HTTP listener.
        /// </summary>
        internal static object ExecuteRouteInternal(string path, Dictionary<string, object> args)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required");

            string body = args != null ? MiniJson.Serialize(args) : "";
            return RouteRequest(path, "POST", body);
        }

        // ─── Helpers ───

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            return MiniJson.Deserialize(json) as Dictionary<string, object>
                ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Execute a function on Unity's main thread and wait for the result.
        /// Used by the legacy synchronous path.
        /// </summary>
        private static object ExecuteOnMainThread(Func<object> action)
        {
            if (Thread.CurrentThread.ManagedThreadId == 1)
                return action();

            object result = null;
            Exception exception = null;
            var resetEvent = new ManualResetEventSlim(false);

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    try { result = action(); }
                    catch (Exception ex) { exception = ex; }
                    finally { resetEvent.Set(); }
                });
            }

            if (!resetEvent.Wait(MCPRequestQueue.SyncTimeoutMs))
                return new { error = $"Timeout waiting for Unity main thread after {MCPRequestQueue.SyncTimeoutMs / 1000}s" };

            if (exception != null)
                return new { error = exception.Message, stackTrace = exception.StackTrace };

            return result;
        }

        private static void ProcessMainThreadQueue()
        {
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action?.Invoke(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AB-UMCP] Main thread action error: {ex}");
                    }
                }
            }
        }

        // Response size limits (bytes) — prevents oversized payloads from crashing the MCP stdio pipe
        private const int ResponseSoftLimitBytes = 8 * 1024 * 1024;  // 8 MB — log warning
        private const int ResponseHardLimitBytes = 16 * 1024 * 1024; // 16 MB — replace with error

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            string json = MiniJson.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            // Size validation — protect against Write EOF on large projects
            if (buffer.Length > ResponseHardLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Response too large ({buffer.Length / (1024 * 1024)}MB), replacing with error. Use pagination parameters.");
                var errorData = new Dictionary<string, object>
                {
                    { "error", "response_too_large" },
                    { "size", buffer.Length },
                    { "limit", ResponseHardLimitBytes },
                    { "message", "Response exceeded size limit. Use pagination parameters (maxNodes, limit, maxResults) to request smaller chunks." },
                };
                json = MiniJson.Serialize(errorData);
                buffer = Encoding.UTF8.GetBytes(json);
                response.StatusCode = 413; // Payload Too Large
            }
            else if (buffer.Length > ResponseSoftLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Large response ({buffer.Length / (1024 * 1024)}MB). Consider using pagination parameters.");
            }

            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }
}
