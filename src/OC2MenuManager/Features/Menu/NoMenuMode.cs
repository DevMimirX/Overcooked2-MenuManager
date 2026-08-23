// Implements a server-authoritative No Menu delivery transaction. Round state
// activates only after concrete campaign/competitive flows finish initialization,
// and reflected recipe pools are accepted only when they match live round data.
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
    /// <summary>
    /// Owns No Menu round eligibility, recipe-bar visibility, order progression,
    /// and the temporary order transaction that delegates scoring to the base game.
    /// Any contract mismatch restores normal behavior for the current round.
    /// </summary>
    internal static class NoMenuMode
    {
        /// <summary>Captures UI state so round shutdown can restore the original recipe bar.</summary>
        private sealed class RecipeBarVisibilityState
        {
            public RecipeFlowGUI RecipeBar;
            public CanvasGroup CanvasGroup;
            public bool CanvasGroupCreated;
            public float Alpha;
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        /// <summary>Captures server auto-progression state owned by the active round.</summary>
        private sealed class OrderProgressionState
        {
            public ServerOrderControllerBase Controller;
            public bool AutoProgress;
        }

        /// <summary>Tracks one temporary order through base-game delivery and compensation.</summary>
        private sealed class SyntheticDeliveryTransaction
        {
            public bool Injected;
            public bool Completed;
            public TeamID TeamId;
            public OrderID OrderId;
            public ServerTeamMonitor Monitor;
            public ClientKitchenFlowControllerBase ClientFlow;
        }

        private static readonly ConfigDefinition LegacyToggleKeyDefinition = new ConfigDefinition("00-菜单管理", "切换无菜单热键");
        private static readonly FieldInfo ClientCampaignTeamMonitorField = AccessTools.Field(typeof(ClientCampaignFlowController), "m_teamMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamOneMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamOneMonitor");
        private static readonly FieldInfo ClientCompetitiveTeamTwoMonitorField = AccessTools.Field(typeof(ClientCompetitiveFlowController), "m_teamTwoMonitor");
        private static readonly FieldInfo ClientTeamMonitorMonitorField = AccessTools.Field(typeof(ClientTeamMonitor), "m_monitor");
        private static readonly FieldInfo TeamMonitorRecipeBarField = AccessTools.Field(typeof(TeamMonitor), "m_recipeBarUIController");
        private static readonly FieldInfo AutoProgressField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_autoProgress");
        private static readonly FieldInfo NextOrderIdField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_nextOrderID");
        private static readonly FieldInfo RoundDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundData");
        private static readonly FieldInfo RoundInstanceDataField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_roundInstanceData");
        private static readonly FieldInfo ActiveOrdersField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_activeOrders");
        private static readonly FieldInfo RoundInstanceCumulativeFrequenciesField = ResolveRoundInstanceCumulativeFrequenciesField();
        private static readonly FieldInfo DynamicRoundInstanceCurrentPhaseField = ResolveDynamicRoundPhaseField();
        private static readonly Type ConnectionStatusType = AccessTools.TypeByName("ConnectionStatus");
        private static readonly MethodInfo ConnectionIsInSessionMethod = ConnectionStatusType != null
            ? AccessTools.Method(ConnectionStatusType, "IsInSession", new Type[0])
            : null;
        private static readonly MethodInfo AddSpecificOrderMethod = AccessTools.Method(typeof(ServerOrderControllerBase), "AddNewOrder", new[] { typeof(RecipeList.Entry) });
        private static readonly HashSet<TeamScopedOrderKey> SyntheticOrderIds = new HashSet<TeamScopedOrderKey>();
        private static readonly HashSet<TeamScopedOrderKey> CompensatedSyntheticOrderIds = new HashSet<TeamScopedOrderKey>();
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
        private static bool roundSynchronizationFailureLogged;
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

        public static bool IsSyntheticOrder(TeamID teamId, OrderID orderId)
        {
            return orderId.m_id != 0u
                && SyntheticOrderIds.Contains(new TeamScopedOrderKey((int)teamId, orderId.m_id));
        }

        public static void ForgetSyntheticOrder(TeamID teamId, OrderID orderId)
        {
            if (orderId.m_id != 0u)
            {
                TeamScopedOrderKey key = new TeamScopedOrderKey((int)teamId, orderId.m_id);
                SyntheticOrderIds.Remove(key);
                CompensatedSyntheticOrderIds.Remove(key);
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

            try
            {
                TryBeginRound(__instance.GetLevelConfig(), __instance is ServerBossFlowController, false);
                if (activeForRound)
                {
                    ServerTeamMonitor monitor = __instance.GetMonitorForTeam(TeamID.One);
                    if (!TryDeactivateForBootstrapOrders(monitor))
                    {
                        SetAutoProgress(monitor, false);
                    }
                }

                ApplyCampaignRecipeBarVisibility(
                    __instance.GetComponent<ClientCampaignFlowController>(),
                    !activeForRound);
            }
            catch (Exception ex)
            {
                HandleRoundSynchronizationFailure("initializing the campaign server flow", ex);
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

            try
            {
                TryBeginRound(__instance.GetLevelConfig(), false, true);
                if (activeForRound)
                {
                    ServerTeamMonitor teamOneMonitor = __instance.GetMonitorForTeam(TeamID.One);
                    ServerTeamMonitor teamTwoMonitor = __instance.GetMonitorForTeam(TeamID.Two);
                    if (!TryDeactivateForBootstrapOrders(teamOneMonitor)
                        && !TryDeactivateForBootstrapOrders(teamTwoMonitor))
                    {
                        SetAutoProgress(teamOneMonitor, false);
                        SetAutoProgress(teamTwoMonitor, false);
                    }
                }

                ApplyCompetitiveRecipeBarVisibility(
                    __instance.GetComponent<ClientCompetitiveFlowController>(),
                    !activeForRound);
            }
            catch (Exception ex)
            {
                HandleRoundSynchronizationFailure("initializing the competitive server flow", ex);
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

            try
            {
                bool hasAuthoritativeServerFlow = __instance.GetComponent<ServerCampaignFlowController>() != null;
                if (NoMenuClientAuthorityPolicy.ShouldInitializeLocalRoundState(hasAuthoritativeServerFlow))
                {
                    EnsureClientRoundState(__instance.GetLevelConfig(), __instance is ClientBossFlowController, false);
                }

                ApplyCampaignRecipeBarVisibility(__instance, !activeForRound);
            }
            catch (Exception ex)
            {
                HandleRoundSynchronizationFailure("initializing the campaign client flow", ex);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientCompetitiveFlowController), "StartSynchronising")]
        private static void ClientCompetitiveFlowController_StartSynchronising_Postfix(ClientCompetitiveFlowController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            try
            {
                bool hasAuthoritativeServerFlow = __instance.GetComponent<ServerCompetitiveFlowController>() != null;
                if (NoMenuClientAuthorityPolicy.ShouldInitializeLocalRoundState(hasAuthoritativeServerFlow))
                {
                    EnsureClientRoundState(__instance.GetLevelConfig(), false, true);
                }

                ApplyCompetitiveRecipeBarVisibility(__instance, !activeForRound);
            }
            catch (Exception ex)
            {
                HandleRoundSynchronizationFailure("initializing the competitive client flow", ex);
            }
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
        private static void ServerKitchenFlowControllerBase_OnFoodDelivered_Prefix(
            ServerKitchenFlowControllerBase __instance,
            AssembledDefinitionNode _definition,
            PlatingStepData _plateType,
            ServerPlateStation _station,
            out SyntheticDeliveryTransaction __state)
        {
            __state = null;
            try
            {
                __state = PrepareNoMenuDelivery(__instance, _definition, _plateType, _station);
            }
            catch (Exception ex)
            {
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
            }
        }

        private static SyntheticDeliveryTransaction PrepareNoMenuDelivery(
            ServerKitchenFlowControllerBase __instance,
            AssembledDefinitionNode definition,
            PlatingStepData plateType,
            ServerPlateStation station)
        {
            if (!ShouldHandleNoMenuDelivery(__instance, station) || !CanUseSyntheticFallback(definition))
            {
                return null;
            }

            TeamID teamId = station.GetTeamID();
            ServerTeamMonitor monitor = __instance.GetMonitorForTeam(station.GetTeamID());
            if (monitor == null || monitor.OrdersController == null)
            {
                return null;
            }

            if (IsAutoProgressEnabled(monitor))
            {
                SetAutoProgress(monitor, false);
            }

            int activeOrderCount;
            if (!TryGetActiveOrderCount(monitor.OrdersController, out activeOrderCount))
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return null;
            }

            if (activeOrderCount != 0)
            {
                DeactivateNoMenu(NoMenuIneligibility.BootstrapOrders);
                return null;
            }

            RecipeList.Entry matchedEntry;
            if (!TryFindNoMenuRecipeMatch(monitor, definition, plateType, out matchedEntry))
            {
                return null;
            }

            if (AddSpecificOrderMethod == null)
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                return null;
            }

            bool syntheticMutationStarted;
            ServerOrderData syntheticOrder = AddSyntheticOrder(teamId, monitor, matchedEntry, out syntheticMutationStarted);
            if (syntheticOrder == null)
            {
                if (syntheticMutationStarted)
                {
                    DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                }

                return null;
            }

            SyntheticDeliveryTransaction transaction = new SyntheticDeliveryTransaction();
            transaction.Injected = true;
            transaction.TeamId = teamId;
            transaction.OrderId = syntheticOrder.ID;
            transaction.Monitor = monitor;
            try
            {
                transaction.ClientFlow = __instance.GetComponent<ClientKitchenFlowControllerBase>();
            }
            catch (Exception ex)
            {
                LogNoMenuHookFailure("resolving the local client flow for delivery cleanup", ex);
            }

            return transaction;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
        private static void ServerKitchenFlowControllerBase_OnFoodDelivered_Postfix(SyntheticDeliveryTransaction __state)
        {
            TryCompleteSyntheticDeliveryTransaction(__state, null);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
        private static Exception ServerKitchenFlowControllerBase_OnFoodDelivered_Finalizer(
            Exception __exception,
            SyntheticDeliveryTransaction __state)
        {
            if (__state == null || !__state.Injected)
            {
                return __exception;
            }

            TryCompleteSyntheticDeliveryTransaction(__state, __exception);
            return null;
        }

        private static void TryCompleteSyntheticDeliveryTransaction(
            SyntheticDeliveryTransaction transaction,
            Exception exception)
        {
            try
            {
                CompleteSyntheticDeliveryTransaction(transaction, exception);
            }
            catch (Exception completionException)
            {
                if (transaction != null && transaction.Injected)
                {
                    transaction.Completed = true;
                    TeamScopedOrderKey key = new TeamScopedOrderKey((int)transaction.TeamId, transaction.OrderId.m_id);
                    CompensatedSyntheticOrderIds.Add(key);
                    TryRemoveSyntheticOrder(transaction.TeamId, transaction.Monitor, transaction.OrderId, false);
                    TryRemoveClientGhostOrder(transaction.ClientFlow, transaction.TeamId, transaction.OrderId);
                }

                if (!syntheticDeliveryFailureLogged)
                {
                    syntheticDeliveryFailureLogged = true;
                    _MODEntry.LogError("[NoMenu] Synthetic delivery cleanup failed, but the base-game delivery call was allowed to finish: "
                        + completionException.GetType().Name + ": " + completionException.Message);
                }

                try
                {
                    DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
                }
                catch
                {
                    activeForRound = false;
                    roundIneligibility = NoMenuIneligibility.MissingRuntimeContract;
                }
            }
        }

        private static void CompleteSyntheticDeliveryTransaction(
            SyntheticDeliveryTransaction transaction,
            Exception exception)
        {
            if (transaction == null
                || !transaction.Injected
                || (transaction.Completed && exception == null))
            {
                return;
            }

            transaction.Completed = true;
            bool orderStillActive = ContainsActiveOrder(transaction.Monitor, transaction.OrderId);
            SyntheticTransactionOutcome outcome = SyntheticTransactionPolicy.Evaluate(
                transaction.Injected,
                orderStillActive,
                exception != null);
            if (outcome == SyntheticTransactionOutcome.Success)
            {
                return;
            }

            TeamScopedOrderKey key = new TeamScopedOrderKey((int)transaction.TeamId, transaction.OrderId.m_id);
            CompensatedSyntheticOrderIds.Add(key);
            TryRemoveSyntheticOrder(transaction.TeamId, transaction.Monitor, transaction.OrderId, false);
            TryRemoveClientGhostOrder(transaction.ClientFlow, transaction.TeamId, transaction.OrderId);

            if (!syntheticDeliveryFailureLogged)
            {
                syntheticDeliveryFailureLogged = true;
                Exception cause = exception is TargetInvocationException && exception.InnerException != null
                    ? exception.InnerException
                    : exception;
                string detail = cause != null
                    ? cause.GetType().Name + ": " + cause.Message
                    : "the injected order was not removed by the original delivery pipeline";
                _MODEntry.LogError("[NoMenu] Synthetic delivery transaction failed: " + detail);
            }

            DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_Postfix(TeamID _teamID, OrderID _orderID)
        {
            try
            {
                ForgetSyntheticOrder(_teamID, _orderID);
            }
            catch (Exception ex)
            {
                LogNoMenuHookFailure("forgetting a delivered synthetic order", ex);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderAdded")]
        private static void ClientKitchenFlowControllerBase_OnOrderAdded_Postfix(
            ClientKitchenFlowControllerBase __instance,
            TeamID _teamID,
            Team17.Online.Multiplayer.Messaging.Serialisable _orderData)
        {
            try
            {
                ServerOrderData orderData = _orderData as ServerOrderData;
                if (__instance == null || orderData == null || orderData.ID.m_id == 0u)
                {
                    return;
                }

                TeamScopedOrderKey key = new TeamScopedOrderKey((int)_teamID, orderData.ID.m_id);
                if (!CompensatedSyntheticOrderIds.Contains(key))
                {
                    return;
                }

                TryRemoveClientGhostOrder(__instance, _teamID, orderData.ID);
            }
            catch (Exception ex)
            {
                LogNoMenuHookFailure("compensating a client synthetic order", ex);
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
            roundSynchronizationFailureLogged = false;
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
            bool hasReadableRoundData = kitchenLevelConfig == null;
            try
            {
                configuredRoundData = kitchenLevelConfig != null ? kitchenLevelConfig.GetRoundData() : null;
                hasReadableRoundData = kitchenLevelConfig == null || configuredRoundData != null;
            }
            catch (Exception ex)
            {
                LogNoMenuHookFailure("reading the configured round data", ex);
            }
            bool hasDynamicRuntimeContract = !(configuredRoundData is DynamicRoundData)
                || (RoundInstanceDataField != null && DynamicRoundInstanceCurrentPhaseField != null);
            bool isInOnlineSession;
            bool hasConnectionAuthority = TryGetConnectionStatus(out isInOnlineSession);
            bool hasRecipeBarContract = ClientTeamMonitorMonitorField != null
                && TeamMonitorRecipeBarField != null
                && (isVersusFlow
                    ? ClientCompetitiveTeamOneMonitorField != null && ClientCompetitiveTeamTwoMonitorField != null
                    : ClientCampaignTeamMonitorField != null);
            bool hasRuntimeContract = AutoProgressField != null
                && NextOrderIdField != null
                && RoundDataField != null
                && ActiveOrdersField != null
                && AddSpecificOrderMethod != null
                && hasReadableRoundData
                && hasConnectionAuthority
                && hasRecipeBarContract
                && hasDynamicRuntimeContract;

            roundIneligibility = NoMenuRoundPolicy.Evaluate(
                IsEnabled,
                isSupportedKitchen,
                isBossFlow || levelConfig is BossCampaignLevelConfig,
                isTutorial,
                isSurvival,
                hasPreTimerOrders,
                hasConnectionAuthority && isInOnlineSession,
                hasRuntimeContract);
            SetRoundActive(roundIneligibility == NoMenuIneligibility.None);
            if (IsEnabled && !activeForRound)
            {
                _MODEntry.LogInfo("[NoMenu] Not active for this round: " + GetIneligibilityText(roundIneligibility, false));
            }
        }

        private static void TryBeginRound(LevelConfigBase levelConfig, bool isBossFlow, bool isVersusFlow)
        {
            try
            {
                BeginRound(levelConfig, isBossFlow, isVersusFlow);
            }
            catch (Exception ex)
            {
                ClearSyntheticOrders();
                ResetNoMenuRecipeCache();
                roundStateInitialized = true;
                roundLevelConfig = levelConfig;
                roundIneligibility = NoMenuIneligibility.MissingRuntimeContract;
                SetRoundActive(false);
                _MODEntry.LogWarning("[NoMenu] Round initialization failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EnsureClientRoundState(LevelConfigBase levelConfig, bool isBossFlow, bool isVersusFlow)
        {
            if (roundStateInitialized && ReferenceEquals(roundLevelConfig, levelConfig))
            {
                return;
            }

            TryBeginRound(levelConfig, isBossFlow, isVersusFlow);
        }

        private static void HandleRoundSynchronizationFailure(string operation, Exception exception)
        {
            try
            {
                DeactivateNoMenu(NoMenuIneligibility.MissingRuntimeContract);
            }
            catch
            {
                activeForRound = false;
                roundIneligibility = NoMenuIneligibility.MissingRuntimeContract;
            }

            LogNoMenuHookFailure(operation, exception);
        }

        private static void LogNoMenuHookFailure(string operation, Exception exception)
        {
            if (roundSynchronizationFailureLogged || exception == null)
            {
                return;
            }

            roundSynchronizationFailureLogged = true;
            _MODEntry.LogWarning("[NoMenu] A compatibility hook failed while " + operation
                + "; normal menu behavior was restored: "
                + exception.GetType().Name + ": " + exception.Message);
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
                roundSynchronizationFailureLogged = false;
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
            roundSynchronizationFailureLogged = false;
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
            catch (Exception ex)
            {
                HandleRoundSynchronizationFailure("resolving the active No Menu recipe pool", ex);
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
            if (dynamicRoundData != null
                && (dynamicRoundData.Phases == null
                    || phaseIndex < 0
                    || phaseIndex >= dynamicRoundData.Phases.Length))
            {
                throw new InvalidOperationException("The active dynamic-round phase could not be resolved from ServerOrderControllerBase.m_roundInstanceData.");
            }

            if (noMenuRecipesInitialized && noMenuRecipePhaseIndex == phaseIndex)
            {
                return NoMenuRecipeEntriesBuffer;
            }

            NoMenuRecipeEntriesBuffer.Clear();
            NoMenuRecipeIdsBuffer.Clear();
            int baseCandidateCount = 0;
            ScriptedRoundData scriptedRoundData = roundData as ScriptedRoundData;
            if (scriptedRoundData != null && scriptedRoundData.m_manualOrder != null)
            {
                for (int i = 0; i < scriptedRoundData.m_manualOrder.Length; i++)
                {
                    AddRecipeEntryDistinct(scriptedRoundData.m_manualOrder[i]);
                }
            }

            if (dynamicRoundData != null && dynamicRoundData.Phases != null)
            {
                if (phaseIndex >= 0 && phaseIndex < dynamicRoundData.Phases.Length)
                {
                    RecipeList phaseRecipes = dynamicRoundData.Phases[phaseIndex].Recipes;
                    baseCandidateCount = phaseRecipes != null && phaseRecipes.m_recipes != null
                        ? phaseRecipes.m_recipes.Length
                        : 0;
                    AddRecipeEntriesDistinct(phaseRecipes);
                }
            }
            else
            {
                RoundData standardRoundData = roundData as RoundData;
                if (standardRoundData != null)
                {
                    baseCandidateCount = standardRoundData.m_recipes != null && standardRoundData.m_recipes.m_recipes != null
                        ? standardRoundData.m_recipes.m_recipes.Length
                        : 0;
                    AddRecipeEntriesDistinct(standardRoundData.m_recipes);
                }
            }

            NoMenuExtensionEntriesBuffer.Clear();
            ServedDishTracker.AppendRecipeExtensionEntries(
                NoMenuExtensionEntriesBuffer,
                levelConfig,
                dynamicRoundData == null,
                Math.Max(0, phaseIndex));
            int cumulativeCandidateCount = GetRuntimeCandidateCount(orderController);
            if (RecipeExtensionPhasePolicy.HasCompatibleRuntimeShape(
                baseCandidateCount,
                NoMenuExtensionEntriesBuffer.Count,
                cumulativeCandidateCount))
            {
                for (int i = 0; i < NoMenuExtensionEntriesBuffer.Count; i++)
                {
                    AddRecipeEntryDistinct(NoMenuExtensionEntriesBuffer[i]);
                }
            }

            noMenuRecipesInitialized = true;
            noMenuRecipePhaseIndex = phaseIndex;
            return NoMenuRecipeEntriesBuffer;
        }

        private static int GetRuntimeCandidateCount(ServerOrderControllerBase orderController)
        {
            if (orderController == null
                || RoundInstanceDataField == null
                || RoundInstanceCumulativeFrequenciesField == null)
            {
                return -1;
            }

            try
            {
                object instanceData = RoundInstanceDataField.GetValue(orderController);
                int[] cumulativeFrequencies = instanceData != null
                    ? RoundInstanceCumulativeFrequenciesField.GetValue(instanceData) as int[]
                    : null;
                return cumulativeFrequencies != null ? cumulativeFrequencies.Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static int GetCurrentDynamicPhaseIndex(ServerOrderControllerBase orderController, DynamicRoundData dynamicRoundData)
        {
            if (dynamicRoundData == null || dynamicRoundData.Phases == null || dynamicRoundData.Phases.Length == 0)
            {
                return -1;
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
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The current dynamic-round phase field could not be read.", ex);
                }
            }

            return -1;
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

        private static ServerOrderData AddSyntheticOrder(
            TeamID teamId,
            ServerTeamMonitor monitor,
            RecipeList.Entry entry,
            out bool mutationStarted)
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
                        SyntheticOrderIds.Add(new TeamScopedOrderKey((int)teamId, reservedOrderId));
                    }
                }

                mutationStarted = true;
                ServerOrderData syntheticOrder = AddSpecificOrderMethod.Invoke(monitor.OrdersController, new object[] { entry }) as ServerOrderData;
                if (syntheticOrder == null)
                {
                    if (reservedOrderId != 0u)
                    {
                        CompensatedSyntheticOrderIds.Add(new TeamScopedOrderKey((int)teamId, reservedOrderId));
                        TryRemoveSyntheticOrder(teamId, monitor, new OrderID(reservedOrderId), false);
                    }

                    return null;
                }

                if (reservedOrderId != 0u && syntheticOrder.ID.m_id != reservedOrderId)
                {
                    SyntheticOrderIds.Remove(new TeamScopedOrderKey((int)teamId, reservedOrderId));
                }

                SyntheticOrderIds.Add(new TeamScopedOrderKey((int)teamId, syntheticOrder.ID.m_id));
                return syntheticOrder;
            }
            catch (Exception ex)
            {
                if (reservedOrderId != 0u)
                {
                    CompensatedSyntheticOrderIds.Add(new TeamScopedOrderKey((int)teamId, reservedOrderId));
                    TryRemoveSyntheticOrder(teamId, monitor, new OrderID(reservedOrderId), false);
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

        private static void TryRemoveSyntheticOrder(
            TeamID teamId,
            ServerTeamMonitor monitor,
            OrderID orderId,
            bool forgetKey)
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

            if (forgetKey)
            {
                ForgetSyntheticOrder(teamId, orderId);
            }
        }

        private static bool TryGetActiveOrderCount(ServerOrderControllerBase orderController, out int count)
        {
            count = 0;
            if (orderController == null || ActiveOrdersField == null)
            {
                return false;
            }

            try
            {
                IList activeOrders = ActiveOrdersField.GetValue(orderController) as IList;
                if (activeOrders == null)
                {
                    return false;
                }

                count = activeOrders.Count;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsActiveOrder(ServerTeamMonitor monitor, OrderID orderId)
        {
            if (monitor == null || monitor.OrdersController == null || ActiveOrdersField == null)
            {
                return true;
            }

            try
            {
                IList activeOrders = ActiveOrdersField.GetValue(monitor.OrdersController) as IList;
                if (activeOrders == null)
                {
                    return true;
                }

                for (int i = 0; i < activeOrders.Count; i++)
                {
                    ServerOrderData activeOrder = activeOrders[i] as ServerOrderData;
                    if (activeOrder != null && activeOrder.ID == orderId)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void TryRemoveClientGhostOrder(
            ClientKitchenFlowControllerBase clientFlow,
            TeamID teamId,
            OrderID orderId)
        {
            if (clientFlow == null || orderId.m_id == 0u)
            {
                return;
            }

            try
            {
                ClientTeamMonitor clientMonitor = clientFlow.GetMonitorForTeam(teamId);
                if (clientMonitor != null && clientMonitor.OrdersController != null)
                {
                    clientMonitor.OrdersController.OnFoodDelivered(true, orderId);
                }
            }
            catch
            {
            }
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

        private static bool TryGetConnectionStatus(out bool isInOnlineSession)
        {
            isInOnlineSession = false;
            if (ConnectionIsInSessionMethod == null)
            {
                return false;
            }

            try
            {
                object sessionValue = ConnectionIsInSessionMethod.Invoke(null, null);
                if (!(sessionValue is bool))
                {
                    return false;
                }

                isInOnlineSession = (bool)sessionValue;
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

        private static FieldInfo ResolveRoundInstanceCumulativeFrequenciesField()
        {
            Type roundInstanceType = typeof(RoundData).GetNestedType(
                "RoundInstanceData",
                BindingFlags.Public | BindingFlags.NonPublic);
            return roundInstanceType != null
                ? roundInstanceType.GetField("CumulativeFrequencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
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
                case NoMenuIneligibility.OnlineSession:
                    return chinese ? "联机关卡（包括私密房间）不支持无菜单，保留正常菜单。" : "Online sessions, including private sessions, do not support No Menu; the normal menu remains enabled.";
                case NoMenuIneligibility.MissingRuntimeContract:
                    return chinese ? "当前游戏版本缺少无菜单所需接口，已安全停用。" : "Required runtime hooks are unavailable; No Menu was safely disabled.";
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
            CompensatedSyntheticOrderIds.Clear();
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
