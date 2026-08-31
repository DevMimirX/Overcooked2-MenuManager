using System;
using System.Collections.Generic;
using UnityEngine;

namespace OC2MenuManager.Infrastructure
{
    internal sealed class DebugOverlayHost
    {
        private readonly List<DebugDisplay> displays = new List<DebugDisplay>();
        private readonly GUIStyle style = new GUIStyle();
        private readonly Func<TextAnchor> alignmentFactory;
        private readonly Func<int> fontSizeFactory;
        private readonly Func<Color> fontColorFactory;
        private readonly Func<FontStyle> fontStyleFactory;
        private readonly Func<float> dpiScaleFactorFactory;
        private readonly Func<GUIStyle, Rect> rectFactory;

        public DebugOverlayHost(
            Func<TextAnchor> alignmentFactory,
            Func<int> fontSizeFactory,
            Func<Color> fontColorFactory,
            Func<FontStyle> fontStyleFactory,
            Func<float> dpiScaleFactorFactory,
            Func<GUIStyle, Rect> rectFactory)
        {
            this.alignmentFactory = alignmentFactory;
            this.fontSizeFactory = fontSizeFactory;
            this.fontColorFactory = fontColorFactory;
            this.fontStyleFactory = fontStyleFactory;
            this.dpiScaleFactorFactory = dpiScaleFactorFactory;
            this.rectFactory = rectFactory;
            style.richText = true;
        }

        public void AddDisplay(DebugDisplay display)
        {
            if (display == null)
            {
                return;
            }

            display.OnSetUp();
            displays.Add(display);
        }

        public void RemoveDisplay(DebugDisplay display)
        {
            if (display != null)
            {
                displays.Remove(display);
            }
        }

        public void Update()
        {
            for (int i = 0; i < displays.Count; i++)
            {
                displays[i].OnUpdate();
            }
        }

        public void OnGUI()
        {
            float dpiScaleFactor = dpiScaleFactorFactory != null ? dpiScaleFactorFactory() : 1f;
            style.alignment = alignmentFactory != null ? alignmentFactory() : TextAnchor.UpperLeft;
            style.fontSize = Mathf.RoundToInt((fontSizeFactory != null ? fontSizeFactory() : 18) * dpiScaleFactor);
            style.normal.textColor = fontColorFactory != null ? fontColorFactory() : Color.white;
            style.fontStyle = fontStyleFactory != null ? fontStyleFactory() : FontStyle.Normal;

            Rect rect = rectFactory(style);
            for (int i = 0; i < displays.Count; i++)
            {
                displays[i].OnDraw(ref rect, style);
            }
        }
    }
}
