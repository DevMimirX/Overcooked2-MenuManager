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
        [HarmonyPatch(typeof(LoadingScreenFlow), "NextScene", MethodType.Getter)]
        [HarmonyPrefix]
        private static void LoadingScreenFlow_NextScene_Prefix()
        {
            currentRun = null;
            InvalidateProbabilityMap();
            cachedClientFlowController = null;
            cachedKitchenFlowController = null;
            nextClientFlowLookupFrame = 0;
            nextKitchenFlowLookupFrame = 0;
            cachedCurrentSceneInfo = null;
            cachedCurrentSceneInfoValid = false;
            cachedCurrentSceneInfoFrame = int.MinValue;
            ClearOnMenuCounts();
            ClearPreparedState();
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderAdded")]
        [HarmonyPostfix]
        private static void ClientKitchenFlowControllerBase_OnOrderAdded_Postfix(Serialisable _orderData)
        {
            if (!enabled.Value)
            {
                return;
            }

            ServerOrderData orderData = _orderData as ServerOrderData;
            if (orderData == null || orderData.RecipeListEntry == null || orderData.RecipeListEntry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, orderData.RecipeListEntry.m_order);
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
            if (!enabled.Value || __instance == null)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
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
            if (!enabled.Value || __instance == null || _orderID.m_id == 0u)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
            RunInfo run = EnsureRun(scene);
            int recipeId = entry.m_order.m_uID;
            run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderExpired")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnOrderExpired_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
            DecrementOnMenuCount(scene.SceneName, entry.m_order.m_uID);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientDynamicFlowController), "OnDynamicLevelMessage")]
        [HarmonyPostfix]
        private static void ClientDynamicFlowController_OnDynamicLevelMessage_Postfix(Serialisable _serialisable)
        {
            DynamicLevelMessage message = _serialisable as DynamicLevelMessage;
            ResetProbabilityState(message != null ? message.m_phase : 0);
            InvalidateOverlay();
        }

        private static void BootstrapPreparedSources()
        {
            while (!RunPreparedBootstrapStep())
            {
            }
        }

        private static void SchedulePreparedBootstrap(int delayFrames)
        {
            preparedSourceBootstrapComplete = false;
            preparedSourceBootstrapStage = 0;
            nextPreparedBootstrapFrame = Time.frameCount + Math.Max(0, delayFrames);
        }

    }
}
