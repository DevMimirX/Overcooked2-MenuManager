// Discovers base-game and DIY scenes and owns scene/catalog hydration. Settings
// refreshes reuse discovery buffers; DIY recipes are loaded lazily from the
// optional mod's authoritative frontend metadata.
using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;
using OC2MenuManager.Infrastructure;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private static List<SceneDirectoryData.SceneDirectoryEntry> GetAvailableSceneEntries()
        {
            List<SceneDirectoryData.SceneDirectoryEntry> entries = AvailableSceneEntriesBuffer;
            entries.Clear();

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

        private static void AddEntries(
            List<SceneDirectoryData.SceneDirectoryEntry> entryList,
            IList<SceneDirectoryData.SceneDirectoryEntry> entries)
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

            AddEntries(entryList, sceneDirectory.Scenes);
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

            List<SceneInfo> scenes = DIYScenesRefreshBuffer;
            scenes.Clear();
            AddDIYScenesFromRuntimeManager(scenes);

            CachedDIYScenes.Clear();
            CachedDIYScenes.AddRange(scenes);
            nextDIYSceneRefreshFrame = Time.frameCount + 120;
            return CachedDIYScenes;
        }

        private static void AddDIYScenesFromRuntimeManager(List<SceneInfo> scenes)
        {
            string error;
            if (!OptionalRecipeAdapters.TryGetDIYLevels(DIYLevelDescriptorsBuffer, out error))
            {
                return;
            }

            for (int i = 0; i < DIYLevelDescriptorsBuffer.Count; i++)
            {
                DIYLevelDescriptor descriptor = DIYLevelDescriptorsBuffer[i];
                if (descriptor == null || string.IsNullOrEmpty(descriptor.SceneName))
                {
                    continue;
                }

                SceneInfo scene;
                if (!SceneCache.TryGetValue(descriptor.SceneName, out scene) || scene == null)
                {
                    scene = new SceneInfo();
                    scene.SceneName = descriptor.SceneName;
                }

                object previousLevelInfo = scene.DIYLevelInfo;
                bool metadataChanged = !ReferenceEquals(previousLevelInfo, descriptor.LevelInfo);
                bool metadataReplaced = previousLevelInfo != null && metadataChanged;
                if (metadataReplaced && ResetSceneRecipeCatalog(scene))
                {
                    NotifyRecipeCatalogChanged(scene);
                }
                scene.DisplayName = UseChinese()
                    ? descriptor.ChineseDisplayName
                    : descriptor.EnglishDisplayName;
                scene.IsDIY = true;
                scene.DIYLevelInfo = descriptor.LevelInfo;
                bool retryFailedHydration = scene.DIYHydrationAttempted
                    && scene.OrderedRecipes.Count == 0
                    && !string.IsNullOrEmpty(scene.DIYHydrationError);
                if (metadataChanged || retryFailedHydration)
                {
                    scene.DIYHydrationAttempted = false;
                    if (metadataChanged)
                    {
                        scene.DIYHydrationError = null;
                    }
                }

                AddDIYSceneIfMissing(scenes, scene);
            }
        }

        private static void AddDIYSceneIfMissing(List<SceneInfo> scenes, SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName))
            {
                return;
            }

            for (int i = 0; i < scenes.Count; i++)
            {
                SceneInfo existing = scenes[i];
                if (existing != null
                    && string.Equals(existing.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            scenes.Add(scene);
        }

        private static bool EnsureDIYSceneHydrated(SceneInfo scene, bool forceRefresh)
        {
            if (scene == null || !scene.IsDIY)
            {
                return scene != null;
            }

            if (forceRefresh)
            {
                scene.DIYHydrationAttempted = false;
                scene.DIYHydrationError = null;
            }

            if (scene.DIYHydrationAttempted)
            {
                return scene.OrderedRecipes.Count > 0;
            }

            scene.DIYHydrationAttempted = true;
            if (scene.DIYLevelInfo == null)
            {
                scene.DIYHydrationError = "DIY Level metadata is still loading or unavailable.";
                return false;
            }

            string error;
            if (!OptionalRecipeAdapters.TryGetDIYRecipes(scene.DIYLevelInfo, DIYRecipeDescriptorsBuffer, out error))
            {
                scene.DIYHydrationError = error;
                return false;
            }

            bool changed = false;
            RuntimeRecipeIdsBuffer.Clear();
            for (int i = 0; i < DIYRecipeDescriptorsBuffer.Count; i++)
            {
                DIYRecipeDescriptor descriptor = DIYRecipeDescriptorsBuffer[i];
                if (descriptor == null || descriptor.Id == 0)
                {
                    continue;
                }

                RuntimeRecipeIdsBuffer.Add(descriptor.Id);
                changed |= descriptor.Definition != null
                    ? EnsureRecipe(scene, descriptor.Definition)
                    : EnsureRecipeMetadata(scene, descriptor.Id, descriptor.InternalName);
            }

            changed |= UpdateRecipeSourceIds(scene, scene.DIYRecipeIds, RuntimeRecipeIdsBuffer);

            scene.DIYHydrationError = scene.OrderedRecipes.Count > 0
                ? null
                : "The DIY level metadata did not contain any usable recipes.";
            if (changed)
            {
                NotifyRecipeCatalogChanged(scene);
            }

            return scene.OrderedRecipes.Count > 0;
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
            CachedSceneInfosBuffer.Clear();
            CachedSceneInfosBuffer.AddRange(SceneCache.Values);
            for (int i = 0; i < CachedSceneInfosBuffer.Count; i++)
            {
                AddScene(scenes, seenScenes, CachedSceneInfosBuffer[i]);
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
            if (cachedWorldMapFlowController == null)
            {
                cachedWorldMapFlowController = UnityEngine.Object.FindObjectOfType<WorldMapFlowController>();
            }

            return cachedWorldMapFlowController != null ? cachedWorldMapFlowController.GetSceneDirectory() : null;
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
            if (cachedDlcManager == null && Time.frameCount >= nextDlcManagerLookupFrame)
            {
                cachedDlcManager = UnityEngine.Object.FindObjectOfType<DLCManager>();
                nextDlcManagerLookupFrame = Time.frameCount + ControllerLookupRetryIntervalFrames;
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
            MergeLevelConfigIntoScene(scene, levelConfig);
            return scene;
        }

        private static bool MergeLevelConfigIntoScene(SceneInfo scene, LevelConfigBase levelConfig)
        {
            if (scene == null || levelConfig == null)
            {
                return false;
            }

            scene.LevelConfigName = levelConfig.name;
            scene.RuntimeLevelConfig = levelConfig;
            bool changed = false;

            List<OrderDefinitionNode> recipes = levelConfig.GetAllRecipes();
            RuntimeRecipeIdsBuffer.Clear();
            CampaignLevelConfigBase campaignLevelConfig = levelConfig as CampaignLevelConfigBase;
            RoundData roundData = campaignLevelConfig != null ? campaignLevelConfig.GetRoundData() as RoundData : null;
            ScriptedRoundData scriptedRoundData = roundData as ScriptedRoundData;
            if (scriptedRoundData != null && scriptedRoundData.m_manualOrder != null)
            {
                for (int i = 0; i < scriptedRoundData.m_manualOrder.Length; i++)
                {
                    RecipeList.Entry entry = scriptedRoundData.m_manualOrder[i];
                    OrderDefinitionNode manualRecipe = entry != null ? entry.m_order : null;
                    if (manualRecipe == null || manualRecipe.m_uID == 0)
                    {
                        continue;
                    }

                    RuntimeRecipeIdsBuffer.Add(manualRecipe.m_uID);
                    changed |= EnsureRecipe(scene, manualRecipe);
                }
            }

            if (recipes != null)
            {
                for (int i = 0; i < recipes.Count; i++)
                {
                    OrderDefinitionNode recipe = recipes[i];
                    if (recipe == null || recipe.m_uID == 0)
                    {
                        continue;
                    }

                    RuntimeRecipeIdsBuffer.Add(recipe.m_uID);
                    changed |= EnsureRecipe(scene, recipe);
                }
            }

            changed |= UpdateRecipeSourceIds(scene, scene.RuntimeRecipeIds, RuntimeRecipeIdsBuffer);

            List<int>[] previousPhaseRecipeIds = scene.PhaseRecipeIds;
            scene.PhaseRecipeIds = null;
            if (campaignLevelConfig != null)
            {
                DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
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
                            if (entry != null && entry.m_order != null && !scene.PhaseRecipeIds[i].Contains(entry.m_order.m_uID))
                            {
                                scene.PhaseRecipeIds[i].Add(entry.m_order.m_uID);
                            }
                        }
                    }
                }
            }

            changed |= !ArePhaseRecipeListsEqual(previousPhaseRecipeIds, scene.PhaseRecipeIds);
            return changed;
        }

        private static bool EnsureRecipe(SceneInfo scene, OrderDefinitionNode recipe)
        {
            if (scene == null || recipe == null || recipe.m_uID == 0)
            {
                return false;
            }

            RecipeInfo existing;
            if (scene.RecipesById.TryGetValue(recipe.m_uID, out existing) && existing != null)
            {
                RecipeCatalogMergeAction action = RecipeCatalogMergePolicy.Evaluate(
                    true,
                    existing.Definition != null,
                    true,
                    !ReferenceEquals(existing.Definition, recipe),
                    !string.Equals(existing.InternalName, recipe.name, StringComparison.Ordinal));
                if (action == RecipeCatalogMergeAction.None)
                {
                    return false;
                }

                PopulateRecipeInfo(existing, recipe.m_uID, recipe.name, recipe);
                DishNameCatalog.RecordRecipe(scene.SceneName, recipe.m_uID, recipe.name);
                return true;
            }

            RecipeInfo info = new RecipeInfo();
            PopulateRecipeInfo(info, recipe.m_uID, recipe.name, recipe);
            scene.RecipesById.Add(info.Id, info);
            scene.OrderedRecipes.Add(info);
            scene.AllRecipeIds.Add(info.Id);
            DishNameCatalog.RecordRecipe(scene.SceneName, recipe.m_uID, recipe.name);
            return true;
        }

        private static bool EnsureRecipeMetadata(SceneInfo scene, int recipeId, string internalName)
        {
            if (scene == null || recipeId == 0)
            {
                return false;
            }

            RecipeInfo existing;
            if (scene.RecipesById.TryGetValue(recipeId, out existing) && existing != null)
            {
                RecipeCatalogMergeAction action = RecipeCatalogMergePolicy.Evaluate(
                    true,
                    existing.Definition != null,
                    false,
                    false,
                    !string.Equals(existing.InternalName, internalName, StringComparison.Ordinal));
                if (action == RecipeCatalogMergeAction.None)
                {
                    return false;
                }

                PopulateRecipeInfo(existing, recipeId, internalName, null);
                DishNameCatalog.RecordRecipe(scene.SceneName, recipeId, internalName);
                return true;
            }

            RecipeInfo info = new RecipeInfo();
            PopulateRecipeInfo(info, recipeId, internalName, null);
            scene.RecipesById.Add(info.Id, info);
            scene.OrderedRecipes.Add(info);
            scene.AllRecipeIds.Add(info.Id);
            DishNameCatalog.RecordRecipe(scene.SceneName, recipeId, internalName);
            return true;
        }

        private static bool UpdateRecipeSourceIds(SceneInfo scene, HashSet<int> sourceIds, HashSet<int> desiredIds)
        {
            if (scene == null || sourceIds == null || desiredIds == null)
            {
                return false;
            }

            StaleRecipeIdsBuffer.Clear();
            foreach (int recipeId in sourceIds)
            {
                if (!desiredIds.Contains(recipeId))
                {
                    StaleRecipeIdsBuffer.Add(recipeId);
                }
            }

            sourceIds.Clear();
            foreach (int recipeId in desiredIds)
            {
                sourceIds.Add(recipeId);
            }

            bool changed = false;
            for (int i = 0; i < StaleRecipeIdsBuffer.Count; i++)
            {
                changed |= RemoveRecipeIfUnreferenced(scene, StaleRecipeIdsBuffer[i]);
            }

            StaleRecipeIdsBuffer.Clear();
            return changed;
        }

        private static bool RemoveRecipeIfUnreferenced(SceneInfo scene, int recipeId)
        {
            if (scene == null
                || scene.DIYRecipeIds.Contains(recipeId)
                || scene.RuntimeRecipeIds.Contains(recipeId)
                || scene.ExtensionRecipeIds.Contains(recipeId))
            {
                return false;
            }

            RecipeInfo recipe;
            if (!scene.RecipesById.TryGetValue(recipeId, out recipe))
            {
                return false;
            }

            scene.RecipesById.Remove(recipeId);
            scene.AllRecipeIds.Remove(recipeId);
            scene.OrderedRecipes.Remove(recipe);
            if (scene.PhaseRecipeIds != null)
            {
                for (int i = 0; i < scene.PhaseRecipeIds.Length; i++)
                {
                    if (scene.PhaseRecipeIds[i] != null)
                    {
                        scene.PhaseRecipeIds[i].Remove(recipeId);
                    }
                }
            }

            return true;
        }

        private static bool ResetSceneRecipeCatalog(SceneInfo scene)
        {
            if (scene == null)
            {
                return false;
            }

            bool changed = scene.OrderedRecipes.Count > 0 || scene.PhaseRecipeIds != null;
            scene.AllRecipeIds.Clear();
            scene.OrderedRecipes.Clear();
            scene.RecipesById.Clear();
            scene.DIYRecipeIds.Clear();
            scene.RuntimeRecipeIds.Clear();
            scene.ExtensionRecipeIds.Clear();
            scene.PhaseRecipeIds = null;
            scene.RuntimeLevelConfig = null;
            scene.LevelConfigName = null;
            return changed;
        }

        private static void PopulateRecipeInfo(RecipeInfo info, int recipeId, string internalName, OrderDefinitionNode definition)
        {
            string resolvedName = string.IsNullOrEmpty(internalName) ? "Recipe_" + recipeId : internalName;
            info.Id = recipeId;
            info.InternalName = resolvedName;
            info.EnglishName = DishNameCatalog.GetEnglishName(resolvedName);
            info.ChineseName = DishNameCatalog.GetChineseFullName(resolvedName);
            info.CategoryName = DishNameCatalog.GetCategoryName(resolvedName);
            info.CategoryTier = DishNameCatalog.GetCategoryTier(resolvedName);
            info.Definition = definition;
            info.SimplifiedDefinition = null;
            info.SimplifiedUnwrappedDefinition = null;
        }

        private static bool ArePhaseRecipeListsEqual(List<int>[] left, List<int>[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return left == null && right == null;
            }

            for (int i = 0; i < left.Length; i++)
            {
                List<int> leftPhase = left[i];
                List<int> rightPhase = right[i];
                int leftCount = leftPhase != null ? leftPhase.Count : 0;
                int rightCount = rightPhase != null ? rightPhase.Count : 0;
                if (leftCount != rightCount)
                {
                    return false;
                }

                for (int j = 0; j < leftCount; j++)
                {
                    if (leftPhase[j] != rightPhase[j])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void NotifyRecipeCatalogChanged(SceneInfo scene)
        {
            if (scene == null)
            {
                return;
            }

            scene.CatalogRevision++;
            foreach (RunInfo run in RunsByTeam.Values)
            {
                if (run != null && string.Equals(run.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    InitializeRunCounts(run, scene);
                }
            }

            preparedCandidateRecipeIdsDirty = true;
            InvalidateProbabilityMap();
            InvalidatePreparedCandidates(true);
            InvalidateOverlay();
            InvalidateTicketWidgets();
        }

        private static bool TryGetCurrentSceneInfo(out SceneInfo scene)
        {
            if (cachedCurrentSceneInfoValid
                && cachedCurrentSceneInfo != null
                && cachedCurrentSceneInfo.RuntimeLevelConfig != null
                && cachedCurrentSceneInfo.OrderedRecipes.Count > 0)
            {
                scene = cachedCurrentSceneInfo;
                return true;
            }

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
            if (SceneCache.TryGetValue(sceneName, out scene) && scene != null)
            {
                if (levelConfig != null && !ReferenceEquals(scene.RuntimeLevelConfig, levelConfig))
                {
                    if (MergeLevelConfigIntoScene(scene, levelConfig))
                    {
                        NotifyRecipeCatalogChanged(scene);
                    }
                }

                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = scene;
                cachedCurrentSceneInfoValid = scene.OrderedRecipes.Count > 0 || levelConfig == null;
                return cachedCurrentSceneInfoValid;
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
            return EnsureRun(scene, TeamID.One);
        }

        private static RunInfo EnsureRun(SceneInfo scene, TeamID teamId)
        {
            RunInfo run;
            if (!RunsByTeam.TryGetValue(teamId, out run)
                || run == null
                || !string.Equals(run.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                run = new RunInfo();
                run.SceneName = scene.SceneName;
                run.TeamId = teamId;
                run.CurrentPhaseIndex = 0;
                run.ReconstructionComplete = ReconstructionReadyTeams.Contains(teamId);
                RunsByTeam[teamId] = run;
                InitializeRunCounts(run, scene);
                InvalidateProbabilityMap();
                return run;
            }

            if (run.AddedCounts.Count < scene.OrderedRecipes.Count || run.ServedCounts.Count < scene.OrderedRecipes.Count)
            {
                InitializeRunCounts(run, scene);
                InvalidateProbabilityMap();
            }

            return run;
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

        private static OverlayRenderRow GetOrCreateOverlayRenderRow(int index)
        {
            while (OverlayRenderRowsBuffer.Count <= index)
            {
                OverlayRenderRowsBuffer.Add(new OverlayRenderRow());
            }

            OverlayRenderRow row = OverlayRenderRowsBuffer[index];
            row.Reset();
            return row;
        }

        private static bool IsHordeLevel(LevelConfigBase levelConfig)
        {
            return levelConfig != null && string.Equals(levelConfig.GetType().Name, "HordeLevelConfig", StringComparison.Ordinal);
        }

    }
}
