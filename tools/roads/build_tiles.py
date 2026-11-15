#!/usr/bin/env python3
"""
Overture Maps transportation (GeoParquet) -> RWR1 road tiles for RocketWorld.

  python3 build_tiles.py --release 2026-10-15.0 --bbox -3.80,40.35,-3.60,40.50 --out ./roads-out
  python3 build_tiles.py --release 2026-10-15.0 --planet --out ./roads-out          (long run: use a big machine, process in bbox slices)
  python3 build_tiles.py --parquet 'local/*.parquet' --release dev --out ./roads-out  (a local extract, for tests)
  python3 build_tiles.py ... --official official.json                                (higher-quality national sources, see README in docs/ROADS.md)

Output: <out>/manifest.json and <out>/tiles/<release>/14/<x>/<y>.rwr (empty tiles are not written: the client treats 404 as "no roads").
Upload <out> to the static host that serves /roads/ (same origin as the game, or a CORS-enabled CDN and ?roads=<url>).
The binary layout is the one RoadTileFormat.cs reads (see Assets/Scripts/Sim/Roads/RoadTileFormat.cs); tools/roads/Program.cs checks both sides agree.
"""
import argparse, json, math, os, struct, sys

Z = 14
Q = 1e7
CLASS = {'motorway': 1, 'trunk': 2, 'primary': 3, 'secondary': 4, 'tertiary': 5, 'residential': 6, 'living_street': 7, 'service': 8,
         'unclassified': 9, 'track': 10, 'pedestrian': 11, 'footway': 12, 'cycleway': 13, 'path': 14, 'steps': 15}
SURF = {'paved': 1, 'asphalt': 2, 'concrete': 3, 'cobblestone': 4, 'paving_stones': 4, 'sett': 4, 'unpaved': 6, 'gravel': 5, 'fine_gravel': 5,
        'compacted': 5, 'dirt': 6, 'ground': 6, 'earth': 6, 'mud': 6, 'sand': 7, 'grass': 8}
F_BRIDGE, F_TUNNEL, F_LINK, F_CONSTR, F_ROUNDABOUT = 1, 2, 4, 8, 16


def fnv(s):
    h = 0x811C9DC5
    for b in s.encode():
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return h or 1


def varu(v):
    out = bytearray()
    while v >= 0x80:
        out.append((v & 0x7F) | 0x80); v >>= 7
    out.append(v); return bytes(out)


def vars_(v):
    return varu(((v << 1) ^ (v >> 31)) & 0xFFFFFFFF)


def encode_tile(release, z, x, y, segs):
    """segs: dicts {id:int, cls:int, surface:int, flags:int, level:int, width:float, lanes:int, conns:[(id,at)], pts:[(lon,lat)]}"""
    b = bytearray(b'RWR1')
    b += struct.pack('<BBHii', 1, z, 0, x, y)
    rel = release.encode('ascii'); b += bytes([len(rel)]) + rel
    b += varu(len(segs))
    for s in segs:
        b += struct.pack('<IBBBbHB', s['id'], s['cls'], s['surface'], s['flags'] & 0xFF, max(-128, min(127, s['level'])), max(0, min(65535, round(s['width'] * 10))), s['lanes'])
        conns = s['conns'][:255]
        b += bytes([len(conns)])
        for cid, at in conns:
            b += struct.pack('<IH', cid, max(0, min(65535, round(at * 65535))))
        pts = s['pts']; b += varu(len(pts))
        px = py = 0
        for i, (lon, lat) in enumerate(pts):
            qx, qy = round(lon * Q), round(lat * Q)
            if i == 0: b += struct.pack('<ii', qx, qy)
            else: b += vars_(qx - px) + vars_(qy - py)
            px, py = qx, qy
    return bytes(b)


def lonlat_tile(lon, lat, z=Z):
    n = 1 << z; lat = max(-85.0511287798, min(85.0511287798, lat)); r = math.radians(lat)
    return int((lon + 180) / 360 * n) % n, min(n - 1, max(0, int((1 - math.log(math.tan(r) + 1 / math.cos(r)) / math.pi) / 2 * n)))


def tiles_of(pts):
    """Every z14 tile a polyline touches (points + samples about every 100 m along the edges)."""
    seen = set()
    for i, (lon, lat) in enumerate(pts):
        seen.add(lonlat_tile(lon, lat))
        if i:
            a = pts[i - 1]; d = math.hypot((lon - a[0]) * 111320 * math.cos(math.radians(lat)), (lat - a[1]) * 110574); k = int(d // 100)
            for j in range(1, k + 1):
                t = j / (k + 1); seen.add(lonlat_tile(a[0] + (lon - a[0]) * t, a[1] + (lat - a[1]) * t))
    return seen


def wkb_line(wkb):
    """LineString (type 2) or the first line of a MultiLineString (type 5) from ISO/OGC WKB."""
    bo = '<' if wkb[0] == 1 else '>'; t = struct.unpack(bo + 'I', wkb[1:5])[0] & 0xFFFF
    off = 5
    if t == 5:
        off = 9; bo = '<' if wkb[off] == 1 else '>'; off += 5
    elif t != 2:
        return None
    n = struct.unpack(bo + 'I', wkb[off:off + 4])[0]; off += 4
    return [struct.unpack(bo + 'dd', wkb[off + 16 * i: off + 16 * i + 16]) for i in range(n)]


def first_value(rules, key='value'):
    if not rules: return None
    for r in rules:
        if r and r.get(key) is not None: return r[key]
    return None


def row_to_segment(r):
    pts = wkb_line(bytes(r['wkb']))
    if not pts or len(pts) < 2: return None
    cls = CLASS.get(r['class'] or '', 0 if (r['class'] or 'unknown') == 'unknown' else 16)
    flags = 0
    for fr in (r.get('road_flags') or []):
        vals = fr.get('values') if isinstance(fr, dict) else None
        for v in (vals or []):
            flags |= {'is_bridge': F_BRIDGE, 'is_tunnel': F_TUNNEL, 'is_link': F_LINK, 'is_under_construction': F_CONSTR}.get(v, 0)
    if flags & F_CONSTR: return None                       # not built yet: no pavement to drive on
    lvl = first_value(r.get('level_rules')) or 0
    conns = []
    for c in (r.get('connectors') or []):
        conns.append((fnv(c['connector_id']), float(c['at'])))
    return dict(id=fnv(r['id']), cls=cls, surface=SURF.get((first_value(r.get('road_surface')) or ''), 0), flags=flags, level=int(lvl),
                width=float(first_value(r.get('width_rules')) or 0), lanes=0, conns=conns, pts=pts)


def load_official(path):
    """Optional higher-quality sources. Each entry must declare its license and that redistribution is allowed; the manifest records the attribution."""
    if not path: return []
    cfg = json.load(open(path)); out = []
    for e in cfg['sources']:
        if not e.get('license') or e.get('redistributable') is not True:
            sys.exit(f"official source '{e.get('name')}' needs license and redistributable:true (check its terms first)")
        gj = json.load(open(e['geojson'])); f = e.get('fields', {}); segs = []
        for ft in gj['features']:
            g = ft['geometry']; lines = [g['coordinates']] if g['type'] == 'LineString' else g['coordinates'] if g['type'] == 'MultiLineString' else []
            for k, pts in enumerate(lines):
                p = ft.get('properties', {}); cname = str(p.get(f.get('class', 'class'), '')).lower()
                segs.append(dict(id=fnv(f"{e['name']}:{p.get(f.get('id', 'id'), id(ft))}:{k}"), cls=e.get('class_map', {}).get(cname, CLASS.get(cname, 9)),
                                 surface=SURF.get(str(p.get(f.get('surface', 'surface'), '')).lower(), 0),
                                 flags=(F_BRIDGE if p.get(f.get('bridge', 'bridge')) in (True, 1, 'yes', 'true') else 0) | (F_TUNNEL if p.get(f.get('tunnel', 'tunnel')) in (True, 1, 'yes', 'true') else 0),
                                 level=0, width=float(p.get(f.get('width', 'width'), 0) or 0), lanes=0, conns=[], pts=[(c[0], c[1]) for c in pts]))
        out.append(dict(name=e['name'], bbox=e['bbox'], license=e['license'], attribution=e.get('attribution', e['name']), segs=segs))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--release', required=True); ap.add_argument('--out', required=True)
    ap.add_argument('--bbox'); ap.add_argument('--planet', action='store_true'); ap.add_argument('--parquet'); ap.add_argument('--official')
    ap.add_argument('--source', default='s3', choices=['s3', 'azure'])
    a = ap.parse_args()
    import duckdb
    con = duckdb.connect(); con.execute('INSTALL spatial; LOAD spatial;')
    if a.parquet: src = f"read_parquet('{a.parquet}', hive_partitioning=1)"
    elif a.source == 's3':
        con.execute("INSTALL httpfs; LOAD httpfs; SET s3_region='us-west-2';"); src = f"read_parquet('s3://overturemaps-us-west-2/release/{a.release}/theme=transportation/type=segment/*', hive_partitioning=1)"
    else:
        con.execute("INSTALL azure; LOAD azure;"); src = f"read_parquet('azure://release/{a.release}/theme=transportation/type=segment/*', hive_partitioning=1)"
    where = "subtype='road'"
    if a.bbox:
        x0, y0, x1, y1 = map(float, a.bbox.split(',')); where += f" AND bbox.xmin<={x1} AND bbox.xmax>={x0} AND bbox.ymin<={y1} AND bbox.ymax>={y0}"
    elif not a.planet and not a.parquet: sys.exit('give --bbox or --planet')
    q = f"SELECT id, class, road_flags, road_surface, width_rules, level_rules, connectors, ST_AsWKB(geometry) AS wkb FROM {src} WHERE {where}"
    cur = con.execute(q); cols = [c[0] for c in cur.description]
    tiles = {}; seen = set(); official = load_official(a.official)
    def inside_official(pt):
        for o in official:
            b = o['bbox']
            if b[0] <= pt[0] <= b[2] and b[1] <= pt[1] <= b[3]: return True
        return False
    n = 0
    while True:
        rows = cur.fetchmany(20000)
        if not rows: break
        for row in rows:
            seg = row_to_segment(dict(zip(cols, row)))
            if not seg or seg['id'] in seen: continue
            if inside_official(seg['pts'][len(seg['pts']) // 2]): continue          # an official source replaces Overture here
            seen.add(seg['id']); n += 1
            for t in tiles_of(seg['pts']): tiles.setdefault(t, []).append(seg)
    for o in official:
        for seg in o['segs']:
            for t in tiles_of(seg['pts']): tiles.setdefault(t, []).append(seg)
    base = os.path.join(a.out, 'tiles', a.release, str(Z))
    for (x, y), segs in tiles.items():
        d = os.path.join(base, str(x)); os.makedirs(d, exist_ok=True)
        open(os.path.join(d, f'{y}.rwr'), 'wb').write(encode_tile(a.release, Z, x, y, segs))
    attr = 'Road data (c) Overture Maps Foundation, derived from OpenStreetMap (c) OpenStreetMap contributors (ODbL) and other sources'
    for o in official: attr += f"; {o['attribution']} ({o['license']})"
    json.dump(dict(release=a.release, schema=1, tileZoom=Z, tiles='tiles/{release}/{z}/{x}/{y}.rwr', attribution=attr, segments=n, tileCount=len(tiles)),
              open(os.path.join(a.out, 'manifest.json'), 'w'), indent=1)
    print(f'{n} segments, {len(tiles)} tiles -> {a.out}')


if __name__ == '__main__':
    main()
