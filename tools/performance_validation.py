"""Protocol-v3 comparison suite. Performance numbers are evidence, not CI thresholds."""
import json
import subprocess

NAMES = ('BASE', 'ON DEMAND', 'SLICED', 'COARSE FIRST', 'COMPACT HUD', 'COMBINED', 'BASE REPEAT', 'OPT FAULTS', 'OPT PRESSURE')
FIELDS = ('magic','version','frame','scenario','caseFrames','budgetKiB','dynamicKiB','nativeKiB',
          'frame50ms','frame95ms','frame99ms','frameMaxMs','frameMean10us','pumpMax10us','pumpMean10us',
          'reads','storageKiB','publications','evictions','deferred','failures','cancelled','draws','triangles',
          'fallbacks','actorX256','actorZ256','blocked','audioActive','recycledUploads','detailFrames','completedMask')


def check_state(s):
    if not 0 <= s['scenario'] < len(NAMES):
        raise AssertionError('Unknown performance scenario')
    if s['dynamicKiB'] > s['budgetKiB']:
        raise AssertionError('Dynamic budget exceeded')
    if s['audioActive'] != 1:
        raise AssertionError('Audio voice stopped')
    if not s['frame50ms'] <= s['frame95ms'] <= s['frame99ms'] <= s['frameMaxMs']:
        raise AssertionError('Inconsistent frame percentiles')
    if s['pumpMean10us'] > s['pumpMax10us']:
        raise AssertionError('Inconsistent pump timings')
    if s['scenario'] < 7 and s['failures']:
        raise AssertionError('Unexpected failure outside injected-fault scenarios')


def comparisons(completed):
    baseline = completed[0]
    result = []
    for case in range(1, 7):
        s = completed[case]
        result.append(dict(name=NAMES[case],
            frame99ChangeMs=s['frame99ms']-baseline['frame99ms'],
            meanChangeUs=(s['frameMean10us']-baseline['frameMean10us'])*10,
            storageChangeKiB=s['storageKiB']-baseline['storageKiB'],
            pumpMaxChangeUs=(s['pumpMax10us']-baseline['pumpMax10us'])*10,
            detailFrameFraction=s['detailFrames']/max(1,s['caseFrames']),
            blockedMoves=s['blocked']))
    return result


def validate(capture, root, smoke):
    # Import at call time to share decoding/projection without a module cycle.
    from dolphin_validation import SIZE, telemetry, scene_pixels, ground_coverage, ffmpeg
    command=['ffmpeg','-threads','1','-v','error','-i',str(capture),'-vf','fps=2,scale=640:480:flags=neighbor',
             '-f','rawvideo','-pix_fmt','rgb24','pipe:1']
    states=[]; completed={}; pixels={}; actors={}; positions={}; last_mask=0
    with (root/'decode.log').open('wb') as err:
        process=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=err)
        try:
            while True:
                rgb=process.stdout.read(SIZE)
                if not rgb: break
                if len(rgb)!=SIZE: raise AssertionError('Truncated RGB frame')
                try: s=telemetry(rgb,fields=FIELDS,version=3)
                except ValueError:
                    if states: raise AssertionError('Guest stopped emitting performance telemetry')
                    continue
                check_state(s)
                if s['completedMask'] | last_mask != s['completedMask']:
                    raise AssertionError('Completion mask regressed')
                last_mask=s['completedMask']; states.append(s); case=s['scenario']
                # All performance cases use the moving-camera projection, including pressure.
                ground_coverage(rgb,dict(s,scenario=0,actorY256=0))
                world, actor=scene_pixels(rgb)
                pixels[case]=max(pixels.get(case,0),world); actors[case]=max(actors.get(case,0),actor)
                positions.setdefault(case,set()).add(s['actorX256'])
                if s['completedMask'] & (1<<case) and case not in completed:
                    completed[case]=s
                    ffmpeg(['-f','rawvideo','-pix_fmt','rgb24','-s','640x480','-i','pipe:0','-frames:v','1','-y',
                            str(root/f'case-{case:02}.png')],root/'encode.log',input=rgb)
            if process.wait()!=0: raise AssertionError('Capture could not be decoded')
        finally:
            process.stdout.close()
            if process.poll() is None: process.terminate()
            process.wait()
            (root/'states.json').write_text(json.dumps(states,indent=2)+'\n')
    if not states: raise AssertionError('No performance telemetry')
    if smoke:
        if len(states)<3 or max(pixels.values(),default=0)<500: raise AssertionError('Insufficient smoke frames')
    else:
        if set(completed)!=set(range(9)) or last_mask!=511: raise AssertionError('Missing completed performance cases')
        for case,s in completed.items():
            if s['caseFrames']<60 or pixels.get(case,0)<500 or actors.get(case,0)<2:
                raise AssertionError(f'Insufficient rendered scene in case {case}')
            if max(positions[case])-min(positions[case])<6*256:
                raise AssertionError(f'Actor made insufficient progress in case {case}')
            if case<7 and (s['detailFrames']<10 or s['publications']==0):
                raise AssertionError(f'Detail streaming did not progress in case {case}')
        if completed[7]['failures']==0 or completed[8]['failures']==0:
            raise AssertionError('Faults were not exercised')
        if completed[7]['recycledUploads']==0:
            raise AssertionError('Cancelled texture staging was not retired and recycled')
    return dict(suite='performance',smoke=smoke,sampledFrames=len(states),
                completedCases=[dict(completed[c],name=NAMES[c]) for c in sorted(completed)],
                comparisons=comparisons(completed) if not smoke else [],last=states[-1])
