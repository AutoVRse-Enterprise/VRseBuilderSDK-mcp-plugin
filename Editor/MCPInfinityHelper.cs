#if INFINITY_WORKSHOP_ENABLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using VRseBuilder.InfinityWorkshop.API;
using VRseBuilder.InfinityWorkshop.Schemas;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP helper for Infinity Workshop operations.
    /// Called from JS tools via executeCode reflection.
    /// All async operations use a job-based pattern to avoid blocking the Unity main thread.
    /// </summary>
    public static class MCPInfinityHelper
    {
        // ─── Async job tracking ───
        private class AsyncJob
        {
            public string Status = "running"; // running, complete, error
            public string Error;
            public object Result;
            public float Progress;
        }

        private static readonly Dictionary<string, AsyncJob> _jobs = new Dictionary<string, AsyncJob>();
        private static AssetServiceClient _client;
        private static bool _isInitialized;
        private static SessionData _sessionData;

        private static AssetServiceClient GetClient()
        {
            if (_client == null)
                _client = new AssetServiceClient();
            return _client;
        }

        // ─── Tool 1: GetStatus ───
        public static string GetStatus()
        {
            try
            {
                var client = GetClient();
                bool isAuth = client.IsLoggedIn();
                string username = client.Username ?? "";
                string userRole = client.UserRole ?? "";

                string currentTenant = "";
                string[] availableTenants = Array.Empty<string>();

                if (_sessionData != null)
                {
                    currentTenant = _sessionData.currentTenant?.name ?? "";
                    availableTenants = _sessionData.tenants?.Select(t => t.name).ToArray() ?? Array.Empty<string>();
                }

                var result = new
                {
                    isAuthenticated = isAuth,
                    username,
                    userRole,
                    isInitialized = _isInitialized,
                    currentTenant,
                    availableTenants
                };

                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        // ─── Tool 7: Initialize ───
        public static string Initialize()
        {
            try
            {
                string jobId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _jobs[jobId] = new AsyncJob();

                InitializeAsync(jobId);

                return $"STARTED:{jobId}";
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        private static async void InitializeAsync(string jobId)
        {
            try
            {
                var client = GetClient();
                bool authResult = await client.AuthenticateAsync();

                if (authResult)
                {
                    _isInitialized = true;

                    _jobs[jobId].Status = "complete";
                    _jobs[jobId].Result = new
                    {
                        isAuthenticated = true,
                        username = client.Username ?? "",
                        tenantName = _sessionData?.currentTenant?.name ?? ""
                    };
                }
                else
                {
                    _jobs[jobId].Status = "complete";
                    _jobs[jobId].Result = new
                    {
                        isAuthenticated = false,
                        username = "",
                        tenantName = ""
                    };
                }
            }
            catch (Exception ex)
            {
                if (_jobs.ContainsKey(jobId))
                {
                    _jobs[jobId].Status = "error";
                    _jobs[jobId].Error = ex.Message;
                }
            }
        }

        // ─── Tool 8: PollInitializeJob ───
        public static string PollInitializeJob(string jobId)
        {
            return PollJob(jobId);
        }

        // ─── Tool 2: ListAssets ───
        public static string ListAssets(string query, string tags, string tenantName,
            int page, int limit, bool collectionsOnly, bool showWip)
        {
            try
            {
                var client = GetClient();
                if (!client.IsLoggedIn())
                {
                    return JsonConvert.SerializeObject(new
                    {
                        error = true,
                        message = "Not authenticated. Please login via Window > Infinity Workshop."
                    });
                }

                // Use the tenant from session data or provided name
                string tenantId = "";
                if (!string.IsNullOrEmpty(tenantName) && _sessionData?.tenants != null)
                {
                    var tenant = _sessionData.tenants.FirstOrDefault(
                        t => t.name.Equals(tenantName, StringComparison.OrdinalIgnoreCase));
                    if (tenant != null) tenantId = tenant._id;
                }
                else if (_sessionData?.currentTenant != null)
                {
                    tenantId = _sessionData.currentTenant._id;
                }

                if (string.IsNullOrEmpty(tenantId))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        error = true,
                        message = "No tenant available. Initialize first or provide tenantName."
                    });
                }

                // Parse tags
                string[] tagArray = null;
                if (!string.IsNullOrEmpty(tags))
                {
                    tagArray = tags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToArray();
                }

                // Run async list
                string jobId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _jobs[jobId] = new AsyncJob();

                ListAssetsAsync(jobId, client, tenantId, query, tagArray, page, limit, collectionsOnly, showWip);

                // Wait synchronously for a short period (listing is fast)
                int waitMs = 0;
                while (waitMs < 15000 && _jobs.ContainsKey(jobId) && _jobs[jobId].Status == "running")
                {
                    System.Threading.Thread.Sleep(100);
                    waitMs += 100;
                }

                if (_jobs.ContainsKey(jobId) && _jobs[jobId].Status == "complete")
                {
                    string result = JsonConvert.SerializeObject(_jobs[jobId].Result);
                    _jobs.Remove(jobId);
                    return result;
                }
                else if (_jobs.ContainsKey(jobId) && _jobs[jobId].Status == "error")
                {
                    string err = JsonConvert.SerializeObject(new { error = true, message = _jobs[jobId].Error });
                    _jobs.Remove(jobId);
                    return err;
                }

                return JsonConvert.SerializeObject(new { error = true, message = "ListAssets timed out." });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        private static async void ListAssetsAsync(string jobId, AssetServiceClient client,
            string tenantId, string query, string[] tags, int page, int limit,
            bool collectionsOnly, bool showWip)
        {
            try
            {
                var assets = await client.ListAssetsAsync(tenantId, null, query, tags, page, limit);

                // Filter WIP if not requested
                var filtered = assets.ToList();
                if (!showWip)
                {
                    filtered = filtered.Where(a => a.tags == null || !a.tags.Contains("WIP")).ToList();
                }

                // Filter collections if requested
                if (collectionsOnly)
                {
                    filtered = filtered.Where(a =>
                        a.currentVersionId?.unityAssetDetails?.isPartOfCollection == true).ToList();
                }

                var result = new
                {
                    assets = filtered.Select(a => new
                    {
                        a.assetId,
                        a.name,
                        a.tags,
                        a.thumbnailUrl,
                        isCollection = a.currentVersionId?.unityAssetDetails?.isPartOfCollection ?? false,
                        collectionKey = a.currentVersionId?.unityAssetDetails?.collectionKey ?? "",
                        updatedAt = a.updatedAt.ToString("o")
                    }).ToList(),
                    total = filtered.Count,
                    page,
                    limit
                };

                _jobs[jobId].Status = "complete";
                _jobs[jobId].Result = result;
            }
            catch (Exception ex)
            {
                if (_jobs.ContainsKey(jobId))
                {
                    _jobs[jobId].Status = "error";
                    _jobs[jobId].Error = ex.Message;
                }
            }
        }

        // ─── Tool 3: DownloadAssets ───
        public static string DownloadAssets(string assetIdsJson, string downloadPath, string conflictPolicy)
        {
            try
            {
                var client = GetClient();
                if (!client.IsLoggedIn())
                {
                    return JsonConvert.SerializeObject(new
                    {
                        error = true,
                        message = "Not authenticated."
                    });
                }

                var assetIds = JsonConvert.DeserializeObject<string[]>(assetIdsJson);
                if (assetIds == null || assetIds.Length == 0)
                {
                    return JsonConvert.SerializeObject(new { error = true, message = "No asset IDs provided." });
                }

                string basePath = string.IsNullOrEmpty(downloadPath) ? "Assets/CommonArt/3DAssets" : downloadPath;
                string jobId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _jobs[jobId] = new AsyncJob();

                DownloadAssetsAsync(jobId, client, assetIds, basePath, conflictPolicy ?? "cancel");

                return $"STARTED:{jobId}:{assetIds.Length}";
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        private static async void DownloadAssetsAsync(string jobId, AssetServiceClient client,
            string[] assetIds, string basePath, string conflictPolicy)
        {
            var results = new List<object>();
            int successCount = 0;
            int failCount = 0;

            try
            {
                foreach (var assetId in assetIds)
                {
                    try
                    {
                        // Get asset info for name
                        var assetInfo = await client.GetAssetByAssetId(assetId);
                        string assetName = assetInfo?.name ?? assetId;
                        string assetFolder = Path.Combine(basePath, assetName);

                        // Check conflict
                        string fullPath = Path.Combine(Application.dataPath, "..",  assetFolder);
                        if (Directory.Exists(fullPath))
                        {
                            switch (conflictPolicy)
                            {
                                case "cancel":
                                    results.Add(new { assetId, name = assetName, status = "conflict", message = "Already exists" });
                                    failCount++;
                                    continue;
                                case "skip":
                                    results.Add(new { assetId, name = assetName, status = "skipped", message = "Already exists" });
                                    continue;
                                case "overwrite":
                                    // Continue with download
                                    break;
                            }
                        }

                        // Download
                        int version = assetInfo?.latestVersionNumber ?? 1;
                        bool success = await client.DownloadAssetAsync(assetId, version, fullPath, null);

                        if (success)
                        {
                            // Write asset ID marker file
                            string markerPath = Path.Combine(fullPath, $"_assetId.txt");
                            File.WriteAllText(markerPath, assetId);

                            successCount++;
                            results.Add(new { assetId, name = assetName, status = "success", path = assetFolder });
                        }
                        else
                        {
                            failCount++;
                            results.Add(new { assetId, name = assetName, status = "failed", message = "Download failed" });
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        results.Add(new { assetId, status = "failed", message = ex.Message });
                    }
                }

                AssetDatabase.Refresh();

                _jobs[jobId].Status = "complete";
                _jobs[jobId].Result = new
                {
                    status = "complete",
                    total = assetIds.Length,
                    success = successCount,
                    failed = failCount,
                    results
                };
            }
            catch (Exception ex)
            {
                _jobs[jobId].Status = "error";
                _jobs[jobId].Error = ex.Message;
            }
        }

        // ─── Tool 3b: PollDownloadJob ───
        public static string PollDownloadJob(string jobId)
        {
            return PollJob(jobId);
        }

        // ─── Tool 4: FindLocalModelPaths ───
        public static string FindLocalModelPaths(string assetIdsJson)
        {
            try
            {
                var assetIds = JsonConvert.DeserializeObject<string[]>(assetIdsJson);
                var paths = new List<object>();

                string searchRoot = Path.Combine(Application.dataPath, "CommonArt", "3DAssets");

                foreach (var assetId in assetIds)
                {
                    string folderPath = "";
                    string fbxPath = "";
                    string prefabPath = "";

                    // Search for _assetId.txt markers
                    if (Directory.Exists(searchRoot))
                    {
                        var markerFiles = Directory.GetFiles(searchRoot, "_assetId.txt", SearchOption.AllDirectories);
                        foreach (var marker in markerFiles)
                        {
                            string content = File.ReadAllText(marker).Trim();
                            if (content == assetId)
                            {
                                string folder = Path.GetDirectoryName(marker);
                                // Convert to Unity relative path
                                folderPath = "Assets" + folder.Substring(Application.dataPath.Length).Replace("\\", "/");

                                // Find FBX and prefab
                                var fbxFiles = Directory.GetFiles(folder, "*.fbx", SearchOption.TopDirectoryOnly);
                                if (fbxFiles.Length > 0)
                                    fbxPath = "Assets" + fbxFiles[0].Substring(Application.dataPath.Length).Replace("\\", "/");

                                var prefabFiles = Directory.GetFiles(folder, "*.prefab", SearchOption.TopDirectoryOnly);
                                if (prefabFiles.Length > 0)
                                    prefabPath = "Assets" + prefabFiles[0].Substring(Application.dataPath.Length).Replace("\\", "/");

                                break;
                            }
                        }
                    }

                    paths.Add(new { assetId, folderPath, fbxPath, prefabPath });
                }

                return JsonConvert.SerializeObject(new { paths });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        // ─── Tool 5: AddToScene ───
        public static string AddToScene(string assetPath, string name,
            float posX, float posY, float posZ,
            float rotX, float rotY, float rotZ,
            float sclX, float sclY, float sclZ,
            string parent)
        {
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset == null)
                {
                    return JsonConvert.SerializeObject(new { error = true, message = $"Asset not found: {assetPath}" });
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                if (instance == null)
                {
                    // Fallback for FBX
                    instance = UnityEngine.Object.Instantiate(asset);
                }

                if (!string.IsNullOrEmpty(name))
                    instance.name = name;

                instance.transform.position = new Vector3(posX, posY, posZ);
                instance.transform.eulerAngles = new Vector3(rotX, rotY, rotZ);
                instance.transform.localScale = new Vector3(sclX, sclY, sclZ);

                if (!string.IsNullOrEmpty(parent))
                {
                    var parentObj = GameObject.Find(parent);
                    if (parentObj != null)
                        instance.transform.SetParent(parentObj.transform, true);
                }

                Undo.RegisterCreatedObjectUndo(instance, $"MCP Add {instance.name}");
                EditorUtility.SetDirty(instance);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    name = instance.name,
                    instanceId = instance.GetInstanceID(),
                    path = GetHierarchyPath(instance.transform)
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        // ─── Tool 9: PlaceSmart ───
        public static string PlaceSmart(string assetPath, string name, string targetArea,
            float spacing, bool snapToGround,
            float sclX, float sclY, float sclZ,
            float centerX, float centerY, float centerZ,
            bool strictSafePlacement, string qualityPreset)
        {
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset == null)
                {
                    return JsonConvert.SerializeObject(new { error = true, message = $"Asset not found: {assetPath}" });
                }

                // Determine center position
                Vector3 center;
                if (float.IsNaN(centerX) || float.IsNaN(centerY) || float.IsNaN(centerZ))
                {
                    center = FindSceneCenter(targetArea);
                }
                else
                {
                    center = new Vector3(centerX, centerY, centerZ);
                }

                // Find safe position using spiral search
                Vector3 placement = FindSafePlacement(center, spacing, strictSafePlacement, qualityPreset);

                // Instantiate
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                if (instance == null)
                    instance = UnityEngine.Object.Instantiate(asset);

                if (!string.IsNullOrEmpty(name))
                    instance.name = name;

                instance.transform.position = placement;
                instance.transform.localScale = new Vector3(sclX, sclY, sclZ);

                // Parent to target area if specified
                if (!string.IsNullOrEmpty(targetArea))
                {
                    var parentObj = GameObject.Find(targetArea);
                    if (parentObj != null)
                        instance.transform.SetParent(parentObj.transform, true);
                }

                // Snap to ground
                if (snapToGround)
                {
                    if (Physics.Raycast(instance.transform.position + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 100f))
                    {
                        instance.transform.position = hit.point;
                    }
                }

                Undo.RegisterCreatedObjectUndo(instance, $"MCP PlaceSmart {instance.name}");
                EditorUtility.SetDirty(instance);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    name = instance.name,
                    instanceId = instance.GetInstanceID(),
                    path = GetHierarchyPath(instance.transform),
                    position = new { x = instance.transform.position.x, y = instance.transform.position.y, z = instance.transform.position.z }
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        // ─── Tool 10: PlaceSmartBatch ───
        public static string PlaceSmartBatch(string assetPathsJson, string targetArea,
            float spacing, bool snapToGround,
            float sclX, float sclY, float sclZ,
            float centerX, float centerY, float centerZ,
            bool strictSafePlacement, string qualityPreset)
        {
            try
            {
                var assetPaths = JsonConvert.DeserializeObject<string[]>(assetPathsJson);
                var placed = new List<object>();
                var skipped = new List<object>();
                var placedPositions = new List<Vector3>();

                Vector3 center;
                if (float.IsNaN(centerX) || float.IsNaN(centerY) || float.IsNaN(centerZ))
                {
                    center = FindSceneCenter(targetArea);
                }
                else
                {
                    center = new Vector3(centerX, centerY, centerZ);
                }

                foreach (var assetPath in assetPaths)
                {
                    try
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        if (asset == null)
                        {
                            skipped.Add(new { assetPath, reason = "Asset not found" });
                            continue;
                        }

                        // Find safe position that avoids existing + batch-placed objects
                        Vector3 position = FindSafePlacementWithExclusions(center, spacing, placedPositions, strictSafePlacement, qualityPreset);

                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                        if (instance == null)
                            instance = UnityEngine.Object.Instantiate(asset);

                        instance.transform.position = position;
                        instance.transform.localScale = new Vector3(sclX, sclY, sclZ);

                        if (!string.IsNullOrEmpty(targetArea))
                        {
                            var parentObj = GameObject.Find(targetArea);
                            if (parentObj != null)
                                instance.transform.SetParent(parentObj.transform, true);
                        }

                        if (snapToGround)
                        {
                            if (Physics.Raycast(instance.transform.position + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 100f))
                            {
                                instance.transform.position = hit.point;
                            }
                        }

                        Undo.RegisterCreatedObjectUndo(instance, $"MCP BatchPlace {instance.name}");
                        placedPositions.Add(instance.transform.position);

                        placed.Add(new
                        {
                            assetPath,
                            name = instance.name,
                            instanceId = instance.GetInstanceID(),
                            position = new { x = instance.transform.position.x, y = instance.transform.position.y, z = instance.transform.position.z }
                        });
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new { assetPath, reason = ex.Message });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    placedCount = placed.Count,
                    skippedCount = skipped.Count,
                    placed,
                    skipped
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = true, message = ex.Message });
            }
        }

        // ─── Helpers ───

        private static string PollJob(string jobId)
        {
            if (!_jobs.ContainsKey(jobId))
            {
                return JsonConvert.SerializeObject(new { status = "error", error = $"Unknown job: {jobId}" });
            }

            var job = _jobs[jobId];
            if (job.Status == "running")
            {
                return JsonConvert.SerializeObject(new { status = "running", progress = job.Progress });
            }
            else if (job.Status == "complete")
            {
                string result = JsonConvert.SerializeObject(job.Result);
                _jobs.Remove(jobId);
                return result;
            }
            else // error
            {
                string result = JsonConvert.SerializeObject(new { status = "error", error = job.Error });
                _jobs.Remove(jobId);
                return result;
            }
        }

        private static Vector3 FindSceneCenter(string targetArea)
        {
            if (!string.IsNullOrEmpty(targetArea))
            {
                var target = GameObject.Find(targetArea);
                if (target != null)
                    return target.transform.position;
            }

            // Find center of all renderers in scene
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            if (renderers.Length == 0)
                return Vector3.zero;

            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);

            return bounds.center;
        }

        private static Vector3 FindSafePlacement(Vector3 center, float spacing,
            bool strict, string preset)
        {
            return FindSafePlacementWithExclusions(center, spacing, new List<Vector3>(), strict, preset);
        }

        private static Vector3 FindSafePlacementWithExclusions(Vector3 center, float spacing,
            List<Vector3> excludePositions, bool strict, string preset)
        {
            float minSpacing = spacing;
            if (preset == "strict") minSpacing = Mathf.Max(spacing, 5f);
            else if (preset == "relaxed") minSpacing = Mathf.Max(spacing * 0.5f, 0.3f);

            // Spiral search outward from center
            int maxAttempts = strict ? 200 : 50;
            float stepSize = minSpacing;

            for (int i = 0; i < maxAttempts; i++)
            {
                // Spiral coordinates
                float angle = i * 137.5f * Mathf.Deg2Rad; // Golden angle
                float radius = stepSize * Mathf.Sqrt(i);
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0,
                    Mathf.Sin(angle) * radius
                );

                // Check against excluded positions
                bool tooClose = false;
                foreach (var pos in excludePositions)
                {
                    if (Vector3.Distance(candidate, pos) < minSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    // Check against scene objects with colliders
                    var nearbyColliders = Physics.OverlapSphere(candidate, minSpacing * 0.5f);
                    if (!strict || nearbyColliders.Length == 0)
                    {
                        return candidate;
                    }
                }
            }

            // Fallback: place at offset from center
            float fallbackOffset = excludePositions.Count * minSpacing;
            return center + new Vector3(fallbackOffset, 0, 0);
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            if (transform.parent == null) return transform.name;
            return GetHierarchyPath(transform.parent) + "/" + transform.name;
        }
    }
}
#endif
