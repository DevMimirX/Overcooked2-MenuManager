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

        public DebugOverlayHost(TextAnchor alignment, Func<GUIStyle, Rect> rectFactory)
        {
            this.rectFactory = rectFactory;
            style.alignment = alignment;
            style.richText = false;
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
            style.fontSize = Mathf.RoundToInt(_MODEntry.defaultFontSize.Value * _MODEntry.dpiScaleFactor);
            style.normal.textColor = _MODEntry.defaultFontColor.Value;

            Rect rect = rectFactory(style);
            for (int i = 0; i < displays.Count; i++)
            {
                displays[i].OnDraw(ref rect, style);
            }
        }
    }
}
