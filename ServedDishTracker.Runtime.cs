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
using HostUtilities;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private static void BindCategoryTierConfigEntries()
        {
            CategoryTierEntriesByKey.Clear();
            string[] categoryKeys = DishNameCatalog.GetOrderedCategoryKeys();
            for (int i = 0; i < categoryKeys.Length; i++)
            {
                string categoryKey = categoryKeys[i];
                if (string.IsNullOrEmpty(categoryKey))
                {
                    continue;
                }

                string configKey = GetCategoryTierConfigKey(categoryKey);
                int defaultTier = DishNameCatalog.GetDefaultCategoryTierByKey(categoryKey);
                ConfigEntry<int> entry = _MODEntry.SettingsConfig.Bind<int>(
                    TierSection,
                    configKey,
                    defaultTier,
                    new ConfigDescription(
                        DishNameCatalog.GetCategoryNameByKey(categoryKey) + "的排序层级。数字越小越靠前。",
                        new AcceptableValueRange<int>(MinCategoryTierValue, MaxCategoryTierValue)));
                CategoryTierEntriesByKey[categoryKey] = entry;
            }
        }

        private static string GetCategoryTierConfigKey(string categoryKey)
        {
            return "层级-" + DishNameCatalog.GetCategoryNameByKey(categoryKey);
        }

        private static void ApplyConfiguredCategoryTierOverrides()
        {
            foreach (KeyValuePair<string, ConfigEntry<int>> pair in CategoryTierEntriesByKey)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                DishNameCatalog.SetCategoryTierOverride(pair.Key, pair.Value.Value);
            }

            RefreshCachedSceneCategoryTiers();
        }

        private static void ApplyCategoryTierOverride(string categoryKey, int tier)
        {
            ConfigEntry<int> entry;
            if (string.IsNullOrEmpty(categoryKey) || !CategoryTierEntriesByKey.TryGetValue(categoryKey, out entry) || entry == null)
            {
                return;
            }

            int clampedTier = Mathf.Clamp(tier, MinCategoryTierValue, MaxCategoryTierValue);
            if (entry.Value == clampedTier)
            {
                return;
            }

            entry.Value = clampedTier;
            DishNameCatalog.SetCategoryTierOverride(categoryKey, clampedTier);
            RefreshCachedSceneCategoryTiers();
            InvalidateReferenceTickets();
            InvalidateOverlay();
        }

        private static void ResetAllCategoryTierOverrides()
        {
            bool changed = false;
            foreach (KeyValuePair<string, ConfigEntry<int>> pair in CategoryTierEntriesByKey)
            {
                ConfigEntry<int> entry = pair.Value;
                if (entry == null)
                {
                    continue;
                }

                int defaultTier = DishNameCatalog.GetDefaultCategoryTierByKey(pair.Key);
                if (entry.Value != defaultTier)
                {
                    entry.Value = defaultTier;
                    changed = true;
                }

                DishNameCatalog.SetCategoryTierOverride(pair.Key, defaultTier);
            }

            if (!changed)
            {
                return;
            }

            RefreshCachedSceneCategoryTiers();
            InvalidateReferenceTickets();
            InvalidateOverlay();
        }

        private static void RefreshCachedSceneCategoryTiers()
        {
            foreach (SceneInfo scene in SceneCache.Values)
            {
                RefreshSceneCategoryTiers(scene);
            }

            for (int i = 0; i < KnownScenes.Count; i++)
            {
                RefreshSceneCategoryTiers(KnownScenes[i]);
            }

            for (int i = 0; i < CachedDIYScenes.Count; i++)
            {
                RefreshSceneCategoryTiers(CachedDIYScenes[i]);
            }

            RefreshSceneCategoryTiers(cachedCurrentSceneInfo);
        }

        private static void RefreshSceneCategoryTiers(SceneInfo scene)
        {
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return;
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null || string.IsNullOrEmpty(recipe.InternalName))
                {
                    continue;
                }

                recipe.CategoryTier = DishNameCatalog.GetCategoryTier(recipe.InternalName);
            }
        }

        private static void InvalidateOverlay()
        {
            InvalidateOverlayRowsCache();
            overlayDirty = true;
            int targetFrame = Time.frameCount + (IsInActiveRound() ? OverlayRefreshIntervalFrames : 0);
            if (nextOverlayRefreshFrame == 0 || targetFrame < nextOverlayRefreshFrame)
            {
                nextOverlayRefreshFrame = targetFrame;
            }
        }

        private static void InvalidateProbabilityMap()
        {
            InvalidateOverlayRowsCache();
            probabilityMapDirty = true;
            probabilityMapSceneName = string.Empty;
            InvalidateReferenceTickets();
        }

        private static void InvalidateTicketWidgets()
        {
            if (!IsMenuTicketTintEnabled() && !ticketWidgetTintActive)
            {
                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
                return;
            }

            ticketWidgetsDirty = true;
            int targetFrame = Time.frameCount + (IsInActiveRound() ? TicketWidgetRefreshDelayFrames : 0);
            if (nextTicketWidgetRefreshFrame == 0 || targetFrame < nextTicketWidgetRefreshFrame)
            {
                nextTicketWidgetRefreshFrame = targetFrame;
            }
        }

        private static void InvalidateReferenceTickets()
        {
            InvalidateOverlayRowsCache();
            referenceTicketsDirty = true;
            int targetFrame = IsInActiveRound() ? Time.frameCount : 0;
            if (nextReferenceTicketSyncFrame == 0 || targetFrame < nextReferenceTicketSyncFrame)
            {
                nextReferenceTicketSyncFrame = targetFrame;
            }
        }

        private static void InvalidateOverlayRowsCache()
        {
            unchecked
            {
                overlayRowsVersion++;
            }
        }

        private static bool IsPreparedTrackingEnabled()
        {
            return preparedTrackingEnabled != null && preparedTrackingEnabled.Value;
        }

        private static bool IsMenuTicketTintEnabled()
        {
            return menuTicketTintEnabled != null && menuTicketTintEnabled.Value;
        }

        private static void ClearOnMenuCounts()
        {
            CurrentOnMenuCounts.Clear();
            currentOnMenuCountsSceneName = string.Empty;
            currentOnMenuCountsDirty = true;
            InvalidateProbabilityMap();
            InvalidatePreparedCandidates(false);
        }

        private static void ClearPreparedState()
        {
            bool hadPreparedState = PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0;
            foreach (PreparedSourceState source in PreparedSourcesByInstanceId.Values)
            {
                if (source != null && source.Provider != null && source.Callback != null)
                {
                    try
                    {
                        source.Provider.UnregisterOrderCompositionChangedCallback(source.Callback);
                    }
                    catch
                    {
                    }
                }
            }

            PreparedSourcesByInstanceId.Clear();
            PreparedSourceIdsByGameObjectId.Clear();
            PreparedCookStateBySourceId.Clear();
            PreparedSourceComponentByHandlerId.Clear();
            PreparedCountsByRecipe.Clear();
            DirtyPreparedSourceIds.Clear();
            PreparedSourceRefreshBuffer.Clear();
            PreparedCandidateRecipeIdsBuffer.Clear();
            nextPreparedSourceRefreshFrame = 0;
            nextPreparedSourcePruneFrame = 0;
            nextPreparedBootstrapFrame = 0;
            nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackDelayFrames;
            preparedSourceBootstrapComplete = true;
            preparedSourceBootstrapStage = 0;
            preparedSourceSceneName = string.Empty;
            preparedCandidateSceneName = string.Empty;
            preparedCandidateRecipeIdsDirty = true;
            InvalidateReferenceTickets();
            if (hadPreparedState)
            {
                InvalidateTicketWidgets();
            }
        }

        private static void ClearTicketWidgetState()
        {
            foreach (TicketWidgetState state in TicketWidgetsByInstanceId.Values)
            {
                RestoreTicketWidgetTint(state);
            }

            TicketWidgetsByInstanceId.Clear();
            TicketWidgetsBuffer.Clear();
            ticketWidgetsDirty = false;
            ticketWidgetTintActive = false;
            nextTicketWidgetRefreshFrame = 0;
        }

        private static void ClearReferenceTickets()
        {
            for (int i = ReferenceTicketStates.Count - 1; i >= 0; i--)
            {
                RemoveReferenceTicketAt(i);
            }

            referenceTicketsDirty = false;
            nextReferenceTicketSyncFrame = 0;
        }

        private static void ClearReferenceTicketsForFlow(int flowInstanceId)
        {
            for (int i = ReferenceTicketStates.Count - 1; i >= 0; i--)
            {
                ReferenceTicketState state = ReferenceTicketStates[i];
                if (state != null && state.FlowInstanceId == flowInstanceId)
                {
                    RemoveReferenceTicketAt(i);
                }
            }
        }

        private static void RemoveReferenceTicketAt(int index)
        {
            RemoveReferenceTicketAt(index, false);
        }

        private static void RemoveReferenceTicketAt(int index, bool animateServedStyle)
        {
            if (index < 0 || index >= ReferenceTicketStates.Count)
            {
                return;
            }

            ReferenceTicketState state = ReferenceTicketStates[index];
            ReferenceTicketStates.RemoveAt(index);
            if (state == null)
            {
                return;
            }

            try
            {
                if (state.Flow != null && state.Widget != null)
                {
                    RecipeFlowGUI.RecipeWidgetData widgetData = state.Flow.GetData(state.Token);
                    if (widgetData != null && animateServedStyle)
                    {
                        TicketWidgetState ticketState;
                        if (TicketWidgetsByInstanceId.TryGetValue(state.Widget.GetInstanceID(), out ticketState) && ticketState != null)
                        {
                            ticketState.IsReferenceTicket = true;
                            ticketState.IsDyingReferenceTicket = true;
                        }

                        state.Flow.RemoveElement(state.Token, new ReferenceTicketDestroyAnimation(GetReferenceTicketDestroyAnimationColor()));
                        return;
                    }

                    IList activeWidgets = RecipeFlowWidgetsField != null ? RecipeFlowWidgetsField.GetValue(state.Flow) as IList : null;
                    if (widgetData != null && activeWidgets != null)
                    {
                        activeWidgets.Remove(widgetData);
                    }

                    bool[] occupiedTables = RecipeFlowOccupiedTablesField != null ? RecipeFlowOccupiedTablesField.GetValue(state.Flow) as bool[] : null;
                    int tableNumber = state.Widget.GetTableNumber();
                    if (occupiedTables != null && tableNumber >= 0 && tableNumber < occupiedTables.Length)
                    {
                        occupiedTables[tableNumber] = false;
                    }

                    UnregisterTicketWidget(state.Widget);
                    UnityEngine.Object.Destroy(state.Widget.gameObject);
                }
                else if (state.Widget != null)
                {
                    UnregisterTicketWidget(state.Widget);
                    UnityEngine.Object.Destroy(state.Widget.gameObject);
                }
            }
            catch
            {
            }
        }

        private static void PruneReferenceTicketStates()
        {
            for (int i = ReferenceTicketStates.Count - 1; i >= 0; i--)
            {
                ReferenceTicketState state = ReferenceTicketStates[i];
                if (state == null || state.Flow == null || state.Widget == null)
                {
                    RemoveReferenceTicketAt(i);
                    continue;
                }

                try
                {
                    if (state.Flow.GetData(state.Token) == null)
                    {
                        RemoveReferenceTicketAt(i);
                    }
                }
                catch
                {
                    RemoveReferenceTicketAt(i);
                }
            }
        }

        private static void RestoreAllTicketWidgetTints()
        {
            foreach (TicketWidgetState state in TicketWidgetsByInstanceId.Values)
            {
                RestoreTicketWidgetTint(state);
            }

            ticketWidgetTintActive = false;
        }

        private static void IncrementOnMenuCount(string sceneName, int recipeId)
        {
            EnsureOnMenuCountScene(sceneName);
            int previousCount = GetCount(CurrentOnMenuCounts, recipeId);
            CurrentOnMenuCounts[recipeId] = previousCount + 1;
            currentOnMenuCountsDirty = false;
            InvalidatePreparedCandidates(previousCount == 0);
        }

        private static void DecrementOnMenuCount(string sceneName, int recipeId)
        {
            EnsureOnMenuCountScene(sceneName);
            int previousCount = GetCount(CurrentOnMenuCounts, recipeId);
            int nextValue = Math.Max(0, previousCount - 1);
            if (nextValue > 0)
            {
                CurrentOnMenuCounts[recipeId] = nextValue;
            }
            else
            {
                CurrentOnMenuCounts.Remove(recipeId);
            }

            currentOnMenuCountsDirty = false;
            InvalidatePreparedCandidates(previousCount > 0 && nextValue == 0);
        }

        private static void InvalidatePreparedCandidates(bool queueAllPreparedSources)
        {
            preparedCandidateRecipeIdsDirty = true;
            InvalidateReferenceTickets();
            if (queueAllPreparedSources && PreparedSourcesByInstanceId.Count > 0)
            {
                foreach (int instanceId in PreparedSourcesByInstanceId.Keys)
                {
                    DirtyPreparedSourceIds.Add(instanceId);
                }

                int targetFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
                if (nextPreparedSourceRefreshFrame == 0 || targetFrame < nextPreparedSourceRefreshFrame)
                {
                    nextPreparedSourceRefreshFrame = targetFrame;
                }
            }
        }

        private static void EnsureOnMenuCountScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            if (!string.Equals(currentOnMenuCountsSceneName, sceneName, StringComparison.OrdinalIgnoreCase))
            {
                CurrentOnMenuCounts.Clear();
                currentOnMenuCountsSceneName = sceneName;
                currentOnMenuCountsDirty = false;
                InvalidateProbabilityMap();
            }
        }

        private static void RefreshPreparedState(bool inActiveRound)
        {
            if (!enabled.Value || !inActiveRound)
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null || scene.OrderedRecipes.Count == 0)
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            if (!HasAnyTrackedRecipes(scene))
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            if (!string.Equals(preparedSourceSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                ClearPreparedState();
                preparedSourceSceneName = scene.SceneName;
            }

            if (preparedSourceBootstrapComplete
                && PreparedSourcesByInstanceId.Count == 0
                && GetPreparedCandidateRecipeIds(scene).Count > 0
                && Time.frameCount >= nextPreparedBootstrapFallbackFrame)
            {
                SchedulePreparedBootstrap(0);
                nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackIntervalFrames;
            }

            if (!preparedSourceBootstrapComplete)
            {
                if (Time.frameCount >= nextPreparedBootstrapFrame)
                {
                    if (RunPreparedBootstrapStep())
                    {
                        preparedSourceBootstrapComplete = true;
                        nextPreparedSourceRefreshFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
                        nextPreparedSourcePruneFrame = Time.frameCount + PreparedSourcePruneIntervalFrames;
                        nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackIntervalFrames;
                    }
                    else
                    {
                        nextPreparedBootstrapFrame = Time.frameCount + PreparedBootstrapStepIntervalFrames;
                    }
                }

                return;
            }

            if (DirtyPreparedSourceIds.Count > 0 && Time.frameCount >= nextPreparedSourceRefreshFrame)
            {
                RefreshDirtyPreparedSources(MaxPreparedSourceRefreshesPerBatch);
                nextPreparedSourceRefreshFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
            }

            if (Time.frameCount >= nextPreparedSourcePruneFrame)
            {
                PrunePreparedSources();
                nextPreparedSourcePruneFrame = Time.frameCount + PreparedSourcePruneIntervalFrames;
            }
        }

        private static bool ShouldShowOverlay()
        {
            return ShouldShowOverlay(IsInActiveRound());
        }

        private static bool ShouldShowOverlay(bool inActiveRound)
        {
            if (enabled == null || !enabled.Value)
            {
                return false;
            }

            if (!inActiveRound)
            {
                return false;
            }

            SceneInfo scene;
            return TryGetCurrentSceneInfo(out scene) && scene != null && scene.OrderedRecipes.Count > 0;
        }

        private static bool IsInActiveRound()
        {
            ClientFlowControllerBase clientFlowController = GetClientFlowController();
            return clientFlowController != null && clientFlowController.InRound;
        }

        private static ClientFlowControllerBase GetClientFlowController()
        {
            if (cachedClientFlowController == null || Time.frameCount >= nextClientFlowLookupFrame)
            {
                cachedClientFlowController = UnityEngine.Object.FindObjectOfType<ClientFlowControllerBase>();
                nextClientFlowLookupFrame = Time.frameCount + (cachedClientFlowController != null ? ControllerLookupIntervalFrames : ControllerLookupRetryIntervalFrames);
            }

            return cachedClientFlowController;
        }

        private static ClientKitchenFlowControllerBase GetKitchenFlowController()
        {
            if (cachedKitchenFlowController == null || Time.frameCount >= nextKitchenFlowLookupFrame)
            {
                cachedKitchenFlowController = UnityEngine.Object.FindObjectOfType<ClientKitchenFlowControllerBase>();
                nextKitchenFlowLookupFrame = Time.frameCount + (cachedKitchenFlowController != null ? ControllerLookupIntervalFrames : ControllerLookupRetryIntervalFrames);
            }

            return cachedKitchenFlowController;
        }

        private static void SyncReferenceTickets()
        {
            referenceTicketsDirty = false;
            nextReferenceTicketSyncFrame = 0;

            if (enabled == null || !enabled.Value || !IsInActiveRound())
            {
                if (ReferenceTicketStates.Count > 0)
                {
                    ClearReferenceTickets();
                }

                return;
            }

            int displayLimit = GetReferenceTicketDisplayLimit();
            if (displayLimit <= 0)
            {
                if (ReferenceTicketStates.Count > 0)
                {
                    ClearReferenceTickets();
                }

                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null || scene.OrderedRecipes.Count == 0 || !HasAnyTrackedRecipes(scene))
            {
                if (ReferenceTicketStates.Count > 0)
                {
                    ClearReferenceTickets();
                }

                return;
            }

            List<ReferenceTicketCandidate> desiredCandidates = BuildReferenceTicketCandidates(scene, EnsureRun(scene), displayLimit);
            if (desiredCandidates.Count == 0)
            {
                if (ReferenceTicketStates.Count > 0)
                {
                    ClearReferenceTickets();
                }

                return;
            }

            List<RecipeFlowGUI> flows = GetReferenceTicketFlows();
            if (flows.Count == 0)
            {
                PruneReferenceTicketStates();
                return;
            }

            PruneReferenceTicketStates();
            ReferenceTicketFlowIdsBuffer.Clear();
            for (int i = 0; i < flows.Count; i++)
            {
                RecipeFlowGUI flow = flows[i];
                if (flow != null)
                {
                    ReferenceTicketFlowIdsBuffer.Add(flow.GetInstanceID());
                }
            }

            for (int i = ReferenceTicketStates.Count - 1; i >= 0; i--)
            {
                ReferenceTicketState state = ReferenceTicketStates[i];
                if (state == null || !ReferenceTicketFlowIdsBuffer.Contains(state.FlowInstanceId))
                {
                    RemoveReferenceTicketAt(i);
                }
            }

            for (int i = 0; i < flows.Count; i++)
            {
                RecipeFlowGUI flow = flows[i];
                if (flow == null)
                {
                    continue;
                }

                EnsureReferenceTicketCapacity(flow, BaseMenuTicketCapacity + displayLimit);
                SyncReferenceTicketsForFlow(flow, desiredCandidates, true);
            }
        }

        private static List<RecipeFlowGUI> GetReferenceTicketFlows()
        {
            ReferenceTicketFlowsBuffer.Clear();
            ReferenceTicketFlowIdsBuffer.Clear();

            ClientKitchenFlowControllerBase flowController = GetKitchenFlowController();
            if (flowController == null || ClientOrderControllerGuiField == null)
            {
                return ReferenceTicketFlowsBuffer;
            }

            for (int i = 0; i < TeamIds.Length; i++)
            {
                ClientTeamMonitor monitor;
                try
                {
                    monitor = flowController.GetMonitorForTeam(TeamIds[i]);
                }
                catch
                {
                    continue;
                }

                if (monitor == null || monitor.OrdersController == null)
                {
                    continue;
                }

                RecipeFlowGUI recipeFlow = ClientOrderControllerGuiField.GetValue(monitor.OrdersController) as RecipeFlowGUI;
                if (recipeFlow == null)
                {
                    continue;
                }

                int instanceId = recipeFlow.GetInstanceID();
                if (ReferenceTicketFlowIdsBuffer.Add(instanceId))
                {
                    ReferenceTicketFlowsBuffer.Add(recipeFlow);
                }
            }

            return ReferenceTicketFlowsBuffer;
        }

        private static void EnsureReferenceTicketCapacity(RecipeFlowGUI flow, int desiredCapacity)
        {
            if (flow == null || RecipeFlowMaxOrdersAllowedField == null || RecipeFlowOccupiedTablesField == null)
            {
                return;
            }

            object currentMaxValue = RecipeFlowMaxOrdersAllowedField.GetValue(flow);
            int currentMax = currentMaxValue is int ? (int)currentMaxValue : BaseMenuTicketCapacity;
            bool[] occupiedTables = RecipeFlowOccupiedTablesField.GetValue(flow) as bool[];
            int targetCapacity = Math.Max(desiredCapacity, Math.Max(currentMax, occupiedTables != null ? occupiedTables.Length : 0));
            if (currentMax != targetCapacity)
            {
                RecipeFlowMaxOrdersAllowedField.SetValue(flow, targetCapacity);
            }

            if (occupiedTables == null || occupiedTables.Length < targetCapacity)
            {
                bool[] nextOccupiedTables = new bool[targetCapacity];
                if (occupiedTables != null && occupiedTables.Length > 0)
                {
                    Array.Copy(occupiedTables, nextOccupiedTables, Math.Min(occupiedTables.Length, nextOccupiedTables.Length));
                }

                RecipeFlowOccupiedTablesField.SetValue(flow, nextOccupiedTables);
            }
        }

        private static void CreateReferenceTicketsForFlow(RecipeFlowGUI flow, List<ReferenceTicketCandidate> candidates)
        {
            if (flow == null || candidates == null || candidates.Count == 0)
            {
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                AddReferenceTicket(flow, candidates[i], i, true);
            }
        }

        private static void SyncReferenceTicketsForFlow(RecipeFlowGUI flow, List<ReferenceTicketCandidate> desiredCandidates, bool silentAdds)
        {
            if (flow == null)
            {
                return;
            }

            int flowInstanceId = flow.GetInstanceID();
            ReferenceTicketStatesForFlowBuffer.Clear();
            for (int i = 0; i < ReferenceTicketStates.Count; i++)
            {
                ReferenceTicketState state = ReferenceTicketStates[i];
                if (state != null && state.FlowInstanceId == flowInstanceId)
                {
                    ReferenceTicketStatesForFlowBuffer.Add(state);
                }
            }

            ReferenceTicketStatesForFlowBuffer.Sort(CompareReferenceTicketStates);
            int desiredCount = desiredCandidates != null ? desiredCandidates.Count : 0;
            Dictionary<int, ReferenceTicketState> existingStatesByRecipeId = ExistingReferenceTicketStatesByRecipeIdBuffer;
            existingStatesByRecipeId.Clear();
            HashSet<int> desiredRecipeIds = DesiredReferenceTicketRecipeIdsBuffer;
            desiredRecipeIds.Clear();
            for (int i = 0; i < desiredCount; i++)
            {
                ReferenceTicketCandidate candidate = desiredCandidates[i];
                if (candidate == null || candidate.Recipe == null)
                {
                    continue;
                }

                desiredRecipeIds.Add(candidate.Recipe.Id);
            }

            for (int i = 0; i < ReferenceTicketStatesForFlowBuffer.Count; i++)
            {
                ReferenceTicketState state = ReferenceTicketStatesForFlowBuffer[i];
                if (state == null)
                {
                    continue;
                }

                if (!existingStatesByRecipeId.ContainsKey(state.RecipeId))
                {
                    existingStatesByRecipeId[state.RecipeId] = state;
                }
                else
                {
                    int duplicateStateIndex = ReferenceTicketStates.IndexOf(state);
                    if (duplicateStateIndex >= 0)
                    {
                        RemoveReferenceTicketAt(duplicateStateIndex, true);
                    }
                }
            }

            for (int i = ReferenceTicketStatesForFlowBuffer.Count - 1; i >= 0; i--)
            {
                ReferenceTicketState state = ReferenceTicketStatesForFlowBuffer[i];
                if (state == null || desiredRecipeIds.Contains(state.RecipeId))
                {
                    continue;
                }

                int stateIndex = ReferenceTicketStates.IndexOf(state);
                if (stateIndex >= 0)
                {
                    RemoveReferenceTicketAt(stateIndex, true);
                }
            }

            for (int i = 0; i < desiredCount; i++)
            {
                ReferenceTicketCandidate candidate = desiredCandidates[i];
                if (candidate == null || candidate.Recipe == null)
                {
                    continue;
                }

                ReferenceTicketState existingState;
                if (existingStatesByRecipeId.TryGetValue(candidate.Recipe.Id, out existingState)
                    && existingState != null
                    && ReferenceTicketStates.Contains(existingState))
                {
                    UpdateReferenceTicketState(existingState, candidate, i);
                    continue;
                }

                AddReferenceTicket(flow, candidate, i, silentAdds);
            }

            existingStatesByRecipeId.Clear();
            desiredRecipeIds.Clear();
        }

        private static int CompareReferenceTicketStates(ReferenceTicketState a, ReferenceTicketState b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a == null)
            {
                return 1;
            }

            if (b == null)
            {
                return -1;
            }

            return GetReferenceTicketOrder(a).CompareTo(GetReferenceTicketOrder(b));
        }

        private static int GetReferenceTicketOrder(ReferenceTicketState state)
        {
            if (state == null)
            {
                return int.MaxValue;
            }

            TicketWidgetState ticketState;
            if (state.Widget != null && TicketWidgetsByInstanceId.TryGetValue(state.Widget.GetInstanceID(), out ticketState) && ticketState != null)
            {
                return ticketState.Order;
            }

            try
            {
                if (state.Flow != null)
                {
                    RecipeFlowGUI.RecipeWidgetData widgetData = state.Flow.GetData(state.Token);
                    if (widgetData != null)
                    {
                        return widgetData.m_order;
                    }
                }
            }
            catch
            {
            }

            return int.MaxValue;
        }

        private static void AddReferenceTicket(RecipeFlowGUI flow, ReferenceTicketCandidate candidate, int displayIndex, bool silentAppearance)
        {
            if (flow == null || candidate == null || candidate.Recipe == null || candidate.Recipe.Definition == null)
            {
                return;
            }

            try
            {
                RecipeFlowGUI.ElementToken token = flow.AddElement(candidate.Recipe.Definition, ReferenceTicketSyntheticTimeLimit, ReferenceTicketExpiredCallback);
                RecipeFlowGUI.RecipeWidgetData widgetData = flow.GetData(token);
                if (widgetData == null || widgetData.m_widget == null)
                {
                    return;
                }

                int referenceOrder = ReferenceTicketOrderBase + Mathf.Max(0, displayIndex);
                widgetData.m_order = referenceOrder;
                ReferenceTicketState state = new ReferenceTicketState();
                state.FlowInstanceId = flow.GetInstanceID();
                state.Flow = flow;
                state.RecipeId = candidate.Recipe.Id;
                state.Probability = candidate.Probability;
                state.Token = token;
                state.Widget = widgetData.m_widget;
                ReferenceTicketStates.Add(state);
                ApplyReferenceTicketPresentation(state, referenceOrder);
                if (silentAppearance)
                {
                    BeginSilentReferenceTicketAppearance(state.Widget);
                }
                else
                {
                    TicketWidgetState ticketState;
                    if (TicketWidgetsByInstanceId.TryGetValue(state.Widget.GetInstanceID(), out ticketState) && ticketState != null)
                    {
                        ticketState.IsDyingReferenceTicket = false;
                    }
                }

                InvalidateTicketWidgets();
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to add reference ticket: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ApplyReferenceTicketPresentation(ReferenceTicketState state)
        {
            ApplyReferenceTicketPresentation(state, -1);
        }

        private static void ApplyReferenceTicketPresentation(ReferenceTicketState state, int referenceOrder)
        {
            if (state == null || state.Widget == null)
            {
                return;
            }

            try
            {
                state.Widget.SetTimePropRemaining(1f);
            }
            catch
            {
            }

            TicketWidgetState ticketState;
            if (TicketWidgetsByInstanceId.TryGetValue(state.Widget.GetInstanceID(), out ticketState) && ticketState != null)
            {
                if (referenceOrder >= 0)
                {
                    ticketState.Order = referenceOrder;
                }
                ticketState.IsReferenceTicket = true;
                ticketState.IsDyingReferenceTicket = false;
                ticketState.ReferenceProbability = state.Probability;
            }
        }

        private static void RebindReferenceTicketState(ReferenceTicketState state, ReferenceTicketCandidate candidate, int displayIndex)
        {
            if (state == null || state.Widget == null || candidate == null || candidate.Recipe == null || candidate.Recipe.Definition == null)
            {
                return;
            }

            state.RecipeId = candidate.Recipe.Id;
            if (!TrySilentlyRefreshReferenceTicketWidget(state.Widget, candidate.Recipe.Definition))
            {
                int stateIndex = ReferenceTicketStates.IndexOf(state);
                if (stateIndex >= 0)
                {
                    RemoveReferenceTicketAt(stateIndex);
                    AddReferenceTicket(state.Flow, candidate, displayIndex, true);
                    return;
                }
            }

            TicketWidgetState ticketState;
            if (TicketWidgetsByInstanceId.TryGetValue(state.Widget.GetInstanceID(), out ticketState) && ticketState != null)
            {
                ticketState.RecipeId = candidate.Recipe.Id;
                ticketState.CachedImages = null;
                ticketState.HasAppliedTint = false;
            }

            InvalidateTicketWidgets();
            UpdateReferenceTicketState(state, candidate, displayIndex);
        }

        private static bool TrySilentlyRefreshReferenceTicketWidget(RecipeWidgetUIController widget, OrderDefinitionNode definition)
        {
            return TryRebuildReferenceTicketWidget(widget, definition, true);
        }

        private static bool TryRebuildReferenceTicketWidget(RecipeWidgetUIController widget, OrderDefinitionNode definition, bool deferReveal)
        {
            if (widget == null || definition == null || RecipeWidgetRecipeTreeField == null)
            {
                return false;
            }

            try
            {
                SetReferenceTicketWidgetVisible(widget, false);
                widget.StopAllCoroutines();
                RecipeWidgetRecipeTreeField.SetValue(widget, definition.m_orderGuiDescription);
                widget.RefreshSubElements();
                if (deferReveal)
                {
                    ScheduleSilentReferenceTicketReveal(widget);
                }
                else
                {
                    SetReferenceTicketWidgetVisible(widget, true);
                }
                return true;
            }
            catch
            {
                SetReferenceTicketWidgetVisible(widget, true);
                return false;
            }
        }

        private static void BeginSilentReferenceTicketAppearance(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return;
            }

            SetReferenceTicketWidgetVisible(widget, false);
            ScheduleSilentReferenceTicketReveal(widget);
        }

        private static void ScheduleSilentReferenceTicketReveal(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return;
            }

            try
            {
                widget.StartCoroutine(FinalizeSilentReferenceTicketAppearance(widget));
            }
            catch
            {
                SetReferenceTicketWidgetVisible(widget, true);
                InvalidateTicketWidgets();
            }
        }

        private static IEnumerator FinalizeSilentReferenceTicketAppearance(RecipeWidgetUIController widget)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForSecondsRealtime(0.75f);
            if (widget == null)
            {
                yield break;
            }

            SetReferenceTicketWidgetVisible(widget, true);
            InvalidateTicketWidgets();
        }

        private static void UpdateReferenceTicketState(ReferenceTicketState state, ReferenceTicketCandidate candidate, int displayIndex)
        {
            if (state == null || candidate == null || candidate.Recipe == null)
            {
                return;
            }

            state.Probability = candidate.Probability;
            int referenceOrder = ReferenceTicketOrderBase + Mathf.Max(0, displayIndex);
            try
            {
                if (state.Flow != null)
                {
                    RecipeFlowGUI.RecipeWidgetData widgetData = state.Flow.GetData(state.Token);
                    if (widgetData != null)
                    {
                        widgetData.m_order = referenceOrder;
                    }
                }
            }
            catch
            {
            }

            ApplyReferenceTicketPresentation(state, referenceOrder);
        }

        private static List<ReferenceTicketCandidate> BuildReferenceTicketCandidates(SceneInfo scene, RunInfo run, int displayLimit)
        {
            ReferenceTicketCandidatesBuffer.Clear();
            if (scene == null || run == null || displayLimit <= 0)
            {
                return ReferenceTicketCandidatesBuffer;
            }

            bool showPrepared = IsPreparedTrackingEnabled();
            List<OverlayRow> rows = BuildAndSortOverlayRows(scene, run, showPrepared);
            for (int i = 0; i < rows.Count; i++)
            {
                OverlayRow row = rows[i];
                if (row == null || row.Recipe == null || !IsOverlayReferenceCandidate(row, showPrepared))
                {
                    continue;
                }

                ReferenceTicketCandidate candidate = new ReferenceTicketCandidate();
                candidate.Recipe = row.Recipe;
                candidate.Probability = row.Probability;
                candidate.Served = row.Served;
                ReferenceTicketCandidatesBuffer.Add(candidate);
                if (ReferenceTicketCandidatesBuffer.Count >= displayLimit)
                {
                    break;
                }
            }

            return ReferenceTicketCandidatesBuffer;
        }

        private static Rect BuildOverlayRect(GUIStyle style)
        {
            float scale = Mathf.Max(_MODEntry.dpiScaleFactor, 1f);
            float width = Mathf.Clamp(overlayWidth != null ? overlayWidth.Value : 500, 240, 1600) * scale;
            float height = Mathf.Clamp(overlayHeight != null ? overlayHeight.Value : 520, 120, 1600) * scale;
            float x = Mathf.Clamp((overlayX != null ? overlayX.Value : 40) * scale, 0f, Mathf.Max(0f, Screen.width - width));
            float y = Mathf.Clamp((overlayY != null ? overlayY.Value : 300) * scale, 0f, Mathf.Max(0f, Screen.height - height));
            return new Rect(x, y, width, height);
        }

        private static TextAnchor GetOverlayTextAnchor()
        {
            OverlayTextAlignment alignment = overlayTextAlignment != null ? overlayTextAlignment.Value : OverlayTextAlignment.Left;
            switch (alignment)
            {
                case OverlayTextAlignment.Right:
                    return TextAnchor.UpperRight;
                case OverlayTextAlignment.Center:
                    return TextAnchor.UpperCenter;
                default:
                    return TextAnchor.UpperLeft;
            }
        }

        private static int GetOverlayFontSize()
        {
            return overlayFontSize != null ? overlayFontSize.Value : _MODEntry.defaultFontSize.Value;
        }

        private static Color GetOverlayFontColor()
        {
            return overlayFontColor != null ? overlayFontColor.Value : _MODEntry.defaultFontColor.Value;
        }

        private static FontStyle GetOverlayFontStyle()
        {
            return overlayBoldFont != null && overlayBoldFont.Value ? FontStyle.Bold : FontStyle.Normal;
        }

    }
}
