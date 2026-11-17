#!/usr/bin/env node
// Optimises + normalises the vehicle GLBs for the web build (nose = +Z, up = +Y, metres, origin = bbox centre).
// usage: node prep.mjs <name|all> <input dir> <output dir>      (needs: npm i @gltf-transform/{core,functions,extensions} meshoptimizer sharp)
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import draco3d from 'draco3dgltf';
import { flatten, uninstance, clearNodeTransform, transformMesh, getBounds, prune, dedup, weld, simplifyPrimitive, joinPrimitives, compactPrimitive, textureCompress } from '@gltf-transform/functions';
import { MeshoptSimplifier } from 'meshoptimizer';
import sharp from 'sharp';
import fs from 'fs';
import path from 'path';

const [, , which = 'all', inDir = '.', outDir = '.'] = process.argv;
await MeshoptSimplifier.ready;
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS).registerDependencies({ 'draco3d.decoder': await draco3d.createDecoderModule() });   // Megaliner is Draco + WebP

const rotY = (deg, s) => { const a = deg * Math.PI / 180, c = Math.cos(a) * s, sn = Math.sin(a) * s; return [c, 0, -sn, 0, 0, s, 0, 0, sn, 0, c, 0, 0, 0, 0, 1]; };
const trans = (x, y, z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];
const tris = p => (p.getIndices()?.getCount() ?? 0) / 3;
const meshNodes = doc => doc.getRoot().listNodes().filter(n => n.getMesh());
const nodeTris = n => n.getMesh().listPrimitives().reduce((a, p) => a + tris(p), 0);
const f2 = v => v.map(x => +x.toFixed(3));

/** Per-triangle split of one primitive into `n` classes (cls(centroid) -> 0..n-1, or -1 to drop). Returns new primitives (null for empty classes). */
function splitPrim(doc, prim, n, cls) {
  const idx = prim.getIndices(), pos = prim.getAttribute('POSITION'), sems = prim.listSemantics();
  const buckets = Array.from({ length: n }, () => []);
  const v = [0, 0, 0];
  for (let t = 0; t < idx.getCount() / 3; t++) {
    const c = [0, 0, 0];
    for (let k = 0; k < 3; k++) { pos.getElement(idx.getScalar(t * 3 + k), v); c[0] += v[0] / 3; c[1] += v[1] / 3; c[2] += v[2] / 3; }
    const k = cls(c); if (k >= 0) buckets[k].push(t);
  }
  return buckets.map(list => {
    if (!list.length) return null;
    const remap = new Map(), order = [], ni = [];
    for (const t of list) for (let k = 0; k < 3; k++) { const o = idx.getScalar(t * 3 + k); let m = remap.get(o); if (m === undefined) { m = order.length; remap.set(o, m); order.push(o); } ni.push(m); }
    const np = doc.createPrimitive().setMaterial(prim.getMaterial()).setMode(prim.getMode());
    np.setIndices(doc.createAccessor().setType('SCALAR').setArray(new Uint32Array(ni)));
    for (const s of sems) {
      const a = prim.getAttribute(s), es = a.getElementSize(), src = a.getArray(), arr = new src.constructor(order.length * es);
      for (let i = 0; i < order.length; i++) for (let k = 0; k < es; k++) arr[i * es + k] = src[order[i] * es + k];
      np.setAttribute(s, doc.createAccessor().setType(a.getType()).setArray(arr).setNormalized(a.getNormalized()));
    }
    return np;
  });
}

function bounds(doc) { const b = getBounds(doc.getRoot().listScenes()[0]); return { min: b.min, max: b.max, size: b.max.map((x, i) => x - b.min[i]), ctr: b.max.map((x, i) => (x + b.min[i]) / 2) }; }

/** Bake every node transform into the vertices, so all meshes sit at the scene root in world space. */
async function bake(doc) {
  await doc.transform(uninstance(), flatten());
  for (const n of meshNodes(doc)) clearNodeTransform(n);
  for (const p of doc.getRoot().listMeshes().flatMap(m => m.listPrimitives())) compactPrimitive(p);
}
function applyMatrix(doc, m) { for (const mesh of doc.getRoot().listMeshes()) transformMesh(mesh, m); }
function dropNodes(doc, pred) { let c = 0; for (const n of meshNodes(doc)) if (pred(n)) { n.dispose(); c++; } return c; }
function meshOf(doc, node, prims, name) { const m = doc.createMesh(name); prims.forEach(p => m.addPrimitive(p)); node.setMesh(m); return node; }

/** Merge every primitive (same material) of the given nodes into one mesh under a new node. */
function mergeNodes(doc, scene, nodes, name) {
  const groups = new Map(), mats = new Map();
  const key = p => (mats.get(p.getMaterial()) ?? mats.set(p.getMaterial(), mats.size).get(p.getMaterial())) + '|' + p.getMode() + '|' +
    p.listSemantics().sort().map(s => s + p.getAttribute(s).getType() + p.getAttribute(s).getComponentType() + p.getAttribute(s).getNormalized()).join(',') +
    '|' + (p.getIndices() ? 'i' : 'n');
  for (const n of nodes) for (const p of n.getMesh().listPrimitives()) { const k = key(p); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(p.clone()); }
  const out = [];
  for (const list of groups.values()) out.push(list.length === 1 ? list[0] : joinPrimitives(list));
  nodes.forEach(n => n.dispose());
  const node = doc.createNode(name); scene.addChild(node);
  return meshOf(doc, node, out, name);
}

/** Some meshes are "triangle soup" (vertices unshared, so the normal simplifier sees no connectivity). Weld by position only, simplify, compact. */
function simplifyHard(prim, ratio, err) {
  const pos = prim.getAttribute('POSITION').getArray(), ia = prim.getIndices();
  const map = new Map(), canon = new Uint32Array(pos.length / 3);
  for (let i = 0; i < canon.length; i++) {
    const k = Math.round(pos[i * 3] * 1e4) + ',' + Math.round(pos[i * 3 + 1] * 1e4) + ',' + Math.round(pos[i * 3 + 2] * 1e4);
    const c = map.get(k); if (c === undefined) { map.set(k, i); canon[i] = i; } else canon[i] = c;
  }
  const idx = new Uint32Array(ia.getCount()); for (let k = 0; k < idx.length; k++) idx[k] = canon[ia.getScalar(k)];
  const target = Math.max(36, Math.floor(idx.length / 3 * ratio) * 3);
  const [out] = MeshoptSimplifier.simplify(idx, pos, 3, target, err, ['Prune']);
  ia.setArray(new Uint32Array(out));
  compactPrimitive(prim);
}

function simplifyAll(doc, targetTris, err = 0.02) {
  const prims = doc.getRoot().listMeshes().flatMap(m => m.listPrimitives());
  const total = prims.reduce((a, p) => a + tris(p), 0);
  const ratio = Math.min(1, targetTris / total);
  if (ratio >= 0.999) return total;
  let dropped = 0;
  for (const p of prims) {
    const before = tris(p); if (before < 400) continue;
    simplifyPrimitive(p, { simplifier: MeshoptSimplifier, ratio, error: err, lockBorder: false });
    const stub = Math.max(0.4, ratio * 2.5);
    if (before > 3000 && tris(p) > before * stub) {
      simplifyHard(p, ratio, err);
      // a few grille / vent detail meshes cannot be simplified at all (non-manifold bar clusters): dropped rather than shipping 30k triangles each
      if (tris(p) > before * stub && tris(p) > 8000) { for (const m of doc.getRoot().listMeshes()) if (m.listPrimitives().includes(p)) m.removePrimitive(p); p.dispose(); dropped++; }
    }
  }
  if (dropped) console.log('simplify: dropped', dropped, 'unsimplifiable detail primitives');
  return total;
}

async function finish(doc, name, targetTris, tex = 1024, err = 0.02) {
  // the game re-creates every material as URP Lit from the BASE COLOUR only (PlaneIntro.ToUrpLit / VehicleVisual), so the other maps are dead weight
  for (const m of doc.getRoot().listMaterials()) {
    m.setNormalTexture(null).setOcclusionTexture(null).setMetallicRoughnessTexture(null).setEmissiveTexture(null);
    for (const e of m.listExtensions()) e.dispose();
  }
  await doc.transform(prune(), weld({ tolerance: 1e-4 }));
  const before = Number.isFinite(targetTris) ? simplifyAll(doc, targetTris, err) : 0;
  await doc.transform(prune(), dedup());
  for (const t of doc.getRoot().listTextures()) if (t.getMimeType() === 'image/webp') {
    const buf = await sharp(Buffer.from(t.getImage())).jpeg({ quality: 82 }).toBuffer();
    t.setImage(new Uint8Array(buf)).setMimeType('image/jpeg');
  }
  await doc.transform(textureCompress({ encoder: sharp, targetFormat: 'jpeg', quality: 80, resize: [tex, tex] }));
  const total = doc.getRoot().listMeshes().flatMap(m => m.listPrimitives()).reduce((a, p) => a + tris(p), 0);
  if (process.env.DBG) for (const m of doc.getRoot().listMeshes()) for (const p of m.listPrimitives()) console.log('  prim', m.getName(), p.getMaterial()?.getName(), tris(p));
  // orientation markers: the game derives "nose" and "up" from these at load time, whatever axis convention the importer applies
  const bm = bounds(doc), scn = doc.getRoot().listScenes()[0];
  scn.addChild(doc.createNode('Marker_Origin').setTranslation([0, 0, 0]));
  scn.addChild(doc.createNode('Marker_Nose').setTranslation([0, 0, bm.max[2]]));
  scn.addChild(doc.createNode('Marker_Up').setTranslation([0, bm.max[1], 0]));
  fs.mkdirSync(outDir, { recursive: true });
  await io.write(path.join(outDir, name + '.glb'), doc);
  const b = bounds(doc);
  const size = fs.statSync(path.join(outDir, name + '.glb')).size;
  console.log(`${name}: tris ${before} -> ${total}, ${(size / 1e6).toFixed(2)} MB, size ${f2(b.size)} min ${f2(b.min)} max ${f2(b.max)}`);
  return { name, tris: total, bytes: size, min: f2(b.min), max: f2(b.max) };
}
function recenter(doc, c) { applyMatrix(doc, trans(-c[0], -c[1], -c[2])); }

const jobs = {
  // ---------------------------------------------------------------------------------------------------------------- F-15E
  async F15E() {
    const doc = await io.read(path.join(inDir, 'f-15e_strike_eagle_-_fighter_jet_-_free.glb'));
    await bake(doc);
    applyMatrix(doc, rotY(-90, 0.1));                      // nose +X -> +Z, 10 cm units -> m
    const scene = doc.getRoot().listScenes()[0];
    const gearDown = meshNodes(doc).filter(n => ['Object_16', 'Object_18'].includes(n.getName()));
    const gearUp = meshNodes(doc).filter(n => n.getName() === 'Object_14');
    const rest = meshNodes(doc).filter(n => !gearDown.includes(n) && !gearUp.includes(n));
    mergeNodes(doc, scene, gearDown, 'Gear_Down'); mergeNodes(doc, scene, gearUp, 'Gear_Up');
    mergeNodes(doc, scene, rest, 'Body');
    const b = bounds(doc); const c = b.ctr; recenter(doc, [c[0], c[1], c[2]]);
    return finish(doc, 'F15E', 36000, 1024);
  },
  // ---------------------------------------------------------------------------------------------------------------- F-35B
  async F35B() {
    const doc = await io.read(path.join(inDir, 'f-35b_lightning_ii.glb'));
    await bake(doc);
    const scene = doc.getRoot().listScenes()[0];
    mergeNodes(doc, scene, meshNodes(doc), 'Body');
    const c = bounds(doc).ctr; recenter(doc, c);
    return finish(doc, 'F35B', 34000, 1024);
  },
  // ---------------------------------------------------------------------------------------------------------------- helicopter
  async Heli() {
    const doc = await io.read(path.join(inDir, 'helicopter.glb'));
    await bake(doc);
    applyMatrix(doc, rotY(90, 12.8 / 1224.4));              // nose -X -> +Z, 12.8 m long
    const scene = doc.getRoot().listScenes()[0];
    mergeNodes(doc, scene, meshNodes(doc), 'Body');
    const c = bounds(doc).ctr; recenter(doc, c);
    return finish(doc, 'Heli', 24000, 1024);
  },
  // ---------------------------------------------------------------------------------------------------------------- DeLorean
  async DeLorean() {
    const doc = await io.read(path.join(inDir, 'delorean_highpoly.glb'));
    await bake(doc);
    const scene = doc.getRoot().listScenes()[0];
    const byName = n => meshNodes(doc).find(x => x.getName() === n);
    const wheels = { F0: [], F1: [], R0: [], R1: [] };           // 0 = glTF -x, 1 = glTF +x
    wheels.F0.push(byName('Object_80'), byName('Object_196')); wheels.F1.push(byName('Object_194'), byName('Object_192'));
    for (const nm of ['Object_183', 'Object_182', 'Object_186']) {                                          // rear rims / tyres: one mesh for both sides
      const node = byName(nm); const prim = node.getMesh().listPrimitives()[0];
      const parts = splitPrim(doc, prim, 3, c => c[0] < -1.2 ? 0 : c[0] > 1.2 ? 1 : 2);
      const mk = (p, tag) => { if (!p) return null; const n = doc.createNode(nm + tag); scene.addChild(n); return meshOf(doc, n, [p], nm + tag); };
      const a = mk(parts[0], '_n'), b = mk(parts[1], '_p'), r = mk(parts[2], '_c');
      if (a) wheels.R0.push(a); if (b) wheels.R1.push(b);
      node.dispose();
    }
    const used = new Set(Object.values(wheels).flat());
    simplifyAll(doc, 9000, 1.5);
    const rest = meshNodes(doc).filter(n => !used.has(n));
    mergeNodes(doc, scene, rest, 'Body');
    const pivots = {};
    for (const [k, list] of Object.entries(wheels)) {
      const m = mergeNodes(doc, scene, list, 'tmp_' + k);
      const b = getBounds(m), p = b.max.map((x, i) => (x + b.min[i]) / 2);
      transformMesh(m.getMesh(), trans(-p[0], -p[1], -p[2]));
      const wn = doc.createNode('Wheel_' + k).setTranslation(p), spin = doc.createNode('Spin');
      scene.addChild(wn); wn.addChild(spin); spin.setMesh(m.getMesh()); m.setMesh(null); m.dispose();
      pivots[k] = p;
    }
    // scale to 4.2 m at the root and move the centre of the body to the origin (wheel nodes keep their pivots in the scaled space)
    const k = 4.2 / 10.84;
    for (const n of scene.listChildren()) { n.setScale([k, k, k]); n.setTranslation(n.getTranslation().map(x => x * k)); }
    for (const w of Object.keys(pivots)) pivots[w] = pivots[w].map(x => x * k);
    const bb = bounds(doc);
    for (const n of scene.listChildren()) { const t = n.getTranslation(); n.setTranslation([t[0] - bb.ctr[0], t[1] - bb.ctr[1], t[2] - bb.ctr[2]]); }
    for (const w of Object.keys(pivots)) pivots[w] = pivots[w].map((x, i) => x - bb.ctr[i]);
    console.log('DeLorean wheel pivots (m):', JSON.stringify(Object.fromEntries(Object.entries(pivots).map(([a, p]) => [a, f2(p)]))));
    const r = await finish(doc, 'DeLorean', Infinity, 1024);
    r.pivots = Object.fromEntries(Object.entries(pivots).map(([a, p]) => [a, f2(p)]));
    return r;
  },
  // ---------------------------------------------------------------------------------------------------------------- Starship (rocket)
  async Starship() {
    const doc = await io.read(path.join(inDir, 'starship_mk1.glb'));
    await bake(doc);
    // world axes depend on the exporter (Sketchfab node rotations): find the long axis; the nose is the narrower end
    const pts = []; for (const m of doc.getRoot().listMeshes()) for (const pr of m.listPrimitives()) { const a = pr.getAttribute('POSITION').getArray(); for (let i = 0; i < a.length; i += 3) pts.push([a[i], a[i + 1], a[i + 2]]); }
    const lo = [1e9, 1e9, 1e9], hi = [-1e9, -1e9, -1e9]; for (const q of pts) for (let k = 0; k < 3; k++) { lo[k] = Math.min(lo[k], q[k]); hi[k] = Math.max(hi[k], q[k]); }
    const ext = hi.map((h, k) => h - lo[k]); const ax = ext.indexOf(Math.max(...ext)), o = [0, 1, 2].filter(k => k !== ax);
    const radius = (end) => { let s = 0, n = 0; for (const q of pts) { const t = (q[ax] - lo[ax]) / ext[ax]; if (end ? t > 0.9 : t < 0.1) { s += Math.hypot(q[o[0]] - (lo[o[0]] + hi[o[0]]) / 2, q[o[1]] - (lo[o[1]] + hi[o[1]]) / 2); n++; } } return s / Math.max(n, 1); };
    const sign = radius(1) < radius(0) ? 1 : -1;
    const Rz = [0, 0, 0]; Rz[ax] = sign; const Ry = [0, 0, 0]; Ry[o[0]] = 1;
    const Rx = [Ry[1] * Rz[2] - Ry[2] * Rz[1], Ry[2] * Rz[0] - Ry[0] * Rz[2], Ry[0] * Rz[1] - Ry[1] * Rz[0]];
    const s = 50 / ext[ax];                                // Starship Mk1: 50 m tall
    console.log('Starship long axis', ax, 'nose sign', sign, 'scale', s);
    applyMatrix(doc, [Rx[0] * s, Ry[0] * s, Rz[0] * s, 0, Rx[1] * s, Ry[1] * s, Rz[1] * s, 0, Rx[2] * s, Ry[2] * s, Rz[2] * s, 0, 0, 0, 0, 1]);
    const scene = doc.getRoot().listScenes()[0];
    mergeNodes(doc, scene, meshNodes(doc), 'Body');
    recenter(doc, bounds(doc).ctr);
    return finish(doc, 'Starship', Infinity, 1024);
  },
  // ---------------------------------------------------------------------------------------------------------------- Megaliner (giant cruise airliner)
  async Megaliner() {
    const STATUE_M = 93;                                   // Statue of Liberty, ground to torch. The airliner is scaled so its overall HEIGHT matches (=> ~295 m long, ~307 m span)
    const doc = await io.read(path.join(inDir, 'Meshy_AI_Skyborne_Megaliner_1116172811_texture-compressed__1_.glb'));
    for (const e of doc.getRoot().listExtensionsUsed()) if (/draco|webp/i.test(e.extensionName)) e.dispose();   // decoded already; write plain
    await bake(doc);
    const s = STATUE_M / bounds(doc).size[1];
    applyMatrix(doc, [0, 0, -s, 0, 0, s, 0, 0, s, 0, 0, 0, 0, 0, 0, 1]);   // nose -X -> +Z, up stays +Y
    const scene = doc.getRoot().listScenes()[0];
    mergeNodes(doc, scene, meshNodes(doc), 'Body');
    recenter(doc, bounds(doc).ctr);
    return finish(doc, 'Megaliner', 70000, 1024, 0.01);
  },
  // ---------------------------------------------------------------------------------------------------------------- Antonov An-124 (rear ramp is its own node)
  async Antonov() {
    const doc = await io.read(path.join(inDir, 'antonov.glb'));
    await bake(doc);
    const s = 69.1 / bounds(doc).size[2];                  // An-124 is 69.1 m long (after the Sketchfab rotation baked in: nose +Z, up +Y, 9035 units)
    // the ramp is modelled lowered (about 14 deg down) and welded into four meshes: pull its triangles out (rear, low, between the side walls)
    const RAMP_NAMES = ['Object_5', 'Object_13', 'Object_21', 'Object_23'], rampPrims = [];
    for (const n of meshNodes(doc)) {
      const mesh = n.getMesh(); if (!RAMP_NAMES.includes(mesh.getName())) continue;
      const keep = [];
      for (const p of mesh.listPrimitives()) {
        const [rest, ramp] = splitPrim(doc, p, 2, c => (c[2] < -1500 && c[1] < 430 && Math.abs(c[0]) < 400) ? 1 : 0);
        if (rest) keep.push(rest); if (ramp) rampPrims.push(ramp);
      }
      const nm = doc.createMesh(mesh.getName() + '_rest'); keep.forEach(p => nm.addPrimitive(p)); n.setMesh(nm);
    }
    console.log('Antonov ramp triangles:', rampPrims.reduce((a, p) => a + tris(p), 0));
    const scene = doc.getRoot().listScenes()[0];
    const rampNode = doc.createNode('Ramp_tmp'); scene.addChild(rampNode); meshOf(doc, rampNode, rampPrims, 'Ramp');
    mergeNodes(doc, scene, meshNodes(doc).filter(n => n !== rampNode), 'Body');
    applyMatrix(doc, [s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0, 0, 0, 0, 1]);   // units -> m
    const c = bounds(doc).ctr; recenter(doc, c);
    // hinge (model 0, 1581, 384) -> pivot of the node "Ramp": closed = +37 deg about X (up), open = 0
    const hinge = [0 - c[0], 384 * s - c[1], -1581 * s - c[2]];
    transformMesh(rampNode.getMesh(), trans(-hinge[0], -hinge[1], -hinge[2]));
    rampNode.setName('Ramp').setTranslation(hinge);
    const r = await finish(doc, 'Antonov', Infinity, 1024);
    r.hinge = f2(hinge);
    console.log('Antonov ramp hinge (m):', f2(hinge), 'scale', s);
    return r;
  },
};

const names = which === 'all' ? Object.keys(jobs) : [which];
const meta = {};
for (const n of names) { const r = await jobs[n](); if (r) meta[n] = r; }
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'meta.' + (which === 'all' ? 'all' : which) + '.json'), JSON.stringify(meta, null, 1));
