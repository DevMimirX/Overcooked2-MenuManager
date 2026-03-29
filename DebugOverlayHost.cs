using System;
using System.Collections.Generic;
using UnityEngine;

namespace HostUtilities
{
    internal sealed class DebugOverlayHost
    {
        private readonly List<DebugDisplay> displays = new List<DebugDisplay>();
        private readonly GUIStyle style = new GUIStyle();
        private readonly Func<GUIStyle, Rect> rectFactory;
        private readonly Func<TextAnchor> alignmentFactory;
        private readonly Func<int> fontSizeFactory;
        private readonly Func<Color> fontColorFactory;
        private readonly Func<FontStyle> fontStyleFactory;

        public DebugOverlayHost(
            Func<TextAnchor> alignmentFactory,
            Func<int> fontSizeFactory,
            Func<Color> fontColorFactory,
            Func<FontStyle> fontStyleFactory,
            Func<GUIStyle, Rect> rectFactory)
        {
            this.alignmentFactory = alignmentFactory;
            this.fontSizeFactory = fontSizeFactory;
            this.fontColorFactory = fontColorFactory;
            this.fontStyleFactory = fontStyleFactory;
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
            style.alignment = alignmentFactory != null ? alignmentFactory() : TextAnchor.UpperLeft;
            style.fontSize = fontSizeFactory != null
                ? Mathf.RoundToInt(fontSizeFactory() * _MODEntry.dpiScaleFactor)
                : Mathf.RoundToInt(_MODEntry.defaultFontSize.Value * _MODEntry.dpiScaleFactor);
            style.normal.textColor = fontColorFactory != null ? fontColorFactory() : _MODEntry.defaultFontColor.Value;
            style.fontStyle = fontStyleFactory != null ? fontStyleFactory() : FontStyle.Normal;

            Rect rect = rectFactory(style);
            for (int i = 0; i < displays.Count; i++)
            {
                displays[i].OnDraw(ref rect, style);
            }
        }
    }
}
