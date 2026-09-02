// Owns the client-only row presentation for real and synthetic order tickets.
// The base RecipeFlowGUI remains authoritative for widget creation, timers,
// tables, and removal; this module only reparents, scales, layers, and positions
// live widgets after the native layout pass. Rows greedily consume the largest
// ordered prefix that fits at their configured native-size percentage. Lower
// rows mask the decorative stitched header outside the top recipe tile and use
// the measured visible body height without altering ticket content or gameplay
// state.
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using OC2MenuManager.Infrastructure;
using UnityEngine;
using UnityEngine.UI;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private const string TicketRowContainerNamePrefix = "OC2MenuManager Ticket Row ";

        private static readonly Dictionary<int, TicketFlowRowLayoutState> TicketRowLayoutsByFlowId = new Dictionary<int, TicketFlowRowLayoutState>();
        private static readonly List<TicketFlowRowLayoutState> TicketRowLayoutStatesBuffer = new List<TicketFlowRowLayoutState>();
        private static readonly List<RecipeFlowGUI.RecipeWidgetData> TicketRowWidgetsBuffer = new List<RecipeFlowGUI.RecipeWidgetData>();
        private static readonly HashSet<int> TicketRowWidgetIdsBuffer = new HashSet<int>();
        private static readonly List<double> TicketRowWidthsBuffer = new List<double>();
        private static readonly List<double> TicketRowHeightsBuffer = new List<double>();
        private static readonly List<double> TicketRowHeaderExtensionHeightsBuffer = new List<double>();
        private static readonly List<int> TicketRowStartIndicesBuffer = new List<int>();
        private static readonly List<int> TicketRowItemCountsBuffer = new List<int>();
        private static readonly List<double> TicketRowScalesBuffer = new List<double>();
        private static readonly List<int> TicketHeaderCropIdsBuffer = new List<int>();

        /// <summary>
        /// Records one reversible mask applied to a base-game top recipe tile.
        /// Existing masks retain their original enabled state; masks introduced
        /// by this module are disabled and destroyed during native restoration.
        /// </summary>
        private sealed class TicketHeaderCropState
        {
            internal TopRecipeWidgetTile Tile;
            internal RectMask2D Mask;
            internal bool OwnedByTracker;
            internal bool OriginalEnabled;
        }

        /// <summary>
        /// Owns reusable row containers and reversible decorative-header masks
        /// for one base-game recipe flow. Widgets remain owned by RecipeFlowGUI
        /// and are restored before presentation components are destroyed, so
        /// gameplay state never depends on this presentation state.
        /// </summary>
        private sealed class TicketFlowRowLayoutState
        {
            internal RecipeFlowGUI Flow;
            internal readonly List<RectTransform> RowContainers = new List<RectTransform>();
            internal readonly Dictionary<int, TicketHeaderCropState> HeaderCropsByWidgetId = new Dictionary<int, TicketHeaderCropState>();
        }

        [HarmonyPatch(typeof(RecipeFlowGUI), "LayoutWidgets")]
        [HarmonyPostfix]
        private static void RecipeFlowGUI_LayoutWidgets_Postfix(RecipeFlowGUI __instance)
        {
            if (__instance == null)
            {
                return;
            }

            try
            {
                ApplyTicketRowLayout(__instance);
            }
            catch (Exception ex)
            {
                string cleanupFailure = string.Empty;
                try
                {
                    RestoreTicketRowLayout(__instance);
                }
                catch (Exception restoreException)
                {
                    cleanupFailure = "; native restore also failed: " + restoreException.GetType().Name;
                    try
                    {
                        ClearTicketRowLayouts();
                    }
                    catch (Exception clearException)
                    {
                        cleanupFailure += "; global row cleanup also failed: " + clearException.GetType().Name;
                    }
                }

                nextTicketRowLayoutRetryFrame = Time.frameCount + TicketWidgetRetryIntervalFrames;
                ScheduleReferenceTicketRetry();
                if (!ticketRowLayoutFailureWarningLogged)
                {
                    ticketRowLayoutFailureWarningLogged = true;
                    _MODEntry.LogWarning(
                        "[ServedDishTracker] Wrapped ticket layout failed and the native row was restored: "
                        + ex.GetType().Name + ": " + ex.Message + cleanupFailure);
                }
            }
        }

        private static bool HasTicketRowLayoutContract()
        {
            return RecipeFlowOrderedWidgetsField != null
                && RecipeFlowDistanceBetweenOrdersField != null
                && RecipeFlowDistanceFromEndOfScreenField != null
                && (nextTicketRowLayoutRetryFrame <= 0 || Time.frameCount >= nextTicketRowLayoutRetryFrame);
        }

        private static void ApplyTicketRowLayout(RecipeFlowGUI flow)
        {
            if (flow == null
                || enabled == null
                || !enabled.Value
                || NoMenuMode.IsActiveForRound
                || !HasTicketRowLayoutContract())
            {
                RestoreTicketRowLayout(flow);
                return;
            }

            nextTicketRowLayoutRetryFrame = 0;

            IList orderedWidgets = RecipeFlowOrderedWidgetsField.GetValue(flow) as IList;
            if (orderedWidgets == null || orderedWidgets.Count == 0)
            {
                RestoreTicketRowLayout(flow);
                return;
            }

            TicketRowWidgetsBuffer.Clear();
            TicketRowWidgetIdsBuffer.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                bool selectReferences = pass == 1;
                for (int i = 0; i < orderedWidgets.Count; i++)
                {
                    RecipeFlowGUI.RecipeWidgetData widgetData = orderedWidgets[i] as RecipeFlowGUI.RecipeWidgetData;
                    if (widgetData == null || widgetData.m_widget == null)
                    {
                        continue;
                    }

                    bool isReference = IsReferenceTicketWidgetData(widgetData);
                    if (isReference != selectReferences)
                    {
                        continue;
                    }

                    int widgetId = widgetData.m_widget.GetInstanceID();
                    if (TicketRowWidgetIdsBuffer.Add(widgetId))
                    {
                        TicketRowWidgetsBuffer.Add(widgetData);
                    }
                }
            }

            int ticketCount = TicketRowWidgetsBuffer.Count;
            if (ticketCount == 0)
            {
                RestoreTicketRowLayout(flow);
                return;
            }

            float spacing = ReadNonNegativeLayoutFloat(RecipeFlowDistanceBetweenOrdersField.GetValue(flow));
            float edgeInset = ReadNonNegativeLayoutFloat(RecipeFlowDistanceFromEndOfScreenField.GetValue(flow));
            float availableWidth = ResolveTicketRowAvailableWidth(flow, edgeInset);

            TicketRowWidthsBuffer.Clear();
            TicketRowHeightsBuffer.Clear();
            TicketRowHeaderExtensionHeightsBuffer.Clear();
            for (int i = 0; i < ticketCount; i++)
            {
                RecipeWidgetUIController widget = TicketRowWidgetsBuffer[i].m_widget;
                float width = ResolveTicketWidth(widget);
                float height = ResolveTicketHeight(widget);
                TicketRowWidthsBuffer.Add(width);
                TicketRowHeightsBuffer.Add(height);
                TicketRowHeaderExtensionHeightsBuffer.Add(ResolveTicketHeaderExtensionHeight(widget));
            }

            TicketRowStartIndicesBuffer.Clear();
            TicketRowItemCountsBuffer.Clear();
            TicketRowScalesBuffer.Clear();
            int nextStartIndex = 0;
            int rowIndex = 0;
            while (nextStartIndex < ticketCount)
            {
                int configuredScalePercent;
                if (rowIndex == 0)
                {
                    configuredScalePercent = firstTicketRowScalePercent != null
                        ? firstTicketRowScalePercent.Value
                        : TicketRowLayoutPolicy.DefaultFirstRowScalePercent;
                }
                else
                {
                    configuredScalePercent = lowerTicketRowScalePercent != null
                        ? lowerTicketRowScalePercent.Value
                        : TicketRowLayoutPolicy.DefaultLowerRowScalePercent;
                }

                double configuredScale = TicketRowLayoutPolicy.CalculateConfiguredScale(configuredScalePercent);
                int itemCount = TicketRowLayoutPolicy.CalculateFittingItemCount(
                    TicketRowWidthsBuffer,
                    nextStartIndex,
                    availableWidth,
                    spacing,
                    configuredScale);
                if (itemCount <= 0)
                {
                    itemCount = 1;
                }

                double naturalWidth = TicketRowLayoutPolicy.CalculateNaturalWidth(
                    TicketRowWidthsBuffer,
                    nextStartIndex,
                    itemCount,
                    spacing);
                TicketRowStartIndicesBuffer.Add(nextStartIndex);
                TicketRowItemCountsBuffer.Add(itemCount);
                TicketRowScalesBuffer.Add(TicketRowLayoutPolicy.CalculateAppliedRowScale(
                    availableWidth,
                    naturalWidth,
                    configuredScalePercent));
                nextStartIndex += itemCount;
                rowIndex++;
            }

            int rowCount = TicketRowItemCountsBuffer.Count;
            if (rowCount == 1 && TicketRowScalesBuffer[0] >= 1d)
            {
                RestoreTicketRowLayout(flow);
                return;
            }

            TicketFlowRowLayoutState state = GetOrCreateTicketRowLayout(flow);
            EnsureTicketRowContainers(state, rowCount);

            float verticalOffset = 0f;
            for (rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                int startIndex = TicketRowStartIndicesBuffer[rowIndex];
                int itemCount = TicketRowItemCountsBuffer[rowIndex];
                float scale = (float)TicketRowScalesBuffer[rowIndex];
                RectTransform rowContainer = state.RowContainers[rowIndex];
                ConfigureTicketRowContainer(rowContainer, rowIndex, verticalOffset, scale);

                float horizontalOffset = scale > 0f ? edgeInset / scale : edgeInset;
                for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    int widgetIndex = startIndex + itemIndex;
                    RecipeWidgetUIController widget = TicketRowWidgetsBuffer[widgetIndex].m_widget;
                    if (widget == null)
                    {
                        continue;
                    }

                    RectTransform widgetTransform = widget.transform as RectTransform;
                    if (widgetTransform == null)
                    {
                        continue;
                    }

                    if (widgetTransform.parent != rowContainer)
                    {
                        widgetTransform.SetParent(rowContainer, false);
                    }
                    widgetTransform.SetSiblingIndex(itemIndex);
                    SetTicketHeaderCropped(state, widget, rowIndex > 0);

                    RectTransformExtension extension = widget.GetComponent<RectTransformExtension>();
                    if (extension != null)
                    {
                        extension.AnchorOffset = Vector2.zero;
                        extension.PixelOffset = new Vector2(horizontalOffset, 0f);
                    }

                    horizontalOffset += (float)TicketRowWidthsBuffer[widgetIndex] + spacing;
                }

                verticalOffset += (float)TicketRowLayoutPolicy.CalculateRowAdvance(
                    TicketRowHeightsBuffer,
                    TicketRowHeaderExtensionHeightsBuffer,
                    startIndex,
                    itemCount,
                    scale,
                    spacing);
            }

            PruneTicketHeaderCrops(state);

            for (int i = rowCount; i < state.RowContainers.Count; i++)
            {
                if (state.RowContainers[i] != null)
                {
                    state.RowContainers[i].gameObject.SetActive(false);
                }
            }

            for (int i = rowCount - 1; i >= 0; i--)
            {
                RectTransform rowContainer = state.RowContainers[i];
                if (rowContainer != null)
                {
                    rowContainer.SetAsLastSibling();
                }
            }
        }

        private static TicketFlowRowLayoutState GetOrCreateTicketRowLayout(RecipeFlowGUI flow)
        {
            int flowId = flow.GetInstanceID();
            TicketFlowRowLayoutState state;
            if (!TicketRowLayoutsByFlowId.TryGetValue(flowId, out state) || state == null)
            {
                state = new TicketFlowRowLayoutState();
                state.Flow = flow;
                TicketRowLayoutsByFlowId[flowId] = state;
            }

            return state;
        }

        private static void EnsureTicketRowContainers(TicketFlowRowLayoutState state, int rowCount)
        {
            if (state == null || state.Flow == null)
            {
                return;
            }

            while (state.RowContainers.Count < rowCount)
            {
                int rowIndex = state.RowContainers.Count;
                GameObject rowObject = new GameObject(TicketRowContainerNamePrefix + (rowIndex + 1), typeof(RectTransform));
                rowObject.layer = state.Flow.gameObject.layer;
                RectTransform rowTransform = rowObject.transform as RectTransform;
                rowTransform.SetParent(state.Flow.transform, false);
                rowTransform.anchorMin = Vector2.zero;
                rowTransform.anchorMax = Vector2.one;
                rowTransform.offsetMin = Vector2.zero;
                rowTransform.offsetMax = Vector2.zero;
                rowTransform.pivot = new Vector2(0f, 1f);
                state.RowContainers.Add(rowTransform);
            }
        }

        private static void ConfigureTicketRowContainer(RectTransform rowContainer, int rowIndex, float verticalOffset, float scale)
        {
            if (rowContainer == null)
            {
                return;
            }

            rowContainer.gameObject.SetActive(true);
            rowContainer.anchorMin = Vector2.zero;
            rowContainer.anchorMax = Vector2.one;
            rowContainer.offsetMin = Vector2.zero;
            rowContainer.offsetMax = Vector2.zero;
            rowContainer.pivot = new Vector2(0f, 1f);
            rowContainer.localRotation = Quaternion.identity;
            rowContainer.localScale = new Vector3(scale, scale, 1f);
            rowContainer.anchoredPosition = rowIndex == 0
                ? Vector2.zero
                : new Vector2(0f, -verticalOffset);
        }

        private static float ResolveTicketRowAvailableWidth(RecipeFlowGUI flow, float edgeInset)
        {
            float width = 0f;
            RectTransform flowTransform = flow != null ? flow.transform as RectTransform : null;
            if (flowTransform != null)
            {
                width = Mathf.Abs(flowTransform.rect.width);
            }

            Canvas canvas = flow != null ? flow.GetComponentInParent<Canvas>() : null;
            RectTransform canvasTransform = canvas != null ? canvas.transform as RectTransform : null;
            if (width <= 0f && canvasTransform != null)
            {
                width = Mathf.Abs(canvasTransform.rect.width);
            }

            if (width <= 0f)
            {
                width = Mathf.Max(1f, Screen.width);
            }

            return Mathf.Max(1f, width - (2f * edgeInset));
        }

        private static float ResolveTicketWidth(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return 1f;
            }

            float width = Mathf.Abs(widget.GetBounds().width);
            if (width <= 0f)
            {
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(widget.transform);
                width = Mathf.Abs(bounds.size.x);
            }

            return IsFinitePositive(width) ? width : 1f;
        }

        private static float ResolveTicketHeight(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return 1f;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(widget.transform);
            float height = Mathf.Abs(bounds.size.y);
            if (height <= 0f)
            {
                height = Mathf.Abs(widget.GetBounds().height);
            }

            return IsFinitePositive(height) ? height : 1f;
        }

        private static float ResolveTicketHeaderExtensionHeight(RecipeWidgetUIController widget)
        {
            TopRecipeWidgetTile topTile = ResolveTopRecipeWidgetTile(widget);
            Image backgroundTop = topTile != null && RecipeWidgetBackgroundTopField != null
                ? RecipeWidgetBackgroundTopField.GetValue(topTile) as Image
                : null;
            if (backgroundTop == null)
            {
                return 0f;
            }

            RectTransform backgroundTransform = backgroundTop.rectTransform;
            float height = 0f;
            if (backgroundTransform != null)
            {
                Bounds headerBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    widget.transform,
                    backgroundTransform);
                height = Mathf.Abs(headerBounds.size.y);
            }
            if (!IsFinitePositive(height))
            {
                TopRecipeWidgetTile.TopDisplayConfiguration configuration = widget != null && RecipeWidgetTopDisplayConfigField != null
                    ? RecipeWidgetTopDisplayConfigField.GetValue(widget) as TopRecipeWidgetTile.TopDisplayConfiguration
                    : null;
                height = configuration != null ? Mathf.Abs(configuration.m_BackgroundStitchSize.y) : 0f;
            }

            return IsFinitePositive(height) ? height : 0f;
        }

        private static TopRecipeWidgetTile ResolveTopRecipeWidgetTile(RecipeWidgetUIController widget)
        {
            if (widget == null)
            {
                return null;
            }

            TopRecipeWidgetTile topTile = RecipeWidgetTopTileField != null
                ? RecipeWidgetTopTileField.GetValue(widget) as TopRecipeWidgetTile
                : null;
            return topTile != null
                ? topTile
                : widget.GetComponentInChildren<TopRecipeWidgetTile>();
        }

        private static void SetTicketHeaderCropped(
            TicketFlowRowLayoutState state,
            RecipeWidgetUIController widget,
            bool cropped)
        {
            if (state == null || widget == null)
            {
                return;
            }

            int widgetId = widget.GetInstanceID();
            TopRecipeWidgetTile topTile = ResolveTopRecipeWidgetTile(widget);
            TicketHeaderCropState cropState;
            if (state.HeaderCropsByWidgetId.TryGetValue(widgetId, out cropState)
                && (cropState == null
                    || cropState.Mask == null
                    || (topTile != null && cropState.Tile != topTile)))
            {
                RestoreTicketHeaderCrop(cropState);
                state.HeaderCropsByWidgetId.Remove(widgetId);
                cropState = null;
            }

            if (cropState == null && cropped)
            {
                if (topTile == null)
                {
                    return;
                }

                RectMask2D mask = topTile.GetComponent<RectMask2D>();
                bool ownedByTracker = mask == null;
                bool originalEnabled = mask != null && mask.enabled;
                if (mask == null)
                {
                    mask = topTile.gameObject.AddComponent<RectMask2D>();
                }

                if (mask == null)
                {
                    return;
                }

                cropState = new TicketHeaderCropState
                {
                    Tile = topTile,
                    Mask = mask,
                    OwnedByTracker = ownedByTracker,
                    OriginalEnabled = originalEnabled
                };
                state.HeaderCropsByWidgetId[widgetId] = cropState;
            }

            if (cropState != null && cropState.Mask != null)
            {
                cropState.Mask.enabled = cropped || cropState.OriginalEnabled;
            }
        }

        private static void PruneTicketHeaderCrops(TicketFlowRowLayoutState state)
        {
            if (state == null || state.HeaderCropsByWidgetId.Count == 0)
            {
                return;
            }

            TicketHeaderCropIdsBuffer.Clear();
            foreach (KeyValuePair<int, TicketHeaderCropState> pair in state.HeaderCropsByWidgetId)
            {
                if (!TicketRowWidgetIdsBuffer.Contains(pair.Key))
                {
                    TicketHeaderCropIdsBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < TicketHeaderCropIdsBuffer.Count; i++)
            {
                int widgetId = TicketHeaderCropIdsBuffer[i];
                TicketHeaderCropState cropState;
                if (state.HeaderCropsByWidgetId.TryGetValue(widgetId, out cropState))
                {
                    RestoreTicketHeaderCrop(cropState);
                    state.HeaderCropsByWidgetId.Remove(widgetId);
                }
            }

            TicketHeaderCropIdsBuffer.Clear();
        }

        private static void RestoreTicketHeaderCrop(TicketHeaderCropState cropState)
        {
            if (cropState == null)
            {
                return;
            }

            if (cropState.Mask != null && cropState.OwnedByTracker)
            {
                cropState.Mask.enabled = false;
                UnityEngine.Object.Destroy(cropState.Mask);
            }
            else if (cropState.Mask != null)
            {
                cropState.Mask.enabled = cropState.OriginalEnabled;
            }

            cropState.Tile = null;
            cropState.Mask = null;
        }

        private static float ReadNonNegativeLayoutFloat(object value)
        {
            float number = value is float ? (float)value : 0f;
            return float.IsNaN(number) || float.IsInfinity(number) ? 0f : Mathf.Max(0f, number);
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static void RestoreTicketRowLayout(RecipeFlowGUI flow)
        {
            if (flow == null)
            {
                return;
            }

            int flowId = flow.GetInstanceID();
            TicketFlowRowLayoutState state;
            if (!TicketRowLayoutsByFlowId.TryGetValue(flowId, out state) || state == null)
            {
                return;
            }

            RestoreTicketRowLayoutState(state);
            TicketRowLayoutsByFlowId.Remove(flowId);
        }

        private static void RestoreTicketRowLayoutState(TicketFlowRowLayoutState state)
        {
            RecipeFlowGUI flow = state != null ? state.Flow : null;
            if (state != null)
            {
                foreach (TicketHeaderCropState cropState in state.HeaderCropsByWidgetId.Values)
                {
                    RestoreTicketHeaderCrop(cropState);
                }
                state.HeaderCropsByWidgetId.Clear();
            }

            for (int i = 0; state != null && i < state.RowContainers.Count; i++)
            {
                RectTransform rowContainer = state.RowContainers[i];
                if (rowContainer == null)
                {
                    continue;
                }

                if (flow != null)
                {
                    while (rowContainer.childCount > 0)
                    {
                        Transform child = rowContainer.GetChild(0);
                        child.SetParent(flow.transform, false);
                    }
                }

                rowContainer.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(rowContainer.gameObject);
            }

            if (state != null)
            {
                state.RowContainers.Clear();
            }
        }

        private static void ClearTicketRowLayouts()
        {
            TicketRowLayoutStatesBuffer.Clear();
            foreach (TicketFlowRowLayoutState state in TicketRowLayoutsByFlowId.Values)
            {
                TicketRowLayoutStatesBuffer.Add(state);
            }

            for (int i = 0; i < TicketRowLayoutStatesBuffer.Count; i++)
            {
                RestoreTicketRowLayoutState(TicketRowLayoutStatesBuffer[i]);
            }

            TicketRowLayoutStatesBuffer.Clear();
            TicketRowLayoutsByFlowId.Clear();
        }
    }
}
