using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace Y700Switch2V60Viiper;

// Static overview of the same authored model used by the configuration centre.
// No report subscription, input queue, animation timer or controller commands.
public sealed class HomeControllerPreview : Grid
{
    public HomeControllerPreview()
    {
        IsHitTestVisible=false;Focusable=false;
        var scene=new Ns2ProScene(false);
        scene.Orbit(-9,-4);
        scene.Zoom(34);
        SizeChanged+=(_,e)=>
        {
            if(e.NewSize.Height>0 && scene.Viewport.Camera is OrthographicCamera camera)
                camera.Width=Math.Max(175,e.NewSize.Width/e.NewSize.Height*120);
        };
        foreach(ModelVisual3D visual in scene.Viewport.Children)
            if(visual.Content?.CanFreeze==true)visual.Content.Freeze();
        Children.Add(scene.Viewport);
    }
}
