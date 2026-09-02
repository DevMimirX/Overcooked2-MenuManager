// Owns persisted recipe selections and one-time tracker configuration cleanup.
// Legacy configuration values may be bound entries or BepInEx orphaned entries;
// both representations are migrated before obsolete keys are removed.
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

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private static readonly PropertyInfo ConfigFileOrphanedEntriesProperty = typeof(ConfigFile).GetProperty(
            "OrphanedEntries",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ConfigFileOrphanedEntriesField = typeof(ConfigFile).GetField(
            "<OrphanedEntries>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool configOrphanedEntriesReflectionWarningLogged;

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
            migratedFirstTicketRowScalePercentValue = TryGetLegacyValue<int>(LegacyFirstTicketRowScaleDefinition);
            migratedLowerTicketRowScalePercentValue = TryGetLegacyValue<int>(LegacyLowerTicketRowScaleDefinition);
        }

        private static void RemoveLegacyConfigEntries()
        {
            bool removedAny = false;
            ConfigFile config = _MODEntry.SettingsConfig;

            if (RemoveConfigDefinition(config, LegacySelectedSceneStateDefinition))
            {
                removedAny = true;
            }

            for (int i = 0; i < LegacyConfigDefinitions.Length; i++)
            {
                if (RemoveConfigDefinition(config, LegacyConfigDefinitions[i]))
                {
                    removedAny = true;
                }
            }

            if (RemoveConfigDefinition(config, LegacyReferenceTicketCountDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyReferenceTicketColorDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyMenuTicketOnMenuColorDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyMenuTicketPreparedColorDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyGuessCountDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyGuessColorDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyFirstTicketRowScaleDefinition))
            {
                removedAny = true;
            }

            if (RemoveConfigDefinition(config, LegacyLowerTicketRowScaleDefinition))
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
            if (RemoveConfigDefinition(config, LegacySettingsWindowHotkeyDefinition))
            {
                config.Save();
            }
        }

        private static void RemoveGeneratedConfigEntries()
        {
            bool removedAny = false;
            ConfigFile config = _MODEntry.SettingsConfig;
            if (RemoveConfigDefinition(config, new ConfigDefinition(TrackerSection, SceneSelectorKey)))
            {
                removedAny = true;
            }

            List<ConfigDefinition> definitions = config.Keys.ToList();
            IDictionary<ConfigDefinition, string> orphanedEntries = GetOrphanedConfigEntries(config);
            if (orphanedEntries != null)
            {
                foreach (ConfigDefinition definition in orphanedEntries.Keys)
                {
                    if (!definitions.Contains(definition))
                    {
                        definitions.Add(definition);
                    }
                }
            }

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
                    if (RemoveConfigDefinition(config, definition))
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
            object value = null;
            if (config.Keys.Contains(definition))
            {
                ConfigEntryBase entry = config[definition];
                if (entry != null)
                {
                    value = entry.BoxedValue;
                }
            }

            if (value == null)
            {
                IDictionary<ConfigDefinition, string> orphanedEntries = GetOrphanedConfigEntries(config);
                string serializedValue;
                if (orphanedEntries == null
                    || !orphanedEntries.TryGetValue(definition, out serializedValue))
                {
                    return null;
                }

                try
                {
                    value = TomlTypeConverter.ConvertToValue(serializedValue, typeof(T));
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                if (value is T)
                {
                    return (T)value;
                }

                if (typeof(T).IsEnum)
                {
                    return (T)Enum.Parse(typeof(T), value.ToString(), true);
                }

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return null;
            }
        }

        private static bool RemoveConfigDefinition(ConfigFile config, ConfigDefinition definition)
        {
            if (config == null || definition == null)
            {
                return false;
            }

            bool removed = config.Remove(definition);
            IDictionary<ConfigDefinition, string> orphanedEntries = GetOrphanedConfigEntries(config);
            if (orphanedEntries != null && orphanedEntries.Remove(definition))
            {
                removed = true;
            }

            return removed;
        }

        private static IDictionary<ConfigDefinition, string> GetOrphanedConfigEntries(ConfigFile config)
        {
            if (config == null)
            {
                return null;
            }

            Exception inspectionFailure = null;
            if (ConfigFileOrphanedEntriesProperty != null)
            {
                try
                {
                    IDictionary<ConfigDefinition, string> entries = ConfigFileOrphanedEntriesProperty.GetValue(config, null)
                        as IDictionary<ConfigDefinition, string>;
                    if (entries != null)
                    {
                        return entries;
                    }
                }
                catch (Exception ex)
                {
                    inspectionFailure = ex;
                }
            }

            if (ConfigFileOrphanedEntriesField != null)
            {
                try
                {
                    return ConfigFileOrphanedEntriesField.GetValue(config)
                        as IDictionary<ConfigDefinition, string>;
                }
                catch (Exception ex)
                {
                    if (inspectionFailure == null)
                    {
                        inspectionFailure = ex;
                    }
                }
            }

            if (inspectionFailure != null && !configOrphanedEntriesReflectionWarningLogged)
            {
                configOrphanedEntriesReflectionWarningLogged = true;
                _MODEntry.LogWarning(
                    "[ServedDishTracker] Could not inspect orphaned BepInEx configuration entries; legacy cleanup will continue with bound entries only: "
                    + inspectionFailure.GetType().Name + ": " + inspectionFailure.Message);
            }

            return null;
        }
    }
}
