using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using OrderController;
using Team17.Online;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.UI;

namespace OC2MenuManager
{
    internal static partial class ServedDishTracker
    {
        private enum TrackerLanguage
        {
            Auto,
            English,
            Chinese
        }

        private enum OverlayTextAlignment
        {
            Left,
            Right,
            Center
        }

        private sealed class RecipeInfo
        {
            public int Id;
            public string InternalName;
            public string EnglishName;
            public string ChineseName;
            public string CategoryName;
            public int CategoryTier;
            public OrderDefinitionNode Definition;
            public AssembledDefinitionNode SimplifiedDefinition;
            public AssembledDefinitionNode SimplifiedUnwrappedDefinition;
        }

        private sealed class SceneInfo
        {
            public string SceneName;
            public string DisplayName;
            public List<int>[] PhaseRecipeIds;
            public readonly List<int> AllRecipeIds = new List<int>();
            public readonly List<RecipeInfo> OrderedRecipes = new List<RecipeInfo>();
            public readonly Dictionary<int, RecipeInfo> RecipesById = new Dictionary<int, RecipeInfo>();
        }

        private sealed class RunInfo
        {
            public string SceneName;
            public int CurrentPhaseIndex;
            public int TotalAdded;
            public readonly Dictionary<int, int> AddedCounts = new Dictionary<int, int>();
            public readonly Dictionary<int, int> ServedCounts = new Dictionary<int, int>();
        }

        private sealed class PreparedSourceState
        {
            public int InstanceId;
            public int GameObjectInstanceId;
            public Component Component;
            public IClientOrderDefinition Provider;
            public OrderCompositionChangedCallback Callback;
            public int MatchedRecipeId;
            public bool PendingRemoval;
            public int RemovalGraceUntilFrame;
        }

        private sealed class OverlayDisplay : DebugDisplay
        {
            private static readonly Color PanelBackgroundColor = new Color(0f, 0f, 0f, 0.58f);
            private const float PanelPadding = 10f;
            private readonly GUIStyle textStyle = new GUIStyle();
            private string cachedText = string.Empty;

            public override void OnSetUp()
            {
            }

            public override void OnUpdate()
            {
                if (!overlayDirty || Time.frameCount < nextOverlayRefreshFrame)
                {
                    return;
                }

                cachedText = BuildOverlayText();
                overlayDirty = false;
                nextOverlayRefreshFrame = 0;
                lastOverlayBuildFrame = Time.frameCount;
            }

            public override void OnDraw(ref Rect rect, GUIStyle style)
            {
                if (string.IsNullOrEmpty(cachedText))
                {
                    return;
                }

                Color originalColor = GUI.color;
                GUI.color = PanelBackgroundColor;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = originalColor;

                textStyle.alignment = style.alignment;
                textStyle.font = style.font;
                textStyle.fontSize = style.fontSize;
                textStyle.fontStyle = style.fontStyle;
                textStyle.richText = true;
                textStyle.wordWrap = false;
                textStyle.clipping = TextClipping.Clip;
                textStyle.normal.textColor = style.normal.textColor;

                Rect contentRect = new Rect(
                    rect.x + PanelPadding,
                    rect.y + PanelPadding,
                    Mathf.Max(1f, rect.width - PanelPadding * 2f),
                    Mathf.Max(1f, rect.height - PanelPadding * 2f));

                if (OverlayRenderRowsBuffer.Count == 0)
                {
                    GUI.Label(contentRect, cachedText, textStyle);
                    return;
                }

                float rowHeight = Mathf.Max(16f, textStyle.CalcSize(new GUIContent("A")).y + 4f);
                float y = contentRect.y;
                if (!string.IsNullOrEmpty(overlayHeaderText))
                {
                    float headerHeight = Mathf.Max(rowHeight, textStyle.CalcHeight(new GUIContent(overlayHeaderText), contentRect.width));
                    GUI.Label(new Rect(contentRect.x, y, contentRect.width, headerHeight), overlayHeaderText, textStyle);
                    y += headerHeight + 2f;
                }

                for (int i = 0; i < OverlayRenderRowsBuffer.Count; i++)
                {
                    OverlayRenderRow row = OverlayRenderRowsBuffer[i];
                    if (row == null || string.IsNullOrEmpty(row.Text))
                    {
                        continue;
                    }

                    Rect rowRect = new Rect(contentRect.x, y, contentRect.width, rowHeight);
                    if (row.HasBackground)
                    {
                        Color previousColor = GUI.color;
                        GUI.color = row.BackgroundColor;
                        GUI.DrawTexture(rowRect, Texture2D.whiteTexture);
                        GUI.color = previousColor;
                    }

                    Color previousTextColor = GUI.color;
                    GUI.color = row.TextTint;
                    GUI.Label(rowRect, row.Text, textStyle);
                    if (row.HasStrikeThrough)
                    {
                        float strikeY = rowRect.y + Mathf.Floor(rowRect.height * 0.56f);
                        Rect strikeRect = new Rect(rowRect.x + 8f, strikeY, Mathf.Max(12f, rowRect.width - 16f), 4f);
                        GUI.DrawTexture(strikeRect, Texture2D.whiteTexture);
                    }
                    GUI.color = previousTextColor;
                    y += rowHeight + 1f;
                }

                if (!string.IsNullOrEmpty(overlayFooterText) && y < contentRect.yMax)
                {
                    float footerHeight = Mathf.Max(rowHeight, textStyle.CalcHeight(new GUIContent(overlayFooterText), contentRect.width));
                    GUI.Label(new Rect(contentRect.x, y + 1f, contentRect.width, footerHeight), overlayFooterText, textStyle);
                }
            }
        }

        private static readonly Color SettingsWindowBodyColor = new Color(0.10f, 0.10f, 0.10f, 0.96f);
        private static readonly Color SettingsWindowHeaderColor = new Color(0.17f, 0.17f, 0.17f, 0.98f);

        private sealed class OverlayRow
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
            public int Prepared;
            public int OnMenu;
            public int EarliestMenuOrder;

            public void Reset()
            {
                Recipe = null;
                Probability = 0d;
                Served = 0;
                Prepared = 0;
                OnMenu = 0;
                EarliestMenuOrder = int.MaxValue;
            }
        }

        private sealed class OverlayRenderRow
        {
            public string Text;
            public Color BackgroundColor;
            public bool HasBackground;
            public Color TextTint = Color.white;
            public bool HasStrikeThrough;

            public void Reset()
            {
                Text = string.Empty;
                BackgroundColor = Color.clear;
                HasBackground = false;
                TextTint = Color.white;
                HasStrikeThrough = false;
            }
        }

        private sealed class ReferenceTicketCandidate
        {
            public RecipeInfo Recipe;
            public double Probability;
            public int Served;
        }

        private sealed class ReferenceTicketState
        {
            public int FlowInstanceId;
            public RecipeFlowGUI Flow;
            public int RecipeId;
            public double Probability;
            public RecipeFlowGUI.ElementToken Token;
            public RecipeWidgetUIController Widget;
        }

        private sealed class CategorySelectionGroup
        {
            public string CategoryName;
            public int CategoryTier;
            public readonly List<int> RecipeIds = new List<int>();
        }

        private sealed class TicketWidgetState
        {
            public int InstanceId;
            public int RecipeId;
            public int Order;
            public RecipeWidgetUIController Widget;
            public RecipeWidgetTile.DisplayConfiguration DisplayConfig;
            public TopRecipeWidgetTile.TopDisplayConfiguration TopDisplayConfig;
            public Color OriginalDisplayTint;
            public Color OriginalTopTint;
            public float OriginalOpacity = 1f;
            public Image[] CachedImages;
            public CanvasGroup CanvasGroup;
            public bool CanvasGroupResolved;
            public Color AppliedDisplayTint;
            public Color AppliedTopTint;
            public float AppliedOpacity = 1f;
            public bool HasAppliedTint;
            public bool IsReferenceTicket;
            public bool IsDyingReferenceTicket;
            public double ReferenceProbability;
        }

    }
}
