using System;

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
    }

    internal static class TicketCapacityPolicy
    {
        internal static int CalculateTargetCapacity(int effectiveRealLimit, int activeRealTickets, int requestedReferenceTickets)
        {
            int safeRealLimit = Math.Max(0, effectiveRealLimit);
            int safeRealCount = Math.Max(0, activeRealTickets);
            int safeReferenceCount = Math.Max(0, requestedReferenceTickets);
            return Math.Max(safeRealLimit, safeRealCount) + safeReferenceCount;
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
        PublicOnline,
        MissingRuntimeContract,
        RemoteClient,
        BootstrapOrders
    }

    internal static class NoMenuClientAuthorityPolicy
    {
        internal static bool ShouldInitializeLocalRoundState(bool isInOnlineSession, bool isHost)
        {
            return !isInOnlineSession || isHost;
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
            bool isPublicOnline,
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

            if (isPublicOnline)
            {
                return NoMenuIneligibility.PublicOnline;
            }

            return hasRuntimeContract ? NoMenuIneligibility.None : NoMenuIneligibility.MissingRuntimeContract;
        }
    }
}
