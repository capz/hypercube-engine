"""DNLT import and HCPK v2 family metadata. Payloads remain individual records."""
import math
from pathlib import Path
import re
import struct
import zlib

HEADER=struct.Struct('<4s15I')
FAMILY=struct.Struct('<12I80s')
LEVEL=struct.Struct('<II4fII')
NATIVE=6

def crc(data):return zlib.crc32(data)&0xffffffff
def align(n):return (n+31)&~31
def native_bytes(w,h,fmt):
    if not 1<=w<=1024 or not 1<=h<=1024:raise ValueError('Native dimensions')
    if fmt==6:return ((w+3)//4)*((h+3)//4)*64
    if fmt==14:return ((w+7)//8)*((h+7)//8)*32
    raise ValueError('Native format')
def name_bytes(name):
    if not isinstance(name,str) or not re.fullmatch(r'[A-Za-z0-9/_.-]{1,80}',name):raise ValueError('Family name')
    return name.encode('ascii')

def read_dnlt(path):
    data=Path(path).read_bytes()
    if len(data)<64:raise ValueError('Truncated DNLT')
    magic,version,flags,w,h,fmt,count,coarse,index,start,length,digest,uv_offset,uv_bytes,r0,r1=HEADER.unpack_from(data)
    if magic!=b'DNLT' or version!=1 or flags not in (0,1,3,4) or not 1<=count<=11 or coarse!=count-1 or index!=64 or r0 or r1 or length!=len(data) or length>16*1024*1024:
        raise ValueError('DNLT header')
    native_bytes(w,h,fmt)
    if flags==0 and count!=1 or flags==4 and count!=2:raise ValueError('DNLT ladder')
    if flags in (1,3) and (w&(w-1) or h&(h-1)):raise ValueError('DNLT mip dimensions')
    metadata=count*32
    if uv_offset:
        if uv_offset!=64+metadata or uv_bytes!=count*16:raise ValueError('DNLT UV index')
        metadata+=uv_bytes
    elif uv_bytes:raise ValueError('DNLT UV length')
    if start!=align(64+metadata) or start>length or crc(data[64:64+metadata])!=digest:raise ValueError('DNLT metadata CRC/bounds')
    levels=[];previous=-1
    for i in range(count):
        logical,lw,lh,lf,level_flags,offset,size,checksum,reserved=struct.unpack_from('<IHH6I',data,64+i*32)
        uv=struct.unpack_from('<4f',data,uv_offset+i*16) if uv_offset else (1.,1.,0.,0.)
        if logical<=previous or logical>10 or i==0 and logical!=0 or flags==1 and logical!=i or flags==4 and logical!=i or level_flags or reserved or lf!=fmt or size!=native_bytes(lw,lh,lf) or offset%32 or offset<start or size>length-offset:
            raise ValueError('DNLT level')
        if not all(math.isfinite(v) for v in uv) or min(uv[:2])<=0 or min(uv[2:])<0 or uv[0]+uv[2]>1.000001 or uv[1]+uv[3]>1.000001:raise ValueError('DNLT UV')
        if (flags in (1,3) or i==0) and (abs(lw*uv[0]-max(1,w>>logical))>.001 or abs(lh*uv[1]-max(1,h>>logical))>.001):raise ValueError('DNLT usable dimensions')
        payload=data[offset:offset+size]
        if crc(payload)!=checksum:raise ValueError('DNLT payload CRC')
        levels.append(dict(logical=logical,width=lw,height=lh,fmt=lf,uv=uv,payload=payload,offset=offset))
        previous=logical
    end=start
    for level in reversed(levels):
        if level['offset']!=end:raise ValueError('DNLT coarse-first ranges')
        end+=len(level['payload'])
    if end!=length or flags==1 and (w>>previous>1 or h>>previous>1):raise ValueError('DNLT incomplete chain')
    return dict(width=w,height=h,flags=flags,levels=levels)

def add_families(builder,inputs):
    pending=[];names={f['name'] for f in builder.families}
    for name,path in inputs:
        name_bytes(name)
        if name in names or len(names)>=128:raise ValueError('Duplicate/too many families')
        family=read_dnlt(path);family.update(id=len(builder.families)+len(pending)+1,name=name)
        pending.append(family);names.add(name)
    # Group all coarse records together before any family gets a fine upgrade.
    for depth in range(max((len(f['levels']) for f in pending),default=0)):
        for family in pending:
            levels=family['levels'];index=len(levels)-1-depth
            if index<0:continue
            level=levels[index];parent=levels[index+1]['record'] if index+1<len(levels) else 0
            level['record']=builder.add(NATIVE,level['payload'],fmt=level['fmt'],group=family['id'],mip=level['logical'],parent=parent,width=level['width'],useful=level['height'])
    builder.families.extend(pending)
    return [f['id'] for f in pending]

def add_family(builder,name,path):return add_families(builder,[(name,path)])[0]

def write(builder,path):
    from .pack import RECORD
    descriptors=bytearray();levels=bytearray();base=len(builder.families)*128
    for family in builder.families:
        name=name_bytes(family['name']);entries=family['levels']
        descriptors.extend(FAMILY.pack(family['id'],family['flags'],family['width'],family['height'],len(entries),base+len(levels),0,len(name),0,0,0,0,name))
        for entry in entries:levels.extend(LEVEL.pack(entry['record'],entry['logical'],*entry['uv'],0,0))
    families=descriptors+levels
    if len(families)>65536 or not 1<=len(builder.items)<=4096:raise ValueError('Pack metadata budget')
    start=align(64+len(builder.items)*64+len(families));data=bytearray(start);table=bytearray()
    for item in builder.items:
        offset=align(len(data));data.extend(bytes(offset-len(data)));data.extend(item['payload'])
        table.extend(RECORD.pack(item['id'],item['kind'],offset,len(item['payload']),crc(item['payload']),item['fmt'],item['group'],item['mip'],item['cell'],item['parent'],item['width'],item['useful'],item['material'],item['variant'],0,0))
    metadata=table+families;data.extend(bytes(align(len(data))-len(data)));data[64:64+len(metadata)]=metadata
    data[:64]=HEADER.pack(b'HCPK',2,len(builder.items),64,start,len(data),crc(metadata),0,len(builder.families),64+len(table),len(families),0,0,0,0,0)
    Path(path).parent.mkdir(parents=True,exist_ok=True);Path(path).write_bytes(data)
    return dict(version=2,records=len(builder.items),families=len(builder.families),bytes=len(data),index_crc=crc(metadata))

def inspect(data):
    from .pack import RECORD
    if len(data)<64:raise ValueError('Truncated HCPK v2')
    magic,version,count,index,start,length,digest,reserved,n,offset,size,*tail=HEADER.unpack_from(data)
    if magic!=b'HCPK' or version!=2 or not 1<=count<=4096 or index!=64 or length!=len(data) or reserved or any(tail) or not 1<=n<=128 or offset!=64+count*64 or size>65536 or size<n*128 or start<offset+size or start%32 or start>length:raise ValueError('HCPK v2 header')
    if crc(data[64:offset+size])!=digest:raise ValueError('HCPK v2 metadata CRC')
    records=[];end=start
    for i in range(count):
        r=RECORD.unpack_from(data,64+i*64);identity,kind,at,amount,checksum=r[:5]
        if identity!=i+1 or kind not in (1,2,3,4,5,6) or at<end or at%32 or amount<1 or amount>length-at or r[14] or r[15] or crc(data[at:at+amount])!=checksum:raise ValueError('HCPK v2 record')
        if kind==6 and (amount!=native_bytes(r[10],r[11],r[5]) or r[8] or r[12] or r[13]):raise ValueError('HCPK v2 native descriptor')
        records.append(r);end=at+amount
    names=set();seen=set();end=n*128
    for i in range(n):
        identity,flags,w,h,amount,at,fallback,name_length,*rest=FAMILY.unpack_from(data,offset+i*128);name=rest[-1]
        if identity!=i+1 or flags not in (0,1,3,4) or not 1<=amount<=11 or at!=end or at+amount*32>size or fallback or any(rest[:-1]) or not 1<=name_length<=80 or any(name[name_length:]):raise ValueError('HCPK v2 family')
        native_bytes(w,h,6);name=name[:name_length].decode('ascii');name_bytes(name)
        if name in names:raise ValueError('Duplicate family name')
        names.add(name);previous=-1;ids=[]
        if flags==0 and amount!=1 or flags==4 and amount!=2 or flags in (1,3) and (w&(w-1) or h&(h-1)):raise ValueError('Family ladder')
        for j in range(amount):
            record,logical,su,sv,ou,ov,z0,z1=LEVEL.unpack_from(data,offset+at+j*32)
            if not 1<=record<=count:raise ValueError('Family record reference')
            r=records[record-1]
            if record in seen or logical<=previous or logical>10 or j==0 and logical!=0 or flags in (1,4) and logical!=j or r[1]!=6 or r[6]!=identity or r[7]!=logical or z0 or z1 or not all(math.isfinite(x) for x in (su,sv,ou,ov)) or min(su,sv)<=0 or min(ou,ov)<0 or su+ou>1.000001 or sv+ov>1.000001:raise ValueError('Family level')
            if (flags in (1,3) or j==0) and (abs(r[10]*su-max(1,w>>logical))>.001 or abs(r[11]*sv-max(1,h>>logical))>.001):raise ValueError('Family level dimensions')
            previous=logical;seen.add(record);ids.append(record)
        if flags==1 and (w>>previous>1 or h>>previous>1):raise ValueError('Incomplete mip family')
        for j,record in enumerate(ids):
            if records[record-1][9]!=(ids[j+1] if j+1<len(ids) else 0):raise ValueError('Family parent chain')
        end+=amount*32
    if end!=size or any(r[1]==6 and r[0] not in seen for r in records):raise ValueError('Unindexed family data')
    return count
