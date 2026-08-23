// Owns Carnival menu selection rules and their optional Recipe Extension
// integration. Harmony prefixes fail open so an integration fault never blocks
// the base game's recipe selection or mutates non-Carnival rounds.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using OC2MenuManager.Infrastructure;

namespace OC2MenuManager
{
    /// <summary>
    /// Coordinates user-configured Carnival selection rules at the RoundData boundary.
    /// Fixed sequences have highest precedence; weighted integration intervenes only
    /// when the reflected runtime candidate shape exactly matches Recipe Extension.
    /// </summary>
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
        private static readonly FieldInfo RoundInstanceCumulativeFrequenciesField = ResolveRoundInstanceCumulativeFrequenciesField();
        private static readonly List<RecipeList.Entry> CarnivalExtensionEntriesBuffer = new List<RecipeList.Entry>();
        private static RecipeList.Entry[] CarnivalCandidateEntriesBuffer = new RecipeList.Entry[0];
        private static float[] CarnivalCandidateWeightsBuffer = new float[0];
        private static bool carnivalPatchFailureLogged;

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
            isCarnivalMenuGood.SettingChanged += delegate { ServedDishTracker.NotifyProbabilityRuleChanged(); };
            isCarnivalCakeGood.SettingChanged += delegate { ServedDishTracker.NotifyProbabilityRuleChanged(); };
            isCarnivalMenuFixed.SettingChanged += delegate { ServedDishTracker.NotifyProbabilityRuleChanged(); };
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

        /// <summary>
        /// Implements the deterministic and combined-pool Carnival selectors while
        /// preserving the base RoundData instance counters used by the game.
        /// </summary>
        public class FixedMenuRoundData : RoundData
        {
            public static RecipeList.Entry[] GetNextRecipeFixed(RoundData roundData, RoundInstanceDataBase _data)
            {
                RoundData.RoundInstanceData instance = _data as RoundData.RoundInstanceData;
                if (roundData == null
                    || instance == null
                    || instance.CumulativeFrequencies == null
                    || roundData.m_recipes == null
                    || roundData.m_recipes.m_recipes == null)
                {
                    return null;
                }

                if (roundData is ScriptedRoundData scriptedRoundData)
                {
                    if (scriptedRoundData.m_manualOrder != null && instance.RecipeCount < scriptedRoundData.m_manualOrder.Length)
                    {
                        return null;
                    }
                }

                int menuCount = instance.CumulativeFrequencies.Collapse((int f, int total) => total + f);
                if (carnivalMenu == null
                    || carnivalMenu.Length == 0
                    || carnivalMenu[0] == null
                    || carnivalMenu[0].Length <= menuCount)
                {
                    return null;
                }

                int menuIndex = carnivalMenu[0][menuCount];
                if (menuIndex < 0 || menuIndex >= roundData.m_recipes.m_recipes.Length)
                {
                    return null;
                }

                instance.RecipeCount++;
                instance.CumulativeFrequencies[menuIndex]++;
                return new RecipeList.Entry[] { roundData.m_recipes.m_recipes[menuIndex] };
            }

            internal static bool TryGetNextRecipeWithRecipeExtension(
                RoundData roundData,
                RoundInstanceDataBase data,
                bool cakeRulesEnabled,
                out RecipeList.Entry[] result)
            {
                result = null;
                RoundData.RoundInstanceData instance = data as RoundData.RoundInstanceData;
                RecipeList.Entry[] baseEntries = roundData != null && roundData.m_recipes != null
                    ? roundData.m_recipes.m_recipes
                    : null;
                if (instance == null || instance.CumulativeFrequencies == null || baseEntries == null)
                {
                    return false;
                }

                ScriptedRoundData scriptedRoundData = roundData as ScriptedRoundData;
                if (scriptedRoundData != null
                    && scriptedRoundData.m_manualOrder != null
                    && instance.RecipeCount < scriptedRoundData.m_manualOrder.Length)
                {
                    return false;
                }

                CarnivalExtensionEntriesBuffer.Clear();
                ManyRecipesSnapshotState extensionState = OptionalRecipeAdapters.TryGetManyRecipeEntries(
                    CarnivalExtensionEntriesBuffer);
                if (!ManyRecipesSnapshotPolicy.HasExactRuntimeShape(
                        extensionState,
                        baseEntries.Length,
                        CarnivalExtensionEntriesBuffer.Count,
                        instance.CumulativeFrequencies.Length))
                {
                    return false;
                }

                int candidateCount = instance.CumulativeFrequencies.Length;
                if (CarnivalCandidateEntriesBuffer.Length != candidateCount)
                {
                    CarnivalCandidateEntriesBuffer = new RecipeList.Entry[candidateCount];
                    CarnivalCandidateWeightsBuffer = new float[candidateCount];
                }

                for (int i = 0; i < baseEntries.Length; i++)
                {
                    RecipeList.Entry entry = baseEntries[i];
                    if (entry == null || entry.m_order == null)
                    {
                        return false;
                    }

                    CarnivalCandidateEntriesBuffer[i] = entry;
                }

                for (int i = 0; i < CarnivalExtensionEntriesBuffer.Count; i++)
                {
                    RecipeList.Entry entry = CarnivalExtensionEntriesBuffer[i];
                    if (entry == null || entry.m_order == null)
                    {
                        return false;
                    }

                    CarnivalCandidateEntriesBuffer[baseEntries.Length + i] = entry;
                }

                if (!CarnivalRecipeSelectionPolicy.TryCalculateWeights(
                    instance.CumulativeFrequencies,
                    baseEntries.Length,
                    cakeRulesEnabled,
                    CarnivalCandidateWeightsBuffer))
                {
                    return false;
                }

                float totalWeight = 0f;
                for (int i = 0; i < candidateCount; i++)
                {
                    totalWeight += CarnivalCandidateWeightsBuffer[i];
                }

                if (totalWeight <= 0f || float.IsNaN(totalWeight) || float.IsInfinity(totalWeight))
                {
                    return false;
                }

                KeyValuePair<int, RecipeList.Entry> selected = CarnivalCandidateEntriesBuffer.GetWeightedRandomElement(
                    (int index, RecipeList.Entry entry) => CarnivalCandidateWeightsBuffer[index]);
                if (selected.Key < 0 || selected.Key >= candidateCount || selected.Value == null)
                {
                    return false;
                }

                instance.RecipeCount++;
                instance.CumulativeFrequencies[selected.Key]++;
                result = new RecipeList.Entry[] { selected.Value };
                return true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoundData), "GetNextRecipe")]
        [HarmonyBefore(OptionalRecipeAdapters.ManyRecipesPluginGuid)]
        [HarmonyPriority(Priority.First)]
        public static bool RoundDataGetNextRecipePatch(RoundData __instance, ref RecipeList.Entry[] __result, RoundInstanceDataBase _data)
        {
            try
            {
                if (!IsCarnivalLevel())
                {
                    return true;
                }

                if (IsCarnivalMenuFixedEnabled)
                {
                    __result = FixedMenuRoundData.GetNextRecipeFixed(__instance, _data);
                    return __result == null;
                }

                if (IsCarnivalMenuGoodEnabled
                    && FixedMenuRoundData.TryGetNextRecipeWithRecipeExtension(
                        __instance,
                        _data,
                        IsCarnivalCakeGoodEnabled,
                        out __result))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogCarnivalPatchFailure("selecting the next recipe", ex);
                __result = null;
                return true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoundData), "GetWeight")]
        public static bool RoundDataGetWeightPatch(ref float __result, RoundData __instance, object _instance, int _recipeIndex)
        {
            try
            {
                if (IsCarnivalLevel() && IsCarnivalMenuGoodEnabled && !IsCarnivalMenuFixedEnabled)
                {
                    int[] cumulativeFrequencies = GetRoundInstanceCumulativeFrequencies(_instance);
                    RecipeList.Entry[] baseEntries = __instance != null && __instance.m_recipes != null
                        ? __instance.m_recipes.m_recipes
                        : null;
                    float weight;
                    if (baseEntries == null
                        || !CarnivalRecipeSelectionPolicy.TryCalculateWeight(
                            cumulativeFrequencies,
                            baseEntries.Length,
                            _recipeIndex,
                            IsCarnivalCakeGoodEnabled,
                            out weight))
                    {
                        return true;
                    }

                    __result = weight;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogCarnivalPatchFailure("calculating a recipe weight", ex);
                return true;
            }
        }

        private static void LogCarnivalPatchFailure(string operation, Exception exception)
        {
            if (carnivalPatchFailureLogged || exception == null)
            {
                return;
            }

            carnivalPatchFailureLogged = true;
            _MODEntry.LogWarning("[MenuManager] Carnival integration failed while " + operation
                + "; base-game selection was allowed to continue: "
                + exception.GetType().Name + ": " + exception.Message);
        }

        private static bool IsCarnivalLevel()
        {
            LevelConfigBase levelConfig = GameUtils.GetLevelConfig();
            return levelConfig != null
                && !string.IsNullOrEmpty(levelConfig.name)
                && levelConfig.name.StartsWith("Day_3_4", StringComparison.Ordinal);
        }

        private static FieldInfo ResolveRoundInstanceCumulativeFrequenciesField()
        {
            Type nestedType = typeof(RoundData).GetNestedType("RoundInstanceData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return nestedType != null ? nestedType.GetField("CumulativeFrequencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        }

        private static int[] GetRoundInstanceCumulativeFrequencies(object roundInstance)
        {
            if (roundInstance == null || RoundInstanceCumulativeFrequenciesField == null)
            {
                return null;
            }

            return RoundInstanceCumulativeFrequenciesField.GetValue(roundInstance) as int[];
        }
    }
}
