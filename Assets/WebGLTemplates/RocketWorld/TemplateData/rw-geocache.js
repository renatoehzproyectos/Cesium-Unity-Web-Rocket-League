/*
 * Rocket World - persistent geographic tile store ("saved zones").
 *
 * One file, three consumers:
 *   - the service worker (rw-geocache-sw.js) importScripts() it and calls RWGeo.handleFetch / RWGeo.handleMessage;
 *   - the page loads it with a <script> tag and uses RWGeo.Client / RWGeo.bridge (the Unity side talks to the bridge);
 *   - Node (tools/geocache/test.mjs) requires it with fake-indexeddb.
 *
 * Why a service worker: tiles are requested by cesium-native inside the WASM module (and OpenFreeMap tiles by UnityWebRequest), so the
 * browser's fetch layer is the one place every tile request passes through, whatever asked for it. Nothing in the game's C# has to change
 * how it loads tiles: a tile that is already stored is answered from IndexedDB, the rest goes to the network and is stored on the way back
 * when it belongs to a saved zone.
 *
 * Data (IndexedDB "rw-geocache"):
 *   tiles  key -> body (ArrayBuffer), content type, size, stored time, expiry, last use
 *   zones  id  -> name, lat, lon, radius, ground height, detail, timestamps
 *   refs   (zoneId, key) -> which tiles a zone needs. A tile is deleted only when no zone references it any more.
 *   meta   misc (settings, counters)
 *
 * Keep this file free of "{ { {" template macros and of lines that start with a hash sign: it is copied verbatim, but stay safe.
 */
(function (global, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory(global);
  else global.RWGeo = factory(global);
})(typeof self !== 'undefined' ? self : globalThis, function (global) {
  'use strict';

  // ------------------------------------------------------------------------------------------------ configuration
  var DB_NAME = 'rw-geocache', DB_VER = 1, DAY = 86400000;
  var cfg = {
    // host (with port, as in URL.host) -> storage class.
    //   'restricted': Google Map Tiles content. Google's terms forbid pre-fetching, indexing, storing or caching it except as its Cache-Control
    //                 headers allow, so by default it is stored only for the lifetime the response itself declares and is never prefetched.
    //   'open'      : data we may keep (OpenStreetMap vectors, Cesium ion assets that are not Google data, your own tilesets).
    hosts: { 'tile.googleapis.com': 'restricted', 'assets.ion.cesium.com': 'open', 'tiles.openfreemap.org': 'open' },
    allowRestricted: false,      // user override ("I hold a licence that allows storing Google tiles"): restricted hosts behave like open ones
    openTtlMs: 30 * DAY,         // how long an 'open' tile counts as fresh; after that it is refreshed on the next sync (still served offline)
    maxTotalBytes: 1.5e9,        // hard ceiling for the whole store; syncs stop (zone = partial) instead of evicting other zones' tiles
    maxTileBytes: 48e6,          // one response bigger than this is never stored
    maxIndexEntries: 200000,     // in-memory bounding-volume index (rebuilt from the tileset JSON that streams past)
    verticalSlack: 3000,         // m: tiles may sit this far above / below the zone centre and still count
    authParams: ['key', 'session', 'access_token']
  };

  // ------------------------------------------------------------------------------------------------ small utils
  function now() { return Date.now(); }
  function hostOf(u) { try { return new URL(u).host; } catch (e) { return ''; } }
  function classOf(url) {
    var c = cfg.hosts[hostOf(url)];
    if (!c) return null;
    return (c === 'restricted' && cfg.allowRestricted) ? 'open' : c;
  }
  function isRestrictedHost(url) { return cfg.hosts[hostOf(url)] === 'restricted'; }
  function isJsonUrl(url) { try { return /\.json$/i.test(new URL(url).pathname); } catch (e) { return false; } }

  /** Cache key of a URL: credentials and per-session tokens removed, query sorted, no fragment. Never contains an API key. */
  function keyOf(url) {
    var u = new URL(url);
    cfg.authParams.forEach(function (p) { u.searchParams.delete(p); });
    u.searchParams.sort();
    u.hash = '';
    return u.origin + u.pathname + u.search;
  }

  function parseCC(h) {
    var o = {};
    String(h || '').toLowerCase().split(',').forEach(function (p) {
      p = p.trim(); if (!p) return;
      var i = p.indexOf('='), k = i < 0 ? p : p.slice(0, i), v = i < 0 ? true : p.slice(i + 1).replace(/"/g, '');
      o[k] = v;
    });
    return o;
  }

  /**
   * Expiry time (ms epoch) for a response about to be stored, or null = do not store.
   * restricted: only what Cache-Control allows (max-age minus Age), never no-store / no-cache / private-less zero.
   * open      : a fixed TTL, unless the server says no-store.
   */
  function expiryFor(cls, headers, t) {
    var cc = parseCC(headers.get('cache-control'));
    if (cc['no-store']) return null;
    if (cls === 'restricted') {
      if (cc['no-cache']) return null;
      var ma = parseInt(cc['s-maxage'] !== undefined ? cc['s-maxage'] : cc['max-age'], 10);
      if (!(ma > 0)) return null;
      var age = parseInt(headers.get('age') || '0', 10) || 0;
      var left = ma - age;
      return left > 0 ? t + left * 1000 : null;
    }
    return t + cfg.openTtlMs;
  }

  // ------------------------------------------------------------------------------------------------ geometry (ECEF, WGS84)
  var WA = 6378137.0, WF = 1 / 298.257223563, WE2 = WF * (2 - WF), RAD = Math.PI / 180, MEAN_R = 6371008.8;

  function llhToEcef(lonDeg, latDeg, h) {
    var lon = lonDeg * RAD, lat = latDeg * RAD, s = Math.sin(lat), c = Math.cos(lat), n = WA / Math.sqrt(1 - WE2 * s * s);
    return [(n + h) * c * Math.cos(lon), (n + h) * c * Math.sin(lon), (n * (1 - WE2) + h) * s];
  }
  function sub(a, b) { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }
  function dot(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
  function len(a) { return Math.sqrt(dot(a, a)); }

  /** Local frame at a zone centre: ECEF position plus east / north / up unit vectors. */
  function zoneFrame(z) {
    var lon = z.lon * RAD, lat = z.lat * RAD, sl = Math.sin(lon), cl = Math.cos(lon), sp = Math.sin(lat), cp = Math.cos(lat);
    return {
      c: llhToEcef(z.lon, z.lat, z.groundHeight || 0), r: z.radius, latR: lat, lonR: lon,
      e: [-sl, cl, 0], n: [-sp * cl, -sp * sl, cp], u: [cp * cl, cp * sl, sp]
    };
  }

  function mulPoint(m, p) {
    return [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];
  }
  function mulDir(m, p) { return [m[0] * p[0] + m[4] * p[1] + m[8] * p[2], m[1] * p[0] + m[5] * p[1] + m[9] * p[2], m[2] * p[0] + m[6] * p[1] + m[10] * p[2]]; }
  function mulMat(a, b) {            // column-major 4x4: a * b
    var o = new Array(16);
    for (var c = 0; c < 4; c++) for (var r = 0; r < 4; r++) {
      var s = 0; for (var k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
      o[c * 4 + r] = s;
    }
    return o;
  }
  var IDENT = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

  /** Bounding volume of a 3D Tiles tile in ECEF: {t:'box',c,a:[ax,ay,az]} | {t:'sphere',c,r} | {t:'region',w,s,e,n}. */
  function worldBV(bvJson, m) {
    if (!bvJson) return null;
    if (bvJson.box && bvJson.box.length >= 12) {
      var b = bvJson.box;
      return { t: 'box', c: mulPoint(m, [b[0], b[1], b[2]]), a: [mulDir(m, [b[3], b[4], b[5]]), mulDir(m, [b[6], b[7], b[8]]), mulDir(m, [b[9], b[10], b[11]])] };
    }
    if (bvJson.sphere && bvJson.sphere.length >= 4) {
      var s = bvJson.sphere, sc = Math.max(len([m[0], m[1], m[2]]), len([m[4], m[5], m[6]]), len([m[8], m[9], m[10]]));
      return { t: 'sphere', c: mulPoint(m, [s[0], s[1], s[2]]), r: s[3] * sc };
    }
    if (bvJson.region && bvJson.region.length >= 4) { var r = bvJson.region; return { t: 'region', w: r[0], s: r[1], e: r[2], n: r[3] }; }
    return null;
  }

  function hull2(pts) {          // monotone chain, returns CCW polygon
    pts = pts.slice().sort(function (p, q) { return p[0] - q[0] || p[1] - q[1]; });
    if (pts.length < 3) return pts;
    function cross(o, a, b) { return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]); }
    var lo = [], up = [], i;
    for (i = 0; i < pts.length; i++) { while (lo.length >= 2 && cross(lo[lo.length - 2], lo[lo.length - 1], pts[i]) <= 0) lo.pop(); lo.push(pts[i]); }
    for (i = pts.length - 1; i >= 0; i--) { while (up.length >= 2 && cross(up[up.length - 2], up[up.length - 1], pts[i]) <= 0) up.pop(); up.push(pts[i]); }
    lo.pop(); up.pop(); return lo.concat(up);
  }
  function distOriginToPolygon(poly) {      // 0 when the origin is inside the (convex, CCW) polygon
    var n = poly.length; if (n === 0) return Infinity;
    if (n === 1) return Math.hypot(poly[0][0], poly[0][1]);
    var inside = n >= 3, best = Infinity;
    for (var i = 0; i < n; i++) {
      var a = poly[i], b = poly[(i + 1) % n], ex = b[0] - a[0], ey = b[1] - a[1];
      if (inside && (ex * (0 - a[1]) - ey * (0 - a[0])) < 0) inside = false;
      var l2 = ex * ex + ey * ey, t = l2 > 0 ? Math.max(0, Math.min(1, -(a[0] * ex + a[1] * ey) / l2)) : 0;
      best = Math.min(best, Math.hypot(a[0] + t * ex, a[1] + t * ey));
    }
    return inside ? 0 : best;
  }

  function regionHits(bv, zf) {
    var lat = Math.max(bv.s, Math.min(bv.n, zf.latR)), lon = zf.lonR, w = bv.w, e = bv.e;
    var inLon = w <= e ? (lon >= w && lon <= e) : (lon >= w || lon <= e);
    if (!inLon) {
      var dw = Math.abs(Math.atan2(Math.sin(lon - w), Math.cos(lon - w))), de = Math.abs(Math.atan2(Math.sin(lon - e), Math.cos(lon - e)));
      lon = dw < de ? w : e;
    }
    var dLat = lat - zf.latR, dLon = lon - zf.lonR;
    var a = Math.sin(dLat / 2) * Math.sin(dLat / 2) + Math.cos(zf.latR) * Math.cos(lat) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
    return 2 * MEAN_R * Math.asin(Math.min(1, Math.sqrt(a))) <= zf.r * 1.02 + 5;
  }

  /**
   * Does a bounding volume touch the zone's circle? The circle lies on the surface; the test is done in the horizontal (east / north) plane
   * at the zone centre, after a cheap 3D distance rejection that also removes volumes on the far side of the planet.
   */
  function bvHitsZone(bv, zf) {
    if (!bv) return false;
    var reach = zf.r * 1.05 + cfg.verticalSlack;
    if (bv.t === 'region') return regionHits(bv, zf);
    if (bv.t === 'sphere') {
      if (len(sub(bv.c, zf.c)) > bv.r + reach) return false;
      var rel = sub(bv.c, zf.c);
      return Math.hypot(dot(rel, zf.e), dot(rel, zf.n)) <= bv.r + zf.r * 1.02;
    }
    var d = sub(zf.c, bv.c), ex = 0, i;
    for (i = 0; i < 3; i++) {
      var al = len(bv.a[i]); if (al === 0) continue;
      var t = Math.abs(dot(d, bv.a[i])) / al, over = t - al; if (over > 0) ex += over * over;
    }
    if (Math.sqrt(ex) > reach) return false;
    var pts = [];
    for (var sx = -1; sx <= 1; sx += 2) for (var sy = -1; sy <= 1; sy += 2) for (var sz = -1; sz <= 1; sz += 2) {
      var p = [bv.c[0] + sx * bv.a[0][0] + sy * bv.a[1][0] + sz * bv.a[2][0], bv.c[1] + sx * bv.a[0][1] + sy * bv.a[1][1] + sz * bv.a[2][1], bv.c[2] + sx * bv.a[0][2] + sy * bv.a[1][2] + sz * bv.a[2][2]];
      var rp = sub(p, zf.c); pts.push([dot(rp, zf.e), dot(rp, zf.n)]);
    }
    return distOriginToPolygon(hull2(pts)) <= zf.r * 1.02;
  }

  // XYZ vector tiles (OpenFreeMap, slippy-map scheme) -------------------------------------------------------
  function tileLonLatBounds(z, x, y) {
    var n = Math.pow(2, z);
    function lat(yy) { return Math.atan(Math.sinh(Math.PI * (1 - 2 * yy / n))); }
    return { t: 'region', w: (x / n) * 2 * Math.PI - Math.PI, e: ((x + 1) / n) * 2 * Math.PI - Math.PI, n: lat(y), s: lat(y + 1) };
  }
  function xyzFromUrl(url) {
    var m = /\/(\d{1,2})\/(\d+)\/(\d+)\.(?:pbf|mvt)$/i.exec(new URL(url).pathname);
    return m ? { z: +m[1], x: +m[2], y: +m[3] } : null;
  }
  function xyzTilesForZone(zone, z) {
    var zf = zoneFrame(zone), out = [], n = Math.pow(2, z);
    var dLat = (zone.radius / MEAN_R) / RAD, dLon = dLat / Math.max(0.01, Math.cos(zf.latR));
    function tx(lon) { return Math.floor(((lon + 180) / 360) * n); }
    function ty(lat) { lat = Math.max(-85.0511, Math.min(85.0511, lat)) * RAD; return Math.floor((1 - Math.log(Math.tan(lat) + 1 / Math.cos(lat)) / Math.PI) / 2 * n); }
    var x0 = tx(zone.lon - dLon), x1 = tx(zone.lon + dLon), y0 = ty(zone.lat + dLat), y1 = ty(zone.lat - dLat);
    for (var y = Math.max(0, y0); y <= Math.min(n - 1, y1); y++) for (var x = x0; x <= x1; x++) {
      var xx = ((x % n) + n) % n;
      if (bvHitsZone(tileLonLatBounds(z, xx, y), zf)) out.push({ z: z, x: xx, y: y });
    }
    return out;
  }

  // ------------------------------------------------------------------------------------------------ 3D Tiles
  function uriOf(tile) { var c = tile.content; return c ? (c.uri || c.url || null) : null; }
  function urisOf(tile) {
    var o = [], u = uriOf(tile); if (u) o.push(u);
    if (Array.isArray(tile.contents)) tile.contents.forEach(function (c) { var x = c && (c.uri || c.url); if (x) o.push(x); });
    return o;
  }

  /** Every content URI of a tileset with its world bounding volume and geometric error (used for the in-memory index). */
  function indexTileset(json, baseUrl, accT) {
    var out = [];
    (function rec(tile, m) {
      var tm = tile.transform && tile.transform.length === 16 ? mulMat(m, tile.transform) : m;
      var bv = worldBV(tile.boundingVolume, tm);
      urisOf(tile).forEach(function (u) { out.push({ url: new URL(u, baseUrl).href, bv: bv, ge: tile.geometricError || 0, accT: tm, json: isJsonUrl(new URL(u, baseUrl).href) }); });
      (tile.children || []).forEach(function (c) { rec(c, tm); });
    })(json.root, accT || IDENT);
    return out;
  }

  /**
   * Which content does a zone need? A tile's content is needed when its parent was refined at this detail (parent geometric error >=
   * minGe) and it touches the circle. External tilesets (content ending .json) are always followed; they are returned in <b>subtrees</b>
   * with the transform to parse them with. Returns {content:[{url,ge}], subtrees:[{url,accT}]}.
   */
  function planTileset(json, baseUrl, zone, minGe, accT) {
    var zf = zoneFrame(zone), content = [], subtrees = [];
    (function rec(tile, m, parentRefined) {
      var tm = tile.transform && tile.transform.length === 16 ? mulMat(m, tile.transform) : m;
      var bv = worldBV(tile.boundingVolume, tm);
      if (!bvHitsZone(bv, zf)) return;
      var ge = tile.geometricError || 0;
      if (parentRefined) urisOf(tile).forEach(function (u) {
        var abs = new URL(u, baseUrl).href;
        if (isJsonUrl(abs)) subtrees.push({ url: abs, accT: tm }); else content.push({ url: abs, ge: ge });
      });
      var refine = ge >= minGe;
      (tile.children || []).forEach(function (c) { rec(c, tm, parentRefined && refine); });
    })(json.root, accT || IDENT, true);
    return { content: content, subtrees: subtrees };
  }

  /** Detail tiers: smallest geometric error (m) worth having. Tune on a real tileset. */
  var DETAIL_GE = { overview: 32, standard: 4, detailed: 0.5 };

  // ------------------------------------------------------------------------------------------------ IndexedDB
  var _dbp = null;
  function idb() { return typeof indexedDB !== 'undefined' ? indexedDB : null; }
  function reqP(r) { return new Promise(function (res, rej) { r.onsuccess = function () { res(r.result); }; r.onerror = function () { rej(r.error); }; }); }
  function openDb() {
    if (_dbp) return _dbp;
    var f = idb(); if (!f) return Promise.reject(new Error('IndexedDB unavailable'));
    _dbp = new Promise(function (res, rej) {
      var o = f.open(DB_NAME, DB_VER);
      o.onupgradeneeded = function () {
        var d = o.result;
        d.createObjectStore('tiles', { keyPath: 'key' });
        d.createObjectStore('zones', { keyPath: 'id' });
        var rf = d.createObjectStore('refs', { keyPath: ['zoneId', 'key'] });
        rf.createIndex('byKey', 'key'); rf.createIndex('byZone', 'zoneId');
        d.createObjectStore('meta', { keyPath: 'k' });
      };
      o.onsuccess = function () { var d = o.result; d.onversionchange = function () { d.close(); _dbp = null; }; res(d); };
      o.onerror = function () { _dbp = null; rej(o.error); };
    });
    return _dbp;
  }
  // helpers that resolve with a value (the transaction callback returns a promise-like holder)
  function get(store, key) { return openDb().then(function (db) { return reqP(db.transaction(store).objectStore(store).get(key)); }); }
  function getAll(store) { return openDb().then(function (db) { return reqP(db.transaction(store).objectStore(store).getAll()); }); }
  function put(store, val) { return openDb().then(function (db) { return reqP(db.transaction(store, 'readwrite').objectStore(store).put(val)); }); }
  function del(store, key) { return openDb().then(function (db) { return reqP(db.transaction(store, 'readwrite').objectStore(store).delete(key)); }); }
  function refsOfZone(id) { return openDb().then(function (db) { return reqP(db.transaction('refs').objectStore('refs').index('byZone').getAll(IDBKeyRange.only(id))); }); }
  function refCountOfKey(key) { return openDb().then(function (db) { return reqP(db.transaction('refs').objectStore('refs').index('byKey').count(IDBKeyRange.only(key))); }); }
  function refsOfKey(key) { return openDb().then(function (db) { return reqP(db.transaction('refs').objectStore('refs').index('byKey').getAll(IDBKeyRange.only(key))); }); }

  // ------------------------------------------------------------------------------------------------ runtime state
  var S = {
    zones: [], zonesLoaded: false,
    index: new Map(),            // tile key -> {bv, ge, accT}: bounding volumes of tileset JSON that streamed past this session
    inflight: new Map(),         // tile key -> Promise<{buf, ct, status}> : one network request per tile, shared by all askers
    auth: {},                    // host -> {params, authorization}: what the live requests carried (memory only, never stored)
    roots: {},                   // host -> tileset root URL seen this session (no secrets)
    templates: {},               // host -> last vector tile URL seen (to rebuild XYZ URLs)
    counters: { hits: 0, misses: 0, stored: 0, deduped: 0, skipped: 0, bytesCache: 0, bytesNet: 0, errors: 0 },
    totalBytes: null, reserved: 0, bc: null
  };

  function loadZones(force) {
    if (S.zonesLoaded && !force) return Promise.resolve(S.zones);
    return getAll('zones').then(function (z) { S.zones = z; S.zonesLoaded = true; return z; });
  }
  function notifyZonesChanged() { try { if (S.bc) S.bc.postMessage('zones'); } catch (e) { } }
  function initChannel() {
    if (S.bc || typeof BroadcastChannel === 'undefined') return;
    try { S.bc = new BroadcastChannel('rw-geocache'); S.bc.onmessage = function () { S.zonesLoaded = false; S.totalBytes = null; }; } catch (e) { }
  }

  function indexAdd(key, entry) {
    if (S.index.size >= cfg.maxIndexEntries) { var n = Math.floor(cfg.maxIndexEntries * 0.2); var it = S.index.keys(); for (var i = 0; i < n; i++) S.index.delete(it.next().value); }
    S.index.set(key, entry);
  }

  function boundsOfKey(key) {
    var e = S.index.get(key); if (e && e.bv) return e.bv;
    var xyz = xyzFromUrl(key); return xyz ? tileLonLatBounds(xyz.z, xyz.x, xyz.y) : null;
  }

  /** Zones whose circle touches this tile (bounding volume from the index, or from the XYZ address). Unknown tiles: none. */
  function zonesTouching(key) {
    var bv = boundsOfKey(key);
    if (!bv) return [];
    return S.zones.filter(function (z) { return bvHitsZone(bv, zoneFrame(z)); });
  }

  // ------------------------------------------------------------------------------------------------ storing / serving
  function totalBytes() {
    if (S.totalBytes !== null) return Promise.resolve(S.totalBytes);
    return getAll('tiles').then(function (rows) { var s = 0; rows.forEach(function (r) { s += r.size; }); S.totalBytes = s; return s; });
  }

  function rowResponse(row, how) {
    return new Response(row.body.slice(0), { status: 200, headers: { 'Content-Type': row.ct || 'application/octet-stream', 'X-RW-Cache': how } });
  }
  function fresh(row, t) { return row.expiresAt === null || t < row.expiresAt; }

  function captureAuth(url, request) {
    var u = new URL(url), h = u.host, p = {}, any = false;
    cfg.authParams.forEach(function (n) { var v = u.searchParams.get(n); if (v) { p[n] = v; any = true; } });
    var a = request && request.headers ? request.headers.get('authorization') : null;
    if (any || a) S.auth[h] = { params: Object.assign({}, (S.auth[h] || {}).params || {}, p), authorization: a || (S.auth[h] || {}).authorization || null };
    if (isJsonUrl(url) && /(root|tileset)\.json$/i.test(u.pathname) && !S.roots[h]) S.roots[h] = u.origin + u.pathname + (function () { var c = new URL(url); cfg.authParams.forEach(function (n) { c.searchParams.delete(n); }); return c.search; })();
    var xyz = xyzFromUrl(url); if (xyz) S.templates[h] = u.origin + u.pathname.replace(/\/\d{1,2}\/\d+\/\d+(\.\w+)$/, '/{z}/{x}/{y}$1');
  }

  function indexJsonBody(url, key, text) {
    var json; try { json = JSON.parse(text); } catch (e) { return null; }
    if (!json || !json.root) return null;
    var prev = S.index.get(key), accT = prev && prev.accT ? prev.accT : IDENT;
    indexTileset(json, url, accT).forEach(function (n) { indexAdd(keyOf(n.url), { bv: n.bv, ge: n.ge, accT: n.accT }); });
    return json;
  }

  /** Decide, store and return. Called only for candidates (the tile belongs to a zone, or is tileset JSON that may lead to one). */
  function storeIfWanted(url, key, buf, ct, headers, cls, isJson, text) {
    var t = now();
    if (S.zones.length === 0) return Promise.resolve(false);
    if (isJson && /[?&]session=/.test(text || '')) { S.counters.skipped++; return Promise.resolve(false); }   // session-bound JSON must not be replayed
    var exp = expiryFor(cls, headers, t);
    if (exp === null) { S.counters.skipped++; return Promise.resolve(false); }     // no-store, or restricted content without a usable max-age
    if (buf.byteLength > cfg.maxTileBytes) { S.counters.skipped++; return Promise.resolve(false); }
    return Promise.all([refsOfKey(key), totalBytes()]).then(function (r) {
      var refs = r[0], total = r[1];
      var touching = zonesTouching(key).filter(function (z) { return !refs.some(function (x) { return x.zoneId === z.id; }); });
      if (refs.length === 0 && touching.length === 0) return false;
      if (total + S.reserved + buf.byteLength > cfg.maxTotalBytes) { S.counters.skipped++; return false; }
      S.reserved += buf.byteLength;       // claimed synchronously: concurrent downloads must not all see the same free space
      var row = { key: key, body: buf, ct: ct, size: buf.byteLength, storedAt: t, expiresAt: exp, lastUsed: t, host: hostOf(url), json: !!isJson };
      return get('tiles', key).then(function (old) {
        return put('tiles', row).then(function () {
          S.totalBytes = (S.totalBytes === null ? total : S.totalBytes) - (old ? old.size : 0) + row.size; S.counters.stored++;
          return openDb().then(function (db) {
            var tr = db.transaction('refs', 'readwrite'), st = tr.objectStore('refs');
            touching.forEach(function (z) { st.put({ zoneId: z.id, key: key, src: 'org', url: key }); });
            return new Promise(function (res, rej) { tr.oncomplete = res; tr.onerror = function () { rej(tr.error); }; });
          });
        });
      }).then(function () { S.reserved -= buf.byteLength; return true; }, function (e) { S.reserved -= buf.byteLength; throw e; });
    }).catch(function (e) { S.counters.errors++; return false; });   // quota or IDB failure: the tile was still delivered
  }

  /**
   * Service worker entry: answer from the store, or go to the network once (shared by every concurrent asker) and store the result when
   * the tile belongs to a saved zone. deps.fetch is injectable for tests.
   */
  function handleFetch(request, deps) {
    var doFetch = (deps && deps.fetch) || function (r) { return fetch(r); };
    var url = request.url, cls = classOf(url);
    if (!cls || (request.method && request.method !== 'GET') || (request.headers && request.headers.get('range'))) return doFetch(request);
    var key = keyOf(url), json = isJsonUrl(url), t = now();
    captureAuth(url, request);
    return loadZones().then(function () {
      return get('tiles', key).catch(function () { return null; });
    }).then(function (row) {
      if (row && fresh(row, t)) {
        S.counters.hits++; S.counters.bytesCache += row.size;
        row.lastUsed = t; put('tiles', row).catch(function () { });
        if (json) indexJsonBody(url, key, new TextDecoder().decode(row.body));
        return rowResponse(row, 'hit');
      }
      var quick = json || (S.zones.length > 0 && zonesTouching(key).length > 0);
      // a tile can also be wanted because a zone's plan references it even when its volume is not in this session's index (service worker restarted)
      return (quick ? Promise.resolve(true) : (S.zones.length > 0 ? refCountOfKey(key).then(function (n) { return n > 0; }) : Promise.resolve(false))).then(function (candidate) {
      if (!candidate) { S.counters.misses++; return doFetch(request); }
      var p = S.inflight.get(key);
      if (p) S.counters.deduped++;
      else {
        S.counters.misses++;
        p = doFetch(request).then(function (resp) {
          if (!(resp.status === 200 || resp.status === 0) || resp.type === 'opaque') return { passthrough: resp };
          var h = resp.headers;
          return resp.arrayBuffer().then(function (buf) { return { buf: buf, ct: h.get('content-type') || '', headers: h, status: 200 }; });
        }).then(function (r) {
          if (r.buf) {
            S.counters.bytesNet += r.buf.byteLength;
            var text = json ? new TextDecoder().decode(r.buf) : '';
            if (json) indexJsonBody(url, key, text);
            return storeIfWanted(url, key, r.buf, r.ct, r.headers, cls, json, text).then(function () { return r; });
          }
          return r;
        });
        S.inflight.set(key, p);
        var done = function () { S.inflight.delete(key); }; p.then(done, done);
      }
      return p.then(function (r) {
        if (r.passthrough) return r.passthrough;
        return new Response(r.buf.slice(0), { status: 200, headers: { 'Content-Type': r.ct || 'application/octet-stream', 'X-RW-Cache': 'miss' } });
      }, function (err) {
        if (row && cls === 'open') { S.counters.hits++; return rowResponse(row, 'stale'); }    // offline: a stale open tile beats an error
        throw err;
      });
      });
    });
  }

  // ------------------------------------------------------------------------------------------------ zones (service worker side)
  function newId() { return 'z_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8); }
  function clampRadius(r) { return Math.max(50, Math.min(50000, +r || 500)); }
  function cleanZone(z, old) {
    var t = now();
    return {
      id: old ? old.id : (z.id || newId()), name: String(z.name || (old && old.name) || 'Zone').slice(0, 60),
      lat: Math.max(-90, Math.min(90, +z.lat)), lon: ((+z.lon + 540) % 360) - 180, radius: clampRadius(z.radius),
      groundHeight: +z.groundHeight || 0, detail: DETAIL_GE[z.detail] !== undefined ? z.detail : (old && old.detail) || 'standard',
      createdAt: old ? old.createdAt : t, updatedAt: t, syncedAt: old ? old.syncedAt || 0 : 0, planned: old ? !!old.planned : false, liveOnly: old ? !!old.liveOnly : false
    };
  }

  /** Delete the tiles in <keys> that no zone references any more (shared tiles stay). Resolves with how many were deleted. */
  function gcKeys(keys) {
    var seen = {}, list = keys.filter(function (k) { if (seen[k]) return false; seen[k] = 1; return true; }), deleted = 0;
    return list.reduce(function (p, k) {
      return p.then(function () {
        return refCountOfKey(k).then(function (n) {
          if (n > 0) return null;
          return get('tiles', k).then(function (r) { if (!r) return null; return del('tiles', k).then(function () { deleted++; }); });
        });
      });
    }, Promise.resolve()).then(function () { S.totalBytes = null; return deleted; });
  }

  function zoneStats(z) {
    var t = now();
    return refsOfZone(z.id).then(function (refs) {
      return Promise.all(refs.map(function (r) { return get('tiles', r.key); })).then(function (rows) {
        var stored = 0, stale = 0, bytes = 0;
        refs.forEach(function (r, i) { var row = rows[i]; if (row) { stored++; bytes += row.size; if (!fresh(row, t)) stale++; } });
        var state = refs.length === 0 ? 'pending' : stored === 0 ? 'partial' : stale > 0 ? 'stale' : stored < refs.length ? 'partial' : 'ready';
        return Object.assign({}, z, { expected: refs.length, stored: stored, stale: stale, bytes: bytes, state: state });
      });
    });
  }

  var ops = {
    config: function (a) {
      if (a && typeof a.allowRestricted === 'boolean') cfg.allowRestricted = a.allowRestricted;
      return put('meta', { k: 'cfg', allowRestricted: cfg.allowRestricted }).then(function () { notifyZonesChanged(); return { allowRestricted: cfg.allowRestricted }; });
    },
    list: function () { return loadZones(true).then(function (zs) { return Promise.all(zs.map(zoneStats)); }); },
    create: function (a) {
      var z = cleanZone(a.zone || a);
      return put('zones', z).then(function () { return loadZones(true); }).then(function () { notifyZonesChanged(); return zoneStats(z); });
    },
    update: function (a) {
      return get('zones', a.id).then(function (old) {
        if (!old) throw new Error('unknown zone');
        var z = cleanZone(Object.assign({}, old, a.patch || {}), old);
        z.planned = false;       // the plan is for the old circle
        return put('zones', z).then(function () { return loadZones(true); }).then(function () { notifyZonesChanged(); return zoneStats(z); });
      });
    },
    /** Replace the planned tile set of a zone. Organic references that still touch the circle (or whose position is unknown) stay. */
    plan: function (a) {
      return get('zones', a.id).then(function (z) {
        if (!z) throw new Error('unknown zone');
        return refsOfZone(a.id).then(function (old) {
          var want = {}, entries = a.entries || [];
          entries.forEach(function (e) { want[e.key] = e; });
          var zf = zoneFrame(z), drop = [], tr;
          old.forEach(function (r) {
            if (want[r.key]) return;
            var bv = boundsOfKey(r.key);
            if (r.src === 'plan' || (bv && !bvHitsZone(bv, zf))) drop.push(r.key);     // old plan entry, or an organic one now outside the circle
          });
          return openDb().then(function (db) {
            tr = db.transaction('refs', 'readwrite'); var st = tr.objectStore('refs');
            drop.forEach(function (k) { st.delete([a.id, k]); });
            entries.forEach(function (e) { st.put({ zoneId: a.id, key: e.key, src: 'plan', url: e.key, kind: e.kind || 'tile' }); });
            return new Promise(function (res, rej) { tr.oncomplete = res; tr.onerror = function () { rej(tr.error); }; });
          }).then(function () { return gcKeys(drop); }).then(function (n) {
            z.planned = true; z.syncedAt = z.syncedAt || 0; z.liveOnly = !!a.liveOnly; z.updatedAt = now();
            return put('zones', z).then(function () { return loadZones(true); }).then(function () { return { dropped: drop.length, freed: n }; });
          });
        });
      });
    },
    /** Plan entries that are not stored (or not fresh). force=true returns every planned entry (refresh). */
    missing: function (a) {
      var t = now();
      return refsOfZone(a.id).then(function (refs) {
        var planned = refs.filter(function (r) { return r.src === 'plan'; });
        return Promise.all(planned.map(function (r) { return get('tiles', r.key); })).then(function (rows) {
          var out = [], marks = [];
          planned.forEach(function (r, i) {
            var row = rows[i];
            // refresh: expire what is stored, so the next request for it goes to the network (and an offline user still gets the old copy)
            if (a.force && row && fresh(row, t)) { row.expiresAt = 0; marks.push(put('tiles', row)); }
            if (a.force || !row || !fresh(row, t)) out.push({ key: r.key, kind: r.kind });
          });
          return Promise.all(marks).then(function () { return { missing: out, total: planned.length }; });
        });
      });
    },
    /** Read-only: the tiles a zone references ({key, src, kind}). */
    refs: function (a) { return refsOfZone(a.id).then(function (r) { return { refs: r.map(function (x) { return { key: x.key, src: x.src, kind: x.kind }; }) }; }); },
    markSynced: function (a) { return get('zones', a.id).then(function (z) { if (!z) return {}; z.syncedAt = now(); return put('zones', z).then(function () { return loadZones(true); }).then(function () { return {}; }); }); },
    remove: function (a) {
      return refsOfZone(a.id).then(function (refs) {
        var keys = refs.map(function (r) { return r.key; });
        return openDb().then(function (db) {
          var tr = db.transaction(['refs', 'zones'], 'readwrite');
          refs.forEach(function (r) { tr.objectStore('refs').delete([a.id, r.key]); });
          tr.objectStore('zones').delete(a.id);
          return new Promise(function (res, rej) { tr.oncomplete = res; tr.onerror = function () { rej(tr.error); }; });
        }).then(function () { return gcKeys(keys); }).then(function () { return loadZones(true); }).then(function () { notifyZonesChanged(); return { removed: a.id, checked: keys.length }; });
      });
    },
    auth: function (a) { var x = S.auth[a.host] || {}; return Promise.resolve({ params: x.params || {}, authorization: x.authorization || null }); },
    roots: function () { return Promise.resolve({ roots: Object.assign({}, S.roots), templates: Object.assign({}, S.templates), hosts: Object.assign({}, cfg.hosts), allowRestricted: cfg.allowRestricted }); },
    stats: function () {
      return Promise.all([getAll('tiles'), loadZones(true)]).then(function (r) {
        var bytes = 0; r[0].forEach(function (x) { bytes += x.size; });
        var est = (typeof navigator !== 'undefined' && navigator.storage && navigator.storage.estimate) ? navigator.storage.estimate().catch(function () { return {}; }) : Promise.resolve({});
        return est.then(function (e) { return { tiles: r[0].length, bytes: bytes, zones: r[1].length, counters: Object.assign({}, S.counters), quota: e.quota || 0, usage: e.usage || 0, maxTotalBytes: cfg.maxTotalBytes, allowRestricted: cfg.allowRestricted }; });
      });
    },
    clear: function () {
      return openDb().then(function (db) {
        var tr = db.transaction(['tiles', 'refs', 'zones'], 'readwrite');
        ['tiles', 'refs', 'zones'].forEach(function (s) { tr.objectStore(s).clear(); });
        return new Promise(function (res, rej) { tr.oncomplete = res; tr.onerror = function () { rej(tr.error); }; });
      }).then(function () { S.totalBytes = 0; S.zones = []; S.index.clear(); notifyZonesChanged(); return {}; });
    }
  };

  function handleMessage(msg) {
    var f = ops[msg && msg.op];
    if (!f) return Promise.reject(new Error('unknown op ' + (msg && msg.op)));
    return loadConfig().then(function () { return f(msg.args || {}); });
  }
  var _cfgLoaded = false;
  function loadConfig() {
    if (_cfgLoaded) return Promise.resolve();
    return get('meta', 'cfg').then(function (m) { if (m && typeof m.allowRestricted === 'boolean') cfg.allowRestricted = m.allowRestricted; _cfgLoaded = true; }).catch(function () { _cfgLoaded = true; });
  }

  // ------------------------------------------------------------------------------------------------ page side
  /**
   * Talks to the service worker and runs the plan / download loop of a zone. The page does the traversal and the downloads because it can
   * run as long as it likes (a service worker may be stopped after a few seconds idle) and can report progress; every request it makes
   * goes through the service worker, which stores the tile because the zone already references it. Only tiles the store does not hold
   * (or holds stale) are requested: a zone that is already complete costs no network at all.
   */
  var Client = (function () {
    var C = { available: false, reason: 'not started', jobs: {}, listeners: [] };
    function emit(ev) { C.listeners.forEach(function (f) { try { f(ev); } catch (e) { } }); }
    C.on = function (f) { C.listeners.push(f); };
    function controller() { return typeof navigator !== 'undefined' && navigator.serviceWorker ? navigator.serviceWorker.controller : null; }

    C.call = function (op, args) {
      return new Promise(function (res, rej) {
        var c = controller(); if (!c) return rej(new Error('the service worker is not controlling this page'));
        var ch = new MessageChannel(), to = setTimeout(function () { rej(new Error('service worker timeout (' + op + ')')); }, 60000);
        ch.port1.onmessage = function (e) { clearTimeout(to); if (e.data && e.data.ok) res(e.data.value); else rej(new Error((e.data && e.data.error) || 'error')); };
        c.postMessage({ op: op, args: args || {} }, [ch.port2]);
      });
    };

    C.init = function (swUrl, scope) {
      if (typeof navigator === 'undefined' || !navigator.serviceWorker) { C.reason = 'Service workers are not available (needs https or localhost)'; return Promise.resolve(C); }
      return navigator.serviceWorker.register(swUrl, { scope: scope }).then(function () { return navigator.serviceWorker.ready; }).then(function () {
        if (controller()) return;
        return new Promise(function (res) { var t = setTimeout(res, 4000); navigator.serviceWorker.addEventListener('controllerchange', function () { clearTimeout(t); res(); }, { once: true }); });
      }).then(function () {
        C.available = !!controller();
        C.reason = C.available ? '' : 'The page is not controlled yet: reload it once';
        return C;
      }).catch(function (e) { C.reason = 'Service worker registration failed: ' + (e && e.message); return C; });
    };

    function withAuth(url, auth) {
      var u = new URL(url);
      Object.keys(auth.params || {}).forEach(function (k) { if (!u.searchParams.has(k)) u.searchParams.set(k, auth.params[k]); });
      return u.href;
    }
    function fetchAuthed(url, auth, asJson) {
      var init = { credentials: 'omit' };
      if (auth.authorization) init.headers = { Authorization: auth.authorization };
      return fetch(withAuth(url, auth), init).then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status + ' ' + r.url.split('?')[0]);
        return asJson ? r.json() : r.arrayBuffer();
      });
    }
    function pool(items, n, worker, stop) {       // run worker(item) with at most n in flight
      var i = 0, failed = 0;
      return new Promise(function (res) {
        var active = 0;
        function next() {
          while (active < n && i < items.length && !(stop && stop())) { (function (it) { active++; worker(it).catch(function () { failed++; }).then(function () { active--; next(); }); })(items[i++]); }
          if (active === 0 && (i >= items.length || (stop && stop()))) res(failed);
        }
        next();
      });
    }

    /** Plan (which tiles does this zone need?) then download the missing ones. Resolves with a summary. */
    C.sync = function (zone, opts) {
      opts = opts || {};
      if (C.jobs[zone.id]) return C.jobs[zone.id].promise;
      var job = { id: zone.id, cancelled: false, phase: 'planning', done: 0, total: 0, bytes: 0, truncated: false, error: '' };
      var maxTiles = opts.maxTiles || 6000, budget = opts.budgetBytes || 400e6, conc = opts.concurrency || 6;
      function prog() { emit({ ev: 'progress', zone: zone.id, phase: job.phase, done: job.done, total: job.total, bytes: job.bytes, truncated: job.truncated, error: job.error }); }
      function stop() { return job.cancelled; }

      job.promise = C.call('roots').then(function (info) {
        var entries = [], liveOnly = false, minGe = DETAIL_GE[zone.detail] !== undefined ? DETAIL_GE[zone.detail] : DETAIL_GE.standard;
        // vector tiles (OpenStreetMap water): plain XYZ arithmetic, no download needed to know which tiles
        Object.keys(info.templates).forEach(function (h) {
          if (info.hosts[h] === 'restricted' && !info.allowRestricted) return;
          xyzTilesForZone(zone, 14).forEach(function (t) {
            var url = info.templates[h].replace('{z}', t.z).replace('{x}', t.x).replace('{y}', t.y);
            entries.push({ key: keyOf(url), kind: 'vector' });
          });
        });
        // 3D tiles: walk the hierarchy from every root this session has seen
        var roots = Object.keys(info.roots).filter(function (h) {
          var restricted = info.hosts[h] === 'restricted' && !info.allowRestricted;
          if (restricted) liveOnly = true;
          return !restricted;
        });
        var seen = {}, queue = [], jsonCount = 0;
        function walk(rootHost) {
          return C.call('auth', { host: rootHost }).then(function (auth) {
            queue.push({ url: info.roots[rootHost], accT: null });
            function one(item) {
              var k = keyOf(item.url); if (seen[k] || job.cancelled) return Promise.resolve();
              seen[k] = 1; jsonCount++;
              return fetchAuthed(item.url, auth, true).then(function (json) {
                entries.push({ key: k, kind: 'json' });
                var p = planTileset(json, item.url, zone, minGe, item.accT || undefined);
                p.content.forEach(function (c) { if (entries.length < maxTiles) entries.push({ key: keyOf(c.url), kind: 'tile' }); else job.truncated = true; });
                p.subtrees.forEach(function (s) { queue.push({ url: s.url, accT: s.accT }); });
              });
            }
            function drain() {
              if (!queue.length || job.cancelled || jsonCount > 400) return Promise.resolve();
              var batch = queue.splice(0, 6);
              return Promise.all(batch.map(function (b) { return one(b).catch(function () { }); })).then(drain);
            }
            return drain();
          });
        }
        return roots.reduce(function (p, h) { return p.then(function () { return walk(h); }); }, Promise.resolve()).then(function () {
          if (job.cancelled) throw new Error('cancelled');
          var uniq = {}, list = []; entries.forEach(function (e) { if (!uniq[e.key]) { uniq[e.key] = 1; list.push(e); } });
          job.total = list.length; prog();
          return C.call('plan', { id: zone.id, entries: list, liveOnly: liveOnly });
        }).then(function () { return info; });
      }).then(function (info) {
        job.phase = 'downloading';
        return C.call('missing', { id: zone.id, force: !!opts.force }).then(function (m) {
          job.total = m.total; job.done = m.total - m.missing.length; prog();
          var authCache = {};
          function authFor(h) { return authCache[h] || (authCache[h] = C.call('auth', { host: h })); }
          return pool(m.missing, conc, function (it) {
            if (job.bytes > budget) { job.truncated = true; return Promise.resolve(); }
            var h = new URL(it.key).host;
            return authFor(h).then(function (a) { return fetchAuthed(it.key, a, false); }).then(function (buf) { job.bytes += buf.byteLength; job.done++; prog(); }, function (e) { if (!job.firstErr) job.firstErr = e && e.message || String(e); throw e; });
          }, function () { return job.cancelled || job.bytes > budget; });
        });
      }).then(function (failed) {
        if (failed) job.error = failed + ' tile(s) failed to download' + (job.firstErr ? ' (' + job.firstErr + ')' : '');
        job.phase = job.cancelled ? 'cancelled' : 'done';
        return C.call('markSynced', { id: zone.id });
      }).catch(function (e) { job.error = e && e.message || String(e); job.phase = job.cancelled ? 'cancelled' : 'error'; }).then(function () {
        delete C.jobs[zone.id]; prog(); emit({ ev: 'zones' });
        return { id: zone.id, phase: job.phase, bytes: job.bytes, truncated: job.truncated, error: job.error };
      });
      C.jobs[zone.id] = job;
      return job.promise;
    };
    C.cancel = function (id) { if (C.jobs[id]) C.jobs[id].cancelled = true; };
    return C;
  })();

  // ------------------------------------------------------------------------------------------------ bridge for Unity (polled, no callbacks)
  var bridge = (function () {
    var out = [];
    function push(o) { out.push(JSON.stringify(o)); }
    Client.on(function (ev) { push(ev); });
    function decorate(z) { var j = Client.jobs[z.id]; if (j) z.syncing = { phase: j.phase, done: j.done, total: j.total, bytes: j.bytes }; return z; }
    var handlers = {
      init: function () { return Client.call('stats').then(function (s) { return { available: true, allowRestricted: s.allowRestricted }; }).catch(function () { return { available: Client.available, reason: Client.reason }; }); },
      list: function () { return Client.call('list').then(function (zs) { return zs.map(decorate); }); },
      create: function (a) {
        return Client.call('create', { zone: a.zone }).then(function (z) { if (a.autosync !== false) Client.sync(z, a.opts); return decorate(z); });
      },
      radius: function (a) { return Client.call('update', { id: a.id, patch: { radius: a.radius } }).then(function (z) { Client.cancel(z.id); setTimeout(function () { Client.sync(z, a.opts); }, 50); return decorate(z); }); },
      rename: function (a) { return Client.call('update', { id: a.id, patch: { name: a.name } }); },
      sync: function (a) { return Client.call('list').then(function (zs) { var z = zs.filter(function (x) { return x.id === a.id; })[0]; if (!z) throw new Error('unknown zone'); Client.sync(z, Object.assign({ force: !!a.force }, a.opts || {})); return { started: true }; }); },
      cancel: function (a) { Client.cancel(a.id); return Promise.resolve({}); },
      remove: function (a) { Client.cancel(a.id); return Client.call('remove', { id: a.id }); },
      stats: function () { return Client.call('stats'); },
      config: function (a) { return Client.call('config', a); },
      clear: function () { return Client.call('clear'); }
    };
    return {
      /** json: {id, op, args}. The answer comes through poll() as {id, ok, v (JSON text of the value)} or {id, ok:false, error}. */
      request: function (json) {
        var m; try { m = JSON.parse(json); } catch (e) { return; }
        var h = handlers[m.op];
        if (!h) { push({ id: m.id, ok: false, error: 'unknown op ' + m.op }); return; }
        Promise.resolve().then(function () { return h(m.args || {}); }).then(function (v) { push({ id: m.id, ok: true, v: JSON.stringify(v === undefined ? null : v) }); }, function (e) { push({ id: m.id, ok: false, error: (e && e.message) || String(e) }); });
      },
      poll: function () { return out.length ? out.shift() : ''; },
      pending: function () { return out.length; }
    };
  })();


  return {
    cfg: cfg, S: S, keyOf: keyOf, classOf: classOf, expiryFor: expiryFor, zoneFrame: zoneFrame, worldBV: worldBV, bvHitsZone: bvHitsZone,
    indexTileset: indexTileset, planTileset: planTileset, xyzTilesForZone: xyzTilesForZone, DETAIL_GE: DETAIL_GE, llhToEcef: llhToEcef,
    Client: Client, bridge: bridge,
    handleFetch: handleFetch, handleMessage: handleMessage, ops: ops, loadConfig: loadConfig, loadZones: loadZones, initChannel: initChannel,
    _reset: function () { S.zones = []; S.zonesLoaded = false; S.index.clear(); S.inflight.clear(); S.auth = {}; S.roots = {}; S.templates = {}; S.totalBytes = null; S.reserved = 0; _cfgLoaded = false; Object.keys(S.counters).forEach(function (k) { S.counters[k] = 0; }); }
  };
});
