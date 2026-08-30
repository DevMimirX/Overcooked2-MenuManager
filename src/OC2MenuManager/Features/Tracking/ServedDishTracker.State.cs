// Owns tracker configuration, reflection contracts, compatibility indexes, and
// reusable runtime buffers. State here is main-thread only and is reused to keep
// order and prepared-dish events allocation-free after capacity warm-up.
using System;
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
        private const float SceneDropdownHeightRatio = 0.55f;
        private const float SceneDropdownMinHeight = 160f;
        private const float SceneDropdownMaxHeight = 420f;
        private const float SceneDropdownRowHeight = 26f;
        private const int SceneDropdownOverscanRows = 1;
        private const string SceneSearchControlName = "OC2MenuManager.SceneSearch";
        private const int SceneRefreshIntervalInRound = 600;
        private const int SceneRefreshIntervalInRoundWithConfigOpen = 120;
        private const int SceneRefreshIntervalOutOfRound = 120;
        private const int DiscoveryFlushIntervalFrames = 900;
        private const int ControllerLookupRetryIntervalFrames = 120;
        private const int ManyRecipesCatalogRetryIntervalFrames = 120;
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
        private const int MaxTicketWidgetReconciliationAttempts = 8;
        private const int BaseMenuTicketCapacity = 5;
        private const int MaxCombinedActiveTicketCount = 10;
        private const int MaxReferenceTicketDisplayCount = 5;
        private const int DefaultReferenceTicketDisplayCount = 3;
        private const int ReferenceTicketOrderBase = 1000000;
        private const float ReferenceTicketSyntheticTimeLimit = 999999f;
        private const int HotkeyFilePollIntervalFrames = 7200;
        private const KeyCode DefaultSettingsWindowHotkey = KeyCode.F6;

        private static readonly Dictionary<string, HashSet<int>> TrackedIdsByScene = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SceneInfo> SceneCache = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TeamID, RunInfo> RunsByTeam = new Dictionary<TeamID, RunInfo>();
        private static readonly HashSet<TeamID> ReconstructionReadyTeams = new HashSet<TeamID>();
        private static readonly Dictionary<TeamID, Dictionary<int, int>> CurrentOnMenuCountsByTeam = new Dictionary<TeamID, Dictionary<int, int>>();
        private static readonly Dictionary<int, int> CombinedOnMenuCountsBuffer = new Dictionary<int, int>();
        private static readonly Dictionary<TeamID, ServerOrderControllerBase> AuthoritativeOrderControllersByTeam = new Dictionary<TeamID, ServerOrderControllerBase>();
        private static readonly Dictionary<int, int> PreparedCountsByRecipe = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> PreparedCompatibilityCountsByRecipe = new Dictionary<int, int>();
        private static readonly Dictionary<int, PreparedSourceState> PreparedSourcesByInstanceId = new Dictionary<int, PreparedSourceState>();
        private static readonly Dictionary<int, int> PreparedSourceIdsByGameObjectId = new Dictionary<int, int>();
        private static readonly Dictionary<int, CookedCompositeOrderNode.CookingProgress> PreparedCookStateBySourceId = new Dictionary<int, CookedCompositeOrderNode.CookingProgress>();
        private static readonly Dictionary<int, Component> PreparedSourceComponentByHandlerId = new Dictionary<int, Component>();
        private static readonly Dictionary<int, TicketWidgetState> TicketWidgetsByInstanceId = new Dictionary<int, TicketWidgetState>();
        private static readonly List<SceneInfo> KnownScenes = new List<SceneInfo>();
        private static readonly List<SceneInfo> CachedDIYScenes = new List<SceneInfo>();
        private static readonly List<SceneInfo> FilteredSelectableScenesBuffer = new List<SceneInfo>();
        private static readonly List<DIYLevelDescriptor> DIYLevelDescriptorsBuffer = new List<DIYLevelDescriptor>();
        private static readonly List<DIYRecipeDescriptor> DIYRecipeDescriptorsBuffer = new List<DIYRecipeDescriptor>();
        private static readonly List<RecipeCategoryEvidence> DIYRecipeEvidenceBuffer = new List<RecipeCategoryEvidence>();
        private static readonly List<RecipeList.Entry> RuntimeRecipeEntriesBuffer = new List<RecipeList.Entry>();
        private static readonly List<RecipeList.Entry> RuntimePhaseRecipeEntriesBuffer = new List<RecipeList.Entry>();
        private static readonly List<int> RuntimeOrderedRecipeIdsBuffer = new List<int>();
        private static readonly HashSet<int> RuntimeRecipeIdsBuffer = new HashSet<int>();
        private static readonly List<int> StaleRecipeIdsBuffer = new List<int>();
        private static readonly List<SceneDirectoryData.SceneDirectoryEntry> AvailableSceneEntriesBuffer = new List<SceneDirectoryData.SceneDirectoryEntry>();
        private static readonly List<SceneInfo> DIYScenesRefreshBuffer = new List<SceneInfo>();
        private static readonly List<SceneInfo> CachedSceneInfosBuffer = new List<SceneInfo>();
        private static readonly HashSet<string> KnownSceneNamesBuffer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> OrderedSceneSelectorValues = new List<string>();
        private static readonly List<SceneInfo> SelectableScenesBuffer = new List<SceneInfo>();
        private static readonly List<OverlayRow> EmptyOverlayRowsBuffer = new List<OverlayRow>();
        private static readonly List<OverlayRenderRow> OverlayRenderRowsBuffer = new List<OverlayRenderRow>();
        private static readonly List<RecipeSelectionGroup> CategorySelectionGroupsBuffer = new List<RecipeSelectionGroup>();
        private static readonly List<TicketWidgetState> TicketWidgetsBuffer = new List<TicketWidgetState>();
        private static readonly HashSet<int> DirtyPreparedSourceIds = new HashSet<int>();
        private static readonly List<int> PreparedSourceRefreshBuffer = new List<int>();
        private static readonly List<int> PreparedSourceRemovalBuffer = new List<int>();
        private static readonly Dictionary<string, string> SceneSelectorValuesByScene = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> SceneNamesBySelectorValue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, double> ProbabilityByRecipeBuffer = new Dictionary<int, double>();
        private static readonly List<RecipeList.Entry> ProbabilityExtensionEntriesBuffer = new List<RecipeList.Entry>();
        private static readonly HashSet<int> ReconstructableRecipeIdsBuffer = new HashSet<int>();
        private static readonly Dictionary<int, int> ReconstructedRecipeCountsBuffer = new Dictionary<int, int>();
        private static RecipeList.Entry[] ProbabilityEntriesBuffer = new RecipeList.Entry[0];
        private static int[] ProbabilityRecipeIdsBuffer = new int[0];
        private static int[] ProbabilityCumulativeFrequenciesBuffer = new int[0];
        private static double[] ProbabilityEntryValuesBuffer = new double[0];
        private static double[] ProbabilityRawWeightsBuffer = new double[0];
        private static float[] ProbabilityCarnivalWeightsBuffer = new float[0];
        private static readonly Dictionary<int, int> MenuOrderByRecipeBuffer = new Dictionary<int, int>();
        private static readonly List<int> PreparedCandidateRecipeIdsBuffer = new List<int>();
        private static readonly List<RunInfo> PreparedCandidateRunsBuffer = new List<RunInfo>();
        private static readonly List<Dictionary<int, double>> PreparedCandidateProbabilityMapsBuffer = new List<Dictionary<int, double>>();
        private static readonly List<List<int>> PreparedCandidateActiveRecipeIdsBuffer = new List<List<int>>();
        private static readonly List<int> PreparedMatchedRecipeIdsBuffer = new List<int>();
        private static readonly HashSet<int> PreparedMatchedRecipeIdsSetBuffer = new HashSet<int>();
        private static readonly List<PreparedRecipeAssignmentCandidate> PreparedAssignmentCandidatesBuffer = new List<PreparedRecipeAssignmentCandidate>();
        private static readonly Dictionary<int, PreparedTicketPriority> PreparedTicketPrioritiesByRecipeBuffer = new Dictionary<int, PreparedTicketPriority>();
        private static readonly List<int> PreparedCompatibilityRemovalBuffer = new List<int>();
        private static readonly List<ReferenceTicketCandidate> ReferenceTicketCandidatesBuffer = new List<ReferenceTicketCandidate>();
        private static readonly List<ReferenceTicketCandidate> ReferenceTicketCandidatePool = new List<ReferenceTicketCandidate>();
        private static readonly List<ReferenceTicketState> ReferenceTicketStates = new List<ReferenceTicketState>();
        private static readonly List<ReferenceTicketState> ReferenceTicketStatesForFlowBuffer = new List<ReferenceTicketState>();
        private static readonly List<RecipeFlowGUI.RecipeWidgetData> RealTicketWidgetDataBuffer = new List<RecipeFlowGUI.RecipeWidgetData>();
        private static readonly List<RecipeFlowGUI.RecipeWidgetData> ReferenceTicketWidgetDataBuffer = new List<RecipeFlowGUI.RecipeWidgetData>();
        private static readonly List<TeamFlowContext> TeamFlowContextsBuffer = new List<TeamFlowContext>();
        private static readonly List<TeamID> ActiveTeamIdsBuffer = new List<TeamID>();
        private static readonly TeamID[] SupportedTeamIds = new TeamID[] { TeamID.One, TeamID.Two };
        private static readonly TeamFlowContext[] SupportedTeamFlowContexts = new TeamFlowContext[]
        {
            new TeamFlowContext(),
            new TeamFlowContext()
        };
        private static readonly HashSet<int> ReferenceTicketFlowIdsBuffer = new HashSet<int>();
        private static readonly Dictionary<int, int> ReferenceRealTicketLimitByFlowId = new Dictionary<int, int>();
        private static readonly Dictionary<int, ReferenceTicketState> ExistingReferenceTicketStatesByRecipeIdBuffer = new Dictionary<int, ReferenceTicketState>();
        private static readonly HashSet<int> DesiredReferenceTicketRecipeIdsBuffer = new HashSet<int>();
        private static readonly List<GameSession> FrontendSessionBuffer = new List<GameSession>();
        private static readonly List<int> StaleTicketWidgetIdsBuffer = new List<int>();
        private static readonly HashSet<ClientOrderControllerBase> VisitedOrderControllersBuffer = new HashSet<ClientOrderControllerBase>();
        private static readonly Dictionary<string, ConfigEntry<int>> CategoryTierEntriesByKey = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly StringBuilder OverlayTextBuilder = new StringBuilder(768);
        private static readonly Comparison<OverlayRow> OverlayRowsWithPreparedComparison = CompareOverlayRowsWithPrepared;
        private static readonly Comparison<OverlayRow> OverlayRowsWithoutPreparedComparison = CompareOverlayRowsWithoutPrepared;
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
        private static ConfigEntry<bool> overlayEnabled;
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
        private static SettingsInputBlocker settingsInputBlocker;
        private static string selectionFilePath;
        private static int nextSceneRefreshFrame;
        private static int nextDIYSceneRefreshFrame;
        private static int nextManyRecipesCatalogRetryFrame;
        private static int idScanSceneCount;
        private static int idConflictCount;
        private static string idConflictSample = string.Empty;
        private static string lastIdScanLog = string.Empty;
        private static string lastConfigSyncError = string.Empty;
        private static string lastDIYCatalogStatusSignature = string.Empty;
        private static string configuredSceneName = string.Empty;
        private static string sceneSearchText = string.Empty;
        private static string cachedSceneSearchQuery = string.Empty;
        private static string diyCatalogDetail = string.Empty;
        private static int knownScenesRevision;
        private static int selectableScenesRevision;
        private static int cachedSceneSearchRevision = -1;
        private static int filteredDIYSceneCount;
        private static int selectableDIYSceneCount;
        private static int diyCatalogAcceptedSceneCount;
        private static int diyCatalogRejectedEntryCount;
        private static string sceneDropdownKeyboardSceneName = string.Empty;
        private static bool sceneDropdownRetargetFirstResult;
        private static int cachedSelectableKnownScenesRevision = -1;
        private static bool cachedSelectableLockedToRound;
        private static string cachedSelectableCurrentSceneName = string.Empty;
        private static int cachedSceneSelectorMapRevision = -1;
        private static int cachedSceneSelectorMaxLength = -1;
        private static int categoryTierRevision;
        private static int cachedCategorySelectionCatalogRevision = -1;
        private static int cachedCategorySelectionTierRevision = -1;
        private static string cachedCategorySelectionSceneName = string.Empty;
        private static SceneInfo cachedCategorySelectionScene;
        private static bool cachedCategorySelectionChinese;
        private static int cachedSecondarySelectionCatalogRevision = -1;
        private static string cachedSecondarySelectionSceneName = string.Empty;
        private static SceneInfo cachedSecondarySelectionScene;
        private static SceneRecipeSelectionGroupSet cachedSecondarySelectionGroupSet;
        private static readonly HashSet<string> SecondarySelectionWarningScenes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool? migratedEnabledValue;
        private static TrackerLanguage? migratedLanguageValue;
        private static Color? migratedMenuTicketOnMenuTintColorValue;
        private static Color? migratedMenuTicketPreparedTintColorValue;
        private static int? migratedReferenceTicketCountValue;
        private static Color? migratedReferenceTicketTintColorValue;
        private static readonly FieldInfo ActiveOrdersField = typeof(ClientOrderControllerBase).GetField("m_activeOrders", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ServerRoundDataField = typeof(ServerOrderControllerBase).GetField("m_roundData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ServerRoundInstanceDataField = typeof(ServerOrderControllerBase).GetField("m_roundInstanceData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type RoundInstanceDataType = typeof(RoundData).GetNestedType("RoundInstanceData", BindingFlags.NonPublic);
        private static readonly FieldInfo RoundInstanceRecipeCountField = RoundInstanceDataType != null
            ? RoundInstanceDataType.GetField("RecipeCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo RoundInstanceCumulativeFrequenciesField = RoundInstanceDataType != null
            ? RoundInstanceDataType.GetField("CumulativeFrequencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly Type DynamicRoundInstanceDataType = typeof(DynamicRoundData).GetNestedType("DynamicRoundInstanceData", BindingFlags.NonPublic);
        private static readonly FieldInfo DynamicRoundInstanceCurrentPhaseField = DynamicRoundInstanceDataType != null
            ? DynamicRoundInstanceDataType.GetField("CurrentPhase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo ClientOrderControllerGuiField = typeof(ClientOrderControllerBase).GetField("m_gui", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ActiveOrderType = typeof(ClientOrderControllerBase).GetNestedType("ActiveOrder", BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveOrderRecipeListEntryField = ActiveOrderType != null
            ? ActiveOrderType.GetField("RecipeListEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo ActiveOrderUiTokenField = ActiveOrderType != null
            ? ActiveOrderType.GetField("UIToken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        private static readonly FieldInfo RecipeFlowMaxOrdersAllowedField = typeof(RecipeFlowGUI).GetField("m_maxOrdersAllowed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowOccupiedTablesField = typeof(RecipeFlowGUI).GetField("m_occupiedTables", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowWidgetsField = typeof(RecipeFlowGUI).GetField("m_widgets", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeFlowNextIndexField = typeof(RecipeFlowGUI).GetField("m_nextIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RecipeFlowGetMaxOrderNumberMethod = AccessTools.Method(typeof(RecipeFlowGUI), "GetMaxOrderNumber");
        private static readonly FieldInfo FrontendCoopGameSessionPrefabsField = AccessTools.Field(typeof(T17FrontendFlow), "m_CoopGameSessionPrefabs");
        private static readonly FieldInfo FrontendCompetitiveGameSessionPrefabsField = AccessTools.Field(typeof(T17FrontendFlow), "m_CompetitiveGameSessionPrefabs");
        private static readonly FieldInfo RecipeWidgetRecipeTreeField = typeof(RecipeWidgetUIController).GetField("m_recipeTree", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_displayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RecipeWidgetTopDisplayConfigField = typeof(RecipeWidgetUIController).GetField("m_topDisplayConfig", BindingFlags.Instance | BindingFlags.NonPublic);
        private static ClientFlowControllerBase cachedClientFlowController;
        private static int nextClientFlowLookupFrame;
        private static ClientKitchenFlowControllerBase cachedKitchenFlowController;
        private static int nextKitchenFlowLookupFrame;
        private static DLCManager cachedDlcManager;
        private static int nextDlcManagerLookupFrame;
        private static WorldMapFlowController cachedWorldMapFlowController;
        private static int currentDynamicPhaseIndex;
        private static string currentOnMenuCountsSceneName = string.Empty;
        private static bool currentOnMenuCountsDirty = true;
        private static int nextTrackedSceneRefreshPollFrame;
        private static int nextDiscoveryFlushFrame;
        private static int nextPreparedSourceRefreshFrame;
        private static int nextPreparedSourcePruneFrame;
        private static int nextPreparedBootstrapFrame;
        private static int nextPreparedBootstrapFallbackFrame;
        private static int nextTicketWidgetRefreshFrame;
        private static int ticketWidgetReconciliationAttempts;
        private static int nextReferenceTicketSyncFrame;
        private static int nextHotkeyFilePollFrame;
        private static int nextOverlayRefreshFrame;
        private static int cachedCurrentSceneInfoFrame = int.MinValue;
        private static bool overlayVisible;
        private static bool overlayDirty = true;
        private static bool ticketWidgetsDirty = true;
        private static bool ticketWidgetReconciliationPending;
        private static bool referenceTicketsDirty = true;
        private static bool realTicketWidgetTintActive;
        private static bool invalidReferenceTableWarningLogged;
        private static bool invalidRealTableWarningLogged;
        private static bool invalidTableReleaseWarningLogged;
        private static bool ticketAdmissionFailureWarningLogged;
        private static bool ticketWidgetReconciliationContractWarningLogged;
        private static bool ticketWidgetReconciliationRetryWarningLogged;
        private static bool referenceTicketAddFailureLogged;
        private static bool trackingHookFailureWarningLogged;
        private static bool cachedCurrentSceneInfoValid;
        private static bool lastMenuTicketTintEnabled = true;
        private static bool preparedSourceBootstrapComplete = true;
        private static bool settingsWindowVisible;
        private static bool settingsInputBlockerUnavailable;
        private static bool sceneDropdownExpanded;
        private static bool sceneSearchFocusRequested;
        private static SceneDropdownScrollRequest sceneDropdownScrollRequest;
        private static bool diyCatalogHasSnapshot;
        private static bool diyCatalogUsingRetainedSnapshot;
        private static bool diyCatalogStatusHadIssue;
        private static bool capturingHotkey;
        private static int lastSettingsWindowToggleFrame = int.MinValue;
        private static int preparedSourceBootstrapStage;
        private static SceneInfo cachedCurrentSceneInfo;
        private static KeyCode settingsWindowHotkey = DefaultSettingsWindowHotkey;
        private static DIYLevelCatalogReadState diyCatalogReadState = DIYLevelCatalogReadState.Unavailable;
        private static DateTime hotkeyConfigLastWriteUtc = DateTime.MinValue;
        private static string overlayHeaderText = string.Empty;
        private static string overlayFooterText = string.Empty;
        private static string preparedSourceSceneName = string.Empty;
        private static string preparedCandidateSceneName = string.Empty;
        private static Rect settingsWindowRect = new Rect(140f, 90f, SettingsWindowDefaultWidth, SettingsWindowDefaultHeight);
        private static Rect sceneDropdownScreenRect;
        private static Vector2 settingsWindowScrollPosition = Vector2.zero;
        private static Vector2 sceneDropdownScrollPosition = Vector2.zero;
        private static float pendingSceneDropdownWheelDelta;
        private static bool sceneDropdownScreenRectValid;
        private static bool preparedCandidateRecipeIdsDirty = true;
        private static int overlayRowsVersion;

    }
}
