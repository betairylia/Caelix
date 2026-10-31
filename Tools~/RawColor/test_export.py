"""End-to-end discrete texture transport and native-save fixture generation."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image
from export_scene import voxelize
from cxw import CxwWriter


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--work', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    code = np.arange(65536, dtype=np.uint16).reshape(256, 256)
    rgba = np.zeros((256, 256, 4), dtype=np.uint8)
    rgba[..., 0], rgba[..., 1], rgba[..., 3] = code >> 8, code & 255, 255
    Image.fromarray(rgba).save(args.work/'codes.png')
    (args.work/'codes.mtl').write_text('newmtl codes\nKd 1 1 1\nmap_Kd codes.png\n')
    with (args.work/'codes.obj').open('w') as f:
        f.write('mtllib codes.mtl\nusemtl codes\n')
        for i in range(65536):
            x, y = (i % 256)*4, (i//256)*4
            f.write(f'v {x} {y} 0\nv {x+1} {y} 0\nv {x} {y+1} 0\n')
            f.write(f'vt {(i%256+.5)/256} {1-(i//256+.5)/256}\n')
            f.write(f'f {i*3+1}/{i+1} {i*3+2}/{i+1} {i*3+3}/{i+1}\n')
    observed = np.zeros(65536, dtype=bool)
    def check(xyz, packed):
        cell = np.rint(xyz[:, :2].astype(float)/(1024/1021)/4).astype(int)
        expected = cell[:, 0] + 256*cell[:, 1]
        mismatch = packed != expected
        if np.any(mismatch):
            n = np.flatnonzero(mismatch)[0]
            raise AssertionError((xyz[n].tolist(), int(packed[n]), int(expected[n])))
        observed[packed] = True
    voxelize(args.dll, args.work/'codes.obj', 1024, 'y', 4, check)
    assert observed.all(), int(observed.sum())
    print('All 65536 packed codes survived native textured voxelization exactly.', flush=True)
    blocks = np.concatenate((np.arange(0x8000, 0x10000), np.arange(0x4000, 0x8000), np.arange(1, 133))).astype(np.uint16)
    i = np.arange(len(blocks))
    xyz = np.column_stack((i%64-33, i//64%64-33, i//4096-2))
    writer = CxwWriter(args.fixture)
    try:
        writer.add(xyz, blocks)
        print(writer.finish())
    finally:
        writer.close()


if __name__ == '__main__':
    main()
