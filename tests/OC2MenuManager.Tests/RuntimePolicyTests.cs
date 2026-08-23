using System;
using System.Collections.Generic;
using OC2MenuManager.Infrastructure;
using Xunit;

namespace OC2MenuManager.Tests;

public sealed class RuntimePolicyTests
{
    [Theory]
    [InlineData(5, 5, 5, 10)]
    [InlineData(6, 6, 5, 11)]
    [InlineData(5, 8, 5, 13)]
    [InlineData(-1, -1, -1, 0)]
    public void TicketCapacityIncludesRealOrdersAndReferences(int limit, int realCount, int references, int expected)
    {
        Assert.Equal(expected, TicketCapacityPolicy.CalculateTargetCapacity(limit, realCount, references));
    }

    [Fact]
    public void RecipeExtensionSpecialDynamicWindowsMatchTheExtension()
    {
        AssertWindow("ordinary", 0, false, 153, 0, 153);
        AssertWindow("5_6_Dynamic_Lvl_03_variant", 1, false, 153, 0, 149);
        AssertWindow("5_6_Dynamic_Lvl_03_variant", 2, false, 153, 0, 153);
        AssertWindow("1_6_Dynamic_Lvl_01_variant", 0, false, 153, 5, 153);
        AssertWindow("1_6_Dynamic_Lvl_01_variant", 1, false, 153, 5, 153);
        AssertWindow("1_6_Dynamic_Lvl_01_variant", 2, false, 153, 0, 150);
        AssertWindow("1_6_Dynamic_Lvl_01_variant", 0, true, 153, 0, 153);
    }

    [Fact]
    public void LargeRecipePoolProducesFiniteNormalizedProbabilities()
    {
        const int recipeCount = 153;
        double totalWeight = 0d;
        var weights = new double[recipeCount];
        for (var i = 0; i < recipeCount; i++)
        {
            weights[i] = ProbabilityPolicy.CalculateRawWeight(0, recipeCount, 0);
            totalWeight += weights[i];
        }

        double probabilityTotal = 0d;
        for (var i = 0; i < recipeCount; i++)
        {
            var probability = ProbabilityPolicy.Normalize(weights[i], totalWeight);
            Assert.True(ProbabilityPolicy.IsFinite(probability));
            Assert.True(probability > 0d);
            probabilityTotal += probability;
        }

        Assert.InRange(probabilityTotal, 0.999999999d, 1.000000001d);
        Assert.Equal(0d, ProbabilityPolicy.CalculateRawWeight(int.MaxValue, 0, 0));
        Assert.Equal(0d, ProbabilityPolicy.Normalize(double.NaN, 1d));
        Assert.Equal(0d, ProbabilityPolicy.Normalize(1d, 0d));
    }

    [Fact]
    public void CatalogMergeUpgradesLateDefinitionsWithoutDowngradingThem()
    {
        Assert.Equal(
            RecipeCatalogMergeAction.Add,
            RecipeCatalogMergePolicy.Evaluate(false, false, false, false, false));
        Assert.Equal(
            RecipeCatalogMergeAction.Replace,
            RecipeCatalogMergePolicy.Evaluate(true, false, true, true, false));
        Assert.Equal(
            RecipeCatalogMergeAction.None,
            RecipeCatalogMergePolicy.Evaluate(true, true, false, true, true));
        Assert.Equal(
            RecipeCatalogMergeAction.None,
            RecipeCatalogMergePolicy.Evaluate(true, true, true, false, false));
    }

    [Fact]
    public void SelectionDefaultsToAllButHonorsExplicitSceneSubsets()
    {
        var selected = new HashSet<int> { 42 };
        Assert.True(TrackingSelectionPolicy.IsTracked(false, null!, 999));
        Assert.True(TrackingSelectionPolicy.IsTracked(true, selected, 42));
        Assert.False(TrackingSelectionPolicy.IsTracked(true, selected, 999));
        Assert.False(TrackingSelectionPolicy.IsTracked(true, null!, 42));
    }

    [Theory]
    [InlineData(false, true, false, false, false, false, false, true, (int)NoMenuIneligibility.Disabled)]
    [InlineData(true, false, false, false, false, false, false, true, (int)NoMenuIneligibility.UnsupportedLevel)]
    [InlineData(true, true, true, false, false, false, false, true, (int)NoMenuIneligibility.Boss)]
    [InlineData(true, true, false, true, false, false, false, true, (int)NoMenuIneligibility.Tutorial)]
    [InlineData(true, true, false, false, true, false, false, true, (int)NoMenuIneligibility.Survival)]
    [InlineData(true, true, false, false, false, true, false, true, (int)NoMenuIneligibility.PreTimerOrders)]
    [InlineData(true, true, false, false, false, false, true, true, (int)NoMenuIneligibility.PublicOnline)]
    [InlineData(true, true, false, false, false, false, false, false, (int)NoMenuIneligibility.MissingRuntimeContract)]
    [InlineData(true, true, false, false, false, false, false, true, (int)NoMenuIneligibility.None)]
    public void NoMenuEligibilityFailsClosed(
        bool requested,
        bool kitchen,
        bool boss,
        bool tutorial,
        bool survival,
        bool preTimer,
        bool publicOnline,
        bool contract,
        int expected)
    {
        Assert.Equal(
            (NoMenuIneligibility)expected,
            NoMenuRoundPolicy.Evaluate(requested, kitchen, boss, tutorial, survival, preTimer, publicOnline, contract));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void NoMenuClientFallbackRequiresLocalAuthority(bool inSession, bool isHost, bool expected)
    {
        Assert.Equal(expected, NoMenuClientAuthorityPolicy.ShouldInitializeLocalRoundState(inSession, isHost));
    }

    [Theory]
    [InlineData("Tutorial_01_Config", "Kitchen", true)]
    [InlineData("CustomConfig", "DLC_Tutorial_Kitchen", true)]
    [InlineData("CustomConfig", "Kitchen_01", false)]
    [InlineData(null, null, false)]
    public void NoMenuTutorialDetectionChecksConfigAndScene(string? configName, string? sceneName, bool expected)
    {
        Assert.Equal(expected, NoMenuIdentifierPolicy.IsTutorial(configName!, sceneName!));
    }

    private static void AssertWindow(string levelName, int phase, bool allPhases, int count, int expectedStart, int expectedEnd)
    {
        RecipeExtensionPhasePolicy.GetEntryWindow(levelName, phase, allPhases, count, out var start, out var end);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }
}
