using Dolphin.Graphics;
namespace Hypercube.GameCube;

/// <summary>A prepared scene whose drawing can be replayed without updating
/// simulation, animation, input or resource ownership.</summary>
public interface IFrameScene
{
    void SubmitFramePart();
}

/// <summary>Owns one logical frame. Full-resolution GX AA submits the prepared
/// scene twice, resolving overlapping EFB bands into the existing XFB pair.
/// No display-list recording, frame-sized allocation or per-frame delegate.</summary>
public static class FrameRenderer
{
    public static void Draw(GraphicsDevice device,Color32 clear,IFrameScene scene)
    {
        if(device==null)throw new ArgumentNullException(nameof(device));
        if(scene==null)throw new ArgumentNullException(nameof(scene));
        device.BeginFrame(clear);
        do{scene.SubmitFramePart();}while(device.NextFramePart());
        device.EndFrame();
    }
}
