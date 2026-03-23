using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRseBuilder.Backend.Editor.Auth;
using VRseBuilder.Core.Framework;

namespace UnityMCP.Editor
{
    public static class MCPVRseBuilderCommands
    {
        private const string SelectedProjectKey = "VRSEBUILDER_LAST_SELECTED_PROJECT";
        private static readonly HttpClient HttpClient = new HttpClient();

        public static object GetStatus(Dictionary<string, object> args)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            string selectedProject = GetSelectedProjectName();

            return new Dictionary<string, object>
            {
                { "loggedIn", AuthUtility.IsLoggedIn() },
                { "userName", AuthUtility.GetUserName() },
                { "baseUrl", AuthUtility.GetBaseUrl() },
                { "selectedProject", selectedProject },
                { "activeScene", new Dictionary<string, object>
                    {
                        { "name", activeScene.name },
                        { "path", activeScene.path },
                        { "isLoaded", activeScene.isLoaded }
                    }
                },
                { "hasSelectedProjectConfig", !string.IsNullOrEmpty(selectedProject) && TryGetRoomManagerConfig(selectedProject, out _) }
            };
        }

        public static object Login(Dictionary<string, object> args)
        {
            string username = args.ContainsKey("username") ? args["username"]?.ToString()?.Trim() : string.Empty;
            string password = args.ContainsKey("password") ? args["password"]?.ToString() ?? string.Empty : string.Empty;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return new { error = "username and password are required" };

            try
            {
                LoginResponse response = PerformLogin(username, password);
                if (!response.success || response.user == null || string.IsNullOrEmpty(response.user.token))
                    return new { error = string.IsNullOrEmpty(response.message) ? "Login failed" : response.message };

                DateTime? expiry = ParseJwtExpiry(response.user.token);
                if (expiry == null)
                    return new { error = "Login succeeded but token expiry could not be parsed" };

                LicenseManager.SaveToken(response.user.token, expiry.Value.ToString("o"));
                LicenseManager.SaveName(string.IsNullOrEmpty(response.user.name) ? username : response.user.name);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "loggedIn", AuthUtility.IsLoggedIn() },
                    { "userName", AuthUtility.GetUserName() },
                    { "expiresAtUtc", expiry.Value.ToString("o") }
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Login failed: {ex.Message}" };
            }
        }

        public static object ListProjects(Dictionary<string, object> args)
        {
            var accessibleProjects = new List<object>();
            string accessibleProjectsError = null;

            if (AuthUtility.IsLoggedIn())
            {
                try
                {
                    AccessModulesResponse response = FetchAccessModules();
                    if (response != null && response.projects != null)
                    {
                        foreach (AccessProject project in response.projects)
                        {
                            accessibleProjects.Add(new Dictionary<string, object>
                            {
                                { "id", project._id },
                                { "name", project.name },
                                { "description", project.description },
                                { "moduleCount", project.modules != null ? project.modules.Count : 0 }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    accessibleProjectsError = ex.Message;
                }
            }

            var localProjects = new List<object>();
            string studioProjectsRoot = Path.Combine(Application.dataPath, "StudioProjects");
            if (Directory.Exists(studioProjectsRoot))
            {
                foreach (string directory in Directory.GetDirectories(studioProjectsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(directory);
                    bool hasConfig = TryGetRoomManagerConfig(name, out RoomManagerConfig config);
                    localProjects.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "hasRoomManagerConfig", hasConfig },
                        { "hasMenuScene", hasConfig && !string.IsNullOrEmpty(config.MainMenuScene) && File.Exists(config.MainMenuScene) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "loggedIn", AuthUtility.IsLoggedIn() },
                { "userName", AuthUtility.GetUserName() },
                { "selectedProject", GetSelectedProjectName() },
                { "accessibleProjects", accessibleProjects },
                { "accessibleProjectsError", accessibleProjectsError },
                { "localProjects", localProjects }
            };
        }

        public static object SelectProject(Dictionary<string, object> args)
        {
            string requestedName = args.ContainsKey("projectName") ? args["projectName"]?.ToString()?.Trim() : string.Empty;
            string requestedId = args.ContainsKey("projectId") ? args["projectId"]?.ToString()?.Trim() : string.Empty;

            object listResult = ListProjects(args);
            if (!(listResult is Dictionary<string, object> payload))
                return new { error = "Failed to resolve projects" };

            string selectedName = null;

            if (payload.TryGetValue("accessibleProjects", out object accessibleObj) && accessibleObj is List<object> accessibleProjects)
            {
                foreach (object item in accessibleProjects)
                {
                    if (!(item is Dictionary<string, object> project))
                        continue;

                    string projectId = project.ContainsKey("id") ? project["id"]?.ToString() : null;
                    string projectName = project.ContainsKey("name") ? project["name"]?.ToString() : null;

                    if ((!string.IsNullOrEmpty(requestedId) && string.Equals(projectId, requestedId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(requestedName) && string.Equals(projectName, requestedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedName = projectName;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(selectedName) && payload.TryGetValue("localProjects", out object localObj) && localObj is List<object> localProjects)
            {
                foreach (object item in localProjects)
                {
                    if (!(item is Dictionary<string, object> project))
                        continue;

                    string projectName = project.ContainsKey("name") ? project["name"]?.ToString() : null;
                    if (!string.IsNullOrEmpty(requestedName) && string.Equals(projectName, requestedName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedName = projectName;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(selectedName))
                return new { error = "Project not found. Provide a valid projectName or projectId." };

            EditorPrefs.SetString(SelectedProjectKey, selectedName);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "selectedProject", selectedName }
            };
        }

        public static object ListModules(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            AccessModulesResponse remoteResponse = null;
            string remoteError = null;
            if (AuthUtility.IsLoggedIn())
            {
                try
                {
                    remoteResponse = FetchAccessModules();
                }
                catch (Exception ex)
                {
                    remoteError = ex.Message;
                }
            }

            AccessProject remoteProject = remoteResponse?.projects?.FirstOrDefault(project => string.Equals(project.name, projectName, StringComparison.OrdinalIgnoreCase));
            TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig);

            var remoteModules = new List<object>();
            if (remoteProject != null && remoteProject.modules != null)
            {
                foreach (AccessModule module in remoteProject.modules)
                {
                    var experiences = new List<object>();
                    if (module.experiences != null)
                    {
                        foreach (AccessExperience experience in module.experiences)
                        {
                            experiences.Add(new Dictionary<string, object>
                            {
                                { "id", experience._id },
                                { "name", experience.name },
                                { "description", experience.description },
                                { "type", experience.type },
                                { "jsonFileUrl", experience.jsonFileUrl }
                            });
                        }
                    }

                    remoteModules.Add(new Dictionary<string, object>
                    {
                        { "id", module._id },
                        { "name", module.name },
                        { "description", module.description },
                        { "type", module.type },
                        { "experiences", experiences }
                    });
                }
            }

            var configModules = new List<object>();
            if (roomManagerConfig != null && roomManagerConfig.experiences != null)
            {
                foreach (ModuleData module in roomManagerConfig.experiences)
                {
                    var experiences = new List<object>();
                    if (module.ExperienceDataList != null)
                    {
                        foreach (ModuleData.ExperienceData experience in module.ExperienceDataList)
                        {
                            experiences.Add(new Dictionary<string, object>
                            {
                                { "id", experience.ExperienceId },
                                { "name", experience.Name },
                                { "type", experience.Type.ToString() },
                                { "devScene", experience.DevScene },
                                { "artScene", experience.ArtScene },
                                { "storyJsonPath", experience.StoryJsonPath },
                                { "hasDevScene", !string.IsNullOrEmpty(experience.DevScene) && File.Exists(experience.DevScene) },
                                { "hasArtScene", !string.IsNullOrEmpty(experience.ArtScene) && File.Exists(experience.ArtScene) }
                            });
                        }
                    }

                    configModules.Add(new Dictionary<string, object>
                    {
                        { "id", module.ModuleId },
                        { "name", module.GetModuleName() },
                        { "includeInBuild", module.IncludeInBuild },
                        { "experiences", experiences }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "remoteSourceAvailable", remoteProject != null },
                { "remoteSourceError", remoteError },
                { "configSourceAvailable", roomManagerConfig != null },
                { "remoteModules", remoteModules },
                { "configModules", configModules }
            };
        }

        public static object OpenMenuScene(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            if (!TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig))
                return new { error = $"RoomManagerConfig not found for project '{projectName}'." };

            if (string.IsNullOrEmpty(roomManagerConfig.MainMenuScene))
                return new { error = $"Main menu scene is not configured for project '{projectName}'." };

            if (!File.Exists(roomManagerConfig.MainMenuScene))
                return new { error = $"Main menu scene file does not exist: {roomManagerConfig.MainMenuScene}" };

            Scene openedScene = EditorSceneManager.OpenScene(roomManagerConfig.MainMenuScene, OpenSceneMode.Single);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "menuScenePath", roomManagerConfig.MainMenuScene },
                { "openedSceneName", openedScene.name }
            };
        }

        public static object OpenModule(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            if (!TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig))
                return new { error = $"RoomManagerConfig not found for project '{projectName}'." };

            ModuleData module = ResolveModule(roomManagerConfig, args);
            if (module == null)
                return new { error = "Module not found. Provide moduleId or moduleName." };

            ModuleData.ExperienceData experience = ResolveExperience(module, args);
            if (experience == null)
                return new { error = "Experience not found. Provide experienceId, experienceName, or experienceType." };

            if (string.IsNullOrEmpty(experience.DevScene))
                return new { error = $"Experience '{experience.Name}' does not have a dev scene configured." };

            if (!File.Exists(experience.DevScene))
                return new { error = $"Dev scene file does not exist: {experience.DevScene}" };

            Scene devScene = EditorSceneManager.OpenScene(experience.DevScene, OpenSceneMode.Single);
            bool artSceneLoaded = false;
            if (!string.IsNullOrEmpty(experience.ArtScene) && File.Exists(experience.ArtScene))
            {
                EditorSceneManager.OpenScene(experience.ArtScene, OpenSceneMode.Additive);
                artSceneLoaded = true;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "module", new Dictionary<string, object> { { "id", module.ModuleId }, { "name", module.GetModuleName() } } },
                { "experience", new Dictionary<string, object> { { "id", experience.ExperienceId }, { "name", experience.Name }, { "type", experience.Type.ToString() } } },
                { "devScenePath", experience.DevScene },
                { "openedSceneName", devScene.name },
                { "artScenePath", experience.ArtScene },
                { "artSceneLoaded", artSceneLoaded }
            };
        }

        public static object OpenRoomManagerConfig(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            if (!TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig))
                return new { error = $"RoomManagerConfig not found for project '{projectName}'." };

            EditorUtility.OpenPropertyEditor(roomManagerConfig);
            Selection.activeObject = roomManagerConfig;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "assetName", roomManagerConfig.name }
            };
        }

        private static string ResolveProjectName(Dictionary<string, object> args)
        {
            string requestedProject = args.ContainsKey("projectName") ? args["projectName"]?.ToString()?.Trim() : string.Empty;
            return !string.IsNullOrEmpty(requestedProject) ? requestedProject : GetSelectedProjectName();
        }

        private static string GetSelectedProjectName()
        {
            return EditorPrefs.GetString(SelectedProjectKey, string.Empty);
        }

        private static bool TryGetRoomManagerConfig(string projectName, out RoomManagerConfig roomManagerConfig)
        {
            roomManagerConfig = null;
            if (string.IsNullOrEmpty(projectName))
                return false;

            string assetPath = $"Assets/StudioProjects/{projectName}/ProjectSettings/RoomManagerConfig_{projectName}.asset";
            roomManagerConfig = AssetDatabase.LoadAssetAtPath<RoomManagerConfig>(assetPath);
            return roomManagerConfig != null;
        }

        private static ModuleData ResolveModule(RoomManagerConfig roomManagerConfig, Dictionary<string, object> args)
        {
            if (roomManagerConfig?.experiences == null || roomManagerConfig.experiences.Length == 0)
                return null;

            string moduleId = args.ContainsKey("moduleId") ? args["moduleId"]?.ToString()?.Trim() : string.Empty;
            string moduleName = args.ContainsKey("moduleName") ? args["moduleName"]?.ToString()?.Trim() : string.Empty;

            if (!string.IsNullOrEmpty(moduleId))
            {
                ModuleData idMatch = roomManagerConfig.experiences.FirstOrDefault(module => string.Equals(module.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null)
                    return idMatch;
            }

            if (!string.IsNullOrEmpty(moduleName))
                return roomManagerConfig.experiences.FirstOrDefault(module => string.Equals(module.GetModuleName(), moduleName, StringComparison.OrdinalIgnoreCase));

            return null;
        }

        private static ModuleData.ExperienceData ResolveExperience(ModuleData module, Dictionary<string, object> args)
        {
            if (module?.ExperienceDataList == null || module.ExperienceDataList.Count == 0)
                return null;

            string experienceId = args.ContainsKey("experienceId") ? args["experienceId"]?.ToString()?.Trim() : string.Empty;
            string experienceName = args.ContainsKey("experienceName") ? args["experienceName"]?.ToString()?.Trim() : string.Empty;
            string experienceType = args.ContainsKey("experienceType") ? args["experienceType"]?.ToString()?.Trim() : string.Empty;

            if (!string.IsNullOrEmpty(experienceId))
            {
                ModuleData.ExperienceData idMatch = module.ExperienceDataList.FirstOrDefault(experience => string.Equals(experience.ExperienceId, experienceId, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null)
                    return idMatch;
            }

            if (!string.IsNullOrEmpty(experienceName))
            {
                ModuleData.ExperienceData nameMatch = module.ExperienceDataList.FirstOrDefault(experience => string.Equals(experience.Name, experienceName, StringComparison.OrdinalIgnoreCase));
                if (nameMatch != null)
                    return nameMatch;
            }

            if (!string.IsNullOrEmpty(experienceType) && Enum.TryParse(experienceType, true, out ModuleData.ExperienceType parsedType))
            {
                ModuleData.ExperienceData typeMatch = module.ExperienceDataList.FirstOrDefault(experience => experience.Type == parsedType);
                if (typeMatch != null)
                    return typeMatch;
            }

            ModuleData.ExperienceData trainingMatch = module.ExperienceDataList.FirstOrDefault(experience => experience.Type == ModuleData.ExperienceType.Training);
            return trainingMatch ?? module.ExperienceDataList.FirstOrDefault();
        }

        private static AccessModulesResponse FetchAccessModules()
        {
            string token = AuthUtility.GetAccessToken();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Not logged in.");

            string url = AuthUtility.GetBaseUrl().TrimEnd('/') + AuthUtility.ApiEndpoints.AccessModules;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                HttpResponseMessage response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Access-modules request failed ({(int)response.StatusCode}): {payload}");

                AccessModulesResponse parsed = JsonUtility.FromJson<AccessModulesResponse>(payload);
                if (parsed == null)
                    throw new InvalidOperationException("Access-modules response could not be parsed.");

                return parsed;
            }
        }

        private static LoginResponse PerformLogin(string username, string password)
        {
            string url = AuthUtility.GetBaseUrl().TrimEnd('/') + AuthUtility.ApiEndpoints.Login;
            string body = JsonUtility.ToJson(new LoginRequest { username = username, password = password });

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                HttpResponseMessage response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                string payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                LoginResponse parsed = JsonUtility.FromJson<LoginResponse>(payload) ?? new LoginResponse();
                if (!response.IsSuccessStatusCode && string.IsNullOrEmpty(parsed.message))
                    parsed.message = $"HTTP {(int)response.StatusCode}: {payload}";

                return parsed;
            }
        }

        private static DateTime? ParseJwtExpiry(string jwt)
        {
            if (string.IsNullOrEmpty(jwt))
                return null;

            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length != 3)
                    return null;

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                JwtPayload parsed = JsonUtility.FromJson<JwtPayload>(json);
                return parsed != null && parsed.exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(parsed.exp).UtcDateTime : (DateTime?)null;
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        private class JwtPayload
        {
            public long exp;
        }

        [Serializable]
        private class LoginRequest
        {
            public string username;
            public string password;
        }

        [Serializable]
        private class LoginResponse
        {
            public bool success;
            public string message;
            public LoginUser user;
        }

        [Serializable]
        private class LoginUser
        {
            public string name;
            public string token;
        }

        [Serializable]
        private class AccessModulesResponse
        {
            public bool success;
            public List<AccessProject> projects;
        }

        [Serializable]
        private class AccessProject
        {
            public string _id;
            public string name;
            public string description;
            public List<AccessModule> modules;
        }

        [Serializable]
        private class AccessModule
        {
            public string _id;
            public string name;
            public string description;
            public string type;
            public List<AccessExperience> experiences;
        }

        [Serializable]
        private class AccessExperience
        {
            public string _id;
            public string name;
            public string description;
            public string type;
            public string jsonFileUrl;
        }
    }
}