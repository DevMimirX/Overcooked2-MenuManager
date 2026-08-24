// Contains side-effect-free runtime policies shared by the game-facing
// features and unit tests. Collection-taking policies clear and populate
// caller-owned buffers so legacy .NET 3.5 gameplay paths can remain allocation-free.
using System;
using System.Collections.Generic;

namespace OC2MenuManager.Infrastructure
{
    internal enum RecipeCatalogMergeAction
    {
        None,
        Add,
        Replace
    }

    internal static class RecipeCatalogMergePolicy
    {
        internal static RecipeCatalogMergeAction Evaluate(
            bool hasExistingEntry,
            bool existingHasDefinition,
            bool incomingHasDefinition,
            bool definitionChanged,
            bool nameChanged)
        {
            if (!hasExistingEntry)
            {
                return RecipeCatalogMergeAction.Add;
            }

            if (existingHasDefinition && !incomingHasDefinition)
            {
                return RecipeCatalogMergeAction.None;
            }

            if (incomingHasDefinition)
            {
                return !existingHasDefinition || definitionChanged || nameChanged
                    ? RecipeCatalogMergeAction.Replace
                    : RecipeCatalogMergeAction.None;
            }

            return !existingHasDefinition && nameChanged
                ? RecipeCatalogMergeAction.Replace
                : RecipeCatalogMergeAction.None;
        }
    }

    internal static class TrackingSelectionPolicy
    {
        internal static bool IsTracked(bool hasExplicitSceneEntry, System.Collections.Generic.ICollection<int> selectedRecipeIds, int recipeId)
        {
            return !hasExplicitSceneEntry || (selectedRecipeIds != null && selectedRecipeIds.Contains(recipeId));
        }
    }

    /// <summary>
    /// Determines whether the optional tracker overlay may be presented. This
    /// policy intentionally excludes collection, ticket, probability, and guess
    /// state so hiding the panel never disables gameplay-facing tracking.
    /// </summary>
    internal static class OverlayVisibilityPolicy
    {
        internal static bool IsRuntimeEligible(
            bool trackingEnabled,
            bool overlayEnabled,
            bool noMenuActive,
            bool inActiveRound)
        {
            return trackingEnabled && overlayEnabled && !noMenuActive && inActiveRound;
        }
    }

    /// <summary>
    /// Validates and calculates next-recipe probability data without depending on
    /// Unity or mutable game controllers.
    /// </summary>
    internal static class ProbabilityPolicy
    {
        internal static double CalculateRawWeight(int totalAdded, int recipeCount, int recipeAddedCount)
        {
            if (recipeCount <= 0)
            {
                return 0d;
            }

            double weight = ((double)Math.Max(0, totalAdded) + 2d) / recipeCount
                - Math.Max(0, recipeAddedCount);
            return IsFinite(weight) && weight > 0d ? weight : 0d;
        }

        internal static double Normalize(double weight, double totalWeight)
        {
            if (!IsFinite(weight) || !IsFinite(totalWeight) || weight <= 0d || totalWeight <= 0d)
            {
                return 0d;
            }

            double result = weight / totalWeight;
            return IsFinite(result) && result > 0d ? result : 0d;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool TryCalculateEntryProbabilities(
            int[] recipeIds,
            int[] cumulativeFrequencies,
            double[] probabilitiesByEntry)
        {
            if (recipeIds == null
                || cumulativeFrequencies == null
                || probabilitiesByEntry == null
                || recipeIds.Length == 0
                || cumulativeFrequencies.Length != recipeIds.Length
                || probabilitiesByEntry.Length != recipeIds.Length)
            {
                return false;
            }

            long totalAdded = 0L;
            for (int i = 0; i < cumulativeFrequencies.Length; i++)
            {
                int frequency = cumulativeFrequencies[i];
                if (frequency < 0)
                {
                    return false;
                }

                totalAdded += frequency;
                if (totalAdded > int.MaxValue - 2L)
                {
                    return false;
                }
            }

            double totalWeight = 0d;
            for (int i = 0; i < recipeIds.Length; i++)
            {
                float theoreticalWeight = (float)(totalAdded + 2L) / recipeIds.Length;
                float weight = Math.Max(theoreticalWeight - cumulativeFrequencies[i], 0f);
                probabilitiesByEntry[i] = weight;
                totalWeight += weight;
            }

            if (!IsFinite(totalWeight) || totalWeight <= 0d)
            {
                Array.Clear(probabilitiesByEntry, 0, probabilitiesByEntry.Length);
                return false;
            }

            for (int i = 0; i < probabilitiesByEntry.Length; i++)
            {
                probabilitiesByEntry[i] = Normalize(probabilitiesByEntry[i], totalWeight);
            }

            return true;
        }

        internal static bool TryNormalizeEntryWeights(double[] rawWeights, double[] probabilitiesByEntry)
        {
            if (rawWeights == null
                || probabilitiesByEntry == null
                || rawWeights.Length == 0
                || probabilitiesByEntry.Length != rawWeights.Length)
            {
                return false;
            }

            double totalWeight = 0d;
            for (int i = 0; i < rawWeights.Length; i++)
            {
                double weight = rawWeights[i];
                if (!IsFinite(weight) || weight < 0d)
                {
                    return false;
                }

                totalWeight += weight;
            }

            if (!IsFinite(totalWeight) || totalWeight <= 0d)
            {
                Array.Clear(probabilitiesByEntry, 0, probabilitiesByEntry.Length);
                return false;
            }

            for (int i = 0; i < rawWeights.Length; i++)
            {
                probabilitiesByEntry[i] = Normalize(rawWeights[i], totalWeight);
            }

            return true;
        }

        internal static bool TryAggregateByRecipe(
            int[] recipeIds,
            double[] probabilitiesByEntry,
            IDictionary<int, double> probabilitiesByRecipe)
        {
            if (recipeIds == null
                || probabilitiesByEntry == null
                || probabilitiesByRecipe == null
                || recipeIds.Length == 0
                || probabilitiesByEntry.Length != recipeIds.Length)
            {
                return false;
            }

            probabilitiesByRecipe.Clear();
            for (int i = 0; i < recipeIds.Length; i++)
            {
                double probability = probabilitiesByEntry[i];
                if (!IsFinite(probability) || probability < 0d)
                {
                    probabilitiesByRecipe.Clear();
                    return false;
                }

                double existing;
                probabilitiesByRecipe.TryGetValue(recipeIds[i], out existing);
                double combined = existing + probability;
                if (!IsFinite(combined))
                {
                    probabilitiesByRecipe.Clear();
                    return false;
                }

                probabilitiesByRecipe[recipeIds[i]] = combined;
            }

            return true;
        }

        internal static bool TryGetScriptedManualRecipe(int recipeCount, int[] manualRecipeIds, out int recipeId)
        {
            recipeId = 0;
            if (manualRecipeIds == null || recipeCount < 0 || recipeCount >= manualRecipeIds.Length)
            {
                return false;
            }

            recipeId = manualRecipeIds[recipeCount];
            return true;
        }

        internal static bool TryGetSequenceRecipe(
            int[] recipeIds,
            int[] cumulativeFrequencies,
            int[] recipeIndexSequence,
            out int recipeId)
        {
            recipeId = 0;
            if (recipeIds == null
                || cumulativeFrequencies == null
                || recipeIndexSequence == null
                || recipeIds.Length == 0
                || cumulativeFrequencies.Length != recipeIds.Length)
            {
                return false;
            }

            long sequencePosition = 0L;
            for (int i = 0; i < cumulativeFrequencies.Length; i++)
            {
                if (cumulativeFrequencies[i] < 0)
                {
                    return false;
                }

                sequencePosition += cumulativeFrequencies[i];
            }

            if (sequencePosition < 0L || sequencePosition >= recipeIndexSequence.Length)
            {
                return false;
            }

            int recipeIndex = recipeIndexSequence[(int)sequencePosition];
            if (recipeIndex < 0 || recipeIndex >= recipeIds.Length)
            {
                return false;
            }

            recipeId = recipeIds[recipeIndex];
            return true;
        }

        /// <summary>
        /// Validates that recipe IDs are non-empty and unique while populating a
        /// caller-owned set. The destination is empty whenever validation fails.
        /// </summary>
        internal static bool TryCollectDistinctRecipeIds(int[] recipeIds, HashSet<int> distinctRecipeIds)
        {
            if (distinctRecipeIds == null)
            {
                return false;
            }

            distinctRecipeIds.Clear();
            if (recipeIds == null || recipeIds.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < recipeIds.Length; i++)
            {
                if (!distinctRecipeIds.Add(recipeIds[i]))
                {
                    distinctRecipeIds.Clear();
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Remote clients can reconstruct the base game's random-entry state only
    /// when every contributing entry is known and has a distinct recipe ID.
    /// Extension entries carry their own cumulative frequencies, so even an
    /// extension entry that reuses a base recipe ID is ambiguous.
    /// </summary>
    internal static class ProbabilityReconstructionPolicy
    {
        internal static bool CanUseRandomBaseEntries(
            bool historyComplete,
            bool hasExtensionEntries,
            bool baseRecipeIdsDistinct)
        {
            return historyComplete && !hasExtensionEntries && baseRecipeIdsDistinct;
        }
    }

    internal enum OrderLifecycleEvent
    {
        Added,
        SuccessfulDelivery,
        FailedDelivery,
        Expired
    }

    internal struct OrderLifecycleEffect
    {
        internal bool IncrementServed;
        internal bool DecrementOnMenu;
    }

    internal static class OrderLifecyclePolicy
    {
        internal static OrderLifecycleEffect GetEffect(OrderLifecycleEvent lifecycleEvent)
        {
            bool successful = lifecycleEvent == OrderLifecycleEvent.SuccessfulDelivery;
            return new OrderLifecycleEffect
            {
                IncrementServed = successful,
                DecrementOnMenu = successful
            };
        }
    }

    internal struct TeamScopedOrderKey : IEquatable<TeamScopedOrderKey>
    {
        internal TeamScopedOrderKey(int teamId, uint orderId)
        {
            TeamId = teamId;
            OrderId = orderId;
        }

        internal int TeamId { get; private set; }
        internal uint OrderId { get; private set; }

        public bool Equals(TeamScopedOrderKey other)
        {
            return TeamId == other.TeamId && OrderId == other.OrderId;
        }

#pragma warning disable CS8765
        public override bool Equals(object obj)
        {
            return obj is TeamScopedOrderKey && Equals((TeamScopedOrderKey)obj);
        }
#pragma warning restore CS8765

        public override int GetHashCode()
        {
            unchecked
            {
                return (TeamId * 397) ^ (int)OrderId;
            }
        }
    }

    internal static class DynamicPhasePolicy
    {
        internal static bool ShouldReset(int previousPhaseIndex, int nextPhaseIndex)
        {
            return previousPhaseIndex != nextPhaseIndex;
        }
    }

    internal enum SyntheticTransactionOutcome
    {
        NotInjected,
        Success,
        CompensateAndDisable
    }

    internal static class SyntheticTransactionPolicy
    {
        internal static SyntheticTransactionOutcome Evaluate(
            bool injected,
            bool orderStillActive,
            bool originalThrew)
        {
            if (!injected)
            {
                return SyntheticTransactionOutcome.NotInjected;
            }

            return !orderStillActive && !originalThrew
                ? SyntheticTransactionOutcome.Success
                : SyntheticTransactionOutcome.CompensateAndDisable;
        }
    }

    internal static class TicketCapacityPolicy
    {
        internal static int CalculateAllowedReferenceTickets(int activeRealTickets, int configuredReferenceTickets, int maxCombinedTickets)
        {
            int safeRealCount = Math.Max(0, activeRealTickets);
            int safeReferenceCount = Math.Max(0, configuredReferenceTickets);
            int safeCombinedLimit = Math.Max(0, maxCombinedTickets);
            if (safeRealCount >= safeCombinedLimit)
            {
                return 0;
            }

            return Math.Min(safeReferenceCount, safeCombinedLimit - safeRealCount);
        }

        internal static int CalculateEffectiveRealLimit(int baseLimit, int rawConfiguredLimit, int reportedLimit, int observedRealTickets)
        {
            return Math.Max(
                Math.Max(0, baseLimit),
                Math.Max(
                    Math.Max(0, rawConfiguredLimit),
                    Math.Max(Math.Max(0, reportedLimit), Math.Max(0, observedRealTickets))));
        }

        internal static int CalculateTargetCapacity(int effectiveRealLimit, int activeRealTickets, int requestedReferenceTickets)
        {
            int safeRealLimit = Math.Max(0, effectiveRealLimit);
            int safeRealCount = Math.Max(0, activeRealTickets);
            int safeReferenceCount = Math.Max(0, requestedReferenceTickets);
            long activeTicketCapacity = (long)safeRealCount + safeReferenceCount;
            long targetCapacity = Math.Max((long)safeRealLimit, activeTicketCapacity);
            return targetCapacity >= int.MaxValue ? int.MaxValue : (int)targetCapacity;
        }

        internal static bool IsValidTableIndex(int tableIndex, int tableCount)
        {
            return tableIndex >= 0 && tableIndex < Math.Max(0, tableCount);
        }
    }

    internal static class CarnivalRecipeSelectionPolicy
    {
        internal static bool HasCompatibleCandidateShape(int baseRecipeCount, int extensionRecipeCount, int cumulativeFrequencyCount)
        {
            if (baseRecipeCount < 2 || extensionRecipeCount < 0 || cumulativeFrequencyCount < 0)
            {
                return false;
            }

            long candidateCount = (long)baseRecipeCount + extensionRecipeCount;
            return candidateCount <= int.MaxValue && candidateCount == cumulativeFrequencyCount;
        }

        internal static bool TryCalculateWeight(
            int[] cumulativeFrequencies,
            int baseRecipeCount,
            int recipeIndex,
            bool cakeRulesEnabled,
            out float weight)
        {
            weight = 0f;
            if (cumulativeFrequencies == null
                || baseRecipeCount < 2
                || baseRecipeCount > cumulativeFrequencies.Length
                || recipeIndex < 0
                || recipeIndex >= cumulativeFrequencies.Length)
            {
                return false;
            }

            long totalAdded;
            if (!TryGetTotalAdded(cumulativeFrequencies, out totalAdded))
            {
                return false;
            }

            weight = CalculateWeight(cumulativeFrequencies, baseRecipeCount, recipeIndex, cakeRulesEnabled, totalAdded);
            return true;
        }

        internal static bool TryCalculateWeights(
            int[] cumulativeFrequencies,
            int baseRecipeCount,
            bool cakeRulesEnabled,
            float[] weights)
        {
            if (cumulativeFrequencies == null
                || weights == null
                || weights.Length != cumulativeFrequencies.Length
                || baseRecipeCount < 2
                || baseRecipeCount > cumulativeFrequencies.Length)
            {
                return false;
            }

            long totalAdded;
            if (!TryGetTotalAdded(cumulativeFrequencies, out totalAdded))
            {
                return false;
            }

            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = CalculateWeight(cumulativeFrequencies, baseRecipeCount, i, cakeRulesEnabled, totalAdded);
            }

            return true;
        }

        private static float CalculateWeight(
            int[] cumulativeFrequencies,
            int baseRecipeCount,
            int recipeIndex,
            bool cakeRulesEnabled,
            long totalAdded)
        {
            int candidateCount = cumulativeFrequencies.Length;
            float theoreticalWeight = CalculateRawWeight(totalAdded, candidateCount, cumulativeFrequencies[recipeIndex]);
            float weight = theoreticalWeight;

            bool isBaseRecipe = recipeIndex < baseRecipeCount;
            if (isBaseRecipe && totalAdded == 0L && (recipeIndex <= 1 || (recipeIndex >= 5 && recipeIndex <= 7)))
            {
                weight = 0f;
            }
            else if (isBaseRecipe && totalAdded == 1L && recipeIndex <= 1)
            {
                weight = 0f;
            }

            if (!cakeRulesEnabled)
            {
                return weight;
            }

            if (isBaseRecipe && recipeIndex <= 1)
            {
                weight *= 3f;
            }

            float berryWeight = CalculateRawWeight(totalAdded, candidateCount, cumulativeFrequencies[0]);
            float chocolateWeight = CalculateRawWeight(totalAdded, candidateCount, cumulativeFrequencies[1]);
            if (totalAdded == 46L && berryWeight > 0f && chocolateWeight > 0f)
            {
                weight = isBaseRecipe && recipeIndex <= 1 ? 1f : 0f;
            }
            else if (totalAdded == 49L)
            {
                if (berryWeight > 0f && chocolateWeight > 0f)
                {
                    weight = isBaseRecipe && recipeIndex <= 1 ? 1f : 0f;
                }
                else if (berryWeight > 0f)
                {
                    weight = isBaseRecipe && recipeIndex == 0 ? 1f : 0f;
                }
                else if (chocolateWeight > 0f)
                {
                    weight = isBaseRecipe && recipeIndex == 1 ? 1f : 0f;
                }
                else
                {
                    weight = theoreticalWeight;
                }
            }
            else if (totalAdded == 54L && berryWeight > 0f)
            {
                weight = isBaseRecipe && recipeIndex == 0 ? 1f : 0f;
            }
            else if (totalAdded == 55L && chocolateWeight > 0f)
            {
                weight = isBaseRecipe && recipeIndex == 1 ? 1f : 0f;
            }

            return weight;
        }

        private static bool TryGetTotalAdded(int[] cumulativeFrequencies, out long totalAdded)
        {
            totalAdded = 0L;
            for (int i = 0; i < cumulativeFrequencies.Length; i++)
            {
                if (cumulativeFrequencies[i] < 0)
                {
                    return false;
                }

                totalAdded += cumulativeFrequencies[i];
                if (totalAdded > int.MaxValue - 2L)
                {
                    return false;
                }
            }

            return true;
        }

        private static float CalculateRawWeight(long totalAdded, int candidateCount, int recipeAddedCount)
        {
            if (candidateCount <= 0)
            {
                return 0f;
            }

            float result = (float)(totalAdded + 2L) / candidateCount - recipeAddedCount;
            return result > 0f ? result : 0f;
        }
    }

    /// <summary>Describes whether Recipe Extension supplied an authoritative round snapshot.</summary>
    internal enum ManyRecipesSnapshotState
    {
        Absent,
        Disabled,
        Ready,
        ActiveUnavailable
    }

    /// <summary>
    /// Owns cache, identity, runtime-shape, and No Menu safety decisions for the
    /// reflection-only Recipe Extension snapshot shared by runtime consumers.
    /// </summary>
    internal static class ManyRecipesSnapshotPolicy
    {
        internal static bool IsProviderRegistryAvailable(int providerCount)
        {
            // The audited v1.1 plugin registers its providers during Awake, before
            // dependent plugins are started. An enabled plugin with no providers is
            // therefore an incomplete initialization, not an authoritative empty
            // recipe snapshot.
            return providerCount > 0;
        }

        internal static ManyRecipesSnapshotState Classify(
            bool providerPresent,
            bool contractValid,
            bool enabled,
            bool patchListAvailable,
            bool entriesValid)
        {
            if (!providerPresent)
            {
                return ManyRecipesSnapshotState.Absent;
            }

            if (!contractValid)
            {
                return ManyRecipesSnapshotState.ActiveUnavailable;
            }

            if (!enabled)
            {
                return ManyRecipesSnapshotState.Disabled;
            }

            return patchListAvailable && entriesValid
                ? ManyRecipesSnapshotState.Ready
                : ManyRecipesSnapshotState.ActiveUnavailable;
        }

        internal static bool ShouldCache(ManyRecipesSnapshotState state)
        {
            return state != ManyRecipesSnapshotState.ActiveUnavailable;
        }

        internal static bool HasGeneratedEntries(ManyRecipesSnapshotState state, int extensionCandidateCount)
        {
            return state == ManyRecipesSnapshotState.Ready && extensionCandidateCount > 0;
        }

        internal static bool OrderedRecipeIdsMatch(IList<int> left, IList<int> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool HasExactRuntimeShape(
            ManyRecipesSnapshotState state,
            int baseCandidateCount,
            int extensionCandidateCount,
            int cumulativeFrequencyCount)
        {
            if (state == ManyRecipesSnapshotState.ActiveUnavailable
                || baseCandidateCount < 0
                || extensionCandidateCount < 0
                || cumulativeFrequencyCount < 0
                || baseCandidateCount > int.MaxValue - extensionCandidateCount)
            {
                return false;
            }

            if (state == ManyRecipesSnapshotState.Absent || state == ManyRecipesSnapshotState.Disabled)
            {
                return extensionCandidateCount == 0 && baseCandidateCount == cumulativeFrequencyCount;
            }

            return state == ManyRecipesSnapshotState.Ready
                && baseCandidateCount + extensionCandidateCount == cumulativeFrequencyCount;
        }

        internal static bool MustDisableNoMenu(
            ManyRecipesSnapshotState state,
            int baseCandidateCount,
            int extensionCandidateCount,
            int cumulativeFrequencyCount)
        {
            if (state == ManyRecipesSnapshotState.Absent || state == ManyRecipesSnapshotState.Disabled)
            {
                return false;
            }

            return state != ManyRecipesSnapshotState.Ready
                || !HasExactRuntimeShape(
                    state,
                    baseCandidateCount,
                    extensionCandidateCount,
                    cumulativeFrequencyCount);
        }
    }

    internal static class RecipeExtensionPhasePolicy
    {
        internal static void GetEntryWindow(string levelConfigName, int phaseIndex, bool allPhases, int entryCount, out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex = Math.Max(0, entryCount);
            if (allPhases || string.IsNullOrEmpty(levelConfigName))
            {
                return;
            }

            if (levelConfigName.StartsWith("5_6_Dynamic_Lvl_03", StringComparison.Ordinal) && phaseIndex != 2)
            {
                endIndex = Math.Max(startIndex, endIndex - 4);
                return;
            }

            if (!levelConfigName.StartsWith("1_6_Dynamic_Lvl_01", StringComparison.Ordinal))
            {
                return;
            }

            if (phaseIndex <= 1)
            {
                startIndex = Math.Min(5, endIndex);
            }
            else
            {
                endIndex = Math.Max(startIndex, endIndex - 3);
            }
        }
    }

    internal enum NoMenuIneligibility
    {
        None,
        Disabled,
        UnsupportedLevel,
        Boss,
        Tutorial,
        Survival,
        PreTimerOrders,
        OnlineSession,
        MissingRuntimeContract,
        BootstrapOrders
    }

    internal static class NoMenuClientAuthorityPolicy
    {
        internal static bool ShouldInitializeLocalRoundState(bool hasAuthoritativeServerFlow)
        {
            return !hasAuthoritativeServerFlow;
        }
    }

    internal static class NoMenuIdentifierPolicy
    {
        internal static bool IsTutorial(string levelConfigName, string sceneName)
        {
            return ContainsTutorial(levelConfigName) || ContainsTutorial(sceneName);
        }

        private static bool ContainsTutorial(string value)
        {
            const string tutorial = "Tutorial";
            if (string.IsNullOrEmpty(value) || value.Length < tutorial.Length)
            {
                return false;
            }

            for (int i = 0; i <= value.Length - tutorial.Length; i++)
            {
                if (string.Compare(value, i, tutorial, 0, tutorial.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class NoMenuRoundPolicy
    {
        internal static NoMenuIneligibility Evaluate(
            bool requested,
            bool isKitchenLevel,
            bool isBoss,
            bool isTutorial,
            bool isSurvival,
            bool hasPreTimerOrders,
            bool isInOnlineSession,
            bool hasRuntimeContract)
        {
            if (!requested)
            {
                return NoMenuIneligibility.Disabled;
            }

            if (!isKitchenLevel)
            {
                return NoMenuIneligibility.UnsupportedLevel;
            }

            if (isBoss)
            {
                return NoMenuIneligibility.Boss;
            }

            if (isTutorial)
            {
                return NoMenuIneligibility.Tutorial;
            }

            if (isSurvival)
            {
                return NoMenuIneligibility.Survival;
            }

            if (hasPreTimerOrders)
            {
                return NoMenuIneligibility.PreTimerOrders;
            }

            if (isInOnlineSession)
            {
                return NoMenuIneligibility.OnlineSession;
            }

            return hasRuntimeContract ? NoMenuIneligibility.None : NoMenuIneligibility.MissingRuntimeContract;
        }
    }
}
