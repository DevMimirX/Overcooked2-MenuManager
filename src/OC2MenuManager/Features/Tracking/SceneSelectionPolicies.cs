// Defines side-effect-free scene-selector layout, filtering, and DIY catalog
// snapshot policies. The game-facing tracker owns all mutable UI/provider state;
// these policies only validate inputs and calculate deterministic decisions.
using System;
using System.Collections.Generic;

#pragma warning disable CA2249 // .NET 3.5 has no string.Contains overload with StringComparison.

namespace OC2MenuManager
{
    /// <summary>
    /// Owns the searchable scene-list contract and viewport calculations used by
    /// the IMGUI selector. Search is ordinal and case-insensitive, source ordering
    /// is preserved by callers, and invalid layout inputs fail to empty ranges.
    /// </summary>
    internal static class SceneSelectionPolicy
    {
        internal static string NormalizeQuery(string query)
        {
            return string.IsNullOrEmpty(query) ? string.Empty : query.Trim();
        }

        internal static bool Matches(
            string query,
            string sceneName,
            string displayName,
            string englishDisplayName,
            string chineseDisplayName)
        {
            string normalizedQuery = NormalizeQuery(query);
            if (normalizedQuery.Length == 0)
            {
                return true;
            }

            return Contains(sceneName, normalizedQuery)
                || Contains(displayName, normalizedQuery)
                || Contains(englishDisplayName, normalizedQuery)
                || Contains(chineseDisplayName, normalizedQuery);
        }

        internal static float CalculateDropdownHeight(
            float windowHeight,
            float heightRatio,
            float minimumHeight,
            float maximumHeight)
        {
            if (!IsFinite(windowHeight)
                || !IsFinite(heightRatio)
                || !IsFinite(minimumHeight)
                || !IsFinite(maximumHeight))
            {
                return 0f;
            }

            float safeWindowHeight = Math.Max(0f, windowHeight);
            float safeRatio = Math.Max(0f, heightRatio);
            float lowerBound = Math.Max(0f, Math.Min(minimumHeight, maximumHeight));
            float upperBound = Math.Max(lowerBound, Math.Max(minimumHeight, maximumHeight));
            return Math.Max(lowerBound, Math.Min(upperBound, safeWindowHeight * safeRatio));
        }

        internal static float CalculateFittedWindowDimension(
            float screenDimension,
            float margin,
            float desiredDimension,
            float minimumDimension)
        {
            if (!IsFinite(screenDimension)
                || !IsFinite(margin)
                || !IsFinite(desiredDimension)
                || !IsFinite(minimumDimension))
            {
                return 1f;
            }

            float safeScreenDimension = Math.Max(1f, screenDimension);
            float safeMargin = Math.Max(0f, margin);
            float availableDimension = Math.Max(1f, safeScreenDimension - safeMargin * 2f);
            float safeMinimum = Math.Max(1f, minimumDimension);
            float preferredDimension = Math.Max(safeMinimum, Math.Max(1f, desiredDimension));
            return Math.Min(preferredDimension, availableDimension);
        }

        internal static float CalculateClampedWindowPosition(
            float screenDimension,
            float windowDimension,
            float margin,
            float currentPosition)
        {
            if (!IsFinite(screenDimension)
                || !IsFinite(windowDimension)
                || !IsFinite(margin)
                || !IsFinite(currentPosition))
            {
                return 0f;
            }

            float safeScreenDimension = Math.Max(1f, screenDimension);
            float safeWindowDimension = Math.Max(1f, Math.Min(windowDimension, safeScreenDimension));
            float freeSpace = Math.Max(0f, safeScreenDimension - safeWindowDimension);
            float effectiveMargin = Math.Min(Math.Max(0f, margin), freeSpace * 0.5f);
            float maximumPosition = Math.Max(effectiveMargin, freeSpace - effectiveMargin);
            return Math.Max(effectiveMargin, Math.Min(maximumPosition, currentPosition));
        }

        internal static void CalculateVisibleRange(
            int itemCount,
            float scrollOffset,
            float viewportHeight,
            float rowHeight,
            int overscanRows,
            out int firstIndex,
            out int endIndexExclusive)
        {
            firstIndex = 0;
            endIndexExclusive = 0;
            if (itemCount <= 0
                || !IsFinite(scrollOffset)
                || !IsFinite(viewportHeight)
                || !IsFinite(rowHeight)
                || viewportHeight <= 0f
                || rowHeight <= 0f)
            {
                return;
            }

            int safeOverscan = Math.Max(0, overscanRows);
            double safeScrollOffset = Math.Max(0d, scrollOffset);
            int firstVisible = (int)Math.Floor(safeScrollOffset / rowHeight);
            int lastVisibleExclusive = (int)Math.Ceiling((safeScrollOffset + viewportHeight) / rowHeight);
            firstIndex = Math.Max(0, Math.Min(itemCount, firstVisible - safeOverscan));
            endIndexExclusive = Math.Max(firstIndex, Math.Min(itemCount, lastVisibleExclusive + safeOverscan));
        }

        internal static float CalculateScrollOffsetForItem(
            int itemIndex,
            int itemCount,
            float currentScrollOffset,
            float viewportHeight,
            float rowHeight)
        {
            if (itemIndex < 0
                || itemIndex >= itemCount
                || itemCount <= 0
                || !IsFinite(currentScrollOffset)
                || !IsFinite(viewportHeight)
                || !IsFinite(rowHeight)
                || viewportHeight <= 0f
                || rowHeight <= 0f)
            {
                return 0f;
            }

            float maximumScrollOffset = Math.Max(0f, itemCount * rowHeight - viewportHeight);
            float scrollOffset = Math.Max(0f, Math.Min(maximumScrollOffset, currentScrollOffset));
            float itemTop = itemIndex * rowHeight;
            float itemBottom = itemTop + rowHeight;
            if (itemTop < scrollOffset)
            {
                scrollOffset = itemTop;
            }
            else if (itemBottom > scrollOffset + viewportHeight)
            {
                scrollOffset = itemBottom - viewportHeight;
            }

            return Math.Max(0f, Math.Min(maximumScrollOffset, scrollOffset));
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal enum DIYCatalogSnapshotAction
    {
        Retain,
        Replace
    }

    /// <summary>
    /// Protects the last authoritative DIY metadata snapshot from transient or
    /// wholly malformed reads while allowing valid empty and partial catalogs to
    /// replace it. Scene names are the provider's stable, case-insensitive keys.
    /// </summary>
    internal static class DIYCatalogRefreshPolicy
    {
        internal static DIYCatalogSnapshotAction EvaluateSnapshot(
            bool readSucceeded,
            int acceptedSceneCount,
            int rejectedEntryCount)
        {
            if (!readSucceeded || acceptedSceneCount < 0 || rejectedEntryCount < 0)
            {
                return DIYCatalogSnapshotAction.Retain;
            }

            return acceptedSceneCount > 0 || rejectedEntryCount == 0
                ? DIYCatalogSnapshotAction.Replace
                : DIYCatalogSnapshotAction.Retain;
        }

        internal static bool TryAcceptSceneName(string sceneName, HashSet<string> acceptedSceneNames)
        {
            if (acceptedSceneNames == null || string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            for (int i = 0; i < sceneName.Length; i++)
            {
                if (!char.IsWhiteSpace(sceneName[i]))
                {
                    return acceptedSceneNames.Add(sceneName);
                }
            }

            return false;
        }
    }
}

#pragma warning restore CA2249
