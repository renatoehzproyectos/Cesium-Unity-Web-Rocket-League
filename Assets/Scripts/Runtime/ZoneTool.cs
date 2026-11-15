using System.Collections;
using System.Globalization;
using RocketWorld.Sim;
using Unity.Mathematics;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// "Zones" panel: pick a circular zone on the globe (click the ground, set the radius with a slider, see the circle on the surface while
    /// it changes), save it, and manage the saved ones (go there, change the radius, update the stored data, delete). What is stored and
    /// reused is decided by <see cref="GeoCache"/> / the service worker; this class is only the tool and its UI.
    ///
    /// Picking: a physics ray first (exact where the tile colliders exist, i.e. near the car), otherwise the ray against the WGS84 ellipsoid
    /// raised to the current ground height (<see cref="GeoZoneMath.RayEllipsoid"/>). Far from the car the terrain height is not known until
    /// "Save" (the elevation is looked up then), so the circle there can float or sink a little relative to real hills.
    /// </summary>
    public class ZoneTool : MonoBehaviour
    {
        public GameRoot root;
        public GeoCache geo;
        public bool Open { get; private set; }

        enum Tab { New, Saved }
        static readonly string[] Details = { "overview", "standard", "detailed" };
        static readonly string[] DetailNames = { "Overview", "Standard", "Detailed" };

        Tab _tab = Tab.New;
        bool _hasCenter, _saving; double _lat, _lon; float _gh; float _radiusT = GeoZoneMath.SliderFromRadius(500); int _detail = 1;
        string _name = "", _latS = "", _lonS = "";
        string _sel, _confirmDelete; float _selRadiusT; Vector2 _scroll; bool _confirmClear;
        string _toast = ""; float _toastUntil, _nextRefresh;
        bool _pickDown; Vector2 _downPos; float _downTime;
        float _k = 1f; Rect _panel;
        LineRenderer _ring, _pole; Material _mat;
        const int RingPoints = 128;
        readonly Vector3[] _pts = new Vector3[RingPoints];
        GUIStyle _wrap, _sel1, _small;

        public void Toggle() { if (Open) Close(); else OpenPanel(); }
        public void OpenPanel() { Open = true; if (geo != null) geo.Refresh(); }
        public void Close() { Open = false; SetRingVisible(false); }

        /// <summary>True when a screen position (pixels, origin bottom-left) is over the panel, so the camera ignores that drag.</summary>
        public bool Over(Vector2 screenPos)
        {
            if (!Open) return false;
            float k = Mathf.Clamp(Screen.height / 720f, 1f, 2.5f);
            return _panel.Contains(new Vector2(screenPos.x / k, (Screen.height - screenPos.y) / k));
        }

        // ------------------------------------------------------------------------------------------------ picking
        void Update()
        {
            if (!Open || root == null) { SetRingVisible(false); return; }
            if (geo != null && geo.Available && Time.unscaledTime >= _nextRefresh) { _nextRefresh = Time.unscaledTime + (geo.Active.Count > 0 ? 1.5f : 6f); geo.Refresh(); }
            if (_tab == Tab.New) HandlePick();
            UpdateRing();
        }

        void HandlePick()
        {
            Vector2 pos; bool down = false, up = false;
            if (Input.touchCount == 1) { var t = Input.GetTouch(0); pos = t.position; down = t.phase == TouchPhase.Began; up = t.phase == TouchPhase.Ended; }
            else { pos = Input.mousePosition; down = Input.GetMouseButtonDown(0); up = Input.GetMouseButtonUp(0); }
            if (down) { _downPos = pos; _downTime = Time.unscaledTime; _pickDown = !Over(pos) && !(root.hud != null && root.hud.Over(pos)); }
            if (up && _pickDown)
            {
                _pickDown = false;
                if ((pos - _downPos).sqrMagnitude < 36f && Time.unscaledTime - _downTime < 0.4f) PickAt(pos);   // a click, not an orbit drag
            }
        }

        void PickAt(Vector2 screen)
        {
            var cam = root.cam; var geor = root.georeference;
            if (cam == null || geor == null) return;
            var ray = cam.ScreenPointToRay(screen);
            double3 ecef; bool ok = false; float gh = root.settings.groundHeight;
            if (Physics.Raycast(ray, out var hit, 20000f))
            {
                ecef = geor.TransformUnityPositionToEarthCenteredEarthFixed(new double3(hit.point.x, hit.point.y, hit.point.z)); ok = true;
            }
            else
            {
                double3 o = geor.TransformUnityPositionToEarthCenteredEarthFixed(new double3(ray.origin.x, ray.origin.y, ray.origin.z));
                double3 d = math.normalize(geor.TransformUnityDirectionToEarthCenteredEarthFixed(new double3(ray.direction.x, ray.direction.y, ray.direction.z)));
                ok = GeoZoneMath.RayEllipsoid(o.x, o.y, o.z, d.x, d.y, d.z, gh, out double t);
                ecef = o + d * t;
            }
            if (!ok) { Toast("Click on the ground"); return; }
            Geo.EcefToLonLatHeight(ecef.x, ecef.y, ecef.z, out double lon, out double lat, out double h);
            SetCenter(lat, lon, (float)h);
        }

        void SetCenter(double lat, double lon, float gh)
        {
            _lat = lat; _lon = lon; _gh = gh; _hasCenter = true;
            _latS = lat.ToString("0.00000", CultureInfo.InvariantCulture); _lonS = lon.ToString("0.00000", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------------------------------------ circle on the surface
        void EnsureRing()
        {
            if (_ring != null) return;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _mat = new Material(sh) { name = "RW_ZoneRing", renderQueue = 3500 };
            _ring = MakeLine("RW_ZoneRing", RingPoints, true);
            _pole = MakeLine("RW_ZonePole", 2, false);
        }
        LineRenderer MakeLine(string n, int count, bool loop)
        {
            var go = new GameObject(n); go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _mat; lr.useWorldSpace = true; lr.loop = loop; lr.positionCount = count; lr.numCornerVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false; lr.enabled = false;
            return lr;
        }
        void SetRingVisible(bool v) { if (_ring != null) { _ring.enabled = v; _pole.enabled = v; } }

        void UpdateRing()
        {
            double lat, lon, r; float gh; Color col;
            if (_tab == Tab.New && _hasCenter) { lat = _lat; lon = _lon; gh = _gh; r = GeoZoneMath.RadiusFromSlider(_radiusT); col = UiTheme.Accent; }
            else if (_tab == Tab.Saved && geo != null && geo.Find(_sel) is GeoZone z) { lat = z.lat; lon = z.lon; gh = (float)z.groundHeight; r = GeoZoneMath.RadiusFromSlider(_selRadiusT); col = UiTheme.Hot; }
            else { SetRingVisible(false); return; }
            var cam = root.cam; var geor = root.georeference;
            if (cam == null || geor == null) { SetRingVisible(false); return; }
            EnsureRing();

            Vector3 c0 = ToUnity(lat, lon, gh);
            float dist = Vector3.Distance(cam.transform.position, c0);
            if (dist > 60000f) { SetRingVisible(false); return; }              // beyond float precision around the origin: nothing useful to draw
            for (int i = 0; i < RingPoints; i++)
            {
                GeoZoneMath.Destination(lat, lon, i * 360.0 / RingPoints, r, out double la, out double lo);
                Vector3 p = ToUnity(la, lo, gh);
                if (Vector3.Distance(cam.transform.position, p) < 4000f && Physics.Raycast(p + Vector3.up * 300f, Vector3.down, out var h, 700f)) p = h.point + Vector3.up * 0.6f;   // follow the ground where colliders exist
                _pts[i] = p;
            }
            _ring.SetPositions(_pts);
            float w = Mathf.Clamp(dist * 0.006f, 0.4f, 300f);
            _ring.widthMultiplier = w; _pole.widthMultiplier = w * 0.6f;
            _pole.SetPosition(0, c0); _pole.SetPosition(1, c0 + Vector3.up * Mathf.Clamp(dist * 0.08f, 10f, 400f));
            _mat.color = col; if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", col);
            _ring.enabled = _pole.enabled = true;
        }

        Vector3 ToUnity(double lat, double lon, double h)
        {
            Geo.LonLatToEcef(lon, lat, h, out double x, out double y, out double z);
            double3 u = root.georeference.TransformEarthCenteredEarthFixedPositionToUnity(new double3(x, y, z));
            return new Vector3((float)u.x, (float)u.y, (float)u.z);
        }

        // ------------------------------------------------------------------------------------------------ UI
        void Toast(string t) { _toast = t; _toastUntil = Time.unscaledTime + 3.5f; }

        void OnGUI()
        {
            if (!Open || root == null || geo == null) return;
            GUI.depth = -90;
            GUI.skin = UiTheme.Skin;
            _k = Mathf.Clamp(Screen.height / 720f, 1f, 2.5f);
            var oldM = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(_k, _k), Vector2.zero);
            float W = Screen.width / _k, H = Screen.height / _k;
            _panel = new Rect(Mathf.Max(8f, W - 390f), 70f, Mathf.Min(380f, W - 16f), H - 84f);
            _wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 13 };
            _small ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
            _sel1 ??= new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 14 };

            GUILayout.BeginArea(_panel, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Saved zones", UiTheme.Title);
            if (GUILayout.Button("X", GUILayout.Width(34))) { GUILayout.EndHorizontal(); GUILayout.EndArea(); GUI.matrix = oldM; Close(); return; }
            GUILayout.EndHorizontal();

            if (!geo.Available)
            {
                GUILayout.Label("The tile store is not available here.", _wrap);
                GUILayout.Label(string.IsNullOrEmpty(geo.Reason) ? "Starting…" : geo.Reason, _small);
            }
            else
            {
                _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { "New zone", "Saved (" + geo.Zones.Count + ")" });
                _scroll = GUILayout.BeginScrollView(_scroll);
                if (_tab == Tab.New) DrawNew(); else DrawSaved();
                GUILayout.Space(8);
                DrawFooter();
                GUILayout.EndScrollView();
            }
            if (Time.unscaledTime < _toastUntil) GUILayout.Label(_toast, _wrap);
            GUILayout.EndArea();
            GUI.matrix = oldM;
        }

        void DrawNew()
        {
            GUILayout.Label("Click the ground to place the centre of the zone, then set the radius. The circle follows the surface.", _wrap);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lat", GUILayout.Width(28)); string la = GUILayout.TextField(_latS, GUILayout.MinWidth(70));
            GUILayout.Label("Lon", GUILayout.Width(28)); string lo = GUILayout.TextField(_lonS, GUILayout.MinWidth(70));
            GUILayout.EndHorizontal();
            if (la != _latS || lo != _lonS)
            {
                _latS = la; _lonS = lo;
                if (double.TryParse(la, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(lo, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a >= -90 && a <= 90 && b >= -180 && b <= 180)
                { _lat = a; _lon = b; _hasCenter = true; }
            }
            if (GUILayout.Button("Use my position")) SetCenter(root.settings.lat, root.settings.lon, root.settings.groundHeight);

            double r = GeoZoneMath.RadiusFromSlider(_radiusT);
            GUILayout.Label("Radius " + GeoZoneMath.FormatRadius(r));
            _radiusT = GUILayout.HorizontalSlider(_radiusT, 0f, 1f);
            GUILayout.Label("Detail to store");
            _detail = GUILayout.Toolbar(_detail, DetailNames);
            GUILayout.Label(_detail == 0 ? "Coarse tiles only: small download." : _detail == 1 ? "Enough for driving at normal speed." : "Finest tiles: much more data, use small radii.", _small);
            GUILayout.Label("Name");
            _name = GUILayout.TextField(_name, 60);
            GUI.enabled = _hasCenter && !_saving;
            if (GUILayout.Button(_saving ? "Saving…" : "Save zone", GUILayout.Height(34))) StartCoroutine(SaveRoutine());
            GUI.enabled = true;
        }

        IEnumerator SaveRoutine()
        {
            _saving = true;
            float gh = _gh;
            yield return ElevationLookup.Get(_lat, _lon, gh, v => gh = v);
            string nm = string.IsNullOrWhiteSpace(_name) ? "Zone " + (geo.Zones.Count + 1) : _name.Trim();
            geo.Create(nm, _lat, _lon, GeoZoneMath.RadiusFromSlider(_radiusT), gh, Details[_detail], ok =>
            {
                if (ok) { Toast("Zone saved. Storing its tiles…"); _tab = Tab.Saved; _name = ""; _hasCenter = false; }
                else Toast("Could not save the zone: " + geo.LastError);
            });
            _saving = false;
        }

        void DrawSaved()
        {
            if (geo.Zones.Count == 0) { GUILayout.Label("No saved zones yet. Create one in \"New zone\".", _wrap); return; }
            foreach (var z in geo.Zones.ToArray())
            {
                bool sel = z.id == _sel;
                GUILayout.BeginVertical(GUI.skin.box);
                if (GUILayout.Button((sel ? "● " : "○ ") + z.name + "   " + GeoZoneMath.FormatRadius(z.radius), _sel1)) { Select(z); }
                geo.Active.TryGetValue(z.id, out var pr); bool busy = geo.Active.ContainsKey(z.id);
                string line = busy ? (pr.phase == "planning" ? "Finding tiles…" : "Downloading " + pr.done + "/" + pr.total + "  " + GeoZoneMath.FormatBytes(pr.bytes))
                                   : GeoZoneMath.StateLabel(z.state, z.liveOnly, z.stored, z.expected) + "  ·  " + GeoZoneMath.FormatBytes(z.bytes);
                GUILayout.Label(line, _small);
                if (busy && pr.truncated) GUILayout.Label("Stopped at the size limit: the zone is partial.", _small);
                if (sel)
                {
                    GUILayout.Label(z.lat.ToString("0.00000", CultureInfo.InvariantCulture) + ", " + z.lon.ToString("0.00000", CultureInfo.InvariantCulture) + "   " + z.stored + "/" + z.expected + " tiles", _small);
                    double nr = GeoZoneMath.RadiusFromSlider(_selRadiusT);
                    GUILayout.Label("Radius " + GeoZoneMath.FormatRadius(nr) + (Mathf.Abs((float)(nr - z.radius)) > 1f ? "  (was " + GeoZoneMath.FormatRadius(z.radius) + ")" : ""));
                    _selRadiusT = GUILayout.HorizontalSlider(_selRadiusT, 0f, 1f);
                    GUILayout.BeginHorizontal();
                    GUI.enabled = Mathf.Abs((float)(nr - z.radius)) > 1f;
                    if (GUILayout.Button("Apply radius")) { geo.SetRadius(z.id, nr); Toast("Radius changed. Missing tiles are being stored."); }
                    GUI.enabled = true;
                    if (GUILayout.Button("Go there")) { root.TeleportTo(z.lat, z.lon, (float)z.groundHeight); }
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (busy) { if (GUILayout.Button("Cancel")) geo.Cancel(z.id); }
                    else if (GUILayout.Button(z.state == "ready" ? "Update data" : "Download missing")) geo.Sync(z.id, z.state == "ready");
                    if (_confirmDelete == z.id) { if (GUILayout.Button("Confirm delete")) { geo.Remove(z.id); _sel = null; _confirmDelete = null; } }
                    else if (GUILayout.Button("Delete")) _confirmDelete = z.id;
                    GUILayout.EndHorizontal();
                    if (_confirmDelete == z.id) GUILayout.Label("Tiles shared with other zones are kept.", _small);
                }
                GUILayout.EndVertical();
            }
        }

        void Select(GeoZone z)
        {
            _sel = z.id; _selRadiusT = GeoZoneMath.SliderFromRadius(z.radius); _confirmDelete = null;
            root.TeleportTo(z.lat, z.lon, (float)z.groundHeight);        // selecting a zone takes you there
        }

        void DrawFooter()
        {
            GUILayout.Label("Stored: " + geo.StoredTiles + " tiles, " + GeoZoneMath.FormatBytes(geo.StoredBytes)
                + (geo.QuotaBytes > 0 ? " of " + GeoZoneMath.FormatBytes(geo.QuotaBytes) + " available" : "")
                + "\nServed from the store this session: " + geo.Stats.hits + " requests (" + GeoZoneMath.FormatBytes(geo.Stats.bytesCache) + "). Downloaded: " + GeoZoneMath.FormatBytes(geo.Stats.bytesNet) + ".", _small);
            bool allow = GUILayout.Toggle(geo.AllowRestricted, "Keep Google tiles beyond their HTTP cache lifetime and prefetch them");
            if (allow != geo.AllowRestricted) geo.SetAllowRestricted(allow);
            GUILayout.Label(geo.AllowRestricted
                ? "ON: only use this if your Google Maps Platform agreement allows storing Photorealistic 3D Tiles."
                : "OFF (default): Google's terms allow keeping its tiles only as long as the HTTP cache headers say, and no prefetching. Zones then keep OpenStreetMap water tiles for good and reuse Google tiles while they are fresh.", _small);
            if (_confirmClear) { if (GUILayout.Button("Confirm: delete ALL zones and stored tiles")) { geo.ClearAll(); _confirmClear = false; _sel = null; } }
            else if (GUILayout.Button("Clear everything")) _confirmClear = true;
        }

        void OnDestroy() { if (_mat != null) Destroy(_mat); }
    }
}
