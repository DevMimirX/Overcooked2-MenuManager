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

        private enum SelectorPage
        {
            LevelList,
            DishList
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
            public override void OnSetUp()
            {
            }

            public override void OnUpdate()
            {
            }

            public override void OnDraw(ref Rect rect, GUIStyle style)
            {
                string text = BuildOverlayText();
                if (!string.IsNullOrEmpty(text))
                {
                    base.DrawText(ref rect, style, text);
                }
            }
        }

        private sealed class OverlayRow
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
        }

        private static readonly Dictionary<string, HashSet<int>> TrackedIdsByScene = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SceneInfo> SceneCache = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<SceneInfo> SelectorScenes = new List<SceneInfo>();

        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<KeyCode> toggleSelectorKey;
        private static ConfigEntry<KeyCode> cycleLanguageKey;
        private static ConfigEntry<TrackerLanguage> languageMode;
        private static ConfigEntry<int> selectorFontSize;
        private static ConfigEntry<int> selectorWindowX;
        private static ConfigEntry<int> selectorWindowY;
        private static ConfigEntry<int> selectorWindowWidth;
        private static ConfigEntry<int> selectorWindowHeight;

        private static DebugOverlayHost overlayHost;
        private static bool selectorVisible;
        private static Vector2 sceneScroll;
        private static Vector2 dishScroll;
        private static int selectedSceneIndex;
        private static int nextLobbyRefreshFrame;
        private static int idScanSceneCount;
        private static int idConflictCount;
        private static string idConflictSample = string.Empty;
        private static string lastIdScanLog = string.Empty;
        private static string selectionFilePath;
        private static RunInfo currentRun;
        private static bool selectorCurrentLevelOnly;
        private static bool selectorUnsupportedCurrentLevel;
        private static SelectorPage selectorPage;
        private static Rect selectorWindowRect;
        private const int SelectorWindowId = 941273;

        public static void Awake()
        {
            enabled = _MODEntry.Instance.Config.Bind<bool>("03-已送菜品追踪", "启用已送菜品追踪", true, "标准关卡已送菜品追踪，支持在任意界面选择要追踪的菜品；若已在关卡中，则只显示当前关卡。");
            toggleSelectorKey = _MODEntry.Instance.Config.Bind<KeyCode>("03-已送菜品追踪", "打开追踪选择器", KeyCode.F6, "在任意界面打开菜品追踪选择器；若已在关卡中，则只显示当前关卡。");
            cycleLanguageKey = _MODEntry.Instance.Config.Bind<KeyCode>("03-已送菜品追踪", "切换中英显示", KeyCode.F7, "切换追踪器显示语言。");
            languageMode = _MODEntry.Instance.Config.Bind<TrackerLanguage>("03-已送菜品追踪", "显示语言", TrackerLanguage.Auto, "Auto / English / Chinese");
            selectorFontSize = _MODEntry.Instance.Config.Bind<int>("03-已送菜品追踪", "选择器字体大小", 14, new ConfigDescription("已送菜品追踪选择器的字体大小。", new AcceptableValueRange<int>(10, 28)));
            selectorWindowX = _MODEntry.Instance.Config.Bind<int>("03-已送菜品追踪", "选择器窗口X", 24, new ConfigDescription("已送菜品追踪选择器窗口左上角 X 坐标。", new AcceptableValueRange<int>(0, 4000)));
            selectorWindowY = _MODEntry.Instance.Config.Bind<int>("03-已送菜品追踪", "选择器窗口Y", 120, new ConfigDescription("已送菜品追踪选择器窗口左上角 Y 坐标。", new AcceptableValueRange<int>(0, 4000)));
            selectorWindowWidth = _MODEntry.Instance.Config.Bind<int>("03-已送菜品追踪", "选择器窗口宽度", 560, new ConfigDescription("已送菜品追踪选择器窗口宽度。", new AcceptableValueRange<int>(420, 1400)));
            selectorWindowHeight = _MODEntry.Instance.Config.Bind<int>("03-已送菜品追踪", "选择器窗口高度", 640, new ConfigDescription("已送菜品追踪选择器窗口高度。", new AcceptableValueRange<int>(360, 1200)));

            selectionFilePath = Path.Combine(Paths.ConfigPath, "HostUtilities-ServedDishTrackerSelections.txt");
            LoadSelections();
            selectorWindowRect = new Rect(selectorWindowX.Value, selectorWindowY.Value, selectorWindowWidth.Value, selectorWindowHeight.Value);

            overlayHost = new DebugOverlayHost(TextAnchor.UpperRight, delegate(GUIStyle style)
            {
                float scale = Mathf.Max(_MODEntry.dpiScaleFactor, 1f);
                return new Rect(Screen.width - (560f * scale), 40f * scale, 540f * scale, 560f * scale);
            });
            overlayHost.AddDisplay(new OverlayDisplay());

            ModuleUtility.RegisterHarmony(typeof(ServedDishTracker));
        }

        public static void Update()
        {
            if (enabled.Value)
            {
                overlayHost.Update();
            }

            if (Input.GetKeyDown(toggleSelectorKey.Value))
            {
                if (selectorVisible)
                {
                    CloseSelector();
                }
                else
                {
                    OpenSelector();
                }
            }
            if (Input.GetKeyDown(cycleLanguageKey.Value))
            {
                languageMode.Value = languageMode.Value == TrackerLanguage.Auto
                    ? TrackerLanguage.English
                    : (languageMode.Value == TrackerLanguage.English ? TrackerLanguage.Chinese : TrackerLanguage.Auto);
            }

            if (selectorVisible)
            {
                RefreshSelectableScenes(forceRefresh: false);
            }
        }

        public static void OnGUI()
        {
            if (enabled.Value)
            {
                overlayHost.OnGUI();
            }
            if (selectorVisible)
            {
                selectorWindowRect = ClampWindowRect(selectorWindowRect);
                Rect newRect = GUI.Window(SelectorWindowId, selectorWindowRect, DrawSelectorWindow, UseChinese() ? "菜单管理" : "Menu Manager");
                selectorWindowRect = ClampWindowRect(newRect);
                SaveSelectorWindowRect();
            }
            else if (!IsCurrentLevelContext())
            {
                DrawSelectorHint();
            }
        }

        [HarmonyPatch(typeof(LoadingScreenFlow), "NextScene", MethodType.Getter)]
        [HarmonyPrefix]
        private static void LoadingScreenFlow_NextScene_Prefix()
        {
            currentRun = null;
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
        }

        [HarmonyPatch(typeof(ClientDynamicFlowController), "OnDynamicLevelMessage")]
        [HarmonyPostfix]
        private static void ClientDynamicFlowController_OnDynamicLevelMessage_Postfix(Serialisable _serialisable)
        {
            DynamicLevelMessage message = _serialisable as DynamicLevelMessage;
            ResetProbabilityState(message != null ? message.m_phase : 0);
        }

        private static void RefreshSelectableScenes(bool forceRefresh)
        {
            if (!forceRefresh && Time.frameCount < nextLobbyRefreshFrame)
            {
                return;
            }

            nextLobbyRefreshFrame = Time.frameCount + 60;

            string selectedSceneName = SelectorScenes.Count > 0 && selectedSceneIndex >= 0 && selectedSceneIndex < SelectorScenes.Count
                ? SelectorScenes[selectedSceneIndex].SceneName
                : GetConfiguredLobbySceneName();

            SelectorScenes.Clear();
            selectorCurrentLevelOnly = false;
            selectorUnsupportedCurrentLevel = false;

            SceneInfo currentScene;
            if (TryGetCurrentSceneInfo(out currentScene))
            {
                selectorCurrentLevelOnly = true;
                SelectorScenes.Add(currentScene);
                SceneCache[currentScene.SceneName] = currentScene;
                UpdateIdScanStatus(SelectorScenes);
                DishNameCatalog.FlushDiscoveryReport();
                selectedSceneIndex = 0;
                dishScroll = Vector2.zero;
                selectorPage = SelectorPage.DishList;
                return;
            }

            SceneDirectoryData.PerPlayerCountDirectoryEntry currentVariant;
            if (TryGetCurrentSceneVariant(out currentVariant))
            {
                selectorCurrentLevelOnly = true;
                selectorUnsupportedCurrentLevel = currentVariant.LevelConfig == null || IsHordeLevel(currentVariant.LevelConfig);
                UpdateIdScanStatus(SelectorScenes);
                dishScroll = Vector2.zero;
                selectorPage = SelectorPage.DishList;
                return;
            }

            HashSet<string> seenScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<SceneDirectoryData.SceneDirectoryEntry> entries = GetAvailableSceneEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                AddSceneFromEntry(SelectorScenes, seenScenes, entries[i]);
            }

            AddCachedScenes(SelectorScenes, seenScenes);
            SelectorScenes.Sort(delegate(SceneInfo a, SceneInfo b)
            {
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            UpdateIdScanStatus(SelectorScenes);
            DishNameCatalog.FlushDiscoveryReport();
            if (SelectorScenes.Count == 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(selectedSceneName) && SceneCache.ContainsKey("s_test_level"))
            {
                selectedSceneName = "s_test_level";
            }

            int newIndex = SelectorScenes.FindIndex(delegate(SceneInfo x) { return string.Equals(x.SceneName, selectedSceneName, StringComparison.OrdinalIgnoreCase); });
            if (newIndex >= 0 && newIndex != selectedSceneIndex)
            {
                dishScroll = Vector2.zero;
            }
            selectedSceneIndex = newIndex >= 0 ? newIndex : 0;
            if (selectorCurrentLevelOnly)
            {
                selectorPage = SelectorPage.DishList;
            }
        }

        private static void OpenSelector()
        {
            sceneScroll = Vector2.zero;
            dishScroll = Vector2.zero;
            selectorWindowRect = new Rect(selectorWindowX.Value, selectorWindowY.Value, selectorWindowWidth.Value, selectorWindowHeight.Value);
            RefreshSelectableScenes(forceRefresh: true);
            selectorPage = selectorCurrentLevelOnly ? SelectorPage.DishList : SelectorPage.LevelList;
            selectorVisible = true;
        }

        private static void CloseSelector()
        {
            selectorVisible = false;
        }

        private static List<SceneDirectoryData.SceneDirectoryEntry> GetAvailableSceneEntries()
        {
            List<SceneDirectoryData.SceneDirectoryEntry> entries = new List<SceneDirectoryData.SceneDirectoryEntry>();

            AddEntries(entries, MenuLevelHelper.GetLevelList());
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

        private static void AddCachedScenes(List<SceneInfo> scenes, HashSet<string> seenScenes)
        {
            foreach (SceneInfo scene in SceneCache.Values)
            {
                if (scene == null || string.IsNullOrEmpty(scene.SceneName) || scene.OrderedRecipes.Count == 0)
                {
                    continue;
                }

                if (!seenScenes.Add(scene.SceneName))
                {
                    continue;
                }

                scenes.Add(scene);
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

        private static bool IsCurrentLevelContext()
        {
            SceneDirectoryData.PerPlayerCountDirectoryEntry currentVariant;
            return TryGetCurrentSceneVariant(out currentVariant);
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
            FieldInfo field = AccessTools.Field(typeof(T17FrontendFlow), fieldName);
            object dataContainer = field != null ? field.GetValue(frontendFlow) : null;
            if (dataContainer == null)
            {
                return sessions;
            }

            PropertyInfo allDataProperty = dataContainer.GetType().GetProperty("AllData");
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

        private static string GetConfiguredLobbySceneName()
        {
            return null;
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
            if (SceneCache.TryGetValue(sceneName, out scene))
            {
                return true;
            }

            LevelConfigBase levelConfig = sceneVariant.LevelConfig ?? GameUtils.GetLevelConfig();
            if (levelConfig == null || IsHordeLevel(levelConfig))
            {
                return false;
            }

            scene = BuildSceneInfo(sceneName, sceneName, levelConfig);
            if (scene != null)
            {
                SceneCache[scene.SceneName] = scene;
                DishNameCatalog.FlushDiscoveryReport();
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
            }

            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                int recipeId = scene.OrderedRecipes[i].Id;
                if (!currentRun.AddedCounts.ContainsKey(recipeId))
                {
                    currentRun.AddedCounts.Add(recipeId, 0);
                }
                if (!currentRun.ServedCounts.ContainsKey(recipeId))
                {
                    currentRun.ServedCounts.Add(recipeId, 0);
                }
            }

            return currentRun;
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
        }

        private static string BuildOverlayText()
        {
            SceneInfo scene;
            if (!TryGetCurrentSceneInfo(out scene) || scene.OrderedRecipes.Count == 0)
            {
                return string.Empty;
            }

            RunInfo run = EnsureRun(scene);
            List<OverlayRow> rows = new List<OverlayRow>();
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                if (!IsTracked(scene, recipe.Id))
                {
                    continue;
                }

                OverlayRow row = new OverlayRow();
                row.Recipe = recipe;
                row.Probability = GetProbability(scene, run, recipe.Id);
                row.Served = GetCount(run.ServedCounts, recipe.Id);
                rows.Add(row);
            }

            bool chinese = UseChinese();
            if (rows.Count == 0)
            {
                return chinese
                    ? "已送菜品追踪\n当前关卡没有勾选任何追踪菜品。\n按 F6 打开选择器并调整当前关卡。"
                    : "Served Dish Tracker\nNo dishes are tracked for this scene.\nPress F6 to adjust dishes for the current scene.";
            }

            rows.Sort(delegate(OverlayRow a, OverlayRow b)
            {
                int probabilityCompare = b.Probability.CompareTo(a.Probability);
                if (probabilityCompare != 0)
                {
                    return probabilityCompare;
                }

                int servedCompare = b.Served.CompareTo(a.Served);
                if (servedCompare != 0)
                {
                    return servedCompare;
                }
                return string.Compare(GetRecipeDisplayName(a.Recipe), GetRecipeDisplayName(b.Recipe), StringComparison.OrdinalIgnoreCase);
            });

            StringBuilder builder = new StringBuilder();
            builder.Append(chinese ? "已送菜品追踪" : "Served Dish Tracker").Append('\n');
            builder.Append(scene.SceneName).Append(" | ");
            builder.Append(chinese ? "已追踪 " : "Tracking ");
            builder.Append(rows.Count).Append('/').Append(scene.OrderedRecipes.Count).Append('\n');
            builder.Append(chinese ? "按概率排序" : "Sorted by probability").Append(" | ");
            builder.Append(chinese ? "语言: " : "Language: ").Append(GetLanguageLabel(chinese)).Append('\n');

            int maxRows = Math.Min(rows.Count, 12);
            for (int i = 0; i < maxRows; i++)
            {
                OverlayRow row = rows[i];
                builder.Append(i + 1).Append(". ");
                builder.Append(GetRecipeDisplayName(row.Recipe));
                builder.Append(" | ");
                builder.Append(chinese ? "上单数量 " : "Served ").Append(row.Served);
                builder.Append(" | ");
                builder.Append((row.Probability * 100d).ToString("0.0")).Append('%');
                builder.Append('\n');
            }

            if (rows.Count > maxRows)
            {
                builder.Append(chinese ? "还有 " : "+ ").Append(rows.Count - maxRows);
                builder.Append(chinese ? " 个追踪菜品未显示" : " more tracked dishes");
            }

            return builder.ToString().TrimEnd();
        }

        private static double GetProbability(SceneInfo scene, RunInfo run, int recipeId)
        {
            if (scene == null || run == null || scene.OrderedRecipes.Count == 0)
            {
                return 0d;
            }

            List<int> activeRecipeIds = GetActiveRecipeIds(scene, run);
            if (activeRecipeIds == null || activeRecipeIds.Count == 0)
            {
                return 0d;
            }

            double recipeCount = activeRecipeIds.Count;
            double totalWeight = 0d;
            double recipeWeight = 0d;
            for (int i = 0; i < activeRecipeIds.Count; i++)
            {
                int id = activeRecipeIds[i];
                double weight = ((double)(run.TotalAdded + 2) / recipeCount) - GetCount(run.AddedCounts, id);
                if (weight < 0d)
                {
                    weight = 0d;
                }

                totalWeight += weight;
                if (id == recipeId)
                {
                    recipeWeight = weight;
                }
            }

            return totalWeight > 0d ? recipeWeight / totalWeight : 0d;
        }

        private static List<int> GetActiveRecipeIds(SceneInfo scene, RunInfo run)
        {
            if (scene.PhaseRecipeIds == null || scene.PhaseRecipeIds.Length == 0)
            {
                return scene.OrderedRecipes.Select(x => x.Id).ToList();
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

        private static int GetTrackedCount(SceneInfo scene)
        {
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

        private static void SetTracked(SceneInfo scene, int recipeId, bool shouldTrack)
        {
            HashSet<int> trackedIds;
            if (!TrackedIdsByScene.TryGetValue(scene.SceneName, out trackedIds))
            {
                trackedIds = new HashSet<int>(scene.OrderedRecipes.Select(x => x.Id));
                TrackedIdsByScene[scene.SceneName] = trackedIds;
            }

            if (shouldTrack)
            {
                trackedIds.Add(recipeId);
            }
            else
            {
                trackedIds.Remove(recipeId);
            }

            SaveSelections();
        }

        private static void TrackAll(SceneInfo scene)
        {
            TrackedIdsByScene[scene.SceneName] = new HashSet<int>(scene.OrderedRecipes.Select(x => x.Id));
            SaveSelections();
        }

        private static void TrackNone(SceneInfo scene)
        {
            TrackedIdsByScene[scene.SceneName] = new HashSet<int>();
            SaveSelections();
        }

        private static void DrawSelectorHint()
        {
            float scale = Mathf.Max(_MODEntry.dpiScaleFactor, 1f);
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = Mathf.RoundToInt((selectorFontSize.Value + 1) * scale);
            style.normal.textColor = _MODEntry.defaultFontColor.Value;
            GUI.Label(new Rect(20f * scale, 90f * scale, 520f * scale, 30f * scale), UseChinese() ? "F6 打开菜单管理窗口" : "F6 opens the Menu Manager window", style);
        }

        private static void DrawSelectorWindow(int windowId)
        {
            float scale = Mathf.Max(_MODEntry.dpiScaleFactor, 1f);
            bool chinese = UseChinese();
            int baseFontSize = Mathf.RoundToInt(selectorFontSize.Value * scale);
            float buttonHeight = Mathf.Max(34f * scale, baseFontSize + (10f * scale));
            float tallButtonHeight = Mathf.Max(48f * scale, buttonHeight + (12f * scale));

            GUIStyle headerStyle = new GUIStyle(GUI.skin.label);
            headerStyle.fontSize = baseFontSize + Mathf.RoundToInt(4f * scale);
            headerStyle.fontStyle = FontStyle.Bold;
            headerStyle.wordWrap = true;

            GUIStyle textStyle = new GUIStyle(GUI.skin.label);
            textStyle.fontSize = baseFontSize;
            textStyle.wordWrap = true;

            GUIStyle smallStyle = new GUIStyle(textStyle);
            smallStyle.fontSize = Mathf.Max(baseFontSize - Mathf.RoundToInt(1f * scale), 10);
            smallStyle.wordWrap = true;

            GUIStyle actionButtonStyle = new GUIStyle(GUI.skin.button);
            actionButtonStyle.fontSize = baseFontSize;
            actionButtonStyle.alignment = TextAnchor.MiddleCenter;
            actionButtonStyle.wordWrap = true;

            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = baseFontSize;
            buttonStyle.alignment = TextAnchor.MiddleLeft;
            buttonStyle.wordWrap = true;

            GUILayout.BeginVertical();
            GUILayout.Space(4f * scale);
            GUILayout.BeginHorizontal();
            if (!selectorCurrentLevelOnly && selectorPage == SelectorPage.DishList)
            {
                if (GUILayout.Button(chinese ? "返回关卡" : "Back", actionButtonStyle, GUILayout.Width(100f * scale), GUILayout.Height(buttonHeight)))
                {
                    selectorPage = SelectorPage.LevelList;
                    sceneScroll = Vector2.zero;
                }
            }
            else
            {
                GUILayout.Space(100f * scale);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(chinese ? "关闭" : "Close", actionButtonStyle, GUILayout.Width(100f * scale), GUILayout.Height(buttonHeight)))
            {
                CloseSelector();
            }
            GUILayout.EndHorizontal();
            DrawQuickToggleSection(chinese, scale, textStyle, smallStyle, actionButtonStyle, tallButtonHeight);

            if (SelectorScenes.Count == 0)
            {
                if (selectorCurrentLevelOnly && selectorUnsupportedCurrentLevel)
                {
                    GUILayout.Label(chinese ? "当前正在关卡中，只允许编辑当前关卡；但该关卡暂不支持（例如 horde）。" : "You are already in a level, so the selector is limited to the current scene; this scene is not supported yet (for example, horde).", headerStyle);
                }
                else
                {
                    GUILayout.Label(chinese ? "还没有可用的关卡列表" : "No selectable scene list yet", headerStyle);
                    GUILayout.Label(chinese ? "可以在主菜单、世界地图、大厅或关卡内再次按 F6 重试。" : "Try F6 again from the main menu, world map, lobby, or inside a level.", textStyle);
                }
                GUILayout.Label(chinese ? "拖动标题栏可移动窗口，窗口位置/大小/字体可在配置里调整。" : "Drag the title bar to move the window. Position, size, and font are adjustable in config.", smallStyle);
                GUILayout.EndVertical();
                GUI.DragWindow(new Rect(0f, 0f, selectorWindowRect.width, 28f * scale));
                return;
            }

            selectedSceneIndex = Mathf.Clamp(selectedSceneIndex, 0, SelectorScenes.Count - 1);
            if (!selectorCurrentLevelOnly && selectorPage == SelectorPage.LevelList)
            {
                DrawLevelSelectionPage(chinese, headerStyle, textStyle, smallStyle, buttonStyle, tallButtonHeight);
            }
            else
            {
                DrawDishSelectionPage(chinese, scale, headerStyle, textStyle, smallStyle, actionButtonStyle, buttonStyle, buttonHeight, tallButtonHeight);
            }

            GUILayout.Space(4f * scale);
            GUILayout.Label(chinese ? "F6 关闭窗口，F7 切换中英显示，F8 切换无菜单。拖动标题栏可移动窗口。" : "F6 closes the window, F7 changes language, F8 toggles No Menu. Drag the title bar to move it.", smallStyle);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, selectorWindowRect.width, 28f * scale));
        }

        private static void DrawQuickToggleSection(bool chinese, float scale, GUIStyle textStyle, GUIStyle smallStyle, GUIStyle buttonStyle, float buttonHeight)
        {
            GUILayout.Space(4f * scale);
            GUILayout.Label(chinese ? "快捷开关" : "Quick Toggles", textStyle);
            GUILayout.Label(chinese ? "这里可以直接切换菜单相关功能。" : "Toggle the menu-related features directly from this window.", smallStyle);

            GUILayout.BeginHorizontal();
            DrawQuickToggleButton(
                buttonStyle,
                buttonHeight,
                enabled.Value,
                true,
                chinese,
                chinese ? "已送追踪" : "Tracker",
                delegate { enabled.Value = !enabled.Value; });
            DrawQuickToggleButton(
                buttonStyle,
                buttonHeight,
                NoMenuMode.IsEnabled,
                NoMenuMode.IsReady,
                chinese,
                chinese ? "无菜单" : "No Menu",
                delegate { NoMenuMode.ToggleEnabled(); });
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawQuickToggleButton(
                buttonStyle,
                buttonHeight,
                MenuManager.IsCarnivalMenuGoodEnabled,
                MenuManager.IsReady,
                chinese,
                chinese ? "麻团好菜单" : "Carnival Menu",
                delegate { MenuManager.ToggleCarnivalMenuGood(); });
            DrawQuickToggleButton(
                buttonStyle,
                buttonHeight,
                MenuManager.IsCarnivalCakeGoodEnabled,
                MenuManager.IsReady,
                chinese,
                chinese ? "麻团好蛋糕" : "Carnival Cake",
                delegate { MenuManager.ToggleCarnivalCakeGood(); });
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawQuickToggleButton(
                buttonStyle,
                buttonHeight,
                MenuManager.IsCarnivalMenuFixedEnabled,
                MenuManager.IsReady,
                chinese,
                chinese ? "麻团TAS菜单" : "Carnival TAS",
                delegate { MenuManager.ToggleCarnivalMenuFixed(); });
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void DrawQuickToggleButton(GUIStyle buttonStyle, float buttonHeight, bool isEnabled, bool isAvailable, bool chinese, string label, Action toggleAction)
        {
            bool originalEnabled = GUI.enabled;
            Color originalBackground = GUI.backgroundColor;
            GUI.enabled = isAvailable;
            GUI.backgroundColor = isAvailable
                ? (isEnabled ? new Color(0.30f, 0.70f, 0.35f, 1f) : new Color(0.28f, 0.28f, 0.28f, 1f))
                : new Color(0.22f, 0.22f, 0.22f, 1f);

            string stateText = isAvailable ? (isEnabled ? (chinese ? "开启" : "ON") : (chinese ? "关闭" : "OFF")) : (chinese ? "不可用" : "N/A");
            if (GUILayout.Button(label + "\n" + stateText, buttonStyle, GUILayout.Height(buttonHeight), GUILayout.ExpandWidth(true)))
            {
                if (isAvailable && toggleAction != null)
                {
                    toggleAction();
                }
            }

            GUI.backgroundColor = originalBackground;
            GUI.enabled = originalEnabled;
        }

        private static void DrawLevelSelectionPage(bool chinese, GUIStyle headerStyle, GUIStyle textStyle, GUIStyle smallStyle, GUIStyle buttonStyle, float tallButtonHeight)
        {
            GUILayout.Label(chinese ? "先选择关卡" : "Choose a level first", headerStyle);
            GUILayout.Label(chinese ? "点击关卡后进入菜品选择。" : "Pick a scene first, then choose the dishes to track.", textStyle);
            GUILayout.Label(GetIdScanText(chinese), smallStyle);
            GUILayout.Label((chinese ? "可选关卡: " : "Scenes: ") + SelectorScenes.Count, smallStyle);

            sceneScroll = GUILayout.BeginScrollView(sceneScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < SelectorScenes.Count; i++)
            {
                SceneInfo scene = SelectorScenes[i];
                int trackedCount = GetTrackedCount(scene);
                string label = scene.DisplayName + "\n"
                    + (chinese
                        ? ("已追踪 " + trackedCount + "/" + scene.OrderedRecipes.Count + "，点击进入菜品选择")
                        : ("Tracking " + trackedCount + "/" + scene.OrderedRecipes.Count + ", click to choose dishes"));
                if (GUILayout.Button(label, buttonStyle, GUILayout.Height(tallButtonHeight)))
                {
                    selectedSceneIndex = i;
                    dishScroll = Vector2.zero;
                    selectorPage = SelectorPage.DishList;
                }
            }
            GUILayout.EndScrollView();
        }

        private static void DrawDishSelectionPage(bool chinese, float scale, GUIStyle headerStyle, GUIStyle textStyle, GUIStyle smallStyle, GUIStyle actionButtonStyle, GUIStyle buttonStyle, float buttonHeight, float tallButtonHeight)
        {
            SceneInfo scene = GetSelectedScene();
            if (scene == null)
            {
                GUILayout.Label(chinese ? "未找到关卡信息。" : "No scene information is available.", headerStyle);
                return;
            }

            GUILayout.Label(scene.DisplayName, headerStyle);
            if (selectorCurrentLevelOnly)
            {
                GUILayout.Label(chinese ? "范围: 当前关卡" : "Scope: Current level only", textStyle);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(chinese ? "全选" : "All", actionButtonStyle, GUILayout.Width(90f * scale), GUILayout.Height(buttonHeight)))
            {
                TrackAll(scene);
            }
            if (GUILayout.Button(chinese ? "清空" : "None", actionButtonStyle, GUILayout.Width(90f * scale), GUILayout.Height(buttonHeight)))
            {
                TrackNone(scene);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label((chinese ? "语言: " : "Language: ") + GetLanguageLabel(chinese), smallStyle);
            GUILayout.Label(chinese ? "排序: 按概率" : "Rank: Probability", smallStyle);
            GUILayout.Label((chinese ? "已追踪: " : "Tracking: ") + GetTrackedCount(scene) + "/" + scene.OrderedRecipes.Count, textStyle);

            dishScroll = GUILayout.BeginScrollView(dishScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < scene.OrderedRecipes.Count; i++)
            {
                RecipeInfo recipe = scene.OrderedRecipes[i];
                bool tracked = IsTracked(scene, recipe.Id);
                string label = BuildDishButtonLabel(recipe, tracked, chinese);
                Color originalBackground = GUI.backgroundColor;
                GUI.backgroundColor = tracked ? new Color(0.30f, 0.70f, 0.35f, 1f) : new Color(0.28f, 0.28f, 0.28f, 1f);
                if (GUILayout.Button(label, buttonStyle, GUILayout.Height(tallButtonHeight)))
                {
                    SetTracked(scene, recipe.Id, !tracked);
                }
                GUI.backgroundColor = originalBackground;
            }
            GUILayout.EndScrollView();
        }

        private static string BuildDishButtonLabel(RecipeInfo recipe, bool tracked, bool chinese)
        {
            string state = tracked
                ? (chinese ? "已追踪" : "Tracked")
                : (chinese ? "未追踪" : "Hidden");
            string hint = chinese ? "点击切换" : "Click to toggle";
            return "[" + recipe.Id + "] " + GetRecipeDisplayName(recipe) + "\n" + state + " | " + hint;
        }

        private static SceneInfo GetSelectedScene()
        {
            if (SelectorScenes.Count == 0)
            {
                return null;
            }

            selectedSceneIndex = Mathf.Clamp(selectedSceneIndex, 0, SelectorScenes.Count - 1);
            return SelectorScenes[selectedSceneIndex];
        }

        private static Rect ClampWindowRect(Rect rect)
        {
            float minWidth = 420f;
            float minHeight = 360f;
            float maxWidth = Mathf.Max(minWidth, Screen.width - 8f);
            float maxHeight = Mathf.Max(minHeight, Screen.height - 8f);
            rect.width = Mathf.Clamp(rect.width, minWidth, maxWidth);
            rect.height = Mathf.Clamp(rect.height, minHeight, maxHeight);
            rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - rect.height));
            return rect;
        }

        private static void SaveSelectorWindowRect()
        {
            int x = Mathf.RoundToInt(selectorWindowRect.x);
            int y = Mathf.RoundToInt(selectorWindowRect.y);
            int width = Mathf.RoundToInt(selectorWindowRect.width);
            int height = Mathf.RoundToInt(selectorWindowRect.height);

            if (selectorWindowX.Value != x)
            {
                selectorWindowX.Value = x;
            }
            if (selectorWindowY.Value != y)
            {
                selectorWindowY.Value = y;
            }
            if (selectorWindowWidth.Value != width)
            {
                selectorWindowWidth.Value = width;
            }
            if (selectorWindowHeight.Value != height)
            {
                selectorWindowHeight.Value = height;
            }
        }

        private static void UpdateIdScanStatus(List<SceneInfo> scenes)
        {
            idScanSceneCount = scenes.Count;
            idConflictCount = 0;
            idConflictSample = string.Empty;

            Dictionary<int, string> idToName = new Dictionary<int, string>();
            for (int i = 0; i < scenes.Count; i++)
            {
                for (int j = 0; j < scenes[i].OrderedRecipes.Count; j++)
                {
                    RecipeInfo recipe = scenes[i].OrderedRecipes[j];
                    string knownName;
                    if (!idToName.TryGetValue(recipe.Id, out knownName))
                    {
                        idToName.Add(recipe.Id, recipe.InternalName);
                    }
                    else if (!string.Equals(knownName, recipe.InternalName, StringComparison.Ordinal))
                    {
                        idConflictCount++;
                        if (string.IsNullOrEmpty(idConflictSample))
                        {
                            idConflictSample = recipe.Id + ": " + knownName + " / " + recipe.InternalName;
                        }
                    }
                }
            }

            string logMessage = idConflictCount == 0
                ? "[ServedDishTracker] ID scan: no conflicting recipe IDs across " + idScanSceneCount + " standard scenes."
                : "[ServedDishTracker] ID scan found " + idConflictCount + " conflicting recipe IDs. Example: " + idConflictSample;
            if (!string.Equals(logMessage, lastIdScanLog, StringComparison.Ordinal))
            {
                _MODEntry.LogInfo(logMessage);
                lastIdScanLog = logMessage;
            }
        }

        private static string GetIdScanText(bool chinese)
        {
            if (idConflictCount == 0)
            {
                return chinese
                    ? "ID 检查: " + idScanSceneCount + " 个标准关卡中未发现跨菜品 ID 冲突"
                    : "ID scan: no cross-dish conflicts in " + idScanSceneCount + " standard scenes";
            }

            return chinese
                ? "ID 检查: 发现 " + idConflictCount + " 个冲突，示例 " + idConflictSample
                : "ID scan: " + idConflictCount + " conflicts, example " + idConflictSample;
        }

        private static string GetRecipeDisplayName(RecipeInfo recipe)
        {
            return UseChinese() ? recipe.ChineseName : recipe.EnglishName;
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
    }
}
