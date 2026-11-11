using Dolphin.Memory;
using Dolphin.Storage;

internal static class Program
{
    static int Main()
    {
        if(!Aram.Initialize(2*1024*1024)||!Storage.Mount(StorageDevice.SdSerialPort2))return 1;
        var scenario=new TextureRuntimeScenario("sd:/world.hcp");
        while(!scenario.Done)scenario.Step();
        scenario.Dispose();
        return 0;
    }
}
