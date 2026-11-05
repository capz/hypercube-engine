using Hypercube;
internal static class RenderingChecks
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool b,string label){checks++;if(!b)throw new Exception(label);}
        void Reject(Action f){bool rejected=false;try{f();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,"invalid rendering operation rejected");}
        var q=new OpaqueDrawQueue(4);var pose=DrawPose.Identity;pose.X=12;
        Check(q.TryAdd(1,2,0xffffffff,pose),"submit");pose.X=99;
        q.TryAdd(2,3,0xffffffff,pose);q.TryAdd(3,2,0xffffffff,pose);q.TryAdd(4,2,0xff0000ff,pose);
        Check(!q.TryAdd(5,0,0,pose)&&q.Count==4,"overflow does not corrupt queue");Reject(()=>q.At(0));q.Group();
        Check(q.At(0).Mesh==1&&q.At(0).Pose.X==12&&q.At(1).Mesh==3&&q.At(2).Mesh==2&&q.At(3).Mesh==4,"stable material/tint grouping and pose snapshots");
        q.Clear();Reject(()=>q.At(0));Check(q.TryAdd(7,0,0,pose),"reuse after clear");q.Group();Check(q.At(0).Mesh==7,"no stale grouped entries");
        for(int max=3;max<=300;max+=3)for(int current=0;current<=max;current+=3)for(int required=0;required<=max;required++){
            int n=BufferGrowth.Next(current,required,max);Check(n>=current&&n>=required&&n<=max&&n%3==0,"growth bounded/aligned and preserves capacity");
        }
        Reject(()=>BufferGrowth.Next(0,4,3));Reject(()=>BufferGrowth.Next(0,-1,3));Reject(()=>BufferGrowth.Next(1,1,6));
        var f=new CameraFrustum(0,0,0,0,0,-1,0,1,0,90,1,1,100);
        Check(f.Box(-1,-1,-5,1,1,-3)&&!f.Box(-1,-1,3,1,1,5),"camera near/far and facing");
        Check(f.Sphere(5,0,-5,0)&&!f.Sphere(6,0,-5,0)&&f.Sphere(6,0,-5,1),"frustum boundary and sphere radius");
        Check(!f.Box(0,0,-102,1,1,-101)&&f.Box(-2,-2,-101,2,2,-99),"far plane conservative bounds");
        f.Set(100,20,3,101,20,3,0,1,0,60,2,1,50);
        Reject(()=>f.Set(0,0,0,0,0,0,0,1,0,60,1,1,100));
        Check(f.Box(105,19,2,106,21,4)&&!f.Box(90,19,2,91,21,4),"translated rotated configurable camera");
        Reject(()=>new CameraFrustum(0,0,0,0,0,0,0,1,0,60,1,1,100));Reject(()=>f.Sphere(float.NaN,0,0,1));Reject(()=>f.Box(1,0,0,0,1,1));
        // All eight corners inside a view must imply the enclosing AABB survives.
        var random=new Random(71);f.Set(4,7,12,1,2,-5,0,1,0,55,4f/3,.1f,80);
        for(int i=0;i<10000;i++){
            float x=(float)random.NextDouble()*80-40,y=(float)random.NextDouble()*40-20,z=(float)random.NextDouble()*100-70;
            if(f.Sphere(x,y,z,0))Check(f.Box(x-1,y-2,z-3,x+1,y+2,z+3),"visible point inside enclosing AABB retained");
        }
        var r=new ResourceLeases(2,3,16);int slot=r.Register(1,8);var a=r.Acquire(1);var b=r.Acquire(1);r.Pin(a,5);
        r.Release(a);Reject(()=>r.Release(a));Check(r.Slot(b)==slot&&r.ActiveLeases==1,"double release cannot consume another acquisition");
        Check(r.RemoveUnused(100)==-1,"leased resource cannot be evicted");r.Release(b);Check(r.RemoveUnused(4)==-1&&r.RemoveUnused(5)==slot&&r.Bytes==0,"GPU frame fence protects unleased resource");
        r.Register(2,16);var c=r.Acquire(2);Reject(()=>r.Slot(a));Reject(()=>new ResourceLeases(1,1,16).Slot(c));Reject(()=>r.Register(3,1));Reject(()=>r.Register(2,0));
        var d=r.Acquire(2);var e=r.Acquire(2);Reject(()=>r.Acquire(2));r.Release(c);r.Release(d);r.Release(e);Check(r.CanDispose(5),"all acquisitions drain");r.RemoveUnused(5);
        for(int i=0;i<1000;i++){r.Register(i,4);var lease=r.Acquire(i);Reject(()=>r.Release(c));r.Release(lease);Check(r.RemoveUnused(100)==0,"slot and ticket safely reusable");}
        var mask=CloudMask.Texture();int at=0;
        for(int y=0;y<64;y+=4)for(int x=0;x<64;x+=8)for(int dy=0;dy<4;dy++)for(int dx=0;dx<8;dx++){
            byte v=mask[at++];Check(v==CloudMask.Sample(x+dx,y+dy)&&v==CloudMask.Sample(x+dx-64,y+dy+128),"periodic GX packing");Check(Math.Abs(v-CloudMask.Sample(x+dx+1,y+dy))<65,"soft repeat seam");
        }
        Check(CloudMask.Shade(0)==255&&CloudMask.Shade(1)==255&&CloudMask.Shade(.5f)<230&&CloudMask.Ambient(1)<255,"shader bypass endpoints and contrast");
        foreach(double time in new[]{-1e8,-1,-1e-20,0,1,86400,1e8})foreach(float speed in new[]{-.01f,.001f,.002f}){float p=MaterialMotion.Offset(time,speed),n=MaterialMotion.Offset(time+.01,speed),delta=n-p;delta-=(float)Math.Round(delta);Check(p>=0&&p<1&&Math.Abs(delta-speed*.01)<.000001,"long session continuous scrolling");}
        Reject(()=>MaterialMotion.Offset(double.NaN,1));Reject(()=>MaterialMotion.Offset(0,float.PositiveInfinity));
        var counters=new RenderCounters{BatchCalls=3,GeometryRebuilds=2,DrawSubmissions=12,TextureBindings=4};counters.Reset();Check(counters.BatchCalls==0&&counters.GeometryRebuilds==0&&counters.DrawSubmissions==0&&counters.TextureBindings==0,"logical/native counters reset together");
        // Warm-up before measuring steady-state API allocations.
        for(int i=0;i<10;i++){q.Clear();q.TryAdd(0,0,0,pose);q.Group();q.At(0);f.Sphere(0,0,-5,1);MaterialMotion.Offset(i,.1f);}
        r.Register(1001,4);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){q.Clear();q.TryAdd(0,0,0,pose);q.Group();q.At(0);f.Set(0,0,0,0,0,-1,0,1,0,60,1,1,100);f.Box(-1,-1,-5,1,1,-3);MaterialMotion.Offset(i,.1f);var lease=r.Acquire(1001);r.Release(lease);}
        Check(GC.GetAllocatedBytesForCurrentThread()==before,"steady-state core helpers allocate no managed objects");
        Console.WriteLine("Rendering checks passed: "+checks);
    }
}
