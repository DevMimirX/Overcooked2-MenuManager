// Owns declarative, scene-specific secondary recipe groups used by batch
// selection. Definitions are exact ID mappings, may overlap intentionally, and
// resolve only when the complete authored recipe set is available; canonical
// recipe categories and persisted tracked IDs remain separate concerns.
#nullable disable
using System;
using System.Collections.Generic;

namespace OC2MenuManager
{
    /// <summary>Describes the outcome of resolving a scene's secondary group definition.</summary>
    internal enum SceneRecipeGroupResolutionStatus
    {
        NotDefined,
        Resolved,
        Incomplete
    }

    /// <summary>
    /// Describes one reusable batch-selection group. Recipe order is stable,
    /// duplicate IDs are removed, and the same ID may belong to other groups.
    /// </summary>
    internal sealed class RecipeSelectionGroup
    {
        internal string Key = string.Empty;
        internal string EnglishName = string.Empty;
        internal string ChineseName = string.Empty;
        internal int SortTier;
        internal readonly List<int> RecipeIds = new List<int>();

        internal RecipeSelectionGroup()
        {
        }

        internal RecipeSelectionGroup(
            string key,
            string englishName,
            string chineseName,
            IEnumerable<int> recipeIds)
        {
            Key = key ?? string.Empty;
            EnglishName = englishName ?? string.Empty;
            ChineseName = chineseName ?? string.Empty;

            HashSet<int> seen = new HashSet<int>();
            if (recipeIds == null)
            {
                return;
            }

            foreach (int recipeId in recipeIds)
            {
                if (recipeId != 0 && seen.Add(recipeId))
                {
                    RecipeIds.Add(recipeId);
                }
            }
        }
    }

    /// <summary>
    /// Carries one scene's localized secondary-group heading and ordered groups.
    /// It is a selector model only and never changes recipe category assignments.
    /// </summary>
    internal sealed class SceneRecipeSelectionGroupSet
    {
        internal string Key = string.Empty;
        internal string EnglishHeading = string.Empty;
        internal string ChineseHeading = string.Empty;
        internal readonly List<RecipeSelectionGroup> Groups = new List<RecipeSelectionGroup>();

        internal SceneRecipeSelectionGroupSet(string key, string englishHeading, string chineseHeading)
        {
            Key = key ?? string.Empty;
            EnglishHeading = englishHeading ?? string.Empty;
            ChineseHeading = chineseHeading ?? string.Empty;
        }
    }

    /// <summary>
    /// Resolves exact authored-recipe mappings without inspecting optional-mod
    /// objects. Unknown scenes return <see cref="SceneRecipeGroupResolutionStatus.NotDefined"/>;
    /// incomplete or changed authored catalogs fail closed without partial groups.
    /// </summary>
    internal static class SceneRecipeGroupCatalog
    {
        private static readonly int[] RwFivePlayerOneRecipeIds = new int[]
        {
            19991000, 19991001, 19991002, 19991003, 19991025, 19991030,
            26020, 112822, 112832, 19990440, 19990441,
            25656, 19990430, 19990431, 19990433
        };

        private static readonly int[] RwFivePlayerTwoRecipeIds = new int[]
        {
            19991004, 19991005, 19991006, 19991026, 19991027,
            19991009, 19991016, 19991017,
            130976, 228988, 228996, 19990420
        };

        private static readonly int[] RwFivePlayerThreeRecipeIds = new int[]
        {
            19991018, 19991019, 19991020, 19991024, 19991028,
            15614, 15618, 19990406, 19990407, 19990408,
            101593, 19991013, 19991014, 19991015, 19991041
        };

        private static readonly int[] RwFivePlayerFourRecipeIds = new int[]
        {
            101593, 19991013, 19991014, 19991015, 19991041,
            19991007, 19991010, 19991029,
            19991008, 19991022, 19991023,
            19991011, 19991021
        };

        private static readonly Dictionary<string, SceneRecipeSelectionGroupSet> Definitions = BuildDefinitions();

        internal static SceneRecipeGroupResolutionStatus Resolve(
            string sceneName,
            IEnumerable<int> authoredRecipeIds,
            IEnumerable<int> availableRecipeIds,
            out SceneRecipeSelectionGroupSet resolved,
            out string failureReason)
        {
            resolved = null;
            failureReason = string.Empty;

            SceneRecipeSelectionGroupSet definition;
            if (string.IsNullOrEmpty(sceneName)
                || !Definitions.TryGetValue(sceneName, out definition)
                || definition == null)
            {
                return SceneRecipeGroupResolutionStatus.NotDefined;
            }

            bool authoredInputValid;
            bool availableInputValid;
            HashSet<int> authored = BuildRecipeIdSet(authoredRecipeIds, out authoredInputValid);
            HashSet<int> available = BuildRecipeIdSet(availableRecipeIds, out availableInputValid);
            if (!authoredInputValid || !availableInputValid)
            {
                failureReason = "the provider catalog contained an invalid recipe ID";
                return SceneRecipeGroupResolutionStatus.Incomplete;
            }

            if (string.IsNullOrEmpty(definition.Key)
                || string.IsNullOrEmpty(definition.EnglishHeading)
                || string.IsNullOrEmpty(definition.ChineseHeading)
                || definition.Groups.Count == 0)
            {
                failureReason = "the secondary group definition is malformed";
                return SceneRecipeGroupResolutionStatus.Incomplete;
            }

            HashSet<string> groupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> definedRecipeIds = new HashSet<int>();
            for (int i = 0; i < definition.Groups.Count; i++)
            {
                RecipeSelectionGroup group = definition.Groups[i];
                if (group == null
                    || string.IsNullOrEmpty(group.Key)
                    || string.IsNullOrEmpty(group.EnglishName)
                    || string.IsNullOrEmpty(group.ChineseName)
                    || !groupKeys.Add(group.Key)
                    || group.RecipeIds.Count == 0)
                {
                    failureReason = "the secondary group definition is malformed";
                    return SceneRecipeGroupResolutionStatus.Incomplete;
                }

                for (int j = 0; j < group.RecipeIds.Count; j++)
                {
                    int recipeId = group.RecipeIds[j];
                    definedRecipeIds.Add(recipeId);
                    if (!available.Contains(recipeId))
                    {
                        failureReason = "mapped recipe " + recipeId + " is unavailable";
                        return SceneRecipeGroupResolutionStatus.Incomplete;
                    }
                }
            }

            if (!definedRecipeIds.SetEquals(authored))
            {
                failureReason = "the authored recipe set no longer matches the secondary group definition";
                return SceneRecipeGroupResolutionStatus.Incomplete;
            }

            resolved = CloneDefinition(definition);
            return SceneRecipeGroupResolutionStatus.Resolved;
        }

        private static HashSet<int> BuildRecipeIdSet(IEnumerable<int> recipeIds, out bool valid)
        {
            valid = recipeIds != null;
            HashSet<int> result = new HashSet<int>();
            if (recipeIds == null)
            {
                return result;
            }

            foreach (int recipeId in recipeIds)
            {
                if (recipeId == 0)
                {
                    valid = false;
                    continue;
                }

                result.Add(recipeId);
            }

            return result;
        }

        private static SceneRecipeSelectionGroupSet CloneDefinition(SceneRecipeSelectionGroupSet definition)
        {
            SceneRecipeSelectionGroupSet result = new SceneRecipeSelectionGroupSet(
                definition.Key,
                definition.EnglishHeading,
                definition.ChineseHeading);
            for (int i = 0; i < definition.Groups.Count; i++)
            {
                RecipeSelectionGroup source = definition.Groups[i];
                RecipeSelectionGroup group = new RecipeSelectionGroup(
                    source.Key,
                    source.EnglishName,
                    source.ChineseName,
                    source.RecipeIds);
                group.SortTier = source.SortTier;
                result.Groups.Add(group);
            }

            return result;
        }

        private static Dictionary<string, SceneRecipeSelectionGroupSet> BuildDefinitions()
        {
            Dictionary<string, SceneRecipeSelectionGroupSet> definitions =
                new Dictionary<string, SceneRecipeSelectionGroupSet>(StringComparer.OrdinalIgnoreCase);

            SceneRecipeSelectionGroupSet rwFive = new SceneRecipeSelectionGroupSet(
                "player-assignment",
                "Track by player:",
                "按玩家批量勾选：");
            rwFive.Groups.Add(new RecipeSelectionGroup(
                "player-1",
                "Player 1",
                "1号",
                RwFivePlayerOneRecipeIds));
            rwFive.Groups.Add(new RecipeSelectionGroup(
                "player-2",
                "Player 2",
                "2号",
                RwFivePlayerTwoRecipeIds));
            rwFive.Groups.Add(new RecipeSelectionGroup(
                "player-3",
                "Player 3",
                "3号",
                RwFivePlayerThreeRecipeIds));
            rwFive.Groups.Add(new RecipeSelectionGroup(
                "player-4",
                "Player 4",
                "4号",
                RwFivePlayerFourRecipeIds));
            definitions.Add("s_rw_5", rwFive);

            return definitions;
        }
    }
}
