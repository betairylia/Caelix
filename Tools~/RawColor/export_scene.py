"""Material-aware OBJ -> native CXW, using the alpha-fixed obj2voxel C API.

python export_scene.py scene.obj result.cxw --dll obj2voxel-shared.dll --resolution 4096
Use --mtl for an original material library and --glass JSON for explicit name->ID
overrides. RGB bytes carry discrete packed codes, so MAX sampling is mandatory.
Requires numpy and Pillow. Working textures/shards stay beside the output.
"""
import argparse
import ctypes as C
import json
import os
import shutil
import threading
import time
from pathlib import Path

import numpy as np
from PIL import Image
from cxw import CxwWriter


def read_mtl(path):
    materials = {}
    current = None
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        parts = line.strip().split(maxsplit=1)
        if len(parts) != 2 or parts[0].startswith('#'):
            continue
        key, value = parts
        if key == 'newmtl':
            current = materials.setdefault(value, {})
        elif current is not None:
            current[key] = value
    return materials


def load_map(material, key, root):
    if key not in material:
        return None
    value = material[key]
    if value.startswith('-'):
        raise ValueError(f'MTL map options need explicit preprocessing: {key} {value}')
    return Image.open(root / value).convert('RGBA')


def encode_emission(rgb, scale):
    rgb = rgb.astype(np.float32) * scale
    peak = rgb.max(axis=-1)
    strength = np.searchsorted(np.array([1, 8, 64, 512]), peak).clip(0, 3)
    color = np.rint(rgb / np.power(8., strength)[..., None] * 15).clip(0, 15).astype(np.uint16)
    code = 0x4000 | (color[..., 0] << 10) | (color[..., 1] << 6) | (color[..., 2] << 2) | strength.astype(np.uint16)
    return code, (color.max(axis=-1) != 0)


def prepare(source, mtl, work, glass, emission_scale):
    work.mkdir(parents=True, exist_ok=True)
    # Keep geometry unchanged and override its original material-library filename.
    libraries = []
    with source.open(encoding='utf-8-sig') as f:
        for line in f:
            if line.startswith('mtllib '):
                libraries.append(line[7:].strip())
    if len(libraries) != 1 or Path(libraries[0]).name != libraries[0]:
        raise ValueError('Expected one relative material-library filename in OBJ')
    mtl = mtl or source.parent / libraries[0]
    materials = read_mtl(mtl)
    unknown = set(glass) - set(materials)
    if unknown:
        raise ValueError(f'Unknown glass material overrides: {sorted(unknown)}')
    palette_ids = {m['id'] for m in json.loads((Path(__file__).parent/'glass-palette.json').read_text())['entries']}
    if any(value not in palette_ids for value in glass.values()):
        raise ValueError('Glass overrides must reference non-air shared presets')
    obj = work / source.name
    if not obj.exists():
        try:
            os.link(source, obj)
        except OSError:
            shutil.copyfile(source, obj)
    output = []
    report = []
    for i, (name, material) in enumerate(materials.items()):
        if name in glass:
            code = np.full((1, 1), glass[name], dtype=np.uint16)
            alpha = np.full((1, 1), 255, dtype=np.uint8)
            emitting = np.zeros((1, 1), dtype=bool)
        else:
            texture = load_map(material, 'map_Kd', mtl.parent)
            if texture is None:
                kd = np.array([float(v) for v in material.get('Kd', '1 1 1').split()])
                base = np.rint(np.clip(kd, 0, 1) * 255).astype(np.uint8).reshape(1, 1, 3)
                alpha = np.full((1, 1), 255, dtype=np.uint8)
            else:
                rgba = np.array(texture)
                base, alpha = rgba[..., :3], rgba[..., 3]
            # map_d often references the same RGBA base-color image in Bistro.
            if 'map_d' in material and material.get('map_d') != material.get('map_Kd'):
                opacity = load_map(material, 'map_d', mtl.parent)
                opacity = opacity.resize((base.shape[1], base.shape[0]), Image.Resampling.NEAREST)
                alpha = np.minimum(alpha, np.asarray(opacity)[..., 0])
            d = float(material.get('d', str(1 - float(material.get('Tr', '0')))))
            alpha = np.rint(alpha.astype(float) * np.clip(d, 0, 1)).astype(np.uint8)
            color = base.astype(np.uint16) >> 3
            code = 0x8000 | (color[..., 0] << 10) | (color[..., 1] << 5) | color[..., 2]
            emission = load_map(material, 'map_Ke', mtl.parent)
            if emission is not None:
                emission = emission.resize((base.shape[1], base.shape[0]), Image.Resampling.NEAREST)
                # Emissive texture RGB is encoded in sRGB; radiance is linear.
                srgb = np.asarray(emission)[..., :3].astype(np.float32) / 255
                energy = np.where(srgb <= .04045, srgb / 12.92, ((srgb + .055) / 1.055) ** 2.4)
            else:
                energy = np.broadcast_to(np.array([float(v) for v in material.get('Ke', '0 0 0').split()]), base.shape)
            emissive, emitting = encode_emission(energy, emission_scale)
            code = np.where(emitting, emissive, code).astype(np.uint16)
        encoded = np.zeros((*code.shape, 4), dtype=np.uint8)
        encoded[..., 0] = code >> 8
        encoded[..., 1] = code & 255
        encoded[..., 3] = alpha
        filename = f'material-{i:04d}.png'
        Image.fromarray(encoded).save(work / filename)
        fallback = int(code.flat[0])
        output += [f'newmtl {name}', f'Kd {(fallback >> 8)/255:.17g} {(fallback & 255)/255:.17g} 0',
                   'd 1', f'map_Kd {filename}', '']
        report.append({'name': name, 'glass_id': glass.get(name), 'emissive_texels': int(emitting.sum()),
                       'masked_texels': int((alpha < 128).sum())})
    (work / libraries[0]).write_text('\n'.join(output), encoding='utf-8')
    (work / 'materials-report.json').write_text(json.dumps(report, indent=2))
    return obj


def voxelize(dll_path, source, resolution, up, threads, consume):
    dll = C.CDLL(str(dll_path.resolve()))
    ptr = C.c_void_p
    callback_type = C.CFUNCTYPE(C.c_bool, ptr, C.POINTER(C.c_uint32), C.c_size_t)
    log_type = C.CFUNCTYPE(C.c_bool, ptr, C.c_char_p, C.c_uint8)
    declarations = {
        'alloc': ([], ptr), 'free': ([ptr], None),
        'set_input_file': ([ptr, C.c_char_p, C.c_char_p], None),
        'set_output_callback': ([ptr, callback_type, ptr], None),
        'set_parallel': ([ptr, C.c_bool], None), 'run_worker': ([ptr], None),
        'stop_workers': ([ptr], None), 'set_resolution': ([ptr, C.c_uint32], None),
        'set_color_strategy': ([ptr, C.c_uint8], None),
        'set_unit_transform': ([ptr, C.POINTER(C.c_int)], None),
        'voxelize': ([ptr], C.c_uint8),
        'set_log_callback': ([log_type, ptr], None), 'set_log_level': ([C.c_uint8], None),
    }
    for name, (args, result) in declarations.items():
        fn = getattr(dll, 'obj2voxel_' + name)
        fn.argtypes, fn.restype = args, result
    failures = []
    warnings = []
    @callback_type
    def output(_, data, count):
        if failures:
            return False
        try:
            records = np.ctypeslib.as_array(data, shape=(count * 4,)).reshape(-1, 4)
            # Callback uses native-endian x,y,z,ARGB, unlike on-disk VL32.
            consume(records[:, :3], ((records[:, 3] >> 8) & 65535).astype(np.uint16))
            return True
        except BaseException as exc:
            failures.append(exc)
            return False
    @log_type
    def log(_, message, level):
        text = message.decode('utf-8', errors='replace')
        if level <= 2:
            warnings.append(text)
        if level <= 3:
            print(text, flush=True)
        return True
    dll.obj2voxel_set_log_callback(log, None)
    dll.obj2voxel_set_log_level(3)
    instance = dll.obj2voxel_alloc()
    if not instance:
        raise MemoryError('obj2voxel_alloc')
    workers = []
    # The C API retains this pointer rather than copying the filename.
    source_bytes = str(source.resolve()).encode('utf-8')
    previous_cwd = Path.cwd()
    os.chdir(source.resolve().parent)
    try:
        dll.obj2voxel_set_input_file(instance, source_bytes, b'obj')
        dll.obj2voxel_set_output_callback(instance, output, None)
        dll.obj2voxel_set_resolution(instance, resolution)
        dll.obj2voxel_set_color_strategy(instance, 0)
        if up == 'z':
            dll.obj2voxel_set_unit_transform(instance, (C.c_int * 9)(1,0,0,0,0,1,0,-1,0))
        dll.obj2voxel_set_parallel(instance, threads > 0)
        for _ in range(threads):
            thread = threading.Thread(target=dll.obj2voxel_run_worker, args=(instance,))
            thread.start()
            workers.append(thread)
        error = dll.obj2voxel_voxelize(instance)
    finally:
        dll.obj2voxel_stop_workers(instance)
        for worker in workers:
            worker.join()
        dll.obj2voxel_free(instance)
        dll.obj2voxel_set_log_callback(log_type(), None)
        os.chdir(previous_cwd)
    if failures:
        raise failures[0]
    if error or warnings:
        raise RuntimeError(f'Voxelization error={error}, warnings={warnings}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--resolution', type=int, default=4096)
    parser.add_argument('--mtl', type=Path)
    parser.add_argument('--glass', type=Path)
    parser.add_argument('--up', choices=['y', 'z'], default='y')
    parser.add_argument('--threads', type=int, default=12)
    parser.add_argument('--emission-scale', type=float, default=64)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError(args.output)
    started = time.monotonic()
    glass = json.loads(args.glass.read_text()) if args.glass else {}
    work = args.output.resolve().parent / (args.output.stem + '-materials')
    obj = prepare(args.source.resolve(), args.mtl.resolve() if args.mtl else None, work, glass, args.emission_scale)
    writer = CxwWriter(args.output)
    try:
        voxelize(args.dll, obj, args.resolution, args.up, args.threads, writer.add)
        report = writer.finish()
    finally:
        writer.close()
    report.update(source=str(args.source.resolve()), resolution=args.resolution,
                  emission_scale=args.emission_scale, seconds=round(time.monotonic()-started, 2))
    args.output.with_suffix('.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    main()
