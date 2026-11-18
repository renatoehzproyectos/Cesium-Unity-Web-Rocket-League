using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// Look of the in-game UI: one dark "midnight + cyan" skin (rounded 9-slice panels, pill buttons, thick touch-friendly sliders), the bundled
    /// Barlow Semi Condensed font and a few procedural icons. Everything is generated at runtime, so the repo stays code-only.
    ///
    /// Palette: ink-blue surfaces, ONE accent (electric cyan) for "active / primary", amber for "hot" (boost, warnings), mint for "ok", coral for "danger".
    /// The bundled font has no arrows, triangles or dots: never put those characters in a label, use the icon textures instead.
    /// Build() must run inside OnGUI (it clones GUI.skin).
    /// </summary>
    public static class UiTheme
    {
        // ---- palette ----
        public static readonly Color Ink = new Color(0.039f, 0.063f, 0.110f, 1f);        // #0A101C  text on accent fills
        public static readonly Color Accent = new Color(0.239f, 0.839f, 0.961f, 1f);     // #3DD6F5
        public static readonly Color Hot = new Color(1f, 0.690f, 0.125f, 1f);            // #FFB020
        public static readonly Color Good = new Color(0.30f, 0.87f, 0.52f, 1f);          // #4DDE85
        public static readonly Color Danger = new Color(1f, 0.42f, 0.44f, 1f);           // #FF6B70
        public static readonly Color Text = new Color(0.918f, 0.949f, 1f, 1f);           // #EAF2FF
        public static readonly Color TextDim = new Color(0.62f, 0.70f, 0.80f, 1f);       // #9FB3CC
        static readonly Color PanelFill = new Color(0.047f, 0.075f, 0.130f, 0.94f);
        static readonly Color SurfaceFill = new Color(0.100f, 0.150f, 0.240f, 0.96f);
        static readonly Color SurfaceHi = new Color(0.150f, 0.220f, 0.345f, 0.98f);
        static readonly Color Edge = new Color(1f, 1f, 1f, 0.10f);

        public static Font Regular, Semi, Bold;
        public static Texture2D Gear, Arrow, Vignette, Close, Chevron;
        public static GUIStyle Toast, Pill, Touch, BtnListen, BtnSelect, Title, Hint, Big, Unit, Center,
                               Primary, DangerBtn, Chip, ChipOn, IconBtn, Tab, Section, Card, Muted, Value, Row, RowOn, Sheet, Body;

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

            var panel = Round(PanelFill, Edge, 16, 1.2f);
            var card = Round(new Color(1f, 1f, 1f, 0.045f), new Color(1f, 1f, 1f, 0.07f), 12, 1f);
            var slate = Round(SurfaceFill, new Color(1, 1, 1, 0.09f), 11, 1f);
            var slateHot = Round(SurfaceHi, new Color(1, 1, 1, 0.20f), 11, 1f);
            var slateDown = Round(new Color(0.20f, 0.62f, 0.78f, 0.98f), new Color(1, 1, 1, 0.35f), 11, 1f);
            var slateOn = Round(new Color(0.08f, 0.30f, 0.42f, 0.98f), new Color(Accent.r, Accent.g, Accent.b, 0.85f), 11, 1.4f);
            var prim = Round(Accent, new Color(1, 1, 1, 0.35f), 11, 1f);
            var primHot = Round(new Color(0.46f, 0.89f, 1f, 1f), new Color(1, 1, 1, 0.45f), 11, 1f);
            var primDown = Round(new Color(0.16f, 0.66f, 0.82f, 1f), new Color(1, 1, 1, 0.3f), 11, 1f);
            var dng = Round(new Color(0.46f, 0.15f, 0.20f, 0.96f), new Color(Danger.r, Danger.g, Danger.b, 0.55f), 11, 1f);
            var dngHot = Round(new Color(0.62f, 0.19f, 0.25f, 0.98f), new Color(Danger.r, Danger.g, Danger.b, 0.85f), 11, 1f);
            var field = Round(new Color(0f, 0f, 0f, 0.34f), new Color(1, 1, 1, 0.14f), 10, 1f);
            var fieldFocus = Round(new Color(0f, 0f, 0f, 0.46f), new Color(Accent.r, Accent.g, Accent.b, 0.95f), 10, 1.5f);

            // label
            var l = s.label;
            l.font = Regular; l.fontSize = 15; l.richText = true; l.wordWrap = true;
            l.normal.textColor = Text; l.padding = new RectOffset(2, 2, 2, 2); l.margin = new RectOffset(4, 4, 3, 3);

            // box = panel
            var b = s.box;
            b.normal.background = panel; b.border = new RectOffset(16, 16, 16, 16);
            b.padding = new RectOffset(14, 14, 12, 12); b.margin = new RectOffset(0, 0, 0, 0);
            b.normal.textColor = Text; b.font = Semi; b.fontSize = 15; b.alignment = TextAnchor.MiddleCenter; b.richText = true;

            // button
            var bt = s.button;
            bt.normal.background = slate; bt.hover.background = slateHot; bt.active.background = slateDown; bt.focused.background = slate;
            bt.onNormal.background = slateOn; bt.onHover.background = slateOn; bt.onActive.background = slateDown; bt.onFocused.background = slateOn;
            bt.border = new RectOffset(11, 11, 11, 11);
            bt.padding = new RectOffset(14, 14, 8, 8); bt.margin = new RectOffset(3, 3, 3, 3);
            bt.font = Semi; bt.fontSize = 15; bt.alignment = TextAnchor.MiddleCenter; bt.richText = true; bt.wordWrap = false;
            bt.normal.textColor = bt.hover.textColor = bt.focused.textColor = bt.onNormal.textColor = bt.onHover.textColor = bt.onFocused.textColor = Text;
            bt.active.textColor = bt.onActive.textColor = Ink;

            // text field
            var tf = s.textField;
            tf.normal.background = field; tf.hover.background = field; tf.focused.background = fieldFocus; tf.active.background = fieldFocus;
            tf.border = new RectOffset(10, 10, 10, 10); tf.padding = new RectOffset(12, 12, 9, 9); tf.margin = new RectOffset(3, 3, 3, 3);
            tf.font = Regular; tf.fontSize = 16;
            tf.normal.textColor = tf.hover.textColor = tf.focused.textColor = tf.active.textColor = Text;

            // sliders: a tall (touch-sized) transparent strip with a thin rounded groove in the middle, and a big knob
            var track = Groove(8, 28, 5f, new Color(1, 1, 1, 0.20f));
            var knob = Circle(48, Color.white, new Color(Accent.r, Accent.g, Accent.b, 1f), 4f);
            var sl = s.horizontalSlider;
            sl.normal.background = track; sl.border = new RectOffset(6, 6, 0, 0); sl.fixedHeight = 28;
            sl.margin = new RectOffset(6, 6, 4, 8); sl.padding = new RectOffset(0, 0, 0, 0);
            var th = s.horizontalSliderThumb;
            th.normal.background = knob; th.hover.background = knob; th.active.background = knob; th.focused.background = knob;
            th.fixedWidth = 26; th.fixedHeight = 26; th.border = new RectOffset(0, 0, 0, 0);
            th.overflow = new RectOffset(0, 0, 0, 0); th.margin = new RectOffset(0, 0, 1, 0); th.padding = new RectOffset(0, 0, 0, 0);

            // scrollbars (the panels also scroll by dragging, see UiKit.TouchScroll)
            var sbTrack = Round(new Color(1, 1, 1, 0.04f), Color.clear, 4, 0f, 12);
            var sbThumb = Round(new Color(1, 1, 1, 0.26f), Color.clear, 4, 0f, 12);
            var vs = s.verticalScrollbar; vs.normal.background = sbTrack; vs.border = new RectOffset(4, 4, 4, 4); vs.fixedWidth = 8; vs.margin = new RectOffset(4, 0, 0, 0);
            var vt = s.verticalScrollbarThumb; vt.normal.background = sbThumb; vt.hover.background = sbThumb; vt.active.background = sbThumb; vt.border = new RectOffset(4, 4, 4, 4); vt.fixedWidth = 8;
            s.verticalScrollbarUpButton.fixedHeight = 0; s.verticalScrollbarUpButton.fixedWidth = 0;
            s.verticalScrollbarDownButton.fixedHeight = 0; s.verticalScrollbarDownButton.fixedWidth = 0;

            // --- extra styles
            Toast = new GUIStyle(b) { fontSize = 16, padding = new RectOffset(18, 18, 8, 8), wordWrap = true, normal = { background = Round(PanelFill, new Color(Accent.r, Accent.g, Accent.b, 0.45f), 16, 1.2f), textColor = Text } };
            Pill = new GUIStyle(b) { fontSize = 14, padding = new RectOffset(14, 14, 6, 6), wordWrap = true, normal = { background = panel, textColor = TextDim } };
            Touch = new GUIStyle(b)
            {
                normal = { background = Round(new Color(1f, 1f, 1f, 0.06f), new Color(1, 1, 1, 0.20f), 18, 1f), textColor = new Color(1, 1, 1, 0.78f) },
                border = new RectOffset(18, 18, 18, 18), fontSize = 16, padding = new RectOffset(4, 4, 4, 4),
            };
            BtnListen = new GUIStyle(bt) { normal = { background = Round(new Color(1f, 0.74f, 0.2f, 0.95f), new Color(1, 1, 1, 0.5f), 11, 1.2f), textColor = Ink } };
            BtnSelect = new GUIStyle(bt) { normal = { background = slateOn, textColor = Text } };
            Primary = new GUIStyle(bt) { fontSize = 16, fontStyle = FontStyle.Normal, font = Bold, normal = { background = prim, textColor = Ink }, hover = { background = primHot, textColor = Ink }, active = { background = primDown, textColor = Ink }, focused = { background = prim, textColor = Ink } };
            DangerBtn = new GUIStyle(bt) { normal = { background = dng, textColor = Text }, hover = { background = dngHot, textColor = Text }, active = { background = dngHot, textColor = Text } };
            Chip = new GUIStyle(bt)
            {
                fontSize = 15, padding = new RectOffset(10, 10, 0, 0), margin = new RectOffset(0, 0, 0, 0),
                normal = { background = Round(new Color(0.05f, 0.08f, 0.13f, 0.58f), new Color(1, 1, 1, 0.16f), 11, 1f), textColor = Text },
                hover = { background = Round(new Color(0.09f, 0.13f, 0.21f, 0.72f), new Color(1, 1, 1, 0.28f), 11, 1f), textColor = Text },
                active = { background = Round(new Color(0.12f, 0.17f, 0.27f, 0.82f), new Color(1, 1, 1, 0.35f), 11, 1f), textColor = Text },
            };
            ChipOn = new GUIStyle(Chip) { normal = { background = slateOn, textColor = Text }, hover = { background = slateOn, textColor = Text } };
            IconBtn = new GUIStyle(bt) { padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            Tab = new GUIStyle(bt) { fontSize = 15, padding = new RectOffset(8, 8, 6, 6), normal = { textColor = TextDim }, onNormal = { background = slateOn, textColor = Text }, onHover = { background = slateOn, textColor = Text } };
            Row = new GUIStyle(bt) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(14, 10, 6, 6), margin = new RectOffset(0, 0, 2, 2) };
            RowOn = new GUIStyle(Row) { normal = { background = slateOn, textColor = Text }, hover = { background = slateOn, textColor = Text } };
            Title = new GUIStyle(l) { font = Bold, fontSize = 24, fontStyle = FontStyle.Normal, wordWrap = false, margin = new RectOffset(4, 4, 2, 6) };
            Section = new GUIStyle(l) { font = Bold, fontSize = 13, wordWrap = false, margin = new RectOffset(4, 4, 12, 4), normal = { textColor = Accent } };
            Body = new GUIStyle(l) { fontSize = 15 };
            Muted = new GUIStyle(l) { fontSize = 13, normal = { textColor = TextDim } };
            Value = new GUIStyle(l) { font = Semi, fontSize = 15, alignment = TextAnchor.MiddleRight, wordWrap = false, normal = { textColor = Accent } };
            Hint = new GUIStyle(l) { fontSize = 14, alignment = TextAnchor.MiddleCenter, normal = { textColor = TextDim } };
            Big = new GUIStyle(l) { font = Bold, fontSize = 54, alignment = TextAnchor.LowerCenter, wordWrap = false };
            Unit = new GUIStyle(l) { font = Semi, fontSize = 14, alignment = TextAnchor.UpperCenter, wordWrap = false, normal = { textColor = TextDim } };
            Center = new GUIStyle(l) { alignment = TextAnchor.UpperCenter };
            Card = new GUIStyle(b) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(12, 12, 8, 10), margin = new RectOffset(0, 0, 4, 4), normal = { background = card, textColor = Text } };
            Card.border = new RectOffset(12, 12, 12, 12);
            Sheet = new GUIStyle(b);

            Gear = MakeGear(64);
            Arrow = MakeArrow(64);
            Vignette = MakeVignette(64);
            Close = MakeClose(64);
            Chevron = MakeChevron(64);
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

        /// <summary>Slider groove: a w x h strip, transparent except a thin rounded bar in the middle (so the hit area is tall but the line is thin).</summary>
        static Texture2D Groove(int w, int h, float thickness, Color col)
        {
            var t = NewTex(w, h);
            var px = new Color[w * h];
            float cy = h * 0.5f, r = thickness * 0.5f, cx = w * 0.5f, hx = cx - r;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Max(Mathf.Abs(x + 0.5f - cx) - hx, 0f), qy = y + 0.5f - cy;
                    float d = new Vector2(qx, qy).magnitude - r;
                    var c = col; c.a *= Mathf.Clamp01(0.5f - d);
                    px[y * w + x] = c;
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

        /// <summary>Distance from a point to a segment, for the line icons.</summary>
        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        static Texture2D LineIcon(int size, Vector2[][] segs, float halfWidth)
        {
            var t = NewTex(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f); float d = 1e9f;
                    foreach (var s in segs) d = Mathf.Min(d, SegDist(p, s[0], s[1]));
                    px[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(halfWidth - d + 0.5f));
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }

        static Texture2D MakeClose(int size) =>
            LineIcon(size, new[] { new[] { new Vector2(18, 18), new Vector2(46, 46) }, new[] { new Vector2(18, 46), new Vector2(46, 18) } }, 3.6f);

        /// <summary>Down chevron (v), y up in texture space.</summary>
        static Texture2D MakeChevron(int size) =>
            LineIcon(size, new[] { new[] { new Vector2(16, 38), new Vector2(32, 22) }, new[] { new Vector2(32, 22), new Vector2(48, 38) } }, 3.6f);

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
                    px[y * size + x] = new Color(0.02f, 0.04f, 0.08f, Mathf.SmoothStep(0.62f, 1.38f, d) * 0.38f);
                }
            t.SetPixels(px); t.Apply(false, true);
            return t;
        }
    }
}
