#!/usr/bin/env python3
"""Rendered progressive streaming, both texture slots, affine gutters and audio."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time
from dolphin_validation import SIZE,ffmpeg,audio_proof
FIELDS=('magic','phase','frame','publications','crcFailures','failures','stale','active32','upload32','retired32','aram32','coarseMask','deferred','retirements','frame99ms','aramHits','detailMask','peakKiB','audioActive','coarseReady','detailReady','coarseMeanMs','detailMeanMs','maxPumpUs','storageKiB','coldKiB','uploadedKiB','cancelled','frame95ms','readBufferKiB','poolBytes','checksum')
def telemetry(rgb):
    if len(rgb)!=SIZE:raise ValueError('Incomplete frame')
    fields=[]
    for k in range(32):
        value=0
        for bit in range(16):
            at=((436+k//16*10)*640+32+((k%16)*16+bit)*2)*3
            pixel=rgb[at:at+3]
            if max(pixel)-min(pixel)>12:raise ValueError('Corrupt telemetry raster')
            if sum(pixel)>384:value|=1<<bit
        fields.append(value)
    checksum=0
    for value in fields[:-1]:checksum^=value
    if fields[0]!=0x4854 or checksum!=fields[-1]:raise ValueError('No progressive telemetry')
    return dict(zip(FIELDS,fields))
def patch(rgb,x,y,color):
    at=(y*640+x)*3
    if max(abs(a-b) for a,b in zip(rgb[at:at+3],color))>12:raise AssertionError(f'UV/pixel discontinuity at {x},{y}: {tuple(rgb[at:at+3])}, expected {color}')
def validate_rows(rows):
    if len(rows)<10 or rows[-1]['phase']!=10 or len({r['frame'] for r in rows[-10:]})<5:raise AssertionError('Stress scenario did not complete and sustain frames')
    final=rows[-1]
    if final['coarseMask']!=15 or final['crcFailures']<1 or final['failures']<2 or final['stale']<1 or final['deferred']<1 or final['cancelled']<1 or final['aramHits']<1:raise AssertionError(f'Incomplete failure/pressure/ARAM coverage: {final}')
    if any(final[k]!=0 for k in ('active32','upload32','retired32','aram32','readBufferKiB','poolBytes')):raise AssertionError('Unwanted allocations survived final fences and explicit trim')
    if any(r['audioActive']!=1 for r in rows):raise AssertionError('Audio stopped during stress')
    return dict(platform='Dolphin',hardwarePerformanceClaim=False,sampledFrames=len(rows),last=final)
def validate(video,root):
    rows=[];coarse=fine=0;invalid=0;images={}
    with (root/'decode.log').open('wb') as log:
        decoder=subprocess.Popen(['ffmpeg','-threads','1','-v','error','-i',str(video),'-vf','fps=10,scale=640:480:flags=neighbor','-f','rawvideo','-pix_fmt','rgb24','-'],stdout=subprocess.PIPE,stderr=log)
        try:
            while True:
                rgb=decoder.stdout.read(SIZE)
                if not rgb:break
                if len(rgb)!=SIZE:raise AssertionError('Incomplete decoded frame')
                try:row=telemetry(rgb)
                except ValueError:invalid+=1;continue
                invalid=0;rows.append(row)
                if row['phase']==1:
                    for x,y in ((100,180),(220,180),(100,300),(220,300)):patch(rgb,x,y,(255,0,0))
                    coarse+=1;images['coarse']=rgb
                if row['phase']==4 and row['detailMask']==15:
                    for shift in (0,320):
                        for x,y,color in ((100,180,(0,255,0)),(220,180,(0,255,255)),(100,300,(255,0,0)),(220,300,(255,0,255))):patch(rgb,x+shift,y,color)
                    fine+=1;images['fine-dual-uv']=rgb
            if decoder.wait()!=0:raise AssertionError('Frame decoder failed')
        finally:
            if decoder.poll() is None:decoder.kill();decoder.wait()
    (root/'states.json').write_text(json.dumps(rows,indent=2)+'\n')
    if invalid>3 or coarse<2 or fine<3:raise AssertionError(f'Missing sustained coarse/fine pixel evidence ({coarse}, {fine}, trailing invalid {invalid})')
    proof=validate_rows(rows);proof.update(coarsePixelFrames=coarse,fineDualUvPixelFrames=fine,pixelPatches=12)
    for name,rgb in images.items():ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',str(root/(name+'.png'))],root/'encode.log',input=rgb)
    return proof
def run(args):
    if not 10<=args.seconds<=300:raise ValueError('Use a bounded 10–300 second capture')
    root=args.logs.resolve();root.mkdir(parents=True,exist_ok=False);config=root/'user/Config';config.mkdir(parents=True)
    (config/'Dolphin.ini').write_text(f'[Core]\nGFXBackend = OGL\nCPUCore = 1\nCPUThread = False\nDSPHLE = False\nDefaultISO = {args.disc.resolve()}\n[DSP]\nDSPThread = False\nBackend = No Audio Output\nDumpAudio = True\n[Movie]\nDumpFrames = True\nDumpFramesSilent = True\n')
    (config/'GFX.ini').write_text('[Settings]\nSafeTextureCacheColorSamples = 0\nDumpFormat = avi\nDumpCodec = ffv1\nDumpEncoder = ffv1\nDumpPixelFormat = bgr0\n')
    (config/'Logger.ini').write_text('[Options]\nWriteToFile = True\nVerbosity = 3\n[Logs]\nBOOT = True\nCORE = True\nPOWERPC = True\nOSREPORT = True\n')
    env=dict(os.environ,SDL_AUDIODRIVER='dummy',LP_NUM_THREADS='2',LIBGL_ALWAYS_SOFTWARE='1',DOLPHIN_EMU_USERPATH=str(root/'user'))
    with (root/'stdout.log').open('w') as log:
        process=subprocess.Popen(['xvfb-run','-a',args.binary,'--user',str(root/'user'),'--exec',str(args.dol.resolve())],env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try:
            deadline=time.monotonic()+args.seconds
            while time.monotonic()<deadline:
                if process.poll() is not None:raise AssertionError('Dolphin exited during progressive streaming')
                time.sleep(.2)
        finally:
            if process.poll() is None:
                os.killpg(process.pid,signal.SIGTERM)
                try:process.wait(timeout=5)
                except subprocess.TimeoutExpired:os.killpg(process.pid,signal.SIGKILL);process.wait()
    videos=list((root/'user/Dump/Frames').glob('*.avi'))
    if len(videos)!=1:raise AssertionError('Missing frame capture')
    ffmpeg(['-sseof','-1','-i',str(videos[0]),'-frames:v','1','-y',str(root/'tail.png')],root/'tail.log')
    proof=validate(videos[0],root);proof['audio']=audio_proof(root,False,minimum_seconds=min(15,args.seconds/2))
    (root/'results.json').write_text(json.dumps(proof,indent=2)+'\n');print(json.dumps(proof))
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('dol',type=Path);p.add_argument('--disc',type=Path,required=True);p.add_argument('--logs',type=Path,required=True);p.add_argument('--seconds',type=float,default=40);p.add_argument('--binary',default='/usr/games/dolphin-emu-nogui');run(p.parse_args())
