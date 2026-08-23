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
using OC2MenuManager.Infrastructure;

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
            unchecked
            {
                categoryTierRevision++;
            }
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
            return enabled != null
                && enabled.Value
                && !NoMenuMode.IsActiveForRound
                && preparedTrackingEnabled != null
                && preparedTrackingEnabled.Value;
        }

        private static bool IsMenuTicketTintEnabled()
        {
            return enabled != null
                && enabled.Value
                && !NoMenuMode.IsActiveForRound
                && menuTicketTintEnabled != null
                && menuTicketTintEnabled.Value;
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
            bool hadPreparedState = PreparedSourcesByInstanceId.Count > 0
                || PreparedCountsByRecipe.Count > 0
                || PreparedSourceComponentByHandlerId.Count > 0
                || PreparedCookStateBySourceId.Count > 0;
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
                try
                {
                    RestoreTicketWidgetTint(state);
                }
                catch
                {
                    // Widgets may have been destroyed by Unity before transition cleanup runs.
                }
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
                    if (widgetData != null && activeWidgets == null)
                    {
                        state.Flow.RemoveElement(state.Token, new ReferenceTicketDestroyAnimation(GetReferenceTicketDestroyAnimationColor()));
                        return;
                    }

                    bool removedActiveWidget = widgetData != null && activeWidgets != null && activeWidgets.Contains(widgetData);
                    if (removedActiveWidget)
                    {
                        activeWidgets.Remove(widgetData);
                        bool[] occupiedTables = RecipeFlowOccupiedTablesField != null ? RecipeFlowOccupiedTablesField.GetValue(state.Flow) as bool[] : null;
                        int tableNumber = state.Widget.GetTableNumber();
                        if (occupiedTables != null && tableNumber >= 0 && tableNumber < occupiedTables.Length)
                        {
                            occupiedTables[tableNumber] = false;
                        }
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
            if (enabled == null || !enabled.Value || NoMenuMode.IsActiveForRound)
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
            if (cachedClientFlowController == null && Time.frameCount >= nextClientFlowLookupFrame)
            {
                cachedClientFlowController = UnityEngine.Object.FindObjectOfType<ClientFlowControllerBase>();
                nextClientFlowLookupFrame = Time.frameCount + ControllerLookupRetryIntervalFrames;
            }

            return cachedClientFlowController;
        }

        private static ClientKitchenFlowControllerBase GetKitchenFlowController()
        {
            if (cachedKitchenFlowController == null && Time.frameCount >= nextKitchenFlowLookupFrame)
            {
                cachedKitchenFlowController = UnityEngine.Object.FindObjectOfType<ClientKitchenFlowControllerBase>();
                nextKitchenFlowLookupFrame = Time.frameCount + ControllerLookupRetryIntervalFrames;
            }

            return cachedKitchenFlowController;
        }

        private static void SyncReferenceTickets()
        {
            referenceTicketsDirty = false;
            nextReferenceTicketSyncFrame = 0;

            if (enabled == null || !enabled.Value || !IsInActiveRound() || NoMenuMode.IsActiveForRound)
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
                ScheduleReferenceTicketRetry();
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

                EnsureReferenceTicketCapacity(flow, desiredCandidates.Count);
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

        private static void EnsureReferenceTicketCapacity(RecipeFlowGUI flow, int requestedReferenceCount)
        {
            if (flow == null || RecipeFlowMaxOrdersAllowedField == null || RecipeFlowOccupiedTablesField == null)
            {
                return;
            }

            object currentMaxValue = RecipeFlowMaxOrdersAllowedField.GetValue(flow);
            int currentMax = currentMaxValue is int ? (int)currentMaxValue : BaseMenuTicketCapacity;
            bool[] occupiedTables = RecipeFlowOccupiedTablesField.GetValue(flow) as bool[];
            int activeRealTickets = GetActiveRealTicketCount(flow);
            int effectiveRealLimit = GetEffectiveRealTicketLimit(flow, activeRealTickets);
            int desiredCapacity = TicketCapacityPolicy.CalculateTargetCapacity(effectiveRealLimit, activeRealTickets, requestedReferenceCount);
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

        private static int GetEffectiveRealTicketLimit(RecipeFlowGUI flow, int activeRealTickets)
        {
            if (flow == null)
            {
                return Math.Max(BaseMenuTicketCapacity, activeRealTickets);
            }

            int flowInstanceId = flow.GetInstanceID();
            int effectiveLimit;
            if (!ReferenceRealTicketLimitByFlowId.TryGetValue(flowInstanceId, out effectiveLimit))
            {
                effectiveLimit = BaseMenuTicketCapacity;
                if (RecipeFlowGetMaxOrderNumberMethod != null)
                {
                    try
                    {
                        object value = RecipeFlowGetMaxOrderNumberMethod.Invoke(flow, null);
                        if (value is int)
                        {
                            effectiveLimit = Math.Max(effectiveLimit, (int)value);
                        }
                    }
                    catch
                    {
                    }
                }

                ReferenceRealTicketLimitByFlowId[flowInstanceId] = effectiveLimit;
            }

            return Math.Max(effectiveLimit, activeRealTickets);
        }

        private static int GetActiveRealTicketCount(RecipeFlowGUI flow)
        {
            IList activeWidgets = flow != null && RecipeFlowWidgetsField != null
                ? RecipeFlowWidgetsField.GetValue(flow) as IList
                : null;
            int activeWidgetCount = activeWidgets != null ? activeWidgets.Count : 0;
            int referenceCount = 0;
            int flowInstanceId = flow != null ? flow.GetInstanceID() : 0;
            for (int i = 0; i < ReferenceTicketStates.Count; i++)
            {
                ReferenceTicketState state = ReferenceTicketStates[i];
                if (state != null && state.FlowInstanceId == flowInstanceId && state.Widget != null)
                {
                    referenceCount++;
                }
            }

            return Math.Max(0, activeWidgetCount - referenceCount);
        }

        private static bool HasFreeReferenceTicketTable(RecipeFlowGUI flow)
        {
            bool[] occupiedTables = flow != null && RecipeFlowOccupiedTablesField != null
                ? RecipeFlowOccupiedTablesField.GetValue(flow) as bool[]
                : null;
            if (occupiedTables == null)
            {
                return false;
            }

            for (int i = 0; i < occupiedTables.Length; i++)
            {
                if (!occupiedTables[i])
                {
                    return true;
                }
            }

            return false;
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
                    if (!ReferenceEquals(existingState.Definition, candidate.Recipe.Definition))
                    {
                        RebindReferenceTicketState(existingState, candidate, i);
                    }
                    else
                    {
                        UpdateReferenceTicketState(existingState, candidate, i);
                    }
                    continue;
                }

                AddReferenceTicket(flow, candidate, i, silentAdds);
            }

            existingStatesByRecipeId.Clear();
            desiredRecipeIds.Clear();
            ReorderActiveTicketWidgets(flow);
        }

        private static void ReorderActiveTicketWidgets(RecipeFlowGUI flow)
        {
            List<RecipeFlowGUI.RecipeWidgetData> activeWidgets = flow != null && RecipeFlowWidgetsField != null
                ? RecipeFlowWidgetsField.GetValue(flow) as List<RecipeFlowGUI.RecipeWidgetData>
                : null;
            if (activeWidgets == null || activeWidgets.Count < 2)
            {
                return;
            }

            RealTicketWidgetDataBuffer.Clear();
            ReferenceTicketWidgetDataBuffer.Clear();
            bool realTicketFoundAfterReference = false;
            bool foundReference = false;
            for (int i = 0; i < activeWidgets.Count; i++)
            {
                RecipeFlowGUI.RecipeWidgetData widgetData = activeWidgets[i];
                if (IsReferenceTicketWidgetData(widgetData))
                {
                    foundReference = true;
                    ReferenceTicketWidgetDataBuffer.Add(widgetData);
                }
                else
                {
                    realTicketFoundAfterReference |= foundReference;
                    RealTicketWidgetDataBuffer.Add(widgetData);
                }
            }

            ReferenceTicketWidgetDataBuffer.Sort(delegate(RecipeFlowGUI.RecipeWidgetData left, RecipeFlowGUI.RecipeWidgetData right)
            {
                int leftOrder = left != null ? left.m_order : int.MaxValue;
                int rightOrder = right != null ? right.m_order : int.MaxValue;
                return leftOrder.CompareTo(rightOrder);
            });

            bool referenceOrderChanged = false;
            int referenceStartIndex = RealTicketWidgetDataBuffer.Count;
            for (int i = 0; i < ReferenceTicketWidgetDataBuffer.Count; i++)
            {
                int activeIndex = referenceStartIndex + i;
                if (activeIndex >= activeWidgets.Count || !ReferenceEquals(activeWidgets[activeIndex], ReferenceTicketWidgetDataBuffer[i]))
                {
                    referenceOrderChanged = true;
                    break;
                }
            }

            if (realTicketFoundAfterReference || referenceOrderChanged)
            {
                activeWidgets.Clear();
                activeWidgets.AddRange(RealTicketWidgetDataBuffer);
                activeWidgets.AddRange(ReferenceTicketWidgetDataBuffer);
            }

            RealTicketWidgetDataBuffer.Clear();
            ReferenceTicketWidgetDataBuffer.Clear();
        }

        private static bool IsReferenceTicketWidgetData(RecipeFlowGUI.RecipeWidgetData widgetData)
        {
            if (widgetData == null || widgetData.m_widget == null)
            {
                return false;
            }

            TicketWidgetState ticketState;
            if (TicketWidgetsByInstanceId.TryGetValue(widgetData.m_widget.GetInstanceID(), out ticketState)
                && ticketState != null
                && ticketState.IsReferenceTicket)
            {
                return true;
            }

            for (int i = 0; i < ReferenceTicketStates.Count; i++)
            {
                ReferenceTicketState referenceState = ReferenceTicketStates[i];
                if (referenceState != null && ReferenceEquals(referenceState.Widget, widgetData.m_widget))
                {
                    return true;
                }
            }

            return false;
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

            if (!HasFreeReferenceTicketTable(flow))
            {
                ScheduleReferenceTicketRetry();
                return;
            }

            IList activeWidgetsBeforeAdd = RecipeFlowWidgetsField != null
                ? RecipeFlowWidgetsField.GetValue(flow) as IList
                : null;
            int activeWidgetCountBeforeAdd = activeWidgetsBeforeAdd != null ? activeWidgetsBeforeAdd.Count : -1;
            int childCountBeforeAdd = flow.transform != null ? flow.transform.childCount : -1;
            int nextIndexBeforeAdd = -1;
            if (RecipeFlowNextIndexField != null)
            {
                object nextIndexValue = RecipeFlowNextIndexField.GetValue(flow);
                if (nextIndexValue is int)
                {
                    nextIndexBeforeAdd = (int)nextIndexValue;
                }
            }
            bool[] occupiedTablesBeforeAdd = RecipeFlowOccupiedTablesField != null
                ? RecipeFlowOccupiedTablesField.GetValue(flow) as bool[]
                : null;
            occupiedTablesBeforeAdd = occupiedTablesBeforeAdd != null
                ? (bool[])occupiedTablesBeforeAdd.Clone()
                : null;

            try
            {
                RecipeFlowGUI.ElementToken token = flow.AddElement(candidate.Recipe.Definition, ReferenceTicketSyntheticTimeLimit, ReferenceTicketExpiredCallback);
                RecipeFlowGUI.RecipeWidgetData widgetData = flow.GetData(token);
                if (widgetData == null || widgetData.m_widget == null)
                {
                    RollbackFailedReferenceTicketAdd(flow, activeWidgetCountBeforeAdd, childCountBeforeAdd, nextIndexBeforeAdd, occupiedTablesBeforeAdd);
                    return;
                }

                int tableNumber = widgetData.m_widget.GetTableNumber();
                bool[] occupiedTables = RecipeFlowOccupiedTablesField != null ? RecipeFlowOccupiedTablesField.GetValue(flow) as bool[] : null;
                if (occupiedTables == null || tableNumber < 0 || tableNumber >= occupiedTables.Length)
                {
                    RollbackFailedReferenceTicketAdd(flow, activeWidgetCountBeforeAdd, childCountBeforeAdd, nextIndexBeforeAdd, occupiedTablesBeforeAdd);
                    if (!invalidReferenceTableWarningLogged)
                    {
                        invalidReferenceTableWarningLogged = true;
                        _MODEntry.LogWarning("[ServedDishTracker] Deferred a reference ticket because RecipeFlowGUI returned an invalid table index.");
                    }
                    return;
                }

                int referenceOrder = ReferenceTicketOrderBase + Mathf.Max(0, displayIndex);
                widgetData.m_order = referenceOrder;
                ReferenceTicketState state = new ReferenceTicketState();
                state.FlowInstanceId = flow.GetInstanceID();
                state.Flow = flow;
                state.RecipeId = candidate.Recipe.Id;
                state.Definition = candidate.Recipe.Definition;
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
                RollbackFailedReferenceTicketAdd(flow, activeWidgetCountBeforeAdd, childCountBeforeAdd, nextIndexBeforeAdd, occupiedTablesBeforeAdd);
                if (!referenceTicketAddFailureLogged)
                {
                    referenceTicketAddFailureLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Failed to add reference ticket: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void RollbackFailedReferenceTicketAdd(
            RecipeFlowGUI flow,
            int previousWidgetCount,
            int previousChildCount,
            int previousNextIndex,
            bool[] previousOccupiedTables)
        {
            ScheduleReferenceTicketRetry();
            if (flow == null)
            {
                return;
            }

            try
            {
                IList activeWidgets = RecipeFlowWidgetsField != null
                    ? RecipeFlowWidgetsField.GetValue(flow) as IList
                    : null;
                while (activeWidgets != null && previousWidgetCount >= 0 && activeWidgets.Count > previousWidgetCount)
                {
                    int lastIndex = activeWidgets.Count - 1;
                    RecipeFlowGUI.RecipeWidgetData widgetData = activeWidgets[lastIndex] as RecipeFlowGUI.RecipeWidgetData;
                    activeWidgets.RemoveAt(lastIndex);
                    if (widgetData != null && widgetData.m_widget != null)
                    {
                        UnregisterTicketWidget(widgetData.m_widget);
                    }
                }

                Transform flowTransform = flow.transform;
                while (flowTransform != null && previousChildCount >= 0 && flowTransform.childCount > previousChildCount)
                {
                    Transform child = flowTransform.GetChild(flowTransform.childCount - 1);
                    RecipeWidgetUIController widget = child != null ? child.GetComponent<RecipeWidgetUIController>() : null;
                    if (widget != null)
                    {
                        UnregisterTicketWidget(widget);
                    }

                    if (child != null)
                    {
                        child.gameObject.SetActive(false);
                        child.SetParent(null, false);
                        UnityEngine.Object.Destroy(child.gameObject);
                    }
                }

                if (RecipeFlowNextIndexField != null && previousNextIndex >= 0)
                {
                    RecipeFlowNextIndexField.SetValue(flow, previousNextIndex);
                }

                bool[] occupiedTables = RecipeFlowOccupiedTablesField != null
                    ? RecipeFlowOccupiedTablesField.GetValue(flow) as bool[]
                    : null;
                int restoreCount = occupiedTables != null && previousOccupiedTables != null
                    ? Math.Min(occupiedTables.Length, previousOccupiedTables.Length)
                    : 0;
                for (int i = 0; i < restoreCount; i++)
                {
                    occupiedTables[i] = previousOccupiedTables[i];
                }
            }
            catch
            {
            }
        }

        private static void ScheduleReferenceTicketRetry()
        {
            referenceTicketsDirty = true;
            int targetFrame = Time.frameCount + TicketWidgetRetryIntervalFrames;
            if (nextReferenceTicketSyncFrame == 0 || nextReferenceTicketSyncFrame < Time.frameCount || targetFrame < nextReferenceTicketSyncFrame)
            {
                nextReferenceTicketSyncFrame = targetFrame;
            }
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
            state.Definition = candidate.Recipe.Definition;
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
            state.Definition = candidate.Recipe.Definition;
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
