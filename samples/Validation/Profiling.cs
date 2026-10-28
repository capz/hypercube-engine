using System;
using Dolphin.Diagnostics;
using Dolphin.Input;
using Dolphin.Timing;
using Hypercube;

internal sealed partial class ProbeScene
{
#if HYPERCUBE_PROFILING
    private const bool ProfilingSuite=true;
#else
    private const bool ProfilingSuite=false;
#endif
#if HYPERCUBE_FOLLOWUP
    private const bool FollowupSuite=true;
#else
    private const bool FollowupSuite=false;
#endif
#if HYPERCUBE_TEXTURE_FOLLOWUP
    private const bool TextureFollowup=true;
    private readonly string[] textureFollowupNames={"PREVIOUS SDK","SDK 2D","DIRECT16 SDK2D","DIRECT32 SDK2D","DIRECT64 SDK2D","DEADLINE SDK2D","CHECK DIRECT32 SDK2D","ALL32 SDK2D","ALL16 SDK2D","ALL64 SDK2D","ALL32 PREP SDK2D","NO DELAY SDK2D","PREVIOUS REPEAT","ALL32 SDK2D REPEAT","POLL1 SDK2D","APP TIMERS SDK2D"};
    private readonly int[] textureFollowupFlags={1792,1824,10080,18272,34656,5984,20320,24432,16240,40816,24560,24440,1792,24432,23920,1904};
#else
    private const bool TextureFollowup=false;
#endif
#if HYPERCUBE_ONEMS
    private const bool OneMsSuite=true;
    private const int ProfileCases=1;
    private readonly ExecutionMetrics execution=new ExecutionMetrics();
#else
    private const bool OneMsSuite=false;
    private const int ProfileCases=16;
#endif
    private const int ProfilePages=OneMsSuite?5:FollowupSuite?4:3;
    private const int ProfilePageMilliseconds=750;
    private readonly string[] legacyFollowupNames={"PREVIOUS SDK","COMPLETE CHECK","DIRECT 16K","DIRECT 32K","DIRECT 64K","DEADLINE WAIT","CHECK DIRECT32","ALL 32K","ALL 16K","ALL 64K","ALL32 AND PREP","ALL32 NO DELAY","PREVIOUS REPEAT","ALL32 REPEAT","ALL32 POLL1","APP TIMERS ONLY"};
    private readonly int[] legacyFollowupFlags={1792,3840,9984,18176,34560,5888,20224,24336,16144,40720,24560,24344,1792,24336,23824,1808};
    private string[] followupNames=>
#if HYPERCUBE_TEXTURE_FOLLOWUP
        textureFollowupNames;
#else
        legacyFollowupNames;
#endif
    private int[] followupFlags=>
#if HYPERCUBE_TEXTURE_FOLLOWUP
        textureFollowupFlags;
#else
        legacyFollowupFlags;
#endif
    private bool OptimizeIds=>ProfilingSuite&&(FollowupSuite?(OneMsSuite||scenario==10):(ProfileFlags&16)!=0);
    private bool OptimizeTree=>ProfilingSuite&&(FollowupSuite?(OneMsSuite||scenario==10):(ProfileFlags&32)!=0);
    private bool OptimizeReady=>ProfilingSuite&&(FollowupSuite?(OneMsSuite||scenario==10):(ProfileFlags&64)!=0);
    private bool OptimizeDemand=>ProfilingSuite&&(FollowupSuite?(OneMsSuite||scenario==10):(ProfileFlags&128)!=0);
    private readonly int[] demandOrder=new int[24];
    private int visibleMask,detailMask,selectedReadyMask;
    private readonly string[] profileNames={"BASE","COARSE ONLY","TEXTURE ONLY","NO WORLD DRAW","NO DELAY","BASE REPEAT","FAST ID","TREE MASKS","REUSE READY","DEMAND PLAN","PREP COMBINED","SDK WAITS","SDK NATIVE IO","SDK POLL64","SDK COMBINED","SDK AND PREP"};
    private readonly FrameProfile? profile=ProfilingSuite?new FrameProfile():null;
    private readonly int[,]? profileSaved=ProfilingSuite?new int[ProfileCases,ProfilePages*32]:null;
    private readonly bool[]? profileStored=ProfilingSuite?new bool[ProfileCases]:null;
    private readonly long[] profileWaitStats=new long[9],profileReadStats=new long[7];
    private long profileFrameAt;
    private bool profileMeasured,profileFinishPending;
    private int profileDemanded,profileSelected,profileStreamed,profileReportPage;
    internal bool SkipProfileDelay=>ProfilingSuite&&(ProfileFlags&8)!=0;
    internal void BeginProfileFrame()
    {
        if(!ProfilingSuite)return;
        profileFrameAt=Clock.Timestamp;
        for(int i=0;i<8;i++)profile!.Current[i]=0;
        profileMeasured=false;
    }
    internal long BeginProfileYield()=>ProfilingSuite?Clock.Timestamp:0;
    internal void EndProfileFrame(long yieldAt)
    {
        if(!ProfilingSuite)return;
        long end=Clock.Timestamp;
        profile!.Current[FrameProfile.Yield]=(int)(end-yieldAt);
#if HYPERCUBE_ONEMS
        if(profileMeasured)execution.Record((int)(yieldAt-profileFrameAt),GpuProfiler.GpuWaitMicroseconds,GpuProfiler.RetraceWaitMicroseconds);
#endif
        if(profileMeasured)
            profile.Record((int)(end-profileFrameAt),profileDemanded,profileSelected,profileStreamed,visibleCount,
                (ProfileFlags&4)!=0?0:drawCount,(ProfileFlags&4)!=0?0:triangles);
        if(profileFinishPending)
        {
            for(int page=0;page<ProfilePages;page++)
            {
                EncodeProfilePage(page);
                for(int i=0;i<32;i++)profileSaved![scenario,page*32+i]=telemetry.Data[i];
            }
            profileStored![scenario]=true;profileFinishPending=false;
        }
    }
    private void ConfigureProfile()
    {
        GpuProfiler.UseNative2DRectangles=TextureFollowup&&(ProfileFlags&32)!=0;
        GpuProfiler.OverlayGlyphs=OneMsSuite?OverlayGlyphMode.Atlas:TextureFollowup&&(ProfileFlags&32)!=0?OverlayGlyphMode.Rectangles:OverlayGlyphMode.Pixels;
        Dolphin.Diagnostics.GpuProfiler.ReuseFrameClear=TextureFollowup&&(ProfileFlags&64)!=0;
        if(FollowupSuite)
        {
            Dolphin.Storage.AssetStream.UseNativeReads=true;
            Dolphin.Storage.AssetStream.NativeReadQuantum=(ProfileFlags&32768)!=0?65536:(ProfileFlags&16384)!=0?32768:(ProfileFlags&8192)!=0?16384:0;
            Dolphin.Threading.Tasks.Scheduler.ApplicationTimers=(ProfileFlags&16)!=0;
            Dolphin.Threading.Tasks.Scheduler.CompletionAwareWaits=(ProfileFlags&2048)!=0;
            Dolphin.Threading.Tasks.Scheduler.DeadlineWaits=(ProfileFlags&4096)!=0;
            Dolphin.Threading.Tasks.Scheduler.ProductiveWaits=(ProfileFlags&1024)!=0;
            Dolphin.Threading.Tasks.Scheduler.PollStride=(ProfileFlags&512)!=0?64:1;
        }
        else
        {
            // Preserve the original v6 experiments even with the newer SDK defaults.
            Dolphin.Storage.AssetStream.NativeReadQuantum=0;
            Dolphin.Threading.Tasks.Scheduler.ApplicationTimers=false;
            Dolphin.Threading.Tasks.Scheduler.CompletionAwareWaits=false;
            Dolphin.Threading.Tasks.Scheduler.DeadlineWaits=false;
            // TODO(throughput-validation): Retire legacy-read A/B cases with the SDK
            // fallback after repeated Dolphin/hardware results establish native reads
            // as the performant path. Preserve historical results and rebaseline tests.
            Dolphin.Storage.AssetStream.UseNativeReads=scenario==12||scenario==14||scenario==15;
            Dolphin.Threading.Tasks.Scheduler.ProductiveWaits=scenario==11||scenario>=14;
            Dolphin.Threading.Tasks.Scheduler.PollStride=scenario>=13?64:1;
        }
        Dolphin.Threading.Tasks.Scheduler.ResetWaitStats();
        Dolphin.Storage.AssetStream.ResetReadStats();
        profile!.Reset();profileMeasured=profileFinishPending=false;profileStored![scenario]=false;
#if HYPERCUBE_ONEMS
        execution.Reset();
#endif
        streamer.Group=1;streamer.Format=6;streamer.ReadBatchBytes=32768;streamer.MaxBatchRecords=4;
        streamer.UseAram=streamer.InjectFaults=streamer.AdaptiveFallback=streamer.CoarseFirst=false;
        streamer.Policy.ValidateIdsOnly=OptimizeIds;
        streamer.TexturesOnly=(ProfileFlags&2)!=0;streamer.ProfileReads=true;
        telemetry.Compact=true;world.ProfileDecode=true;world.ResetDecodeMetrics();
    }
    private void RestartProfileRun()
    {
        completedMask=0;requested=0;automatic=true;summary=false;profileReportPage=0;
        for(int s=0;s<ProfileCases;s++)
        {
            profileStored![s]=false;
            for(int i=0;i<ProfilePages*32;i++)profileSaved![s,i]=0;
        }
    }
    private void ProfileInput(ControllerState pad)
    {
        // A budget change invalidates the entire comparison, not just one row.
        if(pad.X.WasPressed){budgetIndex=(budgetIndex+1)%3;RestartProfileRun();}
        if(pad.Start.WasPressed)RestartProfileRun();
        if(pad.R.WasPressed){profileReportPage=(profileReportPage+1)%9;summary=true;}
    }
    private void RecordProfilePump(long start,long decodeBefore,long createBefore)
    {
        int elapsed=(int)(Clock.Timestamp-start),decode=(int)(world.DecodeUs-decodeBefore),create=(int)(world.CreateUs-createBefore);
        if(decode+create>elapsed)throw new InvalidOperationException("Mesh profiling exceeds enclosing streaming interval.");
        profile!.Current[FrameProfile.Stream]=elapsed-decode-create;
        profile.Current[FrameProfile.Decode]=decode;profile.Current[FrameProfile.Create]=create;
    }
    private void CountProfileDetail()
    {
        profileSelected=profileStreamed=0;
        for(int c=0;c<24;c++)if(visible[c]&&distances[c]<12)
        {
            if(resolved[c]>=0)profileStreamed++;
            if(OptimizeReady?(selectedReadyMask&(1<<c))!=0:ReadyLevel(c,selectedLod[c],false))profileSelected++;
        }
    }
    private int ProfileFlags=>OneMsSuite?24560:FollowupSuite?followupFlags[scenario]:scenario==1?1:scenario==2?2:scenario==3?4:scenario==4?8:scenario==6?16:scenario==7?32:scenario==8?64:scenario==9?128:scenario==10?240:scenario==12?256:scenario==13?512:scenario==11?1024:scenario==14?1792:scenario==15?2032:0;
    private void FillProfileTelemetry(int elapsed)
    {
        // Hold each page longer than the 500 ms capture interval, including
        // frame timing jitter. All four pages still fit in the 4 s result window.
        int page=elapsed/ProfilePageMilliseconds%ProfilePages;
        if(complete&&profileStored![scenario])
        {for(int i=0;i<32;i++)telemetry.Data[i]=profileSaved![scenario,page*32+i];}
        else EncodeProfilePage(page);
        // Completion is visible only after the final present and yield were measured.
        telemetry.Data[7]=profileStored![scenario]?completedMask:completedMask&~(1<<scenario);
    }
    private void EncodeProfilePage(int page)
    {
        var t=telemetry.Data;var p=profile!;
        for(int i=0;i<32;i++)t[i]=0;
        t[0]=0x4843;t[1]=OneMsSuite?9:TextureFollowup?8:FollowupSuite?7:6;t[2]=frame;t[3]=scenario;t[4]=p.Frames.Samples;
        t[5]=streamer.Policy.BudgetBytes/1024;t[6]=streamer.PhysicalBytes/1024;t[7]=completedMask;t[8]=page;t[9]=ProfileFlags;
        t[10]=(int)(actorX*256);t[11]=(int)(actorZ*256);t[12]=voice.IsPlaying?1:0;t[13]=streamer.Failures;
        t[14]=p.SelectedPermille;t[15]=streamer.Cells.BlockedMoves;
#if HYPERCUBE_ONEMS
        if(page==4){t[16]=execution.Mean;t[17]=execution.P99;t[18]=execution.Maximum;t[19]=execution.Samples;t[20]=execution.GpuMean;t[21]=execution.ViMean;for(int i=0;i<7;i++)t[22+i]=execution.PrepMean(i);return;}
#endif
        if(page==0)
        {
            t[16]=p.Frames.Percentile(50);t[17]=p.Frames.Percentile(95);t[18]=p.Frames.Percentile(99);t[19]=p.Frames.MeanUs/10;
            for(int i=0;i<9;i++)t[20+i]=p.Mean(i)/10;
            t[29]=p.Frames.MaximumUs/1000+1;t[30]=world.MaxDecodeUs/10;t[31]=world.MaxCreateUs/10;
        }
        else if(page==1)
        {
            for(int i=0;i<9;i++)t[16+i]=p.Maximum(i)/10;
            t[25]=streamer.ReadLatencyCalls==0?0:(int)(streamer.ReadLatencyUs/streamer.ReadLatencyCalls/10);
            t[26]=streamer.MaxReadLatencyUs/10;t[27]=streamer.ReadLatencyCalls;t[28]=world.DecodeCalls;
            t[29]=p.Quality100(4);t[30]=p.Quality100(5)/100;t[31]=streamer.DemandPlanBuilds;
        }
        else if(page==2)
        {
            t[16]=streamer.ReadOperations;t[17]=streamer.StorageBytes/1024;t[18]=streamer.Publications;
            t[19]=streamer.Policy.Evicted;t[20]=streamer.Deferred;t[21]=streamer.Policy.Cancelled+streamer.Policy.Stale;
            t[22]=p.DetailFrames;t[23]=streamer.ChecksumUs/10;
            for(int i=0;i<4;i++)t[24+i]=p.Quality100(i);
            t[28]=streamer.MaxPumpUs/10;t[29]=streamer.UploadedBytes/1024;t[30]=(int)(p.TotalUs/1000);t[31]=streamer.DemandPlanHits;
        }
        else
        {
            Dolphin.Threading.Tasks.Scheduler.CopyWaitStats(profileWaitStats);
            Dolphin.Storage.AssetStream.CopyReadStats(profileReadStats);
            long parks=profileWaitStats[1],reads=profileReadStats[4];
            t[16]=(int)profileWaitStats[4];t[17]=(int)parks;
            t[18]=parks==0?0:(int)(profileWaitStats[2]/parks/10);
            t[19]=(int)(profileWaitStats[3]/10);
            t[20]=(int)profileWaitStats[7];t[21]=(int)profileWaitStats[6];
            t[22]=(int)reads;t[23]=(int)profileReadStats[0];
            t[24]=(int)(profileReadStats[1]/1024);
            t[25]=(int)(profileReadStats[2]/1024);t[26]=(int)(profileReadStats[3]/1024);
            t[27]=reads==0?0:(int)(profileReadStats[5]/reads/10);
            t[28]=(int)(profileReadStats[6]/10);
            t[29]=(ProfileFlags&32768)!=0?64:(ProfileFlags&16384)!=0?32:(ProfileFlags&8192)!=0?16:0;
            t[30]=(ProfileFlags>>8)|((ProfileFlags&16)<<4);t[31]=(int)(profileWaitStats[8]/10);
        }
    }
    private void ReportProfile()
    {
        if(!summary)
        {
            var p=profile!;
            DebugOverlay.WriteLine("PROFILE "+(scenario+1)+"/"+ProfileCases+" "+CaseName+" BUDGET "+streamer.Policy.BudgetBytes/1024+" KIB");
            DebugOverlay.WriteLine("F50/95/99 "+p.Frames.Percentile(50)+"/"+p.Frames.Percentile(95)+"/"+p.Frames.Percentile(99)+" SELECTED "+p.SelectedPermille/10+"%");
            DebugOverlay.WriteLine("MEAN US STREAM "+p.Mean(FrameProfile.Stream));
            DebugOverlay.WriteLine("DECODE "+p.Mean(FrameProfile.Decode)+" CREATE "+p.Mean(FrameProfile.Create));
            DebugOverlay.WriteLine("DRAW "+p.Mean(FrameProfile.Draw)+" PRESENT "+p.Mean(FrameProfile.Present));
            DebugOverlay.WriteLine("WAIT "+p.Mean(FrameProfile.Yield)+" HUD "+p.Mean(FrameProfile.Hud)+" PREP "+p.Mean(FrameProfile.Prepare));
            DebugOverlay.WriteLine("READY/DEMAND X100 "+p.Quality100(1)+"/"+p.Quality100(0)+" OTHER US "+p.Mean(FrameProfile.Other));
            DebugOverlay.WriteLine("READ "+streamer.ReadOperations+" STORAGE KIB "+streamer.StorageBytes/1024+" BLOCK "+streamer.Cells.BlockedMoves);
            DebugOverlay.WriteLine("START:RERUN X:BUDGET+RERUN R:RESULT PAGES");
            DebugOverlay.WriteLine("WALL TIME; PRESENT INCLUDES GPU/VSYNC");
            return;
        }
        int view=profileReportPage%3,first=profileReportPage/3*6;
        DebugOverlay.WriteLine(view==0?"PROFILE F50/F95/F99 MS - BUDGET KIB - READY %":
            view==1?"MEAN US: STREAM / DECODE / CREATE / DRAW / PRESENT / WAIT":
            "QUALITY: BUDGET / SELECTED% / STREAMED% / READ / BLOCK");
        for(int s=first;s<first+6;s++)
        {
            if(s>=ProfileCases){DebugOverlay.WriteLine("");continue;}
            if(!profileStored![s]){DebugOverlay.WriteLine((FollowupSuite?followupNames[s]:profileNames[s])+" PENDING");continue;}
            var r=profileSaved!;
            if(view==0)
                DebugOverlay.WriteLine((FollowupSuite?followupNames[s]:profileNames[s])+" "+r[s,16]+"/"+r[s,17]+"/"+r[s,18]+" K"+r[s,5]+" D"+r[s,14]/10+"%");
            else if(view==1)
                DebugOverlay.WriteLine((FollowupSuite?followupNames[s]:profileNames[s])+" "+r[s,20]*10+"/"+r[s,21]*10+"/"+r[s,22]*10+"/"+r[s,23]*10+"/"+r[s,24]*10+"/"+r[s,25]*10);
            else
            {
                int demanded=r[s,64+24],streamed=r[s,64+26];
                DebugOverlay.WriteLine((FollowupSuite?followupNames[s]:profileNames[s])+" K"+r[s,5]+" D"+r[s,14]/10+"% S"+(demanded==0?0:streamed*100/demanded)+"% R"+r[s,64+16]+" B"+r[s,15]);
            }
        }
        DebugOverlay.WriteLine("R:NEXT PAGE START:RERUN X:BUDGET+RERUN");
        DebugOverlay.WriteLine("COARSE/TEXTURE/NO DRAW/NO DELAY ARE ISOLATION TESTS");
        DebugOverlay.WriteLine("PHASES ARE WALL TIME; PRESENT INCLUDES GPU/VSYNC");
    }
}

