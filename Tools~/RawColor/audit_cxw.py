"""Audit every compressed sector in exported packed-color CXW files."""
import argparse
import json
import struct
import zlib
from pathlib import Path
import numpy as np


def audit(path):
    report = json.loads(path.with_suffix('.json').read_text())
    counts = np.zeros(3, dtype=np.int64)
    bricks = 0
    with path.open('rb') as f:
        magic, version, flags, entities = struct.unpack('<IHHQ', f.read(16))
        assert (magic, version, flags) == (0x534c5843, 6, 1)
        f.seek(entities)
        assert struct.unpack('<I', f.read(4))[0] == 1
        f.read(16 + 28 + 2)
        assert f.read(1) == b'\x02'  # Static, no physics body.
        f.read(24)
        assert f.read(1) == b'\x01'
        index_offset, sectors = struct.unpack('<QI', f.read(12))
        assert sectors == report['sectors']
        f.seek(index_offset)
        index = [struct.unpack('<iiiQI', f.read(24)) for _ in range(sectors)]
        assert len({item[:3] for item in index}) == sectors
        for x, y, z, offset, size in index:
            assert 64 <= offset and offset + size <= index_offset
            f.seek(offset + 16384)
            length = struct.unpack('<I', f.read(4))[0]
            assert size == 16388 + length
            raw = zlib.decompress(f.read(length), -15)
            magic, capacity, count, free = struct.unpack_from('<4I', raw)
            assert magic == 0x32535856 and 0 < capacity == count <= 4096 and free == 0
            mapping = np.frombuffer(raw, '<i2', 4096, 16)
            np.testing.assert_array_equal(np.sort(mapping[mapping >= 0]), np.arange(capacity))
            assert np.count_nonzero(mapping == -1) == 4096-capacity
            assert raw[8208:16402] == bytes(8194)
            assert struct.unpack_from('<IBH', raw, 16402) == (1, 0, 2)
            assert len(raw) == 16409 + capacity * 1024
            code = np.frombuffer(raw, '<u2', offset=16409)
            assert np.all(((code & 0xc000) != 0) | (code <= 132))
            counts += [np.count_nonzero(code & 0x8000), np.count_nonzero((code & 0xc000) == 0x4000),
                       np.count_nonzero((code > 0) & (code < 256))]
            bricks += capacity
    assert bricks == report['bricks']
    assert counts.tolist() == [report['opaque'], report['emissive'], report['glass']]
    assert int(counts.sum()) == report['voxels']
    assert path.stat().st_size == report['bytes']
    print(f'{path.name}: {sectors} sectors, {int(counts.sum()):,} voxels; native layout and all material codes valid.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='+', type=Path)
    for path in parser.parse_args().files:
        audit(path)
