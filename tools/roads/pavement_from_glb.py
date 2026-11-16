#!/usr/bin/env python3
"""Builds the road pavement textures (Assets/Resources/Roads/*.png) from the glTF "Road with substance designer" (Gaby4545, CC BY 4.0).

usage: pavement_from_glb.py road.glb [out_dir]

glTF textures: baseColor, normal (OpenGL, +Y), metallicRoughness (G rough, B metal). Unity URP Lit wants metallic in R and smoothness in A, so that map is repacked.
  marked_*  the road exactly as authored (white edge lines + yellow centre line), U runs across the road, V along it, 1024 px = 2x the width.
  plain_*   asphalt only (no markings): a clean strip next to the edge line, mirrored so it tiles sideways too (normal map X inverted on the mirrored half).
"""
import io, json, struct, sys, os
import numpy as np
from PIL import Image

def images(path):
    b = open(path, 'rb').read(); off = 12; js = None; binc = None
    while off < len(b):
        n, t = struct.unpack('<II', b[off:off + 8]); d = b[off + 8:off + 8 + n]
        if t == 0x4E4F534A: js = json.loads(d)
        else: binc = d
        off += 8 + n
    mat = js['materials'][0]; tex = js['textures']
    def src(i): return tex[i]['extensions']['EXT_texture_webp']['source']
    def load(i):
        bv = js['bufferViews'][js['images'][src(i)]['bufferView']]; o = bv.get('byteOffset', 0)
        return np.asarray(Image.open(io.BytesIO(binc[o:o + bv['byteLength']])).convert('RGB'))
    return load(mat['pbrMetallicRoughness']['baseColorTexture']['index']), load(mat['normalTexture']['index']), load(mat['pbrMetallicRoughness']['metallicRoughnessTexture']['index'])

def mask(mr):   # R metallic, A smoothness
    out = np.zeros(mr.shape[:2] + (4,), np.uint8); out[..., 0] = mr[..., 2]; out[..., 3] = 255 - mr[..., 1]; return out

def plain(a, normal=False, x0=40, x1=236, flatten=False):
    c = a[:, x0:x1].astype(np.float32)
    if flatten:     # take out the wheel-track / edge-line shading across the road so the mirrored strip tiles without visible stripes
        lum = c.mean(axis=(0, 2)); k = np.clip(lum.mean() / np.maximum(lum, 1), 0.8, 1.25)
        k = np.convolve(np.pad(k, 4, mode='edge'), np.ones(9) / 9, mode='valid')[None, :, None]
        c = np.clip(c * k, 0, 255)
    c = c.astype(np.uint8); f = c[:, ::-1].copy()
    if normal: f[..., 0] = 255 - f[..., 0]
    both = np.concatenate([c, f], axis=1)
    return np.asarray(Image.fromarray(both).resize((512, both.shape[0]), Image.LANCZOS))

def save(a, p): Image.fromarray(a).save(p, optimize=True)

if __name__ == '__main__':
    glb = sys.argv[1]; out = sys.argv[2] if len(sys.argv) > 2 else 'Assets/Resources/Roads'; os.makedirs(out, exist_ok=True)
    base, nrm, mr = images(glb); m = mask(mr)
    save(base, f'{out}/marked_d.png'); save(nrm, f'{out}/marked_n.png'); save(m, f'{out}/marked_m.png')
    save(plain(base, flatten=True), f'{out}/plain_d.png'); save(plain(nrm, True), f'{out}/plain_n.png'); save(plain(m), f'{out}/plain_m.png')
    print('written to', out)
