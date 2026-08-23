using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameModes;
using HarmonyLib;
using OC2MenuManager.Infrastructure;
using OrderController;
using Team17.Online;
using UnityEngine;

namespace OC2MenuManager
{
    internal static class NoMenuMode
    {
        private sealed class RecipeBarVisibilityState
        {
            public RecipeFlowGUI RecipeBar;
            public CanvasGroup CanvasGroup;
            public bool CanvasGroupCreated;
            public float Alpha;
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        private sealed class OrderProgressionState
        {
            public ServerOrderControllerBase Controller;
            public bool AutoProgress;
        }

        private static readonly ConfigDefinition LegacyToggleKeyDefinition = new ConfigDefinition("00-菜单管理", "切换无菜单热键");
        private static readonly FieldInfo ClientCampaignTeamMonitorField = AccessTools.Field(typeof(ClientCampaignFlowController), "m_teamMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamOneMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamOneMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamTwoMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamTwoMonitor");
        private static readonly FieldInfo ClientTeamMonitorMonitorField = AccessTools.Field(typeof(ClientTeamMonitor), "m_monitor");
        private static readonly FieldInfo TeamMonitorRecipeBarField = AccessTools.Field(typeof(TeamMonitor), "m_recipeBarUIController");
        private static readonly FieldInfo ClientLobbySessionVisibilityField = AccessTools.Field(typeof(ClientLobbyFlowController), "m_sessionVisibility");
        private static readonly FieldInfo PlateReturnControllerField = AccessTools.Field(typeof(ServerKitchenFlowControllerBase), "m_plateReturnController");
        private static readonly FieldInfo AutoProgressField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_autoProgress");
        private static readonly FieldInfo NextOrderIdField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_nextOrderID");
        private static readonly FieldInfo RoundDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundData");
        private static readonly FieldInfo RoundInstanceDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundInstanceData");
        private static readonly FieldInfo ActiveOrdersField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_activeOrders");
        private static readonly FieldInfo DynamicRoundInstanceCurrentPhaseField = ResolveDynamicRoundPhaseField();
        private static readonly Type ConnectionStatusType = AccessTools.TypeByName("ConnectionStatus");
        private static readonly MethodInfo ConnectionIsInSessionMethod = ConnectionStatusType != null
            ? AccessTools.Method(ConnectionStatusType, "IsInSession", new Type[0])
            : null;
        private static readonly MethodInfo ConnectionIsHostMethod = ConnectionStatusType != null
            ? AccessTools.Method(ConnectionStatusType, "IsHost", new Type[0])
            : null;
        private static readonly MethodInfo AddSpecificOrderMethod = AccessTools.Method(typeof(ServerOrderControllerBase), "AddNewOrder", new[] { typeof(RecipeList.Entry) });
        private static readonly MethodInfo SuccessfulDeliveryBaseMethod = AccessTools.Method(
            typeof(ServerKitchenFlowControllerBase),
            "OnSuccessfulDelivery",
            new[] { typeof(OrderID), typeof(RecipeList.Entry), typeof(float), typeof(bool), typeof(ServerPlateStation) });
        private static readonly Dictionary<Type, MethodInfo> SuccessfulDeliveryMethodCache = new Dictionary<Type, MethodInfo>();
        private static readonly HashSet<uint> SyntheticOrderIds = new HashSet<uint>();
        private static readonly Dictionary<int, RecipeBarVisibilityState> RecipeBarVisibilityStates = new Dictionary<int, RecipeBarVisibilityState>();
        private static readonly Dictionary<ServerOrderControllerBase, OrderProgressionState> OrderProgressionStates = new Dictionary<ServerOrderControllerBase, OrderProgressionState>();
        private static readonly List<RecipeList.Entry> NoMenuRecipeEntriesBuffer = new List<RecipeList.Entry>();
        private static readonly List<RecipeList.Entry> NoMenuExtensionEntriesBuffer = new List<RecipeList.Entry>();
        private static readonly HashSet<int> NoMenuRecipeIdsBuffer = new HashSet<int>();
        private static readonly Dictionary<int, AssembledDefinitionNode> NoMenuSimplifiedRecipesById = new Dictionary<int, AssembledDefinitionNode>();

        private static ConfigEntry<bool> enabled;
        private static bool activeForRound;
        private static bool roundStateInitialized;
        private static LevelConfigBase roundLevelConfig;
        private static NoMenuIneligibility roundIneligibility = NoMenuIneligibility.Disabled;
        private static bool syntheticDeliveryFailureLogged;
        private static bool publicOnlineSessionRequested;
        private static bool noMenuRecipesInitialized;
        private static int noMenuRecipePhaseIndex = int.MinValue;

        public static bool IsReady
        {
            get { return enabled != null; }
        }

        public static bool IsEnabled
        {
            get { return enabled != null && enabled.Value; }
        }

        internal static bool IsActiveForRound
        {
            get { return activeForRound; }
        }

        public static void Awake()
        {
            if (_MODEntry.SettingsConfig.Remove(LegacyToggleKeyDefinition))
            {
                _MODEntry.SettingsConfig.Save();
            }

            enabled = _MODEntry.SettingsConfig.Bind<bool>(
                "00-菜单管理",
                "无菜单",
                false,
                "启用内置无菜单模式。设置在下一局开始时生效；不依赖外部 OC2NoMenu.dll。");
            ModuleUtility.RegisterHarmony(typeof(NoMenuMode));
        }

        public static void ToggleEnabled()
        {
            if (enabled != null)
            {
                enabled.Value = !enabled.Value;
            }
        }

        public static void SetEnabled(bool value)
        {
            if (enabled != null)
            {
                enabled.Value = value;
            }
        }

        public static void Shutdown()
        {
            TryResetRoundState(true, "shutdown");
        }

        internal static string GetStatusText(bool chinese)
        {
            if (activeForRound)
            {
                if (IsEnabled)
                {
                    return chinese ? "无菜单已在本局启用。" : "No Menu is active for this round.";
                }

                return chinese ? "无菜单仍在本局启用；关闭将在下一局生效。" : "No Menu remains active this round; disabling applies next round.";
            }

            if (!IsEnabled)
            {
                return chinese ? "无菜单已关闭。" : "No Menu is disabled.";
            }

            if (!roundStateInitialized || roundIneligibility == NoMenuIneligibility.Disabled)
            {
                return chinese ? "无菜单将在下一局受支持的关卡中启用。" : "No Menu will activate in the next supported round.";
            }

            return GetIneligibilityText(roundIneligibility, chinese);
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
        [HarmonyPatch(typeof(ServerFlowControllerBase), "StartSynchronising")]
        private static void ServerFlowControllerBase_StartSynchronising_Postfix(ServerFlowControllerBase __instance)
        {
            if (__instance != null)
            {
                BeginRound(
                    __instance.GetLevelConfig(),
                    __instance is ServerBossFlowController,
                    __instance is ServerCompetitiveFlowController);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ServerOrderControllerBase), "AddNewOrder", new Type[] { })]
        private static bool ServerOrderControllerBase_AddAutomaticOrder_Prefix()
        {
            return !activeForRound;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCampaignFlowController), "StartSynchronising")]
        private static void ServerCampaignFlowController_StartSynchronising_Postfix(ServerCampaignFlowController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            BeginRound(__instance.GetLevelConfig(), __instance is ServerBossFlowController, false);
            if (activeForRound)
            {
                ServerTeamMonitor monitor = __instance.GetMonitorForTeam(TeamID.One);
                if (TryDeactivateForBootstrapOrders(monitor))
                {
                    return;
                }

                SetAutoProgress(monitor, false);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCompetitiveFlowController), "StartSynchronising")]
        private static void ServerCompetitiveFlowController_StartSynchronising_Postfix(ServerCompetitiveFlowController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            BeginRound(__instance.GetLevelConfig(), false, true);
            if (activeForRound)
            {
                ServerTeamMonitor teamOneMonitor = __instance.GetMonitorForTeam(TeamID.One);
                ServerTeamMonitor teamTwoMonitor = __instance.GetMonitorForTeam(TeamID.Two);
                if (TryDeactivateForBootstrapOrders(teamOneMonitor)
                    || TryDeactivateForBootstrapOrders(teamTwoMonitor))
                {
                    return;
                }

                SetAutoProgress(teamOneMonitor, false);
                SetAutoProgress(teamTwoMonitor, false);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientCampaignFlowController), "StartSynchronising")]
        private static void ClientCampaignFlowController_StartSynchronising_Postfix(ClientCampaignFlowController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            EnsureClientRoundState(__instance.GetLevelConfig(), __instance is ClientBossFlowController, false);
            ApplyCampaignRecipeBarVisibility(__instance, !activeForRound);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientCompetitiveFlowController), "StartSynchronising")]
        private static void ClientCompetitiveFlowController_StartSynchronising_Postfix(ClientCompetitiveFlowController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            EnsureClientRoundState(__instance.GetLevelConfig(), false, true);
            ApplyCompetitiveRecipeBarVisibility(__instance, !activeForRound);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LoadingScreenFlow), "LoadScene", new[] { typeof(string), typeof(GameState) })]
        private static void LoadingScreenFlow_LoadScene_NoMenu_Prefix()
        {
            TryResetRoundState(true, "scene load");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LoadingScreenFlow), "RequestReturnToStartScreen")]
        private static void LoadingScreenFlow_RequestReturnToStartScreen_NoMenu_Prefix()
        {
            TryResetRoundState(true, "return to start screen");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
        private static bool ServerKitchenFlowControllerBase_OnFoodDelivered_Prefix(
            ServerKitchenFlowControllerBase __instance,
            AssembledDefinitionNode _definition,
            PlatingStepData _plateType,
            ServerPlateStation _station)
        {
            try
            {
                return HandleNoMenuDelivery(__instance, _definition, _plateType, _station);
            }
            catch (Exception ex)
            {
                // A compatibility failure before the synthetic order is committed must fall
                // through to the game's original delivery path.  Harmony prefixes should never
                // turn an otherwise valid delivery into an exception.
                Exception cause = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;
                if (!syntheticDeliveryFailureLogged)
                {
                    syntheticDeliveryFailureLogged = true;
                    _MODEntry.LogError("[NoMenu] Delivery compatibility check failed; restoring the normal menu for this round: "
                        + cause.GetType().Name + ": " + cause.Message);
                }

                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return true;
            }
        }

        private static bool HandleNoMenuDelivery(
            ServerKitchenFlowControllerBase __instance,
            AssembledDefinitionNode definition,
            PlatingStepData plateType,
            ServerPlateStation station)
        {
            if (!ShouldHandleNoMenuDelivery(__instance, station) || !CanUseSyntheticFallback(definition))
            {
                return true;
            }

            ServerTeamMonitor monitor = __instance.GetMonitorForTeam(station.GetTeamID());
            if (monitor == null || monitor.OrdersController == null)
            {
                return true;
            }

            if (IsAutoProgressEnabled(monitor))
            {
                SetAutoProgress(monitor, false);
            }

            RecipeList.Entry matchedEntry;
            if (!TryFindNoMenuRecipeMatch(monitor, definition, plateType, out matchedEntry))
            {
                return true;
            }

            PlateReturnController plateReturnController = PlateReturnControllerField != null
                ? PlateReturnControllerField.GetValue(__instance) as PlateReturnController
                : null;
            MethodInfo successfulDeliveryMethod = GetSuccessfulDeliveryMethod(__instance.GetType());
            if (plateReturnController == null || successfulDeliveryMethod == null || AddSpecificOrderMethod == null)
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return true;
            }

            bool syntheticMutationStarted;
            ServerOrderData syntheticOrder = AddSyntheticOrder(monitor, matchedEntry, out syntheticMutationStarted);
            if (syntheticOrder == null)
            {
                if (!syntheticMutationStarted)
                {
                    return true;
                }

                TryReturnDeliveredPlate(plateReturnController, definition, plateType, station);
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return false;
            }

            bool orderRemoved = false;
            bool plateReturnAttempted = false;
            try
            {
                bool restartCombo = monitor.Score.TotalCombo == 0;
                bool wasCombo = monitor.OrdersController.IsComboOrder(syntheticOrder.ID, restartCombo);
                monitor.OrdersController.RemoveOrder(syntheticOrder.ID);
                orderRemoved = true;
                plateReturnAttempted = true;
                plateReturnController.FoodDelivered(definition, plateType, station);
                successfulDeliveryMethod.Invoke(__instance, new object[] { syntheticOrder.ID, matchedEntry, 1f, wasCombo, station });
                return false;
            }
            catch (Exception ex)
            {
                if (!orderRemoved)
                {
                    TryRemoveSyntheticOrder(monitor, syntheticOrder.ID);
                }
                if (!plateReturnAttempted)
                {
                    plateReturnAttempted = true;
                    try
                    {
                        plateReturnController.FoodDelivered(definition, plateType, station);
                    }
                    catch
                    {
                    }
                }

                if (!syntheticDeliveryFailureLogged)
                {
                    syntheticDeliveryFailureLogged = true;
                    Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    _MODEntry.LogError("[NoMenu] Synthetic delivery failed after commit: " + cause.GetType().Name + ": " + cause.Message);
                }

                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return false;
            }
            finally
            {
                ForgetSyntheticOrder(syntheticOrder.ID);
            }
        }

        private static void TryReturnDeliveredPlate(
            PlateReturnController plateReturnController,
            AssembledDefinitionNode definition,
            PlatingStepData plateType,
            ServerPlateStation station)
        {
            if (plateReturnController == null || station == null)
            {
                return;
            }

            try
            {
                plateReturnController.FoodDelivered(definition, plateType, station);
            }
            catch
            {
            }
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
        [HarmonyPatch(typeof(FrontendCoopTabOptions), "LoadLobby")]
        [HarmonyPatch(typeof(FrontendVersusTabOptions), "LoadLobby")]
        private static void Frontend_LoadLobby_Prefix(OnlineMultiplayerSessionVisibility _visiblity)
        {
            publicOnlineSessionRequested = _visiblity == OnlineMultiplayerSessionVisibility.ePublic
                || _visiblity == OnlineMultiplayerSessionVisibility.eMatchmaking;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FrontendCoopTabOptions), "OnCouchPlayClicked")]
        [HarmonyPatch(typeof(FrontendCoopTabOptions), "OnLocalPlayClicked")]
        [HarmonyPatch(typeof(FrontendVersusTabOptions), "OnCouchPlayClicked")]
        [HarmonyPatch(typeof(FrontendVersusTabOptions), "OnLocalPlayClicked")]
        private static void Frontend_LocalPlay_Prefix()
        {
            publicOnlineSessionRequested = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientLobbyFlowController), "OnLobbyServerMessage")]
        private static void ClientLobbyFlowController_OnLobbyServerMessage_Postfix(ClientLobbyFlowController __instance)
        {
            if (__instance == null || ClientLobbySessionVisibilityField == null)
            {
                return;
            }

            object value = ClientLobbySessionVisibilityField.GetValue(__instance);
            if (value is OnlineMultiplayerSessionVisibility)
            {
                OnlineMultiplayerSessionVisibility visibility = (OnlineMultiplayerSessionVisibility)value;
                publicOnlineSessionRequested = visibility == OnlineMultiplayerSessionVisibility.ePublic
                    || visibility == OnlineMultiplayerSessionVisibility.eMatchmaking;
            }
        }

        private static void BeginRound(LevelConfigBase levelConfig, bool isBossFlow, bool isVersusFlow)
        {
            if (roundStateInitialized && ReferenceEquals(roundLevelConfig, levelConfig))
            {
                return;
            }

            ClearSyntheticOrders();
            roundStateInitialized = true;
            roundLevelConfig = levelConfig;
            syntheticDeliveryFailureLogged = false;
            ResetNoMenuRecipeCache();

            KitchenLevelConfigBase kitchenLevelConfig = levelConfig as KitchenLevelConfigBase;
            GameSession session = GameUtils.GetGameSession();
            bool isTutorial = NoMenuIdentifierPolicy.IsTutorial(
                levelConfig != null ? levelConfig.name : null,
                GetCurrentSceneName(session));
            bool isSurvival = session != null && session.GameModeKind == Kind.Survival;
            bool isSupportedKitchen = kitchenLevelConfig != null
                && session != null
                && (isVersusFlow || session.GameModeKind != Kind.Practice);
            bool hasPreTimerOrders = session != null
                && session.GameModeKind == Kind.Campaign
                && kitchenLevelConfig != null
                && kitchenLevelConfig.m_recipesBeforeTimerStarts > 0;
            RoundDataBase configuredRoundData = null;
            try
            {
                configuredRoundData = kitchenLevelConfig != null ? kitchenLevelConfig.GetRoundData() : null;
            }
            catch
            {
            }
            bool hasDynamicRuntimeContract = !(configuredRoundData is DynamicRoundData)
                || (RoundInstanceDataField != null && DynamicRoundInstanceCurrentPhaseField != null);
            bool hasRuntimeContract = PlateReturnControllerField != null
                && AutoProgressField != null
                && NextOrderIdField != null
                && ActiveOrdersField != null
                && AddSpecificOrderMethod != null
                && SuccessfulDeliveryBaseMethod != null
                && hasDynamicRuntimeContract;

            roundIneligibility = NoMenuRoundPolicy.Evaluate(
                IsEnabled,
                isSupportedKitchen,
                isBossFlow || levelConfig is BossCampaignLevelConfig,
                isTutorial,
                isSurvival,
                hasPreTimerOrders,
                publicOnlineSessionRequested,
                hasRuntimeContract);
            SetRoundActive(roundIneligibility == NoMenuIneligibility.None);
            if (IsEnabled && !activeForRound)
            {
                _MODEntry.LogInfo("[NoMenu] Not active for this round: " + GetIneligibilityText(roundIneligibility, false));
            }
        }

        private static void EnsureClientRoundState(LevelConfigBase levelConfig, bool isBossFlow, bool isVersusFlow)
        {
            if (roundStateInitialized && ReferenceEquals(roundLevelConfig, levelConfig))
            {
                return;
            }

            bool isInOnlineSession;
            bool isHost;
            if (TryGetConnectionAuthority(out isInOnlineSession, out isHost)
                && !NoMenuClientAuthorityPolicy.ShouldInitializeLocalRoundState(isInOnlineSession, isHost))
            {
                ClearSyntheticOrders();
                ResetNoMenuRecipeCache();
                roundStateInitialized = true;
                roundLevelConfig = levelConfig;
                roundIneligibility = NoMenuIneligibility.RemoteClient;
                SetRoundActive(false);
                return;
            }

            BeginRound(levelConfig, isBossFlow, isVersusFlow);
        }

        private static void SetRoundActive(bool active)
        {
            bool changed = activeForRound != active;
            activeForRound = active;
            if (changed)
            {
                try
                {
                    ServedDishTracker.OnNoMenuRoundStateChanged(activeForRound);
                }
                catch (Exception ex)
                {
                    _MODEntry.LogWarning("[NoMenu] Failed to notify the tracker about a round-state change: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void TryResetRoundState(bool restoreRuntimeState, string reason)
        {
            try
            {
                ResetRoundState(restoreRuntimeState);
            }
            catch (Exception ex)
            {
                // Harmony prefixes must never prevent the game from changing scenes.  Leave the
                // feature fail-closed even if another mod destroyed a cached UI/runtime object.
                activeForRound = false;
                roundStateInitialized = false;
                roundLevelConfig = null;
                roundIneligibility = IsEnabled ? NoMenuIneligibility.None : NoMenuIneligibility.Disabled;
                syntheticDeliveryFailureLogged = false;
                RecipeBarVisibilityStates.Clear();
                OrderProgressionStates.Clear();
                ClearSyntheticOrders();
                ResetNoMenuRecipeCache();
                _MODEntry.LogWarning("[NoMenu] Cleanup during " + reason + " was incomplete, but the scene transition was allowed to continue: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ResetRoundState(bool restoreRuntimeState)
        {
            if (restoreRuntimeState)
            {
                RestoreRecipeBarsAndOrderProgression();
            }

            SetRoundActive(false);
            roundStateInitialized = false;
            roundLevelConfig = null;
            roundIneligibility = IsEnabled ? NoMenuIneligibility.None : NoMenuIneligibility.Disabled;
            syntheticDeliveryFailureLogged = false;
            ClearSyntheticOrders();
            ResetNoMenuRecipeCache();
        }

        private static void RestoreRecipeBarsAndOrderProgression()
        {
            foreach (RecipeBarVisibilityState state in RecipeBarVisibilityStates.Values)
            {
                try
                {
                    RestoreRecipeBarVisibilityState(state);
                }
                catch
                {
                    // A scene transition can destroy the UI before this prefix executes.
                }
            }
            RecipeBarVisibilityStates.Clear();

            foreach (OrderProgressionState state in OrderProgressionStates.Values)
            {
                if (state != null && state.Controller != null)
                {
                    try
                    {
                        state.Controller.SetAutoProgress(state.AutoProgress);
                    }
                    catch
                    {
                        // A replacement controller supplied by another mod may already be gone.
                    }
                }
            }
            OrderProgressionStates.Clear();
        }

        private static bool TryDeactivateForBootstrapOrders(ServerTeamMonitor monitor)
        {
            if (monitor == null || monitor.OrdersController == null || ActiveOrdersField == null)
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return true;
            }

            try
            {
                IList activeOrders = ActiveOrdersField.GetValue(monitor.OrdersController) as IList;
                if (activeOrders == null)
                {
                    DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                    return true;
                }

                if (activeOrders.Count == 0)
                {
                    return false;
                }
            }
            catch
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return true;
            }

            DeactivateNoMenu(NoMenuIneligibility.BootstrapOrders);
            return true;
        }

        private static void DeactivateNoMenu(NoMenuIneligibility reason)
        {
            roundIneligibility = reason;
            SetRoundActive(false);
            RestoreRecipeBarsAndOrderProgression();
            if (IsEnabled)
            {
                _MODEntry.LogWarning("[NoMenu] Safely disabled for this round: " + GetIneligibilityText(reason, false));
            }
        }

        private static bool ShouldHandleNoMenuDelivery(ServerKitchenFlowControllerBase flow, ServerPlateStation station)
        {
            if (!activeForRound || flow == null || station == null)
            {
                return false;
            }

            return roundLevelConfig is KitchenLevelConfigBase
                && !(roundLevelConfig is BossCampaignLevelConfig);
        }

        private static bool TryFindNoMenuRecipeMatch(ServerTeamMonitor monitor, AssembledDefinitionNode definition, PlatingStepData plateType, out RecipeList.Entry matchedEntry)
        {
            matchedEntry = null;
            AssembledDefinitionNode simplifiedDefinition;
            try
            {
                simplifiedDefinition = definition != null ? definition.Simpilfy() : null;
            }
            catch
            {
                return false;
            }

            if (simplifiedDefinition == null || simplifiedDefinition == AssembledDefinitionNode.NullNode)
            {
                return false;
            }

            List<RecipeList.Entry> entries;
            try
            {
                entries = GetRecipesForCurrentLevel(monitor);
            }
            catch
            {
                return false;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                RecipeList.Entry candidate = entries[i];
                if (candidate != null && candidate.m_order != null && MatchesRecipe(candidate.m_order, simplifiedDefinition, plateType))
                {
                    matchedEntry = candidate;
                    return true;
                }
            }

            return false;
        }

        private static List<RecipeList.Entry> GetRecipesForCurrentLevel(ServerTeamMonitor monitor)
        {
            ServerOrderControllerBase orderController = monitor != null ? monitor.OrdersController : null;
            RoundDataBase roundData = orderController != null && RoundDataField != null
                ? RoundDataField.GetValue(orderController) as RoundDataBase
                : null;
            KitchenLevelConfigBase levelConfig = roundLevelConfig as KitchenLevelConfigBase;
            if (roundData == null && levelConfig != null)
            {
                roundData = levelConfig.GetRoundData();
            }

            if (roundData == null && levelConfig == null)
            {
                return NoMenuRecipeEntriesBuffer;
            }

            DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
            int phaseIndex = dynamicRoundData != null
                ? GetCurrentDynamicPhaseIndex(orderController, dynamicRoundData)
                : -1;
            if (noMenuRecipesInitialized && noMenuRecipePhaseIndex == phaseIndex)
            {
                return NoMenuRecipeEntriesBuffer;
            }

            NoMenuRecipeEntriesBuffer.Clear();
            NoMenuRecipeIdsBuffer.Clear();
            if (dynamicRoundData != null && dynamicRoundData.Phases != null)
            {
                if (phaseIndex >= 0 && phaseIndex < dynamicRoundData.Phases.Length)
                {
                    AddRecipeEntriesDistinct(dynamicRoundData.Phases[phaseIndex].Recipes);
                }
            }
            else
            {
                RoundData standardRoundData = roundData as RoundData;
                if (standardRoundData != null)
                {
                    AddRecipeEntriesDistinct(standardRoundData.m_recipes);
                }
            }

            NoMenuExtensionEntriesBuffer.Clear();
            ServedDishTracker.AppendRecipeExtensionEntries(
                NoMenuExtensionEntriesBuffer,
                levelConfig,
                dynamicRoundData == null,
                Math.Max(0, phaseIndex));
            for (int i = 0; i < NoMenuExtensionEntriesBuffer.Count; i++)
            {
                AddRecipeEntryDistinct(NoMenuExtensionEntriesBuffer[i]);
            }

            noMenuRecipesInitialized = true;
            noMenuRecipePhaseIndex = phaseIndex;
            return NoMenuRecipeEntriesBuffer;
        }

        private static int GetCurrentDynamicPhaseIndex(ServerOrderControllerBase orderController, DynamicRoundData dynamicRoundData)
        {
            if (dynamicRoundData == null || dynamicRoundData.Phases == null || dynamicRoundData.Phases.Length == 0)
            {
                return 0;
            }

            if (orderController != null && RoundInstanceDataField != null && DynamicRoundInstanceCurrentPhaseField != null)
            {
                try
                {
                    object instanceData = RoundInstanceDataField.GetValue(orderController);
                    object phaseValue = instanceData != null
                        ? DynamicRoundInstanceCurrentPhaseField.GetValue(instanceData)
                        : null;
                    if (phaseValue is int)
                    {
                        int phaseIndex = (int)phaseValue;
                        if (phaseIndex >= 0 && phaseIndex < dynamicRoundData.Phases.Length)
                        {
                            return phaseIndex;
                        }
                    }
                }
                catch
                {
                }
            }

            return 0;
        }

        private static void AddRecipeEntriesDistinct(RecipeList recipeList)
        {
            if (recipeList == null || recipeList.m_recipes == null)
            {
                return;
            }

            for (int i = 0; i < recipeList.m_recipes.Length; i++)
            {
                AddRecipeEntryDistinct(recipeList.m_recipes[i]);
            }
        }

        private static void AddRecipeEntryDistinct(RecipeList.Entry entry)
        {
            if (entry == null || entry.m_order == null || !NoMenuRecipeIdsBuffer.Add(entry.m_order.m_uID))
            {
                return;
            }

            NoMenuRecipeEntriesBuffer.Add(entry);
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

            return true;
        }

        private static bool MatchesRecipe(OrderDefinitionNode required, AssembledDefinitionNode simplifiedProvided, PlatingStepData plateType)
        {
            if (required == null || simplifiedProvided == null || required.m_platingStep != plateType)
            {
                return false;
            }

            try
            {
                AssembledDefinitionNode simplifiedRequired;
                if (!NoMenuSimplifiedRecipesById.TryGetValue(required.m_uID, out simplifiedRequired))
                {
                    simplifiedRequired = required.Simpilfy();
                    NoMenuSimplifiedRecipesById[required.m_uID] = simplifiedRequired;
                }

                if (simplifiedRequired == null)
                {
                    return false;
                }

                return required.GetType() == typeof(WildcardOrderNode)
                    ? AssembledDefinitionNode.MatchingAlreadySimple(simplifiedRequired, simplifiedProvided)
                    : AssembledDefinitionNode.MatchingAlreadySimple(simplifiedProvided, simplifiedRequired);
            }
            catch
            {
                return false;
            }
        }

        private static ServerOrderData AddSyntheticOrder(ServerTeamMonitor monitor, RecipeList.Entry entry, out bool mutationStarted)
        {
            mutationStarted = false;
            if (monitor == null || monitor.OrdersController == null || AddSpecificOrderMethod == null || entry == null)
            {
                return null;
            }

            uint reservedOrderId = 0u;
            try
            {
                if (NextOrderIdField != null)
                {
                    object nextIdValue = NextOrderIdField.GetValue(monitor.OrdersController);
                    if (nextIdValue is uint)
                    {
                        reservedOrderId = (uint)nextIdValue;
                        SyntheticOrderIds.Add(reservedOrderId);
                    }
                }

                mutationStarted = true;
                ServerOrderData syntheticOrder = AddSpecificOrderMethod.Invoke(monitor.OrdersController, new object[] { entry }) as ServerOrderData;
                if (syntheticOrder == null)
                {
                    if (reservedOrderId != 0u)
                    {
                        TryRemoveSyntheticOrder(monitor, new OrderID(reservedOrderId));
                    }

                    return null;
                }

                if (reservedOrderId != 0u && syntheticOrder.ID.m_id != reservedOrderId)
                {
                    SyntheticOrderIds.Remove(reservedOrderId);
                }

                SyntheticOrderIds.Add(syntheticOrder.ID.m_id);
                return syntheticOrder;
            }
            catch (Exception ex)
            {
                if (reservedOrderId != 0u)
                {
                    TryRemoveSyntheticOrder(monitor, new OrderID(reservedOrderId));
                }

                if (!syntheticDeliveryFailureLogged)
                {
                    syntheticDeliveryFailureLogged = true;
                    Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    _MODEntry.LogError("[NoMenu] Could not create a synthetic order: " + cause.GetType().Name + ": " + cause.Message);
                }

                return null;
            }
        }

        private static void TryRemoveSyntheticOrder(ServerTeamMonitor monitor, OrderID orderId)
        {
            try
            {
                if (monitor != null && monitor.OrdersController != null && orderId.m_id != 0u)
                {
                    monitor.OrdersController.RemoveOrder(orderId);
                }
            }
            catch
            {
            }

            ForgetSyntheticOrder(orderId);
        }

        private static MethodInfo GetSuccessfulDeliveryMethod(Type type)
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

            method = AccessTools.Method(
                type,
                "OnSuccessfulDelivery",
                new[] { typeof(OrderID), typeof(RecipeList.Entry), typeof(float), typeof(bool), typeof(ServerPlateStation) });
            SuccessfulDeliveryMethodCache[type] = method;
            return method;
        }

        private static bool IsAutoProgressEnabled(ServerTeamMonitor monitor)
        {
            return AutoProgressField != null
                && monitor != null
                && monitor.OrdersController != null
                && (bool)AutoProgressField.GetValue(monitor.OrdersController);
        }

        private static void SetAutoProgress(ServerTeamMonitor monitor, bool autoProgress)
        {
            if (monitor == null || monitor.OrdersController == null || AutoProgressField == null)
            {
                return;
            }

            ServerOrderControllerBase controller = monitor.OrdersController;
            if (!OrderProgressionStates.ContainsKey(controller))
            {
                try
                {
                    OrderProgressionState state = new OrderProgressionState();
                    state.Controller = controller;
                    state.AutoProgress = (bool)AutoProgressField.GetValue(controller);
                    OrderProgressionStates[controller] = state;
                }
                catch
                {
                    return;
                }
            }

            controller.SetAutoProgress(autoProgress);
        }

        private static void ApplyCampaignRecipeBarVisibility(ClientCampaignFlowController flow, bool visible)
        {
            if (flow == null || flow is ClientBossFlowController || ClientCampaignTeamMonitorField == null)
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
                SetRecipeBarVisible(GetRecipeBar(ClientCompetitiveTeamOneMonitorField.GetValue(flow) as ClientTeamMonitor), visible);
            }

            if (ClientCompetitiveTeamTwoMonitorField != null)
            {
                SetRecipeBarVisible(GetRecipeBar(ClientCompetitiveTeamTwoMonitorField.GetValue(flow) as ClientTeamMonitor), visible);
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

            int instanceId = recipeBar.GetInstanceID();
            RecipeBarVisibilityState state;
            if (RecipeBarVisibilityStates.TryGetValue(instanceId, out state))
            {
                if (state != null && state.RecipeBar == recipeBar)
                {
                    if (visible)
                    {
                        RestoreRecipeBarVisibilityState(state);
                        RecipeBarVisibilityStates.Remove(instanceId);
                    }
                    else if (state.CanvasGroup != null)
                    {
                        state.CanvasGroup.alpha = 0f;
                        state.CanvasGroup.interactable = false;
                        state.CanvasGroup.blocksRaycasts = false;
                    }

                    return;
                }

                RestoreRecipeBarVisibilityState(state);
                RecipeBarVisibilityStates.Remove(instanceId);
            }

            if (visible)
            {
                return;
            }

            CanvasGroup canvasGroup = recipeBar.gameObject.GetComponent<CanvasGroup>();
            bool canvasGroupCreated = false;
            if (canvasGroup == null)
            {
                canvasGroup = recipeBar.gameObject.AddComponent<CanvasGroup>();
                canvasGroupCreated = true;
            }

            state = new RecipeBarVisibilityState();
            state.RecipeBar = recipeBar;
            state.CanvasGroup = canvasGroup;
            state.CanvasGroupCreated = canvasGroupCreated;
            state.Alpha = canvasGroup.alpha;
            state.Interactable = canvasGroup.interactable;
            state.BlocksRaycasts = canvasGroup.blocksRaycasts;
            RecipeBarVisibilityStates[instanceId] = state;

            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        private static void RestoreRecipeBarVisibilityState(RecipeBarVisibilityState state)
        {
            if (state == null || state.CanvasGroup == null)
            {
                return;
            }

            state.CanvasGroup.alpha = state.Alpha;
            state.CanvasGroup.interactable = state.Interactable;
            state.CanvasGroup.blocksRaycasts = state.BlocksRaycasts;
            if (state.CanvasGroupCreated)
            {
                UnityEngine.Object.Destroy(state.CanvasGroup);
            }
        }

        private static bool TryGetConnectionAuthority(out bool isInOnlineSession, out bool isHost)
        {
            isInOnlineSession = false;
            isHost = false;
            if (ConnectionIsInSessionMethod == null || ConnectionIsHostMethod == null)
            {
                return false;
            }

            try
            {
                object sessionValue = ConnectionIsInSessionMethod.Invoke(null, null);
                object hostValue = ConnectionIsHostMethod.Invoke(null, null);
                if (!(sessionValue is bool) || !(hostValue is bool))
                {
                    return false;
                }

                isInOnlineSession = (bool)sessionValue;
                isHost = (bool)hostValue;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetCurrentSceneName(GameSession session)
        {
            try
            {
                return session != null
                    && session.LevelSettings != null
                    && session.LevelSettings.SceneDirectoryVarientEntry != null
                    ? session.LevelSettings.SceneDirectoryVarientEntry.SceneName
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo ResolveDynamicRoundPhaseField()
        {
            Type dynamicRoundInstanceType = typeof(DynamicRoundData).GetNestedType(
                "DynamicRoundInstanceData",
                BindingFlags.Public | BindingFlags.NonPublic);
            return dynamicRoundInstanceType != null
                ? dynamicRoundInstanceType.GetField("CurrentPhase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                : null;
        }

        private static string GetIneligibilityText(NoMenuIneligibility reason, bool chinese)
        {
            switch (reason)
            {
                case NoMenuIneligibility.UnsupportedLevel:
                    return chinese ? "本关不是受支持的厨房关卡，保留正常菜单。" : "This is not a supported kitchen level; the normal menu remains enabled.";
                case NoMenuIneligibility.Boss:
                    return chinese ? "Boss 关卡不支持无菜单，保留正常菜单。" : "Boss levels do not support No Menu; the normal menu remains enabled.";
                case NoMenuIneligibility.Tutorial:
                    return chinese ? "教程关卡不支持无菜单，保留正常菜单。" : "Tutorial levels do not support No Menu; the normal menu remains enabled.";
                case NoMenuIneligibility.Survival:
                    return chinese ? "生存模式不支持无菜单，保留正常菜单。" : "Survival mode does not support No Menu; the normal menu remains enabled.";
                case NoMenuIneligibility.PreTimerOrders:
                    return chinese ? "需要用菜单启动计时器的关卡不支持无菜单。" : "Levels that require orders before the timer starts do not support No Menu.";
                case NoMenuIneligibility.PublicOnline:
                    return chinese ? "公开联机关卡不支持无菜单，保留正常菜单。" : "Public online sessions do not support No Menu; the normal menu remains enabled.";
                case NoMenuIneligibility.MissingRuntimeContract:
                    return chinese ? "当前游戏版本缺少无菜单所需接口，已安全停用。" : "Required runtime hooks are unavailable; No Menu was safely disabled.";
                case NoMenuIneligibility.RemoteClient:
                    return chinese ? "无菜单由主机控制；远程客户端保留服务器菜单。" : "No Menu is host-controlled; remote clients keep the server-provided menu.";
                case NoMenuIneligibility.BootstrapOrders:
                    return chinese ? "本关启动时创建了特殊订单，已保留正常菜单以避免订单不同步。" : "This level creates special startup orders; the normal menu was kept to avoid desynchronizing them.";
                case NoMenuIneligibility.Disabled:
                    return chinese ? "无菜单已关闭。" : "No Menu is disabled.";
                default:
                    return chinese ? "无菜单将在下一局生效。" : "No Menu will apply next round.";
            }
        }

        private static void ClearSyntheticOrders()
        {
            SyntheticOrderIds.Clear();
        }

        private static void ResetNoMenuRecipeCache()
        {
            noMenuRecipesInitialized = false;
            noMenuRecipePhaseIndex = int.MinValue;
            NoMenuRecipeEntriesBuffer.Clear();
            NoMenuExtensionEntriesBuffer.Clear();
            NoMenuRecipeIdsBuffer.Clear();
            NoMenuSimplifiedRecipesById.Clear();
        }
    }
}
