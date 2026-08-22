using System.Collections.Generic;
using HarmonyLib;
using Team17.Online;

namespace OC2MenuManager
{
    internal static class MenuLevelHelper
    {
        public sealed class LevelNameInfo
        {
            public readonly string ThemeName;
            public readonly string LevelName;
            public readonly string ThemeLabel;
            public readonly string LevelLabel;

            public LevelNameInfo(string themeName, string levelName, string themeLabel, string levelLabel)
            {
                ThemeName = themeName;
                LevelName = levelName;
                ThemeLabel = themeLabel;
                LevelLabel = levelLabel;
            }
        }

        public static List<SceneDirectoryData.SceneDirectoryEntry> GetLevelList()
        {
            ServerLobbyFlowController serverLobby = ServerLobbyFlowController.Instance;
            LobbyFlowController lobby = LobbyFlowController.Instance;
            if (serverLobby == null || lobby == null)
            {
                return new List<SceneDirectoryData.SceneDirectoryEntry>();
            }

            SceneDirectoryData[] sceneDirectories = lobby.GetSceneDirectories();
            DLCManager dlcManager = GameUtils.RequireManager<DLCManager>();
            if (sceneDirectories == null || dlcManager == null)
            {
                return new List<SceneDirectoryData.SceneDirectoryEntry>();
            }

            List<DLCFrontendData> allDlc = dlcManager.AllDlc;
            GameSession.GameType gameType = GetLobbyGameType(serverLobby);
            List<SceneDirectoryData.SceneDirectoryEntry> list = new List<SceneDirectoryData.SceneDirectoryEntry>();
            for (int i = 0; i < sceneDirectories.Length; i++)
            {
                SceneDirectoryData sceneDirectory = sceneDirectories[i];
                if (sceneDirectory == null || sceneDirectory.Scenes == null)
                {
                    continue;
                }

                DLCFrontendData matchedDlc = null;
                int dlcId = lobby.GetDLCIDFromSceneDirIndex(gameType, i);
                for (int j = 0; j < allDlc.Count; j++)
                {
                    DLCFrontendData candidate = allDlc[j];
                    if (candidate != null && candidate.m_DLCID == dlcId)
                    {
                        matchedDlc = candidate;
                        break;
                    }
                }

                if (matchedDlc == null || dlcManager.IsDLCAvailable(matchedDlc))
                {
                    list.AddRange(sceneDirectory.Scenes);
                }
            }

            list.RemoveAll(delegate(SceneDirectoryData.SceneDirectoryEntry entry)
            {
                string label = entry != null ? entry.Label : null;
                return string.IsNullOrEmpty(label)
                    || label.Contains("ThroneRoom")
                    || label.Contains("Tutorial")
                    || label.Contains("DLC07Battlements08");
            });
            return list;
        }

        private static GameSession.GameType GetLobbyGameType(ServerLobbyFlowController serverLobby)
        {
            if (serverLobby == null)
            {
                return GameSession.GameType.Cooperative;
            }

            try
            {
                AccessTools.FieldRef<ServerLobbyFlowController, bool> coopRef = AccessTools.FieldRefAccess<ServerLobbyFlowController, bool>("m_bIsCoop");
                return coopRef(serverLobby) ? GameSession.GameType.Cooperative : GameSession.GameType.Competitive;
            }
            catch
            {
                return GameSession.GameType.Cooperative;
            }
        }

        public static string GetLevelName(SceneDirectoryData.SceneDirectoryEntry entry, bool withLevelLabel)
        {
            return GetLevelName(LobbyFlowController.Instance, entry, withLevelLabel);
        }

        public static string GetLevelName(LobbyFlowController lobbyFlow, SceneDirectoryData.SceneDirectoryEntry entry, bool withLevelLabel)
        {
            LevelNameInfo info = GetLevelNameInfo(lobbyFlow, entry);
            return info.ThemeName + " - " + info.LevelName + (withLevelLabel
                ? " (" + info.ThemeLabel + " - " + info.LevelLabel + ")"
                : string.Empty);
        }

        public static LevelNameInfo GetLevelNameInfo(LobbyFlowController lobbyFlow, SceneDirectoryData.SceneDirectoryEntry entry)
        {
            if (entry == null)
            {
                return new LevelNameInfo("Other", "Unknown", "Other", string.Empty);
            }

            string themeLabel;
            string themeName;
            if (lobbyFlow != null)
            {
                ThemeSelectButton button = lobbyFlow.m_themeSelectMenu.GetButtonForTheme(entry.Theme);
                if (button == null)
                {
                    return GetLevelNameInfo(null, entry);
                }

                T17Text themeText = button.GetComponentInChildren<T17Text>(true);
                if (themeText == null)
                {
                    return GetLevelNameInfo(null, entry);
                }

                themeLabel = themeText.m_LocalizationTag;
                themeName = themeText.text;
            }
            else
            {
                ThemeLabels.TryGetValue(entry.Theme, out themeLabel);
                if (themeLabel == null)
                {
                    themeLabel = "Other";
                    themeName = "Other";
                }
                else
                {
                    Localization.Get(themeLabel, out themeName, new LocToken[0]);
                }
            }

            string levelName;
            Localization.Get(entry.Label, out levelName, new LocToken[0]);
            return new LevelNameInfo(themeName, levelName, themeLabel, entry.Label);
        }

        public static readonly Dictionary<SceneDirectoryData.LevelTheme, string> ThemeLabels = new Dictionary<SceneDirectoryData.LevelTheme, string>
        {
            { SceneDirectoryData.LevelTheme.Sushi, "Text.Theme.Sushi" },
            { SceneDirectoryData.LevelTheme.Balloon, "Text.Theme.Balloon" },
            { SceneDirectoryData.LevelTheme.Wizard, "Text.Theme.Wizard" },
            { SceneDirectoryData.LevelTheme.Space, "Text.Theme.Alien" },
            { SceneDirectoryData.LevelTheme.Rapids, "Text.Theme.Rapids" },
            { SceneDirectoryData.LevelTheme.Mine, "Text.Theme.Mine" },
            { SceneDirectoryData.LevelTheme.Random, "Text.Theme.Random" },
            { SceneDirectoryData.LevelTheme.Beach, "Text.Theme.Beach" },
            { SceneDirectoryData.LevelTheme.Resort, "Text.Theme.Resort" },
            { SceneDirectoryData.LevelTheme.Wonderland, "Text.Theme.Wonderland" },
            { SceneDirectoryData.LevelTheme.ChinaTown, "Text.Theme.Lunar" },
            { SceneDirectoryData.LevelTheme.Campsite, "Text.Theme.Campsite" },
            { SceneDirectoryData.LevelTheme.Treehouse, "Text.Theme.Treehouse" },
            { SceneDirectoryData.LevelTheme.Keep, "Text.Theme.Keep" },
            { SceneDirectoryData.LevelTheme.Courtyard, "Text.Theme.Courtyard" },
            { SceneDirectoryData.LevelTheme.Battlements, "Text.Theme.Battlements" },
            { SceneDirectoryData.LevelTheme.Outside, "Text.Theme.CircusGrounds" },
            { SceneDirectoryData.LevelTheme.Inside, "Text.Theme.Tent" },
            { SceneDirectoryData.LevelTheme.Wonderland2, "Text.Theme.DLC09Theme01" },
            { SceneDirectoryData.LevelTheme.ChinaTown2, "Text.Theme.Lunar2" },
            { SceneDirectoryData.LevelTheme.Summer, "Text.Theme.Summer" },
            { SceneDirectoryData.LevelTheme.ChinaTown3, "Text.Theme.MoonFestival" }
        };
    }
}
