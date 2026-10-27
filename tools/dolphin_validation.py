#!/usr/bin/env python3
"""Boot Validation.dol with its data ISO and check rendered scene + guest telemetry."""
import argparse
import array
import json
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import time
import wave

W,H=640,480
SIZE=W*H*3
FIELDS=('magic','version','frame','scenario','caseFrames','dynamicKiB','nativeKiB','residentKiB','frame99ms','publications','reads','storageKiB','aramKiB','aramHits','reuse','evictions','failures','cancelled','maxBatch','multiPublish','draws','triangles','fallbacks','visibleCells','actorX256','actorY256','actorZ256','blocked','audioActive','recycledUploads','occluded','completedMask')

def telemetry(rgb,fields=FIELDS,version=2):
    values=[]
    for field in range(32):
        value=0
        for bit in range(16):
            index=field*16+bit
            at=((450+index//64*4)*W+index%64*10+5)*3
            pixel=rgb[at:at+3]
            if max(pixel)-min(pixel)>12: raise ValueError('Corrupt telemetry')
            if sum(pixel)>384:value|=1<<bit
        values.append(value)
    state=dict(zip(fields,values))
    if values[:2]!=[0x4843,version]:raise ValueError('No guest telemetry')
    return state

def ffmpeg(args,log,**kwargs):
    with log.open('wb') as err:
        return subprocess.run(['ffmpeg','-threads','1','-v','error',*args],stderr=err,check=True,**kwargs)

def scene_pixels(rgb):
    # Exclude console and telemetry. Inspect actual color, independently of counters.
    world=actor=0
    for y in range(180,448,2):
        for x in range(0,W,2):
            at=(y*W+x)*3;r,g,b=rgb[at:at+3]
            if b>r+15 and b>g+10:world+=1
            if r>150 and g>120 and b<90:actor+=1
    return world,actor

def ground_coverage(rgb,state):
    fx,fz=(12,4) if state['scenario']>=9 else (state['actorX256']/256,state['actorZ256']/256)
    tested=missing=0
    for z in (1,5,9,13):
        for x in (1,5,9,13,17,21):
            h=.22*math.sin(x*.9)*math.cos(z*.7)
            depth=-.6*(h-12)-.8*(z-fz-16)
            if depth<=.1:continue
            px=round(320+(x-fx)/(depth*.52056705*4/3)*320)
            py=round(240-(.8*(h-12)-.6*(z-fz-16))/(depth*.52056705)*240)
            if not(12<=px<628 and 180<=py<436):continue
            at=(py*640+px)*3;r,g,b=rgb[at:at+3]
            if r>150 and g>120 and b<90:continue  # Probe actor can hide a ground sample.
            tested+=1;missing+=b<45
    if tested<2 or missing>1:raise AssertionError(f'Ground coverage holes: {missing}/{tested} at {fx},{fz}')
    return tested

def validate(capture,root,smoke):
    # A streaming decoder bounds memory independently of suite duration.
    command=['ffmpeg','-threads','1','-v','error','-i',str(capture),'-vf','fps=2,scale=640:480:flags=neighbor','-f','rawvideo','-pix_fmt','rgb24','pipe:1']
    states=[];completed={};images={};invalid=0;last_mask=0;baseline=None;pixels={};actor_pixels={};positions={}
    with (root/'decode.log').open('wb') as err:
        process=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=err)
        while True:
            rgb=process.stdout.read(SIZE)
            if not rgb:break
            if len(rgb)!=SIZE:raise AssertionError('Truncated RGB frame')
            try:s=telemetry(rgb)
            except ValueError:
                invalid+=1
                if states:
                    (root/'invalid.rgb').write_bytes(rgb)
                    (root/'states.json').write_text(json.dumps(states,indent=2)+'\n')
                    ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',str(root/'failure.png')],root/'failure-encode.log',input=rgb)
                    raise AssertionError('Guest stopped emitting telemetry after startup')
                continue
            if not states:
                ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',str(root/'first.png')],root/'first-encode.log',input=rgb)
            states.append(s);case=s['scenario']
            if not 0<=case<11:raise AssertionError('Unknown scenario')
            if s['dynamicKiB']>(48 if case==8 else 384) or s['residentKiB']>s['dynamicKiB']+1:raise AssertionError('Dynamic budget exceeded')
            current=s['nativeKiB']-s['dynamicKiB']
            if baseline is None:baseline=current
            if abs(current-baseline)>1:raise AssertionError('Resources leaked between cases')
            if s['audioActive']!=1:raise AssertionError('Audio voice stopped')
            if s['completedMask']|last_mask!=s['completedMask']:raise AssertionError('Completion mask regressed')
            last_mask=s['completedMask']
            try:ground_coverage(rgb,s)
            except AssertionError:
                (root/'coverage.rgb').write_bytes(rgb)
                (root/'states.json').write_text(json.dumps(states,indent=2)+'\n')
                ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',str(root/'failure.png')],root/'failure-encode.log',input=rgb)
                raise
            world,actor=scene_pixels(rgb)
            pixels[case]=max(pixels.get(case,0),world)
            actor_pixels[case]=max(actor_pixels.get(case,0),actor)
            positions.setdefault(case,set()).add((s['actorX256'],s['actorZ256']))
            if s['completedMask']&(1<<case):
                completed[case]=s;images[case]=rgb
        if process.wait()!=0:raise AssertionError('Capture could not be decoded')
    (root/'states.json').write_text(json.dumps(states,indent=2)+'\n')
    if not states:raise AssertionError('No rendered guest telemetry; inspect emulator log and capture')
    for case,rgb in images.items():
        ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',str(root/f'case-{case:02}.png')],root/'encode.log',input=rgb)
    if smoke:
        if len(states)<3 or states[-1]['frame']<10 or max(pixels.values())<500:raise AssertionError('Insufficient rendered smoke frames')
    else:
        if set(completed)!=set(range(11)) or last_mask!=2047:raise AssertionError(f'Missing completed scenarios: {sorted(completed)}; last {states[-1]}')
        for case,s in completed.items():
            if s['caseFrames']<60 or pixels.get(case,0)<500:raise AssertionError(f'Insufficient rendered scene in case {case}')
        for case in range(9):
            xs=[p[0] for p in positions[case]]
            if actor_pixels.get(case,0)<2 or max(xs)-min(xs)<6*256:raise AssertionError(f'No visible moving actor in case {case}')
        if max(s['maxBatch'] for s in states)<2 or max(s['multiPublish'] for s in states)<1:raise AssertionError('No actual batched multi-resource publication')
        if max(s['reuse'] for s in states)<1:raise AssertionError('No texture allocation reuse')
        if max(s['recycledUploads'] for s in states)<1:raise AssertionError('No obsolete completed staging was recycled')
        if max(s['aramHits'] for s in states if s['scenario']==6)<1:raise AssertionError('ARAM path was not exercised')
        if max(s['failures'] for s in states if s['scenario'] in (7,8))<1:raise AssertionError('Injected failures were not detected')
        if max(s['blocked'] for s in states)<1:raise AssertionError('Collision readiness gate was not exercised')
        if completed[10]['occluded']<1 or completed[10]['draws']>=completed[9]['draws']:raise AssertionError('Occlusion did not reduce submitted geometry')
        # Fixed camera, actor, material and leaf LOD in both cases. Ignore HUD.
        a,b=images[9],images[10];different=total=0;max_delta=0
        for y in range(132,444):
            for x in range(W):
                at=(y*W+x)*3;delta=max(abs(a[at+i]-b[at+i]) for i in range(3))
                total+=1;different+=delta>8;max_delta=max(max_delta,delta)
        if different:raise AssertionError(f'Occlusion changed visible pixels: {different}/{total}, max delta {max_delta}')
    proof={'smoke':smoke,'sampledFrames':len(states),'completedCases':list(completed.values()),'maximumWorldPixels':pixels,'baselineResourceKiB':baseline,'last':states[-1]}
    if not smoke:proof['occlusionComparison']=dict(referenceDraws=completed[9]['draws'],culledDraws=completed[10]['draws'],comparedPixels=total,changedPixels=different,maxChannelDifference=max_delta)
    return proof

def audio_proof(root,smoke,minimum_seconds=100):
    proofs=[]
    for path in sorted((root/'user'/'Dump'/'Audio').glob('*dspdump*.wav')):
        with wave.open(str(path)) as wav:
            if wav.getsampwidth()!=2:raise AssertionError('Unexpected DSP sample format')
            frames=active=quiet=longest=0;started=False;window=max(1,wav.getframerate()//10)
            while block:=wav.readframes(window):
                samples=array.array('h');samples.frombytes(block)
                peak=max(map(abs,samples),default=0);frames+=len(samples)//wav.getnchannels()
                if peak>50:started=True;active+=1;quiet=0
                elif started:quiet+=1;longest=max(longest,quiet)
            if started:
                proofs.append(dict(file=path.name,seconds=frames/wav.getframerate(),activeWindows=active,longestQuietMs=longest*100))
    if not proofs or max(p['seconds'] for p in proofs)<(5 if smoke else minimum_seconds):raise AssertionError('No sustained non-silent DSP output')
    if max(p['longestQuietMs'] for p in proofs)>300:raise AssertionError('Audio output contains a long silence during streaming')
    (root/'audio.json').write_text(json.dumps(proofs,indent=2)+'\n')
    return proofs

def run(args):
    root=args.logs.resolve();root.mkdir(parents=True,exist_ok=False)
    config=root/'user'/'Config';config.mkdir(parents=True)
    (config/'Dolphin.ini').write_text(f'[Core]\nGFXBackend = OGL\nCPUCore = 1\nCPUThread = False\nDSPHLE = {args.dsp=="HLE"}\nDefaultISO = {args.disc.resolve()}\n[DSP]\nDSPThread = False\nBackend = No Audio Output\nDumpAudio = True\n[Movie]\nDumpFrames = True\nDumpFramesSilent = True\n')
    (config/'GFX.ini').write_text('[Settings]\nSafeTextureCacheColorSamples = 0\nDumpFormat = avi\nDumpCodec = ffv1\nDumpEncoder = ffv1\nDumpPixelFormat = bgr0\n')
    (config/'Logger.ini').write_text('[Options]\nWriteToFile = True\nWriteToConsole = True\nVerbosity = 3\n[Logs]\nBOOT = True\nCORE = True\nPOWERPC = True\nOSREPORT = True\nOSREPORT_HLE = True\n')
    env=dict(os.environ,SDL_AUDIODRIVER='dummy',LP_NUM_THREADS='2',LIBGL_ALWAYS_SOFTWARE='1')
    env.update(DOLPHIN_EMU_USERPATH=str(root/'user'))
    for key,name in [('XDG_CONFIG_HOME','config'),('XDG_CACHE_HOME','cache'),('XDG_DATA_HOME','data')]:
        (root/name).mkdir();env[key]=str(root/name)
    with (root/'stdout.log').open('w') as log:
        process=subprocess.Popen(['xvfb-run','-a',args.binary,'--user',str(root/'user'),'--exec',str(args.dol.resolve())],stdout=log,stderr=subprocess.STDOUT,env=env,start_new_session=True)
        try:
            start=time.monotonic()
            while time.monotonic()-start<args.seconds:
                if process.poll() is not None:raise RuntimeError(f'Dolphin exited: {process.returncode}')
                time.sleep(.2)
        finally:
            if process.poll() is None:
                os.killpg(process.pid,signal.SIGTERM)
                try:process.wait(timeout=5)
                except subprocess.TimeoutExpired:pass
            try:os.killpg(process.pid,signal.SIGKILL)
            except ProcessLookupError:pass
            process.wait()
    captures=list((root/'user'/'Dump'/'Frames').glob('*.avi'))
    if len(captures)!=1:raise AssertionError(f'Expected one capture, got {captures}')
    ffmpeg(['-sseof','-1','-i',str(captures[0]),'-frames:v','1','-y',str(root/'tail.png')],root/'tail-encode.log')
    if args.suite=='profiling':
        from profiling_validation import validate as validate_profiling
        proof=validate_profiling(captures[0],root,args.smoke)
    elif args.suite=='performance':
        from performance_validation import validate as validate_performance
        proof=validate_performance(captures[0],root,args.smoke)
    else:proof=validate(captures[0],root,args.smoke)
    proof['audio']=audio_proof(root,args.smoke,minimum_seconds=70 if args.suite=='profiling' else 80 if args.suite=='performance' else 100)
    (root/'results.json').write_text(json.dumps(proof,indent=2)+'\n');print(json.dumps(proof))

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('dol',type=Path);p.add_argument('--disc',required=True,type=Path)
    p.add_argument('--suite',choices=['validation','performance','profiling'],default='validation');p.add_argument('--dsp',choices=['HLE','LLE'],default='LLE');p.add_argument('--logs',type=Path,required=True);p.add_argument('--seconds',type=float,default=200);p.add_argument('--smoke',action='store_true');p.add_argument('--binary',default='/usr/games/dolphin-emu-nogui');run(p.parse_args())
