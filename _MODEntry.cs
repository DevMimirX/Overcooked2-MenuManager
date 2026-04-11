using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using HostUtilities;

namespace OC2MenuManager
{
    [BepInPlugin("com.ch3ngyz.plugin.OC2MenuManager", "[OC2MenuManager] Menu Tools", "1.0.0")]
    [BepInProcess("Overcooked2.exe")]
    public class _MODEntry : BaseUnityPlugin
    {
        private const string PluginGuid = "com.ch3ngyz.plugin.OC2MenuManager";
        private const string SettingsConfigBaseName = "OC2MenuManager";
        private static readonly Action[] ModuleAwakeActions = new Action[]
        {
            DishNameCatalog.Awake,
            MenuManager.Awake,
            NoMenuMode.Awake,
            ServedDishTracker.Awake
        };

        private static readonly Action[] ModuleUpdateActions = new Action[]
        {
            ServedDishTracker.Update
        };

        private static readonly Action[] ModuleOnGuiActions = new Action[]
        {
            ServedDishTracker.OnGUI
        };

        private const float BaseScreenWidth = 1920f;
        private const float BaseScreenHeight = 1080f;

        private static ManualLogSource logSource;

        public static Harmony HarmonyInstance { get; set; }
        public static readonly List<string> AllHarmonyName = new List<string>();
        public static readonly List<Harmony> AllHarmony = new List<Harmony>();
        public static string modName;
        public static _MODEntry Instance;
        public static ConfigFile SettingsConfig { get; private set; }
        public static string HotkeyConfigPath { get; private set; }
        public static float dpiScaleFactor = 1f;
        public static ConfigEntry<int> defaultFontSize;
        public static ConfigEntry<Color> defaultFontColor;

        public void Awake()
        {
            modName = "OC2MenuManager";
            Instance = this;
            logSource = BepInEx.Logging.Logger.CreateLogSource(modName);
            InitializeSettingsConfig();
            defaultFontSize = SettingsConfig.Bind<int>("00-UI", "MOD的UI字体大小", 18, new ConfigDescription("MOD的UI字体大小", new AcceptableValueRange<int>(5, 40)));
            defaultFontColor = SettingsConfig.Bind<Color>("00-UI", "MOD的UI字体颜色", new Color(1f, 1f, 1f, 1f));
            UpdateGUIDpi();
            PluginRuntimeContext.Configure(
                CreateAndRegisterHarmony,
                LogInfo,
                delegate { return defaultFontSize.Value; },
                delegate { return defaultFontColor.Value; },
                delegate { return dpiScaleFactor; });

            for (int i = 0; i < ModuleAwakeActions.Length; i++)
            {
                ModuleAwakeActions[i]();
            }

            HarmonyInstance = ModuleUtility.RegisterHarmony(typeof(_MODEntry));
        }

        private void OnDestroy()
        {
            Instance = null;
            for (int i = 0; i < AllHarmony.Count; i++)
            {
                Harmony harmony = AllHarmony[i];
                if (harmony != null)
                {
                    harmony.UnpatchAll(harmony.Id);
                }
            }

            AllHarmony.Clear();
            AllHarmonyName.Clear();
        }

        public void Update()
        {
            int expectedWidth = Mathf.RoundToInt(BaseScreenWidth * dpiScaleFactor);
            int expectedHeight = Mathf.RoundToInt(BaseScreenHeight * dpiScaleFactor);
            if (Screen.width != expectedWidth || Screen.height != expectedHeight)
            {
                UpdateGUIDpi();
            }

            for (int i = 0; i < ModuleUpdateActions.Length; i++)
            {
                ModuleUpdateActions[i]();
            }
        }

        public void OnGUI()
        {
            for (int i = 0; i < ModuleOnGuiActions.Length; i++)
            {
                ModuleOnGuiActions[i]();
            }
        }

        public static void RegisterHarmony(string harmonyName, Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            AllHarmony.Add(harmony);
            AllHarmonyName.Add(harmonyName);
        }

        private static Harmony CreateAndRegisterHarmony(Type type)
        {
            Harmony harmony = Harmony.CreateAndPatchAll(type);
            RegisterHarmony(type.Name, harmony);
            return harmony;
        }

        private void UpdateGUIDpi()
        {
            float ratioWidth = (float)Screen.width / BaseScreenWidth;
            float ratioHeight = (float)Screen.height / BaseScreenHeight;
            dpiScaleFactor = Mathf.Min(ratioWidth, ratioHeight);
        }

        public static void LogWarning(string message)
        {
            if (logSource != null)
            {
                logSource.LogWarning(message);
            }
        }

        public static void LogInfo(string message)
        {
            if (logSource != null)
            {
                logSource.LogInfo(message);
            }
        }

        public static void LogError(string message)
        {
            if (logSource != null)
            {
                logSource.LogError(message);
            }
        }

        private static void InitializeSettingsConfig()
        {
            string standaloneConfigPath = Path.Combine(Paths.ConfigPath, SettingsConfigBaseName + ".standalone.cfg");
            string legacyConfigPath = Path.Combine(Paths.ConfigPath, SettingsConfigBaseName + ".cfg");
            string oldStandaloneConfigPath = Path.Combine(Paths.ConfigPath, PluginGuid + ".standalone.cfg");
            string oldLegacyConfigPath = Path.Combine(Paths.ConfigPath, PluginGuid + ".cfg");
            HotkeyConfigPath = Path.Combine(Paths.ConfigPath, "OC2MenuManager.hotkey.txt");
            if (!File.Exists(standaloneConfigPath))
            {
                string migrationSourcePath = null;
                if (File.Exists(oldStandaloneConfigPath))
                {
                    migrationSourcePath = oldStandaloneConfigPath;
                }
                else if (File.Exists(legacyConfigPath))
                {
                    migrationSourcePath = legacyConfigPath;
                }
                else if (File.Exists(oldLegacyConfigPath))
                {
                    migrationSourcePath = oldLegacyConfigPath;
                }

                if (!string.IsNullOrEmpty(migrationSourcePath))
                {
                    File.Copy(migrationSourcePath, standaloneConfigPath, true);
                }
            }

            SettingsConfig = new ConfigFile(standaloneConfigPath, true);
        }
    }
}
