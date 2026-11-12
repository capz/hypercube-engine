using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Dolphin.Graphics;

public sealed class StreamingTexture : IDisposable
{
    public static bool Delay;
    private static readonly List<StreamingTexture> pending=new List<StreamingTexture>();
    public static int PendingCount=>pending.Count;
    private Texture2D front,back;
    private NativeBuffer? source;
    private int offset;
    private TaskCompletionSource<int>? completion;
    private Task? upload;
    public Texture2D Texture=>front;
    public bool HasPendingUpload=>upload!=null;
    public StreamingTexture(int width,int height,NativeTextureFormat format,bool linear)
    {front=Texture2D.CreateNative(width,height,format,linear);back=Texture2D.CreateNative(width,height,format,linear);}
    public Task StageUploadAsync(NativeBuffer buffer,int sourceOffset)
    {
        if(upload!=null)throw new Exception("An upload is already pending.");
        buffer.Hold();source=buffer;offset=sourceOffset;
        if(Delay){completion=new TaskCompletionSource<int>();upload=completion.Task;pending.Add(this);}
        else{Complete();upload=Task.CompletedTask;}
        return upload;
    }
    private void Complete()
    {
        Array.Copy(source!.Data,offset,back.Data,0,back.NativeBytes);
        source.Release();source=null;completion?.SetResult(back.NativeBytes);completion=null;
    }
    public static void CompletePending()
    {while(pending.Count!=0){var texture=pending[0];pending.RemoveAt(0);texture.Complete();}}
    public bool TryPublish()
    {
        if(upload==null||!upload.IsCompleted)return false;
        upload.GetAwaiter().GetResult();upload=null;var old=front;front=back;back=old;return true;
    }
    public void Dispose()
    {if(upload!=null)throw new Exception("Disposing an unfinished streaming texture.");front.Dispose();back.Dispose();}
}
