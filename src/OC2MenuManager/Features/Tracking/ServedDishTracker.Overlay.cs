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
        private static void ResetProbabilityState(int phaseIndex)
        {
            if (currentRun == null)
            {
                return;
            }

            currentRun.CurrentPhaseIndex = Math.Max(0, phaseIndex);
            currentRun.TotalAdded = 0;
            currentRun.AddedCounts.Clear();
            InvalidateProbabilityMap();
            InvalidatePreparedCandidates(true);
            InvalidateOverlay();
        }

        private static int ComputeOverlayContentSignature()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + (enabled != null && enabled.Value ? 1 : 0);
                hash = (hash * 31) + (preparedTrackingEnabled != null && preparedTrackingEnabled.Value ? 1 : 0);
                hash = (hash * 31) + (overlayMaxDisplayDishes != null ? overlayMaxDisplayDishes.Value : 0);
                hash = (hash * 31) + (int)(languageMode != null ? languageMode.Value : TrackerLanguage.Auto);
                hash = (hash * 31) + (currentRun != null ? currentRun.TotalAdded : 0);
                hash = (hash * 31) + (currentRun != null ? currentRun.CurrentPhaseIndex : 0);
                return hash;
            }
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

            bool showPrepared = IsPreparedTrackingEnabled();
            RunInfo run = EnsureRun(scene);
            List<OverlayRow> rows = BuildAndSortOverlayRows(scene, run, showPrepared);

            bool chinese = UseChinese();
            if (rows.Count == 0)
            {
                OverlayRenderRowsBuffer.Clear();
                return chinese
                ? "历史菜单追踪\n当前关卡没有勾选任何追踪菜品。\n请打开 OC2MenuManager 独立窗口勾选需要追踪的菜品。"
                : "Menu History Tracker\nNo dishes are tracked for this scene.\nOpen the standalone OC2MenuManager window to choose tracked dishes.";
            }

            StringBuilder builder = OverlayTextBuilder;
            builder.Length = 0;
            builder.Append(TruncateWithEllipsis(GetOverlaySceneLabel(scene), GetMaxOverlaySceneDisplayLength())).Append(" | ");
            builder.Append(chinese ? "已追踪 " : "Tracking ");
            builder.Append(rows.Count).Append('/').Append(scene.OrderedRecipes.Count).Append('\n');
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

            int maxRows = Math.Min(rows.Count, Math.Max(1, overlayMaxDisplayDishes != null ? overlayMaxDisplayDishes.Value : 12));
            for (int i = 0; i < maxRows; i++)
            {
                OverlayRow row = rows[i];
                bool isDeferredTodo = row.Probability <= 0d && row.OnMenu <= 0 && (!showPrepared || row.Prepared <= 0);
                builder.Append(GetOverlayTodoPrefix(row, showPrepared)).Append(' ');
                builder.Append(GetOverlayDishNameText(row, showPrepared));
                builder.Append("  |  ");
                builder.Append(WrapRichValue(row.Served.ToString(), GetOverlayServedValueColor(row, showPrepared)));
                builder.Append("  |  ");
                builder.Append(WrapRichValue((row.Probability * 100d).ToString("0.0") + "%", GetOverlayProbabilityValueColor(row, showPrepared)));
                OverlayRenderRow renderRow = GetOrCreateOverlayRenderRow(i);
                renderRow.Text = builder.ToString();
                renderRow.BackgroundColor = GetOverlayRowBackgroundColor(row, showPrepared);
                renderRow.HasBackground = renderRow.BackgroundColor.a > 0f;
                renderRow.TextTint = GetOverlayRowTextTint(row, showPrepared);
                renderRow.HasStrikeThrough = isDeferredTodo;
                builder.Length = 0;
            }

            if (OverlayRenderRowsBuffer.Count > maxRows)
            {
                for (int i = maxRows; i < OverlayRenderRowsBuffer.Count; i++)
                {
                    OverlayRenderRowsBuffer[i].Reset();
                }

                OverlayRenderRowsBuffer.RemoveRange(maxRows, OverlayRenderRowsBuffer.Count - maxRows);
            }

            if (rows.Count > maxRows)
            {
                builder.Length = 0;
                builder.Append(chinese ? "+ 还有 " : "+ ").Append(rows.Count - maxRows);
                builder.Append(chinese ? " 个追踪菜品未显示" : " more tracked dishes");
                overlayFooterText = builder.ToString();
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
            if (!string.IsNullOrEmpty(overlayFooterText))
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(overlayFooterText);
            }

            return builder.ToString().TrimEnd();
        }

        private static List<OverlayRow> BuildAndSortOverlayRows(SceneInfo scene, RunInfo run, bool showPrepared)
        {
            List<OverlayRow> rows = OverlayRowsBuffer;
            if (scene == null || run == null)
            {
                rows.Clear();
                cachedOverlayRowsSceneName = string.Empty;
                cachedOverlayRowsShowPrepared = showPrepared;
                cachedOverlayRowsVersion = overlayRowsVersion;
                return rows;
            }

            if (cachedOverlayRowsVersion == overlayRowsVersion
                && cachedOverlayRowsShowPrepared == showPrepared
                && string.Equals(cachedOverlayRowsSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                return rows;
            }

            rows.Clear();
            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            Dictionary<int, int> menuOrderByRecipeId = BuildMenuOrderMap(scene);
            Dictionary<int, double> probabilityByRecipeId = GetProbabilityMap(scene, run);
            int rowCount = 0;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null || !IsTracked(scene, recipe.Id))
                {
                    continue;
                }

                OverlayRow row = GetOrCreateOverlayRow(rowCount);
                row.Recipe = recipe;
                row.Probability = GetProbability(probabilityByRecipeId, recipe.Id);
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

            rows.Sort(delegate(OverlayRow a, OverlayRow b)
            {
                return CompareOverlayRows(a, b, showPrepared);
            });

            cachedOverlayRowsSceneName = scene.SceneName ?? string.Empty;
            cachedOverlayRowsShowPrepared = showPrepared;
            cachedOverlayRowsVersion = overlayRowsVersion;
            return rows;
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

            return string.Compare(GetRecipeDisplayName(a.Recipe), GetRecipeDisplayName(b.Recipe), StringComparison.OrdinalIgnoreCase);
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
            if (!prepared && row.Probability > 0d)
            {
                return 1;
            }

            if (!prepared && row.Probability <= 0d)
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
                probabilityMapDirty = true;
                probabilityMapSceneName = string.Empty;
                return ProbabilityByRecipeBuffer;
            }

            if (!probabilityMapDirty
                && string.Equals(probabilityMapSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                return ProbabilityByRecipeBuffer;
            }

            probabilityMapSceneName = scene.SceneName;
            probabilityMapDirty = false;
            return BuildProbabilityMap(scene, run);
        }

        private static Dictionary<int, double> BuildProbabilityMap(SceneInfo scene, RunInfo run)
        {
            Dictionary<int, double> probabilityByRecipeId = ProbabilityByRecipeBuffer;
            probabilityByRecipeId.Clear();
            if (scene == null || run == null || scene.OrderedRecipes.Count == 0)
            {
                return probabilityByRecipeId;
            }

            List<int> activeRecipeIds = GetActiveRecipeIds(scene, run);
            if (activeRecipeIds == null || activeRecipeIds.Count == 0)
            {
                return probabilityByRecipeId;
            }

            double recipeCount = activeRecipeIds.Count;
            double totalWeight = 0d;
            Dictionary<int, double> weightsByRecipeId = ProbabilityWeightsByRecipeBuffer;
            weightsByRecipeId.Clear();
            for (int i = 0; i < activeRecipeIds.Count; i++)
            {
                int id = activeRecipeIds[i];
                double weight = ((double)(run.TotalAdded + 2) / recipeCount) - GetCount(run.AddedCounts, id);
                if (weight < 0d)
                {
                    weight = 0d;
                }

                totalWeight += weight;
                double existingWeight;
                weightsByRecipeId[id] = weightsByRecipeId.TryGetValue(id, out existingWeight) ? existingWeight + weight : weight;
            }

            if (totalWeight <= 0d)
            {
                return probabilityByRecipeId;
            }

            foreach (KeyValuePair<int, double> pair in weightsByRecipeId)
            {
                probabilityByRecipeId[pair.Key] = pair.Value / totalWeight;
            }

            return probabilityByRecipeId;
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
            if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds))
            {
                return true;
            }

            return trackedIds.Contains(recipeId);
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

            return row.Probability > 0d ? "[ - ]" : "[ x ]";
        }

        private static string GetOverlayDishNameText(OverlayRow row, bool showPrepared)
        {
            string name = TruncateWithEllipsis(GetRecipeDisplayName(row.Recipe), GetMaxOverlayDishDisplayLength());
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

            bool isDeferredTodo = row.Probability <= 0d && row.OnMenu <= 0 && (!showPrepared || row.Prepared <= 0);
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

            if (row.Probability <= 0d && row.OnMenu <= 0 && (!showPrepared || row.Prepared <= 0))
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

            if (row.Probability <= 0d && row.OnMenu <= 0)
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

        private static Dictionary<int, int> GetCurrentOnMenuCounts(SceneInfo scene)
        {
            if (scene == null)
            {
                return CurrentOnMenuCounts;
            }

            if (!string.Equals(currentOnMenuCountsSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                || currentOnMenuCountsDirty)
            {
                RebuildCurrentOnMenuCounts(scene.SceneName);
            }

            return CurrentOnMenuCounts;
        }

        private static Dictionary<int, int> BuildMenuOrderMap(SceneInfo scene)
        {
            MenuOrderByRecipeBuffer.Clear();
            if (scene == null || TicketWidgetsByInstanceId.Count == 0)
            {
                return MenuOrderByRecipeBuffer;
            }

            foreach (KeyValuePair<int, TicketWidgetState> pair in TicketWidgetsByInstanceId)
            {
                TicketWidgetState state = pair.Value;
                if (state == null || state.Widget == null || state.IsReferenceTicket || state.Order < 0 || !IsTracked(scene, state.RecipeId))
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
            CurrentOnMenuCounts.Clear();
            currentOnMenuCountsSceneName = sceneName ?? string.Empty;
            currentOnMenuCountsDirty = false;

            ClientKitchenFlowControllerBase flowController = GetKitchenFlowController();
            if (flowController == null)
            {
                return;
            }

            HashSet<ClientOrderControllerBase> visitedControllers = VisitedOrderControllersBuffer;
            visitedControllers.Clear();
            for (int i = 0; i < TeamIds.Length; i++)
            {
                ClientTeamMonitor monitor;
                try
                {
                    monitor = flowController.GetMonitorForTeam(TeamIds[i]);
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
                    CurrentOnMenuCounts[recipeId] = GetCount(CurrentOnMenuCounts, recipeId) + 1;
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
