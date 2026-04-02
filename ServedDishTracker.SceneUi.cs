using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;

namespace HostUtilities
{
    internal static partial class ServedDishTracker
    {
        private static List<SceneDirectoryData.SceneDirectoryEntry> GetAvailableSceneEntries()
        {
            List<SceneDirectoryData.SceneDirectoryEntry> entries = new List<SceneDirectoryData.SceneDirectoryEntry>();

            try
            {
                AddEntries(entries, MenuLevelHelper.GetLevelList());
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to read lobby level list, falling back to other scene sources: " + ex.GetType().Name + ": " + ex.Message);
            }

            AddEntriesFromSceneDirectory(entries, GetProgressSceneDirectory());
            AddEntriesFromSceneDirectory(entries, GetWorldMapSceneDirectory());
            AddFrontendSessionEntries(entries, GameSession.GameType.Cooperative);
            AddFrontendSessionEntries(entries, GameSession.GameType.Competitive);

            return entries;
        }

        private static void AddEntries(List<SceneDirectoryData.SceneDirectoryEntry> entryList, List<SceneDirectoryData.SceneDirectoryEntry> entries)
        {
            if (entryList == null || entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                SceneDirectoryData.SceneDirectoryEntry entry = entries[i];
                if (entry == null || IsIgnoredSceneEntry(entry))
                {
                    continue;
                }

                entryList.Add(entry);
            }
        }

        private static void AddEntriesFromSceneDirectory(List<SceneDirectoryData.SceneDirectoryEntry> entryList, SceneDirectoryData sceneDirectory)
        {
            if (sceneDirectory == null || sceneDirectory.Scenes == null)
            {
                return;
            }

            AddEntries(entryList, sceneDirectory.Scenes.ToList());
        }

        private static void AddFrontendSessionEntries(List<SceneDirectoryData.SceneDirectoryEntry> entryList, GameSession.GameType gameType)
        {
            List<GameSession> sessionPrefabs = GetFrontendSessionPrefabs(gameType);
            for (int i = 0; i < sessionPrefabs.Count; i++)
            {
                AddEntriesFromSceneDirectory(entryList, GetSceneDirectory(sessionPrefabs[i]));
            }
        }

        private static List<SceneInfo> GetDIYScenes()
        {
            if (Time.frameCount < nextDIYSceneRefreshFrame)
            {
                return CachedDIYScenes;
            }

            List<SceneInfo> scenes = new List<SceneInfo>();
            AddDIYScenesFromRuntimeManager(scenes);
            AddDIYScenesFromFileSystem(scenes);

            CachedDIYScenes.Clear();
            CachedDIYScenes.AddRange(scenes);
            nextDIYSceneRefreshFrame = Time.frameCount + 120;
            return CachedDIYScenes;
        }

        private static void AddDIYScenesFromRuntimeManager(List<SceneInfo> scenes)
        {
            IList levelSetInfos;
            if (!TryGetDIYLevelSetInfos(out levelSetInfos))
            {
                return;
            }

            for (int i = 0; i < levelSetInfos.Count; i++)
            {
                object levelSetPair = levelSetInfos[i];
                object levelSetInfo = GetPairValue(levelSetPair);
                Array levelInfos = GetFieldValue(levelSetInfo, "levelInfos") as Array;
                if (levelInfos == null)
                {
                    continue;
                }

                string levelSetName = GetLocalizedString(levelSetInfo, "levelSetNameZH", "levelSetName");
                for (int j = 0; j < levelInfos.Length; j++)
                {
                    object levelInfo = levelInfos.GetValue(j);
                    string sceneName = GetStringField(levelInfo, "sceneName");
                    if (string.IsNullOrEmpty(sceneName))
                    {
                        continue;
                    }

                    string levelName = GetLocalizedString(levelInfo, "levelNameZH", "levelName");
                    string displayName = BuildDIYDisplayName(levelSetName, levelName, sceneName);

                    LevelConfigBase levelConfig;
                    SceneInfo scene = TryGetDIYLevelConfig(levelInfo, levelSetInfo, out levelConfig) && !IsHordeLevel(levelConfig)
                        ? BuildSceneInfo(sceneName, displayName, levelConfig)
                        : null;

                    if (scene == null)
                    {
                        SceneInfo cachedScene;
                        if (SceneCache.TryGetValue(sceneName, out cachedScene) && cachedScene != null)
                        {
                            cachedScene.DisplayName = displayName;
                            scene = cachedScene;
                        }
                    }

                    if (scene == null)
                    {
                        scene = new SceneInfo();
                        scene.SceneName = sceneName;
                        scene.DisplayName = displayName;
                    }
                    else
                    {
                        scene.DisplayName = displayName;
                    }

                    AddDIYSceneIfMissing(scenes, scene);
                }
            }
        }

        private static void AddDIYScenesFromFileSystem(List<SceneInfo> scenes)
        {
            string diyLevelsRoot = Path.Combine(Path.Combine(Paths.PluginPath, "OC2DIYLevel"), "levels");
            if (!Directory.Exists(diyLevelsRoot))
            {
                return;
            }

            string[] directories = Directory.GetDirectories(diyLevelsRoot);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string setName = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(setName))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(directory);
                for (int j = 0; j < files.Length; j++)
                {
                    string fileName = Path.GetFileName(files[j]);
                    if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SceneInfo scene = new SceneInfo();
                    scene.SceneName = fileName;
                    scene.DisplayName = BuildDIYDisplayName("DIY " + setName, fileName, fileName);

                    SceneInfo cachedScene;
                    if (SceneCache.TryGetValue(scene.SceneName, out cachedScene) && cachedScene != null)
                    {
                        cachedScene.DisplayName = scene.DisplayName;
                        scene = cachedScene;
                    }

                    AddDIYSceneIfMissing(scenes, scene);
                }
            }
        }

        private static void AddDIYSceneIfMissing(List<SceneInfo> scenes, SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName))
            {
                return;
            }

            if (scenes.Any(existing => string.Equals(existing.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            scenes.Add(scene);
        }

        private static bool TryGetDIYLevelSetInfos(out IList levelSetInfos)
        {
            levelSetInfos = null;

            Type diyLevelAssetBundleManagerType = AccessTools.TypeByName("OC2DIYLevel.DIYLevelAssetBundleManager");
            if (diyLevelAssetBundleManagerType == null)
            {
                return false;
            }

            PropertyInfo isInitializedProperty = AccessTools.Property(diyLevelAssetBundleManagerType, "IsInitialized");
            if (isInitializedProperty == null || !(bool)isInitializedProperty.GetValue(null, null))
            {
                return false;
            }

            FieldInfo levelSetInfosField = AccessTools.Field(diyLevelAssetBundleManagerType, "levelSetInfos");
            if (levelSetInfosField == null)
            {
                return false;
            }

            levelSetInfos = levelSetInfosField.GetValue(null) as IList;
            return levelSetInfos != null;
        }

        private static bool TryGetDIYLevelConfig(object levelInfo, object levelSetInfo, out LevelConfigBase levelConfig)
        {
            levelConfig = GetLevelConfigObject(levelInfo);
            if (levelConfig != null)
            {
                return true;
            }

            levelConfig = GetLevelConfigObject(levelSetInfo);
            return levelConfig != null;
        }

        private static LevelConfigBase GetLevelConfigObject(object source)
        {
            if (source == null)
            {
                return null;
            }

            Type sourceType = source.GetType();
            FieldInfo[] fields = sourceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (typeof(LevelConfigBase).IsAssignableFrom(field.FieldType))
                {
                    return field.GetValue(source) as LevelConfigBase;
                }
            }

            PropertyInfo[] properties = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || !typeof(LevelConfigBase).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                try
                {
                    return property.GetValue(source, null) as LevelConfigBase;
                }
                catch
                {
                }
            }

            return null;
        }

        private static string BuildDIYDisplayName(string levelSetName, string levelName, string sceneName)
        {
            string resolvedLevelSetName = string.IsNullOrEmpty(levelSetName) ? "DIY" : levelSetName;
            string resolvedLevelName = string.IsNullOrEmpty(levelName) ? sceneName : levelName;
            return resolvedLevelSetName + " - " + resolvedLevelName + " [" + sceneName + "]";
        }

        private static string GetLocalizedString(object source, string primaryFieldName, string fallbackFieldName)
        {
            string localized = GetStringField(source, primaryFieldName);
            if (!string.IsNullOrEmpty(localized))
            {
                return localized;
            }

            string fallback = GetStringField(source, fallbackFieldName);
            if (!string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }

            return null;
        }

        private static object GetPairValue(object pair)
        {
            return pair != null
                ? pair.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public).GetValue(pair, null)
                : null;
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            FieldInfo fieldInfo = instance != null ? AccessTools.Field(instance.GetType(), fieldName) : null;
            return fieldInfo != null ? fieldInfo.GetValue(instance) : null;
        }

        private static string GetStringField(object instance, string fieldName)
        {
            return GetFieldValue(instance, fieldName) as string;
        }

        private static void AddSceneFromEntry(List<SceneInfo> scenes, HashSet<string> seenScenes, SceneDirectoryData.SceneDirectoryEntry entry)
        {
            if (entry == null || IsIgnoredSceneEntry(entry))
            {
                return;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVarient = GetSceneVarient(entry);
            if (sceneVarient == null || sceneVarient.LevelConfig == null || IsHordeLevel(sceneVarient.LevelConfig) || string.IsNullOrEmpty(sceneVarient.SceneName))
            {
                return;
            }

            if (!seenScenes.Add(sceneVarient.SceneName))
            {
                return;
            }

            string displayName = MenuLevelHelper.GetLevelName(entry, false) + " [" + sceneVarient.SceneName + "]";
            SceneInfo scene = BuildSceneInfo(sceneVarient.SceneName, displayName, sceneVarient.LevelConfig);
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return;
            }

            scenes.Add(scene);
            SceneCache[scene.SceneName] = scene;
        }

        private static void AddScene(List<SceneInfo> scenes, HashSet<string> seenScenes, SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName) || scene.OrderedRecipes.Count == 0)
            {
                return;
            }

            if (!seenScenes.Add(scene.SceneName))
            {
                return;
            }

            scenes.Add(scene);
            SceneCache[scene.SceneName] = scene;
        }

        private static void AddCachedScenes(List<SceneInfo> scenes, HashSet<string> seenScenes)
        {
            SceneInfo[] cachedScenes = SceneCache.Values.ToArray();
            for (int i = 0; i < cachedScenes.Length; i++)
            {
                AddScene(scenes, seenScenes, cachedScenes[i]);
            }
        }

        private static void AddDIYScenes(List<SceneInfo> scenes, HashSet<string> seenScenes)
        {
            List<SceneInfo> diyScenes = GetDIYScenes();
            for (int i = 0; i < diyScenes.Count; i++)
            {
                SceneInfo scene = diyScenes[i];
                if (scene == null || string.IsNullOrEmpty(scene.SceneName))
                {
                    continue;
                }

                if (!seenScenes.Add(scene.SceneName))
                {
                    SceneCache[scene.SceneName] = scene;
                    continue;
                }

                scenes.Add(scene);
                SceneCache[scene.SceneName] = scene;
            }
        }

        private static bool IsIgnoredSceneEntry(SceneDirectoryData.SceneDirectoryEntry entry)
        {
            string label = entry != null ? entry.Label : null;
            return string.IsNullOrEmpty(label)
                || label.Contains("ThroneRoom")
                || label.Contains("Tutorial")
                || label.Contains("DLC07Battlements08");
        }

        private static bool TryGetCurrentSceneVariant(out SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVariant)
        {
            sceneVariant = null;
            GameSession gameSession = GameUtils.GetGameSession();
            if (gameSession == null || gameSession.LevelSettings == null)
            {
                return false;
            }

            sceneVariant = gameSession.LevelSettings.SceneDirectoryVarientEntry;
            return sceneVariant != null && !string.IsNullOrEmpty(sceneVariant.SceneName);
        }

        private static SceneDirectoryData GetProgressSceneDirectory()
        {
            GameSession gameSession = GameUtils.GetGameSession();
            if (gameSession == null || gameSession.Progress == null)
            {
                return null;
            }

            return gameSession.Progress.GetSceneDirectory();
        }

        private static SceneDirectoryData GetWorldMapSceneDirectory()
        {
            WorldMapFlowController worldMap = UnityEngine.Object.FindObjectOfType<WorldMapFlowController>();
            return worldMap != null ? worldMap.GetSceneDirectory() : null;
        }

        private static SceneDirectoryData GetSceneDirectory(GameSession sessionPrefab)
        {
            if (sessionPrefab == null)
            {
                return null;
            }

            GameProgress progress = sessionPrefab.gameObject.RequireComponentRecursive<GameProgress>();
            return progress != null ? progress.GetSceneDirectory() : null;
        }

        private static List<GameSession> GetFrontendSessionPrefabs(GameSession.GameType gameType)
        {
            List<GameSession> sessions = FrontendSessionBuffer;
            sessions.Clear();
            T17FrontendFlow frontendFlow = T17FrontendFlow.Instance;
            if (frontendFlow == null)
            {
                return sessions;
            }

            System.Reflection.FieldInfo field = gameType == GameSession.GameType.Competitive
                ? FrontendCompetitiveGameSessionPrefabsField
                : FrontendCoopGameSessionPrefabsField;
            object dataContainer = field != null ? field.GetValue(frontendFlow) : null;
            if (dataContainer == null)
            {
                return sessions;
            }

            System.Reflection.PropertyInfo allDataProperty = dataContainer.GetType().GetProperty("AllData");
            GameSession[] allData = allDataProperty != null ? allDataProperty.GetValue(dataContainer, null) as GameSession[] : null;
            if (allData == null)
            {
                return sessions;
            }

            for (int i = 0; i < allData.Length; i++)
            {
                GameSession session = allData[i];
                if (session != null && IsSessionAvailable(session))
                {
                    sessions.Add(session);
                }
            }

            return sessions;
        }

        private static bool IsSessionAvailable(GameSession session)
        {
            if (session == null || session.DLC < 0)
            {
                return session != null;
            }

            DLCManager dlcManager = GetDlcManager();
            if (dlcManager == null || dlcManager.AllDlc == null)
            {
                return true;
            }

            for (int i = 0; i < dlcManager.AllDlc.Count; i++)
            {
                DLCFrontendData dlc = dlcManager.AllDlc[i];
                if (dlc != null && dlc.m_DLCID == session.DLC)
                {
                    return dlcManager.IsDLCAvailable(dlc);
                }
            }

            return true;
        }

        private static DLCManager GetDlcManager()
        {
            if (cachedDlcManager == null || Time.frameCount >= nextDlcManagerLookupFrame)
            {
                cachedDlcManager = UnityEngine.Object.FindObjectOfType<DLCManager>();
                nextDlcManagerLookupFrame = Time.frameCount + (cachedDlcManager != null ? ControllerLookupIntervalFrames : ControllerLookupRetryIntervalFrames);
            }

            return cachedDlcManager;
        }

        private static SceneDirectoryData.PerPlayerCountDirectoryEntry GetSceneVarient(SceneDirectoryData.SceneDirectoryEntry entry)
        {
            int playerCount = 1;
            if (ServerUserSystem.m_Users != null && ServerUserSystem.m_Users.Count > 0)
            {
                playerCount = ServerUserSystem.m_Users.Count;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVarient = entry.GetSceneVarient(playerCount);
            if (sceneVarient == null && entry.SceneVarients != null && entry.SceneVarients.Length > 0)
            {
                sceneVarient = entry.SceneVarients[0];
            }

            return sceneVarient;
        }

        private static SceneInfo BuildSceneInfo(string sceneName, string displayName, LevelConfigBase levelConfig)
        {
            if (levelConfig == null)
            {
                return null;
            }

            SceneInfo scene = new SceneInfo();
            scene.SceneName = sceneName;
            scene.DisplayName = displayName;

            List<OrderDefinitionNode> recipes = levelConfig.GetAllRecipes();
            if (recipes == null)
            {
                return scene;
            }

            for (int i = 0; i < recipes.Count; i++)
            {
                EnsureRecipe(scene, recipes[i]);
            }

            CampaignLevelConfigBase campaignLevelConfig = levelConfig as CampaignLevelConfigBase;
            if (campaignLevelConfig != null)
            {
                DynamicRoundData dynamicRoundData = campaignLevelConfig.GetRoundData() as DynamicRoundData;
                if (dynamicRoundData != null && dynamicRoundData.Phases != null && dynamicRoundData.Phases.Length > 0)
                {
                    scene.PhaseRecipeIds = new List<int>[dynamicRoundData.Phases.Length];
                    for (int i = 0; i < dynamicRoundData.Phases.Length; i++)
                    {
                        scene.PhaseRecipeIds[i] = new List<int>();
                        RecipeList phaseRecipes = dynamicRoundData.Phases[i].Recipes;
                        if (phaseRecipes == null || phaseRecipes.m_recipes == null)
                        {
                            continue;
                        }

                        for (int j = 0; j < phaseRecipes.m_recipes.Length; j++)
                        {
                            RecipeList.Entry entry = phaseRecipes.m_recipes[j];
                            if (entry != null && entry.m_order != null)
                            {
                                scene.PhaseRecipeIds[i].Add(entry.m_order.m_uID);
                            }
                        }
                    }
                }
            }

            return scene;
        }

        private static void EnsureRecipe(SceneInfo scene, OrderDefinitionNode recipe)
        {
            if (scene == null || recipe == null || scene.RecipesById.ContainsKey(recipe.m_uID))
            {
                return;
            }

            RecipeInfo info = new RecipeInfo();
            info.Id = recipe.m_uID;
            info.InternalName = recipe.name;
            info.EnglishName = DishNameCatalog.GetEnglishName(recipe.name);
            info.ChineseName = DishNameCatalog.GetChineseFullName(recipe.name);
            info.CategoryName = DishNameCatalog.GetCategoryName(recipe.name);
            info.CategoryTier = DishNameCatalog.GetCategoryTier(recipe.name);
            info.Definition = recipe;
            scene.RecipesById.Add(info.Id, info);
            scene.OrderedRecipes.Add(info);
            scene.AllRecipeIds.Add(info.Id);
            DishNameCatalog.RecordRecipe(scene.SceneName, recipe.m_uID, recipe.name);
        }

        private static bool TryGetCurrentSceneInfo(out SceneInfo scene)
        {
            if (Time.frameCount == cachedCurrentSceneInfoFrame)
            {
                scene = cachedCurrentSceneInfo;
                return cachedCurrentSceneInfoValid;
            }

            scene = null;
            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVariant;
            if (!TryGetCurrentSceneVariant(out sceneVariant))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = null;
                cachedCurrentSceneInfoValid = false;
                return false;
            }

            string sceneName = sceneVariant.SceneName;
            LevelConfigBase levelConfig = sceneVariant.LevelConfig ?? GameUtils.GetLevelConfig();
            if (SceneCache.TryGetValue(sceneName, out scene)
                && scene != null
                && (scene.OrderedRecipes.Count > 0 || levelConfig == null))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = scene;
                cachedCurrentSceneInfoValid = true;
                return true;
            }

            if (levelConfig == null || IsHordeLevel(levelConfig))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = null;
                cachedCurrentSceneInfoValid = false;
                return false;
            }

            string displayName = scene != null && !string.IsNullOrEmpty(scene.DisplayName) ? scene.DisplayName : sceneName;
            scene = BuildSceneInfo(sceneName, displayName, levelConfig);
            if (scene != null)
            {
                SceneCache[scene.SceneName] = scene;
            }

            cachedCurrentSceneInfoFrame = Time.frameCount;
            cachedCurrentSceneInfo = scene;
            cachedCurrentSceneInfoValid = scene != null;
            return cachedCurrentSceneInfoValid;
        }

        private static RunInfo EnsureRun(SceneInfo scene)
        {
            if (currentRun == null || !string.Equals(currentRun.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                currentRun = new RunInfo();
                currentRun.SceneName = scene.SceneName;
                currentRun.CurrentPhaseIndex = 0;
                InitializeRunCounts(currentRun, scene);
                InvalidateProbabilityMap();
                return currentRun;
            }

            if (currentRun.AddedCounts.Count < scene.OrderedRecipes.Count || currentRun.ServedCounts.Count < scene.OrderedRecipes.Count)
            {
                InitializeRunCounts(currentRun, scene);
                InvalidateProbabilityMap();
            }

            return currentRun;
        }

        private static void InitializeRunCounts(RunInfo run, SceneInfo scene)
        {
            if (run == null || scene == null)
            {
                return;
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                int recipeId = scene.OrderedRecipes[i].Id;
                if (!run.AddedCounts.ContainsKey(recipeId))
                {
                    run.AddedCounts.Add(recipeId, 0);
                }

                if (!run.ServedCounts.ContainsKey(recipeId))
                {
                    run.ServedCounts.Add(recipeId, 0);
                }
            }
        }

        private static int GetCount(Dictionary<int, int> counts, int recipeId)
        {
            int value;
            return counts != null && counts.TryGetValue(recipeId, out value) ? value : 0;
        }

        private static int GetMenuOrder(Dictionary<int, int> menuOrders, int recipeId)
        {
            int value;
            return menuOrders != null && menuOrders.TryGetValue(recipeId, out value) ? value : int.MaxValue;
        }

        private static OverlayRow GetOrCreateOverlayRow(int index)
        {
            while (OverlayRowsBuffer.Count <= index)
            {
                OverlayRowsBuffer.Add(new OverlayRow());
            }

            return OverlayRowsBuffer[index];
        }

        private static OverlayRenderRow GetOrCreateOverlayRenderRow(int index)
        {
            while (OverlayRenderRowsBuffer.Count <= index)
            {
                OverlayRenderRowsBuffer.Add(new OverlayRenderRow());
            }

            return OverlayRenderRowsBuffer[index];
        }

        private static bool IsHordeLevel(LevelConfigBase levelConfig)
        {
            return levelConfig != null && string.Equals(levelConfig.GetType().Name, "HordeLevelConfig", StringComparison.Ordinal);
        }

    }
}
