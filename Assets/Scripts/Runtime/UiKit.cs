using UnityEngine;

namespace RocketWorld
{
    /// <summary>Small IMGUI building blocks shared by every panel, so they all look and behave the same.</summary>
    public static class UiKit
    {
        // ---- panel header: title on the left, a big close button on the right ----
        public static bool Header(string title)
        {
            bool close = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, UiTheme.Title);
            GUILayout.FlexibleSpace();
            var r = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
            close = IconButton(r, UiTheme.Close);
            GUILayout.EndHorizontal();
            return close;
        }

        /// <summary>A button showing only an icon (tinted with the text colour), centred.</summary>
        public static bool IconButton(Rect r, Texture2D icon, float iconSize = 22f)
        {
            bool hit = GUI.Button(r, GUIContent.none, UiTheme.IconBtn);
            if (Event.current.type == EventType.Repaint && icon != null)
            {
                var old = GUI.color; GUI.color = UiTheme.Text;
                GUI.DrawTexture(new Rect(r.center.x - iconSize * 0.5f, r.center.y - iconSize * 0.5f, iconSize, iconSize), icon);
                GUI.color = old;
            }
            return hit;
        }

        public static void Section(string title) { GUILayout.Label(title.ToUpperInvariant(), UiTheme.Section); }

        public static void Gap(float h = 6f) { GUILayout.Space(h); }

        /// <summary>Label on the left, value on the right, a thick slider underneath.</summary>
        public static float Slider(string label, string valueText, float v, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiTheme.Body);
            GUILayout.FlexibleSpace();
            GUILayout.Label(valueText, UiTheme.Value);
            GUILayout.EndHorizontal();
            return GUILayout.HorizontalSlider(v, min, max);
        }

        /// <summary>A switch row: the whole row is tappable. <paramref name="hint"/> (optional) is a small dim line under the label.</summary>
        public static bool Toggle(bool value, string label, string hint = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(label, UiTheme.Body);
            if (!string.IsNullOrEmpty(hint)) GUILayout.Label(hint, UiTheme.Muted);
            GUILayout.EndVertical();
            GUILayout.Space(6);
            var sw = GUILayoutUtility.GetRect(50, 30, GUILayout.Width(50), GUILayout.Height(30));
            GUILayout.EndHorizontal();
            var row = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                float cy = row.y + Mathf.Min(row.height * 0.5f, 22f);
                var track = new Rect(sw.x, cy - 13f, 48f, 26f);
                GUI.DrawTexture(track, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, value ? UiTheme.Accent : new Color(1f, 1f, 1f, 0.20f), 0f, 13f);
                float kx = value ? track.xMax - 12f - 2f : track.x + 12f + 2f;
                GUI.DrawTexture(new Rect(kx - 10f, cy - 10f, 20f, 20f), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, value ? UiTheme.Ink : Color.white, 0f, 10f);
                GUI.color = old;
            }
            if (GUI.Button(row, GUIContent.none, GUIStyle.none)) value = !value;
            return value;
        }

        public static bool PrimaryButton(string text, float height = 40f) { return GUILayout.Button(text, UiTheme.Primary, GUILayout.Height(height)); }
        public static bool Button(string text, float height = 40f) { return GUILayout.Button(text, GUILayout.Height(height)); }
        public static bool DangerButton(string text, float height = 40f) { return GUILayout.Button(text, UiTheme.DangerBtn, GUILayout.Height(height)); }

        // ---- drag-to-scroll: IMGUI scroll views only react to the mouse wheel or the (thin) scrollbar, which is useless with a finger ----
        static bool _drag, _moved; static float _y0, _s0;

        /// <summary>
        /// Call right after the panel's EndArea (so every control has already had its chance to grab the press): a drag that started on empty panel
        /// space scrolls <paramref name="scroll"/>. Presses on buttons / sliders / text fields never scroll.
        /// </summary>
        public static void TouchScroll(Rect area, ref Vector2 scroll)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    _drag = area.Contains(e.mousePosition) && GUIUtility.hotControl == 0;
                    _moved = false; _y0 = e.mousePosition.y; _s0 = scroll.y;
                    break;
                case EventType.MouseDrag:
                    if (!_drag) break;
                    if (GUIUtility.hotControl != 0 && !_moved) { _drag = false; break; }
                    float dy = e.mousePosition.y - _y0;
                    if (_moved || Mathf.Abs(dy) > 8f) { _moved = true; scroll.y = Mathf.Max(0f, _s0 - dy); e.Use(); }
                    break;
                case EventType.MouseUp:
                case EventType.Ignore:
                    _drag = false;
                    break;
            }
        }
    }
}
