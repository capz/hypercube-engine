#!/usr/bin/env python3
"""Reproducible payload accounting for a validated HCPK v1 world (not a hardware trace)."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
from hypercube import pack


def audit(path, maximum_read_batch_bytes=65536):
    if not 32 <= maximum_read_batch_bytes <= 131040:
        raise ValueError('Maximum read batch must be 32..131040 bytes')
    path = Path(path)
    count = pack.inspect(path)
    raw = path.read_bytes()
    if struct.unpack_from('<I', raw, 4)[0] != 1 or count < 1:
        raise ValueError('Expected a nonempty HCPK v1 world')
    lengths = [pack.RECORD.unpack_from(raw, 32 + i * 64)[3]
               for i in range(count) if pack.RECORD.unpack_from(raw, 32 + i * 64)[1] in (1, 2, 3)]
    supported = [size for size in lengths if size <= 131040]
    staging = (max([maximum_read_batch_bytes] + supported) + 31) & ~31
    def change(before, after):
        return dict(before=before, after=after, saved=before-after)
    # Asset IDs are bounded by4096. Fixed ID indexes contain781 entries; next
    # includes one sentinel. Scheduler's ID->slot lookup also has a sentinel.
    catalog = change(4*(count+782), 2*(count+782))
    scheduler = change(4*(count+1), 2*(count+1))
    native = change(131072, staging)
    # Bounded idle/fence arrays and cached-read flags replace the previous
    # per-batch store-task references. Scalar object fields are excluded.
    lifecycle = change(8*4,128*4+48*4*2+8)
    aram_active = change(8*(count+1),12*(count+1))
    return dict(schema=2,scope='validated-v1-world-payload-accounting',hardwareMeasurement=False,
                sourceSha256=hashlib.sha256(raw).hexdigest(),records=count,
                maximumReadBatchBytes=maximum_read_batch_bytes,
                largestSupportedStreamRecord=max(supported, default=0),
                unsupportedStreamRecords=sum(size>131040 for size in lengths),
                nativeStagingBytes=native,
                optionalColdWriteStaging=dict(allocatedLazily=True,bufferBytes=16384,
                    sourceAndColdBuffers=change(131072,staging+16384)),
                retainedManagedPayload=dict(catalogIds=catalog,schedulerLookup=scheduler,
                                            saved=catalog['saved']+scheduler['saved']),
                idleAramMetadata=dict(targetPointerBytes=4,disabled=change(8*(count+1),0),
                                      enabledAfterFirstStore=aram_active),
                idleLifecycleArrayPayload=dict(targetPointerBytes=4,change=lifecycle,
                    idleResourceFramesBytes=128*4,poolIdleAndFenceBytes=48*4*2,
                    cachedReadFlagsBytes=8,removedStoreTaskReferencesBytes=8*4),
                selectedManagedArraySavings=dict(
                    coldCacheUnused=catalog['saved']+scheduler['saved']+8*(count+1)+lifecycle['saved'],
                    coldCacheActive=catalog['saved']+scheduler['saved']+aram_active['saved']+lifecycle['saved']),
                transientCatalogTailPayload=change(96*4,96*2),
                unchanged=dict(parsedRecordPayloadBytes=count*56,metadataInputBytes=count*64,
                               textureCopiesPerPoolSlot=2),
                caveats=['Selected array element payload only; object field changes, headers and allocator rounding excluded.',
                         'Managed heap reservation is unchanged; these are occupancy reductions.',
                         'Cold ARAM, permanent geometry, active/idle texture pairs, framebuffers and SDK heaps are separate.',
                         'The optional cold-write ring is extra MEM1; only physically retired residents create further savings.',
                         'Unsupported large records retain their existing demand-time rejection.'])


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pack',type=Path)
    parser.add_argument('--maximum-read-batch-bytes',type=int,default=65536)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    result=json.dumps(audit(args.pack,args.maximum_read_batch_bytes),indent=2,sort_keys=True)+'\n'
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(result)
    else:
        print(result,end='')
if __name__=='__main__':main()
