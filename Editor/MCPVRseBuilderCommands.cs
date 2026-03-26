using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRseBuilder.Backend.Editor.Auth;
using VRseBuilder.Core.Framework;
using VRseBuilder.Tools.Editor;
using VRseBuilder.Tools.Editor.BuildTool;

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

        public static object GetSelectedProject(Dictionary<string, object> args)
        {
            string projectName = GetSelectedProjectName();
            RoomManagerConfig config = null;
            bool hasConfig = !string.IsNullOrEmpty(projectName) && TryGetRoomManagerConfig(projectName, out config);
            bool hasMenuScene = hasConfig && !string.IsNullOrEmpty(config.MainMenuScene) && File.Exists(config.MainMenuScene);
            return new Dictionary<string, object>
            {
                { "selectedProject", projectName },
                { "hasRoomManagerConfig", hasConfig },
                { "hasMenuScene", hasMenuScene }
            };
        }

        public static object GetProjectConfig(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            if (!TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig))
                return new { error = $"RoomManagerConfig not found for project '{projectName}'." };

            return new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "projectId", roomManagerConfig.ProjectID },
                { "mainMenuScene", roomManagerConfig.MainMenuScene },
                { "liveLinkEnabled", roomManagerConfig.LiveLinkEnabled },
                { "useCustomAvatars", roomManagerConfig.UseCustomAvatars },
                { "stepNavigationDataEnabled", roomManagerConfig.StepNavigationDataEnabled },
                { "moduleCount", roomManagerConfig.experiences != null ? roomManagerConfig.experiences.Length : 0 },
                { "photonAppSettings", roomManagerConfig.photonAppSettings },
                { "loginAccessSettings", roomManagerConfig.loginAccessSettings },
                { "buildSettings", roomManagerConfig.buildSettings },
                { "ttsSettings", roomManagerConfig.ttsSettings }
            };
        }

        public static object EnsureProjectSettings(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            bool created = VRseProjectWindowProjectSettingsController.EnsureProjectSettingsExist(projectName);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "newSettingsCreated", created }
            };
        }

        public static object ApplyProjectSettings(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            return VRseProjectConfigAutoApply.AutoApplyAllSettingsOnProjectChange(projectName);
        }

        public static object OpenStudioProjectWindow(Dictionary<string, object> args)
        {
            VRseProjectWindowUI.ShowWindow();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", "VRse Studio Projects" }
            };
        }

        public static object OpenProjectConfigWindow(Dictionary<string, object> args)
        {
            VRseProjectConfigWindow.ShowWindow();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", "VRse Project Config" }
            };
        }

        public static object OpenBuildToolWindow(Dictionary<string, object> args)
        {
            VRseBuildToolWindow.ShowWindow();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", "VRse Build Tool" }
            };
        }

        public static object CreateExperience(Dictionary<string, object> args)
        {
            string projectName = ResolveProjectName(args);
            if (string.IsNullOrEmpty(projectName))
                return new { error = "No project selected. Use vrse/select-project first or pass projectName." };

            string moduleName = GetStringArg(args, "moduleName");
            string experienceName = GetStringArg(args, "experienceName");
            string moduleId = GetStringArg(args, "moduleId");
            string experienceId = GetStringArg(args, "experienceId");
            string jsonFileUrl = GetStringArg(args, "jsonFileUrl");

            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(experienceName))
                return new { error = "moduleName and experienceName are required." };

            if (string.IsNullOrEmpty(jsonFileUrl))
                jsonFileUrl = ResolveExperienceJsonFileUrl(projectName, args);

            if (string.IsNullOrEmpty(jsonFileUrl))
                return new { error = "jsonFileUrl is required, or the experience must be resolvable from the logged-in backend project." };

            if (!Uri.TryCreate(jsonFileUrl, UriKind.Absolute, out Uri jsonUri) ||
                (jsonUri.Scheme != Uri.UriSchemeHttp && jsonUri.Scheme != Uri.UriSchemeHttps))
            {
                return new { error = "jsonFileUrl must be an absolute http or https URL." };
            }

            ModuleData.ExperienceType experienceType = ResolveExperienceType(args);
            var controller = new VRseProjectWindowController();

            try
            {
                controller.CreateExperienceDevScene(projectName, moduleName, experienceName, jsonFileUrl, moduleId, experienceId, experienceType);
            }
            catch (Exception ex)
            {
                return new { error = $"Experience creation failed: {ex.Message}" };
            }

            AssetDatabase.Refresh();

            TryGetRoomManagerConfig(projectName, out RoomManagerConfig roomManagerConfig);
            ModuleData module = roomManagerConfig != null ? ResolveModule(roomManagerConfig, args) : null;
            ModuleData.ExperienceData experience = module != null ? ResolveExperience(module, args) : null;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "jsonFileUrl", jsonFileUrl },
                { "module", BuildModulePayload(module, moduleId, moduleName) },
                { "experience", BuildExperiencePayload(experience, experienceId, experienceName, experienceType) },
                { "creationStatus", BuildExperienceCreationStatus(projectName, module, experience) },
                { "warning", experience == null ? "The creation flow ran, but the experience could not be resolved from RoomManagerConfig. Provide moduleId and experienceId for reliable config tracking." : null }
            };
        }

        public static object GetExperienceCreationStatus(Dictionary<string, object> args)
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

            return new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "module", BuildModulePayload(module, module.ModuleId, module.GetModuleName()) },
                { "experience", BuildExperiencePayload(experience, experience.ExperienceId, experience.Name, experience.Type) },
                { "creationStatus", BuildExperienceCreationStatus(projectName, module, experience) }
            };
        }

        public static object OpenArtScene(Dictionary<string, object> args)
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

            if (string.IsNullOrEmpty(experience.ArtScene))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Experience '{experience.Name}' does not have an art scene configured." },
                    { "projectName", projectName },
                    { "module", BuildModulePayload(module, module.ModuleId, module.GetModuleName()) },
                    { "experience", BuildExperiencePayload(experience, experience.ExperienceId, experience.Name, experience.Type) },
                    { "creationStatus", BuildExperienceCreationStatus(projectName, module, experience) }
                };
            }

            if (!File.Exists(experience.ArtScene))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Art scene file does not exist: {experience.ArtScene}" },
                    { "projectName", projectName },
                    { "module", BuildModulePayload(module, module.ModuleId, module.GetModuleName()) },
                    { "experience", BuildExperiencePayload(experience, experience.ExperienceId, experience.Name, experience.Type) },
                    { "creationStatus", BuildExperienceCreationStatus(projectName, module, experience) }
                };
            }

            Scene artScene = EditorSceneManager.OpenScene(experience.ArtScene, OpenSceneMode.Additive);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectName", projectName },
                { "module", BuildModulePayload(module, module.ModuleId, module.GetModuleName()) },
                { "experience", BuildExperiencePayload(experience, experience.ExperienceId, experience.Name, experience.Type) },
                { "artScenePath", experience.ArtScene },
                { "openedSceneName", artScene.name }
            };
        }

        public static object StoryAddTriggerSet(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            if (section != "onWrong" && section != "onRight")
                return new { error = "section must be 'onWrong' or 'onRight'." };

            var newTriggerActionSet = new TriggerActionSet
            {
                trigger = CreateDefaultNode(isAction: false),
                actions = new Node[0]
            };

            int triggerSetIndex;
            if (section == "onWrong")
            {
                if (moment.onWrong == null)
                    moment.onWrong = new TriggerActionSet[0];

                Array.Resize(ref moment.onWrong, moment.onWrong.Length + 1);
                triggerSetIndex = moment.onWrong.Length - 1;
                moment.onWrong[triggerSetIndex] = newTriggerActionSet;
            }
            else
            {
                if (moment.onRight == null)
                {
                    moment.onRight = new TriggerActionSetsWithMode
                    {
                        mode = string.IsNullOrEmpty(GetStringArg(args, "mode")) ? _Constants.IN_ORDER : GetStringArg(args, "mode"),
                        triggerActionSets = new TriggerActionSet[0]
                    };
                }

                if (moment.onRight.triggerActionSets == null)
                    moment.onRight.triggerActionSets = new TriggerActionSet[0];

                if (string.IsNullOrEmpty(moment.onRight.mode))
                    moment.onRight.mode = string.IsNullOrEmpty(GetStringArg(args, "mode")) ? _Constants.IN_ORDER : GetStringArg(args, "mode");

                Array.Resize(ref moment.onRight.triggerActionSets, moment.onRight.triggerActionSets.Length + 1);
                triggerSetIndex = moment.onRight.triggerActionSets.Length - 1;
                moment.onRight.triggerActionSets[triggerSetIndex] = newTriggerActionSet;
            }

            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "trigger", SerializeNode(newTriggerActionSet.trigger) },
                { "actionCount", 0 }
            };
        }

        public static object StoryAddAction(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            Node newNode = CreateDefaultNode(isAction: true);
            int nodeIndex;

            if (IsSimpleActionSection(section))
            {
                ActionSet actionSet = GetOrCreateActionSet(moment, section);
                if (actionSet.actions == null)
                    actionSet.actions = new Node[0];

                Array.Resize(ref actionSet.actions, actionSet.actions.Length + 1);
                nodeIndex = actionSet.actions.Length - 1;
                actionSet.actions[nodeIndex] = newNode;
            }
            else if (section == "onWrong" || section == "onRight")
            {
                int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);
                if (!TryGetTriggerActionSet(moment, section, triggerSetIndex, out TriggerActionSet triggerActionSet, out string triggerSetError))
                    return new { error = triggerSetError };

                if (triggerActionSet.actions == null)
                    triggerActionSet.actions = new Node[0];

                Array.Resize(ref triggerActionSet.actions, triggerActionSet.actions.Length + 1);
                nodeIndex = triggerActionSet.actions.Length - 1;
                triggerActionSet.actions[nodeIndex] = newNode;

                MarkStoryChanged(storyCreator);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "chapterIndex", chapterIndex },
                    { "momentIndex", momentIndex },
                    { "section", section },
                    { "triggerSetIndex", triggerSetIndex },
                    { "nodeIndex", nodeIndex },
                    { "node", SerializeNode(newNode) }
                };
            }
            else
            {
                return new { error = "section must be one of onAwake, onStart, onFirstWarning, onLastWarning, onEnd, onWrong, or onRight." };
            }

            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "nodeIndex", nodeIndex },
                { "node", SerializeNode(newNode) }
            };
        }

        public static object StoryUpdateNode(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            string nodeKind = GetStringArg(args, "nodeKind");
            int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);
            int nodeIndex = GetIntArg(args, "nodeIndex", -1);

            if (!TryResolveNode(moment, section, nodeKind, triggerSetIndex, nodeIndex, out Node node, out string nodeError))
                return new { error = nodeError };

            if (args.ContainsKey("name"))
                node.Name = args["name"]?.ToString() ?? string.Empty;

            if (args.ContainsKey("option"))
                node.Option = args["option"]?.ToString() ?? string.Empty;

            if (args.ContainsKey("query"))
                node.Query = args["query"]?.ToString() ?? string.Empty;

            if (args.ContainsKey("data"))
                node.Data = args["data"]?.ToString() ?? string.Empty;

            if (args.ContainsKey("type"))
                node.Type = ParseNodeTypeArg(args["type"], node.Type);

            if (GetBoolArg(args, "clearTargetGameObject", false))
            {
                node.TargetGameObject = null;
                node.Query = string.Empty;
            }

            string targetSpecifier = GetStringArg(args, "targetGameObjectPath");
            if (string.IsNullOrEmpty(targetSpecifier))
                targetSpecifier = GetStringArg(args, "targetGameObjectName");

            if (!string.IsNullOrEmpty(targetSpecifier))
            {
                GameObject targetGameObject = GameObject.Find(targetSpecifier);
                if (targetGameObject == null)
                    return new { error = $"Target GameObject not found in the open scene(s): {targetSpecifier}" };

                SetNodeTargetGameObject(node, targetGameObject);
            }

            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "nodeIndex", nodeIndex },
                { "nodeKind", string.IsNullOrEmpty(nodeKind) ? "action" : nodeKind },
                { "node", SerializeNode(node) }
            };
        }

        public static object StorySave(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            try
            {
                storyCreator.SaveToJSONFile();
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to save story JSON: {ex.Message}" };
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "storyCreator", storyCreator.gameObject.name },
                { "fileName", storyCreator._fileName },
                { "filePath", storyCreator._FilePath },
                { "isSavedToFile", storyCreator.GetIsStorySavedToFileCached() }
            };
        }

        public static object StoryValidate(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            if (storyCreator._story == null)
                return new { error = "The active StoryCreator does not have a story loaded." };

            try
            {
                if (GetBoolArg(args, "autoAssignDefaultTargets", true))
                {
                    var helper = new DataVrseHelper();
                    helper.AutoAssignDefaultNodeTargetObjects(storyCreator._story);
                }

                Type validatorType = typeof(StoryReportEditor).GetNestedType("StoryFlowValidator", BindingFlags.NonPublic);
                if (validatorType == null)
                    return new { error = "Could not locate the StoryFlowValidator implementation." };

                object validatorInstance = Activator.CreateInstance(validatorType, true);
                MethodInfo validateMethod = validatorType.GetMethod("Validate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (validateMethod == null)
                    return new { error = "Could not locate StoryFlowValidator.Validate." };

                var validationResult = validateMethod.Invoke(validatorInstance, new object[] { storyCreator._story })
                    as Dictionary<Moment, Dictionary<string, List<string>>>;

                if (validationResult == null)
                    validationResult = new Dictionary<Moment, Dictionary<string, List<string>>>();

                List<object> issues = BuildValidationIssues(storyCreator._story, validationResult);
                int totalIssueCount = issues
                    .OfType<Dictionary<string, object>>()
                    .Sum(entry => entry.ContainsKey("issueCount") ? Convert.ToInt32(entry["issueCount"]) : 0);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "storyCreator", storyCreator.gameObject.name },
                    { "isValid", totalIssueCount == 0 },
                    { "totalMomentsWithIssues", issues.Count },
                    { "totalIssueCount", totalIssueCount },
                    { "issues", issues }
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Story validation failed: {ex.Message}" };
            }
        }

        public static object StoryRemoveNodeByName(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);
            string nodeName = GetStringArg(args, "nodeName");

            if (string.IsNullOrEmpty(nodeName))
                return new { error = "nodeName is required." };

            if (!TryGetActionNodeArray(moment, section, triggerSetIndex, out Node[] nodeArray, out string arrayError))
                return new { error = arrayError };

            int originalCount = nodeArray.Length;
            Node[] filtered = nodeArray.Where(node => node == null || !string.Equals(node.Name, nodeName, StringComparison.OrdinalIgnoreCase)).ToArray();

            SetActionNodeArray(moment, section, triggerSetIndex, filtered);
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "nodeName", nodeName },
                { "removedCount", originalCount - filtered.Length }
            };
        }

        public static object ApplyMomentWeightage(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            MomentDefaults defaults = LoadMomentDefaults(moment.defaults);

            bool weightageProvided = args.ContainsKey("weightage");
            bool wrongReductionProvided = args.ContainsKey("wrongReduction");
            defaults.weightage = weightageProvided ? GetFloatArg(args, "weightage", defaults.weightage) : defaults.weightage;
            defaults.wrongReduction = wrongReductionProvided ? GetFloatArg(args, "wrongReduction", defaults.wrongReduction) : defaults.wrongReduction;

            moment.defaults = JsonUtility.ToJson(defaults);
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "weightage", defaults.weightage },
                { "wrongReduction", defaults.wrongReduction }
            };
        }

        public static object StoryHasPendingVO(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null || storyCreator._story == null)
                return new { error = "No StoryCreator with a loaded story is available." };

            bool hasPendingVOs = GenerateVOWindow.HasPendingVOsToGenerate(storyCreator._story);
            return new Dictionary<string, object>
            {
                { "hasPendingVOs", hasPendingVOs },
                { "storyCreator", storyCreator.gameObject.name }
            };
        }

        public static object StoryRead(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            if (storyCreator._story == null)
                return new { error = "The active StoryCreator does not have a story loaded." };

            Story story = storyCreator._story;
            int filterChapter = GetIntArg(args, "chapterIndex", -1);
            int filterMoment = GetIntArg(args, "momentIndex", -1);
            string filterSection = NormalizeSectionName(GetStringArg(args, "section"));
            int maxNodes = GetIntArg(args, "maxNodes", 500);

            int nodeCount = 0;
            var chapters = new List<object>();

            for (int ci = 0; ci < (story.chapters?.Length ?? 0); ci++)
            {
                if (filterChapter >= 0 && ci != filterChapter) continue;
                Chapter chapter = story.chapters[ci];
                if (chapter == null) continue;

                var moments = new List<object>();
                for (int mi = 0; mi < (chapter.moments?.Length ?? 0); mi++)
                {
                    if (filterMoment >= 0 && mi != filterMoment) continue;
                    Moment moment = chapter.moments[mi];
                    if (moment == null) continue;

                    var momentData = new Dictionary<string, object>
                    {
                        { "index", mi },
                        { "name", moment.name },
                        { "defaults", moment.defaults }
                    };

                    var sections = new Dictionary<string, object>();
                    string[] sectionNames = { "onAwake", "onStart", "onFirstWarning", "onLastWarning", "onEnd" };
                    foreach (string sectionName in sectionNames)
                    {
                        if (!string.IsNullOrEmpty(filterSection) && filterSection != sectionName) continue;
                        ActionSet actionSet = GetOrCreateActionSet(moment, sectionName);
                        if (actionSet?.actions != null && actionSet.actions.Length > 0)
                        {
                            var nodes = new List<object>();
                            foreach (Node node in actionSet.actions)
                            {
                                if (nodeCount >= maxNodes) break;
                                nodes.Add(SerializeNode(node));
                                nodeCount++;
                            }
                            sections[sectionName] = nodes;
                        }
                    }

                    // onWrong
                    if (string.IsNullOrEmpty(filterSection) || filterSection == "onWrong")
                    {
                        if (moment.onWrong != null && moment.onWrong.Length > 0)
                        {
                            var wrongSets = new List<object>();
                            for (int tsi = 0; tsi < moment.onWrong.Length; tsi++)
                            {
                                TriggerActionSet tas = moment.onWrong[tsi];
                                if (tas == null) continue;
                                wrongSets.Add(SerializeTriggerActionSet(tas, tsi, ref nodeCount, maxNodes));
                            }
                            sections["onWrong"] = wrongSets;
                        }
                    }

                    // onRight
                    if (string.IsNullOrEmpty(filterSection) || filterSection == "onRight")
                    {
                        if (moment.onRight?.triggerActionSets != null && moment.onRight.triggerActionSets.Length > 0)
                        {
                            var rightSets = new List<object>();
                            for (int tsi = 0; tsi < moment.onRight.triggerActionSets.Length; tsi++)
                            {
                                TriggerActionSet tas = moment.onRight.triggerActionSets[tsi];
                                if (tas == null) continue;
                                rightSets.Add(SerializeTriggerActionSet(tas, tsi, ref nodeCount, maxNodes));
                            }
                            var onRightData = new Dictionary<string, object>
                            {
                                { "mode", moment.onRight.mode },
                                { "triggerActionSets", rightSets }
                            };
                            sections["onRight"] = onRightData;
                        }
                    }

                    momentData["sections"] = sections;
                    moments.Add(momentData);
                }

                chapters.Add(new Dictionary<string, object>
                {
                    { "index", ci },
                    { "name", chapter.name },
                    { "momentCount", chapter.moments?.Length ?? 0 },
                    { "moments", moments }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "storyCreator", storyCreator.gameObject.name },
                { "fileName", storyCreator._fileName },
                { "isSavedToFile", storyCreator.GetIsStorySavedToFileCached() },
                { "totalChapters", story.chapters?.Length ?? 0 },
                { "totalNodesReturned", nodeCount },
                { "maxNodes", maxNodes },
                { "chapters", chapters }
            };
        }

        private static Dictionary<string, object> SerializeTriggerActionSet(TriggerActionSet tas, int index, ref int nodeCount, int maxNodes)
        {
            var result = new Dictionary<string, object>
            {
                { "index", index },
                { "trigger", nodeCount < maxNodes ? SerializeNode(tas.trigger) : null }
            };
            if (tas.trigger != null) nodeCount++;

            var actions = new List<object>();
            if (tas.actions != null)
            {
                foreach (Node action in tas.actions)
                {
                    if (nodeCount >= maxNodes) break;
                    actions.Add(SerializeNode(action));
                    nodeCount++;
                }
            }
            result["actions"] = actions;
            return result;
        }

        public static object StoryListNodeTemplates(Dictionary<string, object> args)
        {
            NodeTemplatesData nodeTemplatesData = LoadNodeTemplatesData();
            if (nodeTemplatesData == null)
                return new { error = "NodeTemplatesData ScriptableObject not found in the project." };

            var actionTemplates = new List<object>();
            foreach (NodeTemplatesData.NodeData template in nodeTemplatesData.actionTemplates)
            {
                actionTemplates.Add(SerializeNodeTemplate(template));
            }

            var triggerTemplates = new List<object>();
            foreach (NodeTemplatesData.NodeData template in nodeTemplatesData.triggerTemplates)
            {
                triggerTemplates.Add(SerializeNodeTemplate(template));
            }

            var defaultParams = new List<object>();
            foreach (NodeTemplatesData.ParameterData param in nodeTemplatesData.DefaultParameters)
            {
                defaultParams.Add(new Dictionary<string, object>
                {
                    { "key", param.Key },
                    { "type", param.Type },
                    { "defaultValue", param.DefaultValue },
                    { "description", param.Description }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "actionTemplateCount", actionTemplates.Count },
                { "triggerTemplateCount", triggerTemplates.Count },
                { "actionTemplates", actionTemplates },
                { "triggerTemplates", triggerTemplates },
                { "defaultParameters", defaultParams }
            };
        }

        private static Dictionary<string, object> SerializeNodeTemplate(NodeTemplatesData.NodeData template)
        {
            var options = new List<object>();
            if (template.Options != null)
            {
                foreach (NodeTemplatesData.OptionData option in template.Options)
                {
                    var parameters = new List<object>();
                    if (option.Parameters != null)
                    {
                        foreach (NodeTemplatesData.ParameterData param in option.Parameters)
                        {
                            parameters.Add(new Dictionary<string, object>
                            {
                                { "key", param.Key },
                                { "type", param.Type },
                                { "defaultValue", param.DefaultValue },
                                { "description", param.Description }
                            });
                        }
                    }

                    var nestedParams = new List<object>();
                    if (option.NestedParameters != null)
                    {
                        foreach (NodeTemplatesData.NestedParameterData nested in option.NestedParameters)
                        {
                            var nestedParamList = new List<object>();
                            if (nested.Parameters != null)
                            {
                                foreach (NodeTemplatesData.ParameterData np in nested.Parameters)
                                {
                                    nestedParamList.Add(new Dictionary<string, object>
                                    {
                                        { "key", np.Key },
                                        { "type", np.Type },
                                        { "defaultValue", np.DefaultValue },
                                        { "description", np.Description }
                                    });
                                }
                            }
                            nestedParams.Add(new Dictionary<string, object>
                            {
                                { "key", nested.Key },
                                { "description", nested.Description },
                                { "parameters", nestedParamList }
                            });
                        }
                    }

                    options.Add(new Dictionary<string, object>
                    {
                        { "name", option.Name },
                        { "description", option.Description },
                        { "parameters", parameters },
                        { "nestedParameters", nestedParams }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "name", template.Name },
                { "backendId", template.BackendId },
                { "type", template.Type },
                { "description", template.Description },
                { "options", options }
            };
        }

        public static object QueryObjectsList(Dictionary<string, object> args)
        {
            QueryObjectsIdManager queryManager = UnityEngine.Object.FindObjectOfType<QueryObjectsIdManager>();
            if (queryManager == null)
                return new { error = "No QueryObjectsIdManager found in loaded scenes." };

            List<GameObjectQuery> queries = queryManager.GetAllGameObjectQueries();
            if (queries == null)
                queries = new List<GameObjectQuery>();

            var result = new List<object>();
            foreach (GameObjectQuery goQuery in queries)
            {
                if (goQuery == null || goQuery.gameObject == null) continue;

                var components = new List<string>();
                foreach (Component comp in goQuery.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    System.Type type = comp.GetType();
                    string typeName = type.Name;
                    if (typeName.Contains("Grabbable")) components.Add("Grabbable");
                    else if (typeName.Contains("PlacePoint")) components.Add("PlacePoint");
                    else if (typeName.Contains("BaseItem")) components.Add("BaseItem");
                }

                string path = GetGameObjectPath(goQuery.gameObject);

                result.Add(new Dictionary<string, object>
                {
                    { "queryName", goQuery.Name },
                    { "id", goQuery.ID },
                    { "isIDValid", goQuery.IsIDValid },
                    { "gameObjectName", goQuery.gameObject.name },
                    { "gameObjectPath", path },
                    { "activeInHierarchy", goQuery.gameObject.activeInHierarchy },
                    { "vrseComponents", components }
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "count", result.Count },
                { "queryObjects", result }
            };
        }

        private static string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        public static object StoryRemoveAction(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            int nodeIndex = GetIntArg(args, "nodeIndex", -1);
            int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);

            if (!TryGetActionNodeArray(moment, section, triggerSetIndex, out Node[] nodeArray, out string arrayError))
                return new { error = arrayError };

            if (nodeIndex < 0 || nodeIndex >= nodeArray.Length)
                return new { error = $"nodeIndex must be within 0-{nodeArray.Length - 1}." };

            Dictionary<string, object> removedNode = SerializeNode(nodeArray[nodeIndex]);

            var newArray = new Node[nodeArray.Length - 1];
            if (nodeIndex > 0)
                Array.Copy(nodeArray, 0, newArray, 0, nodeIndex);
            if (nodeIndex < nodeArray.Length - 1)
                Array.Copy(nodeArray, nodeIndex + 1, newArray, nodeIndex, nodeArray.Length - nodeIndex - 1);

            SetActionNodeArray(moment, section, triggerSetIndex, newArray);
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "removedNodeIndex", nodeIndex },
                { "removedNode", removedNode },
                { "remainingCount", newArray.Length }
            };
        }

        public static object StoryAddChapter(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null) return new { error = "No StoryCreator found in the loaded scenes." };

            string name = GetStringArg(args, "name");
            if (string.IsNullOrEmpty(name)) name = "New Chapter";
            
            int index = GetIntArg(args, "index", -1);

            if (storyCreator._story.chapters == null)
                storyCreator._story.chapters = new Chapter[0];

            List<Chapter> chapters = storyCreator._story.chapters.ToList();
            var newChapter = new Chapter { name = name, moments = new Moment[0] };

            if (index >= 0 && index <= chapters.Count)
                chapters.Insert(index, newChapter);
            else
            {
                chapters.Add(newChapter);
                index = chapters.Count - 1;
            }

            storyCreator._story.chapters = chapters.ToArray();
            storyCreator._story.AssignChapterAndMomentIndex();
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "chapterIndex", index }, { "chapterName", name } };
        }

        public static object StoryRenameChapter(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null) return new { error = "No StoryCreator found in the loaded scenes." };

            int chapterIndex = GetIntArg(args, "chapterIndex", -1);
            if (storyCreator._story.chapters == null || chapterIndex < 0 || chapterIndex >= storyCreator._story.chapters.Length)
                return new { error = $"Invalid chapterIndex. Valid range: 0-{(storyCreator._story.chapters?.Length - 1 ?? -1)}." };

            string newName = GetStringArg(args, "newName");
            if (string.IsNullOrEmpty(newName)) return new { error = "newName is required." };

            storyCreator._story.chapters[chapterIndex].name = newName;
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "chapterIndex", chapterIndex }, { "newName", newName } };
        }

        public static object StoryRemoveChapter(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null) return new { error = "No StoryCreator found in the loaded scenes." };

            int chapterIndex = GetIntArg(args, "chapterIndex", -1);
            if (storyCreator._story.chapters == null || chapterIndex < 0 || chapterIndex >= storyCreator._story.chapters.Length)
                return new { error = $"Invalid chapterIndex. Valid range: 0-{(storyCreator._story.chapters?.Length - 1 ?? -1)}." };

            List<Chapter> chapters = storyCreator._story.chapters.ToList();
            var removedName = chapters[chapterIndex].name;
            chapters.RemoveAt(chapterIndex);
            
            storyCreator._story.chapters = chapters.ToArray();
            storyCreator._story.AssignChapterAndMomentIndex();
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "removedChapterIndex", chapterIndex }, { "removedChapterName", removedName }, { "remainingCount", chapters.Count } };
        }

        public static object StoryAddMoment(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null) return new { error = "No StoryCreator found in the loaded scenes." };

            int chapterIndex = GetIntArg(args, "chapterIndex", -1);
            if (storyCreator._story.chapters == null || chapterIndex < 0 || chapterIndex >= storyCreator._story.chapters.Length)
                return new { error = $"Invalid chapterIndex. Valid range: 0-{(storyCreator._story.chapters?.Length - 1 ?? -1)}." };

            Chapter chapter = storyCreator._story.chapters[chapterIndex];

            string name = GetStringArg(args, "name");
            if (string.IsNullOrEmpty(name)) name = "New Moment";
            
            int index = GetIntArg(args, "index", -1);

            if (chapter.moments == null)
                chapter.moments = new Moment[0];

            List<Moment> moments = chapter.moments.ToList();
            var newMoment = new Moment { name = name };

            if (index >= 0 && index <= moments.Count)
                moments.Insert(index, newMoment);
            else
            {
                moments.Add(newMoment);
                index = moments.Count - 1;
            }

            chapter.moments = moments.ToArray();
            storyCreator._story.AssignChapterAndMomentIndex();
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "chapterIndex", chapterIndex }, { "momentIndex", index }, { "momentName", name } };
        }

        public static object StoryRenameMoment(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string newName = GetStringArg(args, "newName");
            if (string.IsNullOrEmpty(newName)) return new { error = "newName is required." };

            moment.name = newName;
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "chapterIndex", chapterIndex }, { "momentIndex", momentIndex }, { "newName", newName } };
        }

        public static object StoryRemoveMoment(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            Chapter chapter = storyCreator._story.chapters[chapterIndex];
            List<Moment> moments = chapter.moments.ToList();
            string removedName = moment.name;
            moments.RemoveAt(momentIndex);

            chapter.moments = moments.ToArray();
            storyCreator._story.AssignChapterAndMomentIndex();
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object> { { "success", true }, { "chapterIndex", chapterIndex }, { "removedMomentIndex", momentIndex }, { "removedMomentName", removedName }, { "remainingCount", moments.Count } };
        }

        public static object CreateEvaluationFromTraining(Dictionary<string, object> args)
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
                return new { error = "Training experience not found. Provide experienceId or experienceName." };

            if (string.IsNullOrEmpty(experience.DevScene) || !File.Exists(experience.DevScene))
                return new { error = "Training dev scene not found." };

            EditorSceneManager.OpenScene(experience.DevScene, OpenSceneMode.Single);
            StoryCreator storyCreator = UnityEngine.Object.FindObjectOfType<StoryCreator>();
            if (storyCreator == null)
                return new { error = "StoryCreator not found in training scene." };

            VRseProjectWindowController projectController = new VRseProjectWindowController();
            string storyJsonPath = projectController.GetLocalStoryJsonPathFromConfig(projectName, module.ModuleId, experience.ExperienceId);
            if (string.IsNullOrEmpty(storyJsonPath) || !File.Exists(storyJsonPath))
                return new { error = "Training story JSON file not found." };

            string evaluationSceneName = GetStringArg(args, "evaluationSceneName");
            if (string.IsNullOrEmpty(evaluationSceneName))
                evaluationSceneName = CreateDefaultEvaluationName(experience.Name, "Evaluation_Scene");

            string evaluationStoryName = GetStringArg(args, "evaluationStoryName");
            if (string.IsNullOrEmpty(evaluationStoryName))
                evaluationStoryName = CreateDefaultEvaluationName(experience.Name, "Evaluation_JSON");

            string storyDefaults = GetStringArg(args, "storyDefaults");
            if (string.IsNullOrEmpty(storyDefaults))
                storyDefaults = "{\"storyMode\":\"Evaluation\",\"maxAttempts\":\"3\",\"useDefaultMaxAttempts\":\"false\"}";

            var window = EditorWindow.GetWindow<VRseEvaluationAutomationToolEditor>("Evaluation Automation Tool");
            if (window == null)
                return new { error = "Could not open Evaluation Automation Tool window." };

            Type windowType = window.GetType();
            if (!TrySetPrivateField(window, "_storyCreator", storyCreator) ||
                !TrySetPrivateField(window, "_originalStoryPath", storyJsonPath))
            {
                return new { error = "Could not configure Evaluation Automation Tool." };
            }

            TrySetPrivateField(window, "_currentSceneName", Path.GetFileNameWithoutExtension(experience.DevScene));
            TrySetPrivateField(window, "_currentStoryName", storyCreator._fileName ?? evaluationStoryName);
            TrySetPrivateField(window, "_evaluationSceneName", evaluationSceneName);
            TrySetPrivateField(window, "_evaluationStoryName", evaluationStoryName);
            TrySetPrivateField(window, "_storyDefaults", storyDefaults);
            TrySetPrivateField(window, "_useExistingEvaluation", false);

            if (args.ContainsKey("evaluationType"))
            {
                string evalType = GetStringArg(args, "evaluationType");
                if (Enum.TryParse(evalType, true, out Enums.EvaluationType parsed))
                    TrySetPrivateField(window, "_evaluationType", parsed);
            }

            TrySetPrivateField(window, "_showAutoToastMessageForWrongAction", GetBoolArg(args, "showAutoToastForWrongAction", true));
            TrySetPrivateField(window, "_showAutoToastMessageForRightAction", GetBoolArg(args, "showAutoToastForRightAction", true));
            TrySetPrivateField(window, "_rightActionToastMessageDisplayTime", GetFloatArg(args, "rightActionToastMessageDisplayTime", 2.5f));
            TrySetPrivateField(window, "_mistakeCoolDownTime", GetFloatArg(args, "mistakeCoolDownTime", 2.0f));
            TrySetPrivateField(window, "_debugMistakesCountToPass", GetIntArg(args, "debugMistakesCountToPass", 3));

            MethodInfo createMethod = windowType.GetMethod("CreateEvaluationScene", BindingFlags.Instance | BindingFlags.NonPublic);
            if (createMethod == null)
                return new { error = "Evaluation creation method not found." };

            try
            {
                createMethod.Invoke(window, null);
            }
            catch (Exception ex)
            {
                return new { error = $"Evaluation creation failed: {ex.Message}" };
            }

            string evaluationScenePath = Path.Combine(Path.GetDirectoryName(experience.DevScene) ?? string.Empty, evaluationSceneName + ".unity");
            string evaluationStoryPath = Path.Combine(Path.GetDirectoryName(storyJsonPath) ?? string.Empty, evaluationStoryName + ".json");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "evaluationScene", evaluationScenePath },
                { "evaluationStory", evaluationStoryPath },
                { "storyDefaults", storyDefaults }
            };
        }

        private static string CreateDefaultEvaluationName(string baseName, string suffix)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                return $"Evaluation_{suffix}";
            }

            if (baseName.EndsWith("_Training", StringComparison.OrdinalIgnoreCase))
                return baseName.Substring(0, baseName.Length - "_Training".Length) + "_" + suffix;

            return baseName + "_" + suffix;
        }

        private static bool TrySetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = typeof(VRseEvaluationAutomationToolEditor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return false;

            field.SetValue(target, value);
            return true;
        }

        private static float GetFloatArg(Dictionary<string, object> args, string key, float defaultValue)
        {
            if (!args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            object value = args[key];
            if (value is double doubleValue)
                return (float)doubleValue;
            if (value is float floatValue)
                return floatValue;
            if (value is long longValue)
                return (float)longValue;
            if (value is int intValue)
                return intValue;

            return float.TryParse(value.ToString(), out float parsed) ? parsed : defaultValue;
        }

        private static MomentDefaults LoadMomentDefaults(string defaultsJson)
        {
            if (string.IsNullOrEmpty(defaultsJson))
                return new MomentDefaults();

            try
            {
                var parsed = JsonUtility.FromJson<MomentDefaults>(defaultsJson);
                return parsed ?? new MomentDefaults();
            }
            catch
            {
                return new MomentDefaults();
            }
        }

        private class MomentDefaults
        {
            public float weightage;
            public float wrongReduction;
        }

        private static string GetStringArg(Dictionary<string, object> args, string key)
        {
            return args.ContainsKey(key) ? args[key]?.ToString()?.Trim() ?? string.Empty : string.Empty;
        }

        private static int GetIntArg(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (!args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            object value = args[key];
            if (value is long longValue)
                return (int)longValue;
            if (value is int intValue)
                return intValue;
            if (value is double doubleValue)
                return (int)doubleValue;

            return int.TryParse(value.ToString(), out int parsed) ? parsed : defaultValue;
        }

        private static bool GetBoolArg(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (!args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            object value = args[key];
            if (value is bool boolValue)
                return boolValue;

            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static ModuleData.ExperienceType ResolveExperienceType(Dictionary<string, object> args)
        {
            string experienceType = GetStringArg(args, "experienceType");
            return Enum.TryParse(experienceType, true, out ModuleData.ExperienceType parsedType)
                ? parsedType
                : ModuleData.ExperienceType.Training;
        }

        private static byte ParseNodeTypeArg(object rawType, byte defaultValue)
        {
            if (rawType == null)
                return defaultValue;

            if (rawType is long longValue)
                return (byte)longValue;
            if (rawType is int intValue)
                return (byte)intValue;

            string typeText = rawType.ToString()?.Trim() ?? string.Empty;
            if (byte.TryParse(typeText, out byte numericType))
                return numericType;

            if (string.Equals(typeText, "action", StringComparison.OrdinalIgnoreCase))
                return (byte)_Constants.ACTION_TYPE;

            if (string.Equals(typeText, "trigger", StringComparison.OrdinalIgnoreCase))
                return (byte)_Constants.TRIGGER_TYPE;

            return defaultValue;
        }

        private static StoryCreator FindStoryCreator(Dictionary<string, object> args)
        {
            string storyCreatorName = GetStringArg(args, "storyCreatorName");
            IEnumerable<StoryCreator> storyCreators = Resources.FindObjectsOfTypeAll<StoryCreator>()
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid() && candidate.gameObject.scene.isLoaded);

            if (!string.IsNullOrEmpty(storyCreatorName))
            {
                return storyCreators.FirstOrDefault(candidate =>
                    string.Equals(candidate.gameObject.name, storyCreatorName, StringComparison.OrdinalIgnoreCase));
            }

            return storyCreators.FirstOrDefault();
        }

        private static bool TryResolveMoment(Dictionary<string, object> args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error)
        {
            storyCreator = FindStoryCreator(args);
            moment = null;
            chapterIndex = GetIntArg(args, "chapterIndex", -1);
            momentIndex = GetIntArg(args, "momentIndex", -1);
            error = null;

            if (storyCreator == null)
            {
                error = "No StoryCreator found in the loaded scenes.";
                return false;
            }

            if (storyCreator._story?.chapters == null || storyCreator._story.chapters.Length == 0)
            {
                error = "The active StoryCreator does not have any chapters loaded.";
                return false;
            }

            if (chapterIndex < 0 || chapterIndex >= storyCreator._story.chapters.Length)
            {
                error = $"chapterIndex is out of range. Valid range: 0-{storyCreator._story.chapters.Length - 1}.";
                return false;
            }

            Chapter chapter = storyCreator._story.chapters[chapterIndex];
            if (chapter?.moments == null || chapter.moments.Length == 0)
            {
                error = $"Chapter {chapterIndex} does not contain any moments.";
                return false;
            }

            if (momentIndex < 0 || momentIndex >= chapter.moments.Length)
            {
                error = $"momentIndex is out of range for chapter {chapterIndex}. Valid range: 0-{chapter.moments.Length - 1}.";
                return false;
            }

            moment = chapter.moments[momentIndex];
            if (moment == null)
            {
                error = $"Moment {momentIndex} in chapter {chapterIndex} is null.";
                return false;
            }

            return true;
        }

        private static bool IsSimpleActionSection(string section)
        {
            return section == "onAwake" ||
                   section == "onStart" ||
                   section == "onFirstWarning" ||
                   section == "onLastWarning" ||
                   section == "onEnd";
        }

        private static ActionSet GetOrCreateActionSet(Moment moment, string section)
        {
            switch (section)
            {
                case "onAwake":
                    if (moment.onAwake == null) moment.onAwake = new ActionSet { actions = new Node[0] };
                    return moment.onAwake;
                case "onStart":
                    if (moment.onStart == null) moment.onStart = new ActionSet { actions = new Node[0] };
                    return moment.onStart;
                case "onFirstWarning":
                    if (moment.onFirstWarning == null) moment.onFirstWarning = new ActionSet { actions = new Node[0] };
                    return moment.onFirstWarning;
                case "onLastWarning":
                    if (moment.onLastWarning == null) moment.onLastWarning = new ActionSet { actions = new Node[0] };
                    return moment.onLastWarning;
                case "onEnd":
                    if (moment.onEnd == null) moment.onEnd = new ActionSet { actions = new Node[0] };
                    return moment.onEnd;
                default:
                    return null;
            }
        }

        private static bool TryGetTriggerActionSet(Moment moment, string section, int triggerSetIndex, out TriggerActionSet triggerActionSet, out string error)
        {
            triggerActionSet = null;
            error = null;

            if (triggerSetIndex < 0)
            {
                error = "triggerSetIndex is required for onWrong and onRight sections.";
                return false;
            }

            TriggerActionSet[] sets = null;
            if (section == "onWrong")
            {
                sets = moment.onWrong;
            }
            else if (section == "onRight")
            {
                sets = moment.onRight?.triggerActionSets;
            }

            if (sets == null || sets.Length == 0)
            {
                error = $"Section '{section}' does not contain any trigger sets.";
                return false;
            }

            if (triggerSetIndex >= sets.Length)
            {
                error = $"triggerSetIndex is out of range for section '{section}'. Valid range: 0-{sets.Length - 1}.";
                return false;
            }

            triggerActionSet = sets[triggerSetIndex];
            if (triggerActionSet == null)
            {
                error = $"Trigger set {triggerSetIndex} in section '{section}' is null.";
                return false;
            }

            return true;
        }

        private static bool TryResolveNode(Moment moment, string section, string nodeKind, int triggerSetIndex, int nodeIndex, out Node node, out string error)
        {
            node = null;
            error = null;

            if (IsSimpleActionSection(section))
            {
                ActionSet actionSet = GetOrCreateActionSet(moment, section);
                if (actionSet?.actions == null || actionSet.actions.Length == 0)
                {
                    error = $"Section '{section}' does not contain any actions.";
                    return false;
                }

                if (nodeIndex < 0 || nodeIndex >= actionSet.actions.Length)
                {
                    error = $"nodeIndex is out of range for section '{section}'. Valid range: 0-{actionSet.actions.Length - 1}.";
                    return false;
                }

                node = actionSet.actions[nodeIndex];
                return true;
            }

            if (section != "onWrong" && section != "onRight")
            {
                error = "section must be one of onAwake, onStart, onFirstWarning, onLastWarning, onEnd, onWrong, or onRight.";
                return false;
            }

            if (!TryGetTriggerActionSet(moment, section, triggerSetIndex, out TriggerActionSet triggerActionSet, out error))
                return false;

            string normalizedNodeKind = string.IsNullOrEmpty(nodeKind) ? "action" : nodeKind.Trim().ToLowerInvariant();
            if (normalizedNodeKind == "trigger")
            {
                node = triggerActionSet.trigger;
                if (node == null)
                {
                    error = $"Trigger set {triggerSetIndex} in section '{section}' does not have a trigger node.";
                    return false;
                }

                return true;
            }

            if (triggerActionSet.actions == null || triggerActionSet.actions.Length == 0)
            {
                error = $"Trigger set {triggerSetIndex} in section '{section}' does not contain any actions.";
                return false;
            }

            if (nodeIndex < 0 || nodeIndex >= triggerActionSet.actions.Length)
            {
                error = $"nodeIndex is out of range for trigger set {triggerSetIndex} in section '{section}'. Valid range: 0-{triggerActionSet.actions.Length - 1}.";
                return false;
            }

            node = triggerActionSet.actions[nodeIndex];
            return true;
        }

        private static string NormalizeSectionName(string section)
        {
            string normalized = (section ?? string.Empty)
                .Trim()
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .ToLowerInvariant();

            switch (normalized)
            {
                case "onawake": return "onAwake";
                case "onstart": return "onStart";
                case "onwrong": return "onWrong";
                case "onright": return "onRight";
                case "onfirstwarning": return "onFirstWarning";
                case "onlastwarning": return "onLastWarning";
                case "onend": return "onEnd";
                default: return section;
            }
        }

        private static Node CreateDefaultNode(bool isAction)
        {
            string defaultName = isAction ? "Objects" : "GrabbableTrigger";
            var node = new Node
            {
                Name = defaultName,
                Type = (byte)(isAction ? _Constants.ACTION_TYPE : _Constants.TRIGGER_TYPE),
                Query = string.Empty,
                Option = isAction ? "Spawn" : string.Empty,
                Data = "{}",
                TargetGameObject = null
            };

            NodeTemplatesData nodeTemplatesData = LoadNodeTemplatesData();
            NodeTemplatesData.NodeData template = nodeTemplatesData != null ? nodeTemplatesData.GetNodeTemplate(defaultName) : null;
            if (template != null && template.Options != null && template.Options.Count > 0)
            {
                NodeTemplatesData.OptionData option = template.Options.FirstOrDefault(currentOption => !string.IsNullOrWhiteSpace(currentOption.Name));
                if (option != null)
                {
                    node.Option = option.Name;
                    var data = new Dictionary<string, object>();
                    foreach (NodeTemplatesData.ParameterData parameter in option.Parameters)
                    {
                        data[parameter.Key] = parameter.DefaultValue ?? string.Empty;
                    }
                    node.Data = JsonConvert.SerializeObject(data);
                }
            }

            return node;
        }

        private static NodeTemplatesData LoadNodeTemplatesData()
        {
            string[] guids = AssetDatabase.FindAssets("t:NodeTemplatesData");
            if (guids == null || guids.Length == 0)
                return null;

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            NodeTemplatesData nodeTemplatesData = AssetDatabase.LoadAssetAtPath<NodeTemplatesData>(path);
            if (nodeTemplatesData != null)
                nodeTemplatesData.OnValidate();
            return nodeTemplatesData;
        }

        private static void SetNodeTargetGameObject(Node node, GameObject targetGameObject)
        {
            node.TargetGameObject = targetGameObject;

            QueryObjectsIdManager queryObjectsIdManager = UnityEngine.Object.FindObjectOfType<QueryObjectsIdManager>();
            if (queryObjectsIdManager != null)
            {
                node.Query = queryObjectsIdManager.GetQueryObjectNameWithId(targetGameObject);
            }
            else
            {
                node.Query = targetGameObject != null ? targetGameObject.name : string.Empty;
            }
        }

        private static void MarkStoryChanged(StoryCreator storyCreator)
        {
            if (storyCreator == null)
                return;

            if (storyCreator._story != null)
                storyCreator._story.AssignChapterAndMomentIndex();
            storyCreator.InvalidateIsStorySavedToFileCache();

            ReferenceManager referenceManager = UnityEngine.Object.FindObjectOfType<ReferenceManager>();
            if (referenceManager != null)
                referenceManager.OnStoryChangedFromInspector();

            EditorUtility.SetDirty(storyCreator);
            if (storyCreator.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(storyCreator.gameObject.scene);
        }

        private static Dictionary<string, object> SerializeNode(Node node)
        {
            return new Dictionary<string, object>
            {
                { "name", node?.Name },
                { "option", node?.Option },
                { "query", node?.Query },
                { "data", node?.Data },
                { "type", node != null ? node.Type : (byte)0 },
                { "targetGameObject", node?.TargetGameObject != null ? node.TargetGameObject.name : null }
            };
        }

        private static Dictionary<string, object> BuildModulePayload(ModuleData module, string fallbackModuleId, string fallbackModuleName)
        {
            return new Dictionary<string, object>
            {
                { "id", module != null ? module.ModuleId : fallbackModuleId },
                { "name", module != null ? module.GetModuleName() : fallbackModuleName }
            };
        }

        private static Dictionary<string, object> BuildExperiencePayload(ModuleData.ExperienceData experience, string fallbackExperienceId, string fallbackExperienceName, ModuleData.ExperienceType fallbackType)
        {
            return new Dictionary<string, object>
            {
                { "id", experience != null ? experience.ExperienceId : fallbackExperienceId },
                { "name", experience != null ? experience.Name : fallbackExperienceName },
                { "type", experience != null ? experience.Type.ToString() : fallbackType.ToString() }
            };
        }

        private static Dictionary<string, object> BuildExperienceCreationStatus(string projectName, ModuleData module, ModuleData.ExperienceData experience)
        {
            if (experience == null)
            {
                return new Dictionary<string, object>
                {
                    { "projectName", projectName },
                    { "moduleFound", module != null },
                    { "experienceFound", false }
                };
            }

            string storyJsonAbsolutePath = GetAbsoluteStoryJsonPath(experience.StoryJsonPath);
            bool storyJsonExists = !string.IsNullOrEmpty(storyJsonAbsolutePath) && File.Exists(storyJsonAbsolutePath);
            bool devSceneExists = !string.IsNullOrEmpty(experience.DevScene) && File.Exists(experience.DevScene);
            bool artSceneExists = !string.IsNullOrEmpty(experience.ArtScene) && File.Exists(experience.ArtScene);

            return new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "moduleFound", module != null },
                { "experienceFound", true },
                { "moduleId", module?.ModuleId },
                { "moduleName", module?.GetModuleName() },
                { "experienceId", experience.ExperienceId },
                { "experienceName", experience.Name },
                { "experienceType", experience.Type.ToString() },
                { "jsonUrl", experience.JsonUrl },
                { "storyJsonPath", experience.StoryJsonPath },
                { "storyJsonAbsolutePath", storyJsonAbsolutePath },
                { "storyJsonExists", storyJsonExists },
                { "devScenePath", experience.DevScene },
                { "devSceneExists", devSceneExists },
                { "artScenePath", experience.ArtScene },
                { "artSceneExists", artSceneExists },
                { "isFullyConfigured", storyJsonExists && devSceneExists && artSceneExists }
            };
        }

        private static string GetAbsoluteStoryJsonPath(string storyJsonPath)
        {
            if (string.IsNullOrEmpty(storyJsonPath))
                return null;

            if (Path.IsPathRooted(storyJsonPath))
                return storyJsonPath;

            string trimmed = storyJsonPath.TrimStart('/', '\\');
            return Path.Combine(Application.streamingAssetsPath, trimmed).Replace("\\", "/");
        }

        private static string ResolveExperienceJsonFileUrl(string projectName, Dictionary<string, object> args)
        {
            if (!AuthUtility.IsLoggedIn())
                return null;

            try
            {
                AccessModulesResponse response = FetchAccessModules();
                if (response?.projects == null)
                    return null;

                string projectId = GetStringArg(args, "projectId");
                AccessProject remoteProject = response.projects.FirstOrDefault(project =>
                    (!string.IsNullOrEmpty(projectId) && string.Equals(project._id, projectId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(project.name, projectName, StringComparison.OrdinalIgnoreCase));

                if (remoteProject?.modules == null)
                    return null;

                string moduleId = GetStringArg(args, "moduleId");
                string moduleName = GetStringArg(args, "moduleName");
                AccessModule remoteModule = remoteProject.modules.FirstOrDefault(module =>
                    (!string.IsNullOrEmpty(moduleId) && string.Equals(module._id, moduleId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(moduleName) && string.Equals(module.name, moduleName, StringComparison.OrdinalIgnoreCase)));

                if (remoteModule?.experiences == null)
                    return null;

                string experienceId = GetStringArg(args, "experienceId");
                string experienceName = GetStringArg(args, "experienceName");
                ModuleData.ExperienceType experienceType = ResolveExperienceType(args);
                AccessExperience remoteExperience = remoteModule.experiences.FirstOrDefault(experience =>
                    (!string.IsNullOrEmpty(experienceId) && string.Equals(experience._id, experienceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(experienceName) && string.Equals(experience.name, experienceName, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(experience.type, experienceType.ToString(), StringComparison.OrdinalIgnoreCase));

                return remoteExperience?.jsonFileUrl;
            }
            catch
            {
                return null;
            }
        }

        private static List<object> BuildValidationIssues(Story story, Dictionary<Moment, Dictionary<string, List<string>>> validationResult)
        {
            var issues = new List<object>();
            if (story?.chapters == null)
                return issues;

            for (int chapterIndex = 0; chapterIndex < story.chapters.Length; chapterIndex++)
            {
                Chapter chapter = story.chapters[chapterIndex];
                if (chapter?.moments == null)
                    continue;

                for (int momentIndex = 0; momentIndex < chapter.moments.Length; momentIndex++)
                {
                    Moment moment = chapter.moments[momentIndex];
                    if (moment == null || !validationResult.TryGetValue(moment, out Dictionary<string, List<string>> momentIssues) || momentIssues == null || momentIssues.Count == 0)
                        continue;

                    var sections = new List<object>();
                    int issueCount = 0;
                    foreach (KeyValuePair<string, List<string>> entry in momentIssues)
                    {
                        List<string> messages = entry.Value ?? new List<string>();
                        issueCount += messages.Count;
                        sections.Add(new Dictionary<string, object>
                        {
                            { "section", entry.Key },
                            { "messages", messages }
                        });
                    }

                    issues.Add(new Dictionary<string, object>
                    {
                        { "chapterIndex", chapterIndex },
                        { "chapterName", chapter.name },
                        { "momentIndex", momentIndex },
                        { "momentName", moment.name },
                        { "issueCount", issueCount },
                        { "sections", sections }
                    });
                }
            }

            return issues;
        }



        public static object StoryMoveAction(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            int fromIndex = GetIntArg(args, "fromIndex", -1);
            int toIndex = GetIntArg(args, "toIndex", -1);
            int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);

            if (!TryGetActionNodeArray(moment, section, triggerSetIndex, out Node[] nodeArray, out string arrayError))
                return new { error = arrayError };

            if (fromIndex < 0 || fromIndex >= nodeArray.Length || toIndex < 0 || toIndex >= nodeArray.Length)
                return new { error = $"fromIndex and toIndex must be within 0-{nodeArray.Length - 1}." };

            MoveNodeInArray(ref nodeArray, fromIndex, toIndex);
            SetActionNodeArray(moment, section, triggerSetIndex, nodeArray);
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "fromIndex", fromIndex },
                { "toIndex", toIndex }
            };
        }

        public static object StoryDuplicateAction(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment moment, out int chapterIndex, out int momentIndex, out string error))
                return new { error };

            string section = NormalizeSectionName(GetStringArg(args, "section"));
            int nodeIndex = GetIntArg(args, "nodeIndex", -1);
            int triggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);

            if (!TryGetActionNodeArray(moment, section, triggerSetIndex, out Node[] nodeArray, out string arrayError))
                return new { error = arrayError };

            if (nodeIndex < 0 || nodeIndex >= nodeArray.Length)
                return new { error = $"nodeIndex must be within 0-{nodeArray.Length - 1}." };

            var newArray = new Node[nodeArray.Length + 1];
            Array.Copy(nodeArray, 0, newArray, 0, nodeIndex + 1);
            newArray[nodeIndex + 1] = CloneNode(nodeArray[nodeIndex]);
            Array.Copy(nodeArray, nodeIndex + 1, newArray, nodeIndex + 2, nodeArray.Length - nodeIndex - 1);

            SetActionNodeArray(moment, section, triggerSetIndex, newArray);
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "chapterIndex", chapterIndex },
                { "momentIndex", momentIndex },
                { "section", section },
                { "triggerSetIndex", triggerSetIndex },
                { "sourceNodeIndex", nodeIndex },
                { "newNodeIndex", nodeIndex + 1 },
                { "node", SerializeNode(newArray[nodeIndex + 1]) }
            };
        }

        public static object StoryApplyActionToMultipleMoments(Dictionary<string, object> args)
        {
            if (!TryResolveMoment(args, out StoryCreator storyCreator, out Moment sourceMoment, out int sourceChapterIndex, out int sourceMomentIndex, out string error))
                return new { error };

            string sourceSection = NormalizeSectionName(GetStringArg(args, "section"));
            int sourceNodeIndex = GetIntArg(args, "nodeIndex", -1);
            int sourceTriggerSetIndex = GetIntArg(args, "triggerSetIndex", -1);

            if (!TryGetActionNodeArray(sourceMoment, sourceSection, sourceTriggerSetIndex, out Node[] sourceArray, out string sourceArrayError))
                return new { error = sourceArrayError };

            if (sourceNodeIndex < 0 || sourceNodeIndex >= sourceArray.Length)
                return new { error = $"nodeIndex must be within 0-{sourceArray.Length - 1}." };

            if (!args.TryGetValue("targets", out object targetsObj) || !(targetsObj is List<object> rawTargets) || rawTargets.Count == 0)
                return new { error = "targets is required and must contain at least one target moment." };

            Node sourceNode = sourceArray[sourceNodeIndex];
            var appliedTargets = new List<object>();

            foreach (object rawTarget in rawTargets)
            {
                if (!(rawTarget is Dictionary<string, object> target))
                    continue;

                int targetChapterIndex = GetNestedIntArg(target, "chapterIndex", -1);
                int targetMomentIndex = GetNestedIntArg(target, "momentIndex", -1);
                string targetSection = NormalizeSectionName(GetNestedStringArg(target, "section"));
                if (string.IsNullOrEmpty(targetSection))
                    targetSection = sourceSection;
                int targetTriggerSetIndex = GetNestedIntArg(target, "triggerSetIndex", sourceTriggerSetIndex);

                if (!TryResolveMomentByIndex(storyCreator, targetChapterIndex, targetMomentIndex, out Moment targetMoment, out string targetMomentError))
                {
                    appliedTargets.Add(new Dictionary<string, object>
                    {
                        { "chapterIndex", targetChapterIndex },
                        { "momentIndex", targetMomentIndex },
                        { "section", targetSection },
                        { "triggerSetIndex", targetTriggerSetIndex },
                        { "success", false },
                        { "error", targetMomentError }
                    });
                    continue;
                }

                if (!TryGetActionNodeArray(targetMoment, targetSection, targetTriggerSetIndex, out Node[] targetArray, out string targetArrayError))
                {
                    appliedTargets.Add(new Dictionary<string, object>
                    {
                        { "chapterIndex", targetChapterIndex },
                        { "momentIndex", targetMomentIndex },
                        { "section", targetSection },
                        { "triggerSetIndex", targetTriggerSetIndex },
                        { "success", false },
                        { "error", targetArrayError }
                    });
                    continue;
                }

                Array.Resize(ref targetArray, targetArray.Length + 1);
                int newNodeIndex = targetArray.Length - 1;
                targetArray[newNodeIndex] = CloneNode(sourceNode);
                SetActionNodeArray(targetMoment, targetSection, targetTriggerSetIndex, targetArray);

                appliedTargets.Add(new Dictionary<string, object>
                {
                    { "chapterIndex", targetChapterIndex },
                    { "momentIndex", targetMomentIndex },
                    { "section", targetSection },
                    { "triggerSetIndex", targetTriggerSetIndex },
                    { "success", true },
                    { "newNodeIndex", newNodeIndex }
                });
            }

            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "source", new Dictionary<string, object>
                    {
                        { "chapterIndex", sourceChapterIndex },
                        { "momentIndex", sourceMomentIndex },
                        { "section", sourceSection },
                        { "triggerSetIndex", sourceTriggerSetIndex },
                        { "nodeIndex", sourceNodeIndex },
                        { "node", SerializeNode(sourceNode) }
                    }
                },
                { "targets", appliedTargets }
            };
        }

        public static object ListStoryBackups(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            string filePath = storyCreator._FilePath;
            var backups = StoryVersioning.GetHistory(filePath)
                .Select((backup, index) => new Dictionary<string, object>
                {
                    { "index", index },
                    { "filePath", backup.filePath },
                    { "timestamp", backup.timestamp },
                    { "displayDate", backup.displayDate },
                    { "reason", backup.reason }
                })
                .Cast<object>()
                .ToList();

            return new Dictionary<string, object>
            {
                { "storyCreator", storyCreator.gameObject.name },
                { "filePath", filePath },
                { "backupCount", backups.Count },
                { "backups", backups }
            };
        }

        public static object CreateStoryBackup(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            string filePath = storyCreator._FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return new { error = $"Story JSON file does not exist: {filePath}" };

            string reason = GetStringArg(args, "reason");
            if (string.IsNullOrEmpty(reason))
                reason = "Manual MCP Backup";

            StoryVersioning.CreateVersion(filePath, reason);
            var latest = StoryVersioning.GetHistory(filePath).FirstOrDefault();

            return new Dictionary<string, object>
            {
                { "success", latest != null },
                { "storyCreator", storyCreator.gameObject.name },
                { "filePath", filePath },
                { "latestBackup", latest == null ? null : new Dictionary<string, object>
                    {
                        { "filePath", latest.filePath },
                        { "timestamp", latest.timestamp },
                        { "displayDate", latest.displayDate },
                        { "reason", latest.reason }
                    }
                }
            };
        }

        public static object RestoreStoryBackup(Dictionary<string, object> args)
        {
            StoryCreator storyCreator = FindStoryCreator(args);
            if (storyCreator == null)
                return new { error = "No StoryCreator found in the loaded scenes." };

            string filePath = storyCreator._FilePath;
            if (string.IsNullOrEmpty(filePath))
                return new { error = "The active StoryCreator does not have a valid story file path." };

            string backupPath = GetStringArg(args, "backupPath");
            if (string.IsNullOrEmpty(backupPath))
            {
                int backupIndex = GetIntArg(args, "backupIndex", -1);
                var history = StoryVersioning.GetHistory(filePath);
                if (backupIndex < 0 || backupIndex >= history.Count)
                    return new { error = $"backupIndex must be within 0-{Math.Max(history.Count - 1, 0)}." };
                backupPath = history[backupIndex].filePath;
            }

            if (!File.Exists(backupPath))
                return new { error = $"Backup file does not exist: {backupPath}" };

            StoryVersioning.RestoreVersion(filePath, backupPath);
            storyCreator.SetStoryFromFile();
            storyCreator.InvalidateIsStorySavedToFileCache();
            MarkStoryChanged(storyCreator);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "storyCreator", storyCreator.gameObject.name },
                { "filePath", filePath },
                { "restoredBackupPath", backupPath }
            };
        }

        private static string GetNestedStringArg(Dictionary<string, object> args, string key)
        {
            return args.ContainsKey(key) ? args[key]?.ToString()?.Trim() ?? string.Empty : string.Empty;
        }

        private static int GetNestedIntArg(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (!args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            object value = args[key];
            if (value is long longValue)
                return (int)longValue;
            if (value is int intValue)
                return intValue;
            if (value is double doubleValue)
                return (int)doubleValue;

            return int.TryParse(value.ToString(), out int parsed) ? parsed : defaultValue;
        }

        private static bool TryResolveMomentByIndex(StoryCreator storyCreator, int chapterIndex, int momentIndex, out Moment moment, out string error)
        {
            moment = null;
            error = null;

            if (storyCreator?._story?.chapters == null || storyCreator._story.chapters.Length == 0)
            {
                error = "The active StoryCreator does not have any chapters loaded.";
                return false;
            }

            if (chapterIndex < 0 || chapterIndex >= storyCreator._story.chapters.Length)
            {
                error = $"chapterIndex is out of range. Valid range: 0-{storyCreator._story.chapters.Length - 1}.";
                return false;
            }

            Chapter chapter = storyCreator._story.chapters[chapterIndex];
            if (chapter?.moments == null || chapter.moments.Length == 0)
            {
                error = $"Chapter {chapterIndex} does not contain any moments.";
                return false;
            }

            if (momentIndex < 0 || momentIndex >= chapter.moments.Length)
            {
                error = $"momentIndex is out of range for chapter {chapterIndex}. Valid range: 0-{chapter.moments.Length - 1}.";
                return false;
            }

            moment = chapter.moments[momentIndex];
            return moment != null;
        }

        private static bool TryGetActionNodeArray(Moment moment, string section, int triggerSetIndex, out Node[] nodeArray, out string error)
        {
            nodeArray = null;
            error = null;

            if (IsSimpleActionSection(section))
            {
                ActionSet actionSet = GetOrCreateActionSet(moment, section);
                if (actionSet.actions == null)
                    actionSet.actions = new Node[0];
                nodeArray = actionSet.actions;
                return true;
            }

            if (section == "onWrong" || section == "onRight")
            {
                if (!TryGetTriggerActionSet(moment, section, triggerSetIndex, out TriggerActionSet triggerActionSet, out error))
                    return false;

                if (triggerActionSet.actions == null)
                    triggerActionSet.actions = new Node[0];

                nodeArray = triggerActionSet.actions;
                return true;
            }

            error = "section must be one of onAwake, onStart, onFirstWarning, onLastWarning, onEnd, onWrong, or onRight.";
            return false;
        }

        private static void SetActionNodeArray(Moment moment, string section, int triggerSetIndex, Node[] nodeArray)
        {
            if (IsSimpleActionSection(section))
            {
                GetOrCreateActionSet(moment, section).actions = nodeArray;
                return;
            }

            if (!TryGetTriggerActionSet(moment, section, triggerSetIndex, out TriggerActionSet triggerActionSet, out _))
                return;

            triggerActionSet.actions = nodeArray;
        }

        private static void MoveNodeInArray(ref Node[] nodeArray, int oldIndex, int newIndex)
        {
            Node node = nodeArray[oldIndex];

            for (int i = oldIndex; i < nodeArray.Length - 1; i++)
                nodeArray[i] = nodeArray[i + 1];

            for (int i = nodeArray.Length - 1; i > newIndex; i--)
                nodeArray[i] = nodeArray[i - 1];

            nodeArray[newIndex] = node;
        }

        private static Node CloneNode(Node original)
        {
            if (original == null)
                return null;

            return new Node
            {
                Name = original.Name,
                TargetGameObject = original.TargetGameObject,
                ID = original.ID,
                Query = original.Query,
                Option = original.Option,
                Data = original.Data,
                Type = original.Type
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