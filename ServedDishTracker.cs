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
            public string CategoryName;
            public int CategoryTier;
            public OrderDefinitionNode Definition;
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

        private sealed class PreparedSourceState
        {
            public int InstanceId;
            public int GameObjectInstanceId;
            public Component Component;
            public IClientOrderDefinition Provider;
            public OrderCompositionChangedCallback Callback;
            public int MatchedRecipeId;
        }

        private sealed class OverlayDisplay : DebugDisplay
        {
            private static readonly Color PanelBackgroundColor = new Color(0f, 0f, 0f, 0.58f);
            private const float PanelPadding = 10f;
            private readonly GUIStyle textStyle = new GUIStyle();
            private string cachedText = string.Empty;

            public override void OnSetUp()
            {
            }

            public override void OnUpdate()
            {
                if (!overlayDirty || Time.frameCount < nextOverlayRefreshFrame)
                {
                    return;
                }

                cachedText = BuildOverlayText();
                overlayDirty = false;
                nextOverlayRefreshFrame = 0;
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

                textStyle.alignment = style.alignment;
                textStyle.font = style.font;
                textStyle.fontSize = style.fontSize;
                textStyle.fontStyle = style.fontStyle;
                textStyle.richText = true;
                textStyle.wordWrap = false;
                textStyle.clipping = TextClipping.Clip;
                textStyle.normal.textColor = style.normal.textColor;

                Rect contentRect = new Rect(
                    rect.x + PanelPadding,
                    rect.y + PanelPadding,
                    Mathf.Max(1f, rect.width - PanelPadding * 2f),
                    Mathf.Max(1f, rect.height - PanelPadding * 2f));

                if (OverlayRenderRowsBuffer.Count == 0)
                {
                    GUI.Label(contentRect, cachedText, textStyle);
                    return;
                }

                float rowHeight = Mathf.Max(16f, textStyle.CalcSize(new GUIContent("A")).y + 4f);
                float y = contentRect.y;
                if (!string.IsNullOrEmpty(overlayHeaderText))
                {
                    float headerHeight = Mathf.Max(rowHeight, textStyle.CalcHeight(new GUIContent(overlayHeaderText), contentRect.width));
                    GUI.Label(new Rect(contentRect.x, y, contentRect.width, headerHeight), overlayHeaderText, textStyle);
                    y += headerHeight + 2f;
                }

                for (int i = 0; i < OverlayRenderRowsBuffer.Count; i++)
                {
                    OverlayRenderRow row = OverlayRenderRowsBuffer[i];
                    if (row == null || string.IsNullOrEmpty(row.Text))
                    {
                        continue;
                    }

                    Rect rowRect = new Rect(contentRect.x, y, contentRect.width, rowHeight);
                    if (row.HasBackground)
                    {
                        Color previousColor = GUI.color;
                        GUI.color = row.BackgroundColor;
                        GUI.DrawTexture(rowRect, Texture2D.whiteTexture);
                        GUI.color = previousColor;
                    }

                    Color previousTextColor = GUI.color;
                    GUI.color = row.TextTint;
                    GUI.Label(rowRect, row.Text, textStyle);
                    if (row.HasStrikeThrough)
                    {
                        float strikeY = rowRect.y + Mathf.Floor(rowRect.height * 0.56f);
                        Rect strikeRect = new Rect(rowRect.x + 34f, strikeY, Mathf.Max(8f, rowRect.width - 40f), 2f);
                        GUI.DrawTexture(strikeRect, Texture2D.whiteTexture);
                    }
                    GUI.color = previousTextColor;
                    y += rowHeight + 1f;
                }

                if (!string.IsNullOrEmpty(overlayFooterText) && y < contentRect.yMax)
                {
                    float footerHeight = Mathf.Max(rowHeight, textStyle.CalcHeight(new GUIContent(overlayFooterText), contentRect.width));
                    GUI.Label(new Rect(contentRect.x, y + 1f, contentRect.width, footerHeight), overlayFooterText, textStyle);
                }
            }
        }

        private static readonly Color SettingsWindowBodyColor = new Color(0.10f, 0.10f, 0.10f, 0.96f);
        private static readonly Color SettingsWindowHeaderColor = new Color(0.17f, 0.17f, 0.17f, 0.98f);

        private sealed class OverlayRow
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
            public int Prepared;
            public int OnMenu;
            public int EarliestMenuOrder;

            public void Reset()
            {
                Recipe = null;
                Probability = 0d;
                Served = 0;
                Prepared = 0;
                OnMenu = 0;
                EarliestMenuOrder = int.MaxValue;
            }
        }

        private sealed class OverlayRenderRow
        {
            public string Text;
            public Color BackgroundColor;
            public bool HasBackground;
            public Color TextTint = Color.white;
            public bool HasStrikeThrough;

            public void Reset()
            {
                Text = string.Empty;
                BackgroundColor = Color.clear;
                HasBackground = false;
                TextTint = Color.white;
                HasStrikeThrough = false;
            }
        }

        private sealed class CategorySelectionGroup
        {
            public string CategoryName;
            public int CategoryTier;
            public readonly List<int> RecipeIds = new List<int>();
        }

        private sealed class TicketWidgetState
        {
            public int InstanceId;
            public int RecipeId;
            public int Order;
            public RecipeWidgetUIController Widget;
            public RecipeWidgetTile.DisplayConfiguration DisplayConfig;
            public TopRecipeWidgetTile.TopDisplayConfiguration TopDisplayConfig;
            public Color OriginalDisplayTint;
            public Color OriginalTopTint;
            public Image[] CachedImages;
            public Color AppliedDisplayTint;
            public Color AppliedTopTint;
            public bool HasAppliedTint;
        }

        private const string TrackerSection = "03-历史菜单追踪";
        private const string DishSelectionSection = "04-历史菜单菜品";
        private const string SceneSelectorKey = "选择关卡";
        private const string NoSceneSelectorValue = "暂无可选关卡";
        private const string SettingsWindowHotkeyKey = "打开菜单管理窗口";
        private const int MaxSceneSelectorDisplayLength = 40;
        private const int MaxDishSelectorDisplayLength = 26;
        private const int MaxOverlaySceneDisplayLength = 24;
        private const int MaxOverlayDishDisplayLength = 12;
        private const int SettingsWindowId = 49271;
        private const float SettingsWindowDefaultWidth = 860f;
        private const float SettingsWindowDefaultHeight = 760f;
        private const float SettingsWindowMinWidth = 760f;
        private const float SettingsWindowMinHeight = 620f;
        private const float SettingsWindowMargin = 24f;
        private const float SettingsLabelWidth = 210f;
        private const float SettingsDescriptionWidth = 560f;
        private const float SettingsActionButtonWidth = 66f;
        private const float SceneDropdownMaxHeight = 180f;
        private const int SceneRefreshIntervalInRound = 600;
        private const int SceneRefreshIntervalInRoundWithConfigOpen = 30;
        private const int SceneRefreshIntervalOutOfRound = 20;
        private const int DiscoveryFlushIntervalFrames = 900;
        private const int ControllerLookupIntervalFrames = 180;
        private const int ControllerLookupRetryIntervalFrames = 20;
        private const int OverlayRefreshIntervalFrames = 24;
        private const int PreparedSourceRefreshIntervalFrames = 30;
        private const int MaxPreparedSourceRefreshesPerBatch = 1;
        private const int PreparedSourcePruneIntervalFrames = 3600;
        private const int PreparedBootstrapStepIntervalFrames = 60;
        private const int PreparedBootstrapFallbackDelayFrames = 240;
        private const int PreparedBootstrapFallbackIntervalFrames = 900;
        private const int TicketWidgetRefreshDelayFrames = 24;
        private const int TicketWidgetRetryIntervalFrames = 60;
        private const int HotkeyFilePollIntervalFrames = 7200;
        private const KeyCode DefaultSettingsWindowHotkey = KeyCode.F6;

        private static readonly Dictionary<string, HashSet<int>> TrackedIdsByScene = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SceneInfo> SceneCache = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, int> CurrentOnMenuCounts = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> PreparedCountsByRecipe = new Dictionary<int, int>();
        private static readonly Dictionary<int, PreparedSourceState> PreparedSourcesByInstanceId = new Dictionary<int, PreparedSourceState>();
        private static readonly Dictionary<int, int> PreparedSourceIdsByGameObjectId = new Dictionary<int, int>();
        private static readonly Dictionary<int, CookedCompositeOrderNode.CookingProgress> PreparedCookStateBySourceId = new Dictionary<int, CookedCompositeOrderNode.CookingProgress>();
        private static readonly Dictionary<int, ClientCookableContainer> PreparedCookableByHandlerId = new Dictionary<int, ClientCookableContainer>();
        private static readonly Dictionary<int, TicketWidgetState> TicketWidgetsByInstanceId = new Dictionary<int, TicketWidgetState>();
        private static readonly List<SceneInfo> KnownScenes = new List<SceneInfo>();
        private static readonly List<SceneInfo> CachedDIYScenes = new List<SceneInfo>();
        private static readonly List<string> OrderedSceneSelectorValues = new List<string>();
        private static readonly List<OverlayRow> OverlayRowsBuffer = new List<OverlayRow>();
        private static readonly List<OverlayRenderRow> OverlayRenderRowsBuffer = new List<OverlayRenderRow>();
        private static readonly List<CategorySelectionGroup> CategorySelectionGroupsBuffer = new List<CategorySelectionGroup>();
        private static readonly List<TicketWidgetState> TicketWidgetsBuffer = new List<TicketWidgetState>();
        private static readonly HashSet<int> DirtyPreparedSourceIds = new HashSet<int>();
        private static readonly List<int> PreparedSourceRefreshBuffer = new List<int>();
        private static readonly List<int> PreparedSourceRemovalBuffer = new List<int>();
        private static readonly Dictionary<string, string> SceneSelectorValuesByScene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> SceneNamesBySelectorValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, double> ProbabilityByRecipeBuffer = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> ProbabilityWeightsByRecipeBuffer = new Dictionary<int, double>();
        private static readonly Dictionary<int, int> PreparedRemainingByRecipeBuffer = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> MenuOrderByRecipeBuffer = new Dictionary<int, int>();
        private static readonly List<int> PreparedCandidateRecipeIdsBuffer = new List<int>();
        private static readonly StringBuilder OverlayTextBuilder = new StringBuilder(768);
        private static readonly TeamID[] TeamIds = (TeamID[])Enum.GetValues(typeof(TeamID));

        private static readonly ConfigDefinition LegacyEnabledDefinition = new ConfigDefinition("03-已送菜品追踪", "启用已送菜品追踪");
        private static readonly ConfigDefinition LegacyLanguageDefinition = new ConfigDefinition("03-已送菜品追踪", "显示语言");
        private static readonly ConfigDefinition LegacySelectedSceneStateDefinition = new ConfigDefinition("99-内部", "已选关卡内部状态");
        private static readonly ConfigDefinition LegacySettingsWindowHotkeyDefinition = new ConfigDefinition("00-菜单管理", SettingsWindowHotkeyKey);
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
        private static ConfigEntry<bool> preparedTrackingEnabled;
        private static ConfigEntry<bool> menuTicketTintEnabled;
        private static ConfigEntry<TrackerLanguage> languageMode;
        private static ConfigEntry<int> overlayX;
        private static ConfigEntry<int> overlayY;
        private static ConfigEntry<int> overlayWidth;
        private static ConfigEntry<int> overlayHeight;
        private static ConfigEntry<int> overlayFontSize;
        private static ConfigEntry<Color> overlayFontColor;
        private static ConfigEntry<Color> overlayServedValueColor;
        private static ConfigEntry<Color> overlayProbabilityValueColor;
        private static ConfigEntry<Color> overlayPreparedValueColor;
        private static ConfigEntry<Color> menuTicketOnMenuTintColor;
        private static ConfigEntry<Color> menuTicketPreparedTintColor;
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
        private static string preferredSceneName = string.Empty;
        private static string selectedSceneName = string.Empty;
        private static bool? migratedEnabledValue;
        private static TrackerLanguage? migratedLanguageValue;
        private static readonly FieldInfo ActiveOrdersField = typeof(ClientOrderControllerBase).GetField("m_activeOrders", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ActiveOrderType = typeof(ClientOrderControllerBase).GetNestedType("ActiveOrder", BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveOrderRecipeListEntryField = ActiveOrderType != null
            ? ActiveOrderType.GetField("RecipeListEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo RecipeWidgetDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_displayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetTopDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_topDisplayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static ClientFlowControllerBase cachedClientFlowController;
        private static int nextClientFlowLookupFrame;
        private static ClientKitchenFlowControllerBase cachedKitchenFlowController;
        private static int nextKitchenFlowLookupFrame;
        private static string currentOnMenuCountsSceneName = string.Empty;
        private static bool currentOnMenuCountsDirty = true;
        private static int nextTrackedSceneRefreshPollFrame;
        private static int nextDiscoveryFlushFrame;
        private static int nextPreparedSourceRefreshFrame;
        private static int nextPreparedSourcePruneFrame;
        private static int nextPreparedBootstrapFrame;
        private static int nextPreparedBootstrapFallbackFrame;
        private static int nextTicketWidgetRefreshFrame;
        private static int nextHotkeyFilePollFrame;
        private static int nextOverlayRefreshFrame;
        private static int lastOverlayBuildFrame = int.MinValue;
        private static int cachedCurrentSceneInfoFrame = int.MinValue;
        private static bool overlayVisible;
        private static bool overlayDirty = true;
        private static bool ticketWidgetsDirty = true;
        private static bool ticketWidgetTintActive;
        private static bool cachedCurrentSceneInfoValid;
        private static bool lastMenuTicketTintEnabled = true;
        private static bool preparedSourceBootstrapComplete;
        private static bool settingsWindowVisible;
        private static bool sceneDropdownExpanded;
        private static bool capturingHotkey;
        private static int lastSettingsWindowToggleFrame = int.MinValue;
        private static int preparedSourceBootstrapStage;
        private static SceneInfo cachedCurrentSceneInfo;
        private static KeyCode settingsWindowHotkey = DefaultSettingsWindowHotkey;
        private static DateTime hotkeyConfigLastWriteUtc = DateTime.MinValue;
        private static string overlayHeaderText = string.Empty;
        private static string overlayFooterText = string.Empty;
        private static string preparedSourceSceneName = string.Empty;
        private static string preparedCandidateSceneName = string.Empty;
        private static Rect settingsWindowRect = new Rect(140f, 90f, SettingsWindowDefaultWidth, SettingsWindowDefaultHeight);
        private static Vector2 settingsWindowScrollPosition = Vector2.zero;
        private static Vector2 sceneDropdownScrollPosition = Vector2.zero;
        private static bool preparedCandidateRecipeIdsDirty = true;

        public static void Awake()
        {
            selectionFilePath = Path.Combine(Paths.ConfigPath, "HostUtilities-ServedDishTrackerSelections.txt");
            CaptureLegacyValues();
            RemoveLegacyConfigEntries();
            RemoveGeneratedConfigEntries();
            RemoveLegacySettingsWindowHotkeyEntry();

            enabled = _MODEntry.SettingsConfig.Bind<bool>(
                TrackerSection,
                "启用历史菜单追踪",
                migratedEnabledValue ?? true,
                "标准关卡历史菜单追踪。先在独立菜单窗口里选择关卡，再勾选要追踪的菜品。进入关卡时会自动锁定为当前关卡。");
            preparedTrackingEnabled = _MODEntry.SettingsConfig.Bind<bool>(
                TrackerSection,
                "启用已备跟踪",
                true,
                "跟踪已完成但尚未上菜的成品。这个功能开销更高，默认开启。");
            menuTicketTintEnabled = _MODEntry.SettingsConfig.Bind<bool>(
                TrackerSection,
                "菜单颜色",
                true,
                "是否给关卡里的菜单栏上色。关闭后可以进一步降低运行开销。");
            menuTicketOnMenuTintColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "菜单票据在单颜色",
                new Color(1f, 0.76f, 0.34f, 1f),
                "菜单栏里“在单未备”的底色。");
            menuTicketPreparedTintColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "菜单票据已备颜色",
                new Color(0.86f, 0.98f, 0.86f, 1f),
                "菜单栏里“已备”的底色。");
            languageMode = _MODEntry.SettingsConfig.Bind<TrackerLanguage>(
                TrackerSection,
                "显示语言",
                migratedLanguageValue ?? TrackerLanguage.Auto,
                "Auto / English / Chinese");
            overlayX = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗X",
                40,
                new ConfigDescription("历史菜单追踪悬浮窗左上角 X 坐标。默认在左侧中部。", new AcceptableValueRange<int>(0, 4000)));
            overlayY = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗Y",
                300,
                new ConfigDescription("历史菜单追踪悬浮窗左上角 Y 坐标。", new AcceptableValueRange<int>(0, 4000)));
            overlayWidth = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗宽度",
                280,
                new ConfigDescription("历史菜单追踪悬浮窗宽度。", new AcceptableValueRange<int>(240, 1600)));
            overlayHeight = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗高度",
                340,
                new ConfigDescription("历史菜单追踪悬浮窗高度。", new AcceptableValueRange<int>(120, 1600)));
            overlayFontSize = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗字体大小",
                15,
                new ConfigDescription("历史菜单追踪悬浮窗字体大小。", new AcceptableValueRange<int>(8, 48)));
            overlayFontColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "悬浮窗字体颜色",
                new Color(1f, 1f, 1f, 1f),
                "历史菜单追踪悬浮窗字体颜色。");
            overlayServedValueColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "悬浮窗上单数量颜色",
                new Color(0.58f, 0.84f, 1f, 1f),
                "历史菜单追踪悬浮窗中“上单数量”数值的颜色。");
            overlayProbabilityValueColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "悬浮窗概率颜色",
                new Color(1f, 0.84f, 0.40f, 1f),
                "历史菜单追踪悬浮窗中“概率”数值的颜色。");
            overlayPreparedValueColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "悬浮窗已备颜色",
                new Color(1f, 0.56f, 0.76f, 1f),
                "历史菜单追踪悬浮窗中“已备”数值的颜色。");
            overlayBoldFont = _MODEntry.SettingsConfig.Bind<bool>(
                TrackerSection,
                "悬浮窗粗体",
                false,
                "是否使用粗体显示历史菜单追踪悬浮窗文字。");
            overlayMaxDisplayDishes = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗最大显示菜品数",
                12,
                new ConfigDescription("历史菜单追踪悬浮窗最多显示多少道菜。", new AcceptableValueRange<int>(1, 40)));
            overlayTextAlignment = _MODEntry.SettingsConfig.Bind<OverlayTextAlignment>(
                TrackerSection,
                "悬浮窗文本对齐",
                OverlayTextAlignment.Left,
                "Left / Right / Center");

            LoadSelections();
            InitializeHotkeyConfig();
            CenterSettingsWindow();

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
            RefreshHotkeyFromFileIfChanged();
            KeyCode hotkey = GetSettingsWindowHotkey();
            if (!capturingHotkey && hotkey != KeyCode.None && Input.GetKeyDown(hotkey))
            {
                ToggleSettingsWindowVisibility();
            }

            if (settingsWindowVisible && Input.GetKeyDown(KeyCode.Escape))
            {
                settingsWindowVisible = false;
                sceneDropdownExpanded = false;
                capturingHotkey = false;
            }

            bool inActiveRound = IsInActiveRound();
            bool shouldTintMenuTickets = IsMenuTicketTintEnabled();
            if (shouldTintMenuTickets != lastMenuTicketTintEnabled)
            {
                lastMenuTicketTintEnabled = shouldTintMenuTickets;
                InvalidateTicketWidgets();
            }

            if (IsPreparedTrackingEnabled())
            {
                RefreshPreparedState(inActiveRound);
            }
            else if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
            {
                ClearPreparedState();
                InvalidateOverlay();
            }

            if (!enabled.Value || !inActiveRound)
            {
                if (TicketWidgetsByInstanceId.Count > 0)
                {
                    ClearTicketWidgetState();
                }
            }
            else if (!shouldTintMenuTickets)
            {
                if (ticketWidgetTintActive)
                {
                    RestoreAllTicketWidgetTints();
                }

                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
            }
            else if (ticketWidgetsDirty)
            {
                if (Time.frameCount >= nextTicketWidgetRefreshFrame)
                {
                    RefreshTicketWidgetTints();
                }
            }

            overlayVisible = ShouldShowOverlay(inActiveRound);
            if (overlayVisible && overlayDirty && Time.frameCount >= nextOverlayRefreshFrame)
            {
                overlayHost.Update();
            }

            int sceneRefreshInterval = inActiveRound
                ? (settingsWindowVisible ? SceneRefreshIntervalInRoundWithConfigOpen : SceneRefreshIntervalInRound)
                : SceneRefreshIntervalOutOfRound;
            bool shouldMaintainConfigUi = settingsWindowVisible;

            if (shouldMaintainConfigUi && Time.frameCount >= nextTrackedSceneRefreshPollFrame)
            {
                try
                {
                    RefreshKnownScenes(false);
                    SyncTrackingConfigEntries();
                    nextTrackedSceneRefreshPollFrame = Time.frameCount + sceneRefreshInterval;
                }
                catch (Exception ex)
                {
                    string errorText = ex.GetType().Name + ": " + ex.Message;
                    if (!string.Equals(lastConfigSyncError, errorText, StringComparison.Ordinal))
                    {
                        lastConfigSyncError = errorText;
                        _MODEntry.LogError("[ServedDishTracker] Failed to refresh tracking config entries: " + ex);
                    }

                    nextTrackedSceneRefreshPollFrame = Time.frameCount + sceneRefreshInterval;
                }
            }

            if (!inActiveRound && Time.frameCount >= nextDiscoveryFlushFrame)
            {
                try
                {
                    DishNameCatalog.FlushDiscoveryReport();
                }
                catch (Exception ex)
                {
                    _MODEntry.LogWarning("[ServedDishTracker] Failed to flush discovery report: " + ex.GetType().Name + ": " + ex.Message);
                }

                nextDiscoveryFlushFrame = Time.frameCount + DiscoveryFlushIntervalFrames;
            }
        }

        public static void OnGUI()
        {
            Event currentEvent = Event.current;
            bool isRepaintEvent = currentEvent == null || currentEvent.type == EventType.Repaint;
            if (overlayVisible && isRepaintEvent)
            {
                overlayHost.OnGUI();
            }

            if (!settingsWindowVisible)
            {
                return;
            }

            KeyCode hotkey = GetSettingsWindowHotkey();
            if (capturingHotkey && currentEvent != null && currentEvent.isKey && currentEvent.type == EventType.KeyDown)
            {
                if (currentEvent.keyCode != KeyCode.None)
                {
                    settingsWindowHotkey = currentEvent.keyCode;
                    SaveHotkeyConfig();
                }
                capturingHotkey = false;
                currentEvent.Use();
            }
            else if (!capturingHotkey
                && currentEvent != null
                && currentEvent.type == EventType.KeyDown
                && currentEvent.keyCode == hotkey
                && hotkey != KeyCode.None)
            {
                ToggleSettingsWindowVisibility();
                currentEvent.Use();
            }

            EnsureSettingsWindowRect();
            Color previousColor = GUI.color;
            Color previousBackgroundColor = GUI.backgroundColor;
            Color previousContentColor = GUI.contentColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            settingsWindowRect = GUI.Window(SettingsWindowId, settingsWindowRect, DrawSettingsWindow, "菜单管理");
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;
        }

        private static KeyCode GetSettingsWindowHotkey()
        {
            return settingsWindowHotkey;
        }

        private static void ToggleSettingsWindowVisibility()
        {
            if (lastSettingsWindowToggleFrame == Time.frameCount)
            {
                return;
            }

            lastSettingsWindowToggleFrame = Time.frameCount;
            settingsWindowVisible = !settingsWindowVisible;
            sceneDropdownExpanded = false;
            if (settingsWindowVisible)
            {
                nextTrackedSceneRefreshPollFrame = 0;
            }
        }

        private static void InitializeHotkeyConfig()
        {
            settingsWindowHotkey = DefaultSettingsWindowHotkey;
            EnsureHotkeyConfigFileExists();
            RefreshHotkeyFromFile(true);
        }

        private static void RefreshHotkeyFromFileIfChanged()
        {
            if (string.IsNullOrEmpty(_MODEntry.HotkeyConfigPath) || Time.frameCount < nextHotkeyFilePollFrame)
            {
                return;
            }

            RefreshHotkeyFromFile(false);
        }

        private static void RefreshHotkeyFromFile(bool force)
        {
            string path = _MODEntry.HotkeyConfigPath;
            if (string.IsNullOrEmpty(path))
            {
                settingsWindowHotkey = DefaultSettingsWindowHotkey;
                nextHotkeyFilePollFrame = Time.frameCount + HotkeyFilePollIntervalFrames;
                return;
            }

            if (!File.Exists(path))
            {
                EnsureHotkeyConfigFileExists();
            }

            DateTime writeTimeUtc = GetHotkeyConfigWriteTimeUtc(path);
            if (!force && writeTimeUtc == hotkeyConfigLastWriteUtc)
            {
                nextHotkeyFilePollFrame = Time.frameCount + HotkeyFilePollIntervalFrames;
                return;
            }

            KeyCode parsedHotkey;
            if (TryParseHotkeyConfig(path, out parsedHotkey))
            {
                settingsWindowHotkey = parsedHotkey;
            }
            else
            {
                settingsWindowHotkey = DefaultSettingsWindowHotkey;
                _MODEntry.LogWarning("[ServedDishTracker] Invalid hotkey config, falling back to " + DefaultSettingsWindowHotkey + ": " + path);
                WriteHotkeyConfig(path, settingsWindowHotkey);
                writeTimeUtc = GetHotkeyConfigWriteTimeUtc(path);
            }

            hotkeyConfigLastWriteUtc = writeTimeUtc;
            nextHotkeyFilePollFrame = Time.frameCount + HotkeyFilePollIntervalFrames;
        }

        private static void SaveHotkeyConfig()
        {
            string path = _MODEntry.HotkeyConfigPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            WriteHotkeyConfig(path, settingsWindowHotkey);
            hotkeyConfigLastWriteUtc = GetHotkeyConfigWriteTimeUtc(path);
            nextHotkeyFilePollFrame = Time.frameCount + HotkeyFilePollIntervalFrames;
        }

        private static void EnsureHotkeyConfigFileExists()
        {
            string path = _MODEntry.HotkeyConfigPath;
            if (string.IsNullOrEmpty(path) || File.Exists(path))
            {
                return;
            }

            WriteHotkeyConfig(path, settingsWindowHotkey);
            hotkeyConfigLastWriteUtc = GetHotkeyConfigWriteTimeUtc(path);
        }

        private static void WriteHotkeyConfig(string path, KeyCode hotkey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new string[]
            {
                "# OC2MenuManager hotkey config",
                "# Edit the value after Hotkey= and save the file.",
                "# Valid values use Unity KeyCode names, for example: F6, F7, Alpha1, Keypad1, Home.",
                "# Use Hotkey=None if you want to disable the launch hotkey.",
                "# The mod will reload this file automatically within a few seconds.",
                "Hotkey=" + hotkey
            });
        }

        private static bool TryParseHotkeyConfig(string path, out KeyCode hotkey)
        {
            hotkey = DefaultSettingsWindowHotkey;
            if (!File.Exists(path))
            {
                return false;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null)
                {
                    continue;
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0
                    || trimmed.StartsWith("#", StringComparison.Ordinal)
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = trimmed;
                int separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex >= 0)
                {
                    string key = trimmed.Substring(0, separatorIndex).Trim();
                    if (!string.Equals(key, "Hotkey", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = trimmed.Substring(separatorIndex + 1).Trim();
                }

                try
                {
                    KeyCode parsed = (KeyCode)Enum.Parse(typeof(KeyCode), value, true);
                    hotkey = parsed;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static DateTime GetHotkeyConfigWriteTimeUtc(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static void CenterSettingsWindow()
        {
            float width = Mathf.Max(SettingsWindowMinWidth, SettingsWindowDefaultWidth * Mathf.Max(1f, _MODEntry.dpiScaleFactor));
            float height = Mathf.Max(SettingsWindowMinHeight, SettingsWindowDefaultHeight * Mathf.Max(1f, _MODEntry.dpiScaleFactor));
            float x = Mathf.Max(SettingsWindowMargin, (Screen.width - width) * 0.5f);
            float y = Mathf.Max(SettingsWindowMargin, (Screen.height - height) * 0.5f);
            settingsWindowRect = new Rect(x, y, width, height);
        }

        private static void EnsureSettingsWindowRect()
        {
            if (settingsWindowRect.width < SettingsWindowMinWidth || settingsWindowRect.height < SettingsWindowMinHeight)
            {
                settingsWindowRect.width = Mathf.Max(SettingsWindowMinWidth, settingsWindowRect.width);
                settingsWindowRect.height = Mathf.Max(SettingsWindowMinHeight, settingsWindowRect.height);
            }

            float maxX = Mathf.Max(SettingsWindowMargin, Screen.width - settingsWindowRect.width - SettingsWindowMargin);
            float maxY = Mathf.Max(SettingsWindowMargin, Screen.height - settingsWindowRect.height - SettingsWindowMargin);
            settingsWindowRect.x = Mathf.Clamp(settingsWindowRect.x, SettingsWindowMargin, maxX);
            settingsWindowRect.y = Mathf.Clamp(settingsWindowRect.y, SettingsWindowMargin, maxY);
        }

        private static void DrawSettingsWindow(int windowId)
        {
            Color previousColor = GUI.color;
            Color previousBackgroundColor = GUI.backgroundColor;
            Color previousContentColor = GUI.contentColor;
            GUI.color = SettingsWindowBodyColor;
            GUI.DrawTexture(new Rect(4f, 24f, Mathf.Max(1f, settingsWindowRect.width - 8f), Mathf.Max(1f, settingsWindowRect.height - 28f)), Texture2D.whiteTexture);
            GUI.color = SettingsWindowHeaderColor;
            GUI.DrawTexture(new Rect(1f, 1f, Mathf.Max(1f, settingsWindowRect.width - 2f), 24f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("OC2MenuManager", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("关闭", GUILayout.Width(72f)))
            {
                settingsWindowVisible = false;
                sceneDropdownExpanded = false;
                capturingHotkey = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            settingsWindowScrollPosition = GUILayout.BeginScrollView(settingsWindowScrollPosition);
            DrawSceneAndDishSelectionSection();
            DrawTrackingSettingsSection();
            DrawFeatureToggleSection();
            DrawOverlaySettingsSection();
            DrawStandaloneUiSection();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUI.color = previousColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;

            GUI.DragWindow(new Rect(0f, 0f, settingsWindowRect.width, 28f));
        }

        private static void DrawStandaloneUiSection()
        {
            DrawSectionHeader("界面");
            DrawIntSliderRow("MOD的UI字体大小", _MODEntry.defaultFontSize, 5, 40, "独立菜单窗口与悬浮窗共用的基础字体大小。");
            DrawColorRow("MOD的UI字体颜色", _MODEntry.defaultFontColor, "独立菜单窗口与悬浮窗共用的基础字体颜色。");
            DrawHotkeyRow();
        }

        private static void DrawFeatureToggleSection()
        {
            DrawSectionHeader("功能开关");
            DrawToggleRow("麻团好菜单", MenuManager.IsCarnivalMenuGoodEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalMenuGood != null)
                {
                    MenuManager.isCarnivalMenuGood.Value = value;
                }
            }, "第一道菜没有葱，前两道菜不是蛋糕。");
            DrawToggleRow("麻团好蛋糕", MenuManager.IsCarnivalCakeGoodEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalCakeGood != null)
                {
                    MenuManager.isCarnivalCakeGood.Value = value;
                }
            }, "提高蛋糕相关菜单出现概率。");
            DrawToggleRow("麻团TAS菜单", MenuManager.IsCarnivalMenuFixedEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalMenuFixed != null)
                {
                    MenuManager.isCarnivalMenuFixed.Value = value;
                }
            }, "固定麻团菜单为 TAS 用配置。");
            DrawToggleRow("无菜单", NoMenuMode.IsEnabled, delegate(bool value)
            {
                NoMenuMode.SetEnabled(value);
            }, "启用无菜单模式。");
        }

        private static void DrawTrackingSettingsSection()
        {
            DrawSectionHeader("历史菜单追踪");
            DrawToggleRow("启用历史菜单追踪", enabled != null && enabled.Value, delegate(bool value)
            {
                if (enabled != null)
                {
                    enabled.Value = value;
                }
            }, "标准关卡历史菜单追踪。");
            DrawToggleRow("启用已备跟踪", IsPreparedTrackingEnabled(), delegate(bool value)
            {
                if (preparedTrackingEnabled != null)
                {
                    preparedTrackingEnabled.Value = value;
                    if (value)
                    {
                        if (IsInActiveRound())
                        {
                            SchedulePreparedBootstrap(0);
                        }
                    }
                    else
                    {
                        ClearPreparedState();
                        InvalidateOverlay();
                    }
                }
            }, "跟踪已完成但尚未上菜的成品。");
            DrawToggleRow("菜单颜色", IsMenuTicketTintEnabled(), delegate(bool value)
            {
                if (menuTicketTintEnabled != null)
                {
                    menuTicketTintEnabled.Value = value;
                    InvalidateTicketWidgets();
                }
            }, "给关卡里的菜单栏上色。");
            DrawColorRow("在单颜色", menuTicketOnMenuTintColor, "菜单栏里“在单未备”的底色。", delegate
            {
                InvalidateTicketWidgets();
            });
            DrawColorRow("已备颜色", menuTicketPreparedTintColor, "菜单栏里“已备”的底色。", delegate
            {
                InvalidateTicketWidgets();
            });
            DrawEnumCycleRow("显示语言", GetTrackerLanguageLabel(languageMode != null ? languageMode.Value : TrackerLanguage.Auto), delegate()
            {
                if (languageMode != null)
                {
                    languageMode.Value = NextLanguage(languageMode.Value);
                    InvalidateOverlay();
                }
            }, "点击切换 Auto / English / Chinese。");
            DrawInfoText("橙名=在单未备，绿名=已备。");
        }

        private static void DrawOverlaySettingsSection()
        {
            DrawSectionHeader("悬浮窗");
            DrawIntSliderRow("悬浮窗X", overlayX, 0, 4000, "悬浮窗左上角 X 坐标。");
            DrawIntSliderRow("悬浮窗Y", overlayY, 0, 4000, "悬浮窗左上角 Y 坐标。");
            DrawIntSliderRow("悬浮窗宽度", overlayWidth, 240, 1600, "历史菜单追踪悬浮窗宽度。");
            DrawIntSliderRow("悬浮窗高度", overlayHeight, 120, 1600, "历史菜单追踪悬浮窗高度。");
            DrawIntSliderRow("悬浮窗字体大小", overlayFontSize, 8, 48, "历史菜单追踪悬浮窗字体大小。");
            DrawToggleRow("悬浮窗粗体", overlayBoldFont != null && overlayBoldFont.Value, delegate(bool value)
            {
                if (overlayBoldFont != null)
                {
                    overlayBoldFont.Value = value;
                }
            }, "是否使用粗体显示悬浮窗文字。");
            DrawIntSliderRow("悬浮窗最大显示菜品数", overlayMaxDisplayDishes, 1, 40, "悬浮窗最多显示多少道菜。");
            DrawEnumCycleRow("悬浮窗文本对齐", GetOverlayAlignmentLabel(overlayTextAlignment != null ? overlayTextAlignment.Value : OverlayTextAlignment.Left), delegate()
            {
                if (overlayTextAlignment != null)
                {
                    overlayTextAlignment.Value = NextAlignment(overlayTextAlignment.Value);
                    InvalidateOverlay();
                }
            }, "点击切换 Left / Right / Center。");
            DrawColorRow("悬浮窗字体颜色", overlayFontColor, "悬浮窗普通文字颜色。");
            DrawColorRow("悬浮窗上单数量颜色", overlayServedValueColor, "悬浮窗中“上单数量”数值颜色。");
            DrawColorRow("悬浮窗概率颜色", overlayProbabilityValueColor, "悬浮窗中“概率”数值颜色。");
            DrawColorRow("悬浮窗已备颜色", overlayPreparedValueColor, "悬浮窗中“已备”数值颜色。");
        }

        private static void DrawSceneAndDishSelectionSection()
        {
            DrawSectionHeader("菜单追踪");
            SceneInfo scene = SyncSceneSelectorConfigEntry();
            List<SceneInfo> selectableScenes = GetSelectableScenes();
            bool locked = IsLockedToCurrentScene();

            DrawSceneSelectorRow(scene, selectableScenes, locked);
            DrawTrackingPanel(null);
        }

        private static void DrawSceneSelectorRow(SceneInfo selectedSceneInfo, List<SceneInfo> selectableScenes, bool locked)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(SceneSelectorKey, GUILayout.Width(SettingsLabelWidth));
            if (locked)
            {
                GUILayout.Label(GetSceneSelectorValue(selectedSceneInfo), GUI.skin.textField, GUILayout.ExpandWidth(true));
            }
            else
            {
                string buttonText = GetSceneSelectorValue(selectedSceneInfo);
                if (GUILayout.Button(buttonText, GUILayout.ExpandWidth(true)))
                {
                    sceneDropdownExpanded = !sceneDropdownExpanded;
                }
            }
            GUILayout.EndHorizontal();

            if (locked)
            {
                DrawInfoText("当前在关卡内，关卡选择已自动锁定为本局关卡。");
            }
            else if (sceneDropdownExpanded)
            {
                sceneDropdownScrollPosition = GUILayout.BeginScrollView(sceneDropdownScrollPosition, GUILayout.MinHeight(88f), GUILayout.MaxHeight(SceneDropdownMaxHeight));
                if (selectableScenes == null || selectableScenes.Count == 0)
                {
                    GUILayout.Label(NoSceneSelectorValue);
                }
                else
                {
                    for (int i = 0; i < selectableScenes.Count; i++)
                    {
                        SceneInfo selectableScene = selectableScenes[i];
                        if (selectableScene == null)
                        {
                            continue;
                        }

                        bool isCurrent = string.Equals(selectedSceneName, selectableScene.SceneName, StringComparison.OrdinalIgnoreCase);
                        string label = GetSceneSelectorValue(selectableScene);
                        if (GUILayout.Button((isCurrent ? "✓ " : string.Empty) + label, GUILayout.ExpandWidth(true)))
                        {
                            if (!isCurrent)
                            {
                                selectedSceneName = selectableScene.SceneName;
                                preferredSceneName = selectableScene.SceneName;
                                SyncSceneSelectorConfigEntry();
                            }

                            sceneDropdownExpanded = false;
                        }
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
        }

        private static void DrawSectionHeader(string title)
        {
            GUILayout.Space(6f);
            GUILayout.Label(title, GUI.skin.box, GUILayout.ExpandWidth(true));
        }

        private static void DrawInfoText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            GUILayout.Label(text, GUILayout.Width(SettingsDescriptionWidth), GUILayout.ExpandWidth(true));
        }

        private static void DrawToggleRow(string label, bool value, Action<bool> setter, string description)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SettingsLabelWidth));
            bool nextValue = GUILayout.Toggle(value, value ? "启用" : "关闭", GUILayout.Width(88f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (nextValue != value && setter != null)
            {
                setter(nextValue);
            }

            DrawInfoText(description);
            GUILayout.EndVertical();
        }

        private static void DrawEnumCycleRow(string label, string valueText, Action onCycle, string description)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SettingsLabelWidth));
            if (GUILayout.Button(valueText, GUILayout.Width(220f)) && onCycle != null)
            {
                onCycle();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            DrawInfoText(description);
            GUILayout.EndVertical();
        }

        private static void DrawIntSliderRow(string label, ConfigEntry<int> entry, int minValue, int maxValue, string description)
        {
            if (entry == null)
            {
                return;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SettingsLabelWidth));
            int nextValue = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, minValue, maxValue, GUILayout.Width(260f)));
            GUILayout.Label(nextValue.ToString(), GUILayout.Width(56f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (nextValue != entry.Value)
            {
                entry.Value = nextValue;
                InvalidateOverlay();
            }

            DrawInfoText(description);
            GUILayout.EndVertical();
        }

        private static void DrawColorRow(string label, ConfigEntry<Color> entry, string description, Action onChanged = null)
        {
            if (entry == null)
            {
                return;
            }

            Color value = entry.Value;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(SettingsLabelWidth));
            Color previousColor = GUI.color;
            GUI.color = value;
            GUILayout.Box(string.Empty, GUILayout.Width(42f), GUILayout.Height(18f));
            GUI.color = previousColor;
            GUILayout.Label(ColorUtility.ToHtmlStringRGBA(value), GUILayout.Width(96f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            value.r = DrawColorChannelSlider("R", value.r);
            value.g = DrawColorChannelSlider("G", value.g);
            value.b = DrawColorChannelSlider("B", value.b);
            value.a = DrawColorChannelSlider("A", value.a);
            if (value != entry.Value)
            {
                entry.Value = value;
                InvalidateOverlay();
                if (onChanged != null)
                {
                    onChanged();
                }
            }

            DrawInfoText(description);
            GUILayout.EndVertical();
        }

        private static float DrawColorChannelSlider(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(18f));
            float nextValue = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.Width(240f));
            GUILayout.Label(Mathf.RoundToInt(nextValue * 255f).ToString(), GUILayout.Width(36f));
            GUILayout.EndHorizontal();
            return nextValue;
        }

        private static void DrawHotkeyRow()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("打开菜单管理窗口", GUILayout.Width(SettingsLabelWidth));
            string hotkeyText = capturingHotkey
                ? "按任意键..."
                : settingsWindowHotkey.ToString();
            if (GUILayout.Button(hotkeyText, GUILayout.Width(140f)))
            {
                capturingHotkey = !capturingHotkey;
            }
            if (GUILayout.Button("重置", GUILayout.Width(SettingsActionButtonWidth)))
            {
                settingsWindowHotkey = DefaultSettingsWindowHotkey;
                SaveHotkeyConfig();
                capturingHotkey = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            DrawInfoText("热键保存在 BepInEx/config/OC2MenuManager.hotkey.txt。点击当前热键可改写该文件，也可以直接编辑文本文件。");
            GUILayout.EndVertical();
        }

        private static TrackerLanguage NextLanguage(TrackerLanguage value)
        {
            if (value == TrackerLanguage.Auto)
            {
                return TrackerLanguage.English;
            }

            if (value == TrackerLanguage.English)
            {
                return TrackerLanguage.Chinese;
            }

            return TrackerLanguage.Auto;
        }

        private static string GetTrackerLanguageLabel(TrackerLanguage value)
        {
            switch (value)
            {
                case TrackerLanguage.English:
                    return "English";
                case TrackerLanguage.Chinese:
                    return "Chinese";
                default:
                    return "Auto";
            }
        }

        private static OverlayTextAlignment NextAlignment(OverlayTextAlignment value)
        {
            if (value == OverlayTextAlignment.Left)
            {
                return OverlayTextAlignment.Center;
            }

            if (value == OverlayTextAlignment.Center)
            {
                return OverlayTextAlignment.Right;
            }

            return OverlayTextAlignment.Left;
        }

        private static string GetOverlayAlignmentLabel(OverlayTextAlignment value)
        {
            switch (value)
            {
                case OverlayTextAlignment.Center:
                    return "Center";
                case OverlayTextAlignment.Right:
                    return "Right";
                default:
                    return "Left";
            }
        }

        private static void InvalidateOverlay()
        {
            overlayDirty = true;
            int targetFrame = Time.frameCount + (IsInActiveRound() ? OverlayRefreshIntervalFrames : 0);
            if (nextOverlayRefreshFrame == 0 || targetFrame < nextOverlayRefreshFrame)
            {
                nextOverlayRefreshFrame = targetFrame;
            }
        }

        private static void InvalidateTicketWidgets()
        {
            if (!IsMenuTicketTintEnabled() && !ticketWidgetTintActive)
            {
                ticketWidgetsDirty = false;
                nextTicketWidgetRefreshFrame = 0;
                return;
            }

            ticketWidgetsDirty = true;
            int targetFrame = Time.frameCount + (IsInActiveRound() ? TicketWidgetRefreshDelayFrames : 0);
            if (nextTicketWidgetRefreshFrame == 0 || targetFrame < nextTicketWidgetRefreshFrame)
            {
                nextTicketWidgetRefreshFrame = targetFrame;
            }
        }

        private static bool IsPreparedTrackingEnabled()
        {
            return preparedTrackingEnabled != null && preparedTrackingEnabled.Value;
        }

        private static bool IsMenuTicketTintEnabled()
        {
            return menuTicketTintEnabled != null && menuTicketTintEnabled.Value;
        }

        private static void ClearOnMenuCounts()
        {
            CurrentOnMenuCounts.Clear();
            currentOnMenuCountsSceneName = string.Empty;
            currentOnMenuCountsDirty = true;
            InvalidatePreparedCandidates(false);
        }

        private static void ClearPreparedState()
        {
            bool hadPreparedState = PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0;
            foreach (PreparedSourceState source in PreparedSourcesByInstanceId.Values)
            {
                if (source != null && source.Provider != null && source.Callback != null)
                {
                    try
                    {
                        source.Provider.UnregisterOrderCompositionChangedCallback(source.Callback);
                    }
                    catch
                    {
                    }
                }
            }

            PreparedSourcesByInstanceId.Clear();
            PreparedSourceIdsByGameObjectId.Clear();
            PreparedCookStateBySourceId.Clear();
            PreparedCookableByHandlerId.Clear();
            PreparedCountsByRecipe.Clear();
            DirtyPreparedSourceIds.Clear();
            PreparedSourceRefreshBuffer.Clear();
            PreparedCandidateRecipeIdsBuffer.Clear();
            nextPreparedSourceRefreshFrame = 0;
            nextPreparedSourcePruneFrame = 0;
            nextPreparedBootstrapFrame = 0;
            nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackDelayFrames;
            preparedSourceBootstrapComplete = true;
            preparedSourceBootstrapStage = 0;
            preparedSourceSceneName = string.Empty;
            preparedCandidateSceneName = string.Empty;
            preparedCandidateRecipeIdsDirty = true;
            if (hadPreparedState)
            {
                InvalidateTicketWidgets();
            }
        }

        private static void ClearTicketWidgetState()
        {
            foreach (TicketWidgetState state in TicketWidgetsByInstanceId.Values)
            {
                RestoreTicketWidgetTint(state);
            }

            TicketWidgetsByInstanceId.Clear();
            TicketWidgetsBuffer.Clear();
            ticketWidgetsDirty = false;
            ticketWidgetTintActive = false;
            nextTicketWidgetRefreshFrame = 0;
        }

        private static void RestoreAllTicketWidgetTints()
        {
            foreach (TicketWidgetState state in TicketWidgetsByInstanceId.Values)
            {
                RestoreTicketWidgetTint(state);
            }

            ticketWidgetTintActive = false;
        }

        private static void IncrementOnMenuCount(string sceneName, int recipeId)
        {
            EnsureOnMenuCountScene(sceneName);
            CurrentOnMenuCounts[recipeId] = GetCount(CurrentOnMenuCounts, recipeId) + 1;
            currentOnMenuCountsDirty = false;
            InvalidatePreparedCandidates(true);
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
            InvalidatePreparedCandidates(true);
        }

        private static void InvalidatePreparedCandidates(bool queueAllPreparedSources)
        {
            preparedCandidateRecipeIdsDirty = true;
            if (queueAllPreparedSources && PreparedSourcesByInstanceId.Count > 0)
            {
                foreach (int instanceId in PreparedSourcesByInstanceId.Keys)
                {
                    DirtyPreparedSourceIds.Add(instanceId);
                }

                int targetFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
                if (nextPreparedSourceRefreshFrame == 0 || targetFrame < nextPreparedSourceRefreshFrame)
                {
                    nextPreparedSourceRefreshFrame = targetFrame;
                }
            }
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

        private static void RefreshPreparedState(bool inActiveRound)
        {
            if (!enabled.Value || !inActiveRound)
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null || scene.OrderedRecipes.Count == 0)
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            if (!HasAnyTrackedRecipes(scene))
            {
                if (PreparedSourcesByInstanceId.Count > 0 || PreparedCountsByRecipe.Count > 0)
                {
                    ClearPreparedState();
                    InvalidateOverlay();
                }

                return;
            }

            if (!string.Equals(preparedSourceSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                ClearPreparedState();
                preparedSourceSceneName = scene.SceneName;
            }

            if (preparedSourceBootstrapComplete
                && PreparedSourcesByInstanceId.Count == 0
                && Time.frameCount >= nextPreparedBootstrapFallbackFrame)
            {
                SchedulePreparedBootstrap(0);
                nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackIntervalFrames;
            }

            if (!preparedSourceBootstrapComplete)
            {
                if (Time.frameCount >= nextPreparedBootstrapFrame)
                {
                    if (RunPreparedBootstrapStep())
                    {
                        preparedSourceBootstrapComplete = true;
                        nextPreparedSourceRefreshFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
                        nextPreparedSourcePruneFrame = Time.frameCount + PreparedSourcePruneIntervalFrames;
                        nextPreparedBootstrapFallbackFrame = Time.frameCount + PreparedBootstrapFallbackIntervalFrames;
                    }
                    else
                    {
                        nextPreparedBootstrapFrame = Time.frameCount + PreparedBootstrapStepIntervalFrames;
                    }
                }

                return;
            }

            if (DirtyPreparedSourceIds.Count > 0 && Time.frameCount >= nextPreparedSourceRefreshFrame)
            {
                RefreshDirtyPreparedSources(MaxPreparedSourceRefreshesPerBatch);
                nextPreparedSourceRefreshFrame = Time.frameCount + PreparedSourceRefreshIntervalFrames;
            }

            if (Time.frameCount >= nextPreparedSourcePruneFrame)
            {
                PrunePreparedSources();
                nextPreparedSourcePruneFrame = Time.frameCount + PreparedSourcePruneIntervalFrames;
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
                nextClientFlowLookupFrame = Time.frameCount + (cachedClientFlowController != null ? ControllerLookupIntervalFrames : ControllerLookupRetryIntervalFrames);
            }

            return cachedClientFlowController;
        }

        private static ClientKitchenFlowControllerBase GetKitchenFlowController()
        {
            if (cachedKitchenFlowController == null || Time.frameCount >= nextKitchenFlowLookupFrame)
            {
                cachedKitchenFlowController = UnityEngine.Object.FindObjectOfType<ClientKitchenFlowControllerBase>();
                nextKitchenFlowLookupFrame = Time.frameCount + (cachedKitchenFlowController != null ? ControllerLookupIntervalFrames : ControllerLookupRetryIntervalFrames);
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
            cachedCurrentSceneInfo = null;
            cachedCurrentSceneInfoValid = false;
            cachedCurrentSceneInfoFrame = int.MinValue;
            ClearOnMenuCounts();
            ClearPreparedState();
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

        private static void BootstrapPreparedSources()
        {
            while (!RunPreparedBootstrapStep())
            {
            }
        }

        private static void SchedulePreparedBootstrap(int delayFrames)
        {
            preparedSourceBootstrapComplete = false;
            preparedSourceBootstrapStage = 0;
            nextPreparedBootstrapFrame = Time.frameCount + Math.Max(0, delayFrames);
        }

        private static bool RunPreparedBootstrapStep()
        {
            switch (preparedSourceBootstrapStage)
            {
                case 0:
                    RegisterPreparedSources(FindObjectsSafe<ClientPlate>());
                    break;
                case 1:
                    RegisterPreparedSources(FindObjectsSafe<ClientCookableContainer>());
                    break;
                case 2:
                    RegisterPreparedSources(FindObjectsSafe<ClientPreparationContainer>());
                    break;
                case 3:
                    RegisterPreparedSources(FindObjectsSafe<ClientItemContainer>());
                    break;
                case 4:
                    RegisterPreparedSources(FindObjectsSafe<ClientLadleContainer>());
                    break;
                case 5:
                    RegisterPreparedSources(FindObjectsSafe<ClientMixableContainer>());
                    break;
                case 6:
                    RegisterPreparedSourcesFromCarriers(FindObjectsSafe<ClientPlayerAttachmentCarrier>());
                    break;
                default:
                    PrunePreparedSources();
                    preparedSourceBootstrapStage = 0;
                    return true;
            }

            preparedSourceBootstrapStage++;
            return false;
        }

        private static T[] FindObjectsSafe<T>() where T : Component
        {
            try
            {
                return UnityEngine.Object.FindObjectsOfType<T>();
            }
            catch
            {
                return new T[0];
            }
        }

        private static void RegisterPreparedSources<T>(T[] components) where T : Component
        {
            if (components == null)
            {
                return;
            }

            for (int i = 0; i < components.Length; i++)
            {
                RegisterPreparedSource(components[i]);
            }
        }

        private static void RegisterPreparedSourcesFromCarriers(ClientPlayerAttachmentCarrier[] carriers)
        {
            if (carriers == null)
            {
                return;
            }

            for (int i = 0; i < carriers.Length; i++)
            {
                TryRegisterPreparedSourcesFromCarrier(carriers[i]);
            }
        }

        private static void TryRegisterPreparedSource(Component component)
        {
            if (!IsPreparedTrackingEnabled() || enabled == null || !enabled.Value || component == null || !IsInActiveRound())
            {
                return;
            }

            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene == null || scene.OrderedRecipes.Count == 0 || !HasAnyTrackedRecipes(scene))
            {
                return;
            }

            if (!string.Equals(preparedSourceSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                ClearPreparedState();
                preparedSourceSceneName = scene.SceneName;
            }

            RegisterPreparedSource(component);
        }

        private static void TryRegisterPreparedSourceFromGameObject(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            Component bestSource = null;
            int bestPriority = int.MaxValue;
            IClientOrderDefinition[] recursiveProviders = gameObject.RequestInterfacesRecursive<IClientOrderDefinition>();
            for (int i = 0; i < recursiveProviders.Length; i++)
            {
                ConsiderPreparedSourceCandidate(recursiveProviders[i] as Component, ref bestSource, ref bestPriority);
            }

            Transform parent = gameObject.transform.parent;
            while (parent != null)
            {
                ConsiderPreparedSourceCandidate(parent.gameObject.RequestInterface<IClientOrderDefinition>() as Component, ref bestSource, ref bestPriority);
                parent = parent.parent;
            }

            if (bestSource != null)
            {
                TryRegisterPreparedSource(bestSource);
            }
        }

        private static void ConsiderPreparedSourceCandidate(Component component, ref Component bestSource, ref int bestPriority)
        {
            if (component == null || !(component is IClientOrderDefinition))
            {
                return;
            }

            int priority = GetPreparedSourcePriority(component);
            if (priority >= bestPriority)
            {
                return;
            }

            bestPriority = priority;
            bestSource = component;
        }

        private static void TryRegisterPreparedSourcesFromCarrier(ClientPlayerAttachmentCarrier carrier)
        {
            if (carrier == null)
            {
                return;
            }

            TryRegisterPreparedSourceFromGameObject(carrier.InspectCarriedItem());
            TryRegisterPreparedSourceFromGameObject(carrier.InspectCarriedItem(PlayerAttachTarget.Back));
        }

        private static void RegisterPreparedSource(Component component)
        {
            IClientOrderDefinition provider = component as IClientOrderDefinition;
            if (component == null || provider == null || component.gameObject == null)
            {
                return;
            }

            int instanceId = component.GetInstanceID();
            if (PreparedSourcesByInstanceId.ContainsKey(instanceId))
            {
                return;
            }

            int gameObjectInstanceId = component.gameObject.GetInstanceID();
            int existingSourceId;
            if (PreparedSourceIdsByGameObjectId.TryGetValue(gameObjectInstanceId, out existingSourceId))
            {
                PreparedSourceState existingSource;
                if (!PreparedSourcesByInstanceId.TryGetValue(existingSourceId, out existingSource) || existingSource == null)
                {
                    PreparedSourceIdsByGameObjectId.Remove(gameObjectInstanceId);
                }
                else if (GetPreparedSourcePriority(existingSource.Component) <= GetPreparedSourcePriority(component))
                {
                    return;
                }
                else
                {
                    RemovePreparedSource(existingSourceId);
                }
            }

            PreparedSourceState source = new PreparedSourceState();
            source.InstanceId = instanceId;
            source.GameObjectInstanceId = gameObjectInstanceId;
            source.Component = component;
            source.Provider = provider;
            source.Callback = delegate
            {
                QueuePreparedSourceRefresh(instanceId);
            };

            PreparedSourcesByInstanceId[instanceId] = source;
            PreparedSourceIdsByGameObjectId[gameObjectInstanceId] = instanceId;

            try
            {
                provider.RegisterOrderCompositionChangedCallback(source.Callback);
            }
            catch
            {
            }

            QueuePreparedSourceRefresh(instanceId);
        }

        private static int GetPreparedSourcePriority(Component component)
        {
            if (component is ClientPlate)
            {
                return 0;
            }
            if (component is ClientCookableContainer)
            {
                return 1;
            }
            if (component is ClientPreparationContainer)
            {
                return 2;
            }
            if (component is ClientItemContainer)
            {
                return 3;
            }
            if (component is ClientLadleContainer)
            {
                return 4;
            }
            if (component is ClientMixableContainer)
            {
                return 5;
            }
            if (component is AssignableOrderDefinition)
            {
                return 6;
            }
            if (component is ItemPropertiesComponent)
            {
                return 7;
            }
            if (component is IngredientPropertiesComponent)
            {
                return 8;
            }

            return 100;
        }

        private static bool TryGetPreparedSceneInfo(out SceneInfo scene)
        {
            scene = null;
            if (!string.IsNullOrEmpty(preparedSourceSceneName) && SceneCache.TryGetValue(preparedSourceSceneName, out scene) && scene != null)
            {
                return true;
            }

            if (TryGetCurrentSceneInfo(out scene) && scene != null)
            {
                preparedSourceSceneName = scene.SceneName;
                return true;
            }

            scene = null;
            return false;
        }

        private static void QueuePreparedSourceRefresh(int instanceId)
        {
            QueuePreparedSourceRefresh(instanceId, PreparedSourceRefreshIntervalFrames);
        }

        private static void QueuePreparedSourceRefresh(int instanceId, int delayFrames)
        {
            if (instanceId != 0)
            {
                DirtyPreparedSourceIds.Add(instanceId);
                int safeDelayFrames = Math.Max(0, delayFrames);
                int targetFrame = Time.frameCount + safeDelayFrames;
                if (nextPreparedSourceRefreshFrame == 0 || targetFrame < nextPreparedSourceRefreshFrame)
                {
                    nextPreparedSourceRefreshFrame = targetFrame;
                }
            }
        }

        private static void RegisterCookableHandler(ClientCookableContainer cookableContainer)
        {
            if (cookableContainer == null)
            {
                return;
            }

            ClientCookingHandler cookingHandler = cookableContainer.GetCookingHandler();
            if (cookingHandler == null)
            {
                return;
            }

            PreparedCookableByHandlerId[cookingHandler.GetInstanceID()] = cookableContainer;
        }

        private static void QueueCookablePreparedSourceRefresh(ClientCookableContainer cookableContainer)
        {
            if (cookableContainer == null || !enabled.Value || !IsPreparedTrackingEnabled())
            {
                return;
            }

            int instanceId = cookableContainer.GetInstanceID();
            ClientCookingHandler cookingHandler = cookableContainer.GetCookingHandler();
            if (cookingHandler == null)
            {
                TryRegisterPreparedSource(cookableContainer);
                QueuePreparedSourceRefresh(instanceId, 2);
                return;
            }

            CookedCompositeOrderNode.CookingProgress nextState = cookingHandler.GetCookedOrderState();
            CookedCompositeOrderNode.CookingProgress previousState;
            bool hasPreviousState = PreparedCookStateBySourceId.TryGetValue(instanceId, out previousState);
            if (hasPreviousState && previousState == nextState)
            {
                return;
            }

            PreparedCookStateBySourceId[instanceId] = nextState;
            if (!hasPreviousState && nextState == CookedCompositeOrderNode.CookingProgress.Raw)
            {
                return;
            }

            TryRegisterPreparedSource(cookableContainer);
            QueuePreparedSourceRefresh(instanceId, 2);
        }

        private static void RefreshDirtyPreparedSources(int maxCount)
        {
            if (DirtyPreparedSourceIds.Count == 0)
            {
                return;
            }

            PreparedSourceRefreshBuffer.Clear();
            foreach (int instanceId in DirtyPreparedSourceIds)
            {
                PreparedSourceRefreshBuffer.Add(instanceId);
                if (PreparedSourceRefreshBuffer.Count >= maxCount)
                {
                    break;
                }
            }

            for (int i = 0; i < PreparedSourceRefreshBuffer.Count; i++)
            {
                int instanceId = PreparedSourceRefreshBuffer[i];
                DirtyPreparedSourceIds.Remove(instanceId);
                RefreshPreparedSource(instanceId);
            }
        }

        private static void RefreshPreparedSource(int instanceId)
        {
            PreparedSourceState source;
            if (!PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) || source == null)
            {
                return;
            }

            if (source.Component == null || source.Provider == null || source.Component.gameObject == null || !source.Component.gameObject.activeInHierarchy)
            {
                RemovePreparedSource(instanceId);
                return;
            }

            SceneInfo scene;
            if (!TryGetPreparedSceneInfo(out scene))
            {
                SetPreparedSourceMatch(source, 0);
                return;
            }

            try
            {
                AssembledDefinitionNode composition = source.Provider.GetOrderComposition();
                int matchedRecipeId = MatchPreparedRecipe(scene, composition);
                SetPreparedSourceMatch(source, matchedRecipeId);
            }
            catch
            {
                SetPreparedSourceMatch(source, 0);
            }
        }

        private static int MatchPreparedRecipe(SceneInfo scene, AssembledDefinitionNode composition)
        {
            if (scene == null || composition == null)
            {
                return 0;
            }

            List<int> candidateRecipeIds = GetPreparedCandidateRecipeIds(scene);
            if (candidateRecipeIds == null || candidateRecipeIds.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < candidateRecipeIds.Count; i++)
            {
                RecipeInfo recipe;
                if (!scene.RecipesById.TryGetValue(candidateRecipeIds[i], out recipe) || recipe == null || recipe.Definition == null)
                {
                    continue;
                }

                if (AssembledDefinitionNode.Matching(composition, recipe.Definition))
                {
                    return recipe.Id;
                }
            }

            return 0;
        }

        private static List<int> GetPreparedCandidateRecipeIds(SceneInfo scene)
        {
            if (scene == null)
            {
                PreparedCandidateRecipeIdsBuffer.Clear();
                return PreparedCandidateRecipeIdsBuffer;
            }

            if (!preparedCandidateRecipeIdsDirty
                && string.Equals(preparedCandidateSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                return PreparedCandidateRecipeIdsBuffer;
            }

            preparedCandidateSceneName = scene.SceneName;
            preparedCandidateRecipeIdsDirty = false;
            PreparedCandidateRecipeIdsBuffer.Clear();

            HashSet<int> trackedIds;
            bool hasExplicitTrackedIds = TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds) && trackedIds != null;
            if (hasExplicitTrackedIds && trackedIds.Count == 0)
            {
                return PreparedCandidateRecipeIdsBuffer;
            }

            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            Dictionary<int, double> probabilityByRecipeId = BuildProbabilityMap(scene, EnsureRun(scene));
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (recipe == null)
                {
                    continue;
                }

                if (hasExplicitTrackedIds && !trackedIds.Contains(recipe.Id))
                {
                    continue;
                }

                if (GetCount(currentMenuCounts, recipe.Id) > 0 || GetProbability(probabilityByRecipeId, recipe.Id) > 0d)
                {
                    PreparedCandidateRecipeIdsBuffer.Add(recipe.Id);
                }
            }

            return PreparedCandidateRecipeIdsBuffer;
        }

        private static void SetPreparedSourceMatch(PreparedSourceState source, int matchedRecipeId)
        {
            if (source == null || source.MatchedRecipeId == matchedRecipeId)
            {
                return;
            }

            if (source.MatchedRecipeId != 0)
            {
                AdjustPreparedCount(source.MatchedRecipeId, -1);
            }

            source.MatchedRecipeId = matchedRecipeId;
            if (matchedRecipeId != 0)
            {
                AdjustPreparedCount(matchedRecipeId, 1);
            }

            InvalidateOverlay();
            InvalidateTicketWidgets();
        }

        private static void AdjustPreparedCount(int recipeId, int delta)
        {
            int nextValue = Math.Max(0, GetCount(PreparedCountsByRecipe, recipeId) + delta);
            if (nextValue > 0)
            {
                PreparedCountsByRecipe[recipeId] = nextValue;
            }
            else
            {
                PreparedCountsByRecipe.Remove(recipeId);
            }
        }

        private static void RemovePreparedSource(int instanceId)
        {
            PreparedSourceState source;
            if (!PreparedSourcesByInstanceId.TryGetValue(instanceId, out source) || source == null)
            {
                return;
            }

            PreparedCookStateBySourceId.Remove(instanceId);

            if (source.Provider != null && source.Callback != null)
            {
                try
                {
                    source.Provider.UnregisterOrderCompositionChangedCallback(source.Callback);
                }
                catch
                {
                }
            }

            if (source.MatchedRecipeId != 0)
            {
                AdjustPreparedCount(source.MatchedRecipeId, -1);
                InvalidateOverlay();
                InvalidateTicketWidgets();
            }

            PreparedSourcesByInstanceId.Remove(instanceId);
            DirtyPreparedSourceIds.Remove(instanceId);
            if (source.GameObjectInstanceId != 0)
            {
                PreparedSourceIdsByGameObjectId.Remove(source.GameObjectInstanceId);
            }
        }

        private static void PrunePreparedSources()
        {
            PreparedSourceRemovalBuffer.Clear();
            foreach (KeyValuePair<int, PreparedSourceState> pair in PreparedSourcesByInstanceId)
            {
                PreparedSourceState source = pair.Value;
                if (source == null || source.Component == null || source.Component.gameObject == null || !source.Component.gameObject.activeInHierarchy)
                {
                    PreparedSourceRemovalBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < PreparedSourceRemovalBuffer.Count; i++)
            {
                RemovePreparedSource(PreparedSourceRemovalBuffer[i]);
            }
        }

        [HarmonyPatch(typeof(ClientPlate), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPlate_StartSynchronising_Postfix(ClientPlate __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientCookableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientCookableContainer_StartSynchronising_Postfix(ClientCookableContainer __instance)
        {
            RegisterCookableHandler(__instance);
            TryRegisterPreparedSource(__instance);
            QueueCookablePreparedSourceRefresh(__instance);
        }

        [HarmonyPatch(typeof(ClientCookingHandler), "ApplyServerUpdate")]
        [HarmonyPostfix]
        private static void ClientCookingHandler_ApplyServerUpdate_Postfix(ClientCookingHandler __instance)
        {
            if (__instance == null)
            {
                return;
            }

            ClientCookableContainer cookableContainer;
            if (!PreparedCookableByHandlerId.TryGetValue(__instance.GetInstanceID(), out cookableContainer) || cookableContainer == null)
            {
                if (__instance.gameObject == null)
                {
                    return;
                }

                cookableContainer = __instance.gameObject.GetComponent<ClientCookableContainer>();
                if (cookableContainer == null)
                {
                    return;
                }

                RegisterCookableHandler(cookableContainer);
            }

            QueueCookablePreparedSourceRefresh(cookableContainer);
        }

        [HarmonyPatch(typeof(ClientPreparationContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPreparationContainer_StartSynchronising_Postfix(ClientPreparationContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientItemContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientItemContainer_StartSynchronising_Postfix(ClientItemContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientLadleContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientLadleContainer_StartSynchronising_Postfix(ClientLadleContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientMixableContainer), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientMixableContainer_StartSynchronising_Postfix(ClientMixableContainer __instance)
        {
            TryRegisterPreparedSource(__instance);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "StartSynchronising")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_StartSynchronising_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            TryRegisterPreparedSourcesFromCarrier(__instance);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "CarryItem")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_CarryItem_Postfix(GameObject _object)
        {
            TryRegisterPreparedSourceFromGameObject(_object);
        }

        [HarmonyPatch(typeof(ClientPlayerAttachmentCarrier), "ApplyServerEvent")]
        [HarmonyPostfix]
        private static void ClientPlayerAttachmentCarrier_ApplyServerEvent_Postfix(ClientPlayerAttachmentCarrier __instance)
        {
            TryRegisterPreparedSourcesFromCarrier(__instance);
        }

        [HarmonyPatch(typeof(ClientSynchroniserBase), "OnDestroy")]
        [HarmonyPrefix]
        private static void ClientSynchroniserBase_OnDestroy_Prefix(ClientSynchroniserBase __instance)
        {
            Component component = __instance;
            if (component == null)
            {
                return;
            }

            ClientCookingHandler cookingHandler = component as ClientCookingHandler;
            if (cookingHandler != null)
            {
                PreparedCookableByHandlerId.Remove(cookingHandler.GetInstanceID());
            }

            ClientCookableContainer cookableContainer = component as ClientCookableContainer;
            if (cookableContainer != null)
            {
                ClientCookingHandler linkedCookingHandler = cookableContainer.GetCookingHandler();
                if (linkedCookingHandler != null)
                {
                    PreparedCookableByHandlerId.Remove(linkedCookingHandler.GetInstanceID());
                }
            }

            int instanceId = component.GetInstanceID();
            if (PreparedSourcesByInstanceId.ContainsKey(instanceId))
            {
                RemovePreparedSource(instanceId);
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

            UnregisterTicketWidget(widgetData.m_widget);
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
            if (!string.Equals(selectedSceneName, desiredSceneName, StringComparison.OrdinalIgnoreCase))
            {
                selectedSceneName = desiredSceneName;
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
                string candidate = AppendSuffixWithLengthLimit(baseValue, suffix, MaxSceneSelectorDisplayLength);
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
                ? "当前在关卡内，关卡选择已自动锁定为本局关卡，但你仍然可以修改本关的追踪菜品。"
                : "请先在上方“选择关卡”下拉框中切换关卡，再勾选要追踪的菜品。");
            GUILayout.Label("橙名=在单未备，绿名=已备。");

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
                GUILayout.Label("这个 DIY 关卡的菜谱还没有被读取。请先进入一次该关卡，然后再打开菜单窗口勾选要追踪的菜品。");
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
            GUILayout.Label("按类别批量勾选：");
            for (int i = 0; i < groups.Count;)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 2 && i < groups.Count; column++, i++)
                {
                    CategorySelectionGroup group = groups[i];
                    bool allTracked = AreAllCategoryRecipesTracked(scene, group);
                    bool nextTracked = GUILayout.Toggle(allTracked, "全部" + group.CategoryName, GUILayout.MinWidth(160f), GUILayout.ExpandWidth(true));
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
            CategorySelectionGroupsBuffer.Clear();
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

                string categoryName = string.IsNullOrEmpty(recipe.CategoryName) ? "其他" : recipe.CategoryName;
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
            state.CachedImages = null;
            state.AppliedDisplayTint = state.OriginalDisplayTint;
            state.AppliedTopTint = state.OriginalTopTint;
            state.HasAppliedTint = false;
            TicketWidgetsByInstanceId[instanceId] = state;
            InvalidateTicketWidgets();
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

            ApplyTicketWidgetTint(state, state.OriginalDisplayTint, state.OriginalTopTint);
        }

        private static bool ApplyTicketWidgetTint(TicketWidgetState state, Color displayTint, Color topTint)
        {
            if (state == null)
            {
                return false;
            }

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

            if (state.HasAppliedTint && state.AppliedDisplayTint == displayTint && state.AppliedTopTint == topTint)
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

            if (!appliedAny)
            {
                state.HasAppliedTint = false;
                return false;
            }

            state.AppliedDisplayTint = displayTint;
            state.AppliedTopTint = topTint;
            state.HasAppliedTint = true;
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
            foreach (KeyValuePair<int, TicketWidgetState> pair in TicketWidgetsByInstanceId)
            {
                TicketWidgetState state = pair.Value;
                if (state == null || state.Widget == null)
                {
                    continue;
                }

                TicketWidgetsBuffer.Add(state);
            }

            TicketWidgetsBuffer.Sort(delegate(TicketWidgetState a, TicketWidgetState b)
            {
                int recipeCompare = a.RecipeId.CompareTo(b.RecipeId);
                if (recipeCompare != 0)
                {
                    return recipeCompare;
                }

                return a.Order.CompareTo(b.Order);
            });

            bool needsRetry = false;
            bool appliedTint = false;
            for (int i = 0; i < TicketWidgetsBuffer.Count; i++)
            {
                TicketWidgetState state = TicketWidgetsBuffer[i];
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

                Color tint = hasPreparedAssignment
                    ? GetMenuTicketPreparedTintColor()
                    : GetMenuTicketOnMenuTintColor();
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

        private static void SetAllTracked(SceneInfo scene, bool shouldTrack)
        {
            if (scene == null)
            {
                return;
            }

            bool changed = false;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                changed |= ApplyTrackedStateCore(scene.SceneName, scene.OrderedRecipes[i].Id, shouldTrack);
            }

            if (changed)
            {
                SaveSelections();
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                InvalidateTicketWidgets();
            }
        }

        private static void ApplyTrackedState(string sceneName, int recipeId, bool shouldTrack, bool save)
        {
            bool changed = ApplyTrackedStateCore(sceneName, recipeId, shouldTrack);
            if (changed && save)
            {
                SaveSelections();
            }

            if (changed)
            {
                InvalidatePreparedCandidates(true);
                InvalidateOverlay();
                InvalidateTicketWidgets();
            }
        }

        private static bool ApplyTrackedStateCore(string sceneName, int recipeId, bool shouldTrack)
        {
            SceneInfo scene;
            if (!SceneCache.TryGetValue(sceneName, out scene) || scene == null)
            {
                return false;
            }

            HashSet<int> trackedIds;
            if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds))
            {
                trackedIds = new HashSet<int>(scene.OrderedRecipes.Select(x => x.Id));
                TrackedIdsByScene[scene.SceneName] = trackedIds;
            }

            return shouldTrack ? trackedIds.Add(recipeId) : trackedIds.Remove(recipeId);
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
            SceneInfo[] cachedScenes = SceneCache.Values.ToArray();
            for (int i = 0; i < cachedScenes.Length; i++)
            {
                AddScene(scenes, seenScenes, cachedScenes[i]);
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
            info.CategoryName = DishNameCatalog.GetCategoryName(recipe.name);
            info.CategoryTier = DishNameCatalog.GetCategoryTier(recipe.name);
            info.Definition = recipe;
            scene.RecipesById.Add(info.Id, info);
            scene.OrderedRecipes.Add(info);
            scene.AllRecipeIds.Add(info.Id);
            DishNameCatalog.RecordRecipe(scene.SceneName, recipe.m_uID, recipe.name);
        }

        private static bool TryGetCurrentSceneInfo(out SceneInfo scene)
        {
            if (Time.frameCount == cachedCurrentSceneInfoFrame)
            {
                scene = cachedCurrentSceneInfo;
                return cachedCurrentSceneInfoValid;
            }

            scene = null;
            SceneDirectoryData.PerPlayerCountDirectoryEntry sceneVariant;
            if (!TryGetCurrentSceneVariant(out sceneVariant))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = null;
                cachedCurrentSceneInfoValid = false;
                return false;
            }

            string sceneName = sceneVariant.SceneName;
            LevelConfigBase levelConfig = sceneVariant.LevelConfig ?? GameUtils.GetLevelConfig();
            if (SceneCache.TryGetValue(sceneName, out scene)
                && scene != null
                && (scene.OrderedRecipes.Count > 0 || levelConfig == null))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = scene;
                cachedCurrentSceneInfoValid = true;
                return true;
            }

            if (levelConfig == null || IsHordeLevel(levelConfig))
            {
                cachedCurrentSceneInfoFrame = Time.frameCount;
                cachedCurrentSceneInfo = null;
                cachedCurrentSceneInfoValid = false;
                return false;
            }

            string displayName = scene != null && !string.IsNullOrEmpty(scene.DisplayName) ? scene.DisplayName : sceneName;
            scene = BuildSceneInfo(sceneName, displayName, levelConfig);
            if (scene != null)
            {
                SceneCache[scene.SceneName] = scene;
            }

            cachedCurrentSceneInfoFrame = Time.frameCount;
            cachedCurrentSceneInfo = scene;
            cachedCurrentSceneInfoValid = scene != null;
            return cachedCurrentSceneInfoValid;
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

        private static int GetMenuOrder(Dictionary<int, int> menuOrders, int recipeId)
        {
            int value;
            return menuOrders != null && menuOrders.TryGetValue(recipeId, out value) ? value : int.MaxValue;
        }

        private static OverlayRow GetOrCreateOverlayRow(int index)
        {
            while (OverlayRowsBuffer.Count <= index)
            {
                OverlayRowsBuffer.Add(new OverlayRow());
            }

            return OverlayRowsBuffer[index];
        }

        private static OverlayRenderRow GetOrCreateOverlayRenderRow(int index)
        {
            while (OverlayRenderRowsBuffer.Count <= index)
            {
                OverlayRenderRowsBuffer.Add(new OverlayRenderRow());
            }

            return OverlayRenderRowsBuffer[index];
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
            Dictionary<int, int> currentMenuCounts = GetCurrentOnMenuCounts(scene);
            Dictionary<int, int> menuOrderByRecipeId = BuildMenuOrderMap(scene);
            Dictionary<int, double> probabilityByRecipeId = BuildProbabilityMap(scene, run);
            List<OverlayRow> rows = OverlayRowsBuffer;
            int rowCount = 0;
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (!IsTracked(scene, recipe.Id))
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

            bool chinese = UseChinese();
            if (rows.Count == 0)
            {
                OverlayRenderRowsBuffer.Clear();
                return chinese
                ? "历史菜单追踪\n当前关卡没有勾选任何追踪菜品。\n请打开 OC2MenuManager 独立窗口勾选需要追踪的菜品。"
                : "Menu History Tracker\nNo dishes are tracked for this scene.\nOpen the standalone OC2MenuManager window to choose tracked dishes.";
            }

            rows.Sort(delegate(OverlayRow a, OverlayRow b)
            {
                bool aOnMenuAndUnprepared = a.OnMenu > 0 && (!showPrepared || a.Prepared <= 0);
                bool bOnMenuAndUnprepared = b.OnMenu > 0 && (!showPrepared || b.Prepared <= 0);
                bool aPrepared = showPrepared && a.Prepared > 0;
                bool bPrepared = showPrepared && b.Prepared > 0;
                bool aOffMenuUnpreparedPositiveProbability = !aOnMenuAndUnprepared && !aPrepared && a.Probability > 0d;
                bool bOffMenuUnpreparedPositiveProbability = !bOnMenuAndUnprepared && !bPrepared && b.Probability > 0d;
                bool aOffMenuUnpreparedZeroProbability = !aOnMenuAndUnprepared && !aPrepared && a.Probability <= 0d;
                bool bOffMenuUnpreparedZeroProbability = !bOnMenuAndUnprepared && !bPrepared && b.Probability <= 0d;

                int aBucket = aOnMenuAndUnprepared ? 0 : (aOffMenuUnpreparedPositiveProbability ? 1 : (aOffMenuUnpreparedZeroProbability ? 2 : 3));
                int bBucket = bOnMenuAndUnprepared ? 0 : (bOffMenuUnpreparedPositiveProbability ? 1 : (bOffMenuUnpreparedZeroProbability ? 2 : 3));
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
            });

            StringBuilder builder = OverlayTextBuilder;
            builder.Length = 0;
            builder.Append(TruncateWithEllipsis(GetOverlaySceneLabel(scene), MaxOverlaySceneDisplayLength)).Append(" | ");
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
                builder.Append(WrapRichValue(row.Served.ToString(), overlayServedValueColor != null ? overlayServedValueColor.Value : new Color(0.58f, 0.84f, 1f, 1f)));
                builder.Append("  |  ");
                builder.Append(WrapRichValue((row.Probability * 100d).ToString("0.0") + "%", overlayProbabilityValueColor != null ? overlayProbabilityValueColor.Value : new Color(1f, 0.84f, 0.40f, 1f)));
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
            string name = TruncateWithEllipsis(GetRecipeDisplayName(row.Recipe), MaxOverlayDishDisplayLength);
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
                if (state == null || state.Widget == null || state.Order < 0 || !IsTracked(scene, state.RecipeId))
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
            ConfigFile config = _MODEntry.SettingsConfig;

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

        private static void RemoveLegacySettingsWindowHotkeyEntry()
        {
            ConfigFile config = _MODEntry.SettingsConfig;
            if (config.Remove(LegacySettingsWindowHotkeyDefinition))
            {
                config.Save();
            }
        }

        private static void RemoveGeneratedConfigEntries()
        {
            bool removedAny = false;
            ConfigFile config = _MODEntry.SettingsConfig;
            if (config.Remove(new ConfigDefinition(TrackerSection, SceneSelectorKey)))
            {
                removedAny = true;
            }

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
            ConfigFile config = _MODEntry.SettingsConfig;
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
