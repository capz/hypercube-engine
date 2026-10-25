"""Original GameCube FST fixture, inserted while Dolphin boots a DOL.

The fixture has no apploader; it is a data disc, not a standalone bootable ISO.
"""
from pathlib import Path
import struct


def build(path, pack):
    payload = Path(pack).read_bytes()
    offset = 0x8000
    image = bytearray((offset+len(payload)+2047)//2048*2048)
    image[:6] = b"HCBE01"
    image[0x20:0x20+20] = b"Hypercube validation"
    struct.pack_into(">I",image,0x1c,0xc2339f3d)
    names = b"\0world.hcp\0"
    table = struct.pack(">III",0x01000000,0,2)+struct.pack(">III",1,offset,len(payload))+names
    struct.pack_into(">III",image,0x424,0x500,len(table),len(table))
    image[0x500:0x500+len(table)] = table
    image[offset:offset+len(payload)] = payload
    Path(path).write_bytes(image)
