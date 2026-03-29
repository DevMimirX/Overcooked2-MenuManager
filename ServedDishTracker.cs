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

namespace HostUtilities
{
    internal static class ServedDishTracker
    {
        private enum TrackerLanguage
        {
            Auto,
            English,
            Chinese
        }

        private enum OverlayTextAlignment
        {
            Left,
            Right,
            Center
        }

        private sealed class RecipeInfo
        {
            public int Id;
            public string InternalName;
            public string EnglishName;
            public string ChineseName;
        }

        private sealed class SceneInfo
        {
            public string SceneName;
            public string DisplayName;
            public List<int>[] PhaseRecipeIds;
            public readonly List<int> AllRecipeIds = new List<int>();
            public readonly List<RecipeInfo> OrderedRecipes = new List<RecipeInfo>();
            public readonly Dictionary<int, RecipeInfo> RecipesById = new Dictionary<int, RecipeInfo>();
        }

        private sealed class RunInfo
        {
            public string SceneName;
            public int CurrentPhaseIndex;
            public int TotalAdded;
            public readonly Dictionary<int, int> AddedCounts = new Dictionary<int, int>();
            public readonly Dictionary<int, int> ServedCounts = new Dictionary<int, int>();
        }

        private sealed class OverlayDisplay : DebugDisplay
        {
            private static readonly Color PanelBackgroundColor = new Color(0f, 0f, 0f, 0.58f);
            private const float PanelPadding = 10f;
            private const int OverlayRebuildIntervalFrames = 10;
            private string cachedText = string.Empty;

            public override void OnSetUp()
            {
            }

            public override void OnUpdate()
            {
                if (!overlayDirty && Time.frameCount < lastOverlayBuildFrame + OverlayRebuildIntervalFrames)
                {
                    return;
                }

                cachedText = BuildOverlayText();
                overlayDirty = false;
                lastOverlayBuildFrame = Time.frameCount;
            }

            public override void OnDraw(ref Rect rect, GUIStyle style)
            {
                if (string.IsNullOrEmpty(cachedText))
                {
                    return;
                }

                Color originalColor = GUI.color;
                GUI.color = PanelBackgroundColor;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = originalColor;

                GUIStyle textStyle = new GUIStyle(style);
                textStyle.richText = true;
                textStyle.wordWrap = false;
                textStyle.clipping = TextClipping.Clip;

                Rect contentRect = new Rect(
                    rect.x + PanelPadding,
                    rect.y + PanelPadding,
                    Mathf.Max(1f, rect.width - PanelPadding * 2f),
                    Mathf.Max(1f, rect.height - PanelPadding * 2f));
                GUI.Label(contentRect, cachedText, textStyle);
            }
        }

        private sealed class OverlayRow
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
            public int OnMenu;
        }

        private const string TrackerSection = "03-历史菜单追踪";
        private const string DishSelectionSection = "04-历史菜单菜品";
        private const string SceneSelectorKey = "选择关卡";
        private const string NoSceneSelectorValue = "暂无可选关卡";
        private const int MaxSceneSelectorDisplayLength = 40;
        private const int MaxDishSelectorDisplayLength = 26;
        private const int MaxOverlaySceneDisplayLength = 24;
        private const int MaxOverlayDishDisplayLength = 12;
        private const int SceneRefreshIntervalInRound = 600;
        private const int SceneRefreshIntervalOutOfRound = 20;
        private const int ConfigCustomizationIntervalInRound = 600;
        private const int ConfigCustomizationIntervalOutOfRound = 30;
        private const int DiscoveryFlushIntervalFrames = 900;
        private const int ControllerLookupIntervalFrames = 30;

        private static readonly Dictionary<string, HashSet<int>> TrackedIdsByScene = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SceneInfo> SceneCache = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, int> CurrentOnMenuCounts = new Dictionary<int, int>();
        private static readonly List<SceneInfo> KnownScenes = new List<SceneInfo>();
        private static readonly List<SceneInfo> CachedDIYScenes = new List<SceneInfo>();
        private static readonly List<string> OrderedSceneSelectorValues = new List<string>();
        private static readonly List<OverlayRow> OverlayRowsBuffer = new List<OverlayRow>();
        private static readonly Dictionary<string, string> SceneSelectorValuesByScene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> SceneNamesBySelectorValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly StringBuilder OverlayTextBuilder = new StringBuilder(768);
        private static readonly TeamID[] TeamIds = (TeamID[])Enum.GetValues(typeof(TeamID));

        private static readonly ConfigDefinition LegacyEnabledDefinition = new ConfigDefinition("03-已送菜品追踪", "启用已送菜品追踪");
        private static readonly ConfigDefinition LegacyLanguageDefinition = new ConfigDefinition("03-已送菜品追踪", "显示语言");
        private static readonly ConfigDefinition LegacySelectedSceneStateDefinition = new ConfigDefinition("99-内部", "已选关卡内部状态");
        private static readonly ConfigDefinition SceneSelectorDefinition = new ConfigDefinition(TrackerSection, SceneSelectorKey);
        private static readonly ConfigDefinition TrackerPanelDefinition = new ConfigDefinition(DishSelectionSection, "关卡与菜品选择器");
        private static readonly ConfigDefinition[] LegacyConfigDefinitions = new ConfigDefinition[]
        {
            new ConfigDefinition("03-已送菜品追踪", "启用已送菜品追踪"),
            new ConfigDefinition("03-已送菜品追踪", "打开追踪选择器"),
            new ConfigDefinition("03-已送菜品追踪", "切换中英显示"),
            new ConfigDefinition("03-已送菜品追踪", "从配置窗口打开菜单管理"),
            new ConfigDefinition("03-已送菜品追踪", "显示菜单管理按钮"),
            new ConfigDefinition("03-已送菜品追踪", "选择器字体大小"),
            new ConfigDefinition("03-已送菜品追踪", "选择器窗口X"),
            new ConfigDefinition("03-已送菜品追踪", "选择器窗口Y"),
            new ConfigDefinition("03-已送菜品追踪", "选择器窗口宽度"),
            new ConfigDefinition("03-已送菜品追踪", "选择器窗口高度")
        };

        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<TrackerLanguage> languageMode;
        private static ConfigEntry<string> selectedScene;
        private static ConfigEntry<string> trackerPanel;
        private static ConfigEntry<int> overlayX;
        private static ConfigEntry<int> overlayY;
        private static ConfigEntry<int> overlayWidth;
        private static ConfigEntry<int> overlayHeight;
        private static ConfigEntry<int> overlayFontSize;
        private static ConfigEntry<Color> overlayFontColor;
        private static ConfigEntry<Color> overlayServedValueColor;
        private static ConfigEntry<Color> overlayProbabilityValueColor;
        private static ConfigEntry<bool> overlayBoldFont;
        private static ConfigEntry<int> overlayMaxDisplayDishes;
        private static ConfigEntry<OverlayTextAlignment> overlayTextAlignment;
        private static DebugOverlayHost overlayHost;
        private static string selectionFilePath;
        private static RunInfo currentRun;
        private static int nextSceneRefreshFrame;
        private static int nextDIYSceneRefreshFrame;
        private static int idScanSceneCount;
        private static int idConflictCount;
        private static string idConflictSample = string.Empty;
        private static string lastIdScanLog = string.Empty;
        private static string lastConfigSyncError = string.Empty;
        private static string lastSceneRefreshContext = string.Empty;
        private static string lastConfigurationManagerIntegrationError = string.Empty;
        private static string preferredSceneName = string.Empty;
        private static bool? migratedEnabledValue;
        private static TrackerLanguage? migratedLanguageValue;
        private static readonly FieldInfo ActiveOrdersField = typeof(ClientOrderControllerBase).GetField("m_activeOrders", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ActiveOrderType = typeof(ClientOrderControllerBase).GetNestedType("ActiveOrder", BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveOrderRecipeListEntryField = ActiveOrderType != null
            ? ActiveOrderType.GetField("RecipeListEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static Assembly configurationManagerAssembly;
        private static Type configurationManagerType;
        private static Type configSettingEntryType;
        private static FieldInfo configurationManagerAllSettingsField;
        private static PropertyInfo configSettingEntryEntryProperty;
        private static bool configurationManagerReflectionInitialized;
        private static UnityEngine.Object cachedConfigurationManagerObject;
        private static int lastConfigurationCustomizationSignature;
        private static int lastConfigurationManagerInstanceId;
        private static ClientFlowControllerBase cachedClientFlowController;
        private static int nextClientFlowLookupFrame;
        private static ClientKitchenFlowControllerBase cachedKitchenFlowController;
        private static int nextKitchenFlowLookupFrame;
        private static string currentOnMenuCountsSceneName = string.Empty;
        private static bool currentOnMenuCountsDirty = true;
        private static int nextTrackedSceneRefreshPollFrame;
        private static int nextConfigurationCustomizationFrame;
        private static int nextDiscoveryFlushFrame;
        private static int lastOverlayBuildFrame = int.MinValue;
        private static bool overlayVisible;
        private static bool overlayDirty = true;

        public static void Awake()
        {
            selectionFilePath = Path.Combine(Paths.ConfigPath, "HostUtilities-ServedDishTrackerSelections.txt");
            CaptureLegacyValues();
            RemoveLegacyConfigEntries();
            RemoveGeneratedConfigEntries();

            enabled = _MODEntry.Instance.Config.Bind<bool>(
                TrackerSection,
                "启用历史菜单追踪",
                migratedEnabledValue ?? true,
                "标准关卡历史菜单追踪。先在本分组里选择关卡，再到“04-历史菜单菜品”里勾选要追踪的菜品。进入关卡时会自动锁定为当前关卡。");
            languageMode = _MODEntry.Instance.Config.Bind<TrackerLanguage>(
                TrackerSection,
                "显示语言",
                migratedLanguageValue ?? TrackerLanguage.Auto,
                "Auto / English / Chinese");
            overlayX = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗X",
                40,
                new ConfigDescription("历史菜单追踪悬浮窗左上角 X 坐标。默认在左侧中部。", new AcceptableValueRange<int>(0, 4000)));
            overlayY = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗Y",
                300,
                new ConfigDescription("历史菜单追踪悬浮窗左上角 Y 坐标。", new AcceptableValueRange<int>(0, 4000)));
            overlayWidth = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗宽度",
                240,
                new ConfigDescription("历史菜单追踪悬浮窗宽度。", new AcceptableValueRange<int>(240, 1600)));
            overlayHeight = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗高度",
                320,
                new ConfigDescription("历史菜单追踪悬浮窗高度。", new AcceptableValueRange<int>(120, 1600)));
            overlayFontSize = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗字体大小",
                15,
                new ConfigDescription("历史菜单追踪悬浮窗字体大小。", new AcceptableValueRange<int>(8, 48)));
            overlayFontColor = _MODEntry.Instance.Config.Bind<Color>(
                TrackerSection,
                "悬浮窗字体颜色",
                new Color(1f, 1f, 1f, 1f),
                "历史菜单追踪悬浮窗字体颜色。");
            overlayServedValueColor = _MODEntry.Instance.Config.Bind<Color>(
                TrackerSection,
                "悬浮窗上单数量颜色",
                new Color(0.58f, 0.84f, 1f, 1f),
                "历史菜单追踪悬浮窗中“上单数量”数值的颜色。");
            overlayProbabilityValueColor = _MODEntry.Instance.Config.Bind<Color>(
                TrackerSection,
                "悬浮窗概率颜色",
                new Color(1f, 0.84f, 0.40f, 1f),
                "历史菜单追踪悬浮窗中“概率”数值的颜色。");
            overlayBoldFont = _MODEntry.Instance.Config.Bind<bool>(
                TrackerSection,
                "悬浮窗粗体",
                false,
                "是否使用粗体显示历史菜单追踪悬浮窗文字。");
            overlayMaxDisplayDishes = _MODEntry.Instance.Config.Bind<int>(
                TrackerSection,
                "悬浮窗最大显示菜品数",
                12,
                new ConfigDescription("历史菜单追踪悬浮窗最多显示多少道菜。", new AcceptableValueRange<int>(1, 40)));
            overlayTextAlignment = _MODEntry.Instance.Config.Bind<OverlayTextAlignment>(
                TrackerSection,
                "悬浮窗文本对齐",
                OverlayTextAlignment.Left,
                "Left / Right / Center");
            selectedScene = _MODEntry.Instance.Config.Bind<string>(
                SceneSelectorDefinition,
                NoSceneSelectorValue,
                new ConfigDescription("在这里选择要配置追踪菜品的关卡。进入关卡时会自动锁定到当前关卡。"));
            trackerPanel = _MODEntry.Instance.Config.Bind<string>(
                TrackerPanelDefinition,
                string.Empty,
                new ConfigDescription("在这里勾选当前选中关卡的追踪菜品。"));

            LoadSelections();

            overlayHost = new DebugOverlayHost(
                GetOverlayTextAnchor,
                GetOverlayFontSize,
                GetOverlayFontColor,
                GetOverlayFontStyle,
                BuildOverlayRect);
            overlayHost.AddDisplay(new OverlayDisplay());

            ModuleUtility.RegisterHarmony(typeof(ServedDishTracker));
        }

        public static void Update()
        {
            bool inActiveRound = IsInActiveRound();
            overlayVisible = ShouldShowOverlay(inActiveRound);
            if (overlayVisible)
            {
                overlayHost.Update();
            }

            try
            {
                if (Time.frameCount >= nextTrackedSceneRefreshPollFrame)
                {
                    RefreshKnownScenes(false);
                    SyncTrackingConfigEntries();
                    nextTrackedSceneRefreshPollFrame = Time.frameCount + (inActiveRound ? SceneRefreshIntervalInRound : SceneRefreshIntervalOutOfRound);
                }

                if (Time.frameCount >= nextConfigurationCustomizationFrame)
                {
                    TryCustomizeConfigurationManagerSettings();
                    nextConfigurationCustomizationFrame = Time.frameCount + (inActiveRound ? ConfigCustomizationIntervalInRound : ConfigCustomizationIntervalOutOfRound);
                }

                if (Time.frameCount >= nextDiscoveryFlushFrame)
                {
                    DishNameCatalog.FlushDiscoveryReport();
                    nextDiscoveryFlushFrame = Time.frameCount + DiscoveryFlushIntervalFrames;
                }
            }
            catch (Exception ex)
            {
                string errorText = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(lastConfigSyncError, errorText, StringComparison.Ordinal))
                {
                    lastConfigSyncError = errorText;
                    _MODEntry.LogError("[ServedDishTracker] Failed to refresh tracking config entries: " + ex);
                }
            }
        }

        public static void OnGUI()
        {
            if (overlayVisible)
            {
                overlayHost.OnGUI();
            }
        }

        private static void InvalidateOverlay()
        {
            overlayDirty = true;
        }

        private static void ClearOnMenuCounts()
        {
            CurrentOnMenuCounts.Clear();
            currentOnMenuCountsSceneName = string.Empty;
            currentOnMenuCountsDirty = true;
        }

        private static void IncrementOnMenuCount(string sceneName, int recipeId)
        {
            EnsureOnMenuCountScene(sceneName);
            CurrentOnMenuCounts[recipeId] = GetCount(CurrentOnMenuCounts, recipeId) + 1;
            currentOnMenuCountsDirty = false;
        }

        private static void DecrementOnMenuCount(string sceneName, int recipeId)
        {
            EnsureOnMenuCountScene(sceneName);
            int nextValue = Math.Max(0, GetCount(CurrentOnMenuCounts, recipeId) - 1);
            if (nextValue > 0)
            {
                CurrentOnMenuCounts[recipeId] = nextValue;
            }
            else
            {
                CurrentOnMenuCounts.Remove(recipeId);
            }

            currentOnMenuCountsDirty = false;
        }

        private static void EnsureOnMenuCountScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            if (!string.Equals(currentOnMenuCountsSceneName, sceneName, StringComparison.OrdinalIgnoreCase))
            {
                CurrentOnMenuCounts.Clear();
                currentOnMenuCountsSceneName = sceneName;
                currentOnMenuCountsDirty = false;
            }
        }

        private static bool ShouldShowOverlay()
        {
            return ShouldShowOverlay(IsInActiveRound());
        }

        private static bool ShouldShowOverlay(bool inActiveRound)
        {
            if (enabled == null || !enabled.Value)
            {
                return false;
            }

            if (!inActiveRound)
            {
                return false;
            }

            SceneInfo scene;
            return TryGetCurrentSceneInfo(out scene) && scene != null && scene.OrderedRecipes.Count > 0;
        }

        private static bool IsInActiveRound()
        {
            ClientFlowControllerBase clientFlowController = GetClientFlowController();
            return clientFlowController != null && clientFlowController.InRound;
        }

        private static ClientFlowControllerBase GetClientFlowController()
        {
            if (cachedClientFlowController == null || Time.frameCount >= nextClientFlowLookupFrame)
            {
                cachedClientFlowController = UnityEngine.Object.FindObjectOfType<ClientFlowControllerBase>();
                nextClientFlowLookupFrame = Time.frameCount + ControllerLookupIntervalFrames;
            }

            return cachedClientFlowController;
        }

        private static ClientKitchenFlowControllerBase GetKitchenFlowController()
        {
            if (cachedKitchenFlowController == null || Time.frameCount >= nextKitchenFlowLookupFrame)
            {
                cachedKitchenFlowController = UnityEngine.Object.FindObjectOfType<ClientKitchenFlowControllerBase>();
                nextKitchenFlowLookupFrame = Time.frameCount + ControllerLookupIntervalFrames;
            }

            return cachedKitchenFlowController;
        }

        private static Rect BuildOverlayRect(GUIStyle style)
        {
            float scale = Mathf.Max(_MODEntry.dpiScaleFactor, 1f);
            float width = Mathf.Clamp(overlayWidth != null ? overlayWidth.Value : 500, 240, 1600) * scale;
            float height = Mathf.Clamp(overlayHeight != null ? overlayHeight.Value : 520, 120, 1600) * scale;
            float x = Mathf.Clamp((overlayX != null ? overlayX.Value : 40) * scale, 0f, Mathf.Max(0f, Screen.width - width));
            float y = Mathf.Clamp((overlayY != null ? overlayY.Value : 300) * scale, 0f, Mathf.Max(0f, Screen.height - height));
            return new Rect(x, y, width, height);
        }

        private static TextAnchor GetOverlayTextAnchor()
        {
            OverlayTextAlignment alignment = overlayTextAlignment != null ? overlayTextAlignment.Value : OverlayTextAlignment.Left;
            switch (alignment)
            {
                case OverlayTextAlignment.Right:
                    return TextAnchor.UpperRight;
                case OverlayTextAlignment.Center:
                    return TextAnchor.UpperCenter;
                default:
                    return TextAnchor.UpperLeft;
            }
        }

        private static int GetOverlayFontSize()
        {
            return overlayFontSize != null ? overlayFontSize.Value : _MODEntry.defaultFontSize.Value;
        }

        private static Color GetOverlayFontColor()
        {
            return overlayFontColor != null ? overlayFontColor.Value : _MODEntry.defaultFontColor.Value;
        }

        private static FontStyle GetOverlayFontStyle()
        {
            return overlayBoldFont != null && overlayBoldFont.Value ? FontStyle.Bold : FontStyle.Normal;
        }

        [HarmonyPatch(typeof(LoadingScreenFlow), "NextScene", MethodType.Getter)]
        [HarmonyPrefix]
        private static void LoadingScreenFlow_NextScene_Prefix()
        {
            currentRun = null;
            cachedClientFlowController = null;
            cachedKitchenFlowController = null;
            nextClientFlowLookupFrame = 0;
            nextKitchenFlowLookupFrame = 0;
            ClearOnMenuCounts();
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderAdded")]
        [HarmonyPostfix]
        private static void ClientKitchenFlowControllerBase_OnOrderAdded_Postfix(Serialisable _orderData)
        {
            if (!enabled.Value)
            {
                return;
            }

            ServerOrderData orderData = _orderData as ServerOrderData;
            if (orderData == null || orderData.RecipeListEntry == null || orderData.RecipeListEntry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, orderData.RecipeListEntry.m_order);
            RunInfo run = EnsureRun(scene);
            int recipeId = orderData.RecipeListEntry.m_order.m_uID;
            run.TotalAdded++;
            run.AddedCounts[recipeId] = GetCount(run.AddedCounts, recipeId) + 1;
            IncrementOnMenuCount(scene.SceneName, recipeId);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnSuccessfulDelivery")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnSuccessfulDelivery_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
            RunInfo run = EnsureRun(scene);
            int recipeId = entry.m_order.m_uID;
            run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
            DecrementOnMenuCount(scene.SceneName, recipeId);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnFailedDelivery")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnFailedDelivery_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null || _orderID.m_id == 0u)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
            RunInfo run = EnsureRun(scene);
            int recipeId = entry.m_order.m_uID;
            run.ServedCounts[recipeId] = GetCount(run.ServedCounts, recipeId) + 1;
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientKitchenFlowControllerBase), "OnOrderExpired")]
        [HarmonyPrefix]
        private static void ClientKitchenFlowControllerBase_OnOrderExpired_Prefix(ClientKitchenFlowControllerBase __instance, TeamID _teamID, OrderID _orderID)
        {
            if (!enabled.Value || __instance == null)
            {
                return;
            }

            ClientTeamMonitor monitor = __instance.GetMonitorForTeam(_teamID);
            if (monitor == null || monitor.OrdersController == null)
            {
                return;
            }

            RecipeList.Entry entry = monitor.OrdersController.GetRecipe(_orderID);
            if (entry == null || entry.m_order == null)
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene))
            {
                return;
            }

            EnsureRecipe(scene, entry.m_order);
            DecrementOnMenuCount(scene.SceneName, entry.m_order.m_uID);
            InvalidateOverlay();
        }

        [HarmonyPatch(typeof(ClientDynamicFlowController), "OnDynamicLevelMessage")]
        [HarmonyPostfix]
        private static void ClientDynamicFlowController_OnDynamicLevelMessage_Postfix(Serialisable _serialisable)
        {
            DynamicLevelMessage message = _serialisable as DynamicLevelMessage;
            ResetProbabilityState(message != null ? message.m_phase : 0);
            InvalidateOverlay();
        }

        private static void RefreshKnownScenes(bool forceRefresh)
        {
            if (forceRefresh)
            {
                nextDIYSceneRefreshFrame = 0;
            }

            List<SceneDirectoryData.SceneDirectoryEntry> entries;
            string refreshContext = BuildSceneRefreshContext(out entries);
            bool contextChanged = !string.Equals(lastSceneRefreshContext, refreshContext, StringComparison.Ordinal);
            if (!forceRefresh && !contextChanged && Time.frameCount < nextSceneRefreshFrame)
            {
                return;
            }

            lastSceneRefreshContext = refreshContext;
            bool inActiveRound = IsInActiveRound();
            nextSceneRefreshFrame = Time.frameCount + (inActiveRound ? SceneRefreshIntervalInRound : SceneRefreshIntervalOutOfRound);

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

            UpdateIdScanStatus(KnownScenes);
        }

        private static string BuildSceneRefreshContext(out List<SceneDirectoryData.SceneDirectoryEntry> entries)
        {
            entries = new List<SceneDirectoryData.SceneDirectoryEntry>();
            SceneDirectoryData.PerPlayerCountDirectoryEntry currentVariant;
            if (IsInActiveRound() && TryGetCurrentSceneVariant(out currentVariant) && currentVariant != null)
            {
                return "level:" + currentVariant.SceneName;
            }

            entries = GetAvailableSceneEntries();
            List<string> signatures = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                string signature = BuildSceneEntrySignature(entries[i]);
                if (!string.IsNullOrEmpty(signature))
                {
                    signatures.Add(signature);
                }
            }

            List<SceneInfo> diyScenes = GetDIYScenes();
            for (int i = 0; i < diyScenes.Count; i++)
            {
                SceneInfo diyScene = diyScenes[i];
                signatures.Add("diy:" + diyScene.SceneName + ":" + diyScene.DisplayName + ":" + diyScene.OrderedRecipes.Count);
            }

            signatures.Sort(StringComparer.OrdinalIgnoreCase);
            return "menu:" + string.Join("|", signatures.ToArray());
        }

        private static string BuildSceneEntrySignature(SceneDirectoryData.SceneDirectoryEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVarient = GetSceneVarient(entry);
            string sceneName = sceneVarient != null ? sceneVarient.SceneName : string.Empty;
            return entry.Label + ":" + sceneName;
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
            SceneInfo desiredScene = selectableScenes.FirstOrDefault(scene => string.Equals(scene.SceneName, desiredSceneName, StringComparison.OrdinalIgnoreCase));
            string desiredSelectorValue = GetSceneSelectorValue(desiredScene);
            if (selectedScene != null && !string.Equals(selectedScene.Value, desiredSelectorValue, StringComparison.OrdinalIgnoreCase))
            {
                selectedScene.Value = desiredSelectorValue;
            }

            SceneInfo selectedSceneInfo;
            return TryResolveSelectedScene(selectableScenes, out selectedSceneInfo) ? selectedSceneInfo : null;
        }

        private static List<SceneInfo> GetSelectableScenes()
        {
            SceneInfo currentScene;
            if (IsInActiveRound() && TryGetCurrentSceneInfo(out currentScene))
            {
                return new List<SceneInfo> { currentScene };
            }

            return KnownScenes.ToList();
        }

        private static void RebuildSceneSelectorMaps(List<SceneInfo> selectableScenes)
        {
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
        }

        private static string ResolveDesiredSceneName(List<SceneInfo> selectableScenes)
        {
            SceneInfo currentScene;
            if (TryGetCurrentSceneInfo(out currentScene))
            {
                return currentScene.SceneName;
            }

            string selectedSceneName;
            if (selectedScene != null
                && TryResolveSceneNameFromSelectorValue(selectableScenes, selectedScene.Value, out selectedSceneName))
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
            if (selectableScenes == null || selectableScenes.Count == 0 || selectedScene == null)
            {
                return false;
            }

            string selectedSceneName;
            if (!TryResolveSceneNameFromSelectorValue(selectableScenes, selectedScene.Value, out selectedSceneName))
            {
                selectedSceneName = selectableScenes[0].SceneName;
            }

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
                string candidate = AppendSuffixWithLengthLimit(baseValue, suffix, MaxSceneSelectorDisplayLength);
                if (!SceneNamesBySelectorValue.ContainsKey(candidate))
                {
                    return candidate;
                }

                index++;
            }
        }

        private static object[] BuildSceneSelectorAcceptableValues()
        {
            List<string> selectorValues = new List<string>(OrderedSceneSelectorValues);
            if (selectorValues.Count == 0)
            {
                selectorValues.Add(NoSceneSelectorValue);
            }

            object[] values = new object[selectorValues.Count];
            for (int i = 0; i < selectorValues.Count; i++)
            {
                values[i] = selectorValues[i];
            }

            return values;
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
            RefreshKnownScenes(false);
            SceneInfo scene = SyncSceneSelectorConfigEntry();
            bool isLockedToCurrentScene = IsLockedToCurrentScene();

            GUILayout.BeginVertical();

            if (scene == null)
            {
                GUILayout.Label("当前还没有可用关卡数据。请先进入世界地图、街机大厅，或先进入一次目标关卡。");
                if (GUILayout.Button("刷新关卡列表"))
                {
                    RefreshKnownScenes(true);
                    SyncSceneSelectorConfigEntry();
                }
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label(isLockedToCurrentScene
                ? "当前在关卡内，关卡选择已自动锁定为本局关卡。"
                : "请先在上方“选择关卡”下拉框中切换关卡，再勾选要追踪的菜品。");

            GUILayout.BeginHorizontal();
            GUILayout.Label("已追踪 " + GetTrackedCount(scene) + "/" + scene.OrderedRecipes.Count, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("全选", GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                SetAllTracked(scene, true);
            }
            if (GUILayout.Button("清空", GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                SetAllTracked(scene, false);
            }
            if (GUILayout.Button("刷新", GUILayout.MinWidth(42f), GUILayout.MaxWidth(52f), GUILayout.ExpandWidth(false)))
            {
                RefreshKnownScenes(true);
                scene = SyncSceneSelectorConfigEntry();
            }
            GUILayout.EndHorizontal();

            if (scene.OrderedRecipes.Count == 0)
            {
                GUILayout.Label("这个 DIY 关卡的菜谱还没有被读取。请先进入一次该关卡，然后回到 F1 再勾选要追踪的菜品。");
                GUILayout.EndVertical();
                return;
            }

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

        private static void SetAllTracked(SceneInfo scene, bool shouldTrack)
        {
            if (scene == null)
            {
                return;
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                ApplyTrackedState(scene.SceneName, scene.OrderedRecipes[i].Id, shouldTrack, false);
            }

            SaveSelections();
        }

        private static void TryCustomizeConfigurationManagerSettings()
        {
            try
            {
                if (!EnsureConfigurationManagerReflection())
                {
                    return;
                }

                object configurationManager = GetConfigurationManagerInstance();
                if (configurationManager == null)
                {
                    return;
                }

                IList settings = configurationManagerAllSettingsField.GetValue(configurationManager) as IList;
                if (settings == null)
                {
                    return;
                }

                int instanceId = ((UnityEngine.Object)configurationManager).GetInstanceID();
                int customizationSignature = ComputeConfigurationCustomizationSignature(instanceId, settings.Count);
                if (instanceId == lastConfigurationManagerInstanceId && customizationSignature == lastConfigurationCustomizationSignature)
                {
                    return;
                }

                for (int j = 0; j < settings.Count; j++)
                {
                    object settingEntry = settings[j];
                    ConfigEntryBase entry = configSettingEntryEntryProperty.GetValue(settingEntry, null) as ConfigEntryBase;
                    if (entry == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (IsSameDefinition(entry.Definition, TrackerPanelDefinition))
                        {
                            CustomizeTrackerPanelSetting(settingEntry);
                        }
                        else if (IsSameDefinition(entry.Definition, SceneSelectorDefinition))
                        {
                            CustomizeSceneSelectorSetting(settingEntry);
                        }
                        else if (IsSameDefinition(entry.Definition, LegacySelectedSceneStateDefinition)
                            || IsLegacyTrackerDefinition(entry.Definition)
                            || IsStaleDishSelectionDefinition(entry.Definition))
                        {
                            HideSetting(settingEntry);
                        }
                    }
                    catch (Exception ex)
                    {
                        ConfigDefinition definition = entry.Definition;
                        _MODEntry.LogWarning("[ServedDishTracker] Skipped one ConfigurationManager entry customization: "
                            + (definition != null ? definition.Section : "(unknown)") + "/"
                            + (definition != null ? definition.Key : "(unknown)") + " -> "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                lastConfigurationManagerInstanceId = instanceId;
                lastConfigurationCustomizationSignature = customizationSignature;
            }
            catch (Exception ex)
            {
                string errorText = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(lastConfigurationManagerIntegrationError, errorText, StringComparison.Ordinal))
                {
                    lastConfigurationManagerIntegrationError = errorText;
                    _MODEntry.LogError("[ServedDishTracker] Failed to customize ConfigurationManager entries: " + ex);
                }
            }
        }

        private static bool EnsureConfigurationManagerReflection()
        {
            if (configurationManagerReflectionInitialized)
            {
                return configurationManagerType != null
                    && configSettingEntryType != null
                    && configurationManagerAllSettingsField != null
                    && configSettingEntryEntryProperty != null;
            }

            configurationManagerAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "ConfigurationManager", StringComparison.Ordinal));
            if (configurationManagerAssembly == null)
            {
                return false;
            }

            configurationManagerType = configurationManagerAssembly.GetType("ConfigurationManager.ConfigurationManager");
            configSettingEntryType = configurationManagerAssembly.GetType("ConfigurationManager.ConfigSettingEntry");
            if (configurationManagerType == null || configSettingEntryType == null)
            {
                return false;
            }

            configurationManagerAllSettingsField = configurationManagerType.GetField("_allSettings", BindingFlags.NonPublic | BindingFlags.Instance);
            configSettingEntryEntryProperty = configSettingEntryType.GetProperty("Entry", BindingFlags.Public | BindingFlags.Instance);
            configurationManagerReflectionInitialized = true;
            return configurationManagerAllSettingsField != null && configSettingEntryEntryProperty != null;
        }

        private static object GetConfigurationManagerInstance()
        {
            if (cachedConfigurationManagerObject == null)
            {
                if (configurationManagerType == null)
                {
                    return null;
                }

                UnityEngine.Object[] configurationManagers = Resources.FindObjectsOfTypeAll(configurationManagerType);
                cachedConfigurationManagerObject = configurationManagers != null && configurationManagers.Length > 0
                    ? configurationManagers[0]
                    : null;
            }

            return cachedConfigurationManagerObject;
        }

        private static int ComputeConfigurationCustomizationSignature(int instanceId, int settingsCount)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + instanceId;
                hash = (hash * 31) + settingsCount;
                hash = (hash * 31) + (selectedScene != null && selectedScene.Value != null ? selectedScene.Value.GetHashCode() : 0);
                hash = (hash * 31) + OrderedSceneSelectorValues.Count;
                hash = (hash * 31) + (enabled != null && enabled.Value ? 1 : 0);
                for (int i = 0; i < OrderedSceneSelectorValues.Count; i++)
                {
                    string value = OrderedSceneSelectorValues[i];
                    hash = (hash * 31) + (value != null ? value.GetHashCode() : 0);
                }

                return hash;
            }
        }

        private static void CustomizeTrackerPanelSetting(object settingEntry)
        {
            TrySetRuntimeMember(settingEntry, "Browsable", true);
            TrySetRuntimeMember(settingEntry, "HideSettingName", false);
            TrySetRuntimeMember(settingEntry, "HideDefaultButton", true);
            TrySetRuntimeMember(settingEntry, "DispName", "当前关卡菜品");
            TrySetRuntimeMember(settingEntry, "Order", -100);
            TrySetRuntimeMember(settingEntry, "CustomDrawer", new Action<ConfigEntryBase>(DrawTrackingPanel));
        }

        private static void CustomizeSceneSelectorSetting(object settingEntry)
        {
            object[] acceptableValues = BuildSceneSelectorAcceptableValues();
            TrySetRuntimeMember(settingEntry, "Browsable", true);
            TrySetRuntimeMember(settingEntry, "HideDefaultButton", false);
            TrySetRuntimeMember(settingEntry, "HideSettingName", false);
            TrySetRuntimeMember(settingEntry, "DispName", SceneSelectorKey);
            TrySetRuntimeMember(settingEntry, "Order", -110);
            TrySetRuntimeMember(settingEntry, "AcceptableValues", acceptableValues);
        }

        private static void HideSetting(object settingEntry)
        {
            TrySetRuntimeMember(settingEntry, "Browsable", false);
            TrySetRuntimeMember(settingEntry, "HideDefaultButton", true);
            TrySetRuntimeMember(settingEntry, "HideSettingName", true);
        }

        private static bool IsLegacyTrackerDefinition(ConfigDefinition definition)
        {
            if (definition == null)
            {
                return false;
            }

            if (string.Equals(definition.Section, "03-已送菜品追踪", StringComparison.Ordinal))
            {
                return true;
            }

            for (int i = 0; i < LegacyConfigDefinitions.Length; i++)
            {
                if (IsSameDefinition(definition, LegacyConfigDefinitions[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStaleDishSelectionDefinition(ConfigDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Section))
            {
                return false;
            }

            return string.Equals(definition.Section, DishSelectionSection, StringComparison.Ordinal)
                && !IsSameDefinition(definition, TrackerPanelDefinition);
        }

        private static bool IsSameDefinition(ConfigDefinition left, ConfigDefinition right)
        {
            return left != null
                && right != null
                && string.Equals(left.Section, right.Section, StringComparison.Ordinal)
                && string.Equals(left.Key, right.Key, StringComparison.Ordinal);
        }

        private static void TrySetRuntimeMember(object target, string memberName, object value)
        {
            if (target == null)
            {
                return;
            }

            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                MethodInfo setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(target, new object[] { value });
                    return;
                }
            }

            FieldInfo field = type.GetField("<" + memberName + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (field != null)
            {
                field.SetValue(target, value);
            }
        }

        private static void ApplyTrackedState(string sceneName, int recipeId, bool shouldTrack, bool save)
        {
            SceneInfo scene;
            if (!SceneCache.TryGetValue(sceneName, out scene) || scene == null)
            {
                return;
            }

            HashSet<int> trackedIds;
            if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds))
            {
                trackedIds = new HashSet<int>(scene.OrderedRecipes.Select(x => x.Id));
                TrackedIdsByScene[scene.SceneName] = trackedIds;
            }

            bool changed = shouldTrack ? trackedIds.Add(recipeId) : trackedIds.Remove(recipeId);
            if (changed && save)
            {
                SaveSelections();
            }

            if (changed)
            {
                InvalidateOverlay();
            }
        }

        private static List<SceneDirectoryData.SceneDirectoryEntry> GetAvailableSceneEntries()
        {
            List<SceneDirectoryData.SceneDirectoryEntry> entries = new List<SceneDirectoryData.SceneDirectoryEntry>();

            try
            {
                AddEntries(entries, MenuLevelHelper.GetLevelList());
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to read lobby level list, falling back to other scene sources: " + ex.GetType().Name + ": " + ex.Message);
            }

            AddEntriesFromSceneDirectory(entries, GetProgressSceneDirectory());
            AddEntriesFromSceneDirectory(entries, GetWorldMapSceneDirectory());
            AddFrontendSessionEntries(entries, GameSession.GameType.Cooperative);
            AddFrontendSessionEntries(entries, GameSession.GameType.Competitive);

            return entries;
        }

        private static void AddEntries(List<SceneDirectoryData.SceneDirectoryEntry> entryList, List<SceneDirectoryData.SceneDirectoryEntry> entries)
        {
            if (entryList == null || entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                SceneDirectoryData.SceneDirectoryEntry entry = entries[i];
                if (entry == null || IsIgnoredSceneEntry(entry))
                {
                    continue;
                }

                entryList.Add(entry);
            }
        }

        private static void AddEntriesFromSceneDirectory(List<SceneDirectoryData.SceneDirectoryEntry> entryList, SceneDirectoryData sceneDirectory)
        {
            if (sceneDirectory == null || sceneDirectory.Scenes == null)
            {
                return;
            }

            AddEntries(entryList, sceneDirectory.Scenes.ToList());
        }

        private static void AddFrontendSessionEntries(List<SceneDirectoryData.SceneDirectoryEntry> entryList, GameSession.GameType gameType)
        {
            List<GameSession> sessionPrefabs = GetFrontendSessionPrefabs(gameType);
            for (int i = 0; i < sessionPrefabs.Count; i++)
            {
                AddEntriesFromSceneDirectory(entryList, GetSceneDirectory(sessionPrefabs[i]));
            }
        }

        private static List<SceneInfo> GetDIYScenes()
        {
            if (Time.frameCount < nextDIYSceneRefreshFrame)
            {
                return CachedDIYScenes;
            }

            List<SceneInfo> scenes = new List<SceneInfo>();
            AddDIYScenesFromRuntimeManager(scenes);
            AddDIYScenesFromFileSystem(scenes);

            CachedDIYScenes.Clear();
            CachedDIYScenes.AddRange(scenes);
            nextDIYSceneRefreshFrame = Time.frameCount + 120;
            return CachedDIYScenes;
        }

        private static void AddDIYScenesFromRuntimeManager(List<SceneInfo> scenes)
        {
            IList levelSetInfos;
            if (!TryGetDIYLevelSetInfos(out levelSetInfos))
            {
                return;
            }

            for (int i = 0; i < levelSetInfos.Count; i++)
            {
                object levelSetPair = levelSetInfos[i];
                object levelSetInfo = GetPairValue(levelSetPair);
                Array levelInfos = GetFieldValue(levelSetInfo, "levelInfos") as Array;
                if (levelInfos == null)
                {
                    continue;
                }

                string levelSetName = GetLocalizedString(levelSetInfo, "levelSetNameZH", "levelSetName");
                for (int j = 0; j < levelInfos.Length; j++)
                {
                    object levelInfo = levelInfos.GetValue(j);
                    string sceneName = GetStringField(levelInfo, "sceneName");
                    if (string.IsNullOrEmpty(sceneName))
                    {
                        continue;
                    }

                    string levelName = GetLocalizedString(levelInfo, "levelNameZH", "levelName");
                    string displayName = BuildDIYDisplayName(levelSetName, levelName, sceneName);

                    LevelConfigBase levelConfig;
                    SceneInfo scene = TryGetDIYLevelConfig(levelInfo, levelSetInfo, out levelConfig) && !IsHordeLevel(levelConfig)
                        ? BuildSceneInfo(sceneName, displayName, levelConfig)
                        : null;

                    if (scene == null)
                    {
                        SceneInfo cachedScene;
                        if (SceneCache.TryGetValue(sceneName, out cachedScene) && cachedScene != null)
                        {
                            cachedScene.DisplayName = displayName;
                            scene = cachedScene;
                        }
                    }

                    if (scene == null)
                    {
                        scene = new SceneInfo();
                        scene.SceneName = sceneName;
                        scene.DisplayName = displayName;
                    }
                    else
                    {
                        scene.DisplayName = displayName;
                    }

                    AddDIYSceneIfMissing(scenes, scene);
                }
            }
        }

        private static void AddDIYScenesFromFileSystem(List<SceneInfo> scenes)
        {
            string diyLevelsRoot = Path.Combine(Path.Combine(Paths.PluginPath, "OC2DIYLevel"), "levels");
            if (!Directory.Exists(diyLevelsRoot))
            {
                return;
            }

            string[] directories = Directory.GetDirectories(diyLevelsRoot);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string setName = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(setName))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(directory);
                for (int j = 0; j < files.Length; j++)
                {
                    string fileName = Path.GetFileName(files[j]);
                    if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("info_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SceneInfo scene = new SceneInfo();
                    scene.SceneName = fileName;
                    scene.DisplayName = BuildDIYDisplayName("DIY " + setName, fileName, fileName);

                    SceneInfo cachedScene;
                    if (SceneCache.TryGetValue(scene.SceneName, out cachedScene) && cachedScene != null)
                    {
                        cachedScene.DisplayName = scene.DisplayName;
                        scene = cachedScene;
                    }

                    AddDIYSceneIfMissing(scenes, scene);
                }
            }
        }

        private static void AddDIYSceneIfMissing(List<SceneInfo> scenes, SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName))
            {
                return;
            }

            if (scenes.Any(existing => string.Equals(existing.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            scenes.Add(scene);
        }

        private static bool TryGetDIYLevelSetInfos(out IList levelSetInfos)
        {
            levelSetInfos = null;

            Type diyLevelAssetBundleManagerType = AccessTools.TypeByName("OC2DIYLevel.DIYLevelAssetBundleManager");
            if (diyLevelAssetBundleManagerType == null)
            {
                return false;
            }

            PropertyInfo isInitializedProperty = AccessTools.Property(diyLevelAssetBundleManagerType, "IsInitialized");
            if (isInitializedProperty == null || !(bool)isInitializedProperty.GetValue(null, null))
            {
                return false;
            }

            FieldInfo levelSetInfosField = AccessTools.Field(diyLevelAssetBundleManagerType, "levelSetInfos");
            if (levelSetInfosField == null)
            {
                return false;
            }

            levelSetInfos = levelSetInfosField.GetValue(null) as IList;
            return levelSetInfos != null;
        }

        private static bool TryGetDIYLevelConfig(object levelInfo, object levelSetInfo, out LevelConfigBase levelConfig)
        {
            levelConfig = GetLevelConfigObject(levelInfo);
            if (levelConfig != null)
            {
                return true;
            }

            levelConfig = GetLevelConfigObject(levelSetInfo);
            return levelConfig != null;
        }

        private static LevelConfigBase GetLevelConfigObject(object source)
        {
            if (source == null)
            {
                return null;
            }

            Type sourceType = source.GetType();
            FieldInfo[] fields = sourceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (typeof(LevelConfigBase).IsAssignableFrom(field.FieldType))
                {
                    return field.GetValue(source) as LevelConfigBase;
                }
            }

            PropertyInfo[] properties = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || !typeof(LevelConfigBase).IsAssignableFrom(property.PropertyType))
                {
                    continue;
                }

                try
                {
                    return property.GetValue(source, null) as LevelConfigBase;
                }
                catch
                {
                }
            }

            return null;
        }

        private static string BuildDIYDisplayName(string levelSetName, string levelName, string sceneName)
        {
            string resolvedLevelSetName = string.IsNullOrEmpty(levelSetName) ? "DIY" : levelSetName;
            string resolvedLevelName = string.IsNullOrEmpty(levelName) ? sceneName : levelName;
            return resolvedLevelSetName + " - " + resolvedLevelName + " [" + sceneName + "]";
        }

        private static string GetLocalizedString(object source, string primaryFieldName, string fallbackFieldName)
        {
            string localized = GetStringField(source, primaryFieldName);
            if (!string.IsNullOrEmpty(localized))
            {
                return localized;
            }

            string fallback = GetStringField(source, fallbackFieldName);
            if (!string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }

            return null;
        }

        private static object GetPairValue(object pair)
        {
            return pair != null
                ? pair.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public).GetValue(pair, null)
                : null;
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            FieldInfo fieldInfo = instance != null ? AccessTools.Field(instance.GetType(), fieldName) : null;
            return fieldInfo != null ? fieldInfo.GetValue(instance) : null;
        }

        private static string GetStringField(object instance, string fieldName)
        {
            return GetFieldValue(instance, fieldName) as string;
        }

        private static void AddSceneFromEntry(List<SceneInfo> scenes, HashSet<string> seenScenes, SceneDirectoryData.SceneDirectoryEntry entry)
        {
            if (entry == null || IsIgnoredSceneEntry(entry))
            {
                return;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVarient = GetSceneVarient(entry);
            if (sceneVarient == null || sceneVarient.LevelConfig == null || IsHordeLevel(sceneVarient.LevelConfig) || string.IsNullOrEmpty(sceneVarient.SceneName))
            {
                return;
            }

            if (!seenScenes.Add(sceneVarient.SceneName))
            {
                return;
            }

            string displayName = MenuLevelHelper.GetLevelName(entry, false) + " [" + sceneVarient.SceneName + "]";
            SceneInfo scene = BuildSceneInfo(sceneVarient.SceneName, displayName, sceneVarient.LevelConfig);
            if (scene == null || scene.OrderedRecipes.Count == 0)
            {
                return;
            }

            scenes.Add(scene);
            SceneCache[scene.SceneName] = scene;
        }

        private static void AddScene(List<SceneInfo> scenes, HashSet<string> seenScenes, SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName) || scene.OrderedRecipes.Count == 0)
            {
                return;
            }

            if (!seenScenes.Add(scene.SceneName))
            {
                return;
            }

            scenes.Add(scene);
            SceneCache[scene.SceneName] = scene;
        }

        private static void AddCachedScenes(List<SceneInfo> scenes, HashSet<string> seenScenes)
        {
            foreach (SceneInfo scene in SceneCache.Values)
            {
                AddScene(scenes, seenScenes, scene);
            }
        }

        private static void AddDIYScenes(List<SceneInfo> scenes, HashSet<string> seenScenes)
        {
            List<SceneInfo> diyScenes = GetDIYScenes();
            for (int i = 0; i < diyScenes.Count; i++)
            {
                SceneInfo scene = diyScenes[i];
                if (scene == null || string.IsNullOrEmpty(scene.SceneName))
                {
                    continue;
                }

                if (!seenScenes.Add(scene.SceneName))
                {
                    SceneCache[scene.SceneName] = scene;
                    continue;
                }

                scenes.Add(scene);
                SceneCache[scene.SceneName] = scene;
            }
        }

        private static bool IsIgnoredSceneEntry(SceneDirectoryData.SceneDirectoryEntry entry)
        {
            return entry.Label.Contains("ThroneRoom")
                || entry.Label.Contains("Tutorial")
                || entry.Label.Contains("DLC07Battlements08");
        }

        private static bool TryGetCurrentSceneVariant(out SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVariant)
        {
            sceneVariant = null;
            GameSession gameSession = GameUtils.GetGameSession();
            if (gameSession == null || gameSession.LevelSettings == null)
            {
                return false;
            }

            sceneVariant = gameSession.LevelSettings.SceneDirectoryVarientEntry;
            return sceneVariant != null && !string.IsNullOrEmpty(sceneVariant.SceneName);
        }

        private static SceneDirectoryData GetProgressSceneDirectory()
        {
            GameSession gameSession = GameUtils.GetGameSession();
            if (gameSession == null || gameSession.Progress == null)
            {
                return null;
            }

            return gameSession.Progress.GetSceneDirectory();
        }

        private static SceneDirectoryData GetWorldMapSceneDirectory()
        {
            WorldMapFlowController worldMap = UnityEngine.Object.FindObjectOfType<WorldMapFlowController>();
            return worldMap != null ? worldMap.GetSceneDirectory() : null;
        }

        private static SceneDirectoryData GetSceneDirectory(GameSession sessionPrefab)
        {
            if (sessionPrefab == null)
            {
                return null;
            }

            GameProgress progress = sessionPrefab.gameObject.RequireComponentRecursive<GameProgress>();
            return progress != null ? progress.GetSceneDirectory() : null;
        }

        private static List<GameSession> GetFrontendSessionPrefabs(GameSession.GameType gameType)
        {
            List<GameSession> sessions = new List<GameSession>();
            T17FrontendFlow frontendFlow = T17FrontendFlow.Instance;
            if (frontendFlow == null)
            {
                return sessions;
            }

            string fieldName = gameType == GameSession.GameType.Competitive ? "m_CompetitiveGameSessionPrefabs" : "m_CoopGameSessionPrefabs";
            System.Reflection.FieldInfo field = AccessTools.Field(typeof(T17FrontendFlow), fieldName);
            object dataContainer = field != null ? field.GetValue(frontendFlow) : null;
            if (dataContainer == null)
            {
                return sessions;
            }

            System.Reflection.PropertyInfo allDataProperty = dataContainer.GetType().GetProperty("AllData");
            GameSession[] allData = allDataProperty != null ? allDataProperty.GetValue(dataContainer, null) as GameSession[] : null;
            if (allData == null)
            {
                return sessions;
            }

            for (int i = 0; i < allData.Length; i++)
            {
                GameSession session = allData[i];
                if (session != null && IsSessionAvailable(session))
                {
                    sessions.Add(session);
                }
            }

            return sessions;
        }

        private static bool IsSessionAvailable(GameSession session)
        {
            if (session == null || session.DLC < 0)
            {
                return session != null;
            }

            DLCManager dlcManager = UnityEngine.Object.FindObjectOfType<DLCManager>();
            if (dlcManager == null || dlcManager.AllDlc == null)
            {
                return true;
            }

            for (int i = 0; i < dlcManager.AllDlc.Count; i++)
            {
                DLCFrontendData dlc = dlcManager.AllDlc[i];
                if (dlc != null && dlc.m_DLCID == session.DLC)
                {
                    return dlcManager.IsDLCAvailable(dlc);
                }
            }

            return true;
        }

        private static SceneDirectoryData.PerPlayerCountDirectoryEntry GetSceneVarient(SceneDirectoryData.SceneDirectoryEntry entry)
        {
            int playerCount = 1;
            if (ServerUserSystem.m_Users != null && ServerUserSystem.m_Users.Count > 0)
            {
                playerCount = ServerUserSystem.m_Users.Count;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVarient = entry.GetSceneVarient(playerCount);
            if (sceneVarient == null && entry.SceneVarients != null && entry.SceneVarients.Length > 0)
            {
                sceneVarient = entry.SceneVarients[0];
            }

            return sceneVarient;
        }

        private static SceneInfo BuildSceneInfo(string sceneName, string displayName, LevelConfigBase levelConfig)
        {
            if (levelConfig == null)
            {
                return null;
            }

            SceneInfo scene = new SceneInfo();
            scene.SceneName = sceneName;
            scene.DisplayName = displayName;

            List<OrderDefinitionNode> recipes = levelConfig.GetAllRecipes();
            if (recipes == null)
            {
                return scene;
            }

            for (int i = 0; i < recipes.Count; i++)
            {
                EnsureRecipe(scene, recipes[i]);
            }

            CampaignLevelConfigBase campaignLevelConfig = levelConfig as CampaignLevelConfigBase;
            if (campaignLevelConfig != null)
            {
                DynamicRoundData dynamicRoundData = campaignLevelConfig.GetRoundData() as DynamicRoundData;
                if (dynamicRoundData != null && dynamicRoundData.Phases != null && dynamicRoundData.Phases.Length > 0)
                {
                    scene.PhaseRecipeIds = new List<int>[dynamicRoundData.Phases.Length];
                    for (int i = 0; i < dynamicRoundData.Phases.Length; i++)
                    {
                        scene.PhaseRecipeIds[i] = new List<int>();
                        RecipeList phaseRecipes = dynamicRoundData.Phases[i].Recipes;
                        if (phaseRecipes == null || phaseRecipes.m_recipes == null)
                        {
                            continue;
                        }

                        for (int j = 0; j < phaseRecipes.m_recipes.Length; j++)
                        {
                            RecipeList.Entry entry = phaseRecipes.m_recipes[j];
                            if (entry != null && entry.m_order != null)
                            {
                                scene.PhaseRecipeIds[i].Add(entry.m_order.m_uID);
                            }
                        }
                    }
                }
            }

            return scene;
        }

        private static void EnsureRecipe(SceneInfo scene, OrderDefinitionNode recipe)
        {
            if (scene == null || recipe == null || scene.RecipesById.ContainsKey(recipe.m_uID))
            {
                return;
            }

            RecipeInfo info = new RecipeInfo();
            info.Id = recipe.m_uID;
            info.InternalName = recipe.name;
            info.EnglishName = DishNameCatalog.GetEnglishName(recipe.name);
            info.ChineseName = DishNameCatalog.GetChineseFullName(recipe.name);
            scene.RecipesById.Add(info.Id, info);
            scene.OrderedRecipes.Add(info);
            scene.AllRecipeIds.Add(info.Id);
            DishNameCatalog.RecordRecipe(scene.SceneName, recipe.m_uID, recipe.name);
        }

        private static bool TryGetCurrentSceneInfo(out SceneInfo scene)
        {
            scene = null;
            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVariant;
            if (!TryGetCurrentSceneVariant(out sceneVariant))
            {
                return false;
            }

            string sceneName = sceneVariant.SceneName;
            LevelConfigBase levelConfig = sceneVariant.LevelConfig ?? GameUtils.GetLevelConfig();
            if (SceneCache.TryGetValue(sceneName, out scene)
                && scene != null
                && (scene.OrderedRecipes.Count > 0 || levelConfig == null))
            {
                return true;
            }

            if (levelConfig == null || IsHordeLevel(levelConfig))
            {
                return false;
            }

            string displayName = scene != null && !string.IsNullOrEmpty(scene.DisplayName) ? scene.DisplayName : sceneName;
            scene = BuildSceneInfo(sceneName, displayName, levelConfig);
            if (scene != null)
            {
                SceneCache[scene.SceneName] = scene;
            }

            return scene != null;
        }

        private static RunInfo EnsureRun(SceneInfo scene)
        {
            if (currentRun == null || !string.Equals(currentRun.SceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                currentRun = new RunInfo();
                currentRun.SceneName = scene.SceneName;
                currentRun.CurrentPhaseIndex = 0;
                InitializeRunCounts(currentRun, scene);
                return currentRun;
            }

            if (currentRun.AddedCounts.Count < scene.OrderedRecipes.Count || currentRun.ServedCounts.Count < scene.OrderedRecipes.Count)
            {
                InitializeRunCounts(currentRun, scene);
            }

            return currentRun;
        }

        private static void InitializeRunCounts(RunInfo run, SceneInfo scene)
        {
            if (run == null || scene == null)
            {
                return;
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                int recipeId = scene.OrderedRecipes[i].Id;
                if (!run.AddedCounts.ContainsKey(recipeId))
                {
                    run.AddedCounts.Add(recipeId, 0);
                }

                if (!run.ServedCounts.ContainsKey(recipeId))
                {
                    run.ServedCounts.Add(recipeId, 0);
                }
            }
        }

        private static int GetCount(Dictionary<int, int> counts, int recipeId)
        {
            int value;
            return counts != null && counts.TryGetValue(recipeId, out value) ? value : 0;
        }

        private static bool IsHordeLevel(LevelConfigBase levelConfig)
        {
            return levelConfig != null && string.Equals(levelConfig.GetType().Name, "HordeLevelConfig", StringComparison.Ordinal);
        }

        private static void ResetProbabilityState(int phaseIndex)
        {
            if (currentRun == null)
            {
                return;
            }

            currentRun.CurrentPhaseIndex = Math.Max(0, phaseIndex);
            currentRun.TotalAdded = 0;
            currentRun.AddedCounts.Clear();
            InvalidateOverlay();
        }

        private static string BuildOverlayText()
        {
            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene.OrderedRecipes.Count == 0)
            {
                return string.Empty;
            }

            RunInfo run = EnsureRun(scene);
            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            Dictionary<int, double> probabilityByRecipeId = BuildProbabilityMap(scene, run);
            List<OverlayRow> rows = OverlayRowsBuffer;
            rows.Clear();
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (!IsTracked(scene, recipe.Id))
                {
                    continue;
                }

                OverlayRow row = new OverlayRow();
                row.Recipe = recipe;
                row.Probability = GetProbability(probabilityByRecipeId, recipe.Id);
                row.Served = GetCount(run.ServedCounts, recipe.Id);
                row.OnMenu = GetCount(currentMenuCounts, recipe.Id);
                rows.Add(row);
            }

            bool chinese = UseChinese();
            if (rows.Count == 0)
            {
                return chinese
                    ? "历史菜单追踪\n当前关卡没有勾选任何追踪菜品。\n请在 F1 配置窗口的 OC2MenuManager 标签页里勾选需要追踪的菜品。"
                    : "Menu History Tracker\nNo dishes are tracked for this scene.\nUse the OC2MenuManager tab in the F1 config window to choose tracked dishes.";
            }

            rows.Sort(delegate(OverlayRow a, OverlayRow b)
            {
                int onMenuCompare = b.OnMenu.CompareTo(a.OnMenu);
                if (onMenuCompare != 0)
                {
                    return onMenuCompare;
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
            });

            StringBuilder builder = OverlayTextBuilder;
            builder.Length = 0;
            builder.Append(TruncateWithEllipsis(GetOverlaySceneLabel(scene), MaxOverlaySceneDisplayLength)).Append(" | ");
            builder.Append(chinese ? "已追踪 " : "Tracking ");
            builder.Append(rows.Count).Append('/').Append(scene.OrderedRecipes.Count).Append('\n');
            builder.Append(chinese ? "排序: 在单 > 概率 > 上单" : "Rank: Menu > Prob > Served").Append('\n');
            builder.Append(WrapRichValue(chinese ? "蓝=上单" : "Blue=Served", overlayServedValueColor != null ? overlayServedValueColor.Value : new Color(0.58f, 0.84f, 1f, 1f)));
            builder.Append(" | ");
            builder.Append(WrapRichValue(chinese ? "金=概率" : "Gold=Prob", overlayProbabilityValueColor != null ? overlayProbabilityValueColor.Value : new Color(1f, 0.84f, 0.40f, 1f)));
            builder.Append(" | ");
            builder.Append(WrapRichValue(chinese ? "绿=在单" : "Green=Menu", new Color(0.48f, 0.92f, 0.52f, 1f)));
            builder.Append('\n');

            int maxRows = Math.Min(rows.Count, Math.Max(1, overlayMaxDisplayDishes != null ? overlayMaxDisplayDishes.Value : 12));
            for (int i = 0; i < maxRows; i++)
            {
                OverlayRow row = rows[i];
                builder.Append(i + 1).Append(". ");
                builder.Append(TruncateWithEllipsis(GetRecipeDisplayName(row.Recipe), MaxOverlayDishDisplayLength));
                builder.Append("  ");
                builder.Append(WrapRichValue(row.Served.ToString(), overlayServedValueColor != null ? overlayServedValueColor.Value : new Color(0.58f, 0.84f, 1f, 1f)));
                builder.Append("   ");
                builder.Append(WrapRichValue((row.Probability * 100d).ToString("0.0") + "%", overlayProbabilityValueColor != null ? overlayProbabilityValueColor.Value : new Color(1f, 0.84f, 0.40f, 1f)));
                builder.Append("   ");
                builder.Append(WrapMenuValue(row.OnMenu));
                if (i < maxRows - 1 || rows.Count > maxRows)
                {
                    builder.Append('\n');
                }
            }

            if (rows.Count > maxRows)
            {
                builder.Append(chinese ? "+ 还有 " : "+ ").Append(rows.Count - maxRows);
                builder.Append(chinese ? " 个追踪菜品未显示" : " more tracked dishes");
            }

            return builder.ToString().TrimEnd();
        }

        private static Dictionary<int, double> BuildProbabilityMap(SceneInfo scene, RunInfo run)
        {
            Dictionary<int, double> probabilityByRecipeId = new Dictionary<int, double>();
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
            Dictionary<int, double> weightsByRecipeId = new Dictionary<int, double>();
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

            return TruncateWithEllipsis(displayName, MaxDishSelectorDisplayLength);
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

        private static string WrapMenuValue(int onMenuCount)
        {
            Color color = onMenuCount > 0
                ? new Color(0.48f, 0.92f, 0.52f, 1f)
                : new Color(0.66f, 0.66f, 0.66f, 1f);
            return WrapRichValue(onMenuCount.ToString(), color);
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

            HashSet<ClientOrderControllerBase> visitedControllers = new HashSet<ClientOrderControllerBase>();
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
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (value.Length <= MaxSceneSelectorDisplayLength)
            {
                return value;
            }

            string sceneSuffix = string.IsNullOrEmpty(sceneName) ? string.Empty : " [" + sceneName + "]";
            if (!string.IsNullOrEmpty(sceneSuffix) && sceneSuffix.Length + 4 < MaxSceneSelectorDisplayLength)
            {
                int prefixLength = MaxSceneSelectorDisplayLength - sceneSuffix.Length - 1;
                if (prefixLength > 0)
                {
                    string prefix = value.Substring(0, Math.Min(value.Length, prefixLength));
                    return TruncateWithEllipsis(prefix, prefixLength) + sceneSuffix;
                }
            }

            return TruncateMiddle(value, MaxSceneSelectorDisplayLength);
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

        private static void LoadSelections()
        {
            TrackedIdsByScene.Clear();
            if (!File.Exists(selectionFilePath))
            {
                return;
            }

            string[] lines = File.ReadAllLines(selectionFilePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(new char[] { '=' }, 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]))
                {
                    continue;
                }

                HashSet<int> ids = new HashSet<int>();
                string[] tokens = parts[1].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int j = 0; j < tokens.Length; j++)
                {
                    int id;
                    if (int.TryParse(tokens[j].Trim(), out id))
                    {
                        ids.Add(id);
                    }
                }

                TrackedIdsByScene[parts[0].Trim()] = ids;
            }
        }

        private static void SaveSelections()
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, HashSet<int>> pair in TrackedIdsByScene.OrderBy(x => x.Key))
            {
                string ids = string.Join(",", pair.Value.OrderBy(x => x).Select(x => x.ToString()).ToArray());
                lines.Add(pair.Key + "=" + ids);
            }

            File.WriteAllLines(selectionFilePath, lines.ToArray());
        }

        private static void CaptureLegacyValues()
        {
            migratedEnabledValue = TryGetLegacyValue<bool>(LegacyEnabledDefinition);
            migratedLanguageValue = TryGetLegacyValue<TrackerLanguage>(LegacyLanguageDefinition);
        }

        private static void RemoveLegacyConfigEntries()
        {
            bool removedAny = false;
            ConfigFile config = _MODEntry.Instance.Config;

            if (config.Remove(LegacySelectedSceneStateDefinition))
            {
                removedAny = true;
            }

            for (int i = 0; i < LegacyConfigDefinitions.Length; i++)
            {
                if (config.Remove(LegacyConfigDefinitions[i]))
                {
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                config.Save();
            }
        }

        private static void RemoveGeneratedConfigEntries()
        {
            bool removedAny = false;
            ConfigFile config = _MODEntry.Instance.Config;
            List<ConfigDefinition> definitions = config.Keys.ToList();
            for (int i = 0; i < definitions.Count; i++)
            {
                ConfigDefinition definition = definitions[i];
                if (definition == null)
                {
                    continue;
                }

                string section = definition.Section ?? string.Empty;
                if (string.Equals(section, DishSelectionSection, StringComparison.Ordinal)
                    || section.StartsWith("05-历史菜单追踪 - ", StringComparison.Ordinal))
                {
                    if (config.Remove(definition))
                    {
                        removedAny = true;
                    }
                }
            }

            if (removedAny)
            {
                config.Save();
            }
        }

        private static T? TryGetLegacyValue<T>(ConfigDefinition definition) where T : struct
        {
            ConfigFile config = _MODEntry.Instance.Config;
            if (!config.Keys.Contains(definition))
            {
                return null;
            }

            ConfigEntryBase entry = config[definition];
            if (entry == null || entry.BoxedValue == null)
            {
                return null;
            }

            try
            {
                if (entry.BoxedValue is T)
                {
                    return (T)entry.BoxedValue;
                }

                if (typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), entry.BoxedValue.ToString(), true);
                }

                return (T)Convert.ChangeType(entry.BoxedValue, typeof(T));
            }
            catch
            {
                return null;
            }
        }
    }
}
