using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameModes;
using HarmonyLib;
using OrderController;
using UnityEngine;
using OC2MenuManager.Infrastructure;

namespace OC2MenuManager
{
    internal static class NoMenuMode
    {
        private static readonly ConfigDefinition LegacyToggleKeyDefinition = new ConfigDefinition("00-菜单管理", "切换无菜单热键");
        private static readonly FieldInfo ClientCampaignTeamMonitorField = AccessTools.Field(typeof(ClientCampaignFlowController), "m_teamMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamOneMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamOneMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamTwoMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamTwoMonitor");
        private static readonly FieldInfo ClientTeamMonitorMonitorField = AccessTools.Field(typeof(ClientTeamMonitor), "m_monitor");
        private static readonly FieldInfo TeamMonitorRecipeBarField = AccessTools.Field(typeof(TeamMonitor), "m_recipeBarUIController");
        private static readonly FieldInfo PlateReturnControllerField = AccessTools.Field(typeof(ServerKitchenFlowControllerBase), "m_plateReturnController");
        private static readonly FieldInfo AutoProgressField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_autoProgress");
        private static readonly FieldInfo NextOrderIdField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_nextOrderID");
        private static readonly FieldInfo RoundDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundData");
        private static readonly FieldInfo RoundInstanceDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundInstanceData");
        private static readonly FieldInfo DynamicRoundInstanceCurrentPhaseField = ResolveDynamicRoundPhaseField();
        private static readonly MethodInfo AddSpecificOrderMethod = AccessTools.Method(typeof(ServerOrderControllerBase), "AddNewOrder", new[] { typeof(RecipeList.Entry) });
        private static readonly Dictionary<System.Type, MethodInfo> SuccessfulDeliveryMethodCache = new Dictionary<System.Type, MethodInfo>();
        private static readonly HashSet<uint> SyntheticOrderIds = new HashSet<uint>();

        private static ConfigEntry<bool> enabled;

        public static bool IsReady
        {
            get { return enabled != null; }
        }

        public static bool IsEnabled
        {
            get { return enabled != null && enabled.Value; }
        }

        public static void Awake()
        {
            if (_MODEntry.SettingsConfig.Remove(LegacyToggleKeyDefinition))
            {
                _MODEntry.SettingsConfig.Save();
            }

            enabled = _MODEntry.SettingsConfig.Bind<bool>("00-菜单管理", "无菜单", false, "启用内置无菜单模式，不依赖外部 OC2NoMenu.dll。");
            ModuleUtility.RegisterHarmony(typeof(NoMenuMode));
        }

        public static void ToggleEnabled()
        {
            if (enabled != null)
            {
                enabled.Value = !enabled.Value;
                RefreshServerOrderFlowState();
                RefreshClientRecipeBarVisibility();
            }
        }

        public static void SetEnabled(bool value)
        {
            if (enabled != null)
            {
                enabled.Value = value;
                RefreshServerOrderFlowState();
                RefreshClientRecipeBarVisibility();
            }
        }

        public static bool IsSyntheticOrder(OrderID orderId)
        {
            return orderId.m_id != 0u && SyntheticOrderIds.Contains(orderId.m_id);
        }

        public static void ForgetSyntheticOrder(OrderID orderId)
        {
            if (orderId.m_id != 0u)
            {
                SyntheticOrderIds.Remove(orderId.m_id);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCampaignFlowController), "StartSynchronising")]
        private static void ServerCampaignFlowController_StartSynchronising_Postfix(ServerCampaignFlowController __instance)
        {
            ClearSyntheticOrders();
            if (__instance is ServerBossFlowController)
            {
                return;
            }

            __instance.SetOrdersAutoProgress(!ShouldDisableCampaignAutoProgress());
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCompetitiveFlowController), "StartSynchronising")]
        private static void ServerCompetitiveFlowController_StartSynchronising_Postfix(ServerCompetitiveFlowController __instance)
        {
            ClearSyntheticOrders();
            if (__instance == null)
            {
                return;
            }

            bool autoProgress = !IsEnabled;
            SetAutoProgress(__instance.GetMonitorForTeam(TeamID.One), autoProgress);
            SetAutoProgress(__instance.GetMonitorForTeam(TeamID.Two), autoProgress);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
        private static bool ServerKitchenFlowControllerBase_OnFoodDelivered_Prefix(ServerKitchenFlowControllerBase __instance, AssembledDefinitionNode _definition, PlatingStepData _plateType, ServerPlateStation _station)
        {
            if (!ShouldHandleNoMenuDelivery(__instance, _station))
            {
                return true;
            }

            ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_station.GetTeamID());
            if (monitor == null || monitor.OrdersController == null)
            {
                return true;
            }

            if (!CanUseSyntheticFallback(_definition))
            {
                return true;
            }

            OrderID activeOrderId;
            float activeOrderRemaining;
            if (monitor.OrdersController.FindBestOrderForRecipe(_definition, _plateType, out activeOrderId, out activeOrderRemaining))
            {
                return true;
            }

            RecipeList.Entry matchedEntry;
            if (!TryFindNoMenuRecipeMatch(monitor, _definition, _plateType, out matchedEntry))
            {
                return true;
            }

            PlateReturnController plateReturnController = PlateReturnControllerField != null ? PlateReturnControllerField.GetValue(__instance) as PlateReturnController : null;
            if (plateReturnController != null)
            {
                plateReturnController.FoodDelivered(_definition, _plateType, _station);
            }

            ServerOrderData syntheticOrder = AddSyntheticOrder(monitor, matchedEntry);
            if (syntheticOrder == null)
            {
                return true;
            }

            bool restartCombo = monitor.Score.TotalCombo == 0;
            bool wasCombo = monitor.OrdersController.IsComboOrder(syntheticOrder.ID, restartCombo);
            monitor.OrdersController.RemoveOrder(syntheticOrder.ID);
            InvokeSuccessfulDelivery(__instance, syntheticOrder.ID, matchedEntry, 1f, wasCombo, _station);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientCampaignFlowController), "StartSynchronising")]
        private static void ClientCampaignFlowController_StartSynchronising_Postfix(ClientCampaignFlowController __instance)
        {
            ClearSyntheticOrders();
            if (__instance is ClientBossFlowController)
            {
                return;
            }

            ApplyCampaignRecipeBarVisibility(__instance, !IsEnabled);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientCompetitiveFlowController), "StartSynchronising")]
        private static void ClientCompetitiveFlowController_StartSynchronising_Postfix(ClientCompetitiveFlowController __instance)
        {
            ClearSyntheticOrders();
            ApplyCompetitiveRecipeBarVisibility(__instance, !IsEnabled);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_Postfix(OrderID _orderID)
        {
            ForgetSyntheticOrder(_orderID);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnFailedDelivery")]
        private static void ClientKitchenFlowControllerBase_OnFailedDelivery_Postfix(OrderID _orderID)
        {
            ForgetSyntheticOrder(_orderID);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderExpired")]
        private static void ClientKitchenFlowControllerBase_OnOrderExpired_Postfix(OrderID _orderID)
        {
            ForgetSyntheticOrder(_orderID);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FrontendCoopTabOptions), "OnOnlinePublicClicked")]
        [HarmonyPatch(typeof(FrontendVersusTabOptions), "OnOnlinePublicClicked")]
        private static bool Frontend_PublicOnline_Prefix()
        {
            if (enabled != null)
            {
                enabled.Value = false;
                RefreshClientRecipeBarVisibility();
            }

            return true;
        }

        private static void RefreshClientRecipeBarVisibility()
        {
            bool visible = !IsEnabled;

            ClientCampaignFlowController[] campaignFlows = Object.FindObjectsOfType<ClientCampaignFlowController>();
            for (int i = 0; i < campaignFlows.Length; i++)
            {
                ClientCampaignFlowController flow = campaignFlows[i];
                if (flow is ClientBossFlowController)
                {
                    continue;
                }

                ApplyCampaignRecipeBarVisibility(flow, visible);
            }

            ClientCompetitiveFlowController[] competitiveFlows = Object.FindObjectsOfType<ClientCompetitiveFlowController>();
            for (int i = 0; i < competitiveFlows.Length; i++)
            {
                ApplyCompetitiveRecipeBarVisibility(competitiveFlows[i], visible);
            }
        }

        private static void RefreshServerOrderFlowState()
        {
            bool campaignAutoProgress = !ShouldDisableCampaignAutoProgress();
            ServerCampaignFlowController[] campaignFlows = Object.FindObjectsOfType<ServerCampaignFlowController>();
            for (int i = 0; i < campaignFlows.Length; i++)
            {
                ServerCampaignFlowController flow = campaignFlows[i];
                if (flow is ServerBossFlowController)
                {
                    continue;
                }

                flow.SetOrdersAutoProgress(campaignAutoProgress);
            }

            bool competitiveAutoProgress = !IsEnabled;
            ServerCompetitiveFlowController[] competitiveFlows = Object.FindObjectsOfType<ServerCompetitiveFlowController>();
            for (int i = 0; i < competitiveFlows.Length; i++)
            {
                ServerCompetitiveFlowController flow = competitiveFlows[i];
                SetAutoProgress(flow.GetMonitorForTeam(TeamID.One), competitiveAutoProgress);
                SetAutoProgress(flow.GetMonitorForTeam(TeamID.Two), competitiveAutoProgress);
            }

            if (!IsEnabled)
            {
                ClearSyntheticOrders();
            }
        }

        private static void ApplyCampaignRecipeBarVisibility(ClientCampaignFlowController flow, bool visible)
        {
            if (flow == null || ClientCampaignTeamMonitorField == null)
            {
                return;
            }

            ClientTeamMonitor monitor = ClientCampaignTeamMonitorField.GetValue(flow) as ClientTeamMonitor;
            SetRecipeBarVisible(GetRecipeBar(monitor), visible);
        }

        private static void ApplyCompetitiveRecipeBarVisibility(ClientCompetitiveFlowController flow, bool visible)
        {
            if (flow == null)
            {
                return;
            }

            if (ClientCompetitiveTeamOneMonitorField != null)
            {
                ClientTeamMonitor teamOne = ClientCompetitiveTeamOneMonitorField.GetValue(flow) as ClientTeamMonitor;
                SetRecipeBarVisible(GetRecipeBar(teamOne), visible);
            }

            if (ClientCompetitiveTeamTwoMonitorField != null)
            {
                ClientTeamMonitor teamTwo = ClientCompetitiveTeamTwoMonitorField.GetValue(flow) as ClientTeamMonitor;
                SetRecipeBarVisible(GetRecipeBar(teamTwo), visible);
            }
        }

        private static RecipeFlowGUI GetRecipeBar(ClientTeamMonitor clientMonitor)
        {
            if (clientMonitor == null || ClientTeamMonitorMonitorField == null || TeamMonitorRecipeBarField == null)
            {
                return null;
            }

            TeamMonitor teamMonitor = ClientTeamMonitorMonitorField.GetValue(clientMonitor) as TeamMonitor;
            return teamMonitor != null ? TeamMonitorRecipeBarField.GetValue(teamMonitor) as RecipeFlowGUI : null;
        }

        private static void SetRecipeBarVisible(RecipeFlowGUI recipeBar, bool visible)
        {
            if (recipeBar == null || recipeBar.gameObject == null)
            {
                return;
            }

            CanvasGroup canvasGroup = recipeBar.gameObject.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = recipeBar.gameObject.AddComponent<CanvasGroup>();
            }

            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;
        }

        private static void SetAutoProgress(ServerTeamMonitor monitor, bool autoProgress)
        {
            if (monitor != null && monitor.OrdersController != null)
            {
                monitor.OrdersController.SetAutoProgress(autoProgress);
            }
        }

        private static bool ShouldHandleNoMenuDelivery(ServerKitchenFlowControllerBase flow, ServerPlateStation station)
        {
            if (!IsEnabled || flow == null || station == null)
            {
                return false;
            }

            LevelConfigBase levelConfig = GameUtils.GetLevelConfig();
            if (!(levelConfig is KitchenLevelConfigBase) || levelConfig is BossCampaignLevelConfig)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(levelConfig.name) && levelConfig.name.StartsWith("Tutorial"))
            {
                return false;
            }

            ServerTeamMonitor monitor = flow.GetMonitorForTeam(station.GetTeamID());
            if (monitor == null || monitor.OrdersController == null)
            {
                return false;
            }

            return !IsAutoProgressEnabled(monitor);
        }

        private static bool ShouldDisableCampaignAutoProgress()
        {
            if (!IsEnabled)
            {
                return false;
            }

            GameSession session = GameUtils.GetGameSession();
            if (session == null || session.GameModeKind == Kind.Survival)
            {
                return false;
            }

            KitchenLevelConfigBase levelConfig = GameUtils.GetLevelConfig() as KitchenLevelConfigBase;
            return !(session.GameModeKind == Kind.Campaign && levelConfig != null && levelConfig.m_recipesBeforeTimerStarts > 0);
        }

        private static bool IsAutoProgressEnabled(ServerTeamMonitor monitor)
        {
            return AutoProgressField != null && monitor != null && monitor.OrdersController != null && (bool)AutoProgressField.GetValue(monitor.OrdersController);
        }

        private static bool TryFindNoMenuRecipeMatch(ServerTeamMonitor monitor, AssembledDefinitionNode definition, PlatingStepData plateType, out RecipeList.Entry matchedEntry)
        {
            matchedEntry = null;
            if (!CanUseSyntheticFallback(definition))
            {
                return false;
            }

            List<RecipeList.Entry> entries = GetRecipesForCurrentLevel(monitor);
            for (int i = 0; i < entries.Count; i++)
            {
                RecipeList.Entry candidate = entries[i];
                if (candidate != null && candidate.m_order != null && MatchesRecipe(candidate.m_order, definition, plateType))
                {
                    matchedEntry = candidate;
                    return true;
                }
            }

            return false;
        }

        private static List<RecipeList.Entry> GetRecipesForCurrentLevel(ServerTeamMonitor monitor)
        {
            List<RecipeList.Entry> entries = new List<RecipeList.Entry>();
            RoundDataBase roundData = null;
            ServerOrderControllerBase orderController = monitor != null ? monitor.OrdersController : null;
            if (orderController != null && RoundDataField != null)
            {
                roundData = RoundDataField.GetValue(orderController) as RoundDataBase;
            }

            if (roundData == null)
            {
                KitchenLevelConfigBase levelConfig = GameUtils.GetLevelConfig() as KitchenLevelConfigBase;
                if (levelConfig == null)
                {
                    return entries;
                }

                roundData = levelConfig.GetRoundData();
            }

            DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
            if (dynamicRoundData != null && dynamicRoundData.Phases != null)
            {
                int currentPhase = GetCurrentDynamicPhaseIndex(orderController, dynamicRoundData);
                AddRecipeEntries(entries, currentPhase >= 0 && currentPhase < dynamicRoundData.Phases.Length
                    ? dynamicRoundData.Phases[currentPhase].Recipes
                    : null);
                return entries;
            }

            RoundData standardRoundData = roundData as RoundData;
            if (standardRoundData != null && standardRoundData.m_recipes != null && standardRoundData.m_recipes.m_recipes != null)
            {
                AddRecipeEntries(entries, standardRoundData.m_recipes);
            }

            return entries;
        }

        private static void AddRecipeEntries(List<RecipeList.Entry> entries, RecipeList recipeList)
        {
            if (entries == null || recipeList == null || recipeList.m_recipes == null)
            {
                return;
            }

            for (int i = 0; i < recipeList.m_recipes.Length; i++)
            {
                entries.Add(recipeList.m_recipes[i]);
            }
        }

        private static int GetCurrentDynamicPhaseIndex(ServerOrderControllerBase orderController, DynamicRoundData dynamicRoundData)
        {
            if (dynamicRoundData == null || dynamicRoundData.Phases == null || dynamicRoundData.Phases.Length == 0)
            {
                return 0;
            }

            if (orderController != null && RoundInstanceDataField != null && DynamicRoundInstanceCurrentPhaseField != null)
            {
                object roundInstanceData = RoundInstanceDataField.GetValue(orderController);
                object rawPhase = roundInstanceData != null ? DynamicRoundInstanceCurrentPhaseField.GetValue(roundInstanceData) : null;
                if (rawPhase is int)
                {
                    int currentPhase = (int)rawPhase;
                    if (currentPhase >= 0 && currentPhase < dynamicRoundData.Phases.Length)
                    {
                        return currentPhase;
                    }
                }
            }

            return 0;
        }

        private static bool CanUseSyntheticFallback(AssembledDefinitionNode definition)
        {
            if (definition == null || definition == AssembledDefinitionNode.NullNode)
            {
                return false;
            }

            CompositeAssembledNode composite = definition as CompositeAssembledNode;
            if (composite != null)
            {
                bool hasComposition = composite.m_composition != null && composite.m_composition.Length > 0;
                bool hasOptional = composite.m_optional != null && composite.m_optional.Length > 0;
                if (!hasComposition && !hasOptional)
                {
                    return false;
                }
            }

            AssembledDefinitionNode simplified = definition.Simpilfy();
            return simplified != null && simplified != AssembledDefinitionNode.NullNode;
        }

        private static bool MatchesRecipe(OrderDefinitionNode required, AssembledDefinitionNode provided, PlatingStepData plateType)
        {
            if (required == null || provided == null || required.m_platingStep != plateType)
            {
                return false;
            }

            if (required.GetType() == typeof(WildcardOrderNode))
            {
                return AssembledDefinitionNode.Matching(required, provided);
            }

            return AssembledDefinitionNode.Matching(provided, required);
        }

        private static ServerOrderData AddSyntheticOrder(ServerTeamMonitor monitor, RecipeList.Entry entry)
        {
            if (monitor == null || monitor.OrdersController == null || AddSpecificOrderMethod == null || entry == null)
            {
                return null;
            }

            uint reservedOrderId = 0u;
            if (NextOrderIdField != null)
            {
                object nextIdValue = NextOrderIdField.GetValue(monitor.OrdersController);
                if (nextIdValue is uint)
                {
                    reservedOrderId = (uint)nextIdValue;
                    SyntheticOrderIds.Add(reservedOrderId);
                }
            }

            ServerOrderData syntheticOrder = AddSpecificOrderMethod.Invoke(monitor.OrdersController, new object[] { entry }) as ServerOrderData;
            if (syntheticOrder == null)
            {
                if (reservedOrderId != 0u)
                {
                    SyntheticOrderIds.Remove(reservedOrderId);
                }

                return null;
            }

            if (reservedOrderId != 0u && syntheticOrder.ID.m_id != reservedOrderId)
            {
                SyntheticOrderIds.Remove(reservedOrderId);
                SyntheticOrderIds.Add(syntheticOrder.ID.m_id);
            }

            return syntheticOrder;
        }

        private static void InvokeSuccessfulDelivery(ServerKitchenFlowControllerBase flow, OrderID orderId, RecipeList.Entry entry, float timeRemainingPercentage, bool wasCombo, ServerPlateStation station)
        {
            if (flow == null || entry == null || station == null)
            {
                return;
            }

            MethodInfo method = GetSuccessfulDeliveryMethod(flow.GetType());
            if (method != null)
            {
                method.Invoke(flow, new object[] { orderId, entry, timeRemainingPercentage, wasCombo, station });
            }
        }

        private static MethodInfo GetSuccessfulDeliveryMethod(System.Type type)
        {
            if (type == null)
            {
                return null;
            }

            MethodInfo method;
            if (SuccessfulDeliveryMethodCache.TryGetValue(type, out method))
            {
                return method;
            }

            method = AccessTools.Method(type, "OnSuccessfulDelivery", new[] { typeof(OrderID), typeof(RecipeList.Entry), typeof(float), typeof(bool), typeof(ServerPlateStation) });
            SuccessfulDeliveryMethodCache[type] = method;
            return method;
        }

        private static FieldInfo ResolveDynamicRoundPhaseField()
        {
            System.Type dynamicRoundInstanceType = typeof(DynamicRoundData).GetNestedType("DynamicRoundInstanceData", BindingFlags.Public | BindingFlags.NonPublic);
            return dynamicRoundInstanceType != null
                ? dynamicRoundInstanceType.GetField("CurrentPhase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                : null;
        }

        private static void ClearSyntheticOrders()
        {
            SyntheticOrderIds.Clear();
        }
    }
}
