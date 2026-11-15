using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// One place that decides how big the UI is and where the free space is, so every panel agrees (and nothing overlaps).
    /// Units are "UI points": the screen divided by <see cref="K"/>. The scale follows the SMALLER of height/720 and width/420, so a phone held
    /// upright (narrow) gets the same comfortable ~420-point-wide canvas as a phone held sideways gets 720 high.
    /// <see cref="Compact"/> (narrow canvas) turns the side panels into full-screen sheets and wraps the toolbar.
    /// </summary>
    public static class UiLayout
    {
        public static float K { get; private set; } = 1f;
        public static float W { get; private set; } = 1280f;
        public static float H { get; private set; } = 720f;
        public static bool Compact { get; private set; }
        /// <summary>Space to keep free at each edge (notch / gesture bar aware), in UI points.</summary>
        public static float Left { get; private set; } = 12f;
        public static float Right { get; private set; } = 12f;
        public static float Top { get; private set; } = 12f;
        public static float Bottom { get; private set; } = 12f;
        /// <summary>Bottom edge of the toolbar / minimap block, written by the HUD each frame. Anything centred at the top goes below it.</summary>
        public static float TopBlockBottom = 110f;
        /// <summary>Bottom edge of the toolbar alone (panels hang below it).</summary>
        public static float ToolbarBottom = 66f;
        /// <summary>Height of top-centre widgets owned by another layer (the multiplayer banner), so the HUD's own notices stack below them.</summary>
        public static float ExtraTop;

        static int _w, _h;
        static Rect _safe;

        public static void Refresh()
        {
            int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            var safe = Screen.safeArea;
            if (sw == _w && sh == _h && safe == _safe) return;
            _w = sw; _h = sh; _safe = safe;
            K = Mathf.Clamp(Mathf.Min(sh / 720f, sw / 420f), 0.75f, 3f);
            W = sw / K; H = sh / K;
            Compact = W < 640f;
            const float pad = 12f;
            Left = Mathf.Max(pad, safe.xMin / K + 4f);
            Right = Mathf.Max(pad, (sw - safe.xMax) / K + 4f);
            Top = Mathf.Max(pad, (sh - safe.yMax) / K + 4f);
            Bottom = Mathf.Max(pad, safe.yMin / K + 4f);
        }

        /// <summary>Call first thing in OnGUI: refreshes the numbers and scales the GUI matrix.</summary>
        public static void Begin()
        {
            Refresh();
            GUIUtility.ScaleAroundPivot(new Vector2(K, K), Vector2.zero);
        }

        /// <summary>Screen pixels (origin bottom-left, as Input reports them) to UI points (origin top-left, as IMGUI uses them).</summary>
        public static Vector2 ToGui(Vector2 screenPos)
        {
            Refresh();
            return new Vector2(screenPos.x / K, (Screen.height - screenPos.y) / K);
        }

        /// <summary>Rect of a side panel: a right-aligned column under the toolbar on wide screens, the whole screen on narrow ones.</summary>
        public static Rect Panel(float preferredWidth = 380f)
        {
            Refresh();
            if (Compact) return new Rect(Left, Top, W - Left - Right, H - Top - Bottom);
            float w = Mathf.Min(preferredWidth, W - Left - Right);
            float y = Mathf.Max(Top, ToolbarBottom + 8f);
            return new Rect(W - Right - w, y, w, H - y - Bottom);
        }

        /// <summary>Width of a centred top banner (toast, notices): never wider than the screen.</summary>
        public static float BannerWidth(float preferred) { Refresh(); return Mathf.Min(preferred, W - Left - Right); }
    }
}
