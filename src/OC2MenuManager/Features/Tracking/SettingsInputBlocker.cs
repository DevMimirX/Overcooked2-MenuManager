// Owns the transparent Unity UI shield that makes the IMGUI settings window
// modal for pointer input. The shield is persistent across scene changes, has
// no visible pixels, and exists only to stop clicks from reaching game menus.
using System;
using UnityEngine;
using UnityEngine.UI;

namespace OC2MenuManager
{
    /// <summary>
    /// Creates a topmost full-screen raycast target while the settings window
    /// is open. Unity IMGUI still receives raw mouse events, while the game's
    /// underlying uGUI controls see this inert target instead of a menu button.
    /// </summary>
    internal sealed class SettingsInputBlocker : IDisposable
    {
        private const string RootName = "OC2MenuManager.SettingsInputBlocker";
        private const string SurfaceName = "InputShield";
        private const int TopmostSortingOrder = 32767;

        private GameObject root;

        /// <summary>
        /// Activates or deactivates pointer blocking. The Unity objects are
        /// created lazily so the closed settings window has no UI footprint.
        /// </summary>
        public void SetActive(bool active)
        {
            if (!active)
            {
                if (root != null && root.activeSelf)
                {
                    root.SetActive(false);
                }

                return;
            }

            EnsureCreated();
            if (!root.activeSelf)
            {
                root.SetActive(true);
            }
        }

        /// <summary>
        /// Removes the persistent shield and releases its Unity objects.
        /// </summary>
        public void Dispose()
        {
            if (root == null)
            {
                return;
            }

            root.SetActive(false);
            UnityEngine.Object.Destroy(root);
            root = null;
        }

        private void EnsureCreated()
        {
            if (root != null)
            {
                return;
            }

            GameObject blockerRoot = new GameObject(RootName);
            try
            {
                blockerRoot.hideFlags = HideFlags.HideAndDontSave;
                Canvas canvas = blockerRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = TopmostSortingOrder;
                blockerRoot.AddComponent<GraphicRaycaster>();

                GameObject surface = new GameObject(
                    SurfaceName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                surface.hideFlags = HideFlags.HideAndDontSave;
                surface.transform.SetParent(blockerRoot.transform, false);

                RectTransform surfaceTransform = (RectTransform)surface.transform;
                surfaceTransform.anchorMin = Vector2.zero;
                surfaceTransform.anchorMax = Vector2.one;
                surfaceTransform.offsetMin = Vector2.zero;
                surfaceTransform.offsetMax = Vector2.zero;

                Image shield = surface.GetComponent<Image>();
                shield.color = Color.clear;
                shield.raycastTarget = true;

                UnityEngine.Object.DontDestroyOnLoad(blockerRoot);
                root = blockerRoot;
            }
            catch
            {
                UnityEngine.Object.Destroy(blockerRoot);
                throw;
            }
        }
    }
}
