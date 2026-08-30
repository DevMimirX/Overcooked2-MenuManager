// Connects base-game order and scene lifecycle events to tracker state. Hooks
// leave gameplay calls unchanged and avoid collecting probability-only state
// while history tracking is disabled or No Menu owns the round.
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
        [HarmonyPatch(typeof(LoadingScreenFlow), "LoadScene", new Type[] { typeof(string), typeof(GameState) })]
        [HarmonyPrefix]
        private static void LoadingScreenFlow_LoadScene_Prefix()
        {
            TryResetRoundRuntimeState("scene load");
        }

        [HarmonyPatch(typeof(LoadingScreenFlow), "RequestReturnToStartScreen")]
        [HarmonyPrefix]
        private static void LoadingScreenFlow_RequestReturnToStartScreen_Prefix()
        {
            TryResetRoundRuntimeState("return to start screen");
        }

        [HarmonyPatch(typeof(GameUtils), "LoadScene", new Type[] { typeof(string), typeof(UnityEngine.SceneManagement.LoadSceneMode) })]
        [HarmonyPrefix]
        private static void GameUtils_LoadScene_Tracker_Prefix(UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            // Build 20236421 also performs full scene replacements directly through
            // GameUtils when NetworkUtils disables the loading screen. Keep additive
            // InGameMenu loads intact while clearing round-owned state on replacements.
            if (mode == UnityEngine.SceneManagement.LoadSceneMode.Single)
            {
                TryResetRoundRuntimeState("direct scene load");
            }
        }

        private static void TryResetRoundRuntimeState(string reason)
        {
            try
            {
                ResetRoundRuntimeState();
            }
            catch (Exception ex)
            {
                // A cleanup failure must not escape a Harmony prefix and block scene loading.
                _MODEntry.LogWarning("[ServedDishTracker] Cleanup during " + reason + " was incomplete, but the scene transition was allowed to continue: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ResetRoundRuntimeState()
        {
            ClearTicketWidgetState();
            RunsByTeam.Clear();
            currentDynamicPhaseIndex = 0;
            ReconstructionReadyTeams.Clear();
            AuthoritativeOrderControllersByTeam.Clear();
            OptionalRecipeAdapters.InvalidateManyRecipeEntries();
            nextManyRecipesCatalogRetryFrame = 0;
            InvalidateProbabilityMap();
            cachedClientFlowController = null;
            cachedKitchenFlowController = null;
            cachedDlcManager = null;
            cachedWorldMapFlowController = null;
            nextClientFlowLookupFrame = 0;
            nextKitchenFlowLookupFrame = 0;
            nextDlcManagerLookupFrame = 0;
            cachedCurrentSceneInfo = null;
            cachedCurrentSceneInfoValid = false;
            cachedCurrentSceneInfoFrame = int.MinValue;
            ReferenceRealTicketLimitByFlowId.Clear();
            invalidReferenceTableWarningLogged = false;
            invalidRealTableWarningLogged = false;
            invalidTableReleaseWarningLogged = false;
            ticketAdmissionFailureWarningLogged = false;
            referenceTicketAddFailureLogged = false;
            trackingHookFailureWarningLogged = false;
            ClearOnMenuCounts();
            ClearPreparedState();
            ClearReferenceTickets();
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderAdded")]
        [HarmonyPostfix]
        private static void ClientKitchenFlowControllerBase_OnOrderAdded_Postfix(TeamID _teamID, Serialisable _orderData)
        {
            try
            {
                if (enabled == null || !enabled.Value)
                {
                    MarkProbabilityReconstructionIncomplete(_teamID);
                    return;
                }

                if (NoMenuMode.IsActiveForRound)
                {
                    return;
                }

                ServerOrderData orderData = _orderData as ServerOrderData;
                if (orderData == null || orderData.RecipeListEntry == null || orderData.RecipeListEntry.m_order == null)
                {
                    MarkProbabilityReconstructionIncomplete(_teamID);
                    return;
                }

                if (NoMenuMode.IsSyntheticOrder(_teamID, orderData.ID))
                {
                    return;
                }

                SceneInfo scene;
                if (!TryGetCurrentSceneInfo(out scene))
                {
                    MarkProbabilityReconstructionIncomplete(_teamID);
                    currentOnMenuCountsDirty = true;
                    InvalidatePreparedCandidates(true);
                    InvalidateOverlay();
                    return;
                }

                scene.RuntimeRecipeIds.Add(orderData.RecipeListEntry.m_order.m_uID);
                if (EnsureRecipe(scene, orderData.RecipeListEntry.m_order))
                {
                    NotifyRecipeCatalogChanged(scene);
                }
                RunInfo run = EnsureRun(scene, _teamID);
                int recipeId = orderData.RecipeListEntry.m_order.m_uID;
                run.TotalAdded++;
                run.AddedCounts[recipeId] = GetCount(run.AddedCounts, recipeId) + 1;
                InvalidateProbabilityMap();
                IncrementOnMenuCount(scene.SceneName, _teamID, recipeId);
                InvalidateOverlay();
            }
            catch (Exception ex)
            {
                MarkProbabilityReconstructionIncomplete(_teamID);
                currentOnMenuCountsDirty = true;
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                LogTrackingHookFailure("recording an added order", ex);
            }
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_Prefix(
            ClientKitchenFlowControllerBase __instance,
            TeamID _teamID,
            OrderID _orderID,
            out int __state)
        {
            __state = 0;
            try
            {
                if (enabled == null
                    || !enabled.Value
                    || __instance == null
                    || NoMenuMode.IsActiveForRound
                    || NoMenuMode.IsSyntheticOrder(_teamID, _orderID))
                {
                    return;
                }

                ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null || monitor.OrdersController == null)
                {
                    return;
                }

                RecipeList.Entry entry;
                if (!TryGetClientRecipe(monitor.OrdersController, _orderID, out entry))
                {
                    return;
                }

                __state = entry.m_order.m_uID;

                SceneInfo scene;
                if (!TryGetCurrentSceneInfo(out scene))
                {
                    MarkProbabilityReconstructionIncomplete(_teamID);
                    currentOnMenuCountsDirty = true;
                    InvalidatePreparedCandidates(true);
                    InvalidateOverlay();
                    return;
                }

                scene.RuntimeRecipeIds.Add(entry.m_order.m_uID);
                if (EnsureRecipe(scene, entry.m_order))
                {
                    NotifyRecipeCatalogChanged(scene);
                }
            }
            catch (Exception ex)
            {
                MarkProbabilityReconstructionIncomplete(_teamID);
                currentOnMenuCountsDirty = true;
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                LogTrackingHookFailure("capturing a successful delivery", ex);
            }
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        [HarmonyPostfix]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_TrackerPostfix(TeamID _teamID, int __state)
        {
            try
            {
                if (__state == 0)
                {
                    return;
                }

                SceneInfo scene;
                if (!TryGetCurrentSceneInfo(out scene))
                {
                    MarkProbabilityReconstructionIncomplete(_teamID);
                    currentOnMenuCountsDirty = true;
                    InvalidatePreparedCandidates(true);
                    InvalidateOverlay();
                    return;
                }

                RunInfo run = EnsureRun(scene, _teamID);
                int recipeId = __state;
                OrderLifecycleEffect effect = OrderLifecyclePolicy.GetEffect(OrderLifecycleEvent.SuccessfulDelivery);
                if (effect.IncrementServed)
                {
                    run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
                }

                if (effect.DecrementOnMenu)
                {
                    DecrementOnMenuCount(scene.SceneName, _teamID, recipeId);
                }

                InvalidateOverlay();
            }
            catch (Exception ex)
            {
                MarkProbabilityReconstructionIncomplete(_teamID);
                currentOnMenuCountsDirty = true;
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                LogTrackingHookFailure("recording a successful delivery", ex);
            }
        }

        private static void MarkProbabilityReconstructionIncomplete(TeamID teamId)
        {
            ReconstructionReadyTeams.Remove(teamId);
            RunInfo run;
            if (RunsByTeam.TryGetValue(teamId, out run) && run != null)
            {
                run.ReconstructionComplete = false;
                run.ProbabilityAvailable = false;
                run.ProbabilityDirty = true;
                run.OverlayRowsVersion = -1;
            }
        }

        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnOrderAdded")]
        [HarmonyPostfix]
        private static void ServerKitchenFlowControllerBase_OnOrderAdded_Postfix(ServerKitchenFlowControllerBase __instance, TeamID _teamID)
        {
            try
            {
                if (__instance == null
                    || enabled == null
                    || !enabled.Value
                    || NoMenuMode.IsActiveForRound)
                {
                    return;
                }

                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
                if (monitor == null || monitor.OrdersController == null)
                {
                    return;
                }

                AuthoritativeOrderControllersByTeam[_teamID] = monitor.OrdersController;
                InvalidateProbabilityMap();
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("capturing the authoritative order controller", ex);
            }
        }

        [HarmonyPatch(typeof(ClientDynamicFlowController), "OnDynamicLevelMessage")]
        [HarmonyPostfix]
        private static void ClientDynamicFlowController_OnDynamicLevelMessage_Postfix(Serialisable _serialisable)
        {
            try
            {
                if (NoMenuMode.IsActiveForRound)
                {
                    return;
                }

                DynamicLevelMessage message = _serialisable as DynamicLevelMessage;
                ResetProbabilityState(message != null ? message.m_phase : 0);
                InvalidateOverlay();
            }
            catch (Exception ex)
            {
                for (int i = 0; i < SupportedTeamIds.Length; i++)
                {
                    MarkProbabilityReconstructionIncomplete(SupportedTeamIds[i]);
                }

                LogTrackingHookFailure("processing a dynamic phase change", ex);
            }
        }

        private static void LogTrackingHookFailure(string operation, Exception exception)
        {
            if (trackingHookFailureWarningLogged || exception == null)
            {
                return;
            }

            trackingHookFailureWarningLogged = true;
            _MODEntry.LogWarning("[ServedDishTracker] A tracking hook failed while " + operation
                + "; the base-game call was allowed to continue: "
                + exception.GetType().Name + ": " + exception.Message);
        }

        private static void SchedulePreparedBootstrap(int delayFrames)
        {
            preparedSourceBootstrapComplete = false;
            preparedSourceBootstrapStage = 0;
            nextPreparedBootstrapFrame = Time.frameCount + Math.Max(0, delayFrames);
        }

        private static bool TryGetClientRecipe(ClientOrderControllerBase orderController, OrderID orderId, out RecipeList.Entry entry)
        {
            entry = null;
            if (orderController == null)
            {
                return false;
            }

            try
            {
                entry = orderController.GetRecipe(orderId);
                return entry != null && entry.m_order != null;
            }
            catch
            {
                return false;
            }
        }

    }
}
