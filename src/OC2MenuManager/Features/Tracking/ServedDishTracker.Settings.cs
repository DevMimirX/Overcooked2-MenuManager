// Owns the standalone tracker settings window, including responsive bounds and
// the searchable virtualized scene selector. UI state remains session-only;
// persistent configuration and gameplay state stay in their dedicated partials.
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
            CloseSceneDropdown(!settingsWindowVisible);
            if (settingsWindowVisible)
            {
                nextTrackedSceneRefreshPollFrame = 0;
            }
        }

        private static void SynchronizeSettingsInputBlocker()
        {
            if (settingsInputBlockerUnavailable
                || (!settingsWindowVisible && settingsInputBlocker == null))
            {
                return;
            }

            try
            {
                if (settingsInputBlocker == null)
                {
                    settingsInputBlocker = new SettingsInputBlocker();
                }

                settingsInputBlocker.SetActive(settingsWindowVisible);
            }
            catch (Exception ex)
            {
                settingsInputBlockerUnavailable = true;
                _MODEntry.LogWarning(
                    "[ServedDishTracker] Could not create the settings input shield; underlying menus may receive clicks: "
                    + ex.GetType().Name
                    + ": "
                    + ex.Message);
            }
        }

        private static void DisposeSettingsInputBlocker()
        {
            try
            {
                if (settingsInputBlocker != null)
                {
                    settingsInputBlocker.Dispose();
                }
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning(
                    "[ServedDishTracker] Failed to dispose the settings input shield: "
                    + ex.GetType().Name
                    + ": "
                    + ex.Message);
            }
            finally
            {
                settingsInputBlocker = null;
                settingsInputBlockerUnavailable = false;
            }
        }

        private static bool IsSettingsWindowPointerEvent(EventType eventType)
        {
            return eventType == EventType.MouseDown
                || eventType == EventType.MouseUp
                || eventType == EventType.MouseMove
                || eventType == EventType.MouseDrag
                || eventType == EventType.ScrollWheel;
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
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

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
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to write hotkey config: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryParseHotkeyConfig(string path, out KeyCode hotkey)
        {
            hotkey = DefaultSettingsWindowHotkey;
            if (!File.Exists(path))
            {
                return false;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to read hotkey config: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

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
            float dpiScale = Mathf.Max(1f, _MODEntry.dpiScaleFactor);
            float width = SceneSelectionPolicy.CalculateFittedWindowDimension(
                Screen.width,
                SettingsWindowMargin,
                SettingsWindowDefaultWidth * dpiScale,
                SettingsWindowMinWidth);
            float height = SceneSelectionPolicy.CalculateFittedWindowDimension(
                Screen.height,
                SettingsWindowMargin,
                SettingsWindowDefaultHeight * dpiScale,
                SettingsWindowMinHeight);
            float x = SceneSelectionPolicy.CalculateClampedWindowPosition(
                Screen.width,
                width,
                SettingsWindowMargin,
                (Screen.width - width) * 0.5f);
            float y = SceneSelectionPolicy.CalculateClampedWindowPosition(
                Screen.height,
                height,
                SettingsWindowMargin,
                (Screen.height - height) * 0.5f);
            settingsWindowRect = new Rect(x, y, width, height);
        }

        private static void EnsureSettingsWindowRect()
        {
            settingsWindowRect.width = SceneSelectionPolicy.CalculateFittedWindowDimension(
                Screen.width,
                SettingsWindowMargin,
                settingsWindowRect.width,
                SettingsWindowMinWidth);
            settingsWindowRect.height = SceneSelectionPolicy.CalculateFittedWindowDimension(
                Screen.height,
                SettingsWindowMargin,
                settingsWindowRect.height,
                SettingsWindowMinHeight);
            settingsWindowRect.x = SceneSelectionPolicy.CalculateClampedWindowPosition(
                Screen.width,
                settingsWindowRect.width,
                SettingsWindowMargin,
                settingsWindowRect.x);
            settingsWindowRect.y = SceneSelectionPolicy.CalculateClampedWindowPosition(
                Screen.height,
                settingsWindowRect.height,
                SettingsWindowMargin,
                settingsWindowRect.y);
        }

        private static void CaptureSceneDropdownWheelInput()
        {
            Event currentEvent = Event.current;
            if (!sceneDropdownExpanded
                || !sceneDropdownScreenRectValid
                || currentEvent == null
                || currentEvent.type != EventType.ScrollWheel)
            {
                return;
            }

            Vector2 screenMousePosition = GUIUtility.GUIToScreenPoint(currentEvent.mousePosition);
            if (!sceneDropdownScreenRect.Contains(screenMousePosition))
            {
                return;
            }

            pendingSceneDropdownWheelDelta += currentEvent.delta.y;
            sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                sceneDropdownScrollRequest,
                SceneDropdownNavigationEvent.UserScrolled);
            currentEvent.Use();
        }

        private static void DrawSettingsWindow(int windowId)
        {
            CaptureSceneDropdownWheelInput();
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
            if (GUILayout.Button(Ui("关闭", "Close"), GUILayout.Width(72f)))
            {
                settingsWindowVisible = false;
                CloseSceneDropdown(true);
                capturingHotkey = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            settingsWindowScrollPosition = GUILayout.BeginScrollView(settingsWindowScrollPosition);
            DrawSceneAndDishSelectionSection();
            DrawTrackingSettingsSection();
            DrawTierSettingsSection();
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
            DrawSectionHeader(Ui("界面", "Interface"));
            DrawIntSliderRow(Ui("MOD的UI字体大小", "UI Font Size"), _MODEntry.defaultFontSize, 5, 40, Ui("独立菜单窗口与悬浮窗共用的基础字体大小。", "Base font size shared by the standalone window and the overlay."));
            DrawColorRow(Ui("MOD的UI字体颜色", "UI Font Color"), _MODEntry.defaultFontColor, Ui("独立菜单窗口与悬浮窗共用的基础字体颜色。", "Base font color shared by the standalone window and the overlay."));
            DrawHotkeyRow();
        }

        private static void DrawFeatureToggleSection()
        {
            DrawSectionHeader(Ui("功能开关", "Features"));
            DrawToggleRow(Ui("麻团好菜单", "Carnival Better Menu"), MenuManager.IsCarnivalMenuGoodEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalMenuGood != null)
                {
                    MenuManager.isCarnivalMenuGood.Value = value;
                }
            }, Ui("第一道菜没有葱，前两道菜不是蛋糕。", "Removes onion from the first order and keeps cake out of the first two orders."));
            DrawToggleRow(Ui("麻团好蛋糕", "Carnival Better Cakes"), MenuManager.IsCarnivalCakeGoodEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalCakeGood != null)
                {
                    MenuManager.isCarnivalCakeGood.Value = value;
                }
            }, Ui("提高蛋糕相关菜单出现概率。", "Raises the chance of cake orders on the carnival stage."));
            DrawToggleRow(Ui("麻团TAS菜单", "Carnival TAS Menu"), MenuManager.IsCarnivalMenuFixedEnabled, delegate(bool value)
            {
                if (MenuManager.isCarnivalMenuFixed != null)
                {
                    MenuManager.isCarnivalMenuFixed.Value = value;
                }
            }, Ui("固定麻团菜单为 TAS 用配置。", "Locks the carnival stage to the TAS menu sequence."));
            DrawToggleRow(Ui("无菜单", "No Menu Mode"), NoMenuMode.IsEnabled, delegate(bool value)
            {
                NoMenuMode.SetEnabled(value);
            }, Ui("无菜单设置在下一局开始时生效。", "No Menu changes apply when the next round starts."));
            GUILayout.Label(NoMenuMode.GetStatusText(UseChinese()));
        }

        private static void DrawTrackingSettingsSection()
        {
            DrawSectionHeader(Ui("历史菜单追踪", "Menu History Tracker"));
            DrawToggleRow(Ui("启用菜单追踪", "Enable Menu Tracking"), enabled != null && enabled.Value, delegate(bool value)
            {
                if (enabled != null)
                {
                    enabled.Value = value;
                    InvalidateReferenceTickets();
                    SynchronizeRealTicketWidgetTints(IsMenuTicketTintEnabled(), value);
                    InvalidateOverlay();
                }
            }, Ui("追踪功能总开关：控制历史、概率、已备、悬浮窗、菜单颜色和猜单；不影响麻团、无菜单或安全修复。", "Master tracking switch for history, probabilities, prepared state, the overlay, ticket colors, and guesses. Carnival, No Menu, and safety fixes remain independent."));
            DrawToggleRow(Ui("显示悬浮窗", "Show Floating Overlay"), overlayEnabled != null && overlayEnabled.Value, delegate(bool value)
            {
                if (overlayEnabled != null)
                {
                    overlayEnabled.Value = value;
                    overlayVisible = false;
                    InvalidateOverlay();
                    if (value)
                    {
                        nextOverlayRefreshFrame = 0;
                    }
                }
            }, Ui("只控制左侧悬浮窗；不会关闭历史记录、已备跟踪、菜单颜色或猜单。", "Controls only the left floating overlay; history, prepared tracking, ticket colors, and guess orders continue."));
            DrawIntSliderRow(Ui("最大猜单数量", "Max Guess Count"), menuReferenceTicketCount, 0, MaxReferenceTicketDisplayCount, Ui("猜单数量上限。真实订单优先；每行最多 10 张，超出的猜单显示在下一行。0 关闭。", "Maximum extra guess orders. Real orders stay first; each row holds up to 10 tickets and excess guesses wrap below. Set to 0 to disable."));
            DrawIntSliderRow(
                Ui("下排票据大小 (%)", "Lower Row Ticket Size (%)"),
                lowerTicketRowScalePercent,
                TicketRowLayoutPolicy.MinimumLowerRowScalePercent,
                TicketRowLayoutPolicy.MaximumLowerRowScalePercent,
                Ui(
                    "控制第一排之后所有票据的大小。默认 70%；100% 保留原自动适配大小。下排会自动上移，让上一排遮住无用的顶部留白。",
                    "Controls every ticket row after the first. The default is 70%; 100% preserves the previous auto-fitted size. Lower rows move up automatically so the preceding row covers the unused header space."));
            DrawToggleRow(Ui("启用已备跟踪", "Enable Prepared Tracking"), preparedTrackingEnabled != null && preparedTrackingEnabled.Value, delegate(bool value)
            {
                if (preparedTrackingEnabled != null)
                {
                    preparedTrackingEnabled.Value = value;
                    InvalidateReferenceTickets();
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
            }, Ui("跟踪已完成但尚未上菜的成品。", "Tracks completed dishes that have not been served yet."));
            DrawToggleRow(Ui("菜单颜色", "Ticket Colors"), menuTicketTintEnabled != null && menuTicketTintEnabled.Value, delegate(bool value)
            {
                if (menuTicketTintEnabled != null)
                {
                    menuTicketTintEnabled.Value = value;
                    SynchronizeRealTicketWidgetTints(IsMenuTicketTintEnabled(), value);
                }
            }, Ui("只给真实菜单上色；猜单始终保留猜单颜色。", "Colors only real order tickets; guess tickets always keep their guess styling."));
            DrawColorRow(Ui("在单颜色", "On-Menu Color"), menuTicketOnMenuTintColor, Ui("菜单栏里“在单未备”的颜色。A 通道控制整张单的透明度。", "Color for orders that are on the menu but not prepared yet. The A channel controls full-order opacity."), delegate
            {
                InvalidateTicketWidgets();
            });
            DrawColorRow(Ui("已备颜色", "Prepared Color"), menuTicketPreparedTintColor, Ui("菜单栏里“已备”的颜色。A 通道控制整张单的透明度。", "Color for orders that are already prepared. The A channel controls full-order opacity."), delegate
            {
                InvalidateTicketWidgets();
            });
            DrawColorRow(Ui("猜单颜色", "Guess Color"), menuReferenceTicketTintColor, Ui("菜单栏里猜单的颜色。A 通道控制整张单的透明度，显示时会额外压暗一点。", "Color for guess orders. The A channel controls full-order opacity, and guess orders are rendered slightly dimmer on top of that."), delegate
            {
                InvalidateTicketWidgets();
            });
            DrawEnumCycleRow(Ui("显示语言", "Display Language"), GetTrackerLanguageLabel(languageMode != null ? languageMode.Value : TrackerLanguage.Auto), delegate()
            {
                if (languageMode != null)
                {
                    languageMode.Value = NextLanguage(languageMode.Value);
                    InvalidateReferenceTickets();
                    InvalidateOverlay();
                }
            }, Ui("控制菜名和设置界面的语言。Auto 会跟随游戏语言。", "Controls both dish names and this settings window. Auto follows the game language."));
            DrawIntSliderRow(Ui("关卡名称最大长度", "Scene Label Max Length"), sceneSelectorMaxTextLength, MinSceneSelectorDisplayLength, MaxSceneSelectorDisplayLengthSetting, Ui("控制关卡选择按钮与下拉列表的最大字符数。", "Maximum characters for the scene selector and dropdown."));
            DrawIntSliderRow(Ui("菜品名称最大长度", "Dish Label Max Length"), dishSelectorMaxTextLength, MinDishSelectorDisplayLength, MaxDishSelectorDisplayLengthSetting, Ui("控制菜单窗口里追踪菜品列表的最大字符数。", "Maximum characters for dish names in the tracked-dish list."));
            DrawInfoText(Ui("橙名=在单未备，绿名=已备。", "Orange = on menu, green = prepared."));
        }

        private static void DrawTierSettingsSection()
        {
            DrawSectionHeader(Ui("层级设置", "Tier Settings"));
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("数字越小越靠前。点击层级按钮循环 1-6。", "Lower tiers sort earlier. Click the tier button to cycle 1-6."), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Ui("全部重置", "Reset All"), GUILayout.Width(96f)))
            {
                ResetAllCategoryTierOverrides();
            }
            GUILayout.EndHorizontal();

            string[] categoryKeys = RecipeCategoryCatalog.GetOrderedCategoryKeys();
            float cellWidth = GetTierSettingsCellWidth();
            for (int i = 0; i < categoryKeys.Length;)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < TierSettingsColumnCount; column++)
                {
                    GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(cellWidth), GUILayout.ExpandWidth(false));
                    if (i < categoryKeys.Length)
                    {
                        DrawCategoryTierCell(categoryKeys[i]);
                        i++;
                    }
                    else
                    {
                        GUILayout.Space(44f);
                    }
                    GUILayout.EndVertical();
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
        }

        private static float GetTierSettingsCellWidth()
        {
            float availableWidth = Mathf.Max(640f, settingsWindowRect.width - 72f);
            float spacingAllowance = 8f * (TierSettingsColumnCount - 1);
            return Mathf.Max(108f, (availableWidth - spacingAllowance) / TierSettingsColumnCount);
        }

        private static void DrawCategoryTierCell(string categoryKey)
        {
            ConfigEntry<int> entry;
            if (string.IsNullOrEmpty(categoryKey) || !CategoryTierEntriesByKey.TryGetValue(categoryKey, out entry) || entry == null)
            {
                return;
            }

            int defaultTier = RecipeCategoryCatalog.GetDefaultCategoryTierByKey(categoryKey);
            string displayName = RecipeCategoryCatalog.GetDisplayCategoryNameByKey(categoryKey, UseChinese());
            GUILayout.Label(displayName, GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Ui("层级 ", "Tier ") + entry.Value, GUILayout.ExpandWidth(true)))
            {
                int nextTier = entry.Value >= MaxCategoryTierValue ? MinCategoryTierValue : entry.Value + 1;
                ApplyCategoryTierOverride(categoryKey, nextTier);
            }
            if (GUILayout.Button(Ui("默认", "Reset"), GUILayout.Width(56f)))
            {
                ApplyCategoryTierOverride(categoryKey, defaultTier);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Ui("默认 ", "Default ") + defaultTier, GUILayout.ExpandWidth(true));
        }

        private static void DrawOverlaySettingsSection()
        {
            DrawSectionHeader(Ui("悬浮窗", "Overlay"));
            DrawIntSliderRow(Ui("悬浮窗X", "Overlay X"), overlayX, 0, 4000, Ui("悬浮窗左上角 X 坐标。", "Overlay top-left X position."));
            DrawIntSliderRow(Ui("悬浮窗Y", "Overlay Y"), overlayY, 0, 4000, Ui("悬浮窗左上角 Y 坐标。", "Overlay top-left Y position."));
            DrawIntSliderRow(Ui("悬浮窗宽度", "Overlay Width"), overlayWidth, 240, 1600, Ui("历史菜单追踪悬浮窗宽度。", "Menu history overlay width."));
            DrawIntSliderRow(Ui("悬浮窗高度", "Overlay Height"), overlayHeight, 120, 1600, Ui("历史菜单追踪悬浮窗高度。", "Menu history overlay height."));
            DrawIntSliderRow(Ui("悬浮窗字体大小", "Overlay Font Size"), overlayFontSize, 8, 48, Ui("历史菜单追踪悬浮窗字体大小。", "Menu history overlay font size."));
            DrawIntSliderRow(Ui("悬浮窗关卡名长度", "Overlay Scene Name Length"), overlaySceneMaxTextLength, MinOverlaySceneDisplayLength, MaxOverlaySceneDisplayLengthSetting, Ui("控制悬浮窗里关卡标题的最大字符数。", "Maximum characters for the scene title in the overlay."));
            DrawIntSliderRow(Ui("悬浮窗菜名长度", "Overlay Dish Name Length"), overlayDishMaxTextLength, MinOverlayDishDisplayLength, MaxOverlayDishDisplayLengthSetting, Ui("控制悬浮窗里菜品名称的最大字符数。", "Maximum characters for dish names in the overlay."));
            DrawToggleRow(Ui("悬浮窗粗体", "Bold Overlay Font"), overlayBoldFont != null && overlayBoldFont.Value, delegate(bool value)
            {
                if (overlayBoldFont != null)
                {
                    overlayBoldFont.Value = value;
                }
            }, Ui("是否使用粗体显示悬浮窗文字。", "Uses bold text in the overlay."));
            DrawIntSliderRow(Ui("悬浮窗最大显示菜品数", "Overlay Dish Limit"), overlayMaxDisplayDishes, 1, 40, Ui("悬浮窗最多显示多少道菜。", "Maximum number of dishes shown in the overlay."));
            DrawEnumCycleRow(Ui("悬浮窗文本对齐", "Overlay Text Align"), GetOverlayAlignmentLabel(overlayTextAlignment != null ? overlayTextAlignment.Value : OverlayTextAlignment.Left), delegate()
            {
                if (overlayTextAlignment != null)
                {
                    overlayTextAlignment.Value = NextAlignment(overlayTextAlignment.Value);
                    InvalidateOverlay();
                }
            }, Ui("点击切换 左 / 中 / 右。", "Click to cycle Left / Center / Right."));
            DrawColorRow(Ui("悬浮窗字体颜色", "Overlay Font Color"), overlayFontColor, Ui("悬浮窗普通文字颜色。", "Normal overlay text color."));
            DrawColorRow(Ui("悬浮窗上单数量颜色", "Served Count Color"), overlayServedValueColor, Ui("悬浮窗中“上单数量”数值颜色。", "Color for served-count values in the overlay."));
            DrawColorRow(Ui("悬浮窗概率颜色", "Probability Color"), overlayProbabilityValueColor, Ui("悬浮窗中“概率”数值颜色。", "Color for probability values in the overlay."));
            DrawColorRow(Ui("悬浮窗已备颜色", "Prepared Count Color"), overlayPreparedValueColor, Ui("悬浮窗中“已备”数值颜色。", "Color for prepared-count values in the overlay."));
        }

        private static void DrawSceneAndDishSelectionSection()
        {
            DrawSectionHeader(Ui("菜单追踪", "Tracked Dishes"));
            SceneInfo scene = ResolveSceneSelectorSelection();
            List<SceneInfo> selectableScenes = GetSelectableScenes();
            bool locked = IsLockedToCurrentScene();

            DrawSceneSelectorRow(scene, selectableScenes, locked);
            DrawTrackingPanel(null);
        }

        private static void DrawSceneSelectorRow(SceneInfo selectedSceneInfo, List<SceneInfo> selectableScenes, bool locked)
        {
            string selectedSceneName = selectedSceneInfo != null ? selectedSceneInfo.SceneName : string.Empty;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui(SceneSelectorKey, "Scene"), GUILayout.Width(SettingsLabelWidth));
            if (locked)
            {
                GUILayout.Label(GetSceneSelectorValue(selectedSceneInfo), GUI.skin.textField, GUILayout.ExpandWidth(true));
            }
            else
            {
                string buttonText = GetSceneSelectorValue(selectedSceneInfo);
                if (GUILayout.Button(buttonText, GUILayout.ExpandWidth(true)))
                {
                    if (sceneDropdownExpanded)
                    {
                        CloseSceneDropdown(false);
                    }
                    else
                    {
                        sceneDropdownExpanded = true;
                        sceneSearchFocusRequested = true;
                        cachedSceneSearchRevision = -1;
                        sceneDropdownKeyboardSceneName = selectedSceneName;
                        sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                            sceneDropdownScrollRequest,
                            SceneDropdownNavigationEvent.DropdownOpened);
                    }
                }
            }
            GUILayout.EndHorizontal();

            if (locked)
            {
                if (sceneDropdownExpanded)
                {
                    CloseSceneDropdown(false);
                }
                DrawInfoText(Ui("当前在关卡内，关卡选择已自动锁定为本局关卡。", "The scene selector is locked to the current round."));
            }
            else if (sceneDropdownExpanded)
            {
                float listHeight = SceneSelectionPolicy.CalculateDropdownHeight(
                    settingsWindowRect.height,
                    SceneDropdownHeightRatio,
                    SceneDropdownMinHeight,
                    SceneDropdownMaxHeight);
                List<SceneInfo> filteredScenes = GetFilteredSelectableScenes(selectableScenes, selectedSceneName);
                SceneInfo sceneToSelect = HandleSceneDropdownKeyboard(filteredScenes, listHeight);

                DrawSceneSearchControls();
                filteredScenes = GetFilteredSelectableScenes(selectableScenes, selectedSceneName);
                GUILayout.Label(
                    Ui("显示 ", "Showing ") + filteredScenes.Count + " / " + (selectableScenes != null ? selectableScenes.Count : 0)
                    + Ui("；DIY ", "; DIY ") + filteredDIYSceneCount + " / " + selectableDIYSceneCount);
                DrawDIYCatalogStatus();

                SceneInfo mouseSelection = DrawVirtualizedSceneList(filteredScenes, listHeight, selectedSceneName);
                if (sceneToSelect == null)
                {
                    sceneToSelect = mouseSelection;
                }

                if (sceneToSelect != null)
                {
                    SelectSceneFromDropdown(sceneToSelect);
                }
            }

            GUILayout.EndVertical();
        }

        private static void DrawSceneSearchControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Ui("搜索", "Search"), GUILayout.Width(72f));
            GUI.SetNextControlName(SceneSearchControlName);
            string nextSearchText = GUILayout.TextField(sceneSearchText ?? string.Empty, GUILayout.ExpandWidth(true));
            if (!string.Equals(nextSearchText, sceneSearchText, StringComparison.Ordinal))
            {
                sceneSearchText = nextSearchText;
                cachedSceneSearchRevision = -1;
                sceneDropdownKeyboardSceneName = string.Empty;
                sceneDropdownRetargetFirstResult = true;
                sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                    sceneDropdownScrollRequest,
                    SceneDropdownNavigationEvent.SearchChanged);
            }

            if (GUILayout.Button(Ui("清除", "Clear"), GUILayout.Width(58f)))
            {
                sceneSearchText = string.Empty;
                cachedSceneSearchRevision = -1;
                sceneDropdownKeyboardSceneName = string.Empty;
                sceneDropdownRetargetFirstResult = true;
                sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                    sceneDropdownScrollRequest,
                    SceneDropdownNavigationEvent.SearchChanged);
                sceneSearchFocusRequested = true;
            }

            if (GUILayout.Button(Ui("刷新", "Refresh"), GUILayout.Width(66f)))
            {
                nextDIYSceneRefreshFrame = 0;
                RefreshKnownScenes(true);
                ResolveSceneSelectorSelection();
                cachedSceneSearchRevision = -1;
                sceneSearchFocusRequested = true;
            }
            GUILayout.EndHorizontal();

            if (sceneSearchFocusRequested)
            {
                GUI.FocusControl(SceneSearchControlName);
                sceneSearchFocusRequested = false;
            }
        }

        private static List<SceneInfo> GetFilteredSelectableScenes(List<SceneInfo> selectableScenes, string selectedSceneName)
        {
            string normalizedQuery = SceneSelectionPolicy.NormalizeQuery(sceneSearchText);
            if (!sceneDropdownRetargetFirstResult
                && cachedSceneSearchRevision == selectableScenesRevision
                && string.Equals(cachedSceneSearchQuery, normalizedQuery, StringComparison.Ordinal))
            {
                return FilteredSelectableScenesBuffer;
            }

            bool retargetFirstResult = sceneDropdownRetargetFirstResult
                || !string.Equals(cachedSceneSearchQuery, normalizedQuery, StringComparison.Ordinal);
            string previousKeyboardSceneName = sceneDropdownKeyboardSceneName;
            FilteredSelectableScenesBuffer.Clear();
            filteredDIYSceneCount = 0;
            selectableDIYSceneCount = 0;
            if (selectableScenes != null)
            {
                for (int i = 0; i < selectableScenes.Count; i++)
                {
                    SceneInfo scene = selectableScenes[i];
                    if (scene == null)
                    {
                        continue;
                    }

                    if (scene.IsDIY)
                    {
                        selectableDIYSceneCount++;
                    }

                    if (!SceneSelectionPolicy.Matches(
                        normalizedQuery,
                        scene.SceneName,
                        scene.DisplayName,
                        scene.EnglishDisplayName,
                        scene.ChineseDisplayName))
                    {
                        continue;
                    }

                    FilteredSelectableScenesBuffer.Add(scene);
                    if (scene.IsDIY)
                    {
                        filteredDIYSceneCount++;
                    }
                }
            }

            cachedSceneSearchQuery = normalizedQuery;
            cachedSceneSearchRevision = selectableScenesRevision;
            sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                sceneDropdownScrollRequest,
                SceneDropdownNavigationEvent.CatalogRefreshed);
            bool previousKeyboardSceneAvailable = FindSceneIndex(FilteredSelectableScenesBuffer, previousKeyboardSceneName) >= 0;
            bool selectedSceneAvailable = FindSceneIndex(FilteredSelectableScenesBuffer, selectedSceneName) >= 0;
            string firstResultSceneName = FilteredSelectableScenesBuffer.Count > 0
                ? FilteredSelectableScenesBuffer[0].SceneName
                : string.Empty;
            sceneDropdownKeyboardSceneName = SceneSelectionPolicy.ResolveKeyboardTargetSceneName(
                retargetFirstResult,
                previousKeyboardSceneName,
                previousKeyboardSceneAvailable,
                selectedSceneName,
                selectedSceneAvailable,
                firstResultSceneName);
            sceneDropdownRetargetFirstResult = false;
            return FilteredSelectableScenesBuffer;
        }

        private static SceneInfo HandleSceneDropdownKeyboard(List<SceneInfo> filteredScenes, float listHeight)
        {
            Event currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.KeyDown || filteredScenes == null || filteredScenes.Count == 0)
            {
                return null;
            }

            int currentIndex = FindSceneIndex(filteredScenes, sceneDropdownKeyboardSceneName);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            int nextIndex = currentIndex;
            int pageSize = Math.Max(1, (int)(Math.Max(1f, listHeight - 4f) / SceneDropdownRowHeight) - 1);
            if (currentEvent.keyCode == KeyCode.UpArrow)
            {
                nextIndex--;
            }
            else if (currentEvent.keyCode == KeyCode.DownArrow)
            {
                nextIndex++;
            }
            else if (currentEvent.keyCode == KeyCode.PageUp)
            {
                nextIndex -= pageSize;
            }
            else if (currentEvent.keyCode == KeyCode.PageDown)
            {
                nextIndex += pageSize;
            }
            else if (currentEvent.keyCode == KeyCode.Home)
            {
                nextIndex = 0;
            }
            else if (currentEvent.keyCode == KeyCode.End)
            {
                nextIndex = filteredScenes.Count - 1;
            }
            else if (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter)
            {
                if (currentIndex >= 0 && currentIndex < filteredScenes.Count)
                {
                    currentEvent.Use();
                    return filteredScenes[currentIndex];
                }
                return null;
            }
            else
            {
                return null;
            }

            int resolvedIndex = Math.Max(0, Math.Min(filteredScenes.Count - 1, nextIndex));
            sceneDropdownKeyboardSceneName = filteredScenes[resolvedIndex].SceneName;
            sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                sceneDropdownScrollRequest,
                SceneDropdownNavigationEvent.KeyboardMoved);
            currentEvent.Use();
            return null;
        }

        private static SceneInfo DrawVirtualizedSceneList(List<SceneInfo> filteredScenes, float listHeight, string selectedSceneName)
        {
            if (filteredScenes == null || filteredScenes.Count == 0)
            {
                sceneDropdownKeyboardSceneName = string.Empty;
                sceneDropdownScrollPosition = Vector2.zero;
                sceneDropdownScrollRequest = SceneDropdownScrollRequest.None;
                pendingSceneDropdownWheelDelta = 0f;
                sceneDropdownScreenRectValid = false;
                GUILayout.Label(Ui("没有匹配的关卡。可清除搜索或刷新 OC2DIYLevel 元数据。", "No matching scenes. Clear the search or refresh OC2DIYLevel metadata."), GUI.skin.box, GUILayout.Height(Mathf.Min(listHeight, 72f)));
                return null;
            }

            Rect listRect = GUILayoutUtility.GetRect(1f, listHeight, GUILayout.ExpandWidth(true), GUILayout.Height(listHeight));
            GUI.Box(listRect, GUIContent.none);
            Rect viewportRect = new Rect(
                listRect.x + 2f,
                listRect.y + 2f,
                Mathf.Max(1f, listRect.width - 4f),
                Mathf.Max(1f, listRect.height - 4f));
            float contentHeight = Mathf.Max(viewportRect.height, filteredScenes.Count * SceneDropdownRowHeight);
            float scrollbarWidth = contentHeight > viewportRect.height
                ? Mathf.Max(16f, GUI.skin.verticalScrollbar.fixedWidth)
                : 0f;
            Rect contentRect = new Rect(
                0f,
                0f,
                Mathf.Max(1f, viewportRect.width - scrollbarWidth),
                contentHeight);

            Event currentEvent = Event.current;
            if (currentEvent != null && currentEvent.type == EventType.Repaint)
            {
                Vector2 screenPosition = GUIUtility.GUIToScreenPoint(new Vector2(viewportRect.x, viewportRect.y));
                sceneDropdownScreenRect = new Rect(screenPosition.x, screenPosition.y, viewportRect.width, viewportRect.height);
                sceneDropdownScreenRectValid = true;
            }

            sceneDropdownScrollPosition.x = 0f;
            sceneDropdownScrollPosition.y = SceneSelectionPolicy.CalculateClampedScrollOffset(
                filteredScenes.Count,
                sceneDropdownScrollPosition.y,
                viewportRect.height,
                SceneDropdownRowHeight);
            if (sceneDropdownScrollRequest == SceneDropdownScrollRequest.ResetToTop)
            {
                sceneDropdownScrollPosition.y = 0f;
            }
            else if (sceneDropdownScrollRequest == SceneDropdownScrollRequest.RevealKeyboardTarget)
            {
                int keyboardIndex = FindSceneIndex(filteredScenes, sceneDropdownKeyboardSceneName);
                if (keyboardIndex >= 0)
                {
                    sceneDropdownScrollPosition.y = SceneSelectionPolicy.CalculateScrollOffsetForItem(
                        keyboardIndex,
                        filteredScenes.Count,
                        sceneDropdownScrollPosition.y,
                        viewportRect.height,
                        SceneDropdownRowHeight);
                }
            }
            sceneDropdownScrollRequest = SceneDropdownScrollRequest.None;

            if (pendingSceneDropdownWheelDelta != 0f)
            {
                sceneDropdownScrollPosition.y = SceneSelectionPolicy.CalculateWheelScrollOffset(
                    filteredScenes.Count,
                    sceneDropdownScrollPosition.y,
                    pendingSceneDropdownWheelDelta,
                    viewportRect.height,
                    SceneDropdownRowHeight);
                pendingSceneDropdownWheelDelta = 0f;
            }

            if (currentEvent != null
                && currentEvent.type == EventType.ScrollWheel
                && viewportRect.Contains(currentEvent.mousePosition))
            {
                sceneDropdownScrollPosition.y = SceneSelectionPolicy.CalculateWheelScrollOffset(
                    filteredScenes.Count,
                    sceneDropdownScrollPosition.y,
                    currentEvent.delta.y,
                    viewportRect.height,
                    SceneDropdownRowHeight);
                sceneDropdownScrollRequest = SceneSelectionPolicy.UpdateScrollRequest(
                    sceneDropdownScrollRequest,
                    SceneDropdownNavigationEvent.UserScrolled);
                currentEvent.Use();
            }

            sceneDropdownScrollPosition = GUI.BeginScrollView(viewportRect, sceneDropdownScrollPosition, contentRect);
            sceneDropdownScrollPosition.x = 0f;
            sceneDropdownScrollPosition.y = SceneSelectionPolicy.CalculateClampedScrollOffset(
                filteredScenes.Count,
                sceneDropdownScrollPosition.y,
                viewportRect.height,
                SceneDropdownRowHeight);
            int firstIndex;
            int endIndexExclusive;
            SceneSelectionPolicy.CalculateVisibleRange(
                filteredScenes.Count,
                sceneDropdownScrollPosition.y,
                viewportRect.height,
                SceneDropdownRowHeight,
                SceneDropdownOverscanRows,
                out firstIndex,
                out endIndexExclusive);

            SceneInfo selectedScene = null;
            for (int i = firstIndex; i < endIndexExclusive; i++)
            {
                SceneInfo selectableScene = filteredScenes[i];
                bool isCurrent = string.Equals(selectedSceneName, selectableScene.SceneName, StringComparison.OrdinalIgnoreCase);
                bool isKeyboardTarget = string.Equals(sceneDropdownKeyboardSceneName, selectableScene.SceneName, StringComparison.OrdinalIgnoreCase);
                string label = (isKeyboardTarget ? "> " : string.Empty)
                    + (isCurrent ? "✓ " : string.Empty)
                    + GetSceneSelectorValue(selectableScene);
                Rect rowRect = new Rect(
                    1f,
                    i * SceneDropdownRowHeight + 1f,
                    Mathf.Max(1f, contentRect.width - 2f),
                    SceneDropdownRowHeight - 2f);
                if (GUI.Button(rowRect, label))
                {
                    sceneDropdownKeyboardSceneName = selectableScene.SceneName;
                    selectedScene = selectableScene;
                    break;
                }
            }
            GUI.EndScrollView();
            return selectedScene;
        }

        private static void DrawDIYCatalogStatus()
        {
            string statusText;
            if (diyCatalogUsingRetainedSnapshot)
            {
                statusText = Ui(
                    "OC2DIYLevel 刷新失败；继续使用上次有效的 " + diyCatalogAcceptedSceneCount + " 个关卡。",
                    "OC2DIYLevel refresh failed; using the last valid " + diyCatalogAcceptedSceneCount + " scenes.");
            }
            else if (diyCatalogReadState == DIYLevelCatalogReadState.Complete)
            {
                statusText = Ui(
                    "OC2DIYLevel 元数据：" + diyCatalogAcceptedSceneCount + " 个关卡。",
                    "OC2DIYLevel metadata: " + diyCatalogAcceptedSceneCount + " scenes.");
            }
            else if (diyCatalogReadState == DIYLevelCatalogReadState.Partial)
            {
                statusText = Ui(
                    "OC2DIYLevel 元数据：" + diyCatalogAcceptedSceneCount + " 个关卡；跳过 " + diyCatalogRejectedEntryCount + " 个无效或重复条目。",
                    "OC2DIYLevel metadata: " + diyCatalogAcceptedSceneCount + " scenes; skipped " + diyCatalogRejectedEntryCount + " invalid or duplicate entries.");
            }
            else if (diyCatalogReadState == DIYLevelCatalogReadState.Loading)
            {
                statusText = Ui("OC2DIYLevel 元数据仍在加载。", "OC2DIYLevel metadata is still loading.");
            }
            else if (diyCatalogReadState == DIYLevelCatalogReadState.Unavailable)
            {
                statusText = Ui("未检测到 OC2DIYLevel 元数据；原版关卡仍可使用。", "OC2DIYLevel metadata was not detected; base-game scenes remain available.");
            }
            else
            {
                statusText = Ui("OC2DIYLevel 元数据暂时不可用；请点击刷新。", "OC2DIYLevel metadata is temporarily unavailable; press Refresh.");
            }

            GUILayout.Label(statusText);
        }

        private static int FindSceneIndex(List<SceneInfo> scenes, string sceneName)
        {
            if (scenes == null || string.IsNullOrEmpty(sceneName))
            {
                return -1;
            }

            for (int i = 0; i < scenes.Count; i++)
            {
                SceneInfo scene = scenes[i];
                if (scene != null && string.Equals(scene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static void SelectSceneFromDropdown(SceneInfo scene)
        {
            if (scene == null || string.IsNullOrEmpty(scene.SceneName))
            {
                return;
            }

            if (!string.Equals(configuredSceneName, scene.SceneName, StringComparison.OrdinalIgnoreCase))
            {
                configuredSceneName = scene.SceneName;
                ResolveSceneSelectorSelection();
            }

            CloseSceneDropdown(false);
        }

        private static void CloseSceneDropdown(bool clearSearch)
        {
            sceneDropdownExpanded = false;
            sceneSearchFocusRequested = false;
            sceneDropdownKeyboardSceneName = string.Empty;
            sceneDropdownRetargetFirstResult = false;
            sceneDropdownScrollRequest = SceneDropdownScrollRequest.None;
            pendingSceneDropdownWheelDelta = 0f;
            sceneDropdownScreenRectValid = false;
            if (clearSearch)
            {
                sceneSearchText = string.Empty;
                cachedSceneSearchQuery = string.Empty;
                cachedSceneSearchRevision = -1;
                sceneDropdownScrollPosition = Vector2.zero;
            }
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
            bool nextValue = GUILayout.Toggle(value, value ? Ui("启用", "On") : Ui("关闭", "Off"), GUILayout.Width(88f));
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
                if (object.ReferenceEquals(entry, menuReferenceTicketCount))
                {
                    InvalidateReferenceTickets();
                }
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
            GUILayout.Label(Ui("打开菜单管理窗口", "Open Menu Manager"), GUILayout.Width(SettingsLabelWidth));
            string hotkeyText = capturingHotkey
                ? Ui("按任意键...", "Press any key...")
                : settingsWindowHotkey.ToString();
            if (GUILayout.Button(hotkeyText, GUILayout.Width(140f)))
            {
                capturingHotkey = !capturingHotkey;
            }
            if (GUILayout.Button(Ui("重置", "Reset"), GUILayout.Width(SettingsActionButtonWidth)))
            {
                settingsWindowHotkey = DefaultSettingsWindowHotkey;
                SaveHotkeyConfig();
                capturingHotkey = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            DrawInfoText(Ui("热键保存在 BepInEx/config/OC2MenuManager.hotkey.txt。点击当前热键可改写该文件，也可以直接编辑文本文件。", "The hotkey is stored in BepInEx/config/OC2MenuManager.hotkey.txt. Click the current hotkey to rewrite that file, or edit the text file directly."));
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
            bool chinese = UseChinese();
            switch (value)
            {
                case TrackerLanguage.English:
                    return chinese ? "英文" : "English";
                case TrackerLanguage.Chinese:
                    return chinese ? "中文" : "Chinese";
                default:
                    return chinese ? "自动" : "Auto";
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
            bool chinese = UseChinese();
            switch (value)
            {
                case OverlayTextAlignment.Right:
                    return chinese ? "右对齐" : "Right";
                case OverlayTextAlignment.Center:
                    return chinese ? "居中" : "Center";
                default:
                    return chinese ? "左对齐" : "Left";
            }
        }
    }
}
