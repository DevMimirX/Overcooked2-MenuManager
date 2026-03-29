using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameModes;
using HarmonyLib;
using OrderController;
using UnityEngine;

namespace HostUtilities
{
    internal static class NoMenuMode
    {
        private static readonly ConfigDefinition LegacyToggleKeyDefinition = new ConfigDefinition("00-菜单管理", "切换无菜单热键");
        private static readonly MethodInfo TriggerAudioMessageMethod = AccessTools.Method("ServerMessenger:TriggerAudioMessage");
        private static readonly FieldInfo CampaignFlowGameModeField = AccessTools.Field(typeof(ServerCampaignFlowController), "m_gameMode");
        private static readonly FieldInfo CampaignModeContextField = AccessTools.Field(typeof(ServerCampaignMode), "m_context");
        private static readonly FieldInfo KitchenPlateReturnControllerField = AccessTools.Field(typeof(ServerKitchenFlowControllerBase), "m_plateReturnController");
        private static readonly FieldInfo KitchenFlowControllerField = AccessTools.Field(typeof(ServerKitchenFlowControllerBase), "m_kitchenFlowController");
        private static readonly FieldInfo KitchenFlowMessageField = AccessTools.Field(typeof(ServerKitchenFlowControllerBase), "m_data");
        private static readonly FieldInfo AutoProgressField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_autoProgress");
        private static readonly MethodInfo MatchesMethod = AccessTools.Method(typeof(ServerOrderControllerBase), "Matches");

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
            if (_MODEntry.Instance.Config.Remove(LegacyToggleKeyDefinition))
            {
                _MODEntry.Instance.Config.Save();
            }
            enabled = _MODEntry.Instance.Config.Bind<bool>("00-菜单管理", "无菜单", false, "启用内置无菜单模式，不依赖外部 OC2NoMenu.dll。");
            ModuleUtility.RegisterHarmony(typeof(NoMenuMode));
        }

        public static void ToggleEnabled()
        {
            if (enabled != null)
            {
                enabled.Value = !enabled.Value;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCampaignFlowController), "StartSynchronising")]
        private static void ServerCampaignFlowController_StartSynchronising_Postfix(ServerCampaignFlowController __instance)
        {
            if (!IsEnabled || __instance is ServerBossFlowController)
            {
                return;
            }

            IServerMode gameMode = GetCampaignFlowGameMode(__instance);
            if (gameMode is ServerSurvivalMode)
            {
                return;
            }

            ServerCampaignMode campaignMode = gameMode as ServerCampaignMode;
            if (campaignMode != null)
            {
                LevelConfigBase levelConfig = GetCampaignModeLevelConfig(campaignMode);
                KitchenLevelConfigBase kitchenLevelConfig = levelConfig as KitchenLevelConfigBase;
                if (kitchenLevelConfig != null && kitchenLevelConfig.m_recipesBeforeTimerStarts > 0)
                {
                    return;
                }
            }

            ServerTeamMonitor monitor = __instance.GetMonitorForTeam(TeamID.One);
            if (monitor != null && monitor.OrdersController != null)
            {
                monitor.OrdersController.SetAutoProgress(false);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ServerCompetitiveFlowController), "StartSynchronising")]
        private static void ServerCompetitiveFlowController_StartSynchronising_Postfix(ServerCompetitiveFlowController __instance)
        {
            if (!IsEnabled)
            {
                return;
            }

            ServerTeamMonitor teamOne = __instance.GetMonitorForTeam(TeamID.One);
            ServerTeamMonitor teamTwo = __instance.GetMonitorForTeam(TeamID.Two);
            if (teamOne != null && teamOne.OrdersController != null)
            {
                teamOne.OrdersController.SetAutoProgress(false);
            }
            if (teamTwo != null && teamTwo.OrdersController != null)
            {
                teamTwo.OrdersController.SetAutoProgress(false);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ServerKitchenFlowControllerBase), "OnFoodDelivered")]
        private static bool ServerKitchenFlowControllerBase_OnFoodDelivered_Prefix(ServerKitchenFlowControllerBase __instance, AssembledDefinitionNode _definition, PlatingStepData _plateType, ServerPlateStation _station)
        {
            if (!IsEnabled)
            {
                return true;
            }

            LevelConfigBase levelConfig = GameUtils.GetLevelConfig();
            KitchenLevelConfigBase kitchenLevelConfig = levelConfig as KitchenLevelConfigBase;
            if (kitchenLevelConfig == null || levelConfig is BossCampaignLevelConfig || levelConfig.name.StartsWith("Tutorial"))
            {
                return true;
            }

            ServerTeamMonitor monitor = __instance.GetMonitorForTeam(_station.GetTeamID());
            if (monitor == null || monitor.OrdersController == null || GetAutoProgress(monitor.OrdersController))
            {
                return true;
            }

            PlateReturnController plateReturnController = GetPlateReturnController(__instance);
            if (plateReturnController != null)
            {
                plateReturnController.FoodDelivered(_definition, _plateType, _station);
            }

            float timeRemainingPercent = 1f;
            int tip = 0;
            bool wasCombo = false;
            OrderID orderId = new OrderID(0u);
            RecipeList.Entry deliveredEntry = null;
            if (monitor.OrdersController.FindBestOrderForRecipe(_definition, _plateType, out orderId, out timeRemainingPercent))
            {
                deliveredEntry = monitor.OrdersController.GetRecipe(orderId);
            }

            if (deliveredEntry == null)
            {
                List<RecipeList.Entry> recipes = GetAvailableRecipes(kitchenLevelConfig);
                deliveredEntry = recipes.Find(delegate(RecipeList.Entry entry)
                {
                    return entry != null
                        && entry.m_order != null
                        && MatchesOrder(monitor.OrdersController, entry.m_order, _definition, _plateType);
                });
                orderId = new OrderID(0u);
                timeRemainingPercent = 1f;
            }

            KitchenFlowControllerBase kitchenFlowController = GetKitchenFlowController(__instance);
            if (kitchenFlowController != null)
            {
                tip = kitchenFlowController.CalculateTip(timeRemainingPercent);
            }

            if (deliveredEntry != null)
            {
                wasCombo = true;
                int baseScore = kitchenFlowController != null ? kitchenFlowController.CalculateBaseScore(deliveredEntry) : 0;
                tip *= Mathf.Max(monitor.Score.TotalMultiplier, 1);
                monitor.Score.TotalBaseScore += baseScore;
                monitor.Score.TotalTipsScore += tip;
                monitor.Score.TotalSuccessfulDeliveries++;
                monitor.Score.TotalCombo++;
                if (monitor.Score.TotalMultiplier < 4)
                {
                    monitor.Score.TotalMultiplier++;
                }

                if (TriggerAudioMessageMethod != null)
                {
                    TriggerAudioMessageMethod.Invoke(null, new object[]
                    {
                        GameOneShotAudioTag.SuccessfulDelivery,
                        __instance.gameObject.layer
                    });
                }
            }
            else
            {
                monitor.Score.TotalCombo = 0;
                monitor.Score.TotalMultiplier = 0;
                monitor.Score.ComboMaintained = false;
            }

            KitchenFlowMessage kitchenFlowMessage = GetKitchenFlowMessage(__instance);
            if (kitchenFlowMessage != null)
            {
                kitchenFlowMessage.m_msgType = KitchenFlowMessage.MsgType.Delivery;
                kitchenFlowMessage.m_teamID = _station.GetTeamID();
                kitchenFlowMessage.m_success = false;
                kitchenFlowMessage.m_plateStation = _station.gameObject;
                kitchenFlowMessage.m_orderID = orderId;
                kitchenFlowMessage.m_wasCombo = wasCombo;
                kitchenFlowMessage.m_timePropRemainingPercentage = timeRemainingPercent;
                kitchenFlowMessage.m_tip = tip;
                kitchenFlowMessage.SetScoreData(monitor.Score);
                __instance.SendServerEvent(kitchenFlowMessage);
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FrontendCoopTabOptions), "OnOnlinePublicClicked")]
        [HarmonyPatch(typeof(FrontendVersusTabOptions), "OnOnlinePublicClicked")]
        private static bool Frontend_PublicOnline_Prefix()
        {
            if (enabled != null)
            {
                enabled.Value = false;
            }
            return true;
        }

        private static IServerMode GetCampaignFlowGameMode(ServerCampaignFlowController controller)
        {
            return CampaignFlowGameModeField != null ? CampaignFlowGameModeField.GetValue(controller) as IServerMode : null;
        }

        private static LevelConfigBase GetCampaignModeLevelConfig(ServerCampaignMode campaignMode)
        {
            if (CampaignModeContextField == null)
            {
                return null;
            }

            object context = CampaignModeContextField.GetValue(campaignMode);
            if (context == null)
            {
                return null;
            }

            FieldInfo levelConfigField = AccessTools.Field(context.GetType(), "m_levelConfig");
            return levelConfigField != null ? levelConfigField.GetValue(context) as LevelConfigBase : null;
        }

        private static PlateReturnController GetPlateReturnController(ServerKitchenFlowControllerBase controller)
        {
            return KitchenPlateReturnControllerField != null
                ? KitchenPlateReturnControllerField.GetValue(controller) as PlateReturnController
                : null;
        }

        private static KitchenFlowControllerBase GetKitchenFlowController(ServerKitchenFlowControllerBase controller)
        {
            return KitchenFlowControllerField != null
                ? KitchenFlowControllerField.GetValue(controller) as KitchenFlowControllerBase
                : null;
        }

        private static KitchenFlowMessage GetKitchenFlowMessage(ServerKitchenFlowControllerBase controller)
        {
            return KitchenFlowMessageField != null
                ? KitchenFlowMessageField.GetValue(controller) as KitchenFlowMessage
                : null;
        }

        private static bool GetAutoProgress(ServerOrderControllerBase controller)
        {
            return AutoProgressField != null && (bool)AutoProgressField.GetValue(controller);
        }

        private static bool MatchesOrder(ServerOrderControllerBase controller, OrderDefinitionNode required, AssembledDefinitionNode provided, PlatingStepData plateType)
        {
            return MatchesMethod != null && (bool)MatchesMethod.Invoke(controller, new object[] { required, provided, plateType });
        }

        private static List<RecipeList.Entry> GetAvailableRecipes(KitchenLevelConfigBase levelConfig)
        {
            List<RecipeList.Entry> list = new List<RecipeList.Entry>();
            RoundData roundData = levelConfig.GetRoundData();
            DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
            if (dynamicRoundData != null && dynamicRoundData.Phases != null)
            {
                for (int i = 0; i < dynamicRoundData.Phases.Length; i++)
                {
                    RecipeList recipes = dynamicRoundData.Phases[i].Recipes;
                    if (recipes == null || recipes.m_recipes == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < recipes.m_recipes.Length; j++)
                    {
                        list.Add(recipes.m_recipes[j]);
                    }
                }
            }
            else if (roundData != null && roundData.m_recipes != null && roundData.m_recipes.m_recipes != null)
            {
                for (int i = 0; i < roundData.m_recipes.m_recipes.Length; i++)
                {
                    list.Add(roundData.m_recipes.m_recipes[i]);
                }
            }

            return list;
        }
    }
}
