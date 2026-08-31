// Verifies exact, overlapping scene-specific batch groups without loading Unity
// or optional-mod assemblies.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

#pragma warning disable CA1861

namespace OC2MenuManager.Tests;

public sealed class SceneRecipeGroupCatalogTests
{
    private static readonly int[] PlayerOne =
    {
        19991000, 19991001, 19991002, 19991003, 19991025, 19991030,
        26020, 112822, 112832, 19990440, 19990441,
        25656, 19990430, 19990431, 19990433
    };

    private static readonly int[] PlayerTwo =
    {
        19991004, 19991005, 19991006, 19991026, 19991027,
        19991011, 19991021, 19991029,
        130976, 228988, 228996, 19990420
    };

    private static readonly int[] PlayerThree =
    {
        19991018, 19991019, 19991020, 19991024, 19991028,
        15614, 15618, 19990406, 19990407, 19990408,
        101593, 19991013, 19991014, 19991015, 19991041
    };

    private static readonly int[] PlayerFour =
    {
        101593, 19991013, 19991014, 19991015, 19991041,
        19991007, 19991010,
        19991008, 19991022, 19991023,
        19991009, 19991016, 19991017
    };

    private static readonly int[] FruitPlatters =
    {
        101593, 19991013, 19991014, 19991015, 19991041
    };

    private static readonly int[] FruitJuices =
    {
        19991009, 19991016, 19991017
    };

    private static readonly int[] FruitIces =
    {
        19991011, 19991021
    };

    [Fact]
    public void RwFiveResolvesAllPlayerAssignmentsAndSharedFruitPlatters()
    {
        int[] authored = GetAuthoredRecipeIds();

        SceneRecipeGroupResolutionStatus status = SceneRecipeGroupCatalog.Resolve(
            "s_rw_5",
            authored,
            authored,
            out SceneRecipeSelectionGroupSet? set,
            out string failureReason);

        Assert.Equal(SceneRecipeGroupResolutionStatus.Resolved, status);
        Assert.Equal(string.Empty, failureReason);
        Assert.NotNull(set);
        Assert.Equal("Track by player:", set.EnglishHeading);
        Assert.Equal("按玩家批量勾选：", set.ChineseHeading);
        Assert.Equal(new[] { "player-1", "player-2", "player-3", "player-4" }, set.Groups.Select(group => group.Key));
        Assert.Equal(PlayerOne, set.Groups[0].RecipeIds);
        Assert.Equal(PlayerTwo, set.Groups[1].RecipeIds);
        Assert.Equal(PlayerThree, set.Groups[2].RecipeIds);
        Assert.Equal(PlayerFour, set.Groups[3].RecipeIds);
        Assert.Equal(new[] { 15, 12, 15, 13 }, set.Groups.Select(group => group.RecipeIds.Count));

        HashSet<int> unique = set.Groups.SelectMany(group => group.RecipeIds).ToHashSet();
        Assert.Equal(50, unique.Count);
        Assert.True(unique.SetEquals(authored));
        Assert.All(FruitPlatters, recipeId => Assert.Contains(recipeId, set.Groups[2].RecipeIds));
        Assert.All(FruitPlatters, recipeId => Assert.Contains(recipeId, set.Groups[3].RecipeIds));
        Assert.All(FruitIces, recipeId => Assert.Contains(recipeId, set.Groups[1].RecipeIds));
        Assert.All(FruitJuices, recipeId => Assert.DoesNotContain(recipeId, set.Groups[1].RecipeIds));
        Assert.All(FruitJuices, recipeId => Assert.Contains(recipeId, set.Groups[3].RecipeIds));
        Assert.All(FruitIces, recipeId => Assert.DoesNotContain(recipeId, set.Groups[3].RecipeIds));
        Assert.Contains(19991029, set.Groups[1].RecipeIds);
        Assert.DoesNotContain(19991029, set.Groups[3].RecipeIds);
    }

    [Fact]
    public void ResolutionIsDeterministicAndDeduplicatesProviderIds()
    {
        int[] authored = GetAuthoredRecipeIds();
        int[] reorderedWithDuplicates = Enumerable
            .Reverse(authored)
            .Concat(authored.Take(4))
            .ToArray();

        Assert.Equal(
            SceneRecipeGroupResolutionStatus.Resolved,
            SceneRecipeGroupCatalog.Resolve(
                "S_RW_5",
                reorderedWithDuplicates,
                reorderedWithDuplicates,
                out SceneRecipeSelectionGroupSet? first,
                out _));
        Assert.Equal(
            SceneRecipeGroupResolutionStatus.Resolved,
            SceneRecipeGroupCatalog.Resolve(
                "s_rw_5",
                authored,
                authored,
                out SceneRecipeSelectionGroupSet? second,
                out _));

        Assert.NotNull(first);
        Assert.NotNull(second);
        for (int i = 0; i < first.Groups.Count; i++)
        {
            Assert.Equal(second.Groups[i].Key, first.Groups[i].Key);
            Assert.Equal(second.Groups[i].RecipeIds, first.Groups[i].RecipeIds);
        }
    }

    [Fact]
    public void UnknownAndChangedScenesDoNotExposePartialGroups()
    {
        int[] authored = GetAuthoredRecipeIds();

        Assert.Equal(
            SceneRecipeGroupResolutionStatus.NotDefined,
            SceneRecipeGroupCatalog.Resolve(
                "s_rw_6",
                authored,
                authored,
                out SceneRecipeSelectionGroupSet? unknown,
                out string unknownReason));
        Assert.Null(unknown);
        Assert.Equal(string.Empty, unknownReason);

        int[] missing = authored.Skip(1).ToArray();
        Assert.Equal(
            SceneRecipeGroupResolutionStatus.Incomplete,
            SceneRecipeGroupCatalog.Resolve(
                "s_rw_5",
                missing,
                missing,
                out SceneRecipeSelectionGroupSet? incomplete,
                out string incompleteReason));
        Assert.Null(incomplete);
        Assert.NotEmpty(incompleteReason);

        int[] changed = authored.Concat(new[] { 29999999 }).ToArray();
        Assert.Equal(
            SceneRecipeGroupResolutionStatus.Incomplete,
            SceneRecipeGroupCatalog.Resolve(
                "s_rw_5",
                changed,
                changed,
                out SceneRecipeSelectionGroupSet? changedSet,
                out string changedReason));
        Assert.Null(changedSet);
        Assert.NotEmpty(changedReason);
    }

    [Fact]
    public void ExtensionRecipesAreAvailableButExcludedFromPlayerGroups()
    {
        int[] authored = GetAuthoredRecipeIds();
        int extensionRecipeId = 29999999;
        int[] available = authored.Concat(new[] { extensionRecipeId }).ToArray();

        SceneRecipeGroupResolutionStatus status = SceneRecipeGroupCatalog.Resolve(
            "s_rw_5",
            authored,
            available,
            out SceneRecipeSelectionGroupSet? set,
            out _);

        Assert.Equal(SceneRecipeGroupResolutionStatus.Resolved, status);
        Assert.NotNull(set);
        Assert.DoesNotContain(extensionRecipeId, set.Groups.SelectMany(group => group.RecipeIds));
    }

    private static int[] GetAuthoredRecipeIds()
    {
        return PlayerOne
            .Concat(PlayerTwo)
            .Concat(PlayerThree)
            .Concat(PlayerFour)
            .Distinct()
            .OrderBy(recipeId => recipeId)
            .ToArray();
    }
}
