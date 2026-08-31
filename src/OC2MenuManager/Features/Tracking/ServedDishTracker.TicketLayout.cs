// Owns the client-only row presentation for real and synthetic order tickets.
// The base RecipeFlowGUI remains authoritative for widget creation, timers,
// tables, and removal; this module only reparents and positions its live widgets
// after the native layout pass when wrapping or width fitting is required.
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using OC2MenuManager.Infrastructure;
using UnityEngine;

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
        private static readonly List<float> TicketRowHeightsBuffer = new List<float>();

        /// <summary>
        /// Owns reusable row containers for one base-game recipe flow. Widgets
        /// remain owned by RecipeFlowGUI and are restored before containers are
        /// destroyed, so gameplay state never depends on this presentation state.
        /// </summary>
        private sealed class TicketFlowRowLayoutState
        {
            internal RecipeFlowGUI Flow;
            internal readonly List<RectTransform> RowContainers = new List<RectTransform>();
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
            bool hasReferenceTicket = false;
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
                        hasReferenceTicket |= isReference;
                    }
                }
            }

            int ticketCount = TicketRowWidgetsBuffer.Count;
            int rowCount = TicketRowLayoutPolicy.CalculateRowCount(ticketCount, MaxTicketsPerRow);
            if (!hasReferenceTicket || rowCount == 0)
            {
                RestoreTicketRowLayout(flow);
                return;
            }

            float spacing = ReadNonNegativeLayoutFloat(RecipeFlowDistanceBetweenOrdersField.GetValue(flow));
            float edgeInset = ReadNonNegativeLayoutFloat(RecipeFlowDistanceFromEndOfScreenField.GetValue(flow));
            float availableWidth = ResolveTicketRowAvailableWidth(flow, edgeInset);

            TicketRowWidthsBuffer.Clear();
            TicketRowHeightsBuffer.Clear();
            for (int i = 0; i < ticketCount; i++)
            {
                RecipeWidgetUIController widget = TicketRowWidgetsBuffer[i].m_widget;
                float width = ResolveTicketWidth(widget);
                float height = ResolveTicketHeight(widget);
                TicketRowWidthsBuffer.Add(width);
                TicketRowHeightsBuffer.Add(height);
            }

            if (rowCount == 1)
            {
                double naturalWidth = TicketRowLayoutPolicy.CalculateNaturalWidth(
                    TicketRowWidthsBuffer,
                    0,
                    ticketCount,
                    spacing);
                if (TicketRowLayoutPolicy.CalculateFitScale(availableWidth, naturalWidth) >= 1d)
                {
                    RestoreTicketRowLayout(flow);
                    return;
                }
            }

            TicketFlowRowLayoutState state = GetOrCreateTicketRowLayout(flow);
            EnsureTicketRowContainers(state, rowCount);

            float verticalOffset = 0f;
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                int startIndex = rowIndex * MaxTicketsPerRow;
                int itemCount = TicketRowLayoutPolicy.CalculateRowItemCount(ticketCount, rowIndex, MaxTicketsPerRow);
                double naturalWidth = TicketRowLayoutPolicy.CalculateNaturalWidth(
                    TicketRowWidthsBuffer,
                    startIndex,
                    itemCount,
                    spacing);
                float scale = (float)TicketRowLayoutPolicy.CalculateFitScale(availableWidth, naturalWidth);
                RectTransform rowContainer = state.RowContainers[rowIndex];
                ConfigureTicketRowContainer(rowContainer, rowIndex, verticalOffset, scale);

                float horizontalOffset = scale > 0f ? edgeInset / scale : edgeInset;
                float rowHeight = 1f;
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

                    RectTransformExtension extension = widget.GetComponent<RectTransformExtension>();
                    if (extension != null)
                    {
                        extension.AnchorOffset = Vector2.zero;
                        extension.PixelOffset = new Vector2(horizontalOffset, 0f);
                    }

                    horizontalOffset += (float)TicketRowWidthsBuffer[widgetIndex] + spacing;
                    rowHeight = Mathf.Max(rowHeight, TicketRowHeightsBuffer[widgetIndex]);
                }

                rowContainer.SetAsLastSibling();
                verticalOffset += (rowHeight * scale) + spacing;
            }

            for (int i = rowCount; i < state.RowContainers.Count; i++)
            {
                if (state.RowContainers[i] != null)
                {
                    state.RowContainers[i].gameObject.SetActive(false);
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
