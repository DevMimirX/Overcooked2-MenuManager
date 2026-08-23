using System;
using System.Collections.Generic;
using OC2MenuManager.Infrastructure;
using Xunit;

namespace OC2MenuManager.Tests;

public sealed class RuntimePolicyTests
{
    [Theory]
    [InlineData(5, 5, 5, 10)]
    [InlineData(6, 6, 4, 10)]
    [InlineData(8, 8, 2, 10)]
    [InlineData(8, 3, 5, 8)]
    [InlineData(8, 11, 0, 11)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(-1, -1, -1, 0)]
    public void TicketCapacityCoversRealOrdersAndActiveReferences(int limit, int realCount, int references, int expected)
    {
        Assert.Equal(expected, TicketCapacityPolicy.CalculateTargetCapacity(limit, realCount, references));
    }

    [Theory]
    [InlineData(3, 5, 10, 5)]
    [InlineData(5, 5, 10, 5)]
    [InlineData(6, 5, 10, 4)]
    [InlineData(7, 5, 10, 3)]
    [InlineData(8, 5, 10, 2)]
    [InlineData(9, 5, 10, 1)]
    [InlineData(10, 5, 10, 0)]
    [InlineData(11, 5, 10, 0)]
    [InlineData(-1, 5, 10, 5)]
    [InlineData(5, -1, 10, 0)]
    [InlineData(5, 5, -1, 0)]
    public void GuessTicketsUseOnlyTheRemainingCombinedBudget(int realCount, int configuredGuesses, int combinedLimit, int expected)
    {
        Assert.Equal(expected, TicketCapacityPolicy.CalculateAllowedReferenceTickets(realCount, configuredGuesses, combinedLimit));
    }

    [Theory]
    [InlineData(5, 8, 6, 3, 8)]
    [InlineData(5, 5, 6, 3, 6)]
    [InlineData(5, 5, 5, 9, 9)]
    [InlineData(-1, -1, -1, -1, 0)]
    public void EffectiveRealLimitUsesTheLargestUnmodifiedContract(int baseLimit, int rawLimit, int reportedLimit, int observed, int expected)
    {
        Assert.Equal(expected, TicketCapacityPolicy.CalculateEffectiveRealLimit(baseLimit, rawLimit, reportedLimit, observed));
    }

    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(9, 10, true)]
    [InlineData(-1, 10, false)]
    [InlineData(10, 10, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, -1, false)]
    public void TableIndexValidationRejectsUnsafeReleases(int tableIndex, int tableCount, bool expected)
    {
        Assert.Equal(expected, TicketCapacityPolicy.IsValidTableIndex(tableIndex, tableCount));
    }

    [Fact]
    public void IncomingRealOrdersTrimGuessesWithoutTruncatingRealCapacity()
    {
        const int configuredGuesses = 5;
        const int combinedLimit = 10;
        const int diyRealLimit = 8;

        for (var projectedRealCount = 4; projectedRealCount <= 11; projectedRealCount++)
        {
            var guesses = TicketCapacityPolicy.CalculateAllowedReferenceTickets(projectedRealCount, configuredGuesses, combinedLimit);
            var capacity = TicketCapacityPolicy.CalculateTargetCapacity(diyRealLimit, projectedRealCount, guesses);

            Assert.True(capacity >= projectedRealCount);
            Assert.True(projectedRealCount > combinedLimit || projectedRealCount + guesses <= combinedLimit);
            Assert.Equal(projectedRealCount > combinedLimit ? projectedRealCount : Math.Max(diyRealLimit, projectedRealCount + guesses), capacity);
        }
    }

    [Theory]
    [InlineData(false, 7, 8)]
    [InlineData(false, 8, 8)]
    [InlineData(false, 11, 11)]
    [InlineData(true, 7, 10)]
    [InlineData(true, 8, 10)]
    [InlineData(true, 11, 11)]
    public void IncomingRealOrdersAlwaysReserveCapacityEvenWhenTrackingIsDisabled(bool trackerEnabled, int projectedRealCount, int expectedCapacity)
    {
        const int vanillaLimit = 5;
        const int diyRawLimit = 8;
        const int recipeExtensionReportedLimit = 6;
        const int configuredGuesses = 5;
        const int combinedLimit = 10;

        var effectiveRealLimit = TicketCapacityPolicy.CalculateEffectiveRealLimit(
            vanillaLimit,
            diyRawLimit,
            recipeExtensionReportedLimit,
            projectedRealCount);
        var guesses = TicketCapacityPolicy.CalculateAllowedReferenceTickets(
            projectedRealCount,
            trackerEnabled ? configuredGuesses : 0,
            combinedLimit);
        var capacity = TicketCapacityPolicy.CalculateTargetCapacity(effectiveRealLimit, projectedRealCount, guesses);

        Assert.Equal(expectedCapacity, capacity);
        Assert.True(capacity >= projectedRealCount);
    }

    [Theory]
    [InlineData(9, 3, 12, true)]
    [InlineData(9, 0, 9, true)]
    [InlineData(9, 3, 11, false)]
    [InlineData(1, 3, 4, false)]
    [InlineData(9, -1, 8, false)]
    [InlineData(int.MaxValue, 1, int.MaxValue, false)]
    public void RecipeExtensionCarnivalCandidatesRequireAnExactFrequencyShape(int baseCount, int extensionCount, int frequencyCount, bool expected)
    {
        Assert.Equal(
            expected,
            CarnivalRecipeSelectionPolicy.HasCompatibleCandidateShape(baseCount, extensionCount, frequencyCount));
    }

    [Fact]
    public void CarnivalOpeningRulesRestrictOnlyTheOriginalBaseRecipes()
    {
        const int baseRecipeCount = 9;
        var frequencies = new int[12];
        var firstWeights = CalculateCarnivalWeights(frequencies, baseRecipeCount, false);

        foreach (var excludedIndex in new[] { 0, 1, 5, 6, 7 })
        {
            Assert.Equal(0f, firstWeights[excludedIndex]);
        }

        Assert.True(firstWeights[2] > 0f);
        Assert.True(firstWeights[9] > 0f);
        Assert.True(firstWeights[10] > 0f);
        Assert.True(firstWeights[11] > 0f);

        frequencies[2] = 1;
        var secondWeights = CalculateCarnivalWeights(frequencies, baseRecipeCount, false);
        Assert.Equal(0f, secondWeights[0]);
        Assert.Equal(0f, secondWeights[1]);
        Assert.True(secondWeights[5] > 0f);
        Assert.True(secondWeights[9] > 0f);
    }

    [Fact]
    public void CarnivalCakeBoostLeavesGeneratedRecipesOnTheExtensionFairnessCurve()
    {
        const int baseRecipeCount = 9;
        var frequencies = new int[12];
        frequencies[11] = 2;

        var weights = CalculateCarnivalWeights(frequencies, baseRecipeCount, true);

        Assert.InRange(weights[0], 0.99999f, 1.00001f);
        Assert.InRange(weights[1], 0.99999f, 1.00001f);
        Assert.InRange(weights[2], 0.33332f, 0.33334f);
        Assert.InRange(weights[9], 0.33332f, 0.33334f);
        Assert.Equal(0f, weights[11]);
    }

    [Fact]
    public void CarnivalLargeExtensionPoolProducesFiniteGeneratedRecipeWeights()
    {
        const int baseRecipeCount = 9;
        const int extensionRecipeCount = 153;
        var frequencies = new int[baseRecipeCount + extensionRecipeCount];
        var weights = CalculateCarnivalWeights(frequencies, baseRecipeCount, true);

        for (var i = 0; i < weights.Length; i++)
        {
            Assert.False(float.IsNaN(weights[i]));
            Assert.False(float.IsInfinity(weights[i]));
            Assert.True(weights[i] >= 0f);
        }

        Assert.True(weights[baseRecipeCount] > 0f);
        Assert.True(weights[weights.Length - 1] > 0f);
    }

    [Fact]
    public void CarnivalForcedCakeCheckpointsTargetOnlyTheOriginalCakeEntries()
    {
        const int baseRecipeCount = 9;

        var checkpoint46 = new int[12];
        checkpoint46[2] = 46;
        AssertOnlyPositiveIndices(CalculateCarnivalWeights(checkpoint46, baseRecipeCount, true), 0, 1);

        var checkpoint49 = new int[12];
        checkpoint49[0] = 4;
        checkpoint49[1] = 5;
        checkpoint49[2] = 40;
        AssertOnlyPositiveIndices(CalculateCarnivalWeights(checkpoint49, baseRecipeCount, true), 0);

        var checkpoint54 = new int[12];
        checkpoint54[0] = 4;
        checkpoint54[2] = 50;
        AssertOnlyPositiveIndices(CalculateCarnivalWeights(checkpoint54, baseRecipeCount, true), 0);

        var checkpoint55 = new int[12];
        checkpoint55[1] = 4;
        checkpoint55[2] = 51;
        AssertOnlyPositiveIndices(CalculateCarnivalWeights(checkpoint55, baseRecipeCount, true), 1);
    }

    [Fact]
    public void CarnivalCheckpointFallsBackToCombinedFairnessWhenBothCakesAreUnavailable()
    {
        const int baseRecipeCount = 9;
        var frequencies = new int[12];
        frequencies[0] = 5;
        frequencies[1] = 5;
        frequencies[2] = 39;

        var weights = CalculateCarnivalWeights(frequencies, baseRecipeCount, true);

        Assert.Equal(0f, weights[0]);
        Assert.Equal(0f, weights[1]);
        Assert.True(weights[3] > 0f);
        Assert.True(weights[9] > 0f);
    }

    [Fact]
    public void CarnivalWeightPolicyFailsOpenForInvalidFrequencyState()
    {
        var frequencies = new int[12];
        frequencies[4] = -1;

        Assert.False(CarnivalRecipeSelectionPolicy.TryCalculateWeight(frequencies, 9, 4, true, out _));
        Assert.False(CarnivalRecipeSelectionPolicy.TryCalculateWeights(frequencies, 9, true, new float[12]));
        Assert.False(CarnivalRecipeSelectionPolicy.TryCalculateWeights(new int[12], 9, true, new float[11]));
        Assert.False(CarnivalRecipeSelectionPolicy.TryCalculateWeight(frequencies, 13, 4, true, out _));
        Assert.False(CarnivalRecipeSelectionPolicy.TryCalculateWeight(frequencies, 9, 12, true, out _));
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

    private static float[] CalculateCarnivalWeights(int[] frequencies, int baseRecipeCount, bool cakeRulesEnabled)
    {
        var weights = new float[frequencies.Length];
        Assert.True(CarnivalRecipeSelectionPolicy.TryCalculateWeights(
            frequencies,
            baseRecipeCount,
            cakeRulesEnabled,
            weights));

        return weights;
    }

    private static void AssertOnlyPositiveIndices(float[] weights, params int[] expectedPositiveIndices)
    {
        var expected = new HashSet<int>(expectedPositiveIndices);
        for (var i = 0; i < weights.Length; i++)
        {
            if (expected.Contains(i))
            {
                Assert.True(weights[i] > 0f);
            }
            else
            {
                Assert.Equal(0f, weights[i]);
            }
        }
    }
}
