using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const float ReferenceTicketColorSaturation = 0.40f;
        private const float ReferenceTicketColorBrightness = 0.88f;
        private const float ReferenceTicketOpacityScale = 0.80f;
        private const float ReferenceTicketDestroyAnimationTintStrength = 0.18f;

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
        [HarmonyPostfix]
        private static void RecipeFlowGUI_AddElement_Postfix(RecipeFlowGUI __instance, OrderDefinitionNode _data, ref RecipeFlowGUI.ElementToken __result)
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

            RegisterTicketWidget(widgetData.m_widget, _data.m_uID, widgetData.m_order);
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "RemoveElement")]
        [HarmonyPrefix]
        private static void RecipeFlowGUI_RemoveElement_Prefix(RecipeFlowGUI __instance, RecipeFlowGUI.ElementToken _token)
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

        [HarmonyPatch(typeof(RecipeWidgetUIController), "OnDestroy")]
        [HarmonyPostfix]
        private static void RecipeWidgetUIController_OnDestroy_Postfix(RecipeWidgetUIController __instance)
        {
            ForgetTicketWidget(__instance);
        }

        private static void SuppressReferenceTicketWidgetAnimator(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return;
            }

            GameObject generatedChildren = GetReferenceTicketGeneratedChildren(widget);
            if (generatedChildren == null)
            {
                return;
            }

            Animator animator = generatedChildren.GetComponent<Animator>();
            if (animator != null)
            {
                animator.enabled = false;
            }

            UI_Move move = generatedChildren.GetComponent<UI_Move>();
            if (move != null)
            {
                move.Offset = Vector2.zero;
                move.UpdateGraphics();
            }
        }

        private static GameObject GetReferenceTicketGeneratedChildren(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return null;
            }

            if (UISubElementContainerContainerField != null)
            {
                GameObject container = UISubElementContainerContainerField.GetValue(widget) as GameObject;
                if (container != null)
                {
                    return container;
                }
            }

            for (int i = widget.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = widget.transform.GetChild(i);
                if (child != null && string.Equals(child.name, "GeneratedChildren", StringComparison.Ordinal))
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private static void SetReferenceTicketWidgetVisible(RecipeWidgetUIController widget, bool visible)
        {
            if (widget == null)
            {
                return;
            }

            CanvasGroup canvasGroup = widget.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = widget.gameObject.AddComponent<CanvasGroup>();
                canvasGroup.blocksRaycasts = false;
            }

            TicketWidgetState state;
            float targetAlpha = visible ? 1f : 0f;
            if (TicketWidgetsByInstanceId.TryGetValue(widget.GetInstanceID(), out state) && state != null)
            {
                state.CanvasGroup = canvasGroup;
                state.CanvasGroupResolved = true;
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

                if (state.OriginalOpacity <= 0f)
                {
                    state.OriginalOpacity = 1f;
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
            List<SceneDirectoryData.SceneDirectoryEntry> entries = inActiveRound
                ? new List<SceneDirectoryData.SceneDirectoryEntry>()
                : GetAvailableSceneEntries();
            int refreshInterval = inActiveRound
                ? (settingsWindowVisible ? SceneRefreshIntervalInRoundWithConfigOpen : SceneRefreshIntervalInRound)
                : SceneRefreshIntervalOutOfRound;
            nextSceneRefreshFrame = Time.frameCount + refreshInterval;

            KnownScenes.Clear();
            HashSet<string> seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            SyncSceneSelectorConfigEntry();
        }

        private static SceneInfo SyncSceneSelectorConfigEntry()
        {
            List<SceneInfo> selectableScenes = GetSelectableScenes();
            RebuildSceneSelectorMaps(selectableScenes);
            string desiredSceneName = ResolveDesiredSceneName(selectableScenes);
            if (!string.Equals(selectedSceneName, desiredSceneName, StringComparison.OrdinalIgnoreCase))
            {
                selectedSceneName = desiredSceneName;
            }

            SceneInfo selectedSceneInfo;
            if (!TryResolveSelectedScene(selectableScenes, out selectedSceneInfo))
            {
                return null;
            }

            EnsureDIYSceneHydrated(selectedSceneInfo, false);
            return selectedSceneInfo;
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

        private static string ResolveDesiredSceneName(List<SceneInfo> selectableScenes)
        {
            SceneInfo currentScene;
            if (TryGetCurrentSceneInfo(out currentScene))
            {
                return currentScene.SceneName;
            }

            string selectedSceneName;
            if (TryResolveSceneNameFromSelectorValue(selectableScenes, ServedDishTracker.selectedSceneName, out selectedSceneName))
            {
                return selectedSceneName;
            }

            if (!string.IsNullOrEmpty(preferredSceneName)
                && selectableScenes.Any(scene => string.Equals(scene.SceneName, preferredSceneName, StringComparison.OrdinalIgnoreCase)))
            {
                return preferredSceneName;
            }

            return selectableScenes.Count > 0 ? selectableScenes[0].SceneName : string.Empty;
        }

        private static bool TryResolveSelectedScene(List<SceneInfo> selectableScenes, out SceneInfo selectedSceneInfo)
        {
            selectedSceneInfo = null;
            if (selectableScenes == null || selectableScenes.Count == 0)
            {
                return false;
            }

            string selectedSceneName;
            if (!TryResolveSceneNameFromSelectorValue(selectableScenes, ServedDishTracker.selectedSceneName, out selectedSceneName))
            {
                selectedSceneName = selectableScenes[0].SceneName;
            }

            ServedDishTracker.selectedSceneName = selectedSceneName;

            for (int i = 0; i < selectableScenes.Count; i++)
            {
                SceneInfo scene = selectableScenes[i];
                if (!string.Equals(scene.SceneName, selectedSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                selectedSceneInfo = scene;
                SceneInfo currentScene;
                if (!TryGetCurrentSceneInfo(out currentScene) || !string.Equals(currentScene.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
                {
                    preferredSceneName = scene.SceneName;
                }
                return true;
            }

            return false;
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
            SceneInfo scene = SyncSceneSelectorConfigEntry();
            bool isLockedToCurrentScene = IsLockedToCurrentScene();

            GUILayout.BeginVertical();

            if (scene == null)
            {
                GUILayout.Label(Ui("当前还没有可用关卡数据。请进入世界地图或街机大厅；DIY 关卡元数据加载完成后也会自动出现。", "No scene data is available yet. Visit the world map or arcade lobby; DIY scenes appear after their metadata finishes loading."));
                if (GUILayout.Button(Ui("刷新关卡列表", "Refresh Scenes")))
                {
                    RefreshKnownScenes(true);
                    SyncSceneSelectorConfigEntry();
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
                scene = SyncSceneSelectorConfigEntry();
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
            List<CategorySelectionGroup> groups = BuildCategorySelectionGroups(scene);
            if (groups.Count == 0)
            {
                return;
            }

            GUILayout.Space(4f);
            GUILayout.Label(Ui("按类别批量勾选：", "Track by category:"));
            for (int i = 0; i < groups.Count;)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 2 && i < groups.Count; column++, i++)
                {
                    CategorySelectionGroup group = groups[i];
                    bool allTracked = AreAllCategoryRecipesTracked(scene, group);
                    bool nextTracked = GUILayout.Toggle(allTracked, Ui("全部", "All ") + group.CategoryName, GUILayout.MinWidth(160f), GUILayout.ExpandWidth(true));
                    if (nextTracked != allTracked)
                    {
                        SetTrackedForCategory(scene, group, nextTracked);
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

        private static List<CategorySelectionGroup> BuildCategorySelectionGroups(SceneInfo scene)
        {
            bool chinese = UseChinese();
            if (scene != null
                && string.Equals(cachedCategorySelectionSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)
                && cachedCategorySelectionCatalogRevision == scene.CatalogRevision
                && cachedCategorySelectionTierRevision == categoryTierRevision
                && cachedCategorySelectionChinese == chinese)
            {
                return CategorySelectionGroupsBuffer;
            }

            CategorySelectionGroupsBuffer.Clear();
            cachedCategorySelectionSceneName = scene != null ? scene.SceneName : string.Empty;
            cachedCategorySelectionCatalogRevision = scene != null ? scene.CatalogRevision : -1;
            cachedCategorySelectionTierRevision = categoryTierRevision;
            cachedCategorySelectionChinese = chinese;
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return CategorySelectionGroupsBuffer;
            }

            Dictionary<string, CategorySelectionGroup> groupsByName = new Dictionary<string, CategorySelectionGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null)
                {
                    continue;
                }

                string categoryName = DishNameCatalog.GetDisplayCategoryName(recipe.InternalName, chinese);
                if (string.IsNullOrEmpty(categoryName))
                {
                    categoryName = Ui("其他", "Other");
                }
                CategorySelectionGroup group;
                if (!groupsByName.TryGetValue(categoryName, out group))
                {
                    group = new CategorySelectionGroup();
                    group.CategoryName = categoryName;
                    group.CategoryTier = recipe.CategoryTier;
                    groupsByName.Add(categoryName, group);
                    CategorySelectionGroupsBuffer.Add(group);
                }
                else
                {
                    group.CategoryTier = Math.Min(group.CategoryTier, recipe.CategoryTier);
                }

                group.RecipeIds.Add(recipe.Id);
            }

            CategorySelectionGroupsBuffer.Sort(delegate(CategorySelectionGroup a, CategorySelectionGroup b)
            {
                int tierCompare = a.CategoryTier.CompareTo(b.CategoryTier);
                if (tierCompare != 0)
                {
                    return tierCompare;
                }

                return string.Compare(a.CategoryName, b.CategoryName, StringComparison.OrdinalIgnoreCase);
            });
            return CategorySelectionGroupsBuffer;
        }

        private static bool AreAllCategoryRecipesTracked(SceneInfo scene, CategorySelectionGroup group)
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

        private static void SetTrackedForCategory(SceneInfo scene, CategorySelectionGroup group, bool shouldTrack)
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

        private static void RegisterTicketWidget(RecipeWidgetUIController widget, int recipeId, int order)
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
            state.Widget = widget;
            state.DisplayConfig = displayConfig;
            state.TopDisplayConfig = topDisplayConfig;
            state.OriginalDisplayTint = displayConfig.m_tint;
            state.OriginalTopTint = topDisplayConfig.m_tint;
            state.CanvasGroup = widget.GetComponent<CanvasGroup>();
            state.CanvasGroupResolved = true;
            state.OriginalOpacity = state.CanvasGroup != null ? Mathf.Clamp01(state.CanvasGroup.alpha) : 1f;
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
                canvasGroup.alpha = 1f;
                canvasGroup.blocksRaycasts = false;
                state.CanvasGroup = canvasGroup;
                state.CanvasGroupResolved = true;
            }

            return canvasGroup;
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

        private static void RefreshTicketWidgetTints()
        {
            if (TicketWidgetsByInstanceId.Count == 0)
            {
                ticketWidgetsDirty = false;
                ticketWidgetTintActive = false;
                nextTicketWidgetRefreshFrame = 0;
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
                if (ticketWidgetTintActive)
                {
                    RestoreAllTicketWidgetTints();
                }

                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
                return;
            }

            bool showPrepared = IsPreparedTrackingEnabled();
            Dictionary<int, int> preparedRemainingByRecipe = PreparedRemainingByRecipeBuffer;
            preparedRemainingByRecipe.Clear();
            if (showPrepared)
            {
                foreach (KeyValuePair<int, int> pair in PreparedCountsByRecipe)
                {
                    preparedRemainingByRecipe[pair.Key] = pair.Value;
                }
            }

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

            if (showPrepared)
            {
                TicketWidgetsBuffer.Sort(delegate(TicketWidgetState a, TicketWidgetState b)
                {
                    int recipeCompare = a.RecipeId.CompareTo(b.RecipeId);
                    if (recipeCompare != 0)
                    {
                        return recipeCompare;
                    }

                    return a.Order.CompareTo(b.Order);
                });
            }

            bool needsRetry = false;
            bool appliedTint = false;
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
                    else
                    {
                        appliedTint = true;
                    }
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

                bool hasPreparedAssignment = false;
                if (showPrepared)
                {
                    int remainingPrepared;
                    if (preparedRemainingByRecipe.TryGetValue(state.RecipeId, out remainingPrepared) && remainingPrepared > 0)
                    {
                        preparedRemainingByRecipe[state.RecipeId] = remainingPrepared - 1;
                        hasPreparedAssignment = true;
                    }
                }

                Color tint = hasPreparedAssignment ? preparedTint : onMenuTint;
                if (!ApplyTicketWidgetTint(state, tint, tint))
                {
                    needsRetry = true;
                }
                else
                {
                    appliedTint = true;
                }
            }

            ticketWidgetsDirty = needsRetry;
            ticketWidgetTintActive = appliedTint;
            nextTicketWidgetRefreshFrame = needsRetry ? Time.frameCount + TicketWidgetRetryIntervalFrames : 0;
        }

    }
}
