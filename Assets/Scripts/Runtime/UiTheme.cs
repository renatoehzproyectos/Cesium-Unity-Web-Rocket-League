using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Look of the in-game UI: one dark-glass skin (rounded 9-slice panels, pill buttons, thin sliders), the bundled
    /// Barlow Semi Condensed font and a few procedural icons. Everything is generated at runtime, so the repo stays code-only.
    /// Build() must run inside OnGUI (it clones GUI.skin).
    /// </summary>
    public static class UiTheme
    {
        public static readonly Color Accent = new Color(0.22f, 0.71f, 1f, 1f);
        public static readonly Color Hot = new Color(1f, 0.62f, 0.16f, 1f);
        public static readonly Color Text = new Color(0.93f, 0.96f, 1f, 1f);
        public static readonly Color TextDim = new Color(0.93f, 0.96f, 1f, 0.62f);
        static readonly Color PanelFill = new Color(0.035f, 0.06f, 0.095f, 0.86f);
        static readonly Color Edge = new Color(1f, 1f, 1f, 0.13f);

        public static Font Regular, Semi, Bold;
        public static Texture2D Gear, Arrow, Vignette;
        public static GUIStyle Toast, Pill, Touch, BtnListen, BtnSelect, Title, Hint, Big, Unit, Center;

        static GUISkin _skin;
        public static GUISkin Skin => _skin != null ? _skin : _skin = Build();

        // ------------------------------------------------------------------------------------------------ build

        static GUISkin Build()
        {
            Regular = Resources.Load<Font>("Fonts/BarlowSemiCondensed-Regular");
            Semi = Resources.Load<Font>("Fonts/BarlowSemiCondensed-SemiBold");
            Bold = Resources.Load<Font>("Fonts/BarlowSemiCondensed-Bold");

            var s = Object.Instantiate(GUI.skin);
            s.hideFlags = HideFlags.HideAndDontSave;
            if (Regular != null) s.font = Regular;

            var panel = Round(PanelFill, Edge, 14, 1.2f);
            var slate = Round(new Color(0.17f, 0.23f, 0.31f, 0.92f), new Color(1, 1, 1, 0.10f), 10, 1f);
            var slateHot = Round(new Color(0.20f, 0.46f, 0.66f, 0.96f), new Color(1, 1, 1, 0.25f), 10, 1f);
            var slateDown = Round(new Color(0.22f, 0.71f, 1f, 0.95f), new Color(1, 1, 1, 0.35f), 10, 1f);
            var field = Round(new Color(0f, 0f, 0f, 0.38f), new Color(1, 1, 1, 0.14f), 9, 1f);
            var fieldFocus = Round(new Color(0f, 0f, 0f, 0.46f), new Color(Accent.r, Accent.g, Accent.b, 0.9f), 9, 1.4f);

            // label
            var l = s.label;
            l.font = Regular; l.fontSize = 15; l.richText = true; l.wordWrap = true;
            l.normal.textColor = Text; l.padding = new RectOffset(2, 2, 2, 2); l.margin = new RectOffset(4, 4, 3, 3);

            // box = panel
            var b = s.box;
            b.normal.background = panel; b.border = new RectOffset(14, 14, 14, 14);
            b.padding = new RectOffset(14, 14, 12, 12); b.margin = new RectOffset(0, 0, 0, 0);
            b.normal.textColor = Text; b.font = Semi; b.fontSize = 15; b.alignment = TextAnchor.MiddleCenter; b.richText = true;

            // button
            var bt = s.button;
            bt.normal.background = slate; bt.hover.background = slateHot; bt.active.background = slateDown; bt.focused.background = slate;
            bt.onNormal.background = slateHot; bt.onHover.background = slateHot; bt.onActive.background = slateDown; bt.onFocused.background = slateHot;
            bt.border = new RectOffset(10, 10, 10, 10);
            bt.padding = new RectOffset(14, 14, 7, 7); bt.margin = new RectOffset(3, 3, 3, 3);
            bt.font = Semi; bt.fontSize = 15; bt.alignment = TextAnchor.MiddleCenter; bt.richText = true;
            bt.normal.textColor = bt.hover.textColor = bt.active.textColor = bt.focused.textColor = Text;
            bt.onNormal.textColor = bt.onHover.textColor = bt.onActive.textColor = bt.onFocused.textColor = Text;

            // text field
            var tf = s.textField;
            tf.normal.background = field; tf.hover.background = field; tf.focused.background = fieldFocus; tf.active.background = fieldFocus;
            tf.border = new RectOffset(9, 9, 9, 9); tf.padding = new RectOffset(10, 10, 7, 7); tf.margin = new RectOffset(3, 3, 3, 3);
            tf.font = Regular; tf.fontSize = 15;
            tf.normal.textColor = tf.hover.textColor = tf.focused.textColor = tf.active.textColor = Text;

            // sliders
            var track = Round(new Color(1, 1, 1, 0.16f), Color.clear, 4, 0f, 12);
            var knob = Circle(32, Color.white, new Color(Accent.r, Accent.g, Accent.b, 1f), 3f);
            var sl = s.horizontalSlider;
            sl.normal.background = track; sl.border = new RectOffset(4, 4, 4, 4); sl.fixedHeight = 8;
            sl.margin = new RectOffset(6, 6, 9, 9); sl.padding = new RectOffset(0, 0, 0, 0);
            var th = s.horizontalSliderThumb;
            th.normal.background = knob; th.hover.background = knob; th.active.background = knob; th.focused.background = knob;
            th.fixedWidth = 18; th.fixedHeight = 18; th.border = new RectOffset(0, 0, 0, 0);
            th.overflow = new RectOffset(0, 0, 5, 5); th.margin = new RectOffset(0, 0, 0, 0); th.padding = new RectOffset(0, 0, 0, 0);

            // scrollbars
            var sbTrack = Round(new Color(1, 1, 1, 0.05f), Color.clear, 4, 0f, 12);
            var sbThumb = Round(new Color(1, 1, 1, 0.28f), Color.clear, 4, 0f, 12);
            var vs = s.verticalScrollbar; vs.normal.background = sbTrack; vs.border = new RectOffset(4, 4, 4, 4); vs.fixedWidth = 8; vs.margin = new RectOffset(4, 0, 0, 0);
            var vt = s.verticalScrollbarThumb; vt.normal.background = sbThumb; vt.hover.background = sbThumb; vt.active.background = sbThumb; vt.border = new RectOffset(4, 4, 4, 4); vt.fixedWidth = 8;
            s.verticalScrollbarUpButton.fixedHeight = 0; s.verticalScrollbarUpButton.fixedWidth = 0;
            s.verticalScrollbarDownButton.fixedHeight = 0; s.verticalScrollbarDownButton.fixedWidth = 0;

            // --- extra styles
            Toast = new GUIStyle(b) { fontSize = 16, padding = new RectOffset(18, 18, 8, 8) };
            Pill = new GUIStyle(b) { fontSize = 14, padding = new RectOffset(14, 14, 6, 6), normal = { background = panel, textColor = TextDim } };
            Touch = new GUIStyle(b)
            {
                normal = { background = Round(new Color(1, 1, 1, 0.10f), new Color(1, 1, 1, 0.22f), 18, 1.5f), textColor = new Color(1, 1, 1, 0.75f) },
                border = new RectOffset(18, 18, 18, 18), fontSize = 16, padding = new RectOffset(4, 4, 4, 4),
            };
            BtnListen = new GUIStyle(bt) { normal = { background = Round(new Color(1f, 0.74f, 0.2f, 0.95f), new Color(1, 1, 1, 0.5f), 10, 1.2f), textColor = new Color(0.1f, 0.07f, 0f) } };
            BtnSelect = new GUIStyle(bt) { normal = { background = Round(new Color(0.12f, 0.34f, 0.52f, 0.95f), new Color(Accent.r, Accent.g, Accent.b, 0.9f), 10, 1.5f), textColor = Text } };
            Title = new GUIStyle(l) { font = Bold, fontSize = 22, fontStyle = FontStyle.Normal, margin = new RectOffset(4, 4, 2, 8) };
            Hint = new GUIStyle(l) { fontSize = 14, alignment = TextAnchor.MiddleCenter, normal = { textColor = TextDim } };
            Big = new GUIStyle(l) { font = Bold, fontSize = 54, alignment = TextAnchor.LowerCenter, wordWrap = false };
            Unit = new GUIStyle(l) { font = Semi, fontSize = 14, alignment = TextAnchor.UpperCenter, wordWrap = false, normal = { textColor = TextDim } };
            Center = new GUIStyle(l) { alignment = TextAnchor.UpperCenter };

            Gear = MakeGear(64);
            Arrow = MakeArrow(64);
            Vignette = MakeVignette(64);
            return s;
        }

        // ------------------------------------------------------------------------------------------ textures

        static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        }

        /// <summary>Anti-aliased rounded rectangle, fill + inner border line. Used as a 9-slice (border = radius).</summary>
        static Texture2D Round(Color fill, Color border, int radius, float borderWidth, int sizeOverride = 0)
        {
            int size = sizeOverride > 0 ? sizeOverride : Mathf.Max(radius * 2 + 4, 24);
            var t = NewTex(size, size);
            var px = new Color[size * size];
            float c = size * 0.5f, h = c - radius;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - c) - h, qy = Mathf.Abs(y + 0.5f - c) - h;
                    float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float cover = Mathf.Clamp01(0.5f - d);
                    float inner = borderWidth <= 0f ? 1f : Mathf.Clamp01(0.5f - (d + borderWidth));
                    var col = Color.Lerp(border, fill, inner);
                    col.a *= cover;
                    px[y * size + x] = col;
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        static Texture2D Circle(int size, Color fill, Color ring, float ringWidth)
        {
            var t = NewTex(size, size);
            var px = new Color[size * size];
            float c = size * 0.5f, r = c - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = new Vector2(x + 0.5f - c, y + 0.5f - c).magnitude - r;
                    float cover = Mathf.Clamp01(0.5f - d);
                    float inner = Mathf.Clamp01(0.5f - (d + ringWidth));
                    var col = Color.Lerp(ring, fill, inner); col.a *= cover;
                    px[y * size + x] = col;
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        static Texture2D MakeGear(int size)
        {
            var t = NewTex(size, size);
            var px = new Color[size * size];
            float c = size * 0.5f, k = size / 64f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f - c, y + 0.5f - c);
                    float r = p.magnitude / k, a = Mathf.Atan2(p.y, p.x);
                    float tooth = Mathf.SmoothStep(-0.25f, 0.25f, Mathf.Cos(8f * a));
                    float outer = 19f + 7f * tooth;
                    float cover = Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - 9f + 0.5f);
                    px[y * size + x] = new Color(1, 1, 1, cover);
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        static Texture2D MakeArrow(int size)
        {
            var t = NewTex(size, size);
            var px = new Color[size * size];
            Vector2[] poly = { new Vector2(32, 60), new Vector2(11, 8), new Vector2(32, 20), new Vector2(53, 8) };   // y up: tip at the top
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                            if (InPoly(poly, x + (sx + 0.5f) / 4f, y + (sy + 0.5f) / 4f)) hit++;
                    px[y * size + x] = new Color(1, 1, 1, hit / 16f);
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        static bool InPoly(Vector2[] p, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
                if ((p[i].y > y) != (p[j].y > y) && x < (p[j].x - p[i].x) * (y - p[i].y) / (p[j].y - p[i].y) + p[i].x) inside = !inside;
            return inside;
        }

        static Texture2D MakeVignette(int size)
        {
            var t = NewTex(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * size + x] = new Color(0.02f, 0.04f, 0.08f, Mathf.SmoothStep(0.62f, 1.38f, d) * 0.42f);
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }
    }
}
