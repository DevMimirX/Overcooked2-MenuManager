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
        public static void Awake()
        {
            selectionFilePath = Path.Combine(Paths.ConfigPath, "OC2MenuManager.selections.txt");
            string legacySelectionFilePath = Path.Combine(Paths.ConfigPath, "HostUtilities-ServedDishTrackerSelections.txt");
            UserDataMigration.CopyFirstExistingWhenDestinationMissing(
                selectionFilePath,
                new string[] { legacySelectionFilePath });
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
                delegate { return _MODEntry.dpiScaleFactor; },
                BuildOverlayRect);
            overlayHost.AddDisplay(new OverlayDisplay());

            ModuleUtility.RegisterHarmony(typeof(ServedDishTracker));
        }

        public static void Shutdown()
        {
            TryResetRoundRuntimeState("shutdown");
            overlayVisible = false;
            settingsWindowVisible = false;
            sceneDropdownExpanded = false;
            capturingHotkey = false;
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

            bool runtimeEnabled = enabled != null && enabled.Value;
            bool needsRoundState = runtimeEnabled
                || settingsWindowVisible
                || PreparedSourcesByInstanceId.Count > 0
                || ReferenceTicketStates.Count > 0
                || TicketWidgetsByInstanceId.Count > 0;
            bool inActiveRound = needsRoundState && IsInActiveRound();
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
            else if (PreparedSourcesByInstanceId.Count > 0
                || PreparedCountsByRecipe.Count > 0
                || PreparedSourceComponentByHandlerId.Count > 0
                || PreparedCookStateBySourceId.Count > 0)
            {
                ClearPreparedState();
                InvalidateOverlay();
            }

            if (!enabled.Value || !inActiveRound || NoMenuMode.IsActiveForRound)
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

        internal static void OnNoMenuRoundStateChanged(bool active)
        {
            if (active)
            {
                ClearReferenceTickets();
                RestoreAllTicketWidgetTints();
                ClearPreparedState();
                overlayVisible = false;
            }

            InvalidatePreparedCandidates(!active);
            InvalidateReferenceTickets();
            InvalidateTicketWidgets();
            InvalidateOverlay();
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
