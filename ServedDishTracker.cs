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
using HostUtilities;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
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
            public AssembledDefinitionNode SimplifiedDefinition;
            public AssembledDefinitionNode SimplifiedUnwrappedDefinition;
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
            public bool PendingRemoval;
            public int RemovalGraceUntilFrame;
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
                        Rect strikeRect = new Rect(rowRect.x + 8f, strikeY, Mathf.Max(12f, rowRect.width - 16f), 4f);
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

        private sealed class ReferenceTicketCandidate
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
        }

        private sealed class ReferenceTicketState
        {
            public int FlowInstanceId;
            public RecipeFlowGUI Flow;
            public int RecipeId;
            public double Probability;
            public RecipeFlowGUI.ElementToken Token;
            public RecipeWidgetUIController Widget;
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
            public float OriginalOpacity = 1f;
            public Image[] CachedImages;
            public CanvasGroup CanvasGroup;
            public bool CanvasGroupResolved;
            public Color AppliedDisplayTint;
            public Color AppliedTopTint;
            public float AppliedOpacity = 1f;
            public bool HasAppliedTint;
            public bool IsReferenceTicket;
            public bool IsDyingReferenceTicket;
            public double ReferenceProbability;
        }

        private const string TrackerSection = "03-历史菜单追踪";
        private const string DishSelectionSection = "04-历史菜单菜品";
        private const string TierSection = "05-层级设置";
        private const string SceneSelectorKey = "选择关卡";
        private const string NoSceneSelectorValue = "暂无可选关卡";
        private const string SettingsWindowHotkeyKey = "打开菜单管理窗口";
        private const int MinCategoryTierValue = 1;
        private const int MaxCategoryTierValue = 6;
        private const int TierSettingsColumnCount = 5;
        private const int MinSceneSelectorDisplayLength = 12;
        private const int MaxSceneSelectorDisplayLengthSetting = 120;
        private const int MaxSceneSelectorDisplayLength = 40;
        private const int MinDishSelectorDisplayLength = 8;
        private const int MaxDishSelectorDisplayLengthSetting = 120;
        private const int MaxDishSelectorDisplayLength = 26;
        private const int MaxDishSelectorDisplayLengthEnglish = 34;
        private const int MinOverlaySceneDisplayLength = 8;
        private const int MaxOverlaySceneDisplayLengthSetting = 120;
        private const int MaxOverlaySceneDisplayLength = 24;
        private const int MinOverlayDishDisplayLength = 6;
        private const int MaxOverlayDishDisplayLengthSetting = 120;
        private const int MaxOverlayDishDisplayLength = 12;
        private const int MaxOverlayDishDisplayLengthEnglish = 18;
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
        private const int ControllerLookupIntervalFrames = 300;
        private const int ControllerLookupRetryIntervalFrames = 30;
        private const int OverlayRefreshIntervalFrames = 24;
        private const int PreparedSourceRefreshIntervalFrames = 45;
        private const int MaxPreparedSourceRefreshesPerBatch = 1;
        private const int PreparedSourcePruneIntervalFrames = 5400;
        private const int PreparedSourceRemovalGraceFrames = 18;
        private const int PreparedBootstrapStepIntervalFrames = 60;
        private const int PreparedBootstrapFallbackDelayFrames = 900;
        private const int PreparedBootstrapFallbackIntervalFrames = 3600;
        private const int TicketWidgetRefreshDelayFrames = 30;
        private const int TicketWidgetRetryIntervalFrames = 90;
        private const int BaseMenuTicketCapacity = 5;
        private const int MaxReferenceTicketDisplayCount = 5;
        private const int DefaultReferenceTicketDisplayCount = 3;
        private const int ReferenceTicketOrderBase = -512;
        private const float ReferenceTicketSyntheticTimeLimit = 999999f;
        private const int HotkeyFilePollIntervalFrames = 7200;
        private const KeyCode DefaultSettingsWindowHotkey = KeyCode.F6;

        private static readonly Dictionary<string, HashSet<int>> TrackedIdsByScene = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SceneInfo> SceneCache = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, int> CurrentOnMenuCounts = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> PreparedCountsByRecipe = new Dictionary<int, int>();
        private static readonly Dictionary<int, PreparedSourceState> PreparedSourcesByInstanceId = new Dictionary<int, PreparedSourceState>();
        private static readonly Dictionary<int, int> PreparedSourceIdsByGameObjectId = new Dictionary<int, int>();
        private static readonly Dictionary<int, CookedCompositeOrderNode.CookingProgress> PreparedCookStateBySourceId = new Dictionary<int, CookedCompositeOrderNode.CookingProgress>();
        private static readonly Dictionary<int, Component> PreparedSourceComponentByHandlerId = new Dictionary<int, Component>();
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
        private static readonly List<ReferenceTicketCandidate> ReferenceTicketCandidatesBuffer = new List<ReferenceTicketCandidate>();
        private static readonly List<ReferenceTicketState> ReferenceTicketStates = new List<ReferenceTicketState>();
        private static readonly List<ReferenceTicketState> ReferenceTicketStatesForFlowBuffer = new List<ReferenceTicketState>();
        private static readonly List<RecipeFlowGUI> ReferenceTicketFlowsBuffer = new List<RecipeFlowGUI>();
        private static readonly HashSet<int> ReferenceTicketFlowIdsBuffer = new HashSet<int>();
        private static readonly Dictionary<int, ReferenceTicketState> ExistingReferenceTicketStatesByRecipeIdBuffer = new Dictionary<int, ReferenceTicketState>();
        private static readonly HashSet<int> DesiredReferenceTicketRecipeIdsBuffer = new HashSet<int>();
        private static readonly List<object> LayoutReferenceWidgetDataBuffer = new List<object>();
        private static readonly List<object> LayoutDyingReferenceWidgetDataBuffer = new List<object>();
        private static readonly List<object> LayoutRealWidgetDataBuffer = new List<object>();
        private static readonly List<GameSession> FrontendSessionBuffer = new List<GameSession>();
        private static readonly List<int> StaleTicketWidgetIdsBuffer = new List<int>();
        private static readonly HashSet<ClientOrderControllerBase> VisitedOrderControllersBuffer = new HashSet<ClientOrderControllerBase>();
        private static readonly Dictionary<string, ConfigEntry<int>> CategoryTierEntriesByKey = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly StringBuilder OverlayTextBuilder = new StringBuilder(768);
        private static readonly TeamID[] TeamIds = (TeamID[])Enum.GetValues(typeof(TeamID));
        private static readonly VoidGeneric<RecipeFlowGUI.ElementToken> ReferenceTicketExpiredCallback = delegate
        {
        };

        private static readonly ConfigDefinition LegacyEnabledDefinition = new ConfigDefinition("03-已送菜品追踪", "启用已送菜品追踪");
        private static readonly ConfigDefinition LegacyLanguageDefinition = new ConfigDefinition("03-已送菜品追踪", "显示语言");
        private static readonly ConfigDefinition LegacySelectedSceneStateDefinition = new ConfigDefinition("99-内部", "已选关卡内部状态");
        private static readonly ConfigDefinition LegacySettingsWindowHotkeyDefinition = new ConfigDefinition("00-菜单管理", SettingsWindowHotkeyKey);
        private static readonly ConfigDefinition LegacyMenuTicketOnMenuColorDefinition = new ConfigDefinition(TrackerSection, "菜单票据在单颜色");
        private static readonly ConfigDefinition LegacyMenuTicketPreparedColorDefinition = new ConfigDefinition(TrackerSection, "菜单票据已备颜色");
        private static readonly ConfigDefinition LegacyGuessCountDefinition = new ConfigDefinition(TrackerSection, "菜单票据猜单数量");
        private static readonly ConfigDefinition LegacyGuessColorDefinition = new ConfigDefinition(TrackerSection, "菜单票据猜单颜色");
        private static readonly ConfigDefinition LegacyReferenceTicketCountDefinition = new ConfigDefinition(TrackerSection, "菜单票据未备参考数量");
        private static readonly ConfigDefinition LegacyReferenceTicketColorDefinition = new ConfigDefinition(TrackerSection, "菜单票据未备参考颜色");
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
        private static ConfigEntry<int> menuReferenceTicketCount;
        private static ConfigEntry<Color> menuReferenceTicketTintColor;
        private static ConfigEntry<bool> overlayBoldFont;
        private static ConfigEntry<int> overlayMaxDisplayDishes;
        private static ConfigEntry<OverlayTextAlignment> overlayTextAlignment;
        private static ConfigEntry<int> sceneSelectorMaxTextLength;
        private static ConfigEntry<int> dishSelectorMaxTextLength;
        private static ConfigEntry<int> overlaySceneMaxTextLength;
        private static ConfigEntry<int> overlayDishMaxTextLength;
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
        private static Color? migratedMenuTicketOnMenuTintColorValue;
        private static Color? migratedMenuTicketPreparedTintColorValue;
        private static int? migratedReferenceTicketCountValue;
        private static Color? migratedReferenceTicketTintColorValue;
        private static readonly FieldInfo ActiveOrdersField = typeof(ClientOrderControllerBase).GetField("m_activeOrders", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ClientOrderControllerGuiField = typeof(ClientOrderControllerBase).GetField("m_gui", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ActiveOrderType = typeof(ClientOrderControllerBase).GetNestedType("ActiveOrder", BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveOrderRecipeListEntryField = ActiveOrderType != null
            ? ActiveOrderType.GetField("RecipeListEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly Type RecipeFlowRecipeWidgetDataType = typeof(RecipeFlowGUI).GetNestedType("RecipeWidgetData", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowMaxOrdersAllowedField = typeof(RecipeFlowGUI).GetField("m_maxOrdersAllowed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowOccupiedTablesField = typeof(RecipeFlowGUI).GetField("m_occupiedTables", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowWidgetsField = typeof(RecipeFlowGUI).GetField("m_widgets", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowDistanceBetweenOrdersField = typeof(RecipeFlowGUI).GetField("m_distanceBetweenOrders", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowDistanceFromEndOfScreenField = typeof(RecipeFlowGUI).GetField("m_distanceFromEndOfScreen", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowOrderedWidgetsField = typeof(RecipeFlowGUI).GetField("m_ordererWidgets", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowRecipeWidgetDataWidgetField = RecipeFlowRecipeWidgetDataType != null
            ? RecipeFlowRecipeWidgetDataType.GetField("m_widget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo FrontendCoopGameSessionPrefabsField = AccessTools.Field(typeof(T17FrontendFlow), "m_CoopGameSessionPrefabs");
        private static readonly FieldInfo FrontendCompetitiveGameSessionPrefabsField = AccessTools.Field(typeof(T17FrontendFlow), "m_CompetitiveGameSessionPrefabs");
        private static readonly FieldInfo UISubElementContainerContainerField = typeof(UISubElementContainer).GetField("m_container", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetRecipeTreeField = typeof(RecipeWidgetUIController).GetField("m_recipeTree", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_displayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetTopDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_topDisplayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static ClientFlowControllerBase cachedClientFlowController;
        private static int nextClientFlowLookupFrame;
        private static ClientKitchenFlowControllerBase cachedKitchenFlowController;
        private static int nextKitchenFlowLookupFrame;
        private static DLCManager cachedDlcManager;
        private static int nextDlcManagerLookupFrame;
        private static string currentOnMenuCountsSceneName = string.Empty;
        private static string probabilityMapSceneName = string.Empty;
        private static bool currentOnMenuCountsDirty = true;
        private static bool probabilityMapDirty = true;
        private static int nextTrackedSceneRefreshPollFrame;
        private static int nextDiscoveryFlushFrame;
        private static int nextPreparedSourceRefreshFrame;
        private static int nextPreparedSourcePruneFrame;
        private static int nextPreparedBootstrapFrame;
        private static int nextPreparedBootstrapFallbackFrame;
        private static int nextTicketWidgetRefreshFrame;
        private static int nextReferenceTicketSyncFrame;
        private static int nextHotkeyFilePollFrame;
        private static int nextOverlayRefreshFrame;
        private static int lastOverlayBuildFrame = int.MinValue;
        private static int cachedCurrentSceneInfoFrame = int.MinValue;
        private static bool overlayVisible;
        private static bool overlayDirty = true;
        private static bool ticketWidgetsDirty = true;
        private static bool referenceTicketsDirty = true;
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
        private static int overlayRowsVersion;
        private static int cachedOverlayRowsVersion = -1;
        private static string cachedOverlayRowsSceneName = string.Empty;
        private static bool cachedOverlayRowsShowPrepared;

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
                "在单颜色",
                migratedMenuTicketOnMenuTintColorValue ?? new Color(1f, 0.76f, 0.34f, 1f),
                "菜单栏里“在单未备”的颜色。A 通道控制整张单的透明度。");
            menuTicketPreparedTintColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "已备颜色",
                migratedMenuTicketPreparedTintColorValue ?? new Color(0.86f, 0.98f, 0.86f, 1f),
                "菜单栏里“已备”的颜色。A 通道控制整张单的透明度。");
            menuReferenceTicketCount = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "最大猜单数量",
                migratedReferenceTicketCountValue ?? DefaultReferenceTicketDisplayCount,
                new ConfigDescription("在菜单栏额外显示多少个猜单。0 关闭，最多 5 个。", new AcceptableValueRange<int>(0, MaxReferenceTicketDisplayCount)));
            menuReferenceTicketTintColor = _MODEntry.SettingsConfig.Bind<Color>(
                TrackerSection,
                "猜单颜色",
                migratedReferenceTicketTintColorValue ?? new Color(0.49f, 0.59f, 0.67f, 0.62f),
                "菜单栏里猜单的颜色。A 通道控制整张单的透明度，显示时会额外压暗一点。");
            languageMode = _MODEntry.SettingsConfig.Bind<TrackerLanguage>(
                TrackerSection,
                "显示语言",
                migratedLanguageValue ?? TrackerLanguage.Auto,
                "Auto / English / Chinese");
            BindCategoryTierConfigEntries();
            ApplyConfiguredCategoryTierOverrides();
            sceneSelectorMaxTextLength = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "关卡名称最大长度",
                MaxSceneSelectorDisplayLength,
                new ConfigDescription("关卡选择按钮与下拉列表的最大字符数。", new AcceptableValueRange<int>(MinSceneSelectorDisplayLength, MaxSceneSelectorDisplayLengthSetting)));
            dishSelectorMaxTextLength = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "菜品名称最大长度",
                GetInitialDishSelectorDisplayLength(),
                new ConfigDescription("菜单窗口里追踪菜品列表的最大字符数。", new AcceptableValueRange<int>(MinDishSelectorDisplayLength, MaxDishSelectorDisplayLengthSetting)));
            overlaySceneMaxTextLength = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗关卡名称最大长度",
                MaxOverlaySceneDisplayLength,
                new ConfigDescription("悬浮窗里关卡标题的最大字符数。", new AcceptableValueRange<int>(MinOverlaySceneDisplayLength, MaxOverlaySceneDisplayLengthSetting)));
            overlayDishMaxTextLength = _MODEntry.SettingsConfig.Bind<int>(
                TrackerSection,
                "悬浮窗菜品名称最大长度",
                GetInitialOverlayDishDisplayLength(),
                new ConfigDescription("悬浮窗里菜品名称的最大字符数。", new AcceptableValueRange<int>(MinOverlayDishDisplayLength, MaxOverlayDishDisplayLengthSetting)));
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
                if (ReferenceTicketStates.Count > 0)
                {
                    ClearReferenceTickets();
                }

                if (TicketWidgetsByInstanceId.Count > 0)
                {
                    ClearTicketWidgetState();
                }
            }
            else
            {
                if (referenceTicketsDirty && Time.frameCount >= nextReferenceTicketSyncFrame)
                {
                    SyncReferenceTickets();
                }

                if (!shouldTintMenuTickets)
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
            settingsWindowRect = GUI.Window(SettingsWindowId, settingsWindowRect, DrawSettingsWindow, Ui("菜单管理", "Menu Manager"));
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;
        }

    }
}
