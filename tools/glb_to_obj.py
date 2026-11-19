"""把混元导出的 GLB 转成 Unity 可直接导入的 OBJ + MTL + 贴图。

混元输出特点（实测）：
  - 单个 mesh / 单个 primitive，150 万三角面
  - 无骨骼、无动画（skins=0, animations=0）
  - Z-up、脚在 z=0、头在 z=-1.06，即身高沿 -Z，几何归一化到 ~1 单位

转换时一并做掉坐标系转换，避免 Unity 里再调：
  绕 X 轴 +90°：(x, y, z) -> (x, -z, y)
  脚底 z=0 -> y=0，头 z=-1.06 -> y=+1.06，模型立起来

用法：
  python tools/glb_to_obj.py <input.glb> <out_dir> [--name Character] [--scale 1.0]
"""
import json
import struct
import sys
import os
from pathlib import Path

import numpy as np


def load_glb(path):
    with open(path, 'rb') as f:
        data = f.read()
    magic, ver, total = struct.unpack('<III', data[:12])
    if magic != 0x46546C67:
        raise ValueError('不是合法的 GLB 文件')
    off = 12
    js, binc = None, None
    while off < total:
        clen, ctype = struct.unpack('<II', data[off:off + 8])
        chunk = data[off + 8:off + 8 + clen]
        if ctype == 0x4E4F534A:
            js = json.loads(chunk.decode('utf-8'))
        elif ctype == 0x004E4942:
            binc = chunk
        off += 8 + clen
    return js, binc


_TYPE_NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}
_TYPE_DTYPE = {5120: np.int8, 5121: np.uint8, 5122: np.int16,
               5123: np.uint16, 5125: np.uint32, 5126: np.float32}


def read_accessor(g, binc, idx):
    acc = g['accessors'][idx]
    dtype = np.dtype(_TYPE_DTYPE[acc['componentType']])
    ncomp = _TYPE_NCOMP[acc['type']]
    bv = g['bufferViews'][acc['bufferView']]
    base = bv.get('byteOffset', 0) + acc.get('byteOffset', 0)
    count = acc['count']
    stride = bv.get('byteStride') or dtype.itemsize * ncomp
    if stride == dtype.itemsize * ncomp:
        arr = np.frombuffer(binc, dtype=dtype, count=count * ncomp, offset=base)
        return arr.reshape(count, ncomp)
    out = np.empty((count, ncomp), dtype=dtype)
    for i in range(count):
        out[i] = np.frombuffer(binc, dtype=dtype, count=ncomp, offset=base + i * stride)
    return out


def glb_to_obj(glb_path, out_dir, name='Character', scale=1.0):
    g, binc = load_glb(glb_path)
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    # ---------- 收集几何 ----------
    verts, uvs, norms, faces = [], [], [], []
    vbase = 0
    for mesh in g['meshes']:
        for prim in mesh['primitives']:
            attrs = prim['attributes']
            pos = read_accessor(g, binc, attrs['POSITION']).astype(np.float64)
            n = len(pos)
            verts.append(pos)
            if 'TEXCOORD_0' in attrs:
                uv = read_accessor(g, binc, attrs['TEXCOORD_0']).astype(np.float64)
                uvs.append(uv)
            else:
                uvs.append(np.zeros((n, 2)))
            if 'NORMAL' in attrs:
                nn = read_accessor(g, binc, attrs['NORMAL']).astype(np.float64)
                norms.append(nn)
            else:
                norms.append(np.zeros((n, 3)))

            if 'indices' in prim:
                idx = read_accessor(g, binc, prim['indices']).reshape(-1).astype(np.int64)
            else:
                idx = np.arange(n, dtype=np.int64)
            faces.append(idx.reshape(-1, 3) + vbase)
            vbase += n

    verts = np.vstack(verts)
    uvs = np.vstack(uvs)
    norms = np.vstack(norms)
    faces = np.vstack(faces)

    # ---------- 坐标系：Z-up(-Z 为身高方向) -> Y-up ----------
    # 绕 X 轴 +90°: (x, y, z) -> (x, -z, y)
    px, py, pz = verts[:, 0], verts[:, 1], verts[:, 2]
    verts = np.stack([px, -pz, py], axis=1) * scale

    nx, ny, nz = norms[:, 0], norms[:, 1], norms[:, 2]
    norms = np.stack([nx, -nz, ny], axis=1)

    # 落地：脚底归到 y = 0
    floor = verts[:, 1].min()
    verts[:, 1] -= floor
    height = verts[:, 1].max()

    # ---------- 贴图 ----------
    tex_names = {}
    for i, img in enumerate(g.get('images', [])):
        bv = g['bufferViews'][img['bufferView']]
        start = bv.get('byteOffset', 0)
        raw = binc[start:start + bv['byteLength']]
        mime = img.get('mimeType', 'image/png')
        ext = '.png' if 'png' in mime else '.jpg'
        fname = '%s_tex%d%s' % (name, i, ext)
        (out_dir / fname).write_bytes(raw)
        tex_names[i] = fname

    mat = g.get('materials', [{}])[0]
    pbr = mat.get('pbrMetallicRoughness', {})
    base_tex = None
    if 'baseColorTexture' in pbr:
        ti = pbr['baseColorTexture']['index']
        src = g['textures'][ti].get('source')
        base_tex = tex_names.get(src)
    base_color = pbr.get('baseColorFactor', [1, 1, 1, 1])

    # ---------- MTL ----------
    mtl_name = '%s.mtl' % name
    mtl = ['newmtl %s' % name,
           'Ka 0 0 0',
           'Kd %.4f %.4f %.4f' % tuple(base_color[:3]),
           'Ks 0 0 0',
           'd 1.0',
           'illum 2']
    if base_tex:
        mtl.append('map_Kd %s' % base_tex)
    (out_dir / mtl_name).write_text('\n'.join(mtl) + '\n', encoding='utf-8')

    # ---------- OBJ ----------
    obj_path = out_dir / ('%s.obj' % name)
    with open(obj_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# converted from %s\n' % os.path.basename(glb_path))
        f.write('mtllib %s\n' % mtl_name)
        f.write('o %s\n' % name)
        f.write('usemtl %s\n' % name)
        for i in range(len(verts)):
            f.write('v %.5f %.5f %.5f\n' % (verts[i, 0], verts[i, 1], verts[i, 2]))
        for i in range(len(uvs)):
            f.write('vt %.6f %.6f\n' % (uvs[i, 0], 1.0 - uvs[i, 1]))  # glTF 左上原点 -> OBJ 左下
        if np.any(norms):
            for i in range(len(norms)):
                f.write('vn %.5f %.5f %.5f\n' % (norms[i, 0], norms[i, 1], norms[i, 2]))
        has_n = bool(np.any(norms))
        # 一次性拼面，避免 150 万次 write
        chunk, bufsize = [], 4000
        for tri in faces:
            if has_n:
                chunk.append('f %d/%d/%d %d/%d/%d %d/%d/%d\n' % (
                    tri[0] + 1, tri[0] + 1, tri[0] + 1,
                    tri[1] + 1, tri[1] + 1, tri[1] + 1,
                    tri[2] + 1, tri[2] + 1, tri[2] + 1))
            else:
                chunk.append('f %d/%d %d/%d %d/%d\n' % (
                    tri[0] + 1, tri[0] + 1, tri[1] + 1, tri[1] + 1, tri[2] + 1, tri[2] + 1))
            if len(chunk) >= bufsize:
                f.write(''.join(chunk))
                chunk = []
        if chunk:
            f.write(''.join(chunk))

    info = {
        'obj': str(obj_path),
        'mtl': mtl_name,
        'textures': list(tex_names.values()),
        'vertices': int(len(verts)),
        'triangles': int(len(faces)),
        'height': float(height),
        'bbox_min': verts.min(axis=0).round(4).tolist(),
        'bbox_max': verts.max(axis=0).round(4).tolist(),
        'size_mb': round(obj_path.stat().st_size / 1048576, 1),
    }
    print(json.dumps(info, ensure_ascii=False, indent=2))
    return info


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    src, dst = sys.argv[1], sys.argv[2]
    kw = {}
    if '--name' in sys.argv:
        kw['name'] = sys.argv[sys.argv.index('--name') + 1]
    if '--scale' in sys.argv:
        kw['scale'] = float(sys.argv[sys.argv.index('--scale') + 1])
    glb_to_obj(src, dst, **kw)
