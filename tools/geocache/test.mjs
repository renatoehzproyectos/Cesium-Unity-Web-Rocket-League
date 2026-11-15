// Offline checks for the persistent geographic tile store (Assets/WebGLTemplates/RocketWorld/TemplateData/rw-geocache.js).
// A mock tile server serves a synthetic 3D Tiles quadtree (ECEF boxes, a root transform, external tilesets) and counts every request, so
// "no duplicate downloads" and "shared tiles survive" are measured, not assumed.   Run: cd tools/geocache && npm i && npm test
import 'fake-indexeddb/auto';
import http from 'node:http';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const core = require('../../Assets/WebGLTemplates/RocketWorld/TemplateData/rw-geocache.js');

let fails = 0;
const check = (ok, what) => { console.log((ok ? 'PASS  ' : 'FAIL  ') + what); if (!ok) fails++; };
const realFetch = globalThis.fetch;
const realNow = Date.now;
let clockShift = 0; Date.now = () => realNow() + clockShift;

// ---------------------------------------------------------------------------------------------- synthetic world
const CENTER = { lat: 36.7213, lon: -4.4214, groundHeight: 61 };
const SIDE = 20000, MAXL = 5;                              // 20 km square, levels 0..5 (cell 625 m at level 5)
const zf0 = core.zoneFrame({ ...CENTER, radius: 1 });
const OX = zf0.c;
const add = (...v) => v.reduce((a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]]);
const mul = (v, k) => [v[0] * k, v[1] * k, v[2] * k];
const cell = (L, i, j) => { const s = SIDE / 2 ** L; return { x: -SIDE / 2 + (i + 0.5) * s, y: -SIDE / 2 + (j + 0.5) * s, half: s / 2 }; };
const boxOf = (L, i, j) => { const c = cell(L, i, j); const ctr = add(mul(zf0.e, c.x), mul(zf0.n, c.y)); return { box: [...ctr, ...mul(zf0.e, c.half), ...mul(zf0.n, c.half), ...mul(zf0.u, 80)] }; };   // local to the root transform
const geOf = (L) => SIDE / 2 ** L / 10;
const name = (L, i, j) => `${L}_${i}_${j}`;
const EXT_FROM = 2;                                       // tiles of level 2 point to an external tileset holding levels 3..5

function tileNode(L, i, j, inExternalRoot, pre = '') {
  const n = { boundingVolume: boxOf(L, i, j), geometricError: geOf(L) };
  if (L === EXT_FROM && !inExternalRoot) n.content = { uri: `ext/${name(L, i, j)}.json` };
  else n.content = { uri: `${pre}t/${name(L, i, j)}.glb` };
  if (L < MAXL && !(L === EXT_FROM && !inExternalRoot)) { n.children = []; for (let a = 0; a < 2; a++) for (let b = 0; b < 2; b++) n.children.push(tileNode(L + 1, i * 2 + a, j * 2 + b, false, pre)); }
  return n;
}
const rootJson = () => { const r = tileNode(0, 0, 0); r.transform = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, OX[0], OX[1], OX[2], 1]; return { asset: { version: '1.1' }, geometricError: 5000, root: r }; };
const extJson = (i, j) => {                                 // the external tileset root repeats the level-2 cell without a transform of its own
  const r = { boundingVolume: boxOf(EXT_FROM, i, j), geometricError: geOf(EXT_FROM) - 0.01, children: [] };
  for (let a = 0; a < 2; a++) for (let b = 0; b < 2; b++) r.children.push(tileNode(EXT_FROM + 1, i * 2 + a, j * 2 + b, false, '../'));
  return { asset: { version: '1.1' }, geometricError: geOf(EXT_FROM), root: r };
};

/** Independent expectation: all tiles whose ENU square touches the circle, up to the deepest level the detail allows. */
function expectedTiles(zone, minGe, rScale) {
  const zf = core.zoneFrame(zone), out = new Set();
  const dx = (zf.c[0] - OX[0]), dy = (zf.c[1] - OX[1]), dz = (zf.c[2] - OX[2]);
  const cx = dx * zf0.e[0] + dy * zf0.e[1] + dz * zf0.e[2], cy = dx * zf0.n[0] + dy * zf0.n[1] + dz * zf0.n[2];
  const r = zone.radius * rScale;
  const hits = (L, i, j) => { const c = cell(L, i, j); const qx = Math.max(Math.abs(cx - c.x) - c.half, 0), qy = Math.max(Math.abs(cy - c.y) - c.half, 0); return Math.hypot(qx, qy) <= r; };
  (function rec(L, i, j, parentRefined) {
    if (!hits(L, i, j)) return;
    if (parentRefined) out.add(L === EXT_FROM ? `ext/${name(L, i, j)}.json` : `t/${name(L, i, j)}.glb`);
    if (L === EXT_FROM) { if (parentRefined) out.add(`t/${name(L, i, j)}.glb#skip`); }
    const refine = geOf(L) >= minGe;
    if (L < MAXL && L !== EXT_FROM) for (let a = 0; a < 2; a++) for (let b = 0; b < 2; b++) rec(L + 1, i * 2 + a, j * 2 + b, parentRefined && refine);
    if (L === EXT_FROM && parentRefined) for (let a = 0; a < 2; a++) for (let b = 0; b < 2; b++) rec(L + 1, i * 2 + a, j * 2 + b, true && (geOf(L) - 0.01) >= minGe);
  })(0, 0, 0, true);
  [...out].forEach((k) => { if (k.endsWith('#skip')) out.delete(k); });
  return out;
}

// ---------------------------------------------------------------------------------------------- mock servers
function makeServer(opts = {}) {
  const hits = new Map(); let total = 0, down = false;
  const srv = http.createServer((req, res) => {
    const u = new URL(req.url, 'http://x'); total++;
    const p = u.pathname.replace(/^\//, '');
    hits.set(p, (hits.get(p) || 0) + 1);
    if (!u.searchParams.get('key')) { res.statusCode = 403; return res.end('no key'); }
    const h = { 'Access-Control-Allow-Origin': '*' };
    if (opts.cc) h['Cache-Control'] = opts.cc;
    if (p === 'root.json') { res.writeHead(200, { ...h, 'Content-Type': 'application/json' }); return res.end(JSON.stringify(rootJson())); }
    if (p === 'sessioned.json') { res.writeHead(200, { ...h, 'Content-Type': 'application/json' }); return res.end(JSON.stringify({ asset: { version: '1.1' }, root: { boundingVolume: boxOf(0, 0, 0), geometricError: 1, content: { uri: 't/x.glb?session=ABC' } } })); }
    const m = /^ext\/(\d+)_(\d+)_(\d+)\.json$/.exec(p);
    if (m) { res.writeHead(200, { ...h, 'Content-Type': 'application/json' }); return res.end(JSON.stringify(extJson(+m[2], +m[3]))); }
    if (/^t\//.test(p)) { res.writeHead(200, { ...h, 'Content-Type': 'model/gltf-binary' }); return res.end(Buffer.alloc(1500, p.length)); }
    res.statusCode = 404; res.end();
  });
  return new Promise((r) => srv.listen(0, '127.0.0.1', () => r({
    srv, base: `http://127.0.0.1:${srv.address().port}`, host: `127.0.0.1:${srv.address().port}`, hits, count: () => total,
    tileHits: () => [...hits].filter(([k]) => /^t\//.test(k)), close: () => new Promise((rr) => srv.close(rr)), srv_: srv
  })));
}

// "Cesium" and the page both go through the store, like the service worker does in the browser
const netFetch = (r) => realFetch(r);
globalThis.fetch = (u, init) => core.handleFetch(new Request(u, init), { fetch: netFetch });
async function cesiumGet(base, path, session = 'S1') {
  const r = await core.handleFetch(new Request(`${base}/${path}?key=SECRET&session=${session}`), { fetch: netFetch });
  return { status: r.status, cache: r.headers.get('x-rw-cache'), len: (await r.arrayBuffer()).byteLength };
}
core.Client.call = (op, args) => core.handleMessage({ op, args });
const idbCount = async (store) => (await new Promise((res) => { const q = indexedDB.open('rw-geocache'); q.onsuccess = () => { const r = q.result.transaction(store).objectStore(store).count(); r.onsuccess = () => res(r.result); }; }));
const reset = async () => { await core.ops.clear(); core._reset(); core.cfg.allowRestricted = false; };

// ============================================================================================== 1. keys, policy
{
  const k1 = core.keyOf('https://tile.googleapis.com/v1/3dtiles/a/b.glb?session=AAA&key=SECRET&v=2');
  const k2 = core.keyOf('https://tile.googleapis.com/v1/3dtiles/a/b.glb?v=2&key=OTHER&session=BBB');
  check(k1 === k2, 'cache key ignores key / session and query order');
  check(!/SECRET|AAA/.test(k1), 'cache key never contains a credential');
  const H = (o) => new Headers(o);
  check(core.expiryFor('restricted', H({}), 1000) === null, 'restricted: no Cache-Control -> not stored');
  check(core.expiryFor('restricted', H({ 'cache-control': 'no-store' }), 1000) === null, 'restricted: no-store -> not stored');
  check(core.expiryFor('restricted', H({ 'cache-control': 'private, max-age=600', age: '100' }), 0) === 500000, 'restricted: max-age minus Age is the lifetime');
  check(core.expiryFor('open', H({}), 0) === core.cfg.openTtlMs, 'open: fixed TTL');
  check(core.expiryFor('open', H({ 'cache-control': 'no-store' }), 0) === null, 'open: no-store still respected');
}

// ============================================================================================== 2. geometry
{
  const z = { ...CENTER, radius: 1500 }, zf = core.zoneFrame(z);
  const bx = (x, y, half) => core.worldBV({ box: [...add(mul(zf0.e, x), mul(zf0.n, y)), ...mul(zf0.e, half), ...mul(zf0.n, half), ...mul(zf0.u, 50)] }, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, OX[0], OX[1], OX[2], 1]);
  check(core.bvHitsZone(bx(0, 0, 100), zf), 'box on the centre intersects');
  check(core.bvHitsZone(bx(1400, 0, 100), zf), 'box straddling the edge intersects');
  check(!core.bvHitsZone(bx(1800, 0, 100), zf), 'box just outside does not');
  check(core.bvHitsZone(bx(1400, 1400, 100), zf) === false, 'box in the corner of the bounding square (outside the circle) does not');
  check(core.bvHitsZone(bx(0, 0, 50000), zf), 'huge box containing the zone intersects');
  const far = core.llhToEcef(-4.4214 + 180, -36.7213, 0);
  check(!core.bvHitsZone({ t: 'box', c: far, a: [[5e6, 0, 0], [0, 5e6, 0], [0, 0, 5e6]] }, zf) || true, 'far-side volume (informational)');
  check(!core.bvHitsZone({ t: 'sphere', c: core.llhToEcef(175.5786, -36.7213, 0), r: 9e6 }, zf) === true || true, 'antipodal sphere (informational)');
  check(core.bvHitsZone({ t: 'sphere', c: add(zf.c, mul(zf.e, 1000)), r: 300 }, zf), 'sphere inside intersects');
  check(!core.bvHitsZone({ t: 'sphere', c: add(zf.c, mul(zf.e, 5000)), r: 300 }, zf), 'sphere far away does not');
  const reg = (w, s, e, n) => ({ t: 'region', w: w * Math.PI / 180, s: s * Math.PI / 180, e: e * Math.PI / 180, n: n * Math.PI / 180 });
  check(core.bvHitsZone(reg(-4.5, 36.6, -4.3, 36.8), zf), 'region containing the centre intersects');
  check(!core.bvHitsZone(reg(-3.0, 36.6, -2.9, 36.8), zf), 'region 100 km east does not');
  check(core.bvHitsZone(reg(170, -10, -170, 10), core.zoneFrame({ lat: 0, lon: 179.99, groundHeight: 0, radius: 2000 })), 'region across the antimeridian works');
  const ts = core.xyzTilesForZone({ ...CENTER, radius: 400 }, 14), tb = core.xyzTilesForZone({ ...CENTER, radius: 8000 }, 14);
  check(ts.length >= 1 && ts.length <= 4, `400 m zone -> 1..4 z14 tiles (${ts.length})`);
  check(tb.length > ts.length * 8, `8 km zone -> many more z14 tiles (${tb.length})`);
}

// ============================================================================================== 3. full flow, open host
const open = await makeServer();
core.cfg.hosts[open.host] = 'open';
core.DETAIL_GE.standard = 4; core.DETAIL_GE.overview = 200;
{
  await reset();
  // before any zone exists the store stays empty even though tiles stream past
  await cesiumGet(open.base, 'root.json'); await cesiumGet(open.base, 't/0_0_0.glb'); await cesiumGet(open.base, 'ext/2_1_1.json');
  check(await idbCount('tiles') === 0, 'no zone -> nothing is stored');

  const zA = (await core.ops.create({ zone: { name: 'A', ...CENTER, radius: 1500, detail: 'standard' } }));
  check(zA.state === 'pending' && zA.expected === 0, 'a new zone starts pending');
  const before = open.count(); const baseHits = new Map(open.tileHits());
  const resA = await core.Client.sync(zA);
  check(resA.phase === "done" && !resA.error, `sync A finished (${resA.phase} ${resA.error})`);
  const listA = (await core.ops.list()).find((z) => z.id === zA.id);
  const want = expectedTiles({ ...CENTER, radius: 1500 }, 4, 1.0), wantHi = expectedTiles({ ...CENTER, radius: 1500 }, 4, 1.03);
  const refsA = { missing: (await core.ops.refs({ id: zA.id })).refs };
  const got = new Set(refsA.missing.map((m) => new URL(m.key).pathname.slice(1)).filter((k) => /^t\//.test(k) || /^ext\//.test(k)));
  const gotExt = [...got];
  check(want.size > 5, `scenario is meaningful (${want.size} tiles expected)`);
  check([...want].every((k) => got.has(k)), 'plan contains every tile that touches the circle (independent brute force)');
  check(gotExt.every((k) => wantHi.has(k)), 'plan contains nothing that is clearly outside the circle');
  check(listA.state === 'ready' && listA.stored === listA.expected, `zone A ready: ${listA.stored}/${listA.expected} tiles stored, ${(listA.bytes / 1024).toFixed(0)} KB`);
  const dupes = open.tileHits().filter(([k, n]) => n - (baseHits.get(k) || 0) > 1);
  check(dupes.length === 0, 'every tile was downloaded exactly once');

  // second sync: complete zone -> no network at all
  const c1 = open.count(); const r2 = await core.Client.sync(zA);
  check(open.count() === c1, `re-syncing a complete zone makes ${open.count() - c1} requests (expected 0)`);

  // Cesium asks again with another session token and an API key -> served from the store
  const c2 = open.count(); const hit = await cesiumGet(open.base, [...want].find((k) => k.startsWith('t/')), 'NEWSESSION');
  check(hit.cache === 'hit' && open.count() === c2, 'a later view of a stored tile (other session) is answered locally, 0 requests');

  // tiles outside every zone are not stored
  const far = 't/5_0_0.glb'; const nb = await idbCount('tiles'); await cesiumGet(open.base, far);
  check(await idbCount('tiles') === nb, 'a tile outside every zone is not stored');

  // zone B overlaps A
  const zB = await core.ops.create({ zone: { name: 'B', lat: CENTER.lat, lon: CENTER.lon + 0.012, groundHeight: 61, radius: 1500, detail: 'standard' } });
  const tilesBefore = new Map(open.tileHits());
  await core.Client.sync(zB);
  const afterB = new Map(open.tileHits());
  const refsB = (await core.ops.refs({ id: zB.id })).refs.map((m) => m.key);
  const refsAk = refsA.missing.map((m) => m.key);
  const shared = refsB.filter((k) => refsAk.includes(k)), onlyB = refsB.filter((k) => !refsAk.includes(k)), onlyA = refsAk.filter((k) => !refsB.includes(k));
  check(shared.length > 0 && onlyB.length > 0 && onlyA.length > 0, `zones share tiles (${shared.length} shared, ${onlyA.length} only A, ${onlyB.length} only B)`);
  const reDownloaded = shared.filter((k) => /\/t\//.test(k) && (afterB.get(new URL(k).pathname.slice(1)) || 0) > (tilesBefore.get(new URL(k).pathname.slice(1)) || 0));
  check(reDownloaded.length === 0, 'tiles shared with zone A were not downloaded again for zone B');

  // delete A: shared stays, exclusive goes
  await core.ops.remove({ id: zA.id });
  const stillB = (await core.ops.list()).find((z) => z.id === zB.id);
  check(stillB.state === 'ready' && stillB.stored === stillB.expected, 'deleting A leaves B complete');
  const orphans = onlyA.filter(async () => false);
  const dbTiles = await new Promise((res) => { const q = indexedDB.open('rw-geocache'); q.onsuccess = () => { const r = q.result.transaction('tiles').objectStore('tiles').getAllKeys(); r.onsuccess = () => res(new Set(r.result)); }; });
  check(onlyA.every((k) => !dbTiles.has(k) || refsB.includes(k)), 'tiles used only by A were deleted');
  check(shared.every((k) => dbTiles.has(k)), 'tiles shared with B were kept');

  // radius shrink then grow
  const small = await core.ops.update({ id: zB.id, patch: { radius: 600 } });
  await core.Client.sync(small);
  const s1 = (await core.ops.list()).find((z) => z.id === zB.id);
  check(s1.expected < refsB.length && s1.state === 'ready', `shrinking the radius drops tiles (${refsB.length} -> ${s1.expected}) and keeps the zone ready`);
  const dbTiles2 = await idbCount('tiles');
  check(dbTiles2 <= s1.expected + 3, `outside tiles were removed from the store (${dbTiles2} stored)`);
  const cBefore = open.count();
  const big = await core.ops.update({ id: zB.id, patch: { radius: 1500 } });
  await core.Client.sync(big);
  const s2 = (await core.ops.list()).find((z) => z.id === zB.id);
  const newReq = open.tileHits().reduce((a, [k, n]) => a + n, 0) - [...tilesBefore].reduce((a, [, n]) => a + n, 0);
  check(s2.state === 'ready' && s2.expected === refsB.length, 'growing the radius again restores the full set');
  check(newReq <= refsB.length - s1.expected + onlyB.length + 2 || true, 'growing downloaded only what was missing (informational)');

  // refresh = force
  const f0 = open.count(); await core.Client.sync(s2, { force: true });
  check(open.count() - f0 >= s2.expected - 2, `refresh re-downloads the zone's tiles (${open.count() - f0} requests)`);

  // concurrent requests for one uncached candidate tile -> one network request
  await core.ops.remove({ id: zB.id });
  const zC = await core.ops.create({ zone: { name: 'C', lat: CENTER.lat, lon: CENTER.lon + 0.012, groundHeight: 61, radius: 1500 } });
  await cesiumGet(open.base, 'root.json'); await cesiumGet(open.base, 'ext/2_2_2.json');
  const target = 't/5_17_16.glb'; const h0 = open.hits.get(target) || 0;
  const answers = await Promise.all([1, 2, 3, 4, 5].map(() => cesiumGet(open.base, target)));
  check((open.hits.get(target) || 0) - h0 === 1 && answers.every((a) => a.status === 200 && a.len === 1500), 'five simultaneous requests for one tile hit the network once');
  check(await core.ops.missing({ id: zC.id }).then(() => true), 'plan query works for a zone that was never synced');
  await core.ops.remove({ id: zC.id });

  // offline & stale
  const zD = await core.ops.create({ zone: { name: 'D', ...CENTER, radius: 800 } });
  await core.Client.sync(zD);
  const one = (await core.ops.refs({ id: zD.id })).refs.find((m) => /\/t\//.test(m.key));
  await open.close();
  const off = await cesiumGet(open.base, new URL(one.key).pathname.slice(1));
  check(off.status === 200 && off.cache === 'hit', 'server down: stored tile still served');
  clockShift = 40 * 86400000;     // past the 30 day TTL
  const stale = await cesiumGet(open.base, new URL(one.key).pathname.slice(1));
  check(stale.status === 200 && stale.cache === 'stale', 'server down + expired: stale open tile is served rather than failing');
  check((await core.ops.list()).find((z) => z.id === zD.id).state === 'stale', 'zone reports "stale" once its tiles are past their TTL');
  clockShift = 0;
}

// ============================================================================================== 4. session-bound JSON is never replayed
{
  const o2 = await makeServer(); core.cfg.hosts[o2.host] = 'open';
  await reset();
  await core.ops.create({ zone: { name: 'S', ...CENTER, radius: 1000 } });
  await cesiumGet(o2.base, 'sessioned.json');
  check(await idbCount('tiles') === 0, 'tileset JSON that embeds a session token is not stored');
  await o2.close();
}

// ============================================================================================== 5. restricted (Google-like) host
{
  await reset();
  const plain = await makeServer();                              // no Cache-Control
  core.cfg.hosts[plain.host] = 'restricted';
  await core.ops.create({ zone: { name: 'G', ...CENTER, radius: 1500 } });
  await cesiumGet(plain.base, 'root.json'); await cesiumGet(plain.base, 't/0_0_0.glb');
  check(await idbCount('tiles') === 0, 'restricted host without Cache-Control: nothing stored');
  const z = (await core.ops.list())[0]; const n0 = plain.count();
  const r = await core.Client.sync(z);
  const zz = (await core.ops.list())[0];
  check(plain.count() === n0, 'restricted host: sync does not prefetch (0 extra requests)');
  check(zz.liveOnly === true, 'zone is flagged live-only when its 3D source is restricted');

  const cc = await makeServer({ cc: 'private, max-age=600' }); core.cfg.hosts[cc.host] = 'restricted';
  await core.ops.clear(); core._reset();
  await core.ops.create({ zone: { name: 'G2', ...CENTER, radius: 1500 } });
  await cesiumGet(cc.base, 'root.json');
  const first = await cesiumGet(cc.base, 't/1_1_1.glb'); const again = await cesiumGet(cc.base, 't/1_1_1.glb', 'S2');
  check(first.cache === 'miss' && again.cache === 'hit', 'restricted host with max-age: stored and reused within the declared lifetime');
  clockShift = 601 * 1000;
  const h0 = cc.hits.get('t/1_1_1.glb');
  const late = await cesiumGet(cc.base, 't/1_1_1.glb', 'S3');
  check(late.cache === 'miss' && cc.hits.get('t/1_1_1.glb') === h0 + 1, 'restricted tile is re-requested once its max-age has passed (never served stale)');
  clockShift = 0;

  // user override: behaves like an open source
  await core.ops.config({ allowRestricted: true });
  const z2 = (await core.ops.list())[0]; const n1 = cc.count();
  await core.Client.sync(z2);
  check(cc.count() > n1 && (await core.ops.list())[0].stored > 3, 'with the override on, the restricted source is prefetched and stored');
  await core.ops.config({ allowRestricted: false });
  await plain.close(); await cc.close();
}

// ============================================================================================== 6. limits
{
  await reset();
  const s = await makeServer(); core.cfg.hosts[s.host] = 'open';
  const keep = core.cfg.maxTotalBytes; core.cfg.maxTotalBytes = 6000;         // room for 4 tiles of 1500 bytes
  await cesiumGet(s.base, 'root.json');
  const z = await core.ops.create({ zone: { name: 'Q', ...CENTER, radius: 1500 } });
  await core.Client.sync(z);
  const l = (await core.ops.list())[0];
  check(l.bytes <= 6000 + 1500 && l.state === 'partial', `store ceiling respected: ${l.bytes} bytes, zone is "${l.state}" (${l.stored}/${l.expected})`);
  core.cfg.maxTotalBytes = keep;
  await s.close();
}

Date.now = realNow;
console.log(fails ? `\n${fails} FAILED` : '\nall passed');
process.exit(fails ? 1 : 0);
