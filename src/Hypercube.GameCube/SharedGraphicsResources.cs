using Dolphin.Graphics;
namespace Hypercube.GameCube;

// Single render-thread owner. Registered resources must be treated as immutable;
// registration transfers ownership only on success. Every lease acquisition has
// an independent ticket. Pin before submitting and trim only completed GPU work.
public sealed class SharedGraphicsResources
{
    private readonly ResourceLeases leases;private readonly Texture2D?[] textures;private readonly GxEffect?[] effects;
    private bool disposed;
    public int TrackedBytes=>leases.Bytes;
    public SharedGraphicsResources(int capacity,int leaseCapacity,int byteBudget)
    {leases=new ResourceLeases(capacity,leaseCapacity,byteBudget);textures=new Texture2D?[capacity];effects=new GxEffect?[capacity];}
    private void Check(){if(disposed)throw new ObjectDisposedException("SharedGraphicsResources");}
    public void RegisterTexture(int key,Texture2D texture)
    {
        Check();if(texture==null)throw new ArgumentNullException();for(int i=0;i<textures.Length;i++)if(textures[i]==texture)throw new ArgumentException("Resource already owned");
        int slot=leases.Register(key,texture.NativeBytes);textures[slot]=texture;
    }
    // Effect native allocation size is not exposed by the SDK. Its byte charge
    // is explicit; texture charges use NativeBytes. Track actual GPU heap separately.
    public void RegisterEffect(int key,GxEffect effect,int byteCharge)
    {
        Check();if(effect==null)throw new ArgumentNullException();for(int i=0;i<effects.Length;i++)if(effects[i]==effect)throw new ArgumentException("Resource already owned");
        int slot=leases.Register(key,byteCharge);effects[slot]=effect;
    }
    public ResourceLease Acquire(int key){Check();return leases.Acquire(key);}
    public Texture2D Texture(ResourceLease lease){Check();return textures[leases.Slot(lease)]??throw new ArgumentException("Not a texture lease");}
    public GxEffect Effect(ResourceLease lease){Check();return effects[leases.Slot(lease)]??throw new ArgumentException("Not an effect lease");}
    public void Release(ResourceLease lease){Check();leases.Release(lease);}
    public void Pin(ResourceLease lease,int submittedFrame){Check();leases.Pin(lease,submittedFrame);}
    public int TrimUnused(int completedFrame)
    {
        Check();int count=0;
        for(int slot=leases.UnusedCandidate(completedFrame);slot>=0;slot=leases.UnusedCandidate(completedFrame)){
            textures[slot]?.Dispose();effects[slot]?.Dispose();textures[slot]=null;effects[slot]=null;leases.RemoveUnused(completedFrame);count++;
        }return count;
    }
    public void Dispose(int completedFrame)
    {if(disposed)return;if(!leases.CanDispose(completedFrame))throw new InvalidOperationException("Outstanding leases or GPU work");TrimUnused(completedFrame);disposed=true;}
}
