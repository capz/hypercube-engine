#!/usr/bin/env python3
"""Evidence admission for PR11: matched-quality strategy comparisons and hardware gates.
Input records are measured by the application/capture operator, never synthesized
by this tool. See docs/progressive-production.md for the versioned run contract.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess

TIERS=('activeBytes','uploadBytes','readBufferBytes','retiredBytes','poolBytes','externalBytes')
MATCHED=('contentSha256','sceneSha256','cameraTraceSha256','shaderSha256','samplerSha256','width','height','format','texelsPerWorldUnit','physicalCeilingBytes','measuredStartFrame','sdkRevision','platform','storage')
CHECKS=('coarseBeforeDetail','noHoles','noStaleDraws','noUvJumps','noCorruptPixels','corruptNeverPublished','cancelNeverPublished','pairReachedHigh','noPromotionGpuWait','noSynchronousWholeTextureRead')
STRESS=('camera-turn','multiple-objects','budget-pressure','camera-reversal','corrupt-read','cancel-read')

def require(value,message):
    if not value:raise ValueError(message)

def numeric(value,minimum=0):
    require(type(value) in (int,float) and math.isfinite(value) and value>=minimum,'Invalid measured number')
    return value

def digest(value):require(isinstance(value,str) and re.fullmatch('[0-9a-f]{64}',value),'Missing SHA256 identity')

def percentile(values,p):
    values=sorted(values);return values[max(0,math.ceil(len(values)*p/100)-1)]

def validate_run(run,hardware=False):
    require(run['schema']==1 and run['platform'] in ('Dolphin','GameCube') and run['storage'] in ('sd2sp2','disc'),'Run source/version')
    require(run['strategy'] in ('fine-pages','cell-atlas'),'Strategy')
    for field in MATCHED:
        if field.endswith('Sha256'):digest(run[field])
    for field in ('dolSha256','packSha256'):digest(run[field])
    require(re.fullmatch('[0-9a-f]{40}',run['sdkRevision']) is not None,'SDK revision')
    for field in ('width','height','texelsPerWorldUnit','physicalCeilingBytes'):numeric(run[field],1)
    require(run['format'] in ('RGBA8','CMPR'),'Native format')
    if hardware:
        require(run['platform']=='GameCube','Emulator timing cannot satisfy hardware acceptance')
        require(isinstance(run.get('console'),str) and bool(run['console'].strip()),'Identify physical console and video mode')
    require(all(run['checks'].get(k) is True for k in CHECKS),'Missing/failed correctness check')
    require(set(STRESS)<=set(run['stressCases']),'Missing stress workload')
    require(type(run['runId'])==str and bool(run['runId'].strip()),'Unique run identity required')
    start=run['measuredStartFrame'];require(type(start)==int and start>=0,'Measured camera window start')
    frames=run['frames'];require(len(frames)>=300,'At least 300 measured frames required')
    require(type(run['captureFrame'])==int and start<=run['captureFrame']<start+len(frames),'Capture is outside measured camera window')
    times=[];peak=0;aram_peak=0
    for index,frame in enumerate(frames):
        require(frame['frame']==start+index,'Missing/reordered camera frame')
        times.append(numeric(frame['frameUs'],1));require(frame['audioActive'] is True,'Audio stopped during pressure')
        memory=frame['memory'];total=sum(numeric(memory[k]) for k in TIERS);aram_peak=max(aram_peak,numeric(memory['aramBytes']))
        require(total==numeric(memory['physicalBytes']) and total<=run['physicalCeilingBytes'],'Physical accounting/ceiling mismatch')
        peak=max(peak,total)
    require(all(run['finalMemory'].get(k)==0 for k in (*TIERS,'aramBytes','physicalBytes')),'Native/ARAM allocation survived final fence/trim')
    for key in ('coarseReadyUs','detailReadyUs'):
        require(len(run[key])>=4,'Missing readiness observations')
        for value in run[key]:numeric(value,1)
    for key in ('storageBytes','aramReadBytes','draws','textureBinds','cancellations','checksumFailures','pressureDeferrals'):numeric(run[key])
    require(run['cancellations']>0 and run['checksumFailures']>0 and run['pressureDeferrals']>0,'Failures/pressure were not exercised')
    return dict(frame95Us=percentile(times,95),frame99Us=percentile(times,99),coarseReady95Us=percentile(run['coarseReadyUs'],95),detailReady95Us=percentile(run['detailReadyUs'],95),physicalPeakBytes=peak,aramPeakBytes=aram_peak,frames=len(frames))

def rgb(path,width,height):
    result=subprocess.run(['ffmpeg','-threads','1','-v','error','-i',str(path),'-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','-'],capture_output=True,check=True)
    require(len(result.stdout)==width*height*3,'Capture dimensions disagree');return result.stdout

def compare(a,b,reference,page_image,atlas_image,max_rmse=2):
    aa=validate_run(a,hardware=a.get('platform')=='GameCube');bb=validate_run(b,hardware=b.get('platform')=='GameCube')
    require(a['strategy']=='fine-pages' and b['strategy']=='cell-atlas','Order strategies: pages, atlas')
    require(all(a[k]==b[k] for k in MATCHED),'Quality, workload, SDK or physical ceiling is not matched')
    # All three captures must represent the same deterministic camera sample.
    require(a['captureFrame']==b['captureFrame'] and len(a['frames'])==len(b['frames']),'Measured camera window/capture differs')
    ref=rgb(reference,a['width'],a['height']);images=[rgb(path,a['width'],a['height']) for path in (page_image,atlas_image)]
    errors=[math.sqrt(sum((x-y)**2 for x,y in zip(ref,pixels))/len(ref)) for pixels in images]
    require(all(error<=max_rmse for error in errors),'Screenshot quality differs from the shared full-detail reference')
    return dict(schema=1,hardwarePerformanceClaim=a['platform']=='GameCube',matched=True,storage=a['storage'],runIds=[a['runId'],b['runId']],maximumRmse=max_rmse,rmse=dict(pages=errors[0],atlas=errors[1]),pages=aa,atlas=bb,comparison={k:dict(pages=a[k],atlas=b[k]) for k in ('storageBytes','aramReadBytes','draws','textureBinds')},evidenceSha256={str(p):hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in (reference,page_image,atlas_image)})

def hardware_gate(runs,maximum_frame99_us,maximum_coarse95_us,maximum_detail95_us):
    require(len(runs)>=12,'Require three repeats of both strategies on SD2SP2 and disc')
    results=[]
    require(len({r['runId'] for r in runs})==len(runs),'Every hardware run needs a unique identity')
    for storage in ('sd2sp2','disc'):
        for strategy in ('fine-pages','cell-atlas'):
            group=[r for r in runs if r['storage']==storage and r['strategy']==strategy]
            require(len(group)>=3,'Missing strategy/storage repeats')
            require(len({r['runId'] for r in group})==len(group),'Repeated run identity')
            first=group[0]
            require(all(all(r[k]==first[k] for k in MATCHED) and len(r['frames'])==len(first['frames']) for r in group),'Repeat workload changed')
    for storage in ('sd2sp2','disc'):
        group=[r for r in runs if r['storage']==storage];first=group[0]
        require(all(all(r[k]==first[k] for k in MATCHED) and len(r['frames'])==len(first['frames']) for r in group),'Hardware strategies do not match quality/workload/ceiling')
    for run in runs:
        result=validate_run(run,hardware=True)
        require(result['frame99Us']<=maximum_frame99_us and result['coarseReady95Us']<=maximum_coarse95_us and result['detailReady95Us']<=maximum_detail95_us,'Hardware performance threshold failed')
        results.append(dict(runId=run['runId'],storage=run['storage'],strategy=run['strategy'],**result))
    return dict(schema=1,hardwarePerformanceClaim=True,passed=True,thresholds=dict(frame99Us=maximum_frame99_us,coarse95Us=maximum_coarse95_us,detail95Us=maximum_detail95_us),runs=results)

def main():
    p=argparse.ArgumentParser(description=__doc__);sub=p.add_subparsers(dest='command',required=True)
    c=sub.add_parser('compare');c.add_argument('pages',type=Path);c.add_argument('atlas',type=Path)
    for flag in ('reference','page-image','atlas-image'):c.add_argument('--'+flag,type=Path,required=True)
    c.add_argument('--maximum-rmse',type=float,default=2)
    h=sub.add_parser('hardware');h.add_argument('runs',type=Path,nargs='+');h.add_argument('--comparison',type=Path,action='append',required=True,help='Six accepted page/atlas screenshot comparison reports')
    for flag in ('frame99-us','coarse95-us','detail95-us'):h.add_argument('--maximum-'+flag,type=int,required=True)
    p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    if args.command=='compare':
        numeric(args.maximum_rmse);result=compare(json.loads(args.pages.read_text()),json.loads(args.atlas.read_text()),args.reference,args.page_image,args.atlas_image,args.maximum_rmse)
    else:
        for value in (args.maximum_frame99_us,args.maximum_coarse95_us,args.maximum_detail95_us):numeric(value,1)
        result=hardware_gate([json.loads(path.read_text()) for path in args.runs],args.maximum_frame99_us,args.maximum_coarse95_us,args.maximum_detail95_us)
        require(len(args.comparison)>=6,'Require matched screenshot evidence for each hardware pair')
        covered=set();comparisons=[]
        for path in args.comparison:
            report=json.loads(path.read_text());require(report.get('schema')==1 and report.get('matched') is True and report.get('hardwarePerformanceClaim') is True,'Hardware matched-quality evidence required')
            hashes=report.get('runEvidenceSha256',{});require(len(hashes)==2,'Missing comparison input hashes');covered.update(hashes.values());comparisons.append(dict(path=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
        require(all(hashlib.sha256(path.read_bytes()).hexdigest() in covered for path in args.runs),'A hardware run lacks matched screenshot evidence')
        result['comparisons']=comparisons
    inputs=[args.pages,args.atlas] if args.command=='compare' else args.runs
    result['runEvidenceSha256']={str(path):hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs}
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2,sort_keys=True)+'\n')
if __name__=='__main__':main()
