using Dolphin.Storage;
using Dolphin.Memory;
using System.Threading.Tasks;
using Dolphin.Audio;

internal static class Program
{
    static int Main()
    {
        if(!Aram.Initialize(2*1024*1024))throw new System.Exception("ARAM initialization failed.");
        string path=Storage.MountDisc()?"dvd:/world.hcp":"sd:/hypercube/textures.hcp";
        if(!System.IO.File.Exists(path))
        {
            Storage.Mount(StorageDevice.SdSerialPort2);path="sd:/hypercube/textures.hcp";
        }
        var tone=new byte[8000];for(int i=0;i<tone.Length;i++)tone[i]=(byte)(128+32*System.Math.Sin(i*System.Math.PI*440/8000));
        using var sound=Sound.FromPcm8(tone,16000);sound.Volume=.2f;var voice=sound.Play(true);
        using var scenario=new TextureRuntimeScenario(path,()=>voice.IsPlaying);
        while(true){scenario.Step();Task.Delay(1).GetAwaiter().GetResult();}
    }
}
