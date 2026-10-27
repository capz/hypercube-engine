namespace Hypercube;

// Millisecond histogram, with a conservative overflow bound. No per-frame allocation.
public sealed class FrameMetrics
{
    private readonly int[] bins = new int[501];
    private long totalUs;
    public int Samples { get; private set; }
    public int MaximumUs { get; private set; }
    public int MeanUs => Samples == 0 ? 0 : (int)(totalUs / Samples);
    public void Reset()
    {
        for (int i = 0; i < bins.Length; i++) bins[i] = 0;
        Samples = MaximumUs = 0; totalUs = 0;
    }
    public void Record(int microseconds)
    {
        if (microseconds < 0) throw new System.ArgumentOutOfRangeException("microseconds");
        int bin = microseconds / 1000;
        bins[bin > 500 ? 500 : bin]++; Samples++; totalUs += microseconds;
        if (microseconds > MaximumUs) MaximumUs = microseconds;
    }
    public int Percentile(int percent)
    {
        if (percent < 1 || percent > 100) throw new System.ArgumentOutOfRangeException("percent");
        if (Samples == 0) return 0;
        int target = (Samples * percent + 99) / 100, total = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            total += bins[i];
            if (total >= target) return i == 500 ? MaximumUs / 1000 + 1 : i + 1;
        }
        return 0;
    }
}
