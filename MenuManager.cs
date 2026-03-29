using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace HostUtilities
{
    public class MenuManager
    {
        public static Harmony HarmonyInstance { get; set; }
        public static void log(string mes)
        {
            _MODEntry.LogInfo(mes);
        }
        public static ConfigEntry<bool> isCarnivalMenuGood;
        public static ConfigEntry<bool> isCarnivalCakeGood;
        public static ConfigEntry<bool> isCarnivalMenuFixed;

        public static int[][] carnivalMenu;

        public static bool IsReady
        {
            get { return isCarnivalMenuGood != null && isCarnivalCakeGood != null && isCarnivalMenuFixed != null; }
        }

        public static bool IsCarnivalMenuGoodEnabled
        {
            get { return isCarnivalMenuGood != null && isCarnivalMenuGood.Value; }
        }

        public static bool IsCarnivalCakeGoodEnabled
        {
            get { return isCarnivalCakeGood != null && isCarnivalCakeGood.Value; }
        }

        public static bool IsCarnivalMenuFixedEnabled
        {
            get { return isCarnivalMenuFixed != null && isCarnivalMenuFixed.Value; }
        }

        public static void Awake()
        {
            isCarnivalMenuGood = _MODEntry.SettingsConfig.Bind<bool>("00-功能开关", "麻团好菜单", true, "第一道菜没有葱，前两道菜不是蛋糕");
            isCarnivalCakeGood = _MODEntry.SettingsConfig.Bind<bool>("00-功能开关", "麻团好蛋糕", true, "出蛋糕概率提高20%；47单前必出11蛋糕，50单前必出12蛋糕；55单前必出13蛋糕，56单前必出14蛋糕");
            isCarnivalMenuFixed = _MODEntry.SettingsConfig.Bind<bool>("00-功能开关", "麻团TAS菜单", false, "固定麻团菜单为TAS专用菜单");
            carnivalMenu = new int[1][];
            carnivalMenu[0] = new int[] {
                3,5,7,8,1,4,6,0,2,5,6,2,1,4,8,7,3,0,7,3,1,4,0,6,2,8,5,8,
                6,2,1,4,7,0,3,5,7,3,1,4,5,6,2,0,8,6,1,2,5,0,4,3,7,7,1,0
            };
            HarmonyInstance = ModuleUtility.RegisterHarmony(typeof(MenuManager));
        }

        public static void ToggleCarnivalMenuGood()
        {
            if (isCarnivalMenuGood != null)
            {
                isCarnivalMenuGood.Value = !isCarnivalMenuGood.Value;
            }
        }

        public static void ToggleCarnivalCakeGood()
        {
            if (isCarnivalCakeGood != null)
            {
                isCarnivalCakeGood.Value = !isCarnivalCakeGood.Value;
            }
        }

        public static void ToggleCarnivalMenuFixed()
        {
            if (isCarnivalMenuFixed != null)
            {
                isCarnivalMenuFixed.Value = !isCarnivalMenuFixed.Value;
            }
        }

        public class FixedMenuRoundData : RoundData
        {
            public static RecipeList.Entry[] GetNextRecipeFixed(RoundData roundData, RoundInstanceDataBase _data)
            {
                log("running fixed menu RoundData");
                RoundData.RoundInstanceData instance = _data as RoundData.RoundInstanceData;
                if (roundData is ScriptedRoundData scriptedRoundData)
                    if (instance.RecipeCount < scriptedRoundData.m_manualOrder.Length)
                    {
                        return null;
                    }
                int menuCount = instance.CumulativeFrequencies.Collapse((int f, int total) => total + f);
                if (carnivalMenu[0].Length <= menuCount)
                    return null;
                int menuIndex = carnivalMenu[0][menuCount];
                instance.RecipeCount++;
                instance.CumulativeFrequencies[menuIndex]++;
                return new RecipeList.Entry[] { roundData.m_recipes.m_recipes[menuIndex] };
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoundData), "GetNextRecipe")]
        public static bool RoundDataGetNextRecipePatch(RoundData __instance, ref RecipeList.Entry[] __result, RoundInstanceDataBase _data)
        {
            LevelConfigBase kitchenLevelConfigBase = GameUtils.GetLevelConfig();
            if (kitchenLevelConfigBase.name.StartsWith("Day_3_4") && isCarnivalMenuFixed.Value)
            {
                __result = FixedMenuRoundData.GetNextRecipeFixed(__instance, _data);
                return __result == null;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoundData), "GetWeight")]
        public static bool RoundDataGetWeightPatch(ref float __result, RoundData __instance, RoundData.RoundInstanceData _instance, int _recipeIndex)
        {
            LevelConfigBase kitchenLevelConfigBase = GameUtils.GetLevelConfig();
            if (kitchenLevelConfigBase.name.StartsWith("Day_3_4") && isCarnivalMenuGood.Value && !isCarnivalMenuFixed.Value)
            {
                int recipe_len = __instance.m_recipes.m_recipes.Length;
                int num = _instance.CumulativeFrequencies.Collapse((int f, int total) => total + f);
                float theo_prob = Mathf.Max((float)(num + 2) / (float)recipe_len - (float)_instance.CumulativeFrequencies[_recipeIndex], 0f);
                float berry_prob = Mathf.Max((float)(num + 2) / (float)recipe_len - (float)_instance.CumulativeFrequencies[0], 0f);
                float choco_prob = Mathf.Max((float)(num + 2) / (float)recipe_len - (float)_instance.CumulativeFrequencies[1], 0f);

                __result = theo_prob;
                if (num == 0 && (_recipeIndex <= 1 || (_recipeIndex >= 5 && _recipeIndex <= 7)))
                {
                    __result = 0f;
                }
                else if (num == 1 && _recipeIndex <= 1)
                {
                    __result = 0f;
                }

                if (isCarnivalCakeGood.Value)
                {
                    if (_recipeIndex <= 1)
                    {
                        __result *= 3f;
                    }

                    if (num == 46)
                    {
                        if (berry_prob > 0f && choco_prob > 0f)
                        {
                            if (_recipeIndex <= 1)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                    }
                    else if (num == 49)
                    {
                        if (berry_prob > 0f && choco_prob > 0f)
                        {
                            if (_recipeIndex <= 1)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                        else if (berry_prob > 0f && choco_prob <= 0f)
                        {
                            if (_recipeIndex == 0)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                        else if (berry_prob <= 0f && choco_prob > 0f)
                        {
                            if (_recipeIndex == 1)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                        else
                        {
                            __result = theo_prob;
                        }
                    }
                    else if (num == 54)
                    {
                        if (berry_prob > 0f)
                        {
                            if (_recipeIndex == 0)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                    }
                    else if (num == 55)
                    {
                        if (choco_prob > 0f)
                        {
                            if (_recipeIndex == 1)
                                __result = 1f;
                            else
                                __result = 0f;
                        }
                    }

                }

                //log($"Round {num}|{_recipeIndex}: {theo_prob}, {__result},  {_instance.CumulativeFrequencies[_recipeIndex]}, {recipe_len}");
                return false;
            }
            return true;
        }
    }
}
