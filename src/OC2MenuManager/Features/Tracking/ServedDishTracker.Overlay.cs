// Builds the history overlay and next-order probabilities from authoritative
// round state. The latest dynamic phase is retained independently of team runs
// so controllers created after a map switch inherit the correct phase. Cached
// probability and sorted-row results rebuild only after explicit invalidation.
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
        private static void ResetProbabilityState(int phaseIndex)
        {
            int nextPhase = DynamicPhasePolicy.NormalizePhaseIndex(phaseIndex);
            currentDynamicPhaseIndex = nextPhase;

            foreach (RunInfo run in RunsByTeam.Values)
            {
                if (run == null)
                {
                    continue;
                }

                if (!DynamicPhasePolicy.ShouldReset(run.CurrentPhaseIndex, nextPhase))
                {
                    continue;
                }

                run.CurrentPhaseIndex = nextPhase;
                run.TotalAdded = 0;
                run.AddedCounts.Clear();
            }
            InvalidateProbabilityMap();
            InvalidatePreparedCandidates(true);
            InvalidateOverlay();
        }

        private static string BuildOverlayText()
        {
            overlayHeaderText = string.Empty;
            overlayFooterText = string.Empty;
            for (int i = 0; i < OverlayRenderRowsBuffer.Count; i++)
            {
                OverlayRenderRowsBuffer[i].Reset();
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene.OrderedRecipes.Count == 0)
            {
                OverlayRenderRowsBuffer.Clear();
                return string.Empty;
            }

            bool chinese = UseChinese();
            List<TeamID> activeTeams = GetActiveTeamIds();
            if (activeTeams.Count == 0)
            {
                activeTeams.Add(TeamID.One);
            }

            bool showPrepared = IsPreparedTrackingEnabled();
            StringBuilder builder = OverlayTextBuilder;
            builder.Length = 0;
            builder.Append(TruncateWithEllipsis(GetOverlaySceneLabel(scene), GetMaxOverlaySceneDisplayLength())).Append(" | ");
            builder.Append(chinese ? "已追踪 " : "Tracking ");
            int trackedRecipeCount = 0;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                if (scene.OrderedRecipes[i] != null && IsTracked(scene, scene.OrderedRecipes[i].Id))
                {
                    trackedRecipeCount++;
                }
            }

            if (trackedRecipeCount == 0)
            {
                OverlayRenderRowsBuffer.Clear();
                return chinese
                    ? "历史菜单追踪\n当前关卡没有勾选任何追踪菜品。\n请打开 OC2MenuManager 独立窗口勾选需要追踪的菜品。"
                    : "Menu History Tracker\nNo dishes are tracked for this scene.\nOpen the standalone OC2MenuManager window to choose tracked dishes.";
            }

            builder.Append(trackedRecipeCount).Append('/').Append(scene.OrderedRecipes.Count).Append('\n');
            builder.Append(chinese
                ? "按下单出现概率排序，复杂的菜优先"
                : "Sorted by next-order probability; harder dishes first").Append('\n');
            builder.Append(showPrepared
                ? (chinese ? "[   ]在单  [ - ]未备  [ x ]已上  [ v ]已备" : "[   ] On menu  [ - ] Unprepared  [ x ] Served  [ v ] Prepared")
                : (chinese ? "[   ]在单  [ - ]未备  [ x ]已上" : "[   ] On menu  [ - ] Unprepared  [ x ] Served")).Append('\n');
            builder.Append(WrapRichValue(chinese ? "蓝=上单" : "Blue=Served", overlayServedValueColor != null ? overlayServedValueColor.Value : new Color(0.58f, 0.84f, 1f, 1f)));
            builder.Append(" | ");
            builder.Append(WrapRichValue(chinese ? "金=概率" : "Gold=Prob", overlayProbabilityValueColor != null ? overlayProbabilityValueColor.Value : new Color(1f, 0.84f, 0.40f, 1f)));
            builder.Append('\n');
            overlayHeaderText = builder.ToString().TrimEnd();
            builder.Length = 0;

            int renderIndex = 0;
            int maxRowsPerTeam = Math.Max(1, overlayMaxDisplayDishes != null ? overlayMaxDisplayDishes.Value : 12);
            bool separateTeams = activeTeams.Count > 1;
            for (int teamIndex = 0; teamIndex < activeTeams.Count; teamIndex++)
            {
                TeamID teamId = activeTeams[teamIndex];
                RunInfo run = EnsureRun(scene, teamId);
                List<OverlayRow> rows = BuildAndSortOverlayRows(scene, run, showPrepared);
                if (separateTeams)
                {
                    OverlayRenderRow teamHeader = GetOrCreateOverlayRenderRow(renderIndex++);
                    teamHeader.Text = chinese
                        ? (teamId == TeamID.Two ? "—— 队伍 2 ——" : "—— 队伍 1 ——")
                        : (teamId == TeamID.Two ? "— Team 2 —" : "— Team 1 —");
                    teamHeader.TextTint = new Color(0.82f, 0.90f, 1f, 1f);
                }

                int maxRows = Math.Min(rows.Count, maxRowsPerTeam);
                for (int i = 0; i < maxRows; i++)
                {
                    OverlayRow row = rows[i];
                    bool isDeferredTodo = row.ProbabilityAvailable
                        && row.Probability <= 0d
                        && row.OnMenu <= 0
                        && (!showPrepared || row.Prepared <= 0);
                    builder.Append(GetOverlayTodoPrefix(row, showPrepared)).Append(' ');
                    builder.Append(GetOverlayDishNameText(scene, row, showPrepared));
                    builder.Append("  |  ");
                    builder.Append(WrapRichValue(row.Served.ToString(), GetOverlayServedValueColor(row, showPrepared)));
                    builder.Append("  |  ");
                    string probabilityText = row.ProbabilityAvailable
                        ? (row.Probability * 100d).ToString("0.0") + "%"
                        : "—";
                    builder.Append(WrapRichValue(probabilityText, GetOverlayProbabilityValueColor(row, showPrepared)));
                    OverlayRenderRow renderRow = GetOrCreateOverlayRenderRow(renderIndex++);
                    renderRow.Text = builder.ToString();
                    renderRow.BackgroundColor = GetOverlayRowBackgroundColor(row, showPrepared);
                    renderRow.HasBackground = renderRow.BackgroundColor.a > 0f;
                    renderRow.TextTint = GetOverlayRowTextTint(row, showPrepared);
                    renderRow.HasStrikeThrough = isDeferredTodo;
                    builder.Length = 0;
                }

                if (rows.Count > maxRows)
                {
                    OverlayRenderRow moreRow = GetOrCreateOverlayRenderRow(renderIndex++);
                    moreRow.Text = (chinese ? "+ 还有 " : "+ ")
                        + (rows.Count - maxRows)
                        + (chinese ? " 个追踪菜品未显示" : " more tracked dishes");
                    moreRow.TextTint = new Color(1f, 1f, 1f, 0.72f);
                }
            }

            if (OverlayRenderRowsBuffer.Count > renderIndex)
            {
                for (int i = renderIndex; i < OverlayRenderRowsBuffer.Count; i++)
                {
                    OverlayRenderRowsBuffer[i].Reset();
                }

                OverlayRenderRowsBuffer.RemoveRange(renderIndex, OverlayRenderRowsBuffer.Count - renderIndex);
            }

            builder.Length = 0;
            if (!string.IsNullOrEmpty(overlayHeaderText))
            {
                builder.Append(overlayHeaderText);
            }
            for (int i = 0; i < OverlayRenderRowsBuffer.Count; i++)
            {
                if (!string.IsNullOrEmpty(OverlayRenderRowsBuffer[i].Text))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(OverlayRenderRowsBuffer[i].Text);
                }
            }
            return builder.ToString().TrimEnd();
        }

        private static List<OverlayRow> BuildAndSortOverlayRows(SceneInfo scene, RunInfo run, bool showPrepared)
        {
            if (scene == null || run == null)
            {
                EmptyOverlayRowsBuffer.Clear();
                return EmptyOverlayRowsBuffer;
            }

            List<OverlayRow> rows = run.OverlayRows;
            if (run.OverlayRowsVersion == overlayRowsVersion
                && run.OverlayRowsShowPrepared == showPrepared)
            {
                return rows;
            }

            rows.Clear();
            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene, run.TeamId);
            Dictionary<int, int> menuOrderByRecipeId = BuildMenuOrderMap(scene, run.TeamId);
            Dictionary<int, double> probabilityByRecipeId = GetProbabilityMap(scene, run);
            int rowCount = 0;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null || !IsTracked(scene, recipe.Id))
                {
                    continue;
                }

                OverlayRow row = GetOrCreateOverlayRow(rows, rowCount);
                row.Recipe = recipe;
                row.Probability = GetProbability(probabilityByRecipeId, recipe.Id);
                row.ProbabilityAvailable = run.ProbabilityAvailable;
                row.Served = GetCount(run.ServedCounts, recipe.Id);
                row.Prepared = showPrepared ? GetCount(PreparedCountsByRecipe, recipe.Id) : 0;
                row.OnMenu = GetCount(currentMenuCounts, recipe.Id);
                row.EarliestMenuOrder = GetMenuOrder(menuOrderByRecipeId, recipe.Id);
                rowCount++;
            }

            if (rows.Count > rowCount)
            {
                for (int i = rowCount; i < rows.Count; i++)
                {
                    rows[i].Reset();
                }

                rows.RemoveRange(rowCount, rows.Count - rowCount);
            }

            rows.Sort(showPrepared ? OverlayRowsWithPreparedComparison : OverlayRowsWithoutPreparedComparison);

            run.OverlayRowsShowPrepared = showPrepared;
            run.OverlayRowsVersion = overlayRowsVersion;
            return rows;
        }

        private static int CompareOverlayRowsWithPrepared(OverlayRow a, OverlayRow b)
        {
            return CompareOverlayRows(a, b, true);
        }

        private static int CompareOverlayRowsWithoutPrepared(OverlayRow a, OverlayRow b)
        {
            return CompareOverlayRows(a, b, false);
        }

        private static OverlayRow GetOrCreateOverlayRow(List<OverlayRow> rows, int index)
        {
            while (rows.Count <= index)
            {
                rows.Add(new OverlayRow());
            }

            return rows[index];
        }

        private static int CompareOverlayRows(OverlayRow a, OverlayRow b, bool showPrepared)
        {
            int aBucket = GetOverlayRowBucket(a, showPrepared);
            int bBucket = GetOverlayRowBucket(b, showPrepared);
            int bucketCompare = aBucket.CompareTo(bBucket);
            if (bucketCompare != 0)
            {
                return bucketCompare;
            }

            if (aBucket == 0 && bBucket == 0)
            {
                int categoryCompare = a.Recipe.CategoryTier.CompareTo(b.Recipe.CategoryTier);
                if (categoryCompare != 0)
                {
                    return categoryCompare;
                }

                int menuOrderCompare = a.EarliestMenuOrder.CompareTo(b.EarliestMenuOrder);
                if (menuOrderCompare != 0)
                {
                    return menuOrderCompare;
                }
            }

            if (aBucket == 1 && bBucket == 1)
            {
                int categoryCompare = a.Recipe.CategoryTier.CompareTo(b.Recipe.CategoryTier);
                if (categoryCompare != 0)
                {
                    return categoryCompare;
                }
            }

            int probabilityCompare = b.Probability.CompareTo(a.Probability);
            if (probabilityCompare != 0)
            {
                return probabilityCompare;
            }

            int servedCompare = a.Served.CompareTo(b.Served);
            if (servedCompare != 0)
            {
                return servedCompare;
            }

            int nameCompare = string.Compare(
                GetRecipeDisplayName(a.Recipe),
                GetRecipeDisplayName(b.Recipe),
                StringComparison.OrdinalIgnoreCase);
            return nameCompare != 0 ? nameCompare : a.Recipe.Id.CompareTo(b.Recipe.Id);
        }

        private static int GetOverlayRowBucket(OverlayRow row, bool showPrepared)
        {
            if (row == null)
            {
                return int.MaxValue;
            }

            bool onMenuAndUnprepared = row.OnMenu > 0 && (!showPrepared || row.Prepared <= 0);
            if (onMenuAndUnprepared)
            {
                return 0;
            }

            bool prepared = showPrepared && row.Prepared > 0;
            if (!prepared && row.ProbabilityAvailable && row.Probability > 0d)
            {
                return 1;
            }

            if (!prepared)
            {
                return 2;
            }

            return 3;
        }

        private static bool IsOverlayReferenceCandidate(OverlayRow row, bool showPrepared)
        {
            return GetOverlayRowBucket(row, showPrepared) == 1;
        }

        private static Dictionary<int, double> GetProbabilityMap(SceneInfo scene, RunInfo run)
        {
            if (scene == null || run == null)
            {
                ProbabilityByRecipeBuffer.Clear();
                return ProbabilityByRecipeBuffer;
            }

            if (!run.ProbabilityDirty)
            {
                return run.ProbabilityByRecipeId;
            }

            BuildProbabilityMap(scene, run, run.ProbabilityByRecipeId);
            run.ProbabilityDirty = false;
            return run.ProbabilityByRecipeId;
        }

        private static Dictionary<int, double> BuildProbabilityMap(
            SceneInfo scene,
            RunInfo run,
            Dictionary<int, double> probabilityByRecipeId)
        {
            probabilityByRecipeId.Clear();
            run.ProbabilityAvailable = false;
            if (scene == null || run == null || scene.OrderedRecipes.Count == 0)
            {
                return probabilityByRecipeId;
            }

            try
            {
                if (TryBuildAuthoritativeProbabilityMap(scene, run, probabilityByRecipeId)
                    || TryBuildReconstructedProbabilityMap(scene, run, probabilityByRecipeId))
                {
                    run.ProbabilityAvailable = true;
                    return probabilityByRecipeId;
                }
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("rebuilding recipe probabilities", ex);
            }

            probabilityByRecipeId.Clear();
            return probabilityByRecipeId;
        }

        private static bool TryBuildAuthoritativeProbabilityMap(
            SceneInfo scene,
            RunInfo run,
            Dictionary<int, double> probabilityByRecipeId)
        {
            ServerOrderControllerBase orderController;
            if (!AuthoritativeOrderControllersByTeam.TryGetValue(run.TeamId, out orderController)
                || orderController == null
                || ServerRoundDataField == null
                || ServerRoundInstanceDataField == null
                || RoundInstanceRecipeCountField == null
                || RoundInstanceCumulativeFrequenciesField == null)
            {
                return false;
            }

            try
            {
                RoundData roundData = ServerRoundDataField.GetValue(orderController) as RoundData;
                object instanceData = ServerRoundInstanceDataField.GetValue(orderController);
                if (roundData == null || instanceData == null)
                {
                    return false;
                }

                Type roundDataType = roundData.GetType();
                if (roundDataType != typeof(RoundData)
                    && roundDataType != typeof(ScriptedRoundData)
                    && roundDataType != typeof(DynamicRoundData))
                {
                    return false;
                }

                int recipeCount = (int)RoundInstanceRecipeCountField.GetValue(instanceData);
                int[] cumulativeFrequencies = RoundInstanceCumulativeFrequenciesField.GetValue(instanceData) as int[];
                RecipeList.Entry[] entries = GetAuthoritativeEntries(scene, roundData, instanceData, cumulativeFrequencies);
                return TryBuildProbabilityFromEntries(
                    scene,
                    roundData,
                    recipeCount,
                    entries,
                    cumulativeFrequencies,
                    probabilityByRecipeId);
            }
            catch
            {
                return false;
            }
        }

        private static RecipeList.Entry[] GetAuthoritativeEntries(
            SceneInfo scene,
            RoundData roundData,
            object instanceData,
            int[] cumulativeFrequencies)
        {
            RecipeList.Entry[] baseEntries = null;
            int phaseIndex = 0;
            DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
            if (dynamicRoundData != null)
            {
                if (DynamicRoundInstanceCurrentPhaseField == null
                    || dynamicRoundData.Phases == null
                    || dynamicRoundData.Phases.Length == 0)
                {
                    return null;
                }

                phaseIndex = (int)DynamicRoundInstanceCurrentPhaseField.GetValue(instanceData);
                if (phaseIndex < 0 || phaseIndex >= dynamicRoundData.Phases.Length)
                {
                    return null;
                }

                DynamicRoundData.Phase phase = dynamicRoundData.Phases[phaseIndex];
                baseEntries = phase != null && phase.Recipes != null ? phase.Recipes.m_recipes : null;
            }
            else
            {
                baseEntries = roundData.m_recipes != null ? roundData.m_recipes.m_recipes : null;
            }

            return ExpandEntriesToFrequencyShape(scene, baseEntries, cumulativeFrequencies, phaseIndex);
        }

        private static RecipeList.Entry[] ExpandEntriesToFrequencyShape(
            SceneInfo scene,
            RecipeList.Entry[] baseEntries,
            int[] cumulativeFrequencies,
            int phaseIndex)
        {
            if (baseEntries == null || cumulativeFrequencies == null || baseEntries.Length > cumulativeFrequencies.Length)
            {
                return null;
            }

            ProbabilityExtensionEntriesBuffer.Clear();
            string levelConfigName = scene != null && scene.RuntimeLevelConfig != null
                ? scene.RuntimeLevelConfig.name
                : (scene != null ? scene.LevelConfigName : string.Empty);
            ManyRecipesSnapshotState extensionState = OptionalRecipeAdapters.AppendManyRecipeEntries(
                ProbabilityExtensionEntriesBuffer,
                levelConfigName,
                phaseIndex,
                false);
            if (!ManyRecipesSnapshotPolicy.HasExactRuntimeShape(
                extensionState,
                baseEntries.Length,
                ProbabilityExtensionEntriesBuffer.Count,
                cumulativeFrequencies.Length))
            {
                return null;
            }

            if (ProbabilityExtensionEntriesBuffer.Count == 0)
            {
                return baseEntries;
            }

            if (ProbabilityEntriesBuffer.Length != cumulativeFrequencies.Length)
            {
                ProbabilityEntriesBuffer = new RecipeList.Entry[cumulativeFrequencies.Length];
            }

            Array.Copy(baseEntries, ProbabilityEntriesBuffer, baseEntries.Length);
            for (int i = 0; i < ProbabilityExtensionEntriesBuffer.Count; i++)
            {
                ProbabilityEntriesBuffer[baseEntries.Length + i] = ProbabilityExtensionEntriesBuffer[i];
            }

            return ProbabilityEntriesBuffer;
        }

        private static bool TryBuildProbabilityFromEntries(
            SceneInfo scene,
            RoundData roundData,
            int recipeCount,
            RecipeList.Entry[] entries,
            int[] cumulativeFrequencies,
            Dictionary<int, double> probabilityByRecipeId)
        {
            if (roundData == null || entries == null || cumulativeFrequencies == null || entries.Length != cumulativeFrequencies.Length)
            {
                return false;
            }

            ScriptedRoundData scriptedRoundData = roundData as ScriptedRoundData;
            if (scriptedRoundData != null && scriptedRoundData.m_manualOrder != null && recipeCount < scriptedRoundData.m_manualOrder.Length)
            {
                RecipeList.Entry manualEntry = recipeCount >= 0 ? scriptedRoundData.m_manualOrder[recipeCount] : null;
                return TrySetDeterministicProbability(scene, manualEntry, probabilityByRecipeId);
            }

            EnsureProbabilityBufferCapacity(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                RecipeList.Entry entry = entries[i];
                if (entry == null || entry.m_order == null)
                {
                    return false;
                }

                int recipeId = entry.m_order.m_uID;
                if (scene == null || !scene.RecipesById.ContainsKey(recipeId))
                {
                    return false;
                }

                ProbabilityRecipeIdsBuffer[i] = recipeId;
                ProbabilityCumulativeFrequenciesBuffer[i] = cumulativeFrequencies[i];
            }

            bool carnival = IsCarnivalScene(scene) && !(roundData is DynamicRoundData);
            if (carnival && MenuManager.IsCarnivalMenuFixedEnabled)
            {
                int[] fixedSequence = MenuManager.carnivalMenu != null && MenuManager.carnivalMenu.Length > 0
                    ? MenuManager.carnivalMenu[0]
                    : null;
                int fixedRecipeId;
                if (ProbabilityPolicy.TryGetSequenceRecipe(
                    ProbabilityRecipeIdsBuffer,
                    ProbabilityCumulativeFrequenciesBuffer,
                    fixedSequence,
                    out fixedRecipeId))
                {
                    probabilityByRecipeId[fixedRecipeId] = 1d;
                    return true;
                }
            }

            if (carnival && MenuManager.IsCarnivalMenuGoodEnabled && !MenuManager.IsCarnivalMenuFixedEnabled)
            {
                int baseRecipeCount = roundData.m_recipes != null && roundData.m_recipes.m_recipes != null
                    ? roundData.m_recipes.m_recipes.Length
                    : 0;
                if (!CarnivalRecipeSelectionPolicy.TryCalculateWeights(
                    ProbabilityCumulativeFrequenciesBuffer,
                    baseRecipeCount,
                    MenuManager.IsCarnivalCakeGoodEnabled,
                    ProbabilityCarnivalWeightsBuffer))
                {
                    return false;
                }

                for (int i = 0; i < ProbabilityCarnivalWeightsBuffer.Length; i++)
                {
                    ProbabilityRawWeightsBuffer[i] = ProbabilityCarnivalWeightsBuffer[i];
                }

                if (!ProbabilityPolicy.TryNormalizeEntryWeights(ProbabilityRawWeightsBuffer, ProbabilityEntryValuesBuffer))
                {
                    return false;
                }
            }
            else if (!ProbabilityPolicy.TryCalculateEntryProbabilities(
                ProbabilityRecipeIdsBuffer,
                ProbabilityCumulativeFrequenciesBuffer,
                ProbabilityEntryValuesBuffer))
            {
                return false;
            }

            return ProbabilityPolicy.TryAggregateByRecipe(
                ProbabilityRecipeIdsBuffer,
                ProbabilityEntryValuesBuffer,
                probabilityByRecipeId);
        }

        private static bool TryBuildReconstructedProbabilityMap(
            SceneInfo scene,
            RunInfo run,
            Dictionary<int, double> probabilityByRecipeId)
        {
            if (run == null || !run.ReconstructionComplete)
            {
                return false;
            }

            // A failed optional-provider read is retryable, but it is never safe to
            // fabricate a base-only probability while the provider may be active.
            ManyRecipesSnapshotState extensionState = OptionalRecipeAdapters.GetManyRecipesSnapshotState();
            if (extensionState == ManyRecipesSnapshotState.ActiveUnavailable
                || scene == null
                || scene.ManyRecipesState != extensionState)
            {
                return false;
            }

            KitchenLevelConfigBase kitchenLevelConfig = scene.RuntimeLevelConfig as KitchenLevelConfigBase;
            RoundData roundData = kitchenLevelConfig != null ? kitchenLevelConfig.GetRoundData() as RoundData : null;
            if (roundData == null
                || (roundData.GetType() != typeof(RoundData)
                    && roundData.GetType() != typeof(ScriptedRoundData)
                    && roundData.GetType() != typeof(DynamicRoundData)))
            {
                return false;
            }

            if (IsCarnivalScene(scene))
            {
                return false;
            }

            ScriptedRoundData scriptedRoundData = roundData as ScriptedRoundData;
            if (scriptedRoundData != null
                && scriptedRoundData.m_manualOrder != null
                && run.TotalAdded < scriptedRoundData.m_manualOrder.Length)
            {
                RecipeList.Entry manualEntry = run.TotalAdded >= 0 ? scriptedRoundData.m_manualOrder[run.TotalAdded] : null;
                return TrySetDeterministicProbability(scene, manualEntry, probabilityByRecipeId);
            }

            RecipeList.Entry[] entries;
            DynamicRoundData dynamicRoundData = roundData as DynamicRoundData;
            if (dynamicRoundData != null)
            {
                if (dynamicRoundData.Phases == null || dynamicRoundData.Phases.Length == 0)
                {
                    return false;
                }

                int phaseIndex = Mathf.Clamp(run.CurrentPhaseIndex, 0, dynamicRoundData.Phases.Length - 1);
                DynamicRoundData.Phase phase = dynamicRoundData.Phases[phaseIndex];
                entries = phase != null && phase.Recipes != null ? phase.Recipes.m_recipes : null;
            }
            else
            {
                entries = roundData.m_recipes != null ? roundData.m_recipes.m_recipes : null;
            }

            if (entries == null || entries.Length == 0)
            {
                return false;
            }

            EnsureProbabilityBufferCapacity(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                RecipeList.Entry entry = entries[i];
                if (entry == null || entry.m_order == null)
                {
                    return false;
                }

                int recipeId = entry.m_order.m_uID;
                if (scene == null || !scene.RecipesById.ContainsKey(recipeId))
                {
                    return false;
                }

                ProbabilityRecipeIdsBuffer[i] = recipeId;
            }

            bool baseRecipeIdsDistinct = ProbabilityPolicy.TryCollectDistinctRecipeIds(
                ProbabilityRecipeIdsBuffer,
                ReconstructableRecipeIdsBuffer);
            if (!ProbabilityReconstructionPolicy.CanUseRandomBaseEntries(
                run.ReconstructionComplete,
                scene.ExtensionRecipeIds.Count > 0
                    || ManyRecipesSnapshotPolicy.HasGeneratedEntries(
                        scene.ManyRecipesState,
                        scene.ManyRecipesOrderedEntryIds.Count),
                baseRecipeIdsDistinct))
            {
                return false;
            }

            ReconstructedRecipeCountsBuffer.Clear();
            foreach (KeyValuePair<int, int> pair in run.AddedCounts)
            {
                ReconstructedRecipeCountsBuffer[pair.Key] = pair.Value;
            }

            if (scriptedRoundData != null && scriptedRoundData.m_manualOrder != null)
            {
                for (int i = 0; i < scriptedRoundData.m_manualOrder.Length; i++)
                {
                    RecipeList.Entry manualEntry = scriptedRoundData.m_manualOrder[i];
                    if (manualEntry == null || manualEntry.m_order == null)
                    {
                        return false;
                    }

                    int manualRecipeId = manualEntry.m_order.m_uID;
                    int existing = GetCount(ReconstructedRecipeCountsBuffer, manualRecipeId);
                    if (existing <= 0)
                    {
                        return false;
                    }

                    ReconstructedRecipeCountsBuffer[manualRecipeId] = existing - 1;
                }
            }

            foreach (KeyValuePair<int, int> pair in ReconstructedRecipeCountsBuffer)
            {
                if (pair.Value > 0 && !ReconstructableRecipeIdsBuffer.Contains(pair.Key))
                {
                    return false;
                }
            }

            for (int i = 0; i < entries.Length; i++)
            {
                ProbabilityCumulativeFrequenciesBuffer[i] = GetCount(ReconstructedRecipeCountsBuffer, ProbabilityRecipeIdsBuffer[i]);
            }

            if (!ProbabilityPolicy.TryCalculateEntryProbabilities(
                ProbabilityRecipeIdsBuffer,
                ProbabilityCumulativeFrequenciesBuffer,
                ProbabilityEntryValuesBuffer))
            {
                return false;
            }

            return ProbabilityPolicy.TryAggregateByRecipe(
                ProbabilityRecipeIdsBuffer,
                ProbabilityEntryValuesBuffer,
                probabilityByRecipeId);
        }

        private static bool TrySetDeterministicProbability(
            SceneInfo scene,
            RecipeList.Entry entry,
            Dictionary<int, double> probabilityByRecipeId)
        {
            if (scene == null
                || entry == null
                || entry.m_order == null
                || !scene.RecipesById.ContainsKey(entry.m_order.m_uID))
            {
                return false;
            }

            probabilityByRecipeId.Clear();
            probabilityByRecipeId[entry.m_order.m_uID] = 1d;
            return true;
        }

        private static void EnsureProbabilityBufferCapacity(int count)
        {
            if (ProbabilityRecipeIdsBuffer.Length != count)
            {
                ProbabilityRecipeIdsBuffer = new int[count];
                ProbabilityCumulativeFrequenciesBuffer = new int[count];
                ProbabilityEntryValuesBuffer = new double[count];
                ProbabilityRawWeightsBuffer = new double[count];
                ProbabilityCarnivalWeightsBuffer = new float[count];
            }
        }

        private static bool IsCarnivalScene(SceneInfo scene)
        {
            string levelConfigName = scene != null && scene.RuntimeLevelConfig != null
                ? scene.RuntimeLevelConfig.name
                : (scene != null ? scene.LevelConfigName : string.Empty);
            return !string.IsNullOrEmpty(levelConfigName)
                && levelConfigName.StartsWith("Day_3_4", StringComparison.Ordinal);
        }

        private static double GetProbability(Dictionary<int, double> probabilityByRecipeId, int recipeId)
        {
            double probability;
            return probabilityByRecipeId != null && probabilityByRecipeId.TryGetValue(recipeId, out probability) ? probability : 0d;
        }

        private static List<int> GetActiveRecipeIds(SceneInfo scene, RunInfo run)
        {
            if (scene.PhaseRecipeIds == null || scene.PhaseRecipeIds.Length == 0)
            {
                return scene.AllRecipeIds;
            }

            int phaseIndex = Mathf.Clamp(run.CurrentPhaseIndex, 0, scene.PhaseRecipeIds.Length - 1);
            return scene.PhaseRecipeIds[phaseIndex];
        }

        private static bool IsTracked(SceneInfo scene, int recipeId)
        {
            HashSet<int> trackedIds;
            bool hasExplicitSelection = TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds);
            return TrackingSelectionPolicy.IsTracked(hasExplicitSelection, trackedIds, recipeId);
        }

        private static string GetRecipeDisplayName(RecipeInfo recipe)
        {
            string displayName = UseChinese() ? recipe.ChineseName : recipe.EnglishName;
            return !string.IsNullOrEmpty(displayName) ? displayName : GetRecipeConfigName(recipe);
        }

        private static string GetRecipeSelectionLabel(RecipeInfo recipe)
        {
            string displayName = GetRecipeDisplayName(recipe);
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = GetRecipeConfigName(recipe);
            }

            return TruncateWithEllipsis(displayName, GetMaxDishSelectorDisplayLength());
        }

        private static string GetOverlaySceneLabel(SceneInfo scene)
        {
            if (scene == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(scene.SceneName))
            {
                return scene.SceneName;
            }

            return !string.IsNullOrEmpty(scene.DisplayName) ? scene.DisplayName : string.Empty;
        }

        private static string WrapRichValue(string value, Color color)
        {
            return "<color=#" + ColorUtility.ToHtmlStringRGBA(color) + ">" + value + "</color>";
        }

        private static string GetOverlayTodoPrefix(OverlayRow row, bool showPrepared)
        {
            if (row == null)
            {
                return "[   ]";
            }

            if (showPrepared && row.Prepared > 0)
            {
                return "[ v ]";
            }

            if (row.OnMenu > 0)
            {
                return "[   ]";
            }

            if (!row.ProbabilityAvailable)
            {
                return "[ ? ]";
            }

            return row.Probability > 0d ? "[ - ]" : "[ x ]";
        }

        private static string GetOverlayDishNameText(SceneInfo scene, OverlayRow row, bool showPrepared)
        {
            string name = GetOverlayRecipeDisplayName(
                scene,
                row.Recipe,
                GetMaxOverlayDishDisplayLength());
            if (showPrepared)
            {
                if (row.Prepared > 0)
                {
                    return WrapRichValue(name, new Color(0.56f, 0.94f, 0.56f, 1f));
                }

                if (row.OnMenu > 0)
                {
                    return WrapRichValue(name, new Color(1f, 0.76f, 0.34f, 1f));
                }
            }

            return name;
        }

        private static string GetOverlayRecipeDisplayName(SceneInfo scene, RecipeInfo recipe, int maximumLength)
        {
            if (recipe == null)
            {
                return string.Empty;
            }

            string displayName = GetRecipeDisplayName(recipe);
            if (scene == null || string.IsNullOrEmpty(displayName))
            {
                return TruncateWithEllipsis(displayName, maximumLength);
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo other = scene.OrderedRecipes[i];
                if (other != null
                    && other.Id != recipe.Id
                    && string.Equals(GetRecipeDisplayName(other), displayName, StringComparison.OrdinalIgnoreCase))
                {
                    string idSuffix = " [" + recipe.Id + "]";
                    int nameLength = Math.Max(1, maximumLength - idSuffix.Length);
                    return TruncateWithEllipsis(displayName, nameLength) + idSuffix;
                }
            }

            return TruncateWithEllipsis(displayName, maximumLength);
        }

        private static int GetMaxDishSelectorDisplayLength()
        {
            int fallback = UseChinese() ? MaxDishSelectorDisplayLength : MaxDishSelectorDisplayLengthEnglish;
            return Mathf.Clamp(dishSelectorMaxTextLength != null ? dishSelectorMaxTextLength.Value : fallback, MinDishSelectorDisplayLength, MaxDishSelectorDisplayLengthSetting);
        }

        private static int GetMaxSceneSelectorDisplayLength()
        {
            return Mathf.Clamp(sceneSelectorMaxTextLength != null ? sceneSelectorMaxTextLength.Value : MaxSceneSelectorDisplayLength, MinSceneSelectorDisplayLength, MaxSceneSelectorDisplayLengthSetting);
        }

        private static int GetMaxOverlaySceneDisplayLength()
        {
            return Mathf.Clamp(overlaySceneMaxTextLength != null ? overlaySceneMaxTextLength.Value : MaxOverlaySceneDisplayLength, MinOverlaySceneDisplayLength, MaxOverlaySceneDisplayLengthSetting);
        }

        private static int GetMaxOverlayDishDisplayLength()
        {
            int fallback = UseChinese() ? MaxOverlayDishDisplayLength : MaxOverlayDishDisplayLengthEnglish;
            return Mathf.Clamp(overlayDishMaxTextLength != null ? overlayDishMaxTextLength.Value : fallback, MinOverlayDishDisplayLength, MaxOverlayDishDisplayLengthSetting);
        }

        private static string Ui(string chineseText, string englishText)
        {
            return UseChinese() ? chineseText : englishText;
        }

        private static int GetInitialDishSelectorDisplayLength()
        {
            return IsGameLanguageChinese() ? MaxDishSelectorDisplayLength : MaxDishSelectorDisplayLengthEnglish;
        }

        private static int GetInitialOverlayDishDisplayLength()
        {
            return IsGameLanguageChinese() ? MaxOverlayDishDisplayLength : MaxOverlayDishDisplayLengthEnglish;
        }

        private static Color GetOverlayServedValueColor(OverlayRow row, bool showPrepared)
        {
            Color baseColor = overlayServedValueColor != null ? overlayServedValueColor.Value : new Color(0.58f, 0.84f, 1f, 1f);
            return AdjustOverlayValueColor(baseColor, row, showPrepared);
        }

        private static Color GetOverlayProbabilityValueColor(OverlayRow row, bool showPrepared)
        {
            Color baseColor = overlayProbabilityValueColor != null ? overlayProbabilityValueColor.Value : new Color(1f, 0.84f, 0.40f, 1f);
            return AdjustOverlayValueColor(baseColor, row, showPrepared);
        }

        private static Color AdjustOverlayValueColor(Color baseColor, OverlayRow row, bool showPrepared)
        {
            if (row == null)
            {
                return baseColor;
            }

            bool isDeferredTodo = row.ProbabilityAvailable
                && row.Probability <= 0d
                && row.OnMenu <= 0
                && (!showPrepared || row.Prepared <= 0);
            if (!isDeferredTodo)
            {
                return baseColor;
            }

            return new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * 0.38f);
        }

        private static Color GetOverlayRowBackgroundColor(OverlayRow row, bool showPrepared)
        {
            if (row == null)
            {
                return Color.clear;
            }

            bool isPrepared = showPrepared && row.Prepared > 0;
            if (isPrepared)
            {
                return new Color(0.22f, 0.58f, 0.22f, 0.28f);
            }

            if (row.ProbabilityAvailable
                && row.Probability <= 0d
                && row.OnMenu <= 0
                && (!showPrepared || row.Prepared <= 0))
            {
                return new Color(0f, 0f, 0f, 0.22f);
            }

            return Color.clear;
        }

        private static Color GetOverlayRowTextTint(OverlayRow row, bool showPrepared)
        {
            if (row == null)
            {
                return Color.white;
            }

            if (showPrepared && row.Prepared > 0)
            {
                return new Color(1f, 1f, 1f, 0.88f);
            }

            if (row.ProbabilityAvailable && row.Probability <= 0d && row.OnMenu <= 0)
            {
                return new Color(1f, 1f, 1f, 0.42f);
            }

            if (row.OnMenu <= 0)
            {
                return new Color(1f, 1f, 1f, 0.90f);
            }

            return Color.white;
        }

        private static string WrapPreparedValue(int preparedCount)
        {
            Color color = preparedCount > 0
                ? (overlayPreparedValueColor != null ? overlayPreparedValueColor.Value : new Color(1f, 0.56f, 0.76f, 1f))
                : new Color(0.66f, 0.66f, 0.66f, 1f);
            return WrapRichValue(preparedCount.ToString(), color);
        }

        private static List<TeamID> GetActiveTeamIds()
        {
            ActiveTeamIdsBuffer.Clear();
            ClientKitchenFlowControllerBase flowController = GetKitchenFlowController();
            if (flowController == null)
            {
                ActiveTeamIdsBuffer.Add(TeamID.One);
                return ActiveTeamIdsBuffer;
            }

            VisitedOrderControllersBuffer.Clear();
            for (int i = 0; i < SupportedTeamIds.Length; i++)
            {
                ClientTeamMonitor monitor;
                try
                {
                    monitor = flowController.GetMonitorForTeam(SupportedTeamIds[i]);
                }
                catch
                {
                    continue;
                }

                if (monitor == null
                    || monitor.OrdersController == null
                    || !VisitedOrderControllersBuffer.Add(monitor.OrdersController))
                {
                    continue;
                }

                ActiveTeamIdsBuffer.Add(SupportedTeamIds[i]);
            }

            if (ActiveTeamIdsBuffer.Count == 0)
            {
                ActiveTeamIdsBuffer.Add(TeamID.One);
            }

            return ActiveTeamIdsBuffer;
        }

        private static Dictionary<int, int> GetCurrentOnMenuCounts(SceneInfo scene)
        {
            CombinedOnMenuCountsBuffer.Clear();
            if (scene == null)
            {
                return CombinedOnMenuCountsBuffer;
            }

            if (!string.Equals(currentOnMenuCountsSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                || currentOnMenuCountsDirty)
            {
                RebuildCurrentOnMenuCounts(scene.SceneName);
            }

            foreach (Dictionary<int, int> teamCounts in CurrentOnMenuCountsByTeam.Values)
            {
                foreach (KeyValuePair<int, int> pair in teamCounts)
                {
                    CombinedOnMenuCountsBuffer[pair.Key] = GetCount(CombinedOnMenuCountsBuffer, pair.Key) + pair.Value;
                }
            }

            return CombinedOnMenuCountsBuffer;
        }

        private static Dictionary<int, int> GetCurrentOnMenuCounts(SceneInfo scene, TeamID teamId)
        {
            if (scene == null)
            {
                return GetOrCreateOnMenuCounts(teamId);
            }

            if (!string.Equals(currentOnMenuCountsSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                || currentOnMenuCountsDirty)
            {
                RebuildCurrentOnMenuCounts(scene.SceneName);
            }

            return GetOrCreateOnMenuCounts(teamId);
        }

        private static Dictionary<int, int> BuildMenuOrderMap(SceneInfo scene, TeamID teamId)
        {
            MenuOrderByRecipeBuffer.Clear();
            if (scene == null || TicketWidgetsByInstanceId.Count == 0)
            {
                return MenuOrderByRecipeBuffer;
            }

            foreach (KeyValuePair<int, TicketWidgetState> pair in TicketWidgetsByInstanceId)
            {
                TicketWidgetState state = pair.Value;
                if (state == null
                    || state.Widget == null
                    || state.IsReferenceTicket
                    || state.TeamId != teamId
                    || state.Order < 0
                    || !IsTracked(scene, state.RecipeId))
                {
                    continue;
                }

                int existingOrder;
                if (!MenuOrderByRecipeBuffer.TryGetValue(state.RecipeId, out existingOrder) || state.Order < existingOrder)
                {
                    MenuOrderByRecipeBuffer[state.RecipeId] = state.Order;
                }
            }

            return MenuOrderByRecipeBuffer;
        }

        private static void RebuildCurrentOnMenuCounts(string sceneName)
        {
            CurrentOnMenuCountsByTeam.Clear();
            currentOnMenuCountsSceneName = sceneName ?? string.Empty;
            currentOnMenuCountsDirty = false;

            ClientKitchenFlowControllerBase flowController = GetKitchenFlowController();
            if (flowController == null)
            {
                return;
            }

            HashSet<ClientOrderControllerBase> visitedControllers = VisitedOrderControllersBuffer;
            visitedControllers.Clear();
            for (int i = 0; i < SupportedTeamIds.Length; i++)
            {
                ClientTeamMonitor monitor;
                try
                {
                    monitor = flowController.GetMonitorForTeam(SupportedTeamIds[i]);
                }
                catch
                {
                    continue;
                }

                if (monitor == null || monitor.OrdersController == null || !visitedControllers.Add(monitor.OrdersController))
                {
                    continue;
                }

                IList activeOrders = ActiveOrdersField != null ? ActiveOrdersField.GetValue(monitor.OrdersController) as IList : null;
                if (activeOrders == null)
                {
                    continue;
                }

                Dictionary<int, int> teamCounts = GetOrCreateOnMenuCounts(SupportedTeamIds[i]);
                for (int j = 0; j < activeOrders.Count; j++)
                {
                    object activeOrder = activeOrders[j];
                    if (activeOrder == null)
                    {
                        continue;
                    }

                    RecipeList.Entry recipeEntry = ActiveOrderRecipeListEntryField != null
                        ? ActiveOrderRecipeListEntryField.GetValue(activeOrder) as RecipeList.Entry
                        : null;
                    if (recipeEntry == null || recipeEntry.m_order == null)
                    {
                        continue;
                    }

                    int recipeId = recipeEntry.m_order.m_uID;
                    teamCounts[recipeId] = GetCount(teamCounts, recipeId) + 1;
                }
            }
        }

        private static string TruncateSceneSelectorValue(string value, string sceneName)
        {
            int maxLength = GetMaxSceneSelectorDisplayLength();
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (value.Length <= maxLength)
            {
                return value;
            }

            string sceneSuffix = string.IsNullOrEmpty(sceneName) ? string.Empty : " [" + sceneName + "]";
            if (!string.IsNullOrEmpty(sceneSuffix) && sceneSuffix.Length + 4 < maxLength)
            {
                int prefixLength = maxLength - sceneSuffix.Length - 1;
                if (prefixLength > 0)
                {
                    string prefix = value.Substring(0, Math.Min(value.Length, prefixLength));
                    return TruncateWithEllipsis(prefix, prefixLength) + sceneSuffix;
                }
            }

            return TruncateMiddle(value, maxLength);
        }

        private static string AppendSuffixWithLengthLimit(string value, string suffix, int maxLength)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return TruncateWithEllipsis(value, maxLength);
            }

            string trimmed = TruncateWithEllipsis(value, Math.Max(1, maxLength - suffix.Length));
            return trimmed + suffix;
        }

        private static string TruncateWithEllipsis(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength == 1)
            {
                return "…";
            }

            return value.Substring(0, maxLength - 1) + "…";
        }

        private static string TruncateMiddle(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength == 1)
            {
                return "…";
            }

            int keep = maxLength - 1;
            int left = keep / 2;
            int right = keep - left;
            return value.Substring(0, left) + "…" + value.Substring(value.Length - right);
        }

        private static string GetLanguageLabel(bool chinese)
        {
            if (languageMode.Value == TrackerLanguage.Auto)
            {
                return chinese ? "自动" : "Auto";
            }
            if (languageMode.Value == TrackerLanguage.Chinese)
            {
                return chinese ? "中文" : "Chinese";
            }
            return chinese ? "英文" : "English";
        }

        private static bool UseChinese()
        {
            if (languageMode.Value == TrackerLanguage.Chinese)
            {
                return true;
            }
            if (languageMode.Value == TrackerLanguage.English)
            {
                return false;
            }

            SupportedLanguages language = Localization.GetLanguage();
            return language == SupportedLanguages.Chinese || language == SupportedLanguages.ChineseTraditional;
        }

        private static bool IsGameLanguageChinese()
        {
            SupportedLanguages language = Localization.GetLanguage();
            return language == SupportedLanguages.Chinese || language == SupportedLanguages.ChineseTraditional;
        }

        private static void UpdateIdScanStatus(List<SceneInfo> scenes)
        {
            idScanSceneCount = scenes.Count;
            idConflictCount = 0;
            idConflictSample = string.Empty;

            Dictionary<int, string> firstNameById = new Dictionary<int, string>();
            Dictionary<int, string> firstSceneById = new Dictionary<int, string>();

            for (int i = 0; i < scenes.Count; i++)
            {
                SceneInfo scene = scenes[i];
                if (scene == null)
                {
                    continue;
                }

                for (int j = 0; j < scene.OrderedRecipes.Count; j++)
                {
                    RecipeInfo recipe = scene.OrderedRecipes[j];
                    string recipeName = recipe.InternalName ?? string.Empty;
                    string existingName;
                    if (!firstNameById.TryGetValue(recipe.Id, out existingName))
                    {
                        firstNameById.Add(recipe.Id, recipeName);
                        firstSceneById.Add(recipe.Id, scene.SceneName);
                        continue;
                    }

                    if (!string.Equals(existingName, recipeName, StringComparison.Ordinal))
                    {
                        idConflictCount++;
                        if (string.IsNullOrEmpty(idConflictSample))
                        {
                            idConflictSample = "ID " + recipe.Id + ": " + firstSceneById[recipe.Id] + " -> " + existingName + " / " + scene.SceneName + " -> " + recipeName;
                        }
                    }
                }
            }

            string currentLog = "Scene scan: " + idScanSceneCount + " scenes, " + idConflictCount + " id conflicts."
                + (string.IsNullOrEmpty(idConflictSample) ? string.Empty : " Sample: " + idConflictSample);
            if (!string.Equals(currentLog, lastIdScanLog, StringComparison.Ordinal))
            {
                lastIdScanLog = currentLog;
                _MODEntry.LogInfo("[ServedDishTracker] " + currentLog);
            }
        }

    }
}
