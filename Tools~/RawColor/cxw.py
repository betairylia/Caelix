"""Current CXLS v6/VXS2 writer. Canonical 16-bit blocks only; no mode marker."""
import collections, hashlib, json, struct, tempfile, uuid, zlib
from pathlib import Path
import numpy as np

RECORD = np.dtype([('position','<u4'),('block','<u2')])

def color_lut():
    code=np.arange(65536,dtype=np.uint32)
    rgb=np.column_stack(((code>>10)&31,(code>>5)&31,code&31)).astype(np.uint32)
    em=(code&0xc000)==0x4000
    rgb[em]=np.column_stack(((code[em]>>10)&15,(code[em]>>6)&15,(code[em]>>2)&15))*31//15
    palette=json.loads((Path(__file__).parent/'glass-palette.json').read_text())['entries']
    glass=np.full((256,3),31,dtype=np.uint32)
    for m in palette:glass[m['id']]=np.rint(np.array(m['tint'])*31).astype(np.uint32)
    tr=(code&0xc000)==0;rgb[tr]=glass[code[tr]&255]
    return rgb

class CxwWriter:
    def __init__(self,path):
        self.path=Path(path).resolve()
        if self.path.exists():raise FileExistsError(self.path)
        self.path.parent.mkdir(parents=True,exist_ok=True)
        self.temp=tempfile.TemporaryDirectory(prefix='cxw-',dir=self.path.parent)
        self.root=Path(self.temp.name);self.handles=collections.OrderedDict();self.sectors=set()
        self.counts=np.zeros(3,dtype=np.int64);self.lo=np.full(3,np.iinfo(np.int32).max);self.hi=-self.lo
    def add(self,xyz,blocks):
        xyz=np.asarray(xyz,dtype=np.int32);blocks=np.asarray(blocks,dtype=np.uint16)
        assert len(xyz)==len(blocks) and np.all(blocks!=0)
        assert np.all(((blocks&0xc000)!=0)|(blocks<256)), 'Source blocks must not contain face masks'
        if not len(blocks):return
        self.lo=np.minimum(self.lo,xyz.min(axis=0));self.hi=np.maximum(self.hi,xyz.max(axis=0))
        self.counts += [np.count_nonzero(blocks&0x8000),np.count_nonzero((blocks&0xc000)==0x4000),np.count_nonzero((blocks&0xc000)==0)]
        sector=xyz//128
        for coord in np.unique(sector,axis=0):
            key=tuple(int(c) for c in coord);mask=np.all(sector==coord,axis=1);local=xyz[mask]%128
            records=np.empty(mask.sum(),dtype=RECORD)
            records['position']=local[:,0]+128*local[:,1]+16384*local[:,2];records['block']=blocks[mask]
            if key in self.handles:self.handles.move_to_end(key)
            else:
                if len(self.handles)>=64:self.handles.popitem(last=False)[1].close()
                self.handles[key]=(self.root/('%d_%d_%d.bin'%key)).open('ab')
            self.handles[key].write(records.tobytes());self.sectors.add(key)
    def finish(self):
        for f in self.handles.values():f.close()
        self.handles.clear();lut=color_lut();index=[];bricks_total=0
        partial=self.path.with_suffix('.partial.cxw')
        with partial.open('wb') as f:
            f.write(bytes(64))
            for coord in sorted(self.sectors):
                r=np.fromfile(self.root/('%d_%d_%d.bin'%coord),dtype=RECORD)
                pos=r['position'];code=r['block']
                assert len(np.unique(pos))==len(pos), 'Duplicate source voxel'
                x=pos%128;y=(pos//128)%128;z=pos//16384
                absolute=x//8+16*(y//8)+256*(z//8)
                allocated,bid=np.unique(absolute,return_inverse=True);capacity=len(allocated);bricks_total+=capacity
                indices=np.full(4096,-1,dtype='<i2');indices[allocated]=np.arange(capacity)
                dense=np.zeros(capacity*512,dtype='<u2')
                local=(x%8)+8*(y%8)+64*(z%8);dense[bid*512+local]=code
                sub=(x%8//4)+2*(y%8//4)+4*(z%8//4)
                occ=np.zeros(capacity,dtype=np.uint32);em=np.zeros_like(occ)
                np.bitwise_or.at(occ,bid,np.left_shift(np.uint32(1),sub))
                emitting=(code&0xc000)==0x4000
                np.bitwise_or.at(em,bid[emitting],np.left_shift(np.uint32(1),sub[emitting]))
                counts=np.bincount(bid,minlength=capacity)
                rgb=np.stack([np.bincount(bid,weights=lut[code,c],minlength=capacity)//counts for c in range(3)],axis=1).astype(np.uint32)
                rgb565=(rgb[:,0]<<11)|(rgb[:,1]<<6)|rgb[:,2]
                preview=np.zeros(4096,dtype='<u4');preview[allocated]=occ|(em<<8)|(rgb565<<16)
                # VXS2: packed brick slots, zero pending simulation flags, Block slot only.
                raw=struct.pack('<4I',0x32535856,capacity,capacity,0)+indices.tobytes()+bytes(2+8192)+struct.pack('<IBH',1,0,2)+dense.tobytes()
                compressor=zlib.compressobj(6,zlib.DEFLATED,-15);payload=compressor.compress(raw)+compressor.flush()
                offset=f.tell();f.write(preview.tobytes());f.write(struct.pack('<I',len(payload)));f.write(payload)
                index.append((*coord,offset,f.tell()-offset))
            index_offset=f.tell()
            for item in index:f.write(struct.pack('<iiiQI',*item))
            entity_offset=f.tell();f.write(struct.pack('<I',1))
            f.write(uuid.uuid4().bytes_le)
            # Position, rotation, require-update, static/no-body, velocities, protected, index.
            f.write(struct.pack('<7fHB6fBQI',0,0,0,0,0,0,1,0,2,0,0,0,0,0,0,1,index_offset,len(index)))
            f.seek(0);f.write(struct.pack('<IHHQ',0x534c5843,6,1,entity_offset))
        partial.rename(self.path)
        with self.path.open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
        report={'file':str(self.path),'bytes':self.path.stat().st_size,'sha256':digest,'voxels':int(sum(self.counts)),'opaque':int(self.counts[0]),'emissive':int(self.counts[1]),'glass':int(self.counts[2]),'sectors':len(index),'bricks':bricks_total,'bounds_min':self.lo.tolist(),'bounds_max':self.hi.tolist(),'up':'Y','canonical_face_bits':0}
        self.temp.cleanup();return report
    def close(self):
        for f in self.handles.values():f.close()
        self.handles.clear();self.temp.cleanup()
