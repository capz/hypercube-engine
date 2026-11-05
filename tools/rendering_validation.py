#!/usr/bin/env python3
"""Direct-DOL proof of the shared rendering helpers; no data disc or audio required."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time
from dolphin_validation import telemetry, ffmpeg, W, H, SIZE

FIELDS=('magic','version','frame','phase','uploads','batchCalls','apiCalls','gxDraws','triangles','instances','culled','rebuilds','growths')+tuple(f'reserved{i}' for i in range(13,31))+('checksum',)

def validate(video,root):
    rows=[];trailing_invalid=0
    with (root/'decode.log').open('wb') as log:
        process=subprocess.Popen(['ffmpeg','-v','error','-i',str(video),'-vf','fps=5,scale=640:480:flags=neighbor','-f','rawvideo','-pix_fmt','rgb24','-'],stdout=subprocess.PIPE,stderr=log)
        try:
            while True:
                rgb=process.stdout.read(SIZE)
                if not rgb:break
                if len(rgb)!=SIZE:raise AssertionError('Short frame')
                try:row=telemetry(rgb,FIELDS,9)
                except ValueError:trailing_invalid+=1;continue
                trailing_invalid=0
                checksum=0
                for key in FIELDS[:-1]:checksum^=row[key]
                if checksum!=row['checksum']:raise AssertionError('Corrupt rendering telemetry')
                rows.append(row)
            if process.wait()!=0:raise AssertionError('Frame decoder failed')
        finally:
            if process.poll() is None:process.kill();process.wait()
    if trailing_invalid>5:raise AssertionError("Guest rendering stopped before the capture ended")
    proof=validate_rows(rows)
    (root/'results.json').write_text(json.dumps(proof,indent=2)+'\n')
    return proof

def validate_rows(rows):
    if len(rows)<10:raise AssertionError('No sustained rendering telemetry')
    if max(r['frame'] for r in rows)<540 or len({r['frame'] for r in rows[-10:]})<5:raise AssertionError('Guest did not sustain progress past retirement and cover cases')
    phases={r['phase'] for r in rows}
    if phases!={0,1,2}:raise AssertionError(f'Missing cloud-cover cases: {phases}')
    for r in rows:
        if r['uploads']!=(1 if r['frame']<120 else 2):raise AssertionError('Repeated/missing dirty mesh upload')
        if not (1<=r['batchCalls']<=2 and r['apiCalls']==1+r['batchCalls']):raise AssertionError('Opaque grouping did not bound API crossings')
        if r['instances']+r['culled']!=16 or r['triangles']<1 or r['gxDraws']<1:raise AssertionError('Missing geometry/culling metrics')
        if r['rebuilds'] not in (0,1) or r['growths']!=0:raise AssertionError('Steady state allocated/rebuilt geometry')
    proof={'frames':len(rows),'phases':sorted(phases),'dirtyUploads':max(r['uploads'] for r in rows),'maxBatchCalls':max(r['batchCalls'] for r in rows),'maxApiCalls':max(r['apiCalls'] for r in rows),'maxGxDraws':max(r['gxDraws'] for r in rows),'maxTriangles':max(r['triangles'] for r in rows)}
    return proof

def run(args):
    root=args.logs.resolve();root.mkdir(parents=True,exist_ok=False);config=root/'user'/'Config';config.mkdir(parents=True)
    (config/'Dolphin.ini').write_text('[Core]\nGFXBackend = OGL\nCPUCore = 1\nCPUThread = False\nDSPHLE = True\n[Display]\nRenderWindowWidth = 640\nRenderWindowHeight = 480\n[DSP]\nBackend = No Audio Output\n[Movie]\nDumpFrames = True\nDumpFramesSilent = True\n')
    (config/'GFX.ini').write_text('[Settings]\nSafeTextureCacheColorSamples = 0\nDumpFormat = avi\nDumpCodec = ffv1\nDumpEncoder = ffv1\nDumpPixelFormat = bgr0\n')
    env=dict(os.environ,SDL_AUDIODRIVER='dummy',LP_NUM_THREADS='2',LIBGL_ALWAYS_SOFTWARE='1')
    with (root/'stdout.log').open('w') as log:
        process=subprocess.Popen(['xvfb-run','-a',args.binary,'--user',str(root/'user'),'--exec',str(args.dol.resolve())],env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try:
            deadline=time.monotonic()+args.seconds
            while time.monotonic()<deadline:
                if process.poll() is not None:raise AssertionError('Emulator exited during rendering sample')
                time.sleep(.2)
        finally:
            if process.poll() is None:
                os.killpg(process.pid,signal.SIGTERM)
                try:process.wait(timeout=5)
                except subprocess.TimeoutExpired:os.killpg(process.pid,signal.SIGKILL);process.wait()
    videos=list((root/'user'/'Dump'/'Frames').glob('*.avi'))
    if len(videos)!=1:raise AssertionError(f'Expected one frame capture: {videos}')
    ffmpeg(['-sseof','-1','-i',str(videos[0]),'-frames:v','1','-y',str(root/'tail.png')],root/'tail.log')
    print(json.dumps(validate(videos[0],root)))

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('dol',type=Path);p.add_argument('--logs',type=Path,required=True);p.add_argument('--seconds',type=float,default=35);p.add_argument('--binary',default='/usr/games/dolphin-emu-nogui');run(p.parse_args())
