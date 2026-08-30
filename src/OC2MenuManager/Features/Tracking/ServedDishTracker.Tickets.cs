// Hardens RecipeFlowGUI capacity/removal and owns ticket presentation state.
// Real-ticket prepared tint consumes source compatibility, while reference
// tickets keep independent styling. Family and scene-specific selector groups
// share stable ID-based batch behavior, and safety patches remain unconditional.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OC2MenuManager.Infrastructure;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private const float ReferenceTicketColorSaturation = 0.40f;
        private const float ReferenceTicketColorBrightness = 0.88f;
        private const float ReferenceTicketOpacityScale = 0.80f;
        private const float ReferenceTicketDestroyAnimationTintStrength = 0.18f;

        /// <summary>
        /// Provides the synthetic-ticket-only fade used when a guess leaves the
        /// recipe bar without borrowing the base game's real-order animation state.
        /// </summary>
        private sealed class ReferenceTicketDestroyAnimation : WidgetAnimation
        {
            private readonly Color accentColor;
            private float elapsedTime;

            private const float TotalTime = 0.5f;

            public ReferenceTicketDestroyAnimation(Color accentColor)
            {
                this.accentColor = accentColor;
                this.accentColor.a = 1f;
            }

            public override void Advance(float _deltaTime)
            {
                elapsedTime += _deltaTime;
            }

            public override bool IsFinished()
            {
                return elapsedTime > TotalTime;
            }

            public override Color GetColourModifier()
            {
                float timeProp = Mathf.Clamp01(elapsedTime / TotalTime);
                float tintProp = Mathf.Sin((float)Math.PI / 2f * Mathf.Clamp01(2f * timeProp));
                Color result = Color.Lerp(Color.white, accentColor, tintProp);
                result.a = Mathf.Lerp(1f, 0f, SmoothStep(Mathf.Clamp01(2f * timeProp - 1f)));
                return result;
            }

            private static float SmoothStep(float value)
            {
                return 0.5f * (1f - Mathf.Cos((float)Math.PI * value));
            }
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "AddElement")]
        [HarmonyPrefix]
        private static void RecipeFlowGUI_AddElement_Prefix(RecipeFlowGUI __instance, VoidGeneric<RecipeFlowGUI.ElementToken> _expirationCallback)
        {
            if (__instance == null || IsMenuManagerReferenceTicketAdd(_expirationCallback))
            {
                return;
            }

            try
            {
                PrepareForIncomingRealTicket(__instance);
            }
            catch (Exception ex)
            {
                if (!ticketAdmissionFailureWarningLogged)
                {
                    ticketAdmissionFailureWarningLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Could not reserve a real-order ticket slot, but the game's AddElement call was left unchanged: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "AddElement")]
        [HarmonyPostfix]
        private static void RecipeFlowGUI_AddElement_Postfix(
            RecipeFlowGUI __instance,
            OrderDefinitionNode _data,
            VoidGeneric<RecipeFlowGUI.ElementToken> _expirationCallback,
            ref RecipeFlowGUI.ElementToken __result)
        {
            try
            {
                if (__instance == null || _data == null)
                {
                    return;
                }

                RecipeFlowGUI.RecipeWidgetData widgetData = __instance.GetData(__result);
                if (widgetData == null || widgetData.m_widget == null)
                {
                    return;
                }

                bool isReferenceTicket = IsMenuManagerReferenceTicketAdd(_expirationCallback);
                if (!isReferenceTicket && RecipeFlowOccupiedTablesField != null)
                {
                    try
                    {
                        bool[] occupiedTables = RecipeFlowOccupiedTablesField.GetValue(__instance) as bool[];
                        int tableCount = occupiedTables != null ? occupiedTables.Length : 0;
                        int tableIndex = widgetData.m_widget.GetTableNumber();
                        if (!TicketCapacityPolicy.IsValidTableIndex(tableIndex, tableCount) && !invalidRealTableWarningLogged)
                        {
                            invalidRealTableWarningLogged = true;
                            _MODEntry.LogWarning("[ServedDishTracker] A real order received an invalid RecipeFlowGUI table index " + tableIndex + " for capacity " + tableCount + ". Removal protection remains active for this round.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTrackingHookFailure("validating an added ticket's table assignment", ex);
                    }
                }

                if (!isReferenceTicket
                    && (enabled == null || !enabled.Value || NoMenuMode.IsActiveForRound))
                {
                    return;
                }

                RegisterTicketWidget(
                    widgetData.m_widget,
                    _data.m_uID,
                    widgetData.m_order,
                    ResolveTeamForRecipeFlow(__instance));
                if (!isReferenceTicket && ReferenceTicketStates.Count > 0)
                {
                    ReorderActiveTicketWidgets(__instance);
                }
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("updating ticket presentation after AddElement", ex);
            }
        }

        private static bool IsMenuManagerReferenceTicketAdd(VoidGeneric<RecipeFlowGUI.ElementToken> expirationCallback)
        {
            return ReferenceEquals(expirationCallback, ReferenceTicketExpiredCallback);
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "ReleaseTable")]
        [HarmonyPrefix]
        private static bool RecipeFlowGUI_ReleaseTable_Prefix(RecipeFlowGUI __instance, int _tableId)
        {
            if (_tableId < 0)
            {
                if (!invalidTableReleaseWarningLogged)
                {
                    invalidTableReleaseWarningLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Ignored a negative RecipeFlowGUI table release (index " + _tableId + ") so the served ticket could finish removing.");
                }

                return false;
            }

            if (__instance == null || RecipeFlowOccupiedTablesField == null)
            {
                return true;
            }

            try
            {
                bool[] occupiedTables = RecipeFlowOccupiedTablesField.GetValue(__instance) as bool[];
                int tableCount = occupiedTables != null ? occupiedTables.Length : 0;
                if (TicketCapacityPolicy.IsValidTableIndex(_tableId, tableCount))
                {
                    return true;
                }

                if (!invalidTableReleaseWarningLogged)
                {
                    invalidTableReleaseWarningLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Ignored an invalid RecipeFlowGUI table release (index " + _tableId + ", capacity " + tableCount + ") so the served ticket could finish removing.");
                }

                return false;
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("validating a RecipeFlowGUI table release", ex);
                // The base implementation indexes the table array without validating the
                // value. If our validation contract itself fails, skipping this bookkeeping
                // operation is safer than allowing removal to abort with an index exception.
                return false;
            }
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "RemoveElement")]
        [HarmonyPrefix]
        private static void RecipeFlowGUI_RemoveElement_Prefix(RecipeFlowGUI __instance, RecipeFlowGUI.ElementToken _token)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                RecipeFlowGUI.RecipeWidgetData widgetData = __instance.GetData(_token);
                if (widgetData == null || widgetData.m_widget == null)
                {
                    return;
                }

                TicketWidgetState state;
                if (TicketWidgetsByInstanceId.TryGetValue(widgetData.m_widget.GetInstanceID(), out state)
                    && state != null
                    && state.IsReferenceTicket
                    && state.IsDyingReferenceTicket)
                {
                    return;
                }

                UnregisterTicketWidget(widgetData.m_widget);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("cleaning ticket state before RemoveElement", ex);
            }
        }

        [HarmonyPatch(typeof(RecipeWidgetUIController), "OnDestroy")]
        [HarmonyPostfix]
        private static void RecipeWidgetUIController_OnDestroy_Postfix(RecipeWidgetUIController __instance)
        {
            try
            {
                ForgetTicketWidget(__instance);
            }
            catch (Exception ex)
            {
                LogTrackingHookFailure("forgetting a destroyed ticket widget", ex);
            }
        }

        private static void SetReferenceTicketWidgetVisible(RecipeWidgetUIController widget, bool visible)
        {
            if (widget == null)
            {
                return;
            }

            CanvasGroup canvasGroup = widget.GetComponent<CanvasGroup>();
            bool createdByMod = false;
            if (canvasGroup == null)
            {
                canvasGroup = widget.gameObject.AddComponent<CanvasGroup>();
                createdByMod = true;
                canvasGroup.blocksRaycasts = false;
            }

            TicketWidgetState state;
            float targetAlpha = visible ? 1f : 0f;
            if (TicketWidgetsByInstanceId.TryGetValue(widget.GetInstanceID(), out state) && state != null)
            {
                state.CanvasGroup = canvasGroup;
                state.CanvasGroupResolved = true;
                if (createdByMod)
                {
                    state.CanvasGroupCreatedByMod = true;
                    state.OriginalOpacity = 1f;
                    state.OriginalInteractable = true;
                    state.OriginalBlocksRaycasts = true;
                }
                if (visible)
                {
                    if (state.HasAppliedTint)
                    {
                        targetAlpha = Mathf.Clamp01(state.AppliedOpacity);
                    }
                    else if (state.IsReferenceTicket)
                    {
                        Color referenceDisplayTint = GetReferenceTicketDisplayTintColor();
                        Color referenceTopTint = GetReferenceTicketTopTintColor(referenceDisplayTint);
                        targetAlpha = GetTicketOpacity(referenceDisplayTint, referenceTopTint);
                    }
                    else if (state.OriginalOpacity > 0f)
                    {
                        targetAlpha = Mathf.Clamp01(state.OriginalOpacity);
                    }
                }
            }

            canvasGroup.alpha = targetAlpha;
        }

        private static void RefreshKnownScenes(bool forceRefresh)
        {
            if (!forceRefresh && Time.frameCount < nextSceneRefreshFrame)
            {
                return;
            }

            if (forceRefresh)
            {
                nextDIYSceneRefreshFrame = 0;
            }

            bool inActiveRound = IsInActiveRound();
            List<SceneDirectoryData.SceneDirectoryEntry> entries;
            if (inActiveRound)
            {
                AvailableSceneEntriesBuffer.Clear();
                entries = AvailableSceneEntriesBuffer;
            }
            else
            {
                entries = GetAvailableSceneEntries();
            }
            int refreshInterval = inActiveRound
                ? (settingsWindowVisible ? SceneRefreshIntervalInRoundWithConfigOpen : SceneRefreshIntervalInRound)
                : SceneRefreshIntervalOutOfRound;
            nextSceneRefreshFrame = Time.frameCount + refreshInterval;

            KnownScenes.Clear();
            HashSet<string> seenScenes = KnownSceneNamesBuffer;
            seenScenes.Clear();

            SceneInfo currentScene;
            if (IsInActiveRound() && TryGetCurrentSceneInfo(out currentScene))
            {
                AddScene(KnownScenes, seenScenes, currentScene);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                AddSceneFromEntry(KnownScenes, seenScenes, entries[i]);
            }

            AddDIYScenes(KnownScenes, seenScenes);
            AddCachedScenes(KnownScenes, seenScenes);
            KnownScenes.Sort(delegate(SceneInfo a, SceneInfo b)
            {
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            unchecked
            {
                knownScenesRevision++;
            }

            UpdateIdScanStatus(KnownScenes);
        }

        private static void SyncTrackingConfigEntries()
        {
            ResolveSceneSelectorSelection();
        }

        private static SceneInfo ResolveSceneSelectorSelection()
        {
            List<SceneInfo> selectableScenes = GetSelectableScenes();
            RebuildSceneSelectorMaps(selectableScenes);

            SceneInfo activeScene = null;
            bool lockedToRound = IsInActiveRound() && TryGetCurrentSceneInfo(out activeScene) && activeScene != null;
            string resolvedConfiguredSceneName;
            bool configuredSceneAvailable = TryResolveSceneNameFromSelectorValue(
                selectableScenes,
                configuredSceneName,
                out resolvedConfiguredSceneName);
            string fallbackSceneName = selectableScenes.Count > 0 ? selectableScenes[0].SceneName : string.Empty;
            string effectiveSceneName = SceneSelectionPolicy.ResolveEffectiveSceneName(
                lockedToRound,
                lockedToRound ? activeScene.SceneName : string.Empty,
                resolvedConfiguredSceneName,
                configuredSceneAvailable,
                fallbackSceneName);

            configuredSceneName = SceneSelectionPolicy.ResolveConfiguredSceneName(
                lockedToRound,
                configuredSceneName,
                configuredSceneAvailable,
                effectiveSceneName);

            for (int i = 0; i < selectableScenes.Count; i++)
            {
                SceneInfo scene = selectableScenes[i];
                if (!string.Equals(scene.SceneName, effectiveSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnsureDIYSceneHydrated(scene, false);
                return scene;
            }

            return null;
        }

        private static List<SceneInfo> GetSelectableScenes()
        {
            SceneInfo currentScene = null;
            bool lockedToRound = IsInActiveRound() && TryGetCurrentSceneInfo(out currentScene);
            string currentSceneName = lockedToRound && currentScene != null ? currentScene.SceneName : string.Empty;
            if (cachedSelectableLockedToRound == lockedToRound
                && cachedSelectableKnownScenesRevision == knownScenesRevision
                && string.Equals(cachedSelectableCurrentSceneName, currentSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return SelectableScenesBuffer;
            }

            SelectableScenesBuffer.Clear();
            if (lockedToRound)
            {
                SelectableScenesBuffer.Add(currentScene);
            }
            else
            {
                SelectableScenesBuffer.AddRange(KnownScenes);
            }

            cachedSelectableLockedToRound = lockedToRound;
            cachedSelectableKnownScenesRevision = knownScenesRevision;
            cachedSelectableCurrentSceneName = currentSceneName;
            unchecked
            {
                selectableScenesRevision++;
            }

            return SelectableScenesBuffer;
        }

        private static void RebuildSceneSelectorMaps(List<SceneInfo> selectableScenes)
        {
            int maxLength = GetMaxSceneSelectorDisplayLength();
            if (cachedSceneSelectorMapRevision == selectableScenesRevision
                && cachedSceneSelectorMaxLength == maxLength)
            {
                return;
            }

            OrderedSceneSelectorValues.Clear();
            SceneSelectorValuesByScene.Clear();
            SceneNamesBySelectorValue.Clear();

            for (int i = 0; i < selectableScenes.Count; i++)
            {
                SceneInfo scene = selectableScenes[i];
                string selectorValue = BuildUniqueSceneSelectorValue(scene);
                OrderedSceneSelectorValues.Add(selectorValue);
                SceneSelectorValuesByScene[scene.SceneName] = selectorValue;
                SceneNamesBySelectorValue[selectorValue] = scene.SceneName;
            }

            cachedSceneSelectorMapRevision = selectableScenesRevision;
            cachedSceneSelectorMaxLength = maxLength;
        }

        private static bool TryResolveSceneNameFromSelectorValue(List<SceneInfo> selectableScenes, string selectorValue, out string sceneName)
        {
            sceneName = string.Empty;
            if (selectableScenes == null || selectableScenes.Count == 0 || string.IsNullOrEmpty(selectorValue))
            {
                return false;
            }

            string mappedSceneName;
            if (SceneNamesBySelectorValue.TryGetValue(selectorValue, out mappedSceneName)
                && selectableScenes.Any(scene => string.Equals(scene.SceneName, mappedSceneName, StringComparison.OrdinalIgnoreCase)))
            {
                sceneName = mappedSceneName;
                return true;
            }

            if (selectableScenes.Any(scene => string.Equals(scene.SceneName, selectorValue, StringComparison.OrdinalIgnoreCase)))
            {
                sceneName = selectorValue;
                return true;
            }

            return false;
        }

        private static string BuildSceneSelectorValue(SceneInfo scene)
        {
            if (scene == null)
            {
                return NoSceneSelectorValue;
            }

            string displayName = string.IsNullOrEmpty(scene.DisplayName) ? scene.SceneName : scene.DisplayName;
            if (string.IsNullOrEmpty(displayName))
            {
                return scene.SceneName;
            }

            string combined = displayName.IndexOf(scene.SceneName, StringComparison.OrdinalIgnoreCase) >= 0
                ? displayName
                : displayName + " [" + scene.SceneName + "]";
            return TruncateSceneSelectorValue(combined, scene.SceneName);
        }

        private static string GetSceneSelectorValue(SceneInfo scene)
        {
            string selectorValue;
            if (scene != null && SceneSelectorValuesByScene.TryGetValue(scene.SceneName, out selectorValue))
            {
                return selectorValue;
            }

            return scene == null ? NoSceneSelectorValue : BuildSceneSelectorValue(scene);
        }

        private static string BuildUniqueSceneSelectorValue(SceneInfo scene)
        {
            string baseValue = BuildSceneSelectorValue(scene);
            if (!SceneNamesBySelectorValue.ContainsKey(baseValue))
            {
                return baseValue;
            }

            int index = 2;
            while (true)
            {
                string suffix = " #" + index;
                string candidate = AppendSuffixWithLengthLimit(baseValue, suffix, GetMaxSceneSelectorDisplayLength());
                if (!SceneNamesBySelectorValue.ContainsKey(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }

        private static string GetRecipeConfigName(RecipeInfo recipe)
        {
            if (!string.IsNullOrEmpty(recipe.ChineseName) && !string.IsNullOrEmpty(recipe.EnglishName))
            {
                return recipe.ChineseName + " / " + recipe.EnglishName;
            }
            if (!string.IsNullOrEmpty(recipe.ChineseName))
            {
                return recipe.ChineseName;
            }
            if (!string.IsNullOrEmpty(recipe.EnglishName))
            {
                return recipe.EnglishName;
            }
            return recipe.InternalName;
        }

        private static void DrawTrackingPanel(ConfigEntryBase entryObject)
        {
            SceneInfo scene = ResolveSceneSelectorSelection();
            bool isLockedToCurrentScene = IsLockedToCurrentScene();

            GUILayout.BeginVertical();

            if (scene == null)
            {
                GUILayout.Label(Ui("当前还没有可用关卡数据。请进入世界地图或街机大厅；DIY 关卡元数据加载完成后也会自动出现。", "No scene data is available yet. Visit the world map or arcade lobby; DIY scenes appear after their metadata finishes loading."));
                if (GUILayout.Button(Ui("刷新关卡列表", "Refresh Scenes")))
                {
                    RefreshKnownScenes(true);
                    ResolveSceneSelectorSelection();
                }
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label(isLockedToCurrentScene
                ? Ui("当前在关卡内，关卡选择已自动锁定为本局关卡，但你仍然可以修改本关的追踪菜品。", "The selector is locked to the current round, but you can still change which dishes this scene tracks.")
                : Ui("请先在上方“选择关卡”下拉框中切换关卡，再勾选要追踪的菜品。", "Choose a scene above, then tick the dishes you want to track."));
            GUILayout.Label(Ui("橙名=在单未备，绿名=已备。", "Orange = on menu, green = prepared."));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("已追踪 ", "Tracked ") + GetTrackedCount(scene) + "/" + scene.OrderedRecipes.Count, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Ui("全选", "All"), GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                SetAllTracked(scene, true);
            }
            if (GUILayout.Button(Ui("清空", "Clear"), GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                SetAllTracked(scene, false);
            }
            if (GUILayout.Button(Ui("刷新", "Refresh"), GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                RefreshKnownScenes(true);
                scene = ResolveSceneSelectorSelection();
                EnsureDIYSceneHydrated(scene, true);
            }
            GUILayout.EndHorizontal();

            if (scene.OrderedRecipes.Count == 0)
            {
                string hydrationStatus = !string.IsNullOrEmpty(scene.DIYHydrationError)
                    ? scene.DIYHydrationError
                    : Ui("DIY 菜谱元数据仍在加载。", "DIY recipe metadata is still loading.");
                GUILayout.Label(Ui("无法预读取这个 DIY 关卡的菜谱：", "Could not preload this DIY scene's recipes: ") + hydrationStatus);
                if (GUILayout.Button(Ui("重试读取 DIY 菜谱", "Retry DIY Recipe Load")))
                {
                    EnsureDIYSceneHydrated(scene, true);
                }
                GUILayout.EndVertical();
                return;
            }

            DrawSecondarySelectionGroupToggles(scene);
            DrawCategorySelectionToggles(scene);
            GUILayout.Space(2f);
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                bool tracked = IsTracked(scene, recipe.Id);
                string label = (i + 1).ToString("00")
                    + ". "
                    + GetRecipeSelectionLabel(recipe)
                    + " ["
                    + recipe.Id
                    + "]";
                bool nextTracked = GUILayout.Toggle(tracked, label);
                if (nextTracked != tracked)
                {
                    ApplyTrackedState(scene.SceneName, recipe.Id, nextTracked, true);
                }
            }

            GUILayout.EndVertical();
        }

        private static void DrawCategorySelectionToggles(SceneInfo scene)
        {
            List<RecipeSelectionGroup> groups = BuildCategorySelectionGroups(scene);
            DrawRecipeSelectionGroupToggles(
                scene,
                Ui("按类别批量勾选：", "Track by category:"),
                groups,
                true);
        }

        private static void DrawSecondarySelectionGroupToggles(SceneInfo scene)
        {
            SceneRecipeSelectionGroupSet groupSet = BuildSecondarySelectionGroupSet(scene);
            if (groupSet == null)
            {
                return;
            }

            DrawRecipeSelectionGroupToggles(
                scene,
                UseChinese() ? groupSet.ChineseHeading : groupSet.EnglishHeading,
                groupSet.Groups,
                false);
        }

        private static void DrawRecipeSelectionGroupToggles(
            SceneInfo scene,
            string heading,
            IList<RecipeSelectionGroup> groups,
            bool includeAllPrefix)
        {
            if (scene == null || groups == null || groups.Count == 0)
            {
                return;
            }

            GUILayout.Space(4f);
            GUILayout.Label(heading);
            for (int i = 0; i < groups.Count;)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 2 && i < groups.Count; column++, i++)
                {
                    RecipeSelectionGroup group = groups[i];
                    bool allTracked = AreAllGroupRecipesTracked(scene, group);
                    string groupName = UseChinese() ? group.ChineseName : group.EnglishName;
                    string label = includeAllPrefix ? Ui("全部", "All ") + groupName : groupName;
                    bool nextTracked = GUILayout.Toggle(allTracked, label, GUILayout.MinWidth(160f), GUILayout.ExpandWidth(true));
                    if (nextTracked != allTracked)
                    {
                        SetTrackedForGroup(scene, group, nextTracked);
                    }
                }
                GUILayout.EndHorizontal();
            }
        }

        private static bool IsLockedToCurrentScene()
        {
            SceneInfo currentScene;
            return IsInActiveRound() && TryGetCurrentSceneInfo(out currentScene) && currentScene != null;
        }

        private static int GetTrackedCount(SceneInfo scene)
        {
            if (scene == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                if (IsTracked(scene, scene.OrderedRecipes[i].Id))
                {
                    count++;
                }
            }

            return count;
        }

        private static SceneRecipeSelectionGroupSet BuildSecondarySelectionGroupSet(SceneInfo scene)
        {
            if (ReferenceEquals(cachedSecondarySelectionScene, scene)
                && scene != null
                && string.Equals(cachedSecondarySelectionSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                && cachedSecondarySelectionCatalogRevision == scene.CatalogRevision)
            {
                return cachedSecondarySelectionGroupSet;
            }

            cachedSecondarySelectionScene = scene;
            cachedSecondarySelectionSceneName = scene != null ? scene.SceneName : string.Empty;
            cachedSecondarySelectionCatalogRevision = scene != null ? scene.CatalogRevision : -1;
            cachedSecondarySelectionGroupSet = null;
            if (scene == null || scene.DIYRecipeIds.Count == 0 || scene.RecipesById.Count == 0)
            {
                return null;
            }

            string failureReason;
            SceneRecipeGroupResolutionStatus status = SceneRecipeGroupCatalog.Resolve(
                scene.SceneName,
                scene.DIYRecipeIds,
                scene.RecipesById.Keys,
                out cachedSecondarySelectionGroupSet,
                out failureReason);
            if (status == SceneRecipeGroupResolutionStatus.Resolved)
            {
                return cachedSecondarySelectionGroupSet;
            }

            if (status == SceneRecipeGroupResolutionStatus.Incomplete
                && SecondarySelectionWarningScenes.Add(scene.SceneName))
            {
                _MODEntry.LogWarning(
                    "[ServedDishTracker] Secondary recipe groups are unavailable for "
                    + scene.SceneName
                    + ": "
                    + failureReason
                    + ".");
            }

            cachedSecondarySelectionGroupSet = null;
            return null;
        }

        private static List<RecipeSelectionGroup> BuildCategorySelectionGroups(SceneInfo scene)
        {
            bool chinese = UseChinese();
            if (ReferenceEquals(cachedCategorySelectionScene, scene)
                && scene != null
                && string.Equals(cachedCategorySelectionSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                && cachedCategorySelectionCatalogRevision == scene.CatalogRevision
                && cachedCategorySelectionTierRevision == categoryTierRevision
                && cachedCategorySelectionChinese == chinese)
            {
                return CategorySelectionGroupsBuffer;
            }

            CategorySelectionGroupsBuffer.Clear();
            cachedCategorySelectionScene = scene;
            cachedCategorySelectionSceneName = scene != null ? scene.SceneName : string.Empty;
            cachedCategorySelectionCatalogRevision = scene != null ? scene.CatalogRevision : -1;
            cachedCategorySelectionTierRevision = categoryTierRevision;
            cachedCategorySelectionChinese = chinese;
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return CategorySelectionGroupsBuffer;
            }

            Dictionary<string, RecipeSelectionGroup> groupsByKey = new Dictionary<string, RecipeSelectionGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null)
                {
                    continue;
                }

                RecipeCategoryAssignment category = recipe.Category
                    ?? RecipeCategoryCatalog.ResolveKnownOrFallback(recipe.InternalName);
                string categoryKey = string.IsNullOrEmpty(category.Key) ? "other" : category.Key;
                RecipeSelectionGroup group;
                if (!groupsByKey.TryGetValue(categoryKey, out group))
                {
                    group = new RecipeSelectionGroup();
                    group.Key = categoryKey;
                    group.EnglishName = string.IsNullOrEmpty(category.EnglishName) ? "Other" : category.EnglishName;
                    group.ChineseName = string.IsNullOrEmpty(category.ChineseName) ? "其他" : category.ChineseName;
                    group.SortTier = recipe.CategoryTier;
                    groupsByKey.Add(categoryKey, group);
                    CategorySelectionGroupsBuffer.Add(group);
                }
                else
                {
                    group.SortTier = Math.Min(group.SortTier, recipe.CategoryTier);
                }

                group.RecipeIds.Add(recipe.Id);
            }

            CategorySelectionGroupsBuffer.Sort(delegate(RecipeSelectionGroup a, RecipeSelectionGroup b)
            {
                int tierCompare = a.SortTier.CompareTo(b.SortTier);
                if (tierCompare != 0)
                {
                    return tierCompare;
                }

                string leftName = chinese ? a.ChineseName : a.EnglishName;
                string rightName = chinese ? b.ChineseName : b.EnglishName;
                int labelCompare = string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
                return labelCompare != 0
                    ? labelCompare
                    : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return CategorySelectionGroupsBuffer;
        }

        private static bool AreAllGroupRecipesTracked(SceneInfo scene, RecipeSelectionGroup group)
        {
            if (scene == null || group == null || group.RecipeIds.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < group.RecipeIds.Count; i++)
            {
                if (!IsTracked(scene, group.RecipeIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void SetTrackedForGroup(SceneInfo scene, RecipeSelectionGroup group, bool shouldTrack)
        {
            if (scene == null || group == null)
            {
                return;
            }

            bool changed = false;
            for (int i = 0; i < group.RecipeIds.Count; i++)
            {
                changed |= ApplyTrackedStateCore(scene.SceneName, group.RecipeIds[i], shouldTrack);
            }

            if (changed)
            {
                SaveSelections();
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                InvalidateTicketWidgets();
            }
        }

        private static void SetAllTracked(SceneInfo scene, bool shouldTrack)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName))
            {
                return;
            }

            bool changed;
            if (shouldTrack)
            {
                changed = TrackedIdsByScene.Remove(scene.SceneName);
            }
            else
            {
                HashSet<int> trackedIds;
                if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds) || trackedIds == null)
                {
                    trackedIds = new HashSet<int>();
                    TrackedIdsByScene[scene.SceneName] = trackedIds;
                    changed = scene.OrderedRecipes.Count > 0;
                }
                else
                {
                    changed = trackedIds.Count > 0;
                    trackedIds.Clear();
                }
            }

            if (!changed)
            {
                return;
            }

            SaveSelections();
            InvalidatePreparedCandidates(true);
            InvalidateOverlay();
            InvalidateTicketWidgets();
        }

        private static void ApplyTrackedState(string sceneName, int recipeId, bool shouldTrack, bool saveSelection)
        {
            if (!ApplyTrackedStateCore(sceneName, recipeId, shouldTrack))
            {
                return;
            }

            if (saveSelection)
            {
                SaveSelections();
            }

            InvalidatePreparedCandidates(true);
            InvalidateOverlay();
            InvalidateTicketWidgets();
        }

        private static bool ApplyTrackedStateCore(string sceneName, int recipeId, bool shouldTrack)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            HashSet<int> trackedIds;
            bool hasExplicitSelection = TrackedIdsByScene.TryGetValue(sceneName, out trackedIds) && trackedIds != null;

            if (shouldTrack)
            {
                if (!hasExplicitSelection)
                {
                    return false;
                }

                if (!trackedIds.Add(recipeId))
                {
                    return false;
                }

                SceneInfo sceneInfo;
                if (TryGetSceneInfoByName(sceneName, out sceneInfo) && sceneInfo != null && sceneInfo.OrderedRecipes.Count > 0)
                {
                    if (trackedIds.Count >= sceneInfo.OrderedRecipes.Count)
                    {
                        TrackedIdsByScene.Remove(sceneName);
                    }
                }

                return true;
            }

            if (!hasExplicitSelection)
            {
                SceneInfo sceneInfo;
                if (!TryGetSceneInfoByName(sceneName, out sceneInfo) || sceneInfo == null || sceneInfo.OrderedRecipes.Count == 0)
                {
                    return false;
                }

                trackedIds = new HashSet<int>(sceneInfo.OrderedRecipes.Select(recipe => recipe.Id));
                TrackedIdsByScene[sceneName] = trackedIds;
            }

            return trackedIds.Remove(recipeId);
        }

        private static bool TryGetSceneInfoByName(string sceneName, out SceneInfo sceneInfo)
        {
            sceneInfo = null;
            if (string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            if (SceneCache.TryGetValue(sceneName, out sceneInfo) && sceneInfo != null)
            {
                return true;
            }

            for (int i = 0; i < KnownScenes.Count; i++)
            {
                SceneInfo knownScene = KnownScenes[i];
                if (knownScene != null && string.Equals(knownScene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    sceneInfo = knownScene;
                    return true;
                }
            }

            SceneInfo currentScene;
            if (TryGetCurrentSceneInfo(out currentScene)
                && currentScene != null
                && string.Equals(currentScene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
            {
                sceneInfo = currentScene;
                return true;
            }

            return false;
        }

        private static bool HasAnyTrackedRecipes(SceneInfo scene)
        {
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return false;
            }

            HashSet<int> trackedIds;
            if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds) || trackedIds == null)
            {
                return true;
            }

            return trackedIds.Count > 0;
        }

        private static void RegisterTicketWidget(
            RecipeWidgetUIController widget,
            int recipeId,
            int order,
            TeamID teamId)
        {
            if (widget == null)
            {
                return;
            }

            int instanceId = widget.GetInstanceID();
            TicketWidgetState existingState;
            if (TicketWidgetsByInstanceId.TryGetValue(instanceId, out existingState) && existingState != null)
            {
                existingState.RecipeId = recipeId;
                existingState.Order = order;
                existingState.TeamId = teamId;
                existingState.IsDyingReferenceTicket = false;
                ticketWidgetsDirty = true;
                return;
            }

            RecipeWidgetTile.DisplayConfiguration displayConfig = RecipeWidgetDisplayConfigField != null
                ? RecipeWidgetDisplayConfigField.GetValue(widget) as RecipeWidgetTile.DisplayConfiguration
                : null;
            TopRecipeWidgetTile.TopDisplayConfiguration topDisplayConfig = RecipeWidgetTopDisplayConfigField != null
                ? RecipeWidgetTopDisplayConfigField.GetValue(widget) as TopRecipeWidgetTile.TopDisplayConfiguration
                : null;
            if (displayConfig == null || topDisplayConfig == null)
            {
                return;
            }

            TicketWidgetState state = new TicketWidgetState();
            state.InstanceId = instanceId;
            state.RecipeId = recipeId;
            state.Order = order;
            state.TeamId = teamId;
            state.Widget = widget;
            state.DisplayConfig = displayConfig;
            state.TopDisplayConfig = topDisplayConfig;
            state.OriginalDisplayTint = displayConfig.m_tint;
            state.OriginalTopTint = topDisplayConfig.m_tint;
            state.CanvasGroup = widget.GetComponent<CanvasGroup>();
            state.CanvasGroupResolved = true;
            state.OriginalOpacity = state.CanvasGroup != null ? Mathf.Clamp01(state.CanvasGroup.alpha) : 1f;
            state.OriginalInteractable = state.CanvasGroup == null || state.CanvasGroup.interactable;
            state.OriginalBlocksRaycasts = state.CanvasGroup == null || state.CanvasGroup.blocksRaycasts;
            state.CachedImages = null;
            state.AppliedDisplayTint = state.OriginalDisplayTint;
            state.AppliedTopTint = state.OriginalTopTint;
            state.AppliedOpacity = state.OriginalOpacity;
            state.HasAppliedTint = false;
            TicketWidgetsByInstanceId[instanceId] = state;
            InvalidateTicketWidgets();
        }

        private static void ForgetTicketWidget(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return;
            }

            if (TicketWidgetsByInstanceId.Remove(widget.GetInstanceID()))
            {
                InvalidateTicketWidgets();
            }
        }

        private static void UnregisterTicketWidget(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return;
            }

            int instanceId = widget.GetInstanceID();
            TicketWidgetState state;
            if (!TicketWidgetsByInstanceId.TryGetValue(instanceId, out state) || state == null)
            {
                return;
            }

            RestoreTicketWidgetTint(state);
            TicketWidgetsByInstanceId.Remove(instanceId);
            InvalidateTicketWidgets();
        }

        private static void RestoreTicketWidgetTint(TicketWidgetState state)
        {
            if (state == null)
            {
                return;
            }

            ApplyTicketWidgetVisuals(state, state.OriginalDisplayTint, state.OriginalTopTint, state.OriginalOpacity);
            if (state.CanvasGroup != null)
            {
                state.CanvasGroup.alpha = state.OriginalOpacity;
                state.CanvasGroup.interactable = state.OriginalInteractable;
                state.CanvasGroup.blocksRaycasts = state.OriginalBlocksRaycasts;
                if (state.CanvasGroupCreatedByMod)
                {
                    UnityEngine.Object.Destroy(state.CanvasGroup);
                    state.CanvasGroup = null;
                    state.CanvasGroupCreatedByMod = false;
                    state.CanvasGroupResolved = false;
                }
            }
        }

        private static bool ApplyTicketWidgetTint(TicketWidgetState state, Color displayTint, Color topTint)
        {
            return ApplyTicketWidgetVisuals(
                state,
                SetAlpha(displayTint, 1f),
                SetAlpha(topTint, 1f),
                GetTicketOpacity(displayTint, topTint));
        }

        private static bool ApplyTicketWidgetVisuals(TicketWidgetState state, Color displayTint, Color topTint, float opacity)
        {
            if (state == null)
            {
                return false;
            }

            opacity = Mathf.Clamp01(opacity);

            if (state.DisplayConfig != null)
            {
                state.DisplayConfig.m_tint = displayTint;
            }

            if (state.TopDisplayConfig != null)
            {
                state.TopDisplayConfig.m_tint = topTint;
            }

            if (state.Widget == null)
            {
                state.HasAppliedTint = false;
                return false;
            }

            Image[] images = ResolveTicketWidgetImages(state);
            if (images == null || images.Length == 0)
            {
                state.HasAppliedTint = false;
                return false;
            }

            if (state.HasAppliedTint
                && state.AppliedDisplayTint == displayTint
                && state.AppliedTopTint == topTint
                && Mathf.Approximately(state.AppliedOpacity, opacity))
            {
                return true;
            }

            bool appliedAny = false;
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null)
                {
                    continue;
                }

                Color imageTint = Color.white;
                if (state.TopDisplayConfig != null && image.sprite == state.TopDisplayConfig.m_background)
                {
                    imageTint = topTint;
                }
                else if (state.DisplayConfig != null && image.sprite == state.DisplayConfig.m_background)
                {
                    imageTint = displayTint;
                }
                else if (state.TopDisplayConfig != null
                    && (image.sprite == state.TopDisplayConfig.lowTipSprite || image.sprite == state.TopDisplayConfig.highTipSprite))
                {
                    imageTint = state.TopDisplayConfig.notchColor;
                }
                else
                {
                    continue;
                }

                image.color = imageTint;
                appliedAny = true;
            }

            if (!ApplyTicketWidgetOpacity(state, opacity))
            {
                state.HasAppliedTint = false;
                return false;
            }

            if (!appliedAny)
            {
                state.HasAppliedTint = false;
                return false;
            }

            state.AppliedDisplayTint = displayTint;
            state.AppliedTopTint = topTint;
            state.AppliedOpacity = opacity;
            state.HasAppliedTint = true;
            return true;
        }

        private static bool ApplyTicketWidgetOpacity(TicketWidgetState state, float opacity)
        {
            if (state == null || state.Widget == null)
            {
                return false;
            }

            CanvasGroup canvasGroup = ResolveTicketCanvasGroup(state, opacity);
            if (canvasGroup == null)
            {
                state.AppliedOpacity = 1f;
                return opacity >= 0.999f;
            }

            canvasGroup.alpha = opacity;
            state.AppliedOpacity = opacity;
            return true;
        }

        private static Image[] ResolveTicketWidgetImages(TicketWidgetState state)
        {
            if (state == null || state.Widget == null)
            {
                return null;
            }

            Image[] images = state.CachedImages;
            if (HasUsableTicketImages(images))
            {
                return images;
            }

            images = state.Widget.gameObject.RequestComponentsRecursive<Image>();
            if (!HasUsableTicketImages(images))
            {
                try
                {
                    state.Widget.RefreshSubElements();
                }
                catch
                {
                }

                images = state.Widget.gameObject.RequestComponentsRecursive<Image>();
            }

            state.CachedImages = images;
            return images;
        }

        private static CanvasGroup ResolveTicketCanvasGroup(TicketWidgetState state, float opacity)
        {
            if (state == null || state.Widget == null)
            {
                return null;
            }

            CanvasGroup canvasGroup = state.CanvasGroup;
            if (canvasGroup == null && !state.CanvasGroupResolved)
            {
                canvasGroup = state.Widget.GetComponent<CanvasGroup>();
                state.CanvasGroup = canvasGroup;
                state.CanvasGroupResolved = true;
            }

            if (canvasGroup == null && opacity < 0.999f)
            {
                canvasGroup = state.Widget.gameObject.AddComponent<CanvasGroup>();
                state.OriginalOpacity = canvasGroup.alpha;
                state.OriginalInteractable = canvasGroup.interactable;
                state.OriginalBlocksRaycasts = canvasGroup.blocksRaycasts;
                state.CanvasGroupCreatedByMod = true;
                canvasGroup.alpha = 1f;
                canvasGroup.blocksRaycasts = false;
                state.CanvasGroup = canvasGroup;
                state.CanvasGroupResolved = true;
            }

            return canvasGroup;
        }

        private static TeamID ResolveTeamForRecipeFlow(RecipeFlowGUI flow)
        {
            if (flow == null || ClientOrderControllerGuiField == null)
            {
                return TeamID.One;
            }

            ClientKitchenFlowControllerBase flowController = GetKitchenFlowController();
            if (flowController == null)
            {
                return TeamID.One;
            }

            for (int i = 0; i < SupportedTeamIds.Length; i++)
            {
                try
                {
                    ClientTeamMonitor monitor = flowController.GetMonitorForTeam(SupportedTeamIds[i]);
                    RecipeFlowGUI candidate = monitor != null && monitor.OrdersController != null
                        ? ClientOrderControllerGuiField.GetValue(monitor.OrdersController) as RecipeFlowGUI
                        : null;
                    if (candidate == flow)
                    {
                        return SupportedTeamIds[i];
                    }
                }
                catch (Exception ex)
                {
                    LogTrackingHookFailure("resolving a ticket's team", ex);
                }
            }

            return TeamID.One;
        }

        private static bool HasUsableTicketImages(Image[] images)
        {
            if (images == null || images.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static Color GetMenuTicketOnMenuTintColor()
        {
            return menuTicketOnMenuTintColor != null ? menuTicketOnMenuTintColor.Value : new Color(1f, 0.76f, 0.34f, 1f);
        }

        private static Color GetMenuTicketPreparedTintColor()
        {
            return menuTicketPreparedTintColor != null ? menuTicketPreparedTintColor.Value : new Color(0.86f, 0.98f, 0.86f, 1f);
        }

        private static int GetReferenceTicketDisplayLimit()
        {
            return Mathf.Clamp(menuReferenceTicketCount != null ? menuReferenceTicketCount.Value : DefaultReferenceTicketDisplayCount, 0, MaxReferenceTicketDisplayCount);
        }

        private static Color GetReferenceTicketTintColor()
        {
            return menuReferenceTicketTintColor != null ? menuReferenceTicketTintColor.Value : new Color(0.49f, 0.59f, 0.67f, 0.62f);
        }

        private static Color GetReferenceTicketDisplayTintColor()
        {
            Color configuredTint = GetReferenceTicketTintColor();
            float grayscale = (configuredTint.r * 0.299f) + (configuredTint.g * 0.587f) + (configuredTint.b * 0.114f);
            Color mutedTint = Color.Lerp(
                new Color(grayscale, grayscale, grayscale, configuredTint.a),
                configuredTint,
                ReferenceTicketColorSaturation);
            mutedTint.r *= ReferenceTicketColorBrightness;
            mutedTint.g *= ReferenceTicketColorBrightness;
            mutedTint.b *= ReferenceTicketColorBrightness;
            mutedTint.a = Mathf.Clamp01(configuredTint.a * ReferenceTicketOpacityScale);
            return mutedTint;
        }

        private static Color GetReferenceTicketDestroyAnimationColor()
        {
            Color displayTint = GetReferenceTicketDisplayTintColor();
            Color accent = Color.Lerp(Color.white, new Color(displayTint.r, displayTint.g, displayTint.b, 1f), ReferenceTicketDestroyAnimationTintStrength);
            accent.a = 1f;
            return accent;
        }

        private static Color GetReferenceTicketTopTintColor(Color displayTint)
        {
            Color topTint = Color.Lerp(displayTint, new Color(0.22f, 0.25f, 0.29f, displayTint.a), 0.28f);
            topTint.r *= 0.97f;
            topTint.g *= 0.97f;
            topTint.b *= 0.98f;
            topTint.a = displayTint.a;
            return topTint;
        }

        private static float GetTicketOpacity(Color displayTint, Color topTint)
        {
            return Mathf.Clamp01(Mathf.Max(displayTint.a, topTint.a));
        }

        private static Color SetAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>
        /// Applies a user-visible real-ticket tint change immediately while leaving
        /// guess-ticket presentation untouched and gameplay refreshes batched.
        /// </summary>
        private static void SynchronizeRealTicketWidgetTints(
            bool shouldTintRealTickets,
            bool reconcileExistingRealTickets)
        {
            lastMenuTicketTintEnabled = shouldTintRealTickets;
            if (!shouldTintRealTickets)
            {
                if (TicketWidgetsByInstanceId.Count > 0)
                {
                    RestoreRealTicketWidgetTints();
                }

                ticketWidgetReconciliationPending = false;
                ticketWidgetReconciliationAttempts = 0;
                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
                return;
            }

            bool inActiveRound = IsInActiveRound();
            if (reconcileExistingRealTickets && inActiveRound)
            {
                ticketWidgetReconciliationAttempts = 0;
                ticketWidgetReconciliationPending = true;
            }
            ticketWidgetsDirty = true;
            nextTicketWidgetRefreshFrame = 0;
            if (inActiveRound)
            {
                RefreshTicketWidgetTints();
            }
        }

        /// <summary>
        /// Rehydrates presentation state for real tickets that predate tracker
        /// activation by resolving each base-game active order through its UI token.
        /// The method reads authoritative controller collections without modifying them.
        /// </summary>
        private static bool TryReconcileActiveTicketWidgets()
        {
            if (ActiveOrdersField == null
                || ActiveOrderRecipeListEntryField == null
                || ActiveOrderUiTokenField == null
                || ClientOrderControllerGuiField == null)
            {
                if (!ticketWidgetReconciliationContractWarningLogged)
                {
                    ticketWidgetReconciliationContractWarningLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Existing order tickets could not be recolored because the active-order UI-token contract is unavailable. Base-game ticket visuals were left unchanged.");
                }

                return true;
            }

            List<TeamFlowContext> flowContexts = GetReferenceTicketFlowContexts();
            if (flowContexts.Count == 0)
            {
                return false;
            }

            bool complete = true;
            for (int i = 0; i < flowContexts.Count; i++)
            {
                TeamFlowContext context = flowContexts[i];
                if (context == null || context.OrderController == null || context.Flow == null)
                {
                    complete = false;
                    continue;
                }

                IList activeOrders;
                try
                {
                    activeOrders = ActiveOrdersField.GetValue(context.OrderController) as IList;
                }
                catch (Exception ex)
                {
                    LogTrackingHookFailure("reading active tickets for live recoloring", ex);
                    complete = false;
                    continue;
                }

                if (activeOrders == null)
                {
                    complete = false;
                    continue;
                }

                for (int j = 0; j < activeOrders.Count; j++)
                {
                    try
                    {
                        object activeOrder = activeOrders[j];
                        if (activeOrder == null)
                        {
                            complete = false;
                            continue;
                        }

                        RecipeList.Entry recipeEntry = ActiveOrderRecipeListEntryField.GetValue(activeOrder) as RecipeList.Entry;
                        object tokenValue = ActiveOrderUiTokenField.GetValue(activeOrder);
                        if (recipeEntry == null
                            || recipeEntry.m_order == null
                            || !(tokenValue is RecipeFlowGUI.ElementToken))
                        {
                            complete = false;
                            continue;
                        }

                        RecipeFlowGUI.RecipeWidgetData widgetData = context.Flow.GetData((RecipeFlowGUI.ElementToken)tokenValue);
                        if (widgetData == null || widgetData.m_widget == null)
                        {
                            complete = false;
                            continue;
                        }

                        RegisterTicketWidget(
                            widgetData.m_widget,
                            recipeEntry.m_order.m_uID,
                            widgetData.m_order,
                            context.TeamId);
                        if (!TicketWidgetsByInstanceId.ContainsKey(widgetData.m_widget.GetInstanceID()))
                        {
                            complete = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTrackingHookFailure("reconciling an existing ticket for live recoloring", ex);
                        complete = false;
                    }
                }
            }

            for (int i = 0; i < ReferenceTicketStates.Count; i++)
            {
                ReferenceTicketState referenceState = ReferenceTicketStates[i];
                if (referenceState == null || referenceState.Widget == null || referenceState.Flow == null)
                {
                    continue;
                }

                int referenceOrder = GetReferenceTicketOrder(referenceState);
                RegisterTicketWidget(
                    referenceState.Widget,
                    referenceState.RecipeId,
                    referenceOrder,
                    referenceState.TeamId);
                if (!TicketWidgetsByInstanceId.ContainsKey(referenceState.Widget.GetInstanceID()))
                {
                    complete = false;
                    continue;
                }

                ApplyReferenceTicketPresentation(referenceState, referenceOrder);
            }

            return complete;
        }

        private static void RefreshTicketWidgetTints()
        {
            bool reconciliationNeedsRetry = false;
            if (ticketWidgetReconciliationPending
                || (ticketWidgetReconciliationAttempts == 0
                    && TicketWidgetsByInstanceId.Count == 0
                    && IsMenuTicketTintEnabled()
                    && IsInActiveRound()))
            {
                ticketWidgetReconciliationAttempts++;
                bool reconciliationComplete = TryReconcileActiveTicketWidgets();
                ticketWidgetReconciliationPending = !reconciliationComplete
                    && ticketWidgetReconciliationAttempts < MaxTicketWidgetReconciliationAttempts;
                if (!reconciliationComplete
                    && !ticketWidgetReconciliationPending
                    && !ticketWidgetReconciliationRetryWarningLogged)
                {
                    ticketWidgetReconciliationRetryWarningLogged = true;
                    _MODEntry.LogWarning("[ServedDishTracker] Existing order-ticket recoloring did not become available after bounded retries. Base-game visuals were preserved; newly added tickets can still register normally.");
                }
                reconciliationNeedsRetry = ticketWidgetReconciliationPending;
            }

            if (TicketWidgetsByInstanceId.Count == 0)
            {
                ticketWidgetsDirty = reconciliationNeedsRetry;
                realTicketWidgetTintActive = false;
                nextTicketWidgetRefreshFrame = reconciliationNeedsRetry
                    ? Time.frameCount + TicketWidgetRetryIntervalFrames
                    : 0;
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null)
            {
                ClearTicketWidgetState();
                return;
            }

            if (!HasAnyTrackedRecipes(scene))
            {
                if (realTicketWidgetTintActive)
                {
                    RestoreRealTicketWidgetTints();
                }

                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
                return;
            }

            bool showPrepared = IsPreparedTrackingEnabled();
            TicketWidgetsBuffer.Clear();
            StaleTicketWidgetIdsBuffer.Clear();
            foreach (KeyValuePair<int, TicketWidgetState> pair in TicketWidgetsByInstanceId)
            {
                TicketWidgetState state = pair.Value;
                if (state == null || state.Widget == null)
                {
                    StaleTicketWidgetIdsBuffer.Add(pair.Key);
                    continue;
                }

                TicketWidgetsBuffer.Add(state);
            }

            if (StaleTicketWidgetIdsBuffer.Count > 0)
            {
                for (int i = 0; i < StaleTicketWidgetIdsBuffer.Count; i++)
                {
                    TicketWidgetsByInstanceId.Remove(StaleTicketWidgetIdsBuffer[i]);
                }
            }

            bool tintRealTickets = IsMenuTicketTintEnabled();
            bool needsRetry = false;
            bool appliedRealTicketTint = false;
            Color referenceDisplayTint = GetReferenceTicketDisplayTintColor();
            Color referenceTopTint = GetReferenceTicketTopTintColor(referenceDisplayTint);
            Color onMenuTint = GetMenuTicketOnMenuTintColor();
            Color preparedTint = GetMenuTicketPreparedTintColor();
            for (int i = 0; i < TicketWidgetsBuffer.Count; i++)
            {
                TicketWidgetState state = TicketWidgetsBuffer[i];
                if (state.IsReferenceTicket)
                {
                    if (!ApplyTicketWidgetTint(state, referenceDisplayTint, referenceTopTint))
                    {
                        needsRetry = true;
                    }
                    continue;
                }

                if (!tintRealTickets)
                {
                    continue;
                }

                if (!IsTracked(scene, state.RecipeId))
                {
                    if (!ApplyTicketWidgetTint(state, state.OriginalDisplayTint, state.OriginalTopTint))
                    {
                        needsRetry = true;
                    }
                    continue;
                }

                bool hasPreparedAssignment = showPrepared
                    && GetCount(PreparedCompatibilityCountsByRecipe, state.RecipeId) > 0;

                Color tint = hasPreparedAssignment ? preparedTint : onMenuTint;
                if (!ApplyTicketWidgetTint(state, tint, tint))
                {
                    needsRetry = true;
                }
                else
                {
                    appliedRealTicketTint = true;
                }
            }

            needsRetry |= reconciliationNeedsRetry;
            ticketWidgetsDirty = needsRetry;
            realTicketWidgetTintActive = appliedRealTicketTint;
            nextTicketWidgetRefreshFrame = needsRetry ? Time.frameCount + TicketWidgetRetryIntervalFrames : 0;
        }

    }
}
