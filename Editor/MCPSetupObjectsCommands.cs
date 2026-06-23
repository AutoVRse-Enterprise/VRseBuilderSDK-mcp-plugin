using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRseBuilder.Platform.MetaXR.Editor;
using GOQ = VRseBuilder.Core.Framework.GameObjectQuery;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Route: vrse/setup-objects (Level 4 of the additive VRse stage-tool pipeline).
    ///
    /// Per object: resolve the art source -> duplicate -> rename to the logical name -> parent into the
    /// (pre-resolved) container path (creating any missing container segments as a lazy fallback) ->
    /// convert to its interactable type -> finalize (GameObjectQuery + collider + enableOnStart=false).
    ///
    /// Logic ported for PARITY from VrseClaw-kit/vrse-editor-helpers/VRseBatchSetup.cs
    /// (DoDuplicate / DoSetupGrabbable / DoSetupTouchable + helpers). This is CONVERSION +
    /// marker-based placement only; spatial inference (on/inside/near) belongs to vrse/place-objects.
    ///
    /// The destination `parent` path is resolved by the Node tool (template-aware); this route just
    /// ensures it exists and parents into it. Container GOQs are added with the native
    /// AddComponent&lt;GameObjectQuery&gt;() — identical to DoCreateContainer — so they are harvestable.
    /// </summary>
    public static class MCPSetupObjectsCommands
    {
        private const string TAG = "[vrse/setup-objects]";

        // Placepoint trigger-collider sizing (parity with VRseBatchSetup): grabbable bounds × scale, clamped.
        private const float TT_PP_MarginScale = 1.15f;
        private const float TT_PP_SizeMin = 0.1f;
        private const float TT_PP_SizeMax = 1.0f;
        // Spatial placepoint (on-surface raycast grid) constants.
        private const float TT_PP_GridResolution = 5;
        private const float TT_PP_RaycastOver = 0.5f;
        private const float TT_PP_LiftFactor = 0.5f;
        private const float TT_PP_LiftDefault = 0.05f;
        private static readonly string[] TT_NonBlockingNamePatterns = new[]
        {
            "FadePlane", "Fade_Plane", "Vignette", "TeleportZone", "AudioZone",
            "ColliderTrigger", "RoomCollisionVignette", "TriggerVolume", "GhostHand",
            "Highlighter", "Outline"
        };
        // Teleport/spawn ring-search constants.
        private const float TT_HeadHeight = 1.6f;
        private const float TT_CapsuleRadius = 0.3f;
        private const float TT_CapsuleLow = 0.4f;
        private const float TT_CapsuleHigh = 1.7f;
        private const float TT_FloorNormalY = 0.7f;
        private const float TT_RaycastDown = 5f;
        private const float TT_DistanceMin = 0.6f;
        private const float TT_DistanceMax = 1.4f;
        private const float TT_DistanceOver = 0.5f;
        private const float TT_DistanceWiden = 0.4f;
        private const int TT_NumCandidates = 8;

        public static object SetupObjects(Dictionary<string, object> args)
        {
            if (args == null || !args.ContainsKey("objects") || !(args["objects"] is List<object> rawObjects))
                return new { success = false, error = "objects[] is required" };

            var results = new List<object>();
            int converted = 0, skipped = 0, failed = 0;

            foreach (var raw in rawObjects)
            {
                var o = raw as Dictionary<string, object>;
                if (o == null) { failed++; results.Add(Result(null, null, false, false, "object entry is not an object")); continue; }

                string type = GetStr(o, "type").ToLowerInvariant();
                string source = GetStr(o, "source");
                string name = GetStr(o, "name");
                string parentPath = GetStr(o, "parent");
                string anchor = GetStr(o, "anchor");
                string preferScene = GetStr(o, "preferScene");

                if (string.IsNullOrEmpty(name)) { failed++; results.Add(Result(name, null, false, false, "name is required")); continue; }

                try
                {
                    var parent = EnsurePath(parentPath);
                    if (parent == null) { failed++; results.Add(Result(name, parentPath, false, false, $"could not resolve or create parent '{parentPath}'")); continue; }

                    string outcome;
                    GameObject obj;
                    switch (type)
                    {
                        case "grabbable":
                        case "pivot": // no pivot converter — convert as grabbable; PivotRotateLimiter is wired separately
                            obj = SetupGrabbable(source, name, parent, anchor, preferScene, out outcome);
                            break;
                        case "touchable":
                            obj = SetupTouchable(source, name, parent, anchor, preferScene, out outcome);
                            break;
                        case "simple":
                        case "duplicate":
                            obj = Duplicate(source, name, parent, preferScene, out outcome);
                            break;
                        case "placepoint":
                            obj = SetupPlacePoint(o, name, parent, out outcome);
                            break;
                        default:
                            failed++; results.Add(Result(name, null, false, false, $"unknown type '{type}'"));
                            continue;
                    }

                    if (obj == null) { failed++; results.Add(Result(name, parentPath, false, false, outcome)); continue; }

                    if (outcome == "skipped")
                    {
                        skipped++;
                        results.Add(Result(name, GetPath(obj.transform), true, true, null));
                        continue;
                    }

                    EnsureGOQ(obj); // every converted object must be harvestable (idempotent)

                    converted++;
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "path", GetPath(obj.transform) },
                        { "ok", true },
                        { "skipped", false },
                        { "nameOk", obj.name == name },
                        { "hasMeshChild", obj.transform.Find(name + "_Mesh") != null },
                        { "components", ComponentNames(obj) }
                    });
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(Result(name, parentPath, false, false, ex.Message));
                }
            }

            return new Dictionary<string, object>
            {
                { "success", failed == 0 },
                { "converted", converted },
                { "skipped", skipped },
                { "failed", failed },
                { "results", results }
            };
        }

        // ── per-type conversion (ported from VRseBatchSetup) ─────────────────────

        private static GameObject SetupGrabbable(string source, string name, Transform parent, string anchorName, string preferScene, out string outcome)
        {
            outcome = null;
            var src = FindByName(source, preferScene);
            if (src == null) { outcome = $"source '{source}' not found"; return null; }
            if (!HasMesh(src)) { outcome = $"source '{source}' has no mesh"; return null; }

            // Resolve transform: explicit anchor marker > source object position.
            var anchor = !string.IsNullOrEmpty(anchorName) ? FindByName(anchorName) : null;
            var srcPos = anchor != null ? anchor.transform.position : src.transform.position;
            var srcRot = anchor != null ? anchor.transform.rotation : src.transform.rotation;
            var srcScale = src.transform.localScale;

            var dup = UnityEngine.Object.Instantiate(src);
            Undo.RegisterCreatedObjectUndo(dup, "Dup " + source);
            ForceActivateMeshHierarchy(dup);
            dup.name = name;
            Undo.SetTransformParent(dup.transform, parent, "Reparent " + name);
            dup.transform.position = srcPos;
            dup.transform.rotation = Quaternion.identity; // reset before conversion
            dup.transform.localScale = srcScale;

            if (dup.GetComponent<VRseBuilder.Core.Interfaces.IGrabbableWrapper>() != null) { outcome = "skipped"; return dup; }

            MetaXRInteractableConverter.ConvertToNetworkMetaXRGrabbable(dup);

            // Post-conversion: re-find wrapper, apply art rotation, force enableOnStart=false, tighten collider.
            var wrapper = FindByName(name) ?? dup;
            wrapper.transform.rotation = srcRot;
            SetEnableOnStartFalse(wrapper);
            TightenGrabbableCollider(wrapper);
            return wrapper;
        }

        private static GameObject SetupTouchable(string source, string name, Transform parent, string anchorName, string preferScene, out string outcome)
        {
            outcome = null;
            var src = FindByName(source, preferScene);
            if (src == null) { outcome = $"source '{source}' not found"; return null; }
            if (!HasMesh(src)) { outcome = $"source '{source}' has no mesh"; return null; }

            var anchor = !string.IsNullOrEmpty(anchorName) ? FindByName(anchorName) : null;
            var srcPos = anchor != null ? anchor.transform.position : src.transform.position;
            var srcRot = anchor != null ? anchor.transform.rotation : src.transform.rotation;
            var srcScale = src.transform.localScale;

            var dup = UnityEngine.Object.Instantiate(src);
            Undo.RegisterCreatedObjectUndo(dup, "Dup " + source);
            ForceActivateMeshHierarchy(dup);
            dup.name = name;
            Undo.SetTransformParent(dup.transform, parent, "Reparent " + name);
            dup.transform.position = srcPos;
            dup.transform.rotation = srcRot;
            dup.transform.localScale = srcScale;

            MetaXRInteractableConverter.ConvertToTouchable(dup);

            // Post-convert reparent guard if the converter relocated the object.
            var converted = FindByName(name);
            if (converted != null && converted.transform.parent != parent)
            {
                Undo.SetTransformParent(converted.transform, parent, "Post-convert reparent " + name);
                converted.transform.position = srcPos;
                converted.transform.rotation = srcRot;
                converted.transform.localScale = srcScale;
            }
            var result = converted ?? dup;
            EnsureTriggerCollider(result);
            return result;
        }

        private static GameObject Duplicate(string source, string name, Transform parent, string preferScene, out string outcome)
        {
            outcome = null;
            var src = FindByName(source, preferScene);
            if (src == null) { outcome = $"source '{source}' not found"; return null; }
            if (!HasMesh(src)) { outcome = $"source '{source}' has no mesh"; return null; }

            var srcPos = src.transform.position;
            var srcRot = src.transform.rotation;
            var srcScale = src.transform.localScale;

            var dup = UnityEngine.Object.Instantiate(src);
            Undo.RegisterCreatedObjectUndo(dup, "Dup " + source);
            ForceActivateMeshHierarchy(dup);
            dup.name = name;
            Undo.SetTransformParent(dup.transform, parent, "Reparent " + name);
            dup.transform.position = srcPos;
            dup.transform.rotation = srcRot;
            dup.transform.localScale = srcScale;

            EnsureTriggerCollider(dup);
            return dup;
        }

        // ── marker-based placepoint (ported from DoCreatePlacePoint; spatial on/inside is place_objects) ──
        private static GameObject SetupPlacePoint(Dictionary<string, object> o, string name, Transform parent, out string outcome)
        {
            outcome = null;
            string anchorName = GetStr(o, "anchor");
            string positionCsv = GetStr(o, "position");
            string allows = GetStr(o, "allows");
            var forcePlace = ParseNullableBool(GetStr(o, "force_place"));
            var heldPlaceOnly = ParseNullableBool(GetStr(o, "held_place_only"));

            // Resolve anchor: explicit anchor name, else the "<name>_PP" marker convention.
            GameObject anchor = !string.IsNullOrEmpty(anchorName) ? FindByName(anchorName) : null;
            if (anchor == null) anchor = FindByName(name + "_PP");

            // Temporary placeholder at the anchor (world) or explicit position (local) or default.
            var ph = new GameObject("_BatchPH_" + name);
            Undo.RegisterCreatedObjectUndo(ph, "Create placeholder for " + name);
            Undo.SetTransformParent(ph.transform, parent, "Reparent placeholder");
            if (anchor != null)
            {
                ph.transform.position = anchor.transform.position;
                ph.transform.rotation = anchor.transform.rotation;
            }
            else
            {
                ph.transform.localPosition = ParseVector3(positionCsv, new Vector3(0, 1, 1.5f));
            }

            // Convert via Selection (parity with DoCreatePlacePoint — parameterless overload).
            Selection.activeGameObject = ph;
            MetaXRInteractableConverter.CreateNetworkMetaXRPlacePoint();

            var np = FindByName("NetworkPlacePoint");
            if (np == null)
            {
                var phDead = FindByName("_BatchPH_" + name);
                if (phDead != null) Undo.DestroyObjectImmediate(phDead);
                outcome = "NetworkPlacePoint not found after conversion";
                return null;
            }

            Undo.RecordObject(np, "Rename to " + name);
            np.name = name;
            EnsureGOQ(np);
            if (np.transform.parent != parent) Undo.SetTransformParent(np.transform, parent, "Reparent " + name);
            if (anchor != null)
            {
                np.transform.position = anchor.transform.position;
                np.transform.rotation = anchor.transform.rotation;
            }
            SetEnableOnStartFalse(np);

            ApplyPlacementMode(np, forcePlace, heldPlaceOnly, name);

            // Size the trigger collider from the first allowed grabbable's bounds (opt-in via `allows`).
            string sizingGrabbable = string.IsNullOrEmpty(allows)
                ? null
                : allows.Split(',').Select(s => s.Trim()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (!string.IsNullOrEmpty(sizingGrabbable))
            {
                var box = np.GetComponent<BoxCollider>() ?? np.GetComponentInChildren<BoxCollider>(true);
                if (box != null && TryComputeAllowsColliderSize(sizingGrabbable, out Vector3 ppSize))
                {
                    Undo.RecordObject(box, "Resize collider for " + name);
                    box.center = Vector3.zero;
                    box.size = ppSize;
                    box.isTrigger = true;
                    EditorUtility.SetDirty(box);
                }
            }

            var phCheck = FindByName("_BatchPH_" + name);
            if (phCheck != null) Undo.DestroyObjectImmediate(phCheck);

            return np;
        }

        // ── vrse/place-objects (Level 5A: spatial placepoints — on a surface / inside a container) ──
        // Spatial inference is C#-only (raycasts/bounds against the live scene). Spawn points and
        // teleport targets (`near` ring-search) are Phase 5B.
        public static object PlaceObjects(Dictionary<string, object> args)
        {
            if (args == null || !args.ContainsKey("objects") || !(args["objects"] is List<object> rawObjects))
                return new { success = false, error = "objects[] is required" };

            var results = new List<object>();
            int placed = 0, failed = 0;

            foreach (var raw in rawObjects)
            {
                var o = raw as Dictionary<string, object>;
                if (o == null) { failed++; results.Add(Result(null, null, false, false, "object entry is not an object")); continue; }

                string type = GetStr(o, "type").ToLowerInvariant();
                string name = GetStr(o, "name");
                string parentPath = GetStr(o, "parent");
                bool hasOnInside = !string.IsNullOrEmpty(GetStr(o, "on")) || !string.IsNullOrEmpty(GetStr(o, "inside"));
                if (string.IsNullOrEmpty(name)) { failed++; results.Add(Result(name, null, false, false, "name is required")); continue; }

                try
                {
                    var parent = EnsurePath(parentPath);
                    if (parent == null) { failed++; results.Add(Result(name, parentPath, false, false, $"could not resolve or create parent '{parentPath}'")); continue; }

                    bool isSpawn = type == "spawnpoint" || type == "spawn" || type == "teleport" || type == "teleporttarget" || !string.IsNullOrEmpty(GetStr(o, "near"));
                    GameObject obj;
                    string outcome;
                    string warning = null;
                    if (type == "placepoint" || hasOnInside)
                        obj = SetupPlacePointSpatial(o, name, parent, out outcome, out warning);
                    else if (isSpawn)
                        obj = SetupSpawnPoint(o, name, parent, out outcome);
                    else
                    {
                        failed++; results.Add(Result(name, null, false, false, $"place_objects: unsupported type '{type}' (expected placepoint with on/inside, or spawnpoint/teleport with near/marker)"));
                        continue;
                    }
                    if (obj == null) { failed++; results.Add(Result(name, parentPath, false, false, outcome)); continue; }

                    placed++;
                    var ok = new Dictionary<string, object>
                    {
                        { "name", name }, { "path", GetPath(obj.transform) }, { "ok", true },
                        { "skipped", false }, { "nameOk", obj.name == name }, { "components", ComponentNames(obj) }
                    };
                    if (!string.IsNullOrEmpty(warning)) ok["warning"] = warning;
                    results.Add(ok);
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(Result(name, parentPath, false, false, ex.Message));
                }
            }

            return new Dictionary<string, object>
            {
                { "success", failed == 0 }, { "placed", placed }, { "failed", failed }, { "results", results }
            };
        }

        // ── Level 6: harvest GameObjectQuery IDs ─────────────────────────────────
        // Enumerates GameObjectQuery components DIRECTLY via FindObjectsOfType (NOT the
        // QueryObjectsIdManager registry, which is stale for code-added GOQs until a rebuild/save) — so
        // every live GOQ, including ones just added by setup_objects/place_objects, is seen with its valid
        // auto-assigned ID. Produces the name->ID rows that generate_story (and validation) bind the Story
        // JSON to. Optional `save` persists the open scenes first (parity with the kit's "save x2"); direct
        // enumeration already yields valid IDs, so it defaults off. `root` scopes the harvest to a container
        // subtree (default 'QueryObjects' from the Node layer); pass empty to harvest all loaded scenes.
        public static object HarvestIds(Dictionary<string, object> args)
        {
            string root = GetStr(args, "root");
            bool save = ParseNullableBool(GetStr(args, "save")) == true;
            bool includeInvalid = ParseNullableBool(GetStr(args, "include_invalid")) != false; // default true

            if (save)
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

            Transform rootT = null;
            if (!string.IsNullOrEmpty(root))
            {
                var rootGo = FindByName(root);
                if (rootGo == null)
                    return new Dictionary<string, object> { { "success", false }, { "error", $"root '{root}' not found in any loaded scene" } };
                rootT = rootGo.transform;
            }

            var results = new List<object>();
            int valid = 0, invalid = 0;
            foreach (var goq in UnityEngine.Object.FindObjectsOfType<GOQ>(true))
            {
                if (goq == null || goq.gameObject == null) continue;
                if (rootT != null && !goq.transform.IsChildOf(rootT)) continue;

                bool idOk = goq.IsIDValid && goq.ID > 0;
                if (idOk) valid++; else invalid++;
                if (!idOk && !includeInvalid) continue;

                results.Add(new Dictionary<string, object>
                {
                    { "name", goq.gameObject.name },
                    { "queryName", goq.Name },
                    { "id", goq.ID },
                    { "isIDValid", goq.IsIDValid },
                    { "path", GetPath(goq.transform) },
                    { "activeInHierarchy", goq.gameObject.activeInHierarchy },
                    { "vrseComponents", ComponentNames(goq.gameObject) }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "total", valid + invalid },
                { "valid", valid },
                { "invalid", invalid },
                { "saved", save },
                { "root", root },
                { "results", results }
            };
        }

        // Spatial placepoint: compute pose from `on`/`inside`, then create+finalize (parity with
        // DoCreatePlacePointSpatial). Shares the create/convert/finalize shape with SetupPlacePoint.
        private static GameObject SetupPlacePointSpatial(Dictionary<string, object> o, string name, Transform parent, out string outcome, out string warning)
        {
            outcome = null;
            warning = null;
            string on = GetStr(o, "on");
            string inside = GetStr(o, "inside");
            string allows = GetStr(o, "allows");
            if (string.IsNullOrEmpty(on) && string.IsNullOrEmpty(inside)) { outcome = "spatial placepoint requires 'on' or 'inside'"; return null; }
            if (!string.IsNullOrEmpty(on) && !string.IsNullOrEmpty(inside)) { outcome = "'on' and 'inside' are mutually exclusive"; return null; }
            if (string.IsNullOrEmpty(allows)) { outcome = "spatial placepoint requires 'allows' (allowed grabbable name(s), CSV)"; return null; }
            var allowsNames = allows.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (allowsNames.Length == 0) { outcome = $"'allows' parsed to zero names (got '{allows}')"; return null; }
            string sizingGrabbable = allowsNames[0];

            bool ok = !string.IsNullOrEmpty(inside)
                ? ResolveInsidePlacepointTransform(inside, sizingGrabbable, out Vector3 pos, out Quaternion rot, out Vector3 colliderSize, out bool grabbableResolved, out string diag)
                : ResolveTopOfPlacepointTransform(on, sizingGrabbable, out pos, out rot, out colliderSize, out grabbableResolved, out diag);
            if (!ok) { outcome = diag; return null; }

            // Sizing is NON-FATAL: if the `allows` grabbable was not found in the loaded scenes the resolver
            // falls back to a heuristic collider size (a fixed default for `on`, a container-relative size for
            // `inside`). Surface that as a WARNING (not a failure) so standalone callers know the trigger was
            // NOT sized from the intended object, instead of silently shipping a wrong-sized collider.
            if (!grabbableResolved)
                warning = $"placepoint collider was NOT sized from '{sizingGrabbable}' — no object with that name " +
                          "and renderable geometry was found in the loaded scenes; a fallback size was used. " +
                          "Load the art/source scene, correct the `allows` name, or run vrse_setup_objects first.";

            var forcePlace = ParseNullableBool(GetStr(o, "force_place"));
            var heldPlaceOnly = ParseNullableBool(GetStr(o, "held_place_only"));

            var ph = new GameObject("_BatchPH_" + name);
            Undo.RegisterCreatedObjectUndo(ph, "Create placeholder for " + name);
            Undo.SetTransformParent(ph.transform, parent, "Reparent placeholder");
            ph.transform.position = pos;
            ph.transform.rotation = rot;

            Selection.activeGameObject = ph;
            MetaXRInteractableConverter.CreateNetworkMetaXRPlacePoint();

            var np = FindByName("NetworkPlacePoint");
            if (np == null)
            {
                var phDead = FindByName("_BatchPH_" + name);
                if (phDead != null) Undo.DestroyObjectImmediate(phDead);
                outcome = "NetworkPlacePoint not found after conversion";
                return null;
            }

            Undo.RecordObject(np, "Rename to " + name);
            np.name = name;
            EnsureGOQ(np);
            if (np.transform.parent != parent) Undo.SetTransformParent(np.transform, parent, "Reparent " + name);
            np.transform.position = pos;
            np.transform.rotation = rot;

            var box = np.GetComponent<BoxCollider>() ?? np.GetComponentInChildren<BoxCollider>(true);
            if (box != null)
            {
                Undo.RecordObject(box, "Resize collider for " + name);
                box.center = Vector3.zero;
                box.size = colliderSize;
                box.isTrigger = true;
                EditorUtility.SetDirty(box);
            }

            SetEnableOnStartFalse(np);
            ApplyPlacementMode(np, forcePlace, heldPlaceOnly, name);

            var phCheck = FindByName("_BatchPH_" + name);
            if (phCheck != null) Undo.DestroyObjectImmediate(phCheck);
            return np;
        }

        // Shared placement-mode override via reflection (avoids a hard ref to the MetaXR runtime wrapper).
        private static void ApplyPlacementMode(GameObject np, bool? forcePlace, bool? heldPlaceOnly, string name)
        {
            if (!forcePlace.HasValue && !heldPlaceOnly.HasValue) return;
            foreach (var comp in np.GetComponents<MonoBehaviour>())
            {
                if (comp == null || comp.GetType().Name != "MetaXRPlacePointWrapper") continue;
                Undo.RecordObject(comp, "Configure placement mode for " + name);
                var ct = comp.GetType();
                if (forcePlace.HasValue) { var p = ct.GetProperty("ForcePlace"); if (p != null && p.CanWrite) p.SetValue(comp, forcePlace.Value); }
                if (heldPlaceOnly.HasValue) { var p = ct.GetProperty("HeldPlaceOnly"); if (p != null && p.CanWrite) p.SetValue(comp, heldPlaceOnly.Value); }
                EditorUtility.SetDirty(comp);
                break;
            }
        }

        // ── spatial resolvers (ported verbatim from VRseBatchSetup) ──────────────
        private static bool ResolveTopOfPlacepointTransform(string surfaceName, string grabbableName,
            out Vector3 position, out Quaternion rotation, out Vector3 colliderSize, out bool grabbableResolved, out string diagnostic)
        {
            position = Vector3.zero; rotation = Quaternion.identity; colliderSize = new Vector3(0.3f, 0.3f, 0.3f);
            grabbableResolved = false; diagnostic = "";

            var surface = FindByName(surfaceName);
            if (surface == null) { diagnostic = $"surface '{surfaceName}' not found in scene"; return false; }
            if (!TryGetWorldBoundsLocal(surface, out Bounds surfaceBounds)) { diagnostic = $"surface '{surfaceName}' has no renderer or collider"; return false; }

            List<MeshCollider> tempColliders = null;
            try
            {
                tempColliders = AddTempCollidersLocal();
                float bestY = float.NegativeInfinity;
                int validHits = 0;
                float xMin = surfaceBounds.min.x, xMax = surfaceBounds.max.x;
                float zMin = surfaceBounds.min.z, zMax = surfaceBounds.max.z;
                float originY = surfaceBounds.max.y + TT_PP_RaycastOver;
                float rayLen = surfaceBounds.size.y + TT_PP_RaycastOver * 2f + 0.5f;

                for (int ix = 0; ix < TT_PP_GridResolution; ix++)
                {
                    for (int iz = 0; iz < TT_PP_GridResolution; iz++)
                    {
                        float x = Mathf.Lerp(xMin, xMax, (ix + 0.5f) / TT_PP_GridResolution);
                        float z = Mathf.Lerp(zMin, zMax, (iz + 0.5f) / TT_PP_GridResolution);
                        var origin = new Vector3(x, originY, z);
                        var hits = Physics.RaycastAll(origin, Vector3.down, rayLen);
                        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                        foreach (var h in hits)
                        {
                            if (h.collider.isTrigger) continue;
                            if (IsNonBlockingEffectCollider(h.collider)) continue;
                            if (h.normal.y < 0.7f) continue;
                            if (h.collider.gameObject != surface && !h.collider.transform.IsChildOf(surface.transform)) continue;
                            if (h.point.y > bestY) bestY = h.point.y;
                            validHits++;
                            break;
                        }
                    }
                }

                float topY;
                if (validHits > 0) topY = bestY;
                else { topY = surfaceBounds.max.y; diagnostic = $"uncertain top — no valid downward hits on '{surfaceName}'; fell back to bounds.max.y={topY:F2}"; }

                var grabbable = FindByName(grabbableName);
                float grabbableHeight = 0.2f;
                if (grabbable != null && TryGetWorldBoundsLocal(grabbable, out Bounds gb))
                {
                    grabbableResolved = true;
                    colliderSize = new Vector3(
                        Mathf.Clamp(gb.size.x * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax),
                        Mathf.Clamp(gb.size.y * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax),
                        Mathf.Clamp(gb.size.z * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax));
                    grabbableHeight = gb.size.y;
                }

                float lift = grabbableResolved ? grabbableHeight * TT_PP_LiftFactor : TT_PP_LiftDefault;
                position = new Vector3(surfaceBounds.center.x, topY + lift, surfaceBounds.center.z);

                Vector3 fwd = surface.transform.forward; fwd.y = 0;
                rotation = fwd.sqrMagnitude > 0.01f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity;
                return true;
            }
            finally
            {
                if (tempColliders != null) RemoveTempCollidersLocal(tempColliders);
            }
        }

        private static bool ResolveInsidePlacepointTransform(string containerName, string grabbableName,
            out Vector3 position, out Quaternion rotation, out Vector3 colliderSize, out bool grabbableResolved, out string diagnostic)
        {
            position = Vector3.zero; rotation = Quaternion.identity; colliderSize = new Vector3(0.3f, 0.3f, 0.3f);
            grabbableResolved = false; diagnostic = "";

            var container = FindByName(containerName);
            if (container == null) { diagnostic = $"container '{containerName}' not found in scene"; return false; }
            if (!TryGetWorldBoundsLocal(container, out Bounds cb)) { diagnostic = $"container '{containerName}' has no renderer or collider"; return false; }

            float lowerInteriorY = cb.min.y + cb.size.y * 0.25f;
            position = new Vector3(cb.center.x, lowerInteriorY, cb.center.z);
            rotation = Quaternion.identity;

            var grabbable = FindByName(grabbableName);
            if (grabbable != null && TryGetWorldBoundsLocal(grabbable, out Bounds gb))
            {
                grabbableResolved = true;
                float marginX = Mathf.Clamp(gb.size.x * TT_PP_MarginScale, TT_PP_SizeMin, Mathf.Min(TT_PP_SizeMax, cb.size.x * 0.7f));
                float marginZ = Mathf.Clamp(gb.size.z * TT_PP_MarginScale, TT_PP_SizeMin, Mathf.Min(TT_PP_SizeMax, cb.size.z * 0.7f));
                float interiorY = Mathf.Max(gb.size.y * TT_PP_MarginScale, cb.size.y * 0.6f);
                interiorY = Mathf.Min(interiorY, cb.size.y * 0.9f);
                colliderSize = new Vector3(marginX, interiorY, marginZ);
            }
            else
            {
                float defaultXZ = Mathf.Min(0.3f, cb.size.x * 0.7f, cb.size.z * 0.7f);
                float defaultY = Mathf.Min(cb.size.y * 0.6f, cb.size.y * 0.9f);
                colliderSize = new Vector3(defaultXZ, defaultY, defaultXZ);
            }
            return true;
        }

        // ── spawn points + teleport targets (Phase 5B) ───────────────────────────
        // A spawn point is an empty GameObject + GameObjectQuery at a resolved pose. If `near` is set,
        // the pose is computed by ResolveTeleportTransform (ring-search); otherwise it is marker-based
        // (anchor / same-named external marker / "<name>" minus "_SP" prefix / explicit position).
        private static GameObject SetupSpawnPoint(Dictionary<string, object> o, string name, Transform parent, out string outcome)
        {
            outcome = null;
            string near = GetStr(o, "near");
            string anchorName = GetStr(o, "anchor");
            string positionCsv = GetStr(o, "position");
            string facing = GetStr(o, "facing");

            Vector3 pos;
            Quaternion rot = Quaternion.identity;

            if (!string.IsNullOrEmpty(near))
            {
                var nearNames = near.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                if (nearNames.Length == 0) { outcome = $"'near' parsed to zero names (got '{near}')"; return null; }
                bool faceAway = !string.IsNullOrEmpty(facing) && facing.Trim().Equals("away", System.StringComparison.OrdinalIgnoreCase);
                if (!ResolveTeleportTransform(nearNames, faceAway, out pos, out rot, out _, out _, out _, out string diag)) { outcome = diag; return null; }
            }
            else
            {
                // Marker-based. Prefer an explicit anchor; else a same-named object that is NOT already
                // under our parent (i.e. an external art marker, not a prior spawn point).
                GameObject marker = !string.IsNullOrEmpty(anchorName) ? FindByName(anchorName) : null;
                if (marker == null) { var m = FindByName(name); if (m != null && !m.transform.IsChildOf(parent)) marker = m; }

                if (marker != null)
                {
                    pos = marker.transform.position;
                    rot = marker.transform.rotation;
                }
                else if (name.EndsWith("_SP"))
                {
                    var prefixObj = FindByName(name.Substring(0, name.Length - 3));
                    if (prefixObj == null) { outcome = $"cannot resolve position for '{name}': no marker and no name-prefix match for '{name.Substring(0, name.Length - 3)}'"; return null; }
                    pos = prefixObj.transform.position;
                    rot = prefixObj.transform.rotation;
                }
                else if (!string.IsNullOrEmpty(positionCsv))
                {
                    pos = ParseVector3(positionCsv, Vector3.zero);
                }
                else { outcome = $"cannot resolve position for spawn point '{name}': provide `near`, an `anchor`/marker, a '_SP' name with a matching object, or `position`"; return null; }
            }

            var sp = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(sp, "Create " + name);
            Undo.SetTransformParent(sp.transform, parent, "Reparent " + name);
            sp.transform.position = pos;
            sp.transform.rotation = rot;
            EnsureGOQ(sp);
            return sp;
        }

        private static bool HitIsTargetOrDescendant(GameObject hit, List<GameObject> targets)
        {
            if (hit == null) return false;
            foreach (var t in targets)
            {
                if (hit == t) return true;
                if (hit.transform.IsChildOf(t.transform)) return true;
            }
            return false;
        }

        // Ported from VRseBatchSetup.ResolveTeleportTransform (verbose console logging omitted).
        private static bool ResolveTeleportTransform(string[] nearNames, bool faceAway,
            out Vector3 position, out Quaternion rotation, out float chosenDistance, out int chosenIndex, out float chosenScore, out string diagnostic)
        {
            position = Vector3.zero; rotation = Quaternion.identity;
            chosenDistance = 0f; chosenIndex = -1; chosenScore = 0f; diagnostic = "";

            // Step A — resolve targets, compute focus + cluster radius.
            var targets = new List<GameObject>(nearNames.Length);
            foreach (var n in nearNames)
            {
                var go = FindByName(n);
                if (go == null) { diagnostic = $"target '{n}' not found in scene"; return false; }
                targets.Add(go);
            }
            Bounds combined = default; bool boundsInit = false;
            foreach (var go in targets)
            {
                if (!TryGetWorldBoundsLocal(go, out Bounds b)) continue;
                if (!boundsInit) { combined = b; boundsInit = true; } else combined.Encapsulate(b);
            }
            if (!boundsInit) { diagnostic = $"none of the `near` targets ([{string.Join(",", nearNames)}]) have renderers or colliders"; return false; }

            Vector3 focus = combined.center;
            float clusterRadius = Mathf.Max(combined.extents.x, combined.extents.z);
            float baseDistance = Mathf.Clamp(clusterRadius + TT_DistanceOver, TT_DistanceMin, TT_DistanceMax);

            Vector3 preferredApproach = Vector3.zero;
            foreach (var t in targets) preferredApproach += t.transform.forward;
            preferredApproach.y = 0;
            bool hasForwardSignal = preferredApproach.sqrMagnitude > 0.01f;
            if (hasForwardSignal) preferredApproach.Normalize();

            List<MeshCollider> tempColliders = null;
            try
            {
                tempColliders = AddTempCollidersLocal();

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    float tryDistance = baseDistance + (attempt == 1 ? TT_DistanceWiden : 0f);
                    tryDistance = Mathf.Min(tryDistance, TT_DistanceMax + TT_DistanceWiden);

                    float bestScore = -1f;
                    Vector3 bestPos = Vector3.zero;
                    Quaternion bestRot = Quaternion.identity;
                    int bestIdx = -1;

                    for (int i = 0; i < TT_NumCandidates; i++)
                    {
                        float yaw = i * (360f / TT_NumCandidates);
                        Vector3 dir = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                        Vector3 candidateXZ = focus - dir * tryDistance;

                        // C.1 — floor.
                        if (!Physics.Raycast(new Vector3(candidateXZ.x, focus.y + 2f, candidateXZ.z), Vector3.down, out RaycastHit floorHit, TT_RaycastDown + 2f)) continue;
                        if (floorHit.normal.y < TT_FloorNormalY) continue;
                        Vector3 standPos = new Vector3(candidateXZ.x, floorHit.point.y, candidateXZ.z);

                        // C.2 — head-height line of sight to focus.
                        Vector3 headOrigin = standPos + Vector3.up * TT_HeadHeight;
                        Vector3 toFocus = focus - headOrigin;
                        float losDistance = toFocus.magnitude;
                        if (losDistance < 0.05f) continue;
                        Vector3 losDir = toFocus / losDistance;
                        var losHits = Physics.RaycastAll(headOrigin, losDir, losDistance + 0.5f);
                        System.Array.Sort(losHits, (a, b) => a.distance.CompareTo(b.distance));
                        bool losBlocked = false;
                        foreach (var h in losHits)
                        {
                            if (h.collider.isTrigger) continue;
                            if (IsNonBlockingEffectCollider(h.collider)) continue;
                            if (HitIsTargetOrDescendant(h.collider.gameObject, targets)) break;
                            losBlocked = true; break;
                        }
                        if (losBlocked) continue;

                        // C.3 — capsule clearance.
                        Vector3 capBottom = standPos + Vector3.up * TT_CapsuleLow;
                        Vector3 capTop = standPos + Vector3.up * TT_CapsuleHigh;
                        bool blocked = false;
                        float nearestBlockerDist = 3f;
                        foreach (var col in Physics.OverlapCapsule(capBottom, capTop, TT_CapsuleRadius))
                        {
                            if (col.bounds.max.y <= standPos.y + 0.05f) continue;
                            if (col.isTrigger) continue;
                            if (IsNonBlockingEffectCollider(col)) continue;
                            if (HitIsTargetOrDescendant(col.gameObject, targets)) continue;
                            blocked = true;
                            float d = Vector3.Distance(standPos, col.bounds.center);
                            if (d < nearestBlockerDist) nearestBlockerDist = d;
                            break;
                        }
                        if (blocked) continue;

                        // C.4 — score.
                        float planarity = floorHit.normal.y;
                        float losClearance = 1f;
                        float blockerSpace = Mathf.Min(nearestBlockerDist, 3f) / 3f;

                        Vector3 awayFromFocus = standPos - focus; awayFromFocus.y = 0;
                        float sideClearance = 1f;
                        if (awayFromFocus.sqrMagnitude > 0.0001f)
                        {
                            awayFromFocus.Normalize();
                            var sideHits = Physics.RaycastAll(focus, awayFromFocus, 5f);
                            System.Array.Sort(sideHits, (a, b) => a.distance.CompareTo(b.distance));
                            foreach (var h in sideHits)
                            {
                                if (h.collider.isTrigger) continue;
                                if (IsNonBlockingEffectCollider(h.collider)) continue;
                                if (HitIsTargetOrDescendant(h.collider.gameObject, targets)) continue;
                                sideClearance = Mathf.Clamp01(h.distance / 5f);
                                break;
                            }
                        }

                        float forwardAlign = 0.5f;
                        if (hasForwardSignal && awayFromFocus.sqrMagnitude > 0.0001f)
                            forwardAlign = (Vector3.Dot(awayFromFocus, preferredApproach) + 1f) * 0.5f;

                        float thisScore = planarity * 0.15f + losClearance * 0.15f + blockerSpace * 0.10f + sideClearance * 0.50f + forwardAlign * 0.10f;

                        if (thisScore > bestScore)
                        {
                            bestScore = thisScore;
                            bestPos = standPos;
                            Vector3 yawTo = focus - standPos; yawTo.y = 0;
                            if (faceAway) yawTo = -yawTo;
                            bestRot = yawTo.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(yawTo.normalized, Vector3.up) : Quaternion.identity;
                            bestIdx = i;
                        }
                    }

                    if (bestIdx >= 0)
                    {
                        position = bestPos; rotation = bestRot;
                        chosenDistance = tryDistance; chosenIndex = bestIdx; chosenScore = bestScore;
                        return true;
                    }
                }

                diagnostic = $"no clear stand position around focus {focus} after ring search at {baseDistance:F2}m / {baseDistance + TT_DistanceWiden:F2}m — add an '_SP' marker, move an obstacle, or trim the `near` list";
                return false;
            }
            finally
            {
                if (tempColliders != null) RemoveTempCollidersLocal(tempColliders);
            }
        }

        private static List<MeshCollider> AddTempCollidersLocal()
        {
            var added = new List<MeshCollider>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;
                added.Add(mf.gameObject.AddComponent<MeshCollider>());
            }
            Physics.SyncTransforms();
            return added;
        }

        private static void RemoveTempCollidersLocal(List<MeshCollider> colliders)
        {
            foreach (var mc in colliders) if (mc != null) UnityEngine.Object.DestroyImmediate(mc);
        }

        private static bool IsNonBlockingEffectCollider(Collider col)
        {
            if (col == null) return false;
            int layer = col.gameObject.layer;
            if (layer == 2 || layer == 5) return true;
            var n = col.gameObject.name;
            foreach (var p in TT_NonBlockingNamePatterns)
                if (n.IndexOf(p, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ── lazy container creation (folded-in setup_chapters) ───────────────────

        /// <summary>
        /// Resolve a "/"-separated container path under the scene, creating any missing segments.
        /// Created child containers get a GameObjectQuery (mirrors DoCreateContainer). The root segment
        /// (e.g. "QueryObjects") is created plain if absent. Returns the leaf Transform.
        /// </summary>
        private static Transform EnsurePath(string path)
        {
            if (string.IsNullOrEmpty(path)) path = "QueryObjects";

            var existing = FindByPath(path) ?? FindByName(path);
            if (existing != null) return existing.transform;

            var segments = path.TrimStart('/').Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();
            if (segments.Length == 0) return null;

            var rootGo = FindRoot(segments[0]);
            if (rootGo == null)
            {
                rootGo = new GameObject(segments[0]);
                Undo.RegisterCreatedObjectUndo(rootGo, "Create " + segments[0]);
            }
            var current = rootGo.transform;

            for (int i = 1; i < segments.Length; i++)
            {
                var next = current.Find(segments[i]);
                if (next == null)
                {
                    var go = new GameObject(segments[i]);
                    Undo.RegisterCreatedObjectUndo(go, "Create " + segments[i]);
                    Undo.SetTransformParent(go.transform, current, "Reparent " + segments[i]);
                    EnsureGOQ(go); // container gets a GameObjectQuery, like DoCreateContainer
                    next = go.transform;
                }
                current = next;
            }
            return current;
        }

        // ── helpers (ported from VRseBatchSetup) ─────────────────────────────────

        private static void EnsureGOQ(GameObject go)
        {
            if (go != null && go.GetComponent<GOQ>() == null)
                Undo.AddComponent<GOQ>(go);
        }

        private static void SetEnableOnStartFalse(GameObject go)
        {
            if (go == null) return;
            foreach (var component in go.GetComponents<MonoBehaviour>())
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                var prop = so.FindProperty("enableOnStart");
                if (prop != null && prop.propertyType == SerializedPropertyType.Boolean && prop.boolValue)
                {
                    prop.boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static void ForceActivateMeshHierarchy(GameObject clone)
        {
            if (clone == null) return;
            if (!clone.activeSelf) clone.SetActive(true);

            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer) continue;
                var t = r.transform;
                while (t != null)
                {
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    if (t == clone.transform) break;
                    t = t.parent;
                }
            }
        }

        private static GameObject FindByName(string name, string preferScene = null, string parentHint = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (name.Contains("/"))
            {
                var resolved = FindByPath(name);
                if (resolved != null) return resolved;
                var lastSegment = name.Substring(name.LastIndexOf('/') + 1);
                if (string.IsNullOrEmpty(lastSegment)) return null;
                name = lastSegment;
            }

            var all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
            GameObject preferred = null, fallback = null, ciFallback = null;
            foreach (var go in all)
            {
                bool exact = go.name == name;
                if (!exact)
                {
                    // NL/storyboard names (e.g. "fuse") often don't match the art object's casing ("Fuse").
                    // Exact matches always win; a case-insensitive match is a last resort only.
                    if (ciFallback == null && string.Equals(go.name, name, System.StringComparison.OrdinalIgnoreCase))
                        ciFallback = go;
                    continue;
                }
                if (!string.IsNullOrEmpty(parentHint) && go.transform.parent != null && go.transform.parent.name == parentHint)
                    return go;
                if (!string.IsNullOrEmpty(preferScene) && go.scene.name == preferScene && preferred == null)
                    preferred = go;
                else if (fallback == null)
                    fallback = go;
            }
            return preferred ?? fallback ?? ciFallback;
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segments = path.TrimStart('/').Split('/');
            if (segments.Length == 0) return null;

            Transform current = null;
            var all = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
            foreach (var go in all)
            {
                if (go.transform.parent == null && go.name == segments[0]) { current = go.transform; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < segments.Length; i++)
            {
                var next = current.Find(segments[i]);
                if (next == null) return null;
                current = next;
            }
            return current.gameObject;
        }

        private static GameObject FindRoot(string name)
        {
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                if (go.transform.parent == null && go.name == name) return go;
            return null;
        }

        private static bool HasMesh(GameObject go)
        {
            return go.GetComponentInChildren<MeshFilter>(true) != null
                || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null
                || go.GetComponentInChildren<LODGroup>(true) != null;
        }

        private static bool TryComputeAllowsColliderSize(string grabbableName, out Vector3 size)
        {
            size = new Vector3(0.2f, 0.2f, 0.2f);
            if (string.IsNullOrEmpty(grabbableName)) return false;
            var grabbable = FindByName(grabbableName);
            if (grabbable == null) return false;
            if (!TryGetWorldBoundsLocal(grabbable, out Bounds gb)) return false;
            size = new Vector3(
                Mathf.Clamp(gb.size.x * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax),
                Mathf.Clamp(gb.size.y * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax),
                Mathf.Clamp(gb.size.z * TT_PP_MarginScale, TT_PP_SizeMin, TT_PP_SizeMax));
            return true;
        }

        private static bool TryGetWorldBoundsLocal(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer) continue;
                if (!found) { bounds = r.bounds; found = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!found)
            {
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                {
                    if (!found) { bounds = c.bounds; found = true; }
                    else bounds.Encapsulate(c.bounds);
                }
            }
            return found;
        }

        private static Vector3 ParseVector3(string csv, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(csv)) return fallback;
            var parts = csv.Split(',');
            if (parts.Length != 3) return fallback;
            if (float.TryParse(parts[0].Trim(), out float x) &&
                float.TryParse(parts[1].Trim(), out float y) &&
                float.TryParse(parts[2].Trim(), out float z))
                return new Vector3(x, y, z);
            return fallback;
        }

        private static bool? ParseNullableBool(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return bool.TryParse(s, out var v) ? v : (bool?)null;
        }

        private static void TightenGrabbableCollider(GameObject wrapper)
        {
            if (wrapper == null) return;

            Mesh mesh = null;
            GameObject meshHolder = null;
            var mf = wrapper.GetComponentInChildren<MeshFilter>(true);
            if (mf != null && mf.sharedMesh != null) { mesh = mf.sharedMesh; meshHolder = mf.gameObject; }
            else
            {
                var smr = wrapper.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null && smr.sharedMesh != null) { mesh = smr.sharedMesh; meshHolder = smr.gameObject; }
            }
            if (mesh == null || meshHolder == null)
            {
                Debug.LogWarning($"{TAG} TightenGrabbableCollider: '{wrapper.name}' has no resolvable mesh; leaving colliders untouched.");
                return;
            }

            var b = mesh.bounds;
            var corners = new[]
            {
                new Vector3(b.min.x, b.min.y, b.min.z), new Vector3(b.max.x, b.min.y, b.min.z),
                new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.max.y, b.min.z),
                new Vector3(b.min.x, b.min.y, b.max.z), new Vector3(b.max.x, b.min.y, b.max.z),
                new Vector3(b.min.x, b.max.y, b.max.z), new Vector3(b.max.x, b.max.y, b.max.z),
            };
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (var c in corners)
            {
                var world = meshHolder.transform.TransformPoint(c);
                var local = wrapper.transform.InverseTransformPoint(world);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            var newCenter = (min + max) * 0.5f;
            var newSize = max - min;

            var allColliders = wrapper.GetComponentsInChildren<Collider>(true);
            foreach (var col in allColliders)
            {
                if (col == null || col.gameObject == wrapper) continue;
                Undo.DestroyObjectImmediate(col);
            }

            BoxCollider keepBox = null;
            foreach (var col in wrapper.GetComponents<Collider>())
            {
                if (col is BoxCollider bcExisting && keepBox == null) keepBox = bcExisting;
                else Undo.DestroyObjectImmediate(col);
            }
            if (keepBox == null) keepBox = Undo.AddComponent<BoxCollider>(wrapper);

            Undo.RecordObject(keepBox, "Tighten grabbable collider");
            keepBox.center = newCenter;
            keepBox.size = newSize;
            keepBox.isTrigger = false;
        }

        private static bool EnsureTriggerCollider(GameObject root)
        {
            if (root == null) return false;
            if (root.GetComponentInChildren<Collider>(true) != null) return true;

            Mesh mesh = null;
            GameObject meshHolder = null;
            var mf = root.GetComponentInChildren<MeshFilter>(true);
            if (mf != null && mf.sharedMesh != null) { mesh = mf.sharedMesh; meshHolder = mf.gameObject; }
            else
            {
                var smr = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null && smr.sharedMesh != null) { mesh = smr.sharedMesh; meshHolder = smr.gameObject; }
            }
            if (mesh == null || meshHolder == null)
            {
                Debug.LogWarning($"{TAG} EnsureTriggerCollider: '{root.name}' has no mesh — cannot size collider.");
                return false;
            }

            var b = mesh.bounds;
            var bc = Undo.AddComponent<BoxCollider>(meshHolder);
            bc.center = b.center;
            bc.size = b.size;
            bc.isTrigger = true;
            return true;
        }

        // ── small utilities ──────────────────────────────────────────────────────

        private static string GetStr(Dictionary<string, object> d, string key)
            => d != null && d.ContainsKey(key) && d[key] != null ? d[key].ToString() : "";

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            return t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;
        }

        private static List<string> ComponentNames(GameObject go)
        {
            var list = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                if (c != null) list.Add(c.GetType().Name);
            return list;
        }

        private static Dictionary<string, object> Result(string name, string path, bool ok, bool skipped, string error)
        {
            var r = new Dictionary<string, object> { { "name", name }, { "ok", ok }, { "skipped", skipped } };
            if (path != null) r["path"] = path;
            if (error != null) r["error"] = error;
            return r;
        }
    }
}
