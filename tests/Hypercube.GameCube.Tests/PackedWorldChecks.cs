using System;
using System.IO;
using Dolphin.Graphics;
using Dolphin.Storage;
using Hypercube;
using Hypercube.GameCube;

internal static class PackedWorldChecks
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Empty(string phase)
    {Check(AssetStream.Open==0&&Texture2D.Live==0&&Mesh.Live==0,"PackedWorld leaked partial ownership: "+phase);}
    internal static void Run()
    {
        const string fixture="out/assets/world.hcp";
        Empty("before checks");
        using(var world=new PackedWorld(fixture))
        {Check(AssetStream.Open==1&&Texture2D.Live==2&&Mesh.Live==world.ParentMeshCount&&Mesh.Live>1,"Successful world bootstrap did not retain its expected owners.");}
        Empty("successful dispose");
        Texture2D.FailOnUpload=Texture2D.Uploads+2;
        try{new PackedWorld(fixture);throw new Exception("Injected texture upload failure was swallowed.");}
        catch(InvalidOperationException error){Check(error.Message=="Injected texture upload failure.","Cleanup masked the original texture error.");}
        finally{Texture2D.FailOnUpload=-1;}
        Empty("second coarse texture upload failure");
        byte[] original=File.ReadAllBytes(fixture);int records=Binary.I32(original,8);
        string temporary=Path.Combine(Path.GetTempPath(),"hypercube-bootstrap-"+Guid.NewGuid().ToString("N")+".hcp");
        try
        {
            var truncated=new byte[16];File.WriteAllBytes(temporary,truncated);
            try{new PackedWorld(temporary);throw new Exception("Truncated bootstrap accepted.");}catch(EndOfStreamException){}
            Empty("truncated header");
            var badMetadata=(byte[])original.Clone();badMetadata[32]^=1;File.WriteAllBytes(temporary,badMetadata);
            try{new PackedWorld(temporary);throw new Exception("Bad bootstrap metadata accepted.");}catch(IOException){}
            Empty("invalid metadata");
            int seen=0,failedPatch=-1;
            for(int i=0;i<records;i++)if(Binary.I32(original,32+i*64+4)==4&&++seen==2){failedPatch=Binary.I32(original,32+i*64+8);break;}
            Check(failedPatch>=0,"Fixture must include at least two bootstrap patches.");
            var badPayload=(byte[])original.Clone();badPayload[failedPatch]^=128;File.WriteAllBytes(temporary,badPayload);
            int created=Mesh.Creates;
            try{new PackedWorld(temporary);throw new Exception("Corrupt bootstrap payload accepted.");}
            catch(IOException error){Check(error.Message=="Bootstrap asset checksum mismatch.","Cleanup masked the original bootstrap error.");}
            Check(Mesh.Creates==created+1,"Corrupt fixture did not reach a partially created mesh hierarchy.");
            Empty("corrupt second patch");
            Mesh.FailOnCreate=Mesh.Creates+2;
            try{new PackedWorld(fixture);throw new Exception("Injected mesh allocation failure was swallowed.");}catch(OutOfMemoryException){}
            finally{Mesh.FailOnCreate=-1;}
            Empty("allocation failure after first patch");
        }
        finally{File.Delete(temporary);Mesh.FailOnCreate=-1;Texture2D.FailOnUpload=-1;}
        Console.WriteLine("PackedWorld bootstrap: source/texture/mesh ownership reclaimed after truncated header, bad index, failed texture upload, corrupt partial hierarchy and allocation failure.");
    }
}
