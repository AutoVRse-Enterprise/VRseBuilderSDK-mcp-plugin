using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRseBuilder.Platform.MetaXR.Editor;

namespace UnityMCP.Editor
{
    public static class MCPInteractableCommands
    {
        public static object InteractableConvert(Dictionary<string, object> args)
        {
            string methodName = args.ContainsKey("methodName") ? args["methodName"]?.ToString() : string.Empty;
            string targetHint = args.ContainsKey("targetHint") ? args["targetHint"]?.ToString() : string.Empty;
            string objectPath = args.ContainsKey("objectPath") ? args["objectPath"]?.ToString() : string.Empty;
            string newName = args.ContainsKey("newName") ? args["newName"]?.ToString() : string.Empty;

            if (string.IsNullOrEmpty(methodName))
            {
                return new { success = false, error = "methodName is required" };
            }

            GameObject target = GetTargetGameObject(targetHint, objectPath);
            if (target == null)
            {
                return new { success = false, error = $"Could not find target object. Hint: '{targetHint}', Path: '{objectPath}'" };
            }

            try
            {
                bool result = false;
                switch (methodName)
                {
                    case "ConvertToVRseObject":
                        result = MetaXRInteractableConverter.ConvertToNetworkMetaXRBaseItem(target);
                        break;
                    case "ConvertToTouchObject":
                        MetaXRInteractableConverter.ConvertToTouchable(target);
                        result = true; // Returns void
                        break;
                    case "ConvertToGrabbable":
                        result = MetaXRInteractableConverter.ConvertToNetworkMetaXRGrabbable(target);
                        break;
                    case "ConvertToPlacePoint":
                        result = MetaXRInteractableConverter.ConvertToNetworkMetaXRPlacePoint(target);
                        break;
                    case "CreatePlacePoint":
                        MetaXRInteractableConverter.CreateNetworkMetaXRPlacePoint(target, newName);
                        result = true; // Returns void
                        break;
                    case "ConvertToRayInteractable":
                        MetaXRInteractableConverter.ConvertToRayPointerInteractable(target);
                        result = true; // Returns void
                        break;
                    default:
                        return new { success = false, error = $"Unknown conversion method: {methodName}" };
                }

                if (!result)
                {
                    return new { success = false, error = $"Conversion {methodName} failed on backend." };
                }

                // If selected object changes during conversion, use active selection as resolved.
                GameObject resolved = Selection.activeGameObject != null ? Selection.activeGameObject : target;
                string newPath = GetHierarchyPath(resolved.transform);

                return new
                {
                    success = true,
                    resolvedObject = newPath,
                    message = $"Successfully executed {methodName}"
                };
            }
            catch (Exception ex)
            {
                return new { success = false, error = $"Error during conversion: {ex.Message}" };
            }
        }

        private static GameObject GetTargetGameObject(string hint, string path)
        {
            // If explicit path is provided, try that first
            if (!string.IsNullOrEmpty(path))
            {
                var target = GameObject.Find(path);
                if (target != null) return target;
            }

            // If a hint is provided, search for it
            if (!string.IsNullOrEmpty(hint))
            {
                // Find all game objects and try to match name loosely
                var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                foreach (var obj in allObjects)
                {
                    if (obj.name.Equals(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        return obj;
                    }
                }
                
                // Try contains
                foreach (var obj in allObjects)
                {
                    if (obj.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return obj;
                    }
                }
            }

            // Fallback to current selection
            return Selection.activeGameObject;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            if (transform.parent == null) return transform.name;
            return GetHierarchyPath(transform.parent) + "/" + transform.name;
        }
    }
}
