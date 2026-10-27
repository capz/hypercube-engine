"""Protocol v6: exclusive frame phases plus deliberate work-isolation controls."""
import json
import csv
import subprocess

NAMES=('BASE','COARSE ONLY','TEXTURE ONLY','NO WORLD DRAW','NO DELAY','BASE REPEAT','FAST ID','TREE MASKS','REUSE READY','DEMAND PLAN','PREP COMBINED','SDK WAITS','SDK NATIVE IO','SDK POLL64','SDK COMBINED','SDK AND PREP')
FLAGS=(0,1,2,4,8,0,16,32,64,128,240,1024,256,512,1792,2032)
PHASES=('streamOther','decode','meshCreate','draw','present','yield','hud','prepare','other')
COMMON=('magic','version','frame','scenario','samples','budgetKiB','dynamicKiB','completedMask',
        'page','flags','actorX256','actorZ256','audioActive','failures','selectedPermille','blocked')
FIELDS=COMMON+tuple(f'word{i}' for i in range(16))


def check_packet(s):
    case=s['scenario']
    if not 0<=case<len(NAMES) or s['flags']!=FLAGS[case] or not 0<=s['page']<3:
        raise AssertionError('Unknown profiling case/page/flags')
    if s['budgetKiB'] not in (192,384,768) or s['dynamicKiB']>s['budgetKiB']:
        raise AssertionError('Invalid profiling budget')
    if s['audioActive']!=1 or s['failures']!=0:
        raise AssertionError('Audio stopped or unexpected streaming failure')
    if not 0<=s['selectedPermille']<=1000:
        raise AssertionError('Invalid detail readiness')


class Collector:
    def __init__(self):
        self.pages={};self.identities={};self.budget=None

    def observe(self,s):
        check_packet(s)
        if self.budget is None:self.budget=s['budgetKiB']
        if self.budget!=s['budgetKiB']:raise AssertionError('Mixed budgets in one profiling run')
        case=s['scenario']
        if not s['completedMask'] & (1<<case):return
        # Completed pages must describe one frozen sample window, not live/stale rows.
        identity=tuple(s[k] for k in COMMON if k not in ('page','completedMask'))
        if case in self.identities and self.identities[case]!=identity:
            raise AssertionError('Completed profiling pages changed')
        self.identities[case]=identity
        self.pages.setdefault(case,{})[s['page']]=s

    def result(self,case):
        pages=self.pages.get(case,{})
        if set(pages)!={0,1,2}:raise AssertionError(f'Missing profiling pages for {NAMES[case]}')
        a,b,c=(pages[p] for p in range(3))
        result={k:a[k] for k in COMMON if k not in ('magic','version','page')}
        result.update(name=NAMES[case],isolation=bool(FLAGS[case]&15),
                      frame50ms=a['word0'],frame95ms=a['word1'],frame99ms=a['word2'],
                      frameMeanUs=a['word3']*10,frameMaxMs=a['word13'],
                      decodeCallMaxUs=a['word14']*10,createCallMaxUs=a['word15']*10,
                      readLatencyMeanUs=b['word9']*10,readLatencyMaxUs=b['word10']*10,
                      readObservations=b['word11'],decodeCalls=b['word12'],drawsMean=b['word13']/100,demandPlanBuilds=b['word15'],
                      trianglesMean=b['word14'],reads=c['word0'],storageKiB=c['word1'],
                      publications=c['word2'],evictions=c['word3'],deferred=c['word4'],cancelled=c['word5'],
                      detailFrames=c['word6'],checksumUs=c['word7']*10,
                      demandedMean=c['word8']/100,selectedMean=c['word9']/100,
                      streamedMean=c['word10']/100,visibleMean=c['word11']/100,
                      pumpMaxUs=c['word12']*10,uploadedKiB=c['word13'],measuredMs=c['word14'],demandPlanHits=c['word15'],
                      phaseMeanUs={p:a[f'word{4+i}']*10 for i,p in enumerate(PHASES)},
                      phaseMaxUs={p:b[f'word{i}']*10 for i,p in enumerate(PHASES)})
        check_result(result)
        return result


def check_result(r):
    if r['samples']<30 or r['measuredMs']<8000:
        raise AssertionError('Insufficient measured frame window')
    if not 0<r['frame50ms']<=r['frame95ms']<=r['frame99ms']<=r['frameMaxMs']:
        raise AssertionError('Invalid frame percentiles')
    difference=r['frameMeanUs']-sum(r['phaseMeanUs'].values())
    if not 0<=difference<=90:raise AssertionError('Frame phase accounting does not close')
    if any(r['phaseMeanUs'][p]>r['phaseMaxUs'][p] for p in PHASES):
        raise AssertionError('Phase mean exceeds maximum')
    if not 0<=r['selectedMean']<=r['streamedMean']<=r['demandedMean']<=r['visibleMean']:
        raise AssertionError('Invalid cell readiness accounting')
    if r['detailFrames']>r['samples'] or r['readObservations']==0:
        raise AssertionError('Invalid progress/observation counters')
    case=r['scenario']
    if case in (1,2):
        if r['decodeCalls'] or r['selectedMean'] or r['streamedMean']:
            raise AssertionError('Geometry isolation case decoded/published geometry')
    elif r['decodeCalls']==0 or r['detailFrames']<10:
        raise AssertionError('Normal geometry streaming made insufficient progress')
    if case==3 and (r['drawsMean'] or r['trianglesMean']):
        raise AssertionError('No-world-draw case submitted world geometry')
    if case in (9,10,15) and (r['demandPlanHits']==0 or r['demandPlanBuilds']==0):
        raise AssertionError('Demand plan caching was not exercised')
    if case==2 and r['publications']<5:
        raise AssertionError('Texture-only case did not publish assets')


def comparison_rows(results):
    base=results[0]
    rows=[]
    for case in range(6,len(NAMES)):
        r=results[case]
        rows.append(dict(name=r['name'],prepareChangeUs=r['phaseMeanUs']['prepare']-base['phaseMeanUs']['prepare'],
                         frameChangeUs=r['frameMeanUs']-base['frameMeanUs'],frame99ChangeMs=r['frame99ms']-base['frame99ms'],
                         selectedChangePermille=r['selectedPermille']-base['selectedPermille'],
                         blockedChange=r['blocked']-base['blocked'],storageChangeKiB=r['storageKiB']-base['storageKiB']))
    return rows


def write_report(results,root):
    fields=('name','frameMeanUs','frame95ms','frame99ms','prepareUs','selectedPermille','streamedMean','blocked',
            'reads','storageKiB','demandPlanBuilds','demandPlanHits')
    with (root/'comparison.csv').open('w',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=fields);writer.writeheader()
        for r in results:writer.writerow(dict({k:r[k] for k in fields if k!='prepareUs'},prepareUs=r['phaseMeanUs']['prepare']))
    lines=['# Engine and SDK throughput profiling results','',
           'Emulator wall times. Optimizations keep the same demand policy; availability can still differ with execution speed.',
           '', '| Case | Frame mean ms | Prepare ms | F99 ms | Selected ready | Blocked | Storage KiB | Plan hits |',
           '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
    for r in results:
        lines.append(f"| {r['name']} | {r['frameMeanUs']/1000:.2f} | {r['phaseMeanUs']['prepare']/1000:.2f} | {r['frame99ms']} | {r['selectedPermille']/10:.1f}% | {r['blocked']} | {r['storageKiB']} | {r['demandPlanHits']} |")
    lines+=['','Cases COARSE ONLY, TEXTURE ONLY, NO WORLD DRAW and NO DELAY deliberately remove work.',
            'Compare optimization rows with BASE and BASE REPEAT, including detail readiness and blocked movement.',
            'A lower frame time with less detail or more blocked movement does not automatically establish a production improvement.',
            'BASE uses this SDK with legacy reads, stride 1 and productive waits off; the ring queue and compiler fixes apply to every case.']
    (root/'comparison.md').write_text('\n'.join(lines)+'\n')


def validate(capture,root,smoke):
    from dolphin_validation import SIZE,telemetry,scene_pixels,ground_coverage,ffmpeg
    cmd=['ffmpeg','-threads','1','-v','error','-i',str(capture),'-vf','fps=2,scale=640:480:flags=neighbor',
         '-f','rawvideo','-pix_fmt','rgb24','pipe:1']
    states=[];collector=Collector();pixels={};actors={};positions={};last_mask=0;images={}
    with (root/'decode.log').open('wb') as err:
        process=subprocess.Popen(cmd,stdout=subprocess.PIPE,stderr=err)
        try:
            while True:
                rgb=process.stdout.read(SIZE)
                if not rgb:break
                if len(rgb)!=SIZE:raise AssertionError('Truncated frame')
                try:s=telemetry(rgb,fields=FIELDS,version=6)
                except ValueError:
                    if states:raise AssertionError('Lost profiling telemetry')
                    continue
                collector.observe(s);states.append(s);case=s['scenario']
                if last_mask|s['completedMask']!=s['completedMask']:raise AssertionError('Completion mask regressed')
                last_mask=s['completedMask']
                world,actor=scene_pixels(rgb)
                pixels[case]=max(pixels.get(case,0),world);actors[case]=max(actors.get(case,0),actor)
                positions.setdefault(case,set()).add(s['actorX256'])
                if case!=3:ground_coverage(rgb,dict(s,scenario=0))
                if s['completedMask']&(1<<case):images[case]=rgb
            if process.wait()!=0:raise AssertionError('Capture decoder failed')
        finally:
            process.stdout.close()
            if process.poll() is None:process.terminate()
            process.wait();(root/'states.json').write_text(json.dumps(states,indent=2)+'\n')
    if not states:raise AssertionError('No profiling telemetry')
    if smoke:
        if len(states)<3 or pixels.get(0,0)<500:raise AssertionError('Insufficient smoke scene')
        results=[]
    else:
        if last_mask!=(1<<len(NAMES))-1:raise AssertionError('Missing completed profiling cases')
        results=[collector.result(c) for c in range(len(NAMES))]
        for c in range(len(NAMES)):
            if actors.get(c,0)<2 or (c!=3 and pixels.get(c,0)<500):raise AssertionError(f'Missing scene/actor in {NAMES[c]}')
            if max(positions.get(c,{0}))-min(positions.get(c,{0}))<6*256:
                raise AssertionError(f'Actor did not progress in {NAMES[c]}')
    for c,rgb in images.items():
        ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',
                str(root/f'case-{c:02}.png')],root/'encode.log',input=rgb)
    if images:
        # Save from decoded frames; unfinalized AVI indices can make end-seeks return the first frame.
        (root/'tail.png').write_bytes((root/f'case-{max(images):02}.png').read_bytes())
    if not smoke:write_report(results,root)
    return dict(comparisons=comparison_rows(results) if not smoke else [],suite='profiling',smoke=smoke,sampledFrames=len(states),completedCases=results,last=states[-1],
                note='Isolation cases intentionally remove work; phase intervals are wall time, not exclusive CPU or GPU time.')
