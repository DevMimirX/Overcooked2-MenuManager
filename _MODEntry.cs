using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HostUtilities
{
    [BepInPlugin("com.ch3ngyz.plugin.OC2MenuManager", "[OC2MenuManager] Menu Tools", "1.0.0")]
    [BepInProcess("Overcooked2.exe")]
    public class _MODEntry : BaseUnityPlugin
    {
        private static readonly Action[] ModuleAwakeActions = new Action[]
        {
            DishNameCatalog.Awake,
            MenuManager.Awake,
            NoMenuMode.Awake,
            ServedDishTracker.Awake
        };

        private static readonly Action[] ModuleUpdateActions = new Action[]
        {
            NoMenuMode.Update,
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
        public static float dpiScaleFactor = 1f;
        public static ConfigEntry<int> defaultFontSize;
        public static ConfigEntry<Color> defaultFontColor;

        public void Awake()
        {
            defaultFontSize = Config.Bind<int>("00-UI", "MOD的UI字体大小", 18, new ConfigDescription("MOD的UI字体大小", new AcceptableValueRange<int>(5, 40)));
            defaultFontColor = Config.Bind<Color>("00-UI", "MOD的UI字体颜色", new Color(1f, 1f, 1f, 1f));

            modName = "OC2MenuManager";
            Instance = this;
            logSource = BepInEx.Logging.Logger.CreateLogSource(modName);
            UpdateGUIDpi();

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
    }
}
