using System;

namespace Hypercube;

/// <summary>Bounded storage for V1 streaming batches. Coalescing is a soft target;
/// a larger supported single record still gets enough aligned source storage.</summary>
public static class StreamingBufferPolicy
{
    public const int MaximumRecordBytes=131040;
    public const int DefaultMaximumReadBatchBytes=65536;
    private static void ValidateMaximum(int bytes)
    {if(bytes<32||bytes>MaximumRecordBytes)throw new ArgumentOutOfRangeException("maximumReadBatchBytes");}
    public static int Capacity(AssetCatalog catalog,int maximumReadBatchBytes=DefaultMaximumReadBatchBytes)
    {
        if(catalog==null)throw new ArgumentNullException("catalog");ValidateMaximum(maximumReadBatchBytes);
        int bytes=maximumReadBatchBytes;
        for(int id=1;id<=catalog.Count;id++)
        {
            var record=catalog.Get(id);
            // WorldStreamer requests textures, detailed meshes and collisions.
            // Larger unsupported levels retain the existing demand-time error;
            // an unused fine level must not reject an otherwise usable pack.
            if(record.Kind>=1&&record.Kind<=3&&record.Length<=MaximumRecordBytes&&record.Length>bytes)bytes=record.Length;
        }
        return (bytes+31)&~31;
    }
    public static int CoalescingLimit(int requestedReadBatchBytes,int maximumReadBatchBytes)
    {
        ValidateMaximum(maximumReadBatchBytes);
        if(requestedReadBatchBytes<32)throw new ArgumentOutOfRangeException("requestedReadBatchBytes");
        return Math.Min(requestedReadBatchBytes,maximumReadBatchBytes);
    }
}
