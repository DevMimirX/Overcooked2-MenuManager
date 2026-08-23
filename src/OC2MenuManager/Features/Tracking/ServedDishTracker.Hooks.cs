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
            currentRun = null;
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
            referenceTicketAddFailureLogged = false;
            ClearOnMenuCounts();
            ClearPreparedState();
            ClearReferenceTickets();
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderAdded")]
        [HarmonyPostfix]
        private static void ClientKitchenFlowControllerBase_OnOrderAdded_Postfix(Serialisable _orderData)
        {
            if (!enabled.Value || NoMenuMode.IsActiveForRound)
            {
                return;
            }

            ServerOrderData orderData = _orderData as ServerOrderData;
            if (orderData == null || orderData.RecipeListEntry == null || orderData.RecipeListEntry.m_order == null)
            {
                return;
            }

            if (NoMenuMode.IsSyntheticOrder(orderData.ID))
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            scene.RuntimeRecipeIds.Add(orderData.RecipeListEntry.m_order.m_uID);
            if (EnsureRecipe(scene, orderData.RecipeListEntry.m_order))
            {
                NotifyRecipeCatalogChanged(scene);
            }
            RunInfo run = EnsureRun(scene);
            int recipeId = orderData.RecipeListEntry.m_order.m_uID;
            run.TotalAdded++;
            run.AddedCounts[recipeId] = GetCount(run.AddedCounts, recipeId) + 1;
            InvalidateProbabilityMap();
            IncrementOnMenuCount(scene.SceneName, recipeId);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null || NoMenuMode.IsActiveForRound || NoMenuMode.IsSyntheticOrder(_orderID))
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

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            scene.RuntimeRecipeIds.Add(entry.m_order.m_uID);
            if (EnsureRecipe(scene, entry.m_order))
            {
                NotifyRecipeCatalogChanged(scene);
            }
            RunInfo run = EnsureRun(scene);
            int recipeId = entry.m_order.m_uID;
            run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
            DecrementOnMenuCount(scene.SceneName, recipeId);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnFailedDelivery")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnFailedDelivery_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null || _orderID.m_id == 0u || NoMenuMode.IsActiveForRound || NoMenuMode.IsSyntheticOrder(_orderID))
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

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            scene.RuntimeRecipeIds.Add(entry.m_order.m_uID);
            if (EnsureRecipe(scene, entry.m_order))
            {
                NotifyRecipeCatalogChanged(scene);
            }
            RunInfo run = EnsureRun(scene);
            int recipeId = entry.m_order.m_uID;
            run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderExpired")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnOrderExpired_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null || NoMenuMode.IsActiveForRound || NoMenuMode.IsSyntheticOrder(_orderID))
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

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            scene.RuntimeRecipeIds.Add(entry.m_order.m_uID);
            if (EnsureRecipe(scene, entry.m_order))
            {
                NotifyRecipeCatalogChanged(scene);
            }
            DecrementOnMenuCount(scene.SceneName, entry.m_order.m_uID);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientDynamicFlowController), "OnDynamicLevelMessage")]
        [HarmonyPostfix]
        private static void ClientDynamicFlowController_OnDynamicLevelMessage_Postfix(Serialisable _serialisable)
        {
            if (NoMenuMode.IsActiveForRound)
            {
                return;
            }

            DynamicLevelMessage message = _serialisable as DynamicLevelMessage;
            ResetProbabilityState(message != null ? message.m_phase : 0);
            InvalidateOverlay();
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
