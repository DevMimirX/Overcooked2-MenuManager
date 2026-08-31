using System;
using System.Collections.Generic;
using OC2MenuManager.Infrastructure;
using Xunit;

#pragma warning disable CA1861

namespace OC2MenuManager.Tests;

public sealed class RuntimePolicyTests
{
    [Fact]
    public void PreparedPlatingCompatibilityMirrorsBaseGamePlateGate()
    {
        object sharedStep = new();
        object distinctStep = new();
        object noStep = null!;

        Assert.True(PreparedPlatingCompatibilityPolicy.IsCompatible(false, sharedStep, distinctStep));
        Assert.True(PreparedPlatingCompatibilityPolicy.IsCompatible(true, sharedStep, sharedStep));
        Assert.False(PreparedPlatingCompatibilityPolicy.IsCompatible(true, sharedStep, distinctStep));
        Assert.True(PreparedPlatingCompatibilityPolicy.IsCompatible(true, noStep, noStep));
        Assert.False(PreparedPlatingCompatibilityPolicy.IsCompatible(true, noStep, sharedStep));
    }

    [Fact]
    public void PreparedAssignmentPrefersUnmetLiveDemandOverCatalogOrder()
    {
        var candidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 0, 0, false, int.MaxValue, int.MaxValue, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 0, false, 3, 0, 1)
        };

        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(candidates));
    }

    [Fact]
    public void PreparedAssignmentDistributesPhysicalDishesAcrossEquivalentLiveRecipes()
    {
        var firstSourceCandidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 1, 0, false, 1, 0, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 0, false, 2, 0, 1)
        };
        var secondSourceCandidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 1, 1, false, 1, 0, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 0, false, 2, 0, 1)
        };

        Assert.Equal(101, PreparedRecipeAssignmentPolicy.SelectCanonical(firstSourceCandidates));
        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(secondSourceCandidates));
    }

    [Fact]
    public void PreparedAssignmentDiscountsItsOwnExistingCount()
    {
        var candidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 1, 1, true, 5, 0, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 1, false, 1, 0, 1)
        };

        Assert.Equal(101, PreparedRecipeAssignmentPolicy.SelectCanonical(candidates));
    }

    [Fact]
    public void PreparedAssignmentRetainsACompatibleCurrentRecipeWhenDemandIsCovered()
    {
        var candidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 0, 1, true, int.MaxValue, int.MaxValue, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 1, false, 1, 0, 1)
        };

        Assert.Equal(101, PreparedRecipeAssignmentPolicy.SelectCanonical(candidates));
    }

    [Fact]
    public void PreparedAssignmentUsesTeamThenCatalogAsDeterministicLiveTicketTies()
    {
        var candidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 1, 0, false, 2, 1, 0),
            new PreparedRecipeAssignmentCandidate(202, 1, 0, false, 2, 0, 1)
        };

        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(candidates));
    }

    [Fact]
    public void PreparedAssignmentFallsBackToCatalogOrderWithoutLiveDemand()
    {
        var candidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 0, 0, false, int.MaxValue, int.MaxValue, 4),
            new PreparedRecipeAssignmentCandidate(202, 0, 0, false, int.MaxValue, int.MaxValue, 2)
        };

        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(candidates));
        Assert.Equal(0, PreparedRecipeAssignmentPolicy.SelectCanonical(null!));
    }

    [Fact]
    public void OneCanonicalPreparedDishCoversEveryCompatibleGuess()
    {
        var compatibleCandidates = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 0, 0, false, int.MaxValue, int.MaxValue, 0),
            new PreparedRecipeAssignmentCandidate(202, 0, 0, false, int.MaxValue, int.MaxValue, 1)
        };

        Assert.Equal(101, PreparedRecipeAssignmentPolicy.SelectCanonical(compatibleCandidates));
        Assert.False(PreparedGuessEligibilityPolicy.IsEligible(0, true, 0.6d, true, 1));
        Assert.False(PreparedGuessEligibilityPolicy.IsEligible(0, true, 0.4d, true, 1));
        Assert.True(PreparedGuessEligibilityPolicy.IsEligible(0, true, 0.6d, false, 1));
        Assert.True(PreparedGuessEligibilityPolicy.IsEligible(0, true, 0.4d, true, 0));
    }

    [Theory]
    [InlineData(1, true, 0.5d, true, 0, false)]
    [InlineData(0, false, 0.5d, true, 0, false)]
    [InlineData(0, true, 0d, true, 0, false)]
    [InlineData(0, true, 0.5d, true, 1, false)]
    [InlineData(0, true, 0.5d, false, 1, true)]
    [InlineData(0, true, 0.5d, true, 0, true)]
    public void GuessEligibilityRequiresOffMenuPositiveProbabilityWithoutActiveCoverage(
        int onMenuCount,
        bool probabilityAvailable,
        double probability,
        bool preparedTrackingEnabled,
        int preparedCoverageCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            PreparedGuessEligibilityPolicy.IsEligible(
                onMenuCount,
                probabilityAvailable,
                probability,
                preparedTrackingEnabled,
                preparedCoverageCount));
    }

    [Fact]
    public void CanonicalPreparedAssignmentIsStableWhenProviderOrderChanges()
    {
        var forward = new[]
        {
            new PreparedRecipeAssignmentCandidate(101, 0, 0, false, int.MaxValue, int.MaxValue, 3),
            new PreparedRecipeAssignmentCandidate(202, 0, 0, false, int.MaxValue, int.MaxValue, 1)
        };
        var reverse = new[]
        {
            forward[1],
            forward[0]
        };

        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(forward));
        Assert.Equal(202, PreparedRecipeAssignmentPolicy.SelectCanonical(reverse));
    }

    [Theory]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, false, false, false)]
    public void FloatingOverlayRequiresExplicitVisibilityAndAnEligibleTrackedRound(
        bool trackingEnabled,
        bool overlayEnabled,
        bool noMenuActive,
        bool inActiveRound,
        bool expected)
    {
        Assert.Equal(
            expected,
            OverlayVisibilityPolicy.IsRuntimeEligible(
                trackingEnabled,
                overlayEnabled,
                noMenuActive,
                inActiveRound));
    }

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

    [Theory]
    [InlineData(false, false, false, false, false, (int)ManyRecipesSnapshotState.Absent)]
    [InlineData(true, true, false, false, false, (int)ManyRecipesSnapshotState.Disabled)]
    [InlineData(true, true, true, true, true, (int)ManyRecipesSnapshotState.Ready)]
    [InlineData(true, false, false, false, false, (int)ManyRecipesSnapshotState.ActiveUnavailable)]
    [InlineData(true, true, true, false, true, (int)ManyRecipesSnapshotState.ActiveUnavailable)]
    [InlineData(true, true, true, true, false, (int)ManyRecipesSnapshotState.ActiveUnavailable)]
    public void ManyRecipesAdapterStateTransitionsFailClosed(
        bool providerPresent,
        bool contractValid,
        bool enabled,
        bool patchListAvailable,
        bool entriesValid,
        int expected)
    {
        Assert.Equal(
            (ManyRecipesSnapshotState)expected,
            ManyRecipesSnapshotPolicy.Classify(
                providerPresent,
                contractValid,
                enabled,
                patchListAvailable,
                entriesValid));
    }

    [Fact]
    public void FailedManyRecipesSnapshotsRemainRetryable()
    {
        Assert.True(ManyRecipesSnapshotPolicy.ShouldCache(ManyRecipesSnapshotState.Absent));
        Assert.True(ManyRecipesSnapshotPolicy.ShouldCache(ManyRecipesSnapshotState.Disabled));
        Assert.True(ManyRecipesSnapshotPolicy.ShouldCache(ManyRecipesSnapshotState.Ready));
        Assert.False(ManyRecipesSnapshotPolicy.ShouldCache(ManyRecipesSnapshotState.ActiveUnavailable));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(21, true)]
    public void EnabledManyRecipesRequiresAnInitializedProviderRegistry(int providerCount, bool expected)
    {
        Assert.Equal(expected, ManyRecipesSnapshotPolicy.IsProviderRegistryAvailable(providerCount));
    }

    [Theory]
    [InlineData((int)ManyRecipesSnapshotState.Absent, 9, 0, 9, true)]
    [InlineData((int)ManyRecipesSnapshotState.Disabled, 9, 0, 9, true)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 0, 9, true)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 3, 12, true)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 3, 11, false)]
    [InlineData((int)ManyRecipesSnapshotState.Absent, 9, 3, 12, false)]
    [InlineData((int)ManyRecipesSnapshotState.ActiveUnavailable, 9, 0, 9, false)]
    [InlineData(99, 9, 0, 9, false)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, -1, 3, 2, false)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, -1, 8, false)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 3, -1, false)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, int.MaxValue, 1, int.MaxValue, false)]
    public void ManyRecipesRuntimeCandidatesRequireTheStateAppropriateExactShape(
        int state,
        int baseCount,
        int extensionCount,
        int frequencyCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            ManyRecipesSnapshotPolicy.HasExactRuntimeShape(
                (ManyRecipesSnapshotState)state,
                baseCount,
                extensionCount,
                frequencyCount));
    }

    [Fact]
    public void ManyRecipesSnapshotIdentityPreservesOrderAndDuplicateEntries()
    {
        var orderedDuplicates = new[] { 8881400, 8881400, 8881401 };

        Assert.True(ManyRecipesSnapshotPolicy.OrderedRecipeIdsMatch(
            orderedDuplicates,
            new[] { 8881400, 8881400, 8881401 }));
        Assert.False(ManyRecipesSnapshotPolicy.OrderedRecipeIdsMatch(
            orderedDuplicates,
            new[] { 8881400, 8881401, 8881400 }));
        Assert.Equal(2, new HashSet<int>(orderedDuplicates).Count);
    }

    [Theory]
    [InlineData((int)ManyRecipesSnapshotState.ActiveUnavailable, 9, 0, 9, true)]
    [InlineData(99, 9, 0, 9, true)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 3, 11, true)]
    [InlineData((int)ManyRecipesSnapshotState.Ready, 9, 3, 12, false)]
    [InlineData((int)ManyRecipesSnapshotState.Disabled, 9, 0, 8, false)]
    public void NoMenuFailsClosedOnlyForAnUnsafeActiveManyRecipesSnapshot(
        int state,
        int baseCount,
        int extensionCount,
        int frequencyCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            ManyRecipesSnapshotPolicy.MustDisableNoMenu(
                (ManyRecipesSnapshotState)state,
                baseCount,
                extensionCount,
                frequencyCount));
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
    public void EntryProbabilitiesPreserveDuplicateRecipeEntriesBeforeAggregation()
    {
        var recipeIds = new[] { 10, 10, 20 };
        var frequencies = new[] { 1, 0, 0 };
        var entryProbabilities = new double[recipeIds.Length];
        var byRecipe = new Dictionary<int, double>();

        Assert.True(ProbabilityPolicy.TryCalculateEntryProbabilities(recipeIds, frequencies, entryProbabilities));
        Assert.True(ProbabilityPolicy.TryAggregateByRecipe(recipeIds, entryProbabilities, byRecipe));

        Assert.Equal(0d, entryProbabilities[0]);
        Assert.Equal(0.5d, entryProbabilities[1], 12);
        Assert.Equal(0.5d, entryProbabilities[2], 12);
        Assert.Equal(0.5d, byRecipe[10], 12);
        Assert.Equal(0.5d, byRecipe[20], 12);
    }

    [Fact]
    public void ScriptedManualOrdersAreDeterministicThenFallBackToEntryBalancing()
    {
        var manualRecipeIds = new[] { 7, 8 };

        Assert.True(ProbabilityPolicy.TryGetScriptedManualRecipe(0, manualRecipeIds, out var first));
        Assert.Equal(7, first);
        Assert.True(ProbabilityPolicy.TryGetScriptedManualRecipe(1, manualRecipeIds, out var second));
        Assert.Equal(8, second);
        Assert.False(ProbabilityPolicy.TryGetScriptedManualRecipe(2, manualRecipeIds, out _));

        var probabilities = new double[2];
        Assert.True(ProbabilityPolicy.TryCalculateEntryProbabilities(
            new[] { 20, 30 },
            new[] { 0, 0 },
            probabilities));
        Assert.Equal(0.5d, probabilities[0], 12);
        Assert.Equal(0.5d, probabilities[1], 12);
    }

    [Fact]
    public void DynamicPhaseIndicesAreNormalizedAndResetOnlyWhenThePhaseChanges()
    {
        Assert.Equal(0, DynamicPhasePolicy.NormalizePhaseIndex(-1));
        Assert.Equal(0, DynamicPhasePolicy.NormalizePhaseIndex(0));
        Assert.Equal(3, DynamicPhasePolicy.NormalizePhaseIndex(3));
        Assert.False(DynamicPhasePolicy.ShouldReset(2, 2));
        Assert.True(DynamicPhasePolicy.ShouldReset(1, 2));
        Assert.True(DynamicPhasePolicy.ShouldReset(2, 0));
    }

    [Fact]
    public void FixedTasSequenceUsesCumulativePositionAndFallsBackWhenExhausted()
    {
        var recipeIds = new[] { 100, 200, 300 };
        var sequence = new[] { 2, 0, 1 };

        Assert.True(ProbabilityPolicy.TryGetSequenceRecipe(
            recipeIds,
            new[] { 1, 1, 0 },
            sequence,
            out var selected));
        Assert.Equal(200, selected);

        Assert.False(ProbabilityPolicy.TryGetSequenceRecipe(
            recipeIds,
            new[] { 1, 1, 1 },
            sequence,
            out _));

        var fallback = new double[recipeIds.Length];
        Assert.True(ProbabilityPolicy.TryCalculateEntryProbabilities(recipeIds, new[] { 1, 1, 1 }, fallback));
        Assert.All(fallback, probability => Assert.True(ProbabilityPolicy.IsFinite(probability)));
    }

    [Fact]
    public void RemoteProbabilityReconstructionCollectsOnlyDistinctRecipeIds()
    {
        var buffer = new HashSet<int>();

        Assert.True(ProbabilityPolicy.TryCollectDistinctRecipeIds(new[] { 1, 2, 3 }, buffer));
        Assert.Equal(3, buffer.Count);
        Assert.False(ProbabilityPolicy.TryCollectDistinctRecipeIds(new[] { 1, 2, 1 }, buffer));
        Assert.Empty(buffer);
        Assert.False(ProbabilityPolicy.TryCollectDistinctRecipeIds(Array.Empty<int>(), buffer));
        Assert.False(ProbabilityPolicy.TryCollectDistinctRecipeIds(new[] { 1 }, null!));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void RemoteRandomProbabilityFailsClosedWhenEntryStateIsAmbiguous(
        bool historyComplete,
        bool hasExtensionEntries,
        bool baseRecipeIdsDistinct,
        bool expected)
    {
        Assert.Equal(
            expected,
            ProbabilityReconstructionPolicy.CanUseRandomBaseEntries(
                historyComplete,
                hasExtensionEntries,
                baseRecipeIdsDistinct));
    }

    [Fact]
    public void OnlySuccessfulDeliveryChangesServedAndOnMenuCounts()
    {
        var served = 0;
        var onMenu = 1;
        foreach (var lifecycleEvent in new[]
                 {
                     OrderLifecycleEvent.Expired,
                     OrderLifecycleEvent.FailedDelivery,
                     OrderLifecycleEvent.Expired
                 })
        {
            var effect = OrderLifecyclePolicy.GetEffect(lifecycleEvent);
            if (effect.IncrementServed)
            {
                served++;
            }

            if (effect.DecrementOnMenu)
            {
                onMenu--;
            }
        }

        Assert.Equal(0, served);
        Assert.Equal(1, onMenu);

        var success = OrderLifecyclePolicy.GetEffect(OrderLifecycleEvent.SuccessfulDelivery);
        Assert.True(success.IncrementServed);
        Assert.True(success.DecrementOnMenu);
        if (success.IncrementServed)
        {
            served++;
        }

        if (success.DecrementOnMenu)
        {
            onMenu--;
        }

        Assert.Equal(1, served);
        Assert.Equal(0, onMenu);
    }

    [Fact]
    public void TeamScopedOrderKeysAllowIdenticalNumericIds()
    {
        var teamOne = new TeamScopedOrderKey(0, 42u);
        var teamTwo = new TeamScopedOrderKey(1, 42u);
        var keys = new HashSet<TeamScopedOrderKey> { teamOne, teamTwo };

        Assert.NotEqual(teamOne, teamTwo);
        Assert.Equal(2, keys.Count);
        Assert.Contains(new TeamScopedOrderKey(0, 42u), keys);
        Assert.Contains(new TeamScopedOrderKey(1, 42u), keys);
    }

    [Fact]
    public void VersusHistoriesAndProbabilitiesRemainIndependentPerTeam()
    {
        var activeOrders = new Dictionary<TeamScopedOrderKey, int>
        {
            [new TeamScopedOrderKey(0, 1u)] = 10,
            [new TeamScopedOrderKey(1, 1u)] = 20
        };
        var servedByTeam = new Dictionary<int, Dictionary<int, int>>
        {
            [0] = new Dictionary<int, int>(),
            [1] = new Dictionary<int, int>()
        };

        var successfulKey = new TeamScopedOrderKey(0, 1u);
        var successfulRecipe = activeOrders[successfulKey];
        var effect = OrderLifecyclePolicy.GetEffect(OrderLifecycleEvent.SuccessfulDelivery);
        if (effect.IncrementServed)
        {
            servedByTeam[0][successfulRecipe] = 1;
        }

        if (effect.DecrementOnMenu)
        {
            activeOrders.Remove(successfulKey);
        }

        Assert.False(activeOrders.ContainsKey(successfulKey));
        Assert.True(activeOrders.ContainsKey(new TeamScopedOrderKey(1, 1u)));
        Assert.Equal(1, servedByTeam[0][10]);
        Assert.Empty(servedByTeam[1]);

        var teamOneProbabilities = new double[2];
        var teamTwoProbabilities = new double[2];
        Assert.True(ProbabilityPolicy.TryCalculateEntryProbabilities(
            new[] { 10, 20 }, new[] { 1, 0 }, teamOneProbabilities));
        Assert.True(ProbabilityPolicy.TryCalculateEntryProbabilities(
            new[] { 10, 20 }, new[] { 0, 1 }, teamTwoProbabilities));
        Assert.NotEqual(teamOneProbabilities[0], teamTwoProbabilities[0]);
        Assert.NotEqual(teamOneProbabilities[1], teamTwoProbabilities[1]);
    }

    [Theory]
    [InlineData(false, false, false, (int)SyntheticTransactionOutcome.NotInjected)]
    [InlineData(true, false, false, (int)SyntheticTransactionOutcome.Success)]
    [InlineData(true, true, false, (int)SyntheticTransactionOutcome.CompensateAndDisable)]
    [InlineData(true, false, true, (int)SyntheticTransactionOutcome.CompensateAndDisable)]
    public void SyntheticTransactionsVerifyOriginalRemovalAndCompensateFailures(
        bool injected,
        bool stillActive,
        bool originalThrew,
        int expected)
    {
        Assert.Equal(
            (SyntheticTransactionOutcome)expected,
            SyntheticTransactionPolicy.Evaluate(injected, stillActive, originalThrew));
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
        const int existingRecipeId = 42;
        const int newlyGeneratedRecipeId = 8881401;
        var selected = new HashSet<int> { existingRecipeId };
        Assert.True(TrackingSelectionPolicy.IsTracked(false, null!, newlyGeneratedRecipeId));
        Assert.True(TrackingSelectionPolicy.IsTracked(true, selected, existingRecipeId));
        Assert.False(TrackingSelectionPolicy.IsTracked(true, selected, newlyGeneratedRecipeId));
        Assert.False(TrackingSelectionPolicy.IsTracked(true, null!, existingRecipeId));
    }

    [Fact]
    public void GuessCandidatesRequireCurrentTrackedSelection()
    {
        const int trackedRecipeId = 42;
        const int untrackedRecipeId = 8881401;
        var selected = new HashSet<int> { trackedRecipeId };

        Assert.True(TrackingSelectionPolicy.IsGuessCandidate(
            true,
            selected,
            trackedRecipeId,
            true));
        Assert.False(TrackingSelectionPolicy.IsGuessCandidate(
            true,
            selected,
            untrackedRecipeId,
            true));
        Assert.True(TrackingSelectionPolicy.IsGuessCandidate(
            false,
            null!,
            untrackedRecipeId,
            true));
        Assert.False(TrackingSelectionPolicy.IsGuessCandidate(
            true,
            selected,
            trackedRecipeId,
            false));
    }

    [Fact]
    public void ExplicitSelectionOnlyCollapsesWhenItCoversEveryCurrentRecipe()
    {
        var currentRecipeIds = new[] { 101, 202, 303 };
        var countMatchedByStaleId = new HashSet<int> { 101, 202, 999 };
        var completeWithStaleId = new HashSet<int> { 101, 202, 303, 999 };

        Assert.False(TrackingSelectionPolicy.CoversEveryAvailableRecipe(
            countMatchedByStaleId,
            currentRecipeIds));
        Assert.True(TrackingSelectionPolicy.CoversEveryAvailableRecipe(
            completeWithStaleId,
            currentRecipeIds));
    }

    [Fact]
    public void StaleOnlySelectionHasNoCurrentTrackedRecipes()
    {
        var currentRecipeIds = new[] { 101, 202 };

        Assert.False(TrackingSelectionPolicy.HasAnyAvailableRecipe(
            new HashSet<int> { 998, 999 },
            currentRecipeIds));
        Assert.True(TrackingSelectionPolicy.HasAnyAvailableRecipe(
            new HashSet<int> { 202, 999 },
            currentRecipeIds));
    }

    [Theory]
    [InlineData(false, true, false, false, false, false, false, true, (int)NoMenuIneligibility.Disabled)]
    [InlineData(true, false, false, false, false, false, false, true, (int)NoMenuIneligibility.UnsupportedLevel)]
    [InlineData(true, true, true, false, false, false, false, true, (int)NoMenuIneligibility.Boss)]
    [InlineData(true, true, false, true, false, false, false, true, (int)NoMenuIneligibility.Tutorial)]
    [InlineData(true, true, false, false, true, false, false, true, (int)NoMenuIneligibility.Survival)]
    [InlineData(true, true, false, false, false, true, false, true, (int)NoMenuIneligibility.PreTimerOrders)]
    [InlineData(true, true, false, false, false, false, true, true, (int)NoMenuIneligibility.OnlineSession)]
    [InlineData(true, true, false, false, false, false, false, false, (int)NoMenuIneligibility.MissingRuntimeContract)]
    [InlineData(true, true, false, false, false, false, false, true, (int)NoMenuIneligibility.None)]
    public void NoMenuEligibilityFailsClosed(
        bool requested,
        bool kitchen,
        bool boss,
        bool tutorial,
        bool survival,
        bool preTimer,
        bool inOnlineSession,
        bool contract,
        int expected)
    {
        Assert.Equal(
            (NoMenuIneligibility)expected,
            NoMenuRoundPolicy.Evaluate(requested, kitchen, boss, tutorial, survival, preTimer, inOnlineSession, contract));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NoMenuClientInitializationDefersToAnAuthoritativeServerFlow(bool hasServerFlow, bool expected)
    {
        Assert.Equal(expected, NoMenuClientAuthorityPolicy.ShouldInitializeLocalRoundState(hasServerFlow));
    }

    [Fact]
    public void NoMenuAllowsLocalStandardDynamicAndVersusButBlocksPrivateAndPublicOnline()
    {
        for (var localCase = 0; localCase < 3; localCase++)
        {
            Assert.Equal(
                NoMenuIneligibility.None,
                NoMenuRoundPolicy.Evaluate(true, true, false, false, false, false, false, true));
        }

        for (var onlineVisibility = 0; onlineVisibility < 2; onlineVisibility++)
        {
            Assert.Equal(
                NoMenuIneligibility.OnlineSession,
                NoMenuRoundPolicy.Evaluate(true, true, false, false, false, false, true, true));
        }
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
