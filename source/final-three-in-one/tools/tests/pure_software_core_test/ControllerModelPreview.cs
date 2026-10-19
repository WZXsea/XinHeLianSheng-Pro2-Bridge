using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Y700Switch2V60Viiper;

// Offline art review: renders the actual production geometry and materials.
internal static class ControllerModelPreview
{
    internal static void Render(string output)
    {
        _ = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        foreach(bool rear in new[]{false,true})
        {
            var scene=new Ns2ProScene(rear);
            scene.Viewport.Width=1000;scene.Viewport.Height=750;
            var canvas=new Grid {Width=1000,Height=750,Background=new SolidColorBrush(Color.FromRgb(17,24,36))};
            canvas.Children.Add(scene.Viewport);
            void Save(string name)
            {
                canvas.Measure(new Size(1000,750));canvas.Arrange(new Rect(0,0,1000,750));canvas.UpdateLayout();
                var image=new RenderTargetBitmap(1000,750,96,96,PixelFormats.Pbgra32);image.Render(canvas);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);
            }
            Save(rear ? "model-rear" : "model-front");
            scene.Orbit(rear ? -22 : 22,-10);Save(rear ? "model-rear-angle" : "model-front-angle");
            scene.Update(new GamepadState {Lx=3400,Ly=800,Rx=2048,Ry=2048},_=>1);
            Save(rear ? "model-rear-pressed" : "model-front-pressed");
            if(!rear)
            {
                scene.ResetView();scene.Update(GamepadState.Neutral(),_=>0);
                var camera=(System.Windows.Media.Media3D.OrthographicCamera)scene.Viewport.Camera;
                camera.Width=42;camera.Position=new(0,49,280);camera.LookDirection=new(0,-15,-280);
                Save("model-logo-detail");
            }
            Console.WriteLine($"{(rear ? "rear" : "front")}: {scene.TriangleCount} triangles");
        }
    }
}
