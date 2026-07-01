using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP command handlers for the vrse/create-button-from-mesh/* routes.
    /// Bridges the MCP server to VRsePhysicalButtonSetup (Assets/Editor assembly)
    /// via reflection to avoid hard assembly coupling.
    /// </summary>
    public static class MCPPhysicalButtonCommands
    {
        // ══════════════════════════════════════════════
        // TOOL: vrse/create-button-from-mesh/analyze
        // ══════════════════════════════════════════════
        public static object Analyze(Dictionary<string, object> args)
        {
            string gameObjectName = args != null && args.ContainsKey("gameObjectName")
                ? args["gameObjectName"]?.ToString() ?? ""
                : "";

            if (string.IsNullOrEmpty(gameObjectName))
                return new Dictionary<string, object> { { "error", "gameObjectName is required" } };

            try
            {
                var setupType = FindTypeAcrossAssemblies("VRsePhysicalButtonSetup");
                if (setupType == null)
                    return new Dictionary<string, object> { { "error", "VRsePhysicalButtonSetup not found — ensure the script is compiled in the project." } };

                var analyzeMethod = setupType.GetMethod("Analyze", BindingFlags.Public | BindingFlags.Static);
                if (analyzeMethod == null)
                    return new Dictionary<string, object> { { "error", "VRsePhysicalButtonSetup.Analyze method not found." } };

                string json = (string)analyzeMethod.Invoke(null, new object[] { gameObjectName });
                var parsed = MiniJson.Deserialize(json);
                return parsed ?? new Dictionary<string, object> { { "error", "Analyze returned null" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", ex.Message }, { "stackTrace", ex.StackTrace } };
            }
        }

        // ══════════════════════════════════════════════
        // TOOL: vrse/create-button-from-mesh/create
        // Accepts EITHER a single object's fields OR a "targets" array of per-object field maps.
        // ══════════════════════════════════════════════
        public static object Create(Dictionary<string, object> args)
        {
            if (args == null)
                return new Dictionary<string, object> { { "error", "args are required" } };

            try
            {
                var setupType = FindTypeAcrossAssemblies("VRsePhysicalButtonSetup");
                if (setupType == null)
                    return new Dictionary<string, object> { { "error", "VRsePhysicalButtonSetup not found — ensure the script is compiled in the project." } };

                var paramsType = setupType.GetNestedType("Params", BindingFlags.Public);
                if (paramsType == null)
                    return new Dictionary<string, object> { { "error", "VRsePhysicalButtonSetup.Params nested type not found." } };

                var createMethod = setupType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
                if (createMethod == null)
                    return new Dictionary<string, object> { { "error", "VRsePhysicalButtonSetup.Create method not found." } };

                // Build the work list: one entry per button to create.
                var jobs = new List<Dictionary<string, object>>();
                if (args.TryGetValue("targets", out var targetsVal) && targetsVal is List<object> list && list.Count > 0)
                {
                    foreach (var item in list)
                        if (item is Dictionary<string, object> d) jobs.Add(d);
                }
                else
                {
                    jobs.Add(args); // single-object form
                }

                var results = new List<object>();
                bool allOk = true;
                foreach (var job in jobs)
                {
                    var p = Activator.CreateInstance(paramsType);
                    SetField(paramsType, p, "instanceId",       job.TryGetValue("instanceId",       out var idVal)   ? Convert.ToInt32(idVal)    : 0);
                    SetField(paramsType, p, "parentObjectPath", job.TryGetValue("parentObjectPath", out var pathVal) ? pathVal?.ToString() ?? "" : "");
                    SetField(paramsType, p, "capMeshName",      job.TryGetValue("capMeshName",      out var capVal)  ? capVal?.ToString() ?? ""  : "");
                    SetField(paramsType, p, "baseMeshNames",    job.TryGetValue("baseMeshNames",    out var bmVal)   ? bmVal?.ToString() ?? ""   : "");
                    SetField(paramsType, p, "rootIsBase",       job.TryGetValue("rootIsBase",       out var ribVal)  ? Convert.ToBoolean(ribVal) : true);
                    SetField(paramsType, p, "pressDirX",        job.TryGetValue("pressDirX",        out var pdxVal)  ? (float)Convert.ToDouble(pdxVal) : 0f);
                    SetField(paramsType, p, "pressDirY",        job.TryGetValue("pressDirY",        out var pdyVal)  ? (float)Convert.ToDouble(pdyVal) : 0f);
                    SetField(paramsType, p, "pressDirZ",        job.TryGetValue("pressDirZ",        out var pdzVal)  ? (float)Convert.ToDouble(pdzVal) : 0f);
                    SetField(paramsType, p, "pressDepth",       job.TryGetValue("pressDepth",       out var depVal)  ? (float)Convert.ToDouble(depVal) : 0f);
                    SetField(paramsType, p, "pressRadius",      job.TryGetValue("pressRadius",      out var radVal)  ? (float)Convert.ToDouble(radVal) : 0f);

                    string result = (string)createMethod.Invoke(null, new object[] { p });
                    var parts = result.Split(new[] { "|||" }, StringSplitOptions.None);
                    if (parts[0] == "FAIL")
                    {
                        allOk = false;
                        results.Add(new Dictionary<string, object> { { "success", false }, { "message", parts.Length > 1 ? parts[1] : "Unknown error" } });
                    }
                    else
                    {
                        // Format: OK|||name|||instanceId|||pressDir=(x,y,z)|||depth=d|||radius=r
                        var entry = new Dictionary<string, object>
                        {
                            { "success", true },
                            { "name", parts.Length > 1 ? parts[1] : "" },
                            { "instanceId", parts.Length > 2 ? (object)int.Parse(parts[2]) : null },
                        };
                        for (int pi = 3; pi < parts.Length; pi++)
                        {
                            var kv = parts[pi].Split('=');
                            if (kv.Length == 2) entry[kv[0].Trim()] = kv[1].Trim();
                        }
                        results.Add(entry);
                    }
                }

                return new Dictionary<string, object>
                {
                    { "success", allOk },
                    { "count", results.Count },
                    { "results", results },
                };
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

        private static void SetField(Type type, object instance, string fieldName, object value)
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) field.SetValue(instance, value);
        }
    }
}
