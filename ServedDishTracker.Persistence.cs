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
    internal static partial class ServedDishTracker
    {
        private static void LoadSelections()
        {
            TrackedIdsByScene.Clear();
            if (string.IsNullOrEmpty(selectionFilePath) || !File.Exists(selectionFilePath))
            {
                return;
            }

            try
            {
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
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to load selection file: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SaveSelections()
        {
            if (string.IsNullOrEmpty(selectionFilePath))
            {
                return;
            }

            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, HashSet<int>> pair in TrackedIdsByScene.OrderBy(x => x.Key))
            {
                string ids = string.Join(",", pair.Value.OrderBy(x => x).Select(x => x.ToString()).ToArray());
                lines.Add(pair.Key + "=" + ids);
            }

            try
            {
                string directory = Path.GetDirectoryName(selectionFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllLines(selectionFilePath, lines.ToArray());
            }
            catch (Exception ex)
            {
                _MODEntry.LogWarning("[ServedDishTracker] Failed to save selection file: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CaptureLegacyValues()
        {
            migratedEnabledValue = TryGetLegacyValue<bool>(LegacyEnabledDefinition);
            migratedLanguageValue = TryGetLegacyValue<TrackerLanguage>(LegacyLanguageDefinition);
            migratedMenuTicketOnMenuTintColorValue = TryGetLegacyValue<Color>(LegacyMenuTicketOnMenuColorDefinition);
            migratedMenuTicketPreparedTintColorValue = TryGetLegacyValue<Color>(LegacyMenuTicketPreparedColorDefinition);
            migratedReferenceTicketCountValue = TryGetLegacyValue<int>(LegacyGuessCountDefinition) ?? TryGetLegacyValue<int>(LegacyReferenceTicketCountDefinition);
            migratedReferenceTicketTintColorValue = TryGetLegacyValue<Color>(LegacyGuessColorDefinition) ?? TryGetLegacyValue<Color>(LegacyReferenceTicketColorDefinition);
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

            if (config.Remove(LegacyReferenceTicketCountDefinition))
            {
                removedAny = true;
            }

            if (config.Remove(LegacyReferenceTicketColorDefinition))
            {
                removedAny = true;
            }

            if (config.Remove(LegacyMenuTicketOnMenuColorDefinition))
            {
                removedAny = true;
            }

            if (config.Remove(LegacyMenuTicketPreparedColorDefinition))
            {
                removedAny = true;
            }

            if (config.Remove(LegacyGuessCountDefinition))
            {
                removedAny = true;
            }

            if (config.Remove(LegacyGuessColorDefinition))
            {
                removedAny = true;
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
