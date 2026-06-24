using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Parity-layer commands that re-implement unity-mcp-pro tools the VRse build
    /// pipeline depends on but VrseBuilder MCP doesn't ship natively.
    ///
    /// Goal: zero-regression migration from unity-mcp-pro to VrseBuilder MCP.
    /// Each method here keeps the same input/output shape as its unity-mcp-pro counterpart
    /// so existing skill files only need a tool-NAME rename, not a param/value adjustment.
    ///
    /// Tools covered:
    ///   - vrse/parity/batch-execute             ↔ unity-mcp-pro batch_execute
    ///   - vrse/parity/get-components            ↔ unity-mcp-pro get_components (all-components form)
    ///   - vrse/parity/get-screenshot-inline     ↔ unity-mcp-pro get_editor_screenshot (base64 inline)
    ///   - vrse/parity/list-loaded-scenes        ↔ unity-mcp-pro find_gameobjects (additive-scene support)
    ///   - compilation/errors                    ↔ unity-mcp-pro get_compilation_errors (missing-route fill)
    /// </summary>
    [InitializeOnLoad]
    public static class MCPParityCommands
    {
        // ─── Compilation error cache ───
        // VrseBuilder MCP's editor-tools.js calls "compilation/errors" but no C# handler exists.
        // We subscribe to CompilationPipeline events to cache the latest compiler messages, giving us
        // unity-mcp-pro parity for the build pipeline's "did my script compile?" gate.
        private struct CachedCompilerMessage
        {
            public string assembly;
            public string file;
            public int line;
            public int column;
            public string severity;   // "error" | "warning"
            public string code;       // e.g. "CS0103"
            public string message;
            public DateTime timestamp;
        }
        private static readonly List<CachedCompilerMessage> _compilerMessages = new List<CachedCompilerMessage>();
        private const int MaxCompilerMessages = 500;

        static MCPParityCommands()
        {
            // Subscribe once at editor load. Multiple subscriptions are de-duped by Unity.
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            // Clear cache when a fresh compile starts so we don't show stale entries from a prior cycle.
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
        }

        private static void OnCompilationStarted(object _)
        {
            lock (_compilerMessages) { _compilerMessages.Clear(); }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            string asmName = Path.GetFileNameWithoutExtension(assemblyPath);

            lock (_compilerMessages)
            {
                foreach (var m in messages)
                {
                    // CompilerMessage.type is Error or Warning enum
                    string sev = m.type == CompilerMessageType.Error ? "error" : "warning";
                    // Try to extract CS#### code from the message body
                    string code = "";
                    var codeMatch = Regex.Match(m.message ?? "", @"\b(CS\d+)\b");
                    if (codeMatch.Success) code = codeMatch.Groups[1].Value;

                    _compilerMessages.Add(new CachedCompilerMessage
                    {
                        assembly = asmName,
                        file = m.file,
                        line = m.line,
                        column = m.column,
                        severity = sev,
                        code = code,
                        message = m.message,
                        timestamp = DateTime.Now
                    });
                }
                if (_compilerMessages.Count > MaxCompilerMessages)
                    _compilerMessages.RemoveRange(0, _compilerMessages.Count - MaxCompilerMessages);
            }
        }

        // ══════════════════════════════════════════════
        // TOOL: get_compilation_errors  (fills the missing-route gap)
        // ══════════════════════════════════════════════
        public static object GetCompilationErrors(Dictionary<string, object> args)
        {
            int count = GetInt(args, "count", 50);
            string severity = GetStr(args, "severity", "all").ToLowerInvariant();

            var entries = new List<Dictionary<string, object>>();
            int totalErrors = 0;
            int totalWarnings = 0;

            lock (_compilerMessages)
            {
                // Walk most-recent-first so we return the latest N matching entries.
                for (int i = _compilerMessages.Count - 1; i >= 0 && entries.Count < count; i--)
                {
                    var m = _compilerMessages[i];
                    if (m.severity == "error") totalErrors++;
                    else if (m.severity == "warning") totalWarnings++;

                    if (severity != "all" && m.severity != severity) continue;

                    entries.Add(new Dictionary<string, object>
                    {
                        { "assembly", m.assembly },
                        { "file", m.file },
                        { "line", m.line },
                        { "column", m.column },
                        { "severity", m.severity },
                        { "code", m.code },
                        { "message", m.message },
                        { "timestamp", m.timestamp.ToString("HH:mm:ss.fff") }
                    });
                }
            }
            entries.Reverse(); // chronological order

            return new Dictionary<string, object>
            {
                { "success", true },
                { "isCompiling", EditorApplication.isCompiling },
                { "totalErrors", totalErrors },
                { "totalWarnings", totalWarnings },
                { "returned", entries.Count },
                { "entries", entries }
            };
        }


        // ─── Param helpers (mirror unity-mcp-pro signature conventions) ───

        private static string GetStr(Dictionary<string, object> p, string key, string fallback = "")
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            return p[key].ToString();
        }

        private static int GetInt(Dictionary<string, object> p, string key, int fallback)
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToInt32(p[key]); }
            catch { return fallback; }
        }

        private static bool GetBool(Dictionary<string, object> p, string key, bool fallback)
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToBoolean(p[key]); }
            catch { return fallback; }
        }

        // Map MCP tool names (unity_*) → C# route paths. Lets unity_batch_execute callers use either
        // form for inner `method` values — keeps skill files readable while remaining strict about
        // routing inside the plugin. If a name isn't in this table the method is passed through as-is,
        // so route paths like "scene/save" continue to work directly.
        private static readonly Dictionary<string, string> _toolNameToRoute = new Dictionary<string, string>
        {
            // Scene
            { "unity_scene_info",                "scene/info" },
            { "unity_scene_open",                "scene/open" },
            { "unity_scene_save",                "scene/save" },
            { "unity_scene_new",                 "scene/new" },
            { "unity_scene_hierarchy",           "scene/hierarchy" },
            // GameObject
            { "unity_gameobject_create",         "gameobject/create" },
            { "unity_gameobject_delete",         "gameobject/delete" },
            { "unity_gameobject_info",           "gameobject/info" },
            { "unity_gameobject_set_transform",  "gameobject/set-transform" },
            // Component
            { "unity_component_add",             "component/add" },
            { "unity_component_remove",          "component/remove" },
            { "unity_component_get_properties",  "component/get-properties" },
            { "unity_component_set_property",    "component/set-property" },
            { "unity_component_set_reference",   "component/set-reference" },
            // Asset
            { "unity_asset_instantiate_prefab",  "asset/instantiate-prefab" },
            { "unity_asset_list",                "asset/list" },
            // Editor
            { "unity_editor_state",              "editor/state" },
            { "unity_editor_ping",               "ping" },
            { "unity_play_mode",                 "editor/play-mode" },
            { "unity_execute_menu_item",         "editor/execute-menu-item" },
            { "unity_execute_code",              "editor/execute-code" },
            { "unity_project_info",              "project/info" },
            // Compile / console / search
            { "unity_get_compilation_errors",    "compilation/errors" },
            { "unity_console_log",               "console/log" },
            { "unity_search_missing_references", "search/missing-references" },
            { "unity_search_by_name",            "search/by-name" },
            { "unity_search_by_component",       "search/by-component" },
            { "unity_search_by_tag",             "search/by-tag" },
            { "unity_search_by_layer",           "search/by-layer" },
            // Selection
            { "unity_selection_set",             "selection/set" },
            { "unity_selection_get",             "selection/get" },
            // Physics
            { "unity_physics_raycast",           "physics/raycast" },
            { "unity_physics_overlap_box",       "physics/overlap-box" },
            { "unity_physics_overlap_sphere",    "physics/overlap-sphere" },
            // VRse parity (this file's tools)
            { "unity_batch_execute",             "vrse/parity/batch-execute" },
            { "unity_gameobject_components",     "vrse/parity/get-components" },
            { "unity_screenshot_inline",         "vrse/parity/get-screenshot-inline" },
            { "unity_search_all_loaded_scenes",  "vrse/parity/list-loaded-scenes" },
            // VRse spatial
            { "unity_gameobject_bounds",         "vrse/spatial/get-bounds" },
            { "unity_spatial_analyze_scene",     "vrse/spatial/analyze-scene" },
            { "unity_spatial_find_surface",      "vrse/spatial/get-surface" },
            { "unity_spatial_check_placement",   "vrse/spatial/check-placement" },
            { "unity_spatial_probe_surfaces",    "vrse/spatial/list-probe-surfaces" },
        };

        // Detect whether a RouteRequest result represents a FAILURE. RouteRequest returns error
        // *objects* (not thrown exceptions) for unknown routes, disabled categories, and most handler
        // failures, in several shapes: Dictionary with "error" key, anonymous `new { error = ... }`,
        // or `{ success = false, ... }`. This inspects all of them.
        private static bool ResultIsError(object result, out string errorMsg)
        {
            errorMsg = null;
            if (result == null) return false;

            // Dictionary shape
            if (result is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("error", out var e) && e != null) { errorMsg = e.ToString(); return true; }
                if (dict.TryGetValue("success", out var s) && s is bool sb && !sb)
                {
                    errorMsg = dict.TryGetValue("message", out var m) && m != null ? m.ToString() : "operation reported success=false";
                    return true;
                }
                return false;
            }

            // Anonymous / POCO shape — reflect for `error` / `success` properties
            var t = result.GetType();
            var errProp = t.GetProperty("error");
            if (errProp != null)
            {
                var val = errProp.GetValue(result);
                if (val != null) { errorMsg = val.ToString(); return true; }
            }
            var succProp = t.GetProperty("success");
            if (succProp != null)
            {
                var val = succProp.GetValue(result);
                if (val is bool b && !b) { errorMsg = "operation reported success=false"; return true; }
            }
            return false;
        }

        // ══════════════════════════════════════════════
        // TOOL: batch_execute
        // Run a list of commands sequentially in one main-thread tick.
        // Mirrors unity-mcp-pro/plugin/Editor/Commands/BatchCommands.cs
        // Inner `method` values can be either MCP tool names (e.g. "unity_scene_save")
        // OR raw API route paths (e.g. "scene/save"). Tool names are translated via _toolNameToRoute.
        // ══════════════════════════════════════════════
        public static object BatchExecute(Dictionary<string, object> args)
        {
            var commandsRaw = args != null && args.ContainsKey("commands") ? args["commands"] as List<object> : null;
            bool stopOnError = GetBool(args, "stop_on_error", true);

            if (commandsRaw == null || commandsRaw.Count == 0)
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "commands is required and must be a non-empty array" }
                };

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP: Batch Execute");

            var results = new List<object>();
            int succeeded = 0;
            int failed = 0;

            foreach (var cmdObj in commandsRaw)
            {
                var cmd = cmdObj as Dictionary<string, object>;
                if (cmd == null)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "method", "unknown" }, { "success", false }, { "error", "command must be an object" }
                    });
                    failed++;
                    if (stopOnError) break;
                    continue;
                }

                string method = cmd.ContainsKey("method") ? cmd["method"].ToString() : null;
                var cmdParams = cmd.ContainsKey("params") ? cmd["params"] as Dictionary<string, object>
                                                          : new Dictionary<string, object>();

                if (string.IsNullOrEmpty(method))
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "method", "unknown" }, { "success", false }, { "error", "method is required" }
                    });
                    failed++;
                    if (stopOnError) break;
                    continue;
                }

                try
                {
                    // Translate MCP tool name → C# route path if needed; pass-through otherwise.
                    string routePath = _toolNameToRoute.TryGetValue(method, out var mapped) ? mapped : method;
                    // Dispatch via MCPBridgeServer.ExecuteRouteInternal so we reuse all routing logic.
                    var result = MCPBridgeServer.ExecuteRouteInternal(routePath, cmdParams ?? new Dictionary<string, object>());

                    // CRITICAL: RouteRequest returns an ERROR OBJECT (not a thrown exception) for unknown
                    // routes, disabled categories, and most handler failures. A try/catch alone would mark
                    // these as success. Inspect the result for an error/`success:false` signal so failed
                    // sub-commands are reported honestly — the pipeline's batch error-handling depends on it.
                    if (ResultIsError(result, out string resultErr))
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "method", method }, { "success", false }, { "error", resultErr }, { "result", result }
                        });
                        failed++;
                        if (stopOnError)
                        {
                            Undo.RevertAllDownToGroup(undoGroup);
                            break;
                        }
                    }
                    else
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "method", method }, { "success", true }, { "result", result }
                        });
                        succeeded++;
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "method", method }, { "success", false }, { "error", ex.Message }
                    });
                    failed++;
                    if (stopOnError)
                    {
                        Undo.RevertAllDownToGroup(undoGroup);
                        break;
                    }
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "totalCommands", commandsRaw.Count },
                { "succeeded", succeeded },
                { "failed", failed },
                { "stoppedOnError", stopOnError && failed > 0 },
                { "results", results }
            };
        }

        // Fast, INACTIVE-AWARE GameObject resolver. The shared MCPGameObjectCommands.FindGameObject
        // uses GameObject.Find() (which SKIPS inactive objects) then falls back to an O(n) scan over
        // EVERY loaded object — catastrophically slow (~131s) when a 1000+ object art scene is loaded
        // additively and the target is an inactive converted object (enableOnStart=false).
        // This resolver walks scene roots → children by name (includes inactive), O(path-depth).
        // Returns null if not resolvable by path, so the caller can fall back.
        private static GameObject ResolveScopedByPath(Dictionary<string, object> args)
        {
            // instanceId shortcut (already O(1) in the shared resolver, but handle it here too)
            if (args.ContainsKey("instanceId") && args["instanceId"] != null)
            {
                try { return EditorUtility.InstanceIDToObject(Convert.ToInt32(args["instanceId"])) as GameObject; }
                catch { /* fall through to path */ }
            }

            string path = GetStr(args, "path", GetStr(args, "gameObjectPath", ""));
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Trim('/');
            var segments = path.Split('/');
            if (segments.Length == 0) return null;

            // Search every loaded scene's ROOT objects (GetRootGameObjects includes inactive roots).
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != segments[0]) continue;
                    var cur = root.transform;
                    bool ok = true;
                    for (int i = 1; i < segments.Length && ok; i++)
                    {
                        Transform next = null;
                        // Manual child scan — includes INACTIVE children (Transform.Find also works on
                        // inactive, but a manual loop is explicit and handles duplicate-name siblings).
                        for (int c = 0; c < cur.childCount; c++)
                        {
                            var child = cur.GetChild(c);
                            if (child.name == segments[i]) { next = child; break; }
                        }
                        if (next == null) ok = false; else cur = next;
                    }
                    if (ok) return cur.gameObject;
                }
            }
            return null; // not resolvable by path → caller falls back
        }

        // ══════════════════════════════════════════════
        // TOOL: gameobject_components — return ALL components on a path
        //        with their full serialized properties.
        // Bridges the gap between unity_gameobject_info (component names only,
        // no values) and unity_component_get_properties (one component at a time).
        // Critical for the VRse build pipeline's per-object verification gates.
        // ══════════════════════════════════════════════
        public static object GetAllComponents(Dictionary<string, object> args)
        {
            // Fast inactive-aware path resolve FIRST; fall back to the shared resolver only if needed.
            var go = ResolveScopedByPath(args) ?? MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            bool includeProperties = GetBool(args, "include_properties", true);
            // Accept both snake_case (component_type) and camelCase (componentType) for the filter param.
            string typeFilter = GetStr(args, "component_type", null);
            if (string.IsNullOrEmpty(typeFilter)) typeFilter = GetStr(args, "componentType", null);
            if (string.IsNullOrEmpty(typeFilter)) typeFilter = GetStr(args, "component", null);

            var componentsOut = new List<Dictionary<string, object>>();

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                {
                    componentsOut.Add(new Dictionary<string, object>
                    {
                        { "type", "MISSING_SCRIPT" }, { "fullType", null }, { "enabled", false }
                    });
                    continue;
                }

                var compType = comp.GetType();
                if (!string.IsNullOrEmpty(typeFilter) && compType.Name != typeFilter && compType.FullName != typeFilter)
                    continue;

                var entry = new Dictionary<string, object>
                {
                    { "type", compType.Name },
                    { "fullType", compType.FullName },
                    { "enabled", comp is Behaviour b ? (object)b.enabled : true },
                    { "instanceId", comp.GetInstanceID() }
                };

                if (includeProperties)
                {
                    var properties = new List<Dictionary<string, object>>();
                    try
                    {
                        var serialized = new SerializedObject(comp);
                        var iterator = serialized.GetIterator();
                        if (iterator.NextVisible(true))
                        {
                            do
                            {
                                properties.Add(new Dictionary<string, object>
                                {
                                    { "name", iterator.name },
                                    { "displayName", iterator.displayName },
                                    { "type", iterator.propertyType.ToString() },
                                    { "value", GetSerializedValueSafe(iterator) },
                                    { "editable", iterator.editable }
                                });
                            } while (iterator.NextVisible(false));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Some components (notably native-only) can throw during SerializedObject access.
                        entry["propertiesError"] = ex.Message;
                    }
                    entry["properties"] = properties;
                }

                componentsOut.Add(entry);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "path", MCPGameObjectCommands.GetHierarchyPath(go) },
                { "instanceId", go.GetInstanceID() },
                { "componentCount", componentsOut.Count },
                { "components", componentsOut }
            };
        }

        // SerializedProperty value extractor — best-effort, returns string fallback on unknown types
        private static object GetSerializedValueSafe(SerializedProperty prop)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:   return prop.intValue;
                    case SerializedPropertyType.Boolean:   return prop.boolValue;
                    case SerializedPropertyType.Float:     return prop.floatValue;
                    case SerializedPropertyType.String:    return prop.stringValue;
                    case SerializedPropertyType.Color:
                        var c = prop.colorValue;
                        return new Dictionary<string, object> { {"r", c.r}, {"g", c.g}, {"b", c.b}, {"a", c.a} };
                    case SerializedPropertyType.ObjectReference:
                        var obj = prop.objectReferenceValue;
                        return obj != null
                            ? new Dictionary<string, object> {
                                {"name", obj.name},
                                {"type", obj.GetType().Name},
                                {"instanceId", obj.GetInstanceID()}
                              }
                            : null;
                    case SerializedPropertyType.LayerMask: return prop.intValue;
                    case SerializedPropertyType.Enum:      return prop.enumValueIndex;
                    case SerializedPropertyType.Vector2:
                        var v2 = prop.vector2Value;
                        return new Dictionary<string, object> { {"x", v2.x}, {"y", v2.y} };
                    case SerializedPropertyType.Vector3:
                        var v3 = prop.vector3Value;
                        return new Dictionary<string, object> { {"x", v3.x}, {"y", v3.y}, {"z", v3.z} };
                    case SerializedPropertyType.Vector4:
                        var v4 = prop.vector4Value;
                        return new Dictionary<string, object> { {"x", v4.x}, {"y", v4.y}, {"z", v4.z}, {"w", v4.w} };
                    case SerializedPropertyType.Quaternion:
                        var q = prop.quaternionValue;
                        return new Dictionary<string, object> { {"x", q.x}, {"y", q.y}, {"z", q.z}, {"w", q.w} };
                    case SerializedPropertyType.Rect:
                        var r = prop.rectValue;
                        return new Dictionary<string, object> { {"x", r.x}, {"y", r.y}, {"width", r.width}, {"height", r.height} };
                    case SerializedPropertyType.Bounds:
                        var b = prop.boundsValue;
                        return new Dictionary<string, object> {
                            {"center", new Dictionary<string, object>{{"x", b.center.x},{"y", b.center.y},{"z", b.center.z}}},
                            {"size",   new Dictionary<string, object>{{"x", b.size.x},  {"y", b.size.y},  {"z", b.size.z}}}
                        };
                    case SerializedPropertyType.ArraySize:  return prop.intValue;
                    case SerializedPropertyType.Character:  return (int)prop.intValue;
                    default: return prop.propertyType.ToString();
                }
            }
            catch (Exception ex)
            {
                return "<error: " + ex.Message + ">";
            }
        }

        // ══════════════════════════════════════════════
        // TOOL: screenshot_inline — capture Scene View, return base64 PNG inline.
        // The existing screenshot/scene route saves to disk and returns a path,
        // which doesn't fit the pipeline's vision-on-the-same-turn flow.
        // ══════════════════════════════════════════════
        public static object CaptureSceneViewInline(Dictionary<string, object> args)
        {
            // Defaults match the old unity-mcp-pro get_editor_screenshot (640x400 JPEG) so the inline
            // base64 stays small (~1,500 vision tokens) instead of overflowing the MCP response cap.
            // The old 1280x720 PNG default produced ~760KB of base64 → "exceeds maximum tokens" every time.
            int width  = Mathf.Clamp(GetInt(args, "width",  640), 64, 1920);
            int height = Mathf.Clamp(GetInt(args, "height", 400), 64, 1080);
            string format = GetStr(args, "format", "jpg").ToLowerInvariant();
            int jpegQuality = Mathf.Clamp(GetInt(args, "quality", 70), 1, 100);
            bool isJpeg = format == "jpg" || format == "jpeg";

            // Hard cap on returned base64 size. Base64 is ~1.37× the byte length; keep well under the
            // MCP response token ceiling that the 762KB PNG blew past.
            const int MaxBase64Chars = 350_000;

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new Dictionary<string, object> { { "success", false }, { "error", "No active Scene View found" } };

            var camera = sceneView.camera;
            if (camera == null)
                return new Dictionary<string, object> { { "success", false }, { "error", "Scene View has no camera" } };

            var rt = new RenderTexture(width, height, 24);
            RenderTexture prevTarget = camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            byte[] bytes;
            string mimeType;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                if (isJpeg) { bytes = tex.EncodeToJPG(jpegQuality); mimeType = "image/jpeg"; }
                else        { bytes = tex.EncodeToPNG();            mimeType = "image/png"; }

                // Safety net: if the encode is still too big (e.g. PNG explicitly requested on a busy view),
                // step JPEG quality down until it fits rather than returning an overflowing blob.
                int safetyQuality = jpegQuality;
                while (System.Convert.ToInt64(bytes.Length) * 4 / 3 > MaxBase64Chars && safetyQuality > 15)
                {
                    safetyQuality = Mathf.Max(15, safetyQuality - 20);
                    bytes = tex.EncodeToJPG(safetyQuality);
                    mimeType = "image/jpeg";
                    isJpeg = true;
                }

                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                UnityEngine.Object.DestroyImmediate(rt);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "image", Convert.ToBase64String(bytes) },
                { "mimeType", mimeType },
                { "width", width },
                { "height", height },
                { "format", isJpeg ? "jpeg" : "png" },
                { "sizeBytes", bytes.Length }
            };
        }

        // ══════════════════════════════════════════════
        // TOOL: search_all_loaded_scenes
        // Like unity_search_by_name but ALSO searches additively-loaded scenes.
        // Promotes the ListArtSceneObjects C# workaround into a first-class MCP tool.
        // ══════════════════════════════════════════════
        public static object SearchAllLoadedScenes(Dictionary<string, object> args)
        {
            string namePattern = GetStr(args, "name_pattern", GetStr(args, "name", ""));
            bool includeInactive = GetBool(args, "include_inactive", true);
            bool regex = GetBool(args, "regex", false);
            int limit = GetInt(args, "limit", 500);
            string sceneFilter = GetStr(args, "scene_name", null); // optional: restrict to one scene

            if (string.IsNullOrEmpty(namePattern))
                return new Dictionary<string, object>
                {
                    { "success", false }, { "error", "name_pattern (or name) is required" }
                };

            // Resources.FindObjectsOfTypeAll<GameObject>() returns ALL GameObjects across ALL
            // loaded scenes (active + additive) AND prefab assets. We must filter by scene
            // membership and HideFlags to exclude prefab assets and editor-internal objects.
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            System.Text.RegularExpressions.Regex re = null;
            if (regex)
            {
                try { re = new System.Text.RegularExpressions.Regex(namePattern); }
                catch (Exception ex) { return new Dictionary<string, object> { { "success", false }, { "error", "Invalid regex: " + ex.Message } }; }
            }
            string lowerPattern = namePattern.ToLowerInvariant();

            var matches = new List<Dictionary<string, object>>();
            int totalFound = 0;

            foreach (var go in all)
            {
                // Exclude prefab assets (scene.IsValid() returns false for prefab-asset GOs)
                // and editor-internal objects (HideFlags.HideAndDontSave etc.)
                if (go == null) continue;
                if (!go.scene.IsValid()) continue;
                if (go.hideFlags == HideFlags.HideAndDontSave || go.hideFlags == HideFlags.NotEditable) continue;

                if (!includeInactive && !go.activeInHierarchy) continue;

                // Scene filter
                if (!string.IsNullOrEmpty(sceneFilter) && go.scene.name != sceneFilter) continue;

                // Name match
                bool nameOk;
                if (regex) nameOk = re.IsMatch(go.name);
                else       nameOk = go.name.ToLowerInvariant().Contains(lowerPattern);
                if (!nameOk) continue;

                totalFound++;
                if (matches.Count >= limit) continue;

                matches.Add(new Dictionary<string, object>
                {
                    { "name", go.name },
                    { "path", MCPGameObjectCommands.GetHierarchyPath(go) },
                    { "instanceId", go.GetInstanceID() },
                    { "sceneName", go.scene.name },
                    { "sceneHandle", go.scene.handle },
                    { "active", go.activeInHierarchy },
                    { "tag", go.tag },
                    { "layer", LayerMask.LayerToName(go.layer) }
                });
            }

            // Also report what scenes were searched
            var loadedScenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                loadedScenes.Add(SceneManager.GetSceneAt(i).name);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "namePattern", namePattern },
                { "totalFound", totalFound },
                { "returned", matches.Count },
                { "limit", limit },
                { "matches", matches },
                { "scenesSearched", loadedScenes }
            };
            if (totalFound > matches.Count)
                result["truncated"] = true;
            return result;
        }
    }
}
