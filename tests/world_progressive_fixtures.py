"""Mix the unchanged validation world and generic families in HCPK v2."""
from pathlib import Path
from progressive_fixtures import build
from hypercube import pack
def mixed(root='out/progressive'):
    root=Path(root);families=build(root);original=root/'world-v1.hcp'
    pack.build(original,root/'world-v1.json',resolution=12);raw=original.read_bytes();count=pack.HEADER.unpack_from(raw)[2];builder=pack.Builder()
    for i in range(count):
        r=pack.RECORD.unpack_from(raw,32+i*64)
        builder.add(r[1],raw[r[2]:r[2]+r[3]],fmt=r[5],group=r[6],mip=r[7],cell=r[8],parent=r[9],width=r[10],useful=r[11],material=r[12],variant=r[13])
    builder.import_textures([(f['name'],root/f'{i}.dnlod') for i,f in enumerate(families.families)])
    builder.write(root/'world-mixed.hcp');pack.inspect(root/'world-mixed.hcp')
if __name__=='__main__':mixed()
