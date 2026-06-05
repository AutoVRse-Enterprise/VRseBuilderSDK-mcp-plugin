using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Spatial-analysis commands ported from unity-mcp-pro/plugin/Editor/Commands/SpatialCommands.cs.
    ///
    /// Goal: parity with the unity-mcp-pro spatial tools the VRse build pipeline depends on at
    /// Step 4.5 (no-marker spatial placement). Same input/output shapes, only the route name
    /// changes — Node tools are exposed as unity_spatial_* / unity_gameobject_bounds.
    ///
    /// Tools covered:
    ///   - vrse/spatial-analyze-scene-get      ↔ unity-mcp-pro spatial_analyze_scene
    ///   - vrse/spatial-get-bounds-get         ↔ unity-mcp-pro spatial_get_bounds
    ///   - vrse/spatial-find-surface-get       ↔ unity-mcp-pro spatial_find_surface
    ///   - vrse/spatial-check-placement-get    ↔ unity-mcp-pro spatial_check_placement
    ///   - vrse/spatial-probe-surfaces-list    ↔ unity-mcp-pro spatial_probe_surfaces
    /// </summary>
    public static class MCPSpatialCommands
    {
        // ─── Param helpers ───
        private static string GetStr(Dictionary<string, object> p, string key, string fallback = "")
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            return p[key].ToString();
        }
        private static int GetInt(Dictionary<string, object> p, string key, int fallback)
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToInt32(p[key]); } catch { return fallback; }
        }
        private static float GetFloat(Dictionary<string, object> p, string key, float fallback)
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToSingle(p[key]); } catch { return fallback; }
        }
        private static bool GetBool(Dictionary<string, object> p, string key, bool fallback)
        {
            if (p == null || !p.ContainsKey(key) || p[key] == null) return fallback;
            try { return Convert.ToBoolean(p[key]); } catch { return fallback; }
        }

        // ─── Formatting helpers (mirror unity-mcp-pro output shape) ───
        private static string V3(Vector3 v) => $"Vector3({v.x:F3},{v.y:F3},{v.z:F3})";

        private static Vector3 ParseV3(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector3.zero;
            // Accept "x,y,z" OR "Vector3(x,y,z)"
            s = s.Trim();
            if (s.StartsWith("Vector3(") && s.EndsWith(")"))
                s = s.Substring(8, s.Length - 9);
            var parts = s.Split(',');
            if (parts.Length < 3) return Vector3.zero;
            float.TryParse(parts[0].Trim(), out float x);
            float.TryParse(parts[1].Trim(), out float y);
            float.TryParse(parts[2].Trim(), out float z);
            return new Vector3(x, y, z);
        }

        private static GameObject Resolve(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return null;
            return MCPGameObjectCommands.FindGameObject(new Dictionary<string, object> { { "path", pathOrName } });
        }

        // ─── World-bounds helper (handles missing colliders/renderers gracefully) ───
        private static bool TryGetWorldBounds(GameObject go, bool includeChildren, out Bounds bounds)
        {
            Renderer[] renderers = includeChildren
                ? go.GetComponentsInChildren<Renderer>(true)
                : go.GetComponents<Renderer>();

            bounds = default;
            bool found = false;

            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer) continue;

                if (!found) { bounds = r.bounds; found = true; }
                else { bounds.Encapsulate(r.bounds); }
            }

            if (!found)
            {
                Collider[] colliders = includeChildren
                    ? go.GetComponentsInChildren<Collider>(true)
                    : go.GetComponents<Collider>();

                foreach (var c in colliders)
                {
                    if (!found) { bounds = c.bounds; found = true; }
                    else { bounds.Encapsulate(c.bounds); }
                }
            }
            return found;
        }

        private static Dictionary<string, object> ComputeSurfaces(Bounds b) => new Dictionary<string, object>
        {
            { "top",    V3(new Vector3(b.center.x, b.max.y,    b.center.z)) },
            { "bottom", V3(new Vector3(b.center.x, b.min.y,    b.center.z)) },
            { "front",  V3(new Vector3(b.center.x, b.center.y, b.max.z))    },
            { "back",   V3(new Vector3(b.center.x, b.center.y, b.min.z))    },
            { "right",  V3(new Vector3(b.max.x,    b.center.y, b.center.z)) },
            { "left",   V3(new Vector3(b.min.x,    b.center.y, b.center.z)) }
        };

        private static string ClassifyObject(GameObject go, Bounds bounds)
        {
            float width  = bounds.size.x;
            float height = bounds.size.y;
            float depth  = bounds.size.z;
            bool hasManyChildren = go.transform.childCount >= 3;
            bool isFlat = height < 0.15f && (width > 0.3f || depth > 0.3f);
            bool isVerticalFlat = (width < 0.15f || depth < 0.15f) && height > 0.3f;
            if (hasManyChildren) return "mechanism";
            if (isFlat) return "surface";
            if (isVerticalFlat) return "panel";
            return "object";
        }

        private static List<MeshCollider> AddTempColliders()
        {
            var added = new List<MeshCollider>();
            var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                added.Add(mc);
            }
            Physics.SyncTransforms();
            return added;
        }

        private static void RemoveTempColliders(List<MeshCollider> colliders)
        {
            foreach (var mc in colliders)
                if (mc != null) UnityEngine.Object.DestroyImmediate(mc);
        }

        // ══════════════════════════════════════════════
        // TOOL: spatial_analyze_scene
        // ══════════════════════════════════════════════
        public static object AnalyzeScene(Dictionary<string, object> p)
        {
            string filterTag = GetStr(p, "filter_tag");
            float filterRadius = GetFloat(p, "filter_radius", -1f);
            string centerStr = GetStr(p, "center");
            bool includeSurfaces = GetBool(p, "include_surfaces", true);
            int gridRes = GetInt(p, "grid_resolution", 10);

            Vector3 filterCenter = Vector3.zero;
            bool useRadiusFilter = filterRadius > 0 && !string.IsNullOrEmpty(centerStr);
            if (useRadiusFilter) filterCenter = ParseV3(centerStr);

            var allRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var processedRoots = new HashSet<int>();
            var objectList = new List<object>();
            Bounds sceneBounds = default;
            bool sceneBoundsInit = false;

            foreach (var renderer in allRenderers)
            {
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer) continue;

                GameObject go = renderer.gameObject;
                Transform root = go.transform;
                while (root.parent != null && root.parent.parent != null)
                {
                    if (root.parent.GetComponent<Renderer>() == null && root.parent.childCount > 5) break;
                    root = root.parent;
                }
                go = root.gameObject;

                int id = go.GetInstanceID();
                if (processedRoots.Contains(id)) continue;
                processedRoots.Add(id);

                if (!string.IsNullOrEmpty(filterTag) && !go.CompareTag(filterTag)) continue;
                if (!TryGetWorldBounds(go, true, out Bounds bounds)) continue;
                if (useRadiusFilter && Vector3.Distance(bounds.center, filterCenter) > filterRadius) continue;

                if (!sceneBoundsInit) { sceneBounds = bounds; sceneBoundsInit = true; }
                else sceneBounds.Encapsulate(bounds);

                var children = new List<object>();
                foreach (Transform child in go.transform)
                    children.Add(new Dictionary<string, object>
                    {
                        { "name", child.name },
                        { "localPosition", V3(child.localPosition) }
                    });

                string classification = ClassifyObject(go, bounds);
                objectList.Add(new Dictionary<string, object>
                {
                    { "name", go.name },
                    { "path", MCPGameObjectCommands.GetHierarchyPath(go) },
                    { "classification", classification },
                    { "worldPosition", V3(go.transform.position) },
                    { "worldRotation", V3(go.transform.eulerAngles) },
                    { "bounds", new Dictionary<string, object>
                        {
                            { "center", V3(bounds.center) }, { "size", V3(bounds.size) },
                            { "min", V3(bounds.min) },       { "max", V3(bounds.max) }
                        }
                    },
                    { "surfaces", ComputeSurfaces(bounds) },
                    { "children", children },
                    { "isStatic", go.isStatic },
                    { "hasCollider", go.GetComponentInChildren<Collider>() != null }
                });
            }

            var discoveredSurfaces = new List<object>();
            if (includeSurfaces && sceneBoundsInit)
            {
                List<MeshCollider> tempColliders = null;
                try
                {
                    tempColliders = AddTempColliders();

                    float xMin = sceneBounds.min.x, xMax = sceneBounds.max.x;
                    float zMin = sceneBounds.min.z, zMax = sceneBounds.max.z;
                    float yTop = sceneBounds.max.y + 2f;

                    for (int ix = 0; ix < gridRes; ix++)
                    {
                        for (int iz = 0; iz < gridRes; iz++)
                        {
                            float x = Mathf.Lerp(xMin, xMax, (ix + 0.5f) / gridRes);
                            float z = Mathf.Lerp(zMin, zMax, (iz + 0.5f) / gridRes);
                            Vector3 origin = new Vector3(x, yTop, z);
                            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, yTop - sceneBounds.min.y + 5f))
                            {
                                if (hit.normal.y > 0.7f)
                                    discoveredSurfaces.Add(new Dictionary<string, object>
                                    {
                                        { "type", "horizontal" },
                                        { "point", V3(hit.point) },
                                        { "normal", V3(hit.normal) },
                                        { "objectName", hit.collider.gameObject.name },
                                        { "objectPath", MCPGameObjectCommands.GetHierarchyPath(hit.collider.gameObject) }
                                    });
                            }
                        }
                    }

                    // Dedupe by objectName + Y bucket
                    var deduped = new List<object>();
                    var seen = new HashSet<string>();
                    foreach (Dictionary<string, object> surf in discoveredSurfaces)
                    {
                        string name = surf["objectName"].ToString();
                        string point = surf["point"].ToString();
                        string yPart = point.Split(',')[1];
                        string key = name + "_" + (yPart.Length >= 4 ? yPart.Substring(0, 4) : yPart);
                        if (seen.Contains(key)) continue;
                        seen.Add(key);
                        deduped.Add(surf);
                    }
                    discoveredSurfaces = deduped;
                }
                finally
                {
                    if (tempColliders != null) RemoveTempColliders(tempColliders);
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "objectCount", objectList.Count },
                { "sceneBounds", sceneBoundsInit
                    ? new Dictionary<string, object> { { "min", V3(sceneBounds.min) }, { "max", V3(sceneBounds.max) } }
                    : null
                },
                { "objects", objectList },
                { "discoveredSurfaces", discoveredSurfaces }
            };
        }

        // ══════════════════════════════════════════════
        // TOOL: spatial_get_bounds
        // ══════════════════════════════════════════════
        public static object GetBounds(Dictionary<string, object> p)
        {
            string goPath = GetStr(p, "game_object_path");
            bool includeChildren = GetBool(p, "include_children", true);

            if (string.IsNullOrEmpty(goPath))
                return new Dictionary<string, object> { { "success", false }, { "error", "game_object_path is required" } };

            var go = Resolve(goPath);
            if (go == null)
                return new Dictionary<string, object> { { "success", false }, { "error", $"GameObject '{goPath}' not found" } };

            if (!TryGetWorldBounds(go, includeChildren, out Bounds bounds))
                return new Dictionary<string, object> { { "success", false }, { "error", $"No Renderer or Collider found on '{goPath}' — cannot compute bounds" } };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "path", MCPGameObjectCommands.GetHierarchyPath(go) },
                { "worldPosition", V3(go.transform.position) },
                { "worldRotation", V3(go.transform.eulerAngles) },
                { "bounds", new Dictionary<string, object>
                    {
                        { "center", V3(bounds.center) }, { "size", V3(bounds.size) },
                        { "min", V3(bounds.min) },       { "max", V3(bounds.max) }
                    }
                },
                { "surfaces", ComputeSurfaces(bounds) }
            };
        }

        // ══════════════════════════════════════════════
        // TOOL: spatial_find_surface
        // ══════════════════════════════════════════════
        public static object FindSurface(Dictionary<string, object> p)
        {
            string targetObj = GetStr(p, "target_object");
            string directionStr = GetStr(p, "direction", "0,-1,0");
            float offset = GetFloat(p, "offset", 1.0f);

            if (string.IsNullOrEmpty(targetObj))
                return new Dictionary<string, object> { { "success", false }, { "error", "target_object is required" } };

            var go = Resolve(targetObj);
            if (go == null)
                return new Dictionary<string, object> { { "success", false }, { "error", $"target_object '{targetObj}' not found" } };

            if (!TryGetWorldBounds(go, true, out Bounds bounds))
                return new Dictionary<string, object> { { "success", false }, { "error", $"No Renderer or Collider on '{targetObj}' — cannot compute bounds" } };

            var direction = ParseV3(directionStr).normalized;
            Vector3 rayOrigin = bounds.center - direction * (bounds.extents.magnitude + offset);

            var tempColliders = new List<MeshCollider>();
            try
            {
                // Ensure target has colliders (children too)
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    if (mf.GetComponent<Collider>() != null) continue;
                    tempColliders.Add(mf.gameObject.AddComponent<MeshCollider>());
                }
                Physics.SyncTransforms();

                float maxDist = bounds.extents.magnitude * 2f + offset * 2f;
                if (Physics.Raycast(rayOrigin, direction, out RaycastHit hit, maxDist))
                {
                    if (hit.collider.transform.IsChildOf(go.transform) || hit.collider.gameObject == go)
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "surfacePoint", V3(hit.point) },
                            { "surfaceNormal", V3(hit.normal) },
                            { "objectName", hit.collider.gameObject.name },
                            { "objectPath", MCPGameObjectCommands.GetHierarchyPath(hit.collider.gameObject) }
                        };
                    }
                }

                // Fallback: AABB face center
                Vector3 fallbackPoint = bounds.center;
                Vector3 fallbackNormal = -direction;
                if (direction.y < -0.5f)      { fallbackPoint.y = bounds.max.y; fallbackNormal = Vector3.up; }
                else if (direction.y > 0.5f)  { fallbackPoint.y = bounds.min.y; fallbackNormal = Vector3.down; }
                else if (direction.z > 0.5f)  { fallbackPoint.z = bounds.min.z; fallbackNormal = Vector3.back; }
                else if (direction.z < -0.5f) { fallbackPoint.z = bounds.max.z; fallbackNormal = Vector3.forward; }
                else if (direction.x > 0.5f)  { fallbackPoint.x = bounds.min.x; fallbackNormal = Vector3.left; }
                else if (direction.x < -0.5f) { fallbackPoint.x = bounds.max.x; fallbackNormal = Vector3.right; }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "surfacePoint", V3(fallbackPoint) },
                    { "surfaceNormal", V3(fallbackNormal) },
                    { "objectName", go.name },
                    { "objectPath", MCPGameObjectCommands.GetHierarchyPath(go) },
                    { "fallback", true },
                    { "message", "Raycast missed target mesh — returning AABB face center as fallback" }
                };
            }
            finally { RemoveTempColliders(tempColliders); }
        }

        // ══════════════════════════════════════════════
        // TOOL: spatial_check_placement
        // ══════════════════════════════════════════════
        public static object CheckPlacement(Dictionary<string, object> p)
        {
            string goPath = GetStr(p, "game_object_path");
            bool checkSurface = GetBool(p, "check_surface", true);
            bool checkOverlap = GetBool(p, "check_overlap", true);
            bool checkFloor = GetBool(p, "check_floor", true);

            if (string.IsNullOrEmpty(goPath))
                return new Dictionary<string, object> { { "success", false }, { "error", "game_object_path is required" } };

            var go = Resolve(goPath);
            if (go == null)
                return new Dictionary<string, object> { { "success", false }, { "error", $"GameObject '{goPath}' not found" } };

            if (!TryGetWorldBounds(go, true, out Bounds bounds))
                return new Dictionary<string, object> { { "success", false }, { "error", $"No Renderer or Collider on '{goPath}' — cannot check placement" } };

            var issues = new List<string>();
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "worldPosition", V3(go.transform.position) },
                { "boundsMin", V3(bounds.min) },
                { "boundsMax", V3(bounds.max) }
            };

            if (checkSurface)
            {
                Vector3 rayOrigin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                List<MeshCollider> tempColliders = null;
                try
                {
                    tempColliders = AddTempColliders();
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f))
                    {
                        if (!hit.collider.transform.IsChildOf(go.transform) && hit.collider.gameObject != go)
                        {
                            result["onSurface"] = true;
                            result["surfaceBelow"] = hit.collider.gameObject.name;
                            result["distanceToSurface"] = hit.distance;
                            if (hit.distance > 0.1f)
                                issues.Add($"Floating {hit.distance:F2}m above '{hit.collider.gameObject.name}'");
                        }
                        else
                        {
                            result["onSurface"] = false;
                            result["surfaceBelow"] = "none (hit self)";
                        }
                    }
                    else
                    {
                        result["onSurface"] = false;
                        result["surfaceBelow"] = "none";
                        issues.Add("No surface detected below object within 10m");
                    }
                }
                finally { if (tempColliders != null) RemoveTempColliders(tempColliders); }
            }

            if (checkFloor)
            {
                bool belowFloor = bounds.min.y < -0.05f;
                result["belowFloor"] = belowFloor;
                if (belowFloor) issues.Add($"Object penetrates floor plane (bounds.min.y = {bounds.min.y:F2})");
            }

            if (checkOverlap)
            {
                float checkRadius = bounds.extents.magnitude;
                var nearby = Physics.OverlapSphere(bounds.center, checkRadius);
                var overlapping = new List<string>();
                foreach (var col in nearby)
                {
                    if (col.gameObject == go) continue;
                    if (col.transform.IsChildOf(go.transform)) continue;
                    if (go.transform.IsChildOf(col.transform)) continue;
                    if (bounds.Intersects(col.bounds)) overlapping.Add(col.gameObject.name);
                }
                result["overlapping"] = overlapping.Count > 0;
                if (overlapping.Count > 0)
                {
                    result["overlappingObjects"] = overlapping;
                    issues.Add($"Overlapping with: {string.Join(", ", overlapping)}");
                }
            }

            result["issues"] = issues;
            result["placementOk"] = issues.Count == 0;
            return result;
        }

        // ══════════════════════════════════════════════
        // TOOL: spatial_probe_surfaces
        // ══════════════════════════════════════════════
        private struct ProbeHit { public Vector3 point; public Vector3 normal; public string childName; public string probeAxis; }

        public static object ProbeSurfaces(Dictionary<string, object> p)
        {
            string targetObj = GetStr(p, "target_object");
            int probeRes = GetInt(p, "probe_resolution", 5);
            string axis = GetStr(p, "axis", "vertical");
            float clusterTol = GetFloat(p, "cluster_tolerance", 0.05f);

            if (string.IsNullOrEmpty(targetObj))
                return new Dictionary<string, object> { { "success", false }, { "error", "target_object is required" } };

            var go = Resolve(targetObj);
            if (go == null)
                return new Dictionary<string, object> { { "success", false }, { "error", $"target_object '{targetObj}' not found" } };

            if (!TryGetWorldBounds(go, true, out Bounds bounds))
                return new Dictionary<string, object> { { "success", false }, { "error", $"No Renderer or Collider on '{targetObj}' — cannot probe" } };

            probeRes = Mathf.Clamp(probeRes, 2, 20);

            var tempColliders = new List<MeshCollider>();
            try
            {
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    if (mf.GetComponent<Collider>() != null) continue;
                    tempColliders.Add(mf.gameObject.AddComponent<MeshCollider>());
                }
                Physics.SyncTransforms();

                var allHits = new List<ProbeHit>();

                if (axis == "vertical" || axis == "v")
                    ProbeVertical(go, bounds, probeRes, allHits);
                else if (axis == "horizontal_x" || axis == "hx")
                    ProbeHorizontal(go, bounds, probeRes, Vector3.right, allHits);
                else if (axis == "horizontal_z" || axis == "hz")
                    ProbeHorizontal(go, bounds, probeRes, Vector3.forward, allHits);
                else if (axis == "all")
                {
                    ProbeVertical(go, bounds, probeRes, allHits);
                    ProbeHorizontal(go, bounds, probeRes, Vector3.right, allHits);
                    ProbeHorizontal(go, bounds, probeRes, Vector3.forward, allHits);
                }
                else
                    return new Dictionary<string, object> { { "success", false }, { "error", $"Unknown axis '{axis}'. Use: vertical, horizontal_x, horizontal_z, or all" } };

                var surfaces = ClusterHits(allHits, axis, clusterTol);
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "targetName", go.name },
                    { "targetPath", MCPGameObjectCommands.GetHierarchyPath(go) },
                    { "targetBounds", new Dictionary<string, object> {
                        { "min", V3(bounds.min) }, { "max", V3(bounds.max) }, { "size", V3(bounds.size) }
                    }},
                    { "axis", axis },
                    { "totalHits", allHits.Count },
                    { "surfaceCount", surfaces.Count },
                    { "surfaces", surfaces }
                };
            }
            finally { RemoveTempColliders(tempColliders); }
        }

        private static bool IsChildOfTarget(Collider col, GameObject target) =>
            col.gameObject == target || col.transform.IsChildOf(target.transform);

        private static void ProbeVertical(GameObject go, Bounds bounds, int res, List<ProbeHit> hits)
        {
            float xMin = bounds.min.x, xMax = bounds.max.x;
            float zMin = bounds.min.z, zMax = bounds.max.z;
            float yTop = bounds.max.y + 0.01f, yBot = bounds.min.y - 0.01f;
            float height = bounds.size.y;

            int ySteps = Mathf.Clamp(Mathf.CeilToInt(height / 0.15f), 2, 20);

            for (int iy = 0; iy < ySteps; iy++)
            {
                float yOrigin = Mathf.Lerp(yTop, yBot + height * 0.1f, (float)iy / Mathf.Max(1, ySteps - 1));
                float maxDist = yOrigin - yBot;
                for (int ix = 0; ix < res; ix++)
                for (int iz = 0; iz < res; iz++)
                {
                    float x = Mathf.Lerp(xMin, xMax, (ix + 0.5f) / res);
                    float z = Mathf.Lerp(zMin, zMax, (iz + 0.5f) / res);
                    var origin = new Vector3(x, yOrigin, z);
                    foreach (var h in Physics.RaycastAll(origin, Vector3.down, maxDist))
                    {
                        if (!IsChildOfTarget(h.collider, go)) continue;
                        if (h.normal.y > 0.5f)
                            hits.Add(new ProbeHit { point = h.point, normal = h.normal, childName = h.collider.gameObject.name, probeAxis = "v" });
                    }
                }
            }

            // Upward rays to catch underside-visible shelf tops
            for (int ix = 0; ix < res; ix++)
            for (int iz = 0; iz < res; iz++)
            {
                float x = Mathf.Lerp(xMin, xMax, (ix + 0.5f) / res);
                float z = Mathf.Lerp(zMin, zMax, (iz + 0.5f) / res);
                var origin = new Vector3(x, yBot, z);
                foreach (var h in Physics.RaycastAll(origin, Vector3.up, height + 0.02f))
                {
                    if (!IsChildOfTarget(h.collider, go)) continue;
                    if (h.normal.y < -0.5f)
                        hits.Add(new ProbeHit { point = h.point, normal = Vector3.up, childName = h.collider.gameObject.name, probeAxis = "v" });
                }
            }
        }

        private static void ProbeHorizontal(GameObject go, Bounds bounds, int res, Vector3 sweepDir, List<ProbeHit> hits)
        {
            bool sweepX = Mathf.Abs(sweepDir.x) > 0.5f;
            string axisLabel = sweepX ? "hx" : "hz";

            float aMin, aMax, bMin, bMax, sweepStart, sweepLength;
            if (sweepX)
            {
                aMin = bounds.min.y; aMax = bounds.max.y;
                bMin = bounds.min.z; bMax = bounds.max.z;
                sweepStart = bounds.min.x - 0.01f; sweepLength = bounds.size.x + 0.02f;
            }
            else
            {
                aMin = bounds.min.x; aMax = bounds.max.x;
                bMin = bounds.min.y; bMax = bounds.max.y;
                sweepStart = bounds.min.z - 0.01f; sweepLength = bounds.size.z + 0.02f;
            }

            for (int ia = 0; ia < res; ia++)
            for (int ib = 0; ib < res; ib++)
            {
                float a = Mathf.Lerp(aMin, aMax, (ia + 0.5f) / res);
                float b = Mathf.Lerp(bMin, bMax, (ib + 0.5f) / res);
                Vector3 origin = sweepX ? new Vector3(sweepStart, a, b) : new Vector3(a, b, sweepStart);
                foreach (var h in Physics.RaycastAll(origin, sweepDir, sweepLength))
                {
                    if (!IsChildOfTarget(h.collider, go)) continue;
                    float dot = Mathf.Abs(Vector3.Dot(h.normal, sweepDir));
                    if (dot > 0.5f)
                        hits.Add(new ProbeHit { point = h.point, normal = h.normal, childName = h.collider.gameObject.name, probeAxis = axisLabel });
                }
            }
        }

        private static float GetClusterValue(Vector3 point, string probeAxis)
        {
            switch (probeAxis)
            {
                case "v":  return point.y;
                case "hx": return point.x;
                case "hz": return point.z;
                default:   return point.y;
            }
        }

        private static List<object> ClusterHits(List<ProbeHit> hits, string axis, float tolerance)
        {
            if (hits.Count == 0) return new List<object>();

            var groups = new Dictionary<string, List<ProbeHit>>();
            foreach (var h in hits)
            {
                if (!groups.ContainsKey(h.probeAxis)) groups[h.probeAxis] = new List<ProbeHit>();
                groups[h.probeAxis].Add(h);
            }

            var allSurfaces = new List<object>();
            foreach (var kv in groups)
            {
                string probeAxis = kv.Key;
                var groupHits = kv.Value;
                groupHits.Sort((a, b) => GetClusterValue(a.point, probeAxis).CompareTo(GetClusterValue(b.point, probeAxis)));

                var clusters = new List<List<ProbeHit>>();
                List<ProbeHit> current = null;
                float currentCenter = 0f;

                foreach (var h in groupHits)
                {
                    float val = GetClusterValue(h.point, probeAxis);
                    if (current == null || Mathf.Abs(val - currentCenter) > tolerance)
                    {
                        current = new List<ProbeHit> { h };
                        clusters.Add(current);
                        currentCenter = val;
                    }
                    else
                    {
                        current.Add(h);
                        currentCenter = current.Average(ph => GetClusterValue(ph.point, probeAxis));
                    }
                }

                foreach (var cluster in clusters)
                {
                    float avg = cluster.Average(h => GetClusterValue(h.point, probeAxis));
                    float minX = cluster.Min(h => h.point.x), maxX = cluster.Max(h => h.point.x);
                    float minY = cluster.Min(h => h.point.y), maxY = cluster.Max(h => h.point.y);
                    float minZ = cluster.Min(h => h.point.z), maxZ = cluster.Max(h => h.point.z);
                    Vector3 center = new Vector3(cluster.Average(h => h.point.x), cluster.Average(h => h.point.y), cluster.Average(h => h.point.z));
                    Vector3 avgNormal = Vector3.zero;
                    foreach (var h in cluster) avgNormal += h.normal;
                    avgNormal = avgNormal.normalized;

                    float area; string orientation;
                    if (probeAxis == "v")       { area = (maxX - minX) * (maxZ - minZ); orientation = "horizontal"; }
                    else if (probeAxis == "hx") { area = (maxY - minY) * (maxZ - minZ); orientation = "vertical_x"; }
                    else                        { area = (maxX - minX) * (maxY - minY); orientation = "vertical_z"; }

                    string childName = cluster.GroupBy(h => h.childName).OrderByDescending(g => g.Count()).First().Key;

                    var surfaceData = new Dictionary<string, object>
                    {
                        { "orientation", orientation },
                        { "center", V3(center) },
                        { "normal", V3(avgNormal) },
                        { "extentMin", V3(new Vector3(minX, minY, minZ)) },
                        { "extentMax", V3(new Vector3(maxX, maxY, maxZ)) },
                        { "approximateArea", (float)Math.Round(area, 4) },
                        { "hitCount", cluster.Count },
                        { "meshName", childName }
                    };
                    if (probeAxis == "v")       surfaceData["height"]    = (float)Math.Round(avg, 4);
                    else if (probeAxis == "hx") surfaceData["xPosition"] = (float)Math.Round(avg, 4);
                    else                        surfaceData["zPosition"] = (float)Math.Round(avg, 4);

                    allSurfaces.Add(surfaceData);
                }
            }

            allSurfaces.Sort((a, b) =>
            {
                var da = (Dictionary<string, object>)a; var db = (Dictionary<string, object>)b;
                string oa = da["orientation"].ToString(); string ob = db["orientation"].ToString();
                if (oa != ob) return string.Compare(oa, ob, StringComparison.Ordinal);
                if (da.ContainsKey("height") && db.ContainsKey("height"))
                    return ((float)db["height"]).CompareTo((float)da["height"]);
                return 0;
            });

            return allSurfaces;
        }
    }
}
