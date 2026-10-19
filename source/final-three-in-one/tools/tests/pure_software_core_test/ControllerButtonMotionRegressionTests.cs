using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Y700Switch2V60Viiper;

internal static class ControllerButtonMotionRegressionTests
{
    internal static void Run(Action<bool,string> check,string output)
    {
        var scene=new Ns2ProScene(false);
        scene.Viewport.Width=800;scene.Viewport.Height=600;
        var canvas=new Grid {Width=800,Height=600,Background=new SolidColorBrush(Color.FromRgb(17,24,36))};
        canvas.Children.Add(scene.Viewport);
        canvas.Measure(new Size(800,600));canvas.Arrange(new Rect(0,0,800,600));canvas.UpdateLayout();
        RenderTargetBitmap Render()
        {
            var bitmap=new RenderTargetBitmap(800,600,96,96,PixelFormats.Pbgra32);
            bitmap.Render(canvas);return bitmap;
        }
        static int Difference(BitmapSource a,BitmapSource b,Point at,int radius)
        {
            var rect=new Int32Rect((int)at.X-radius,(int)at.Y-radius,radius*2+1,radius*2+1);
            int stride=rect.Width*4;var x=new byte[stride*rect.Height];var y=new byte[x.Length];
            a.CopyPixels(rect,x,stride,0);b.CopyPixels(rect,y,stride,0);
            int changed=0;
            for(int i=0;i<x.Length;i+=4)
                if(Math.Abs(x[i]-y[i])+Math.Abs(x[i+1]-y[i+1])+Math.Abs(x[i+2]-y[i+2])>=24)changed++;
            return changed;
        }
        static void Save(BitmapSource bitmap,string path)
        {
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(path);encoder.Save(stream);
        }
        var neutral=Render();Save(neutral,Path.Combine(output,"face-motion-neutral.png"));
        foreach(var key in new[]{GamepadButtons.East,GamepadButtons.South,GamepadButtons.West,GamepadButtons.North,
            GamepadButtons.Back,GamepadButtons.Start,GamepadButtons.Home,GamepadButtons.Capture,GamepadButtons.Aux})
        {
            bool small=key is GamepadButtons.Back or GamepadButtons.Start or GamepadButtons.Home or GamepadButtons.Capture or GamepadButtons.Aux;
            var at=scene.Project(key);int radius=small ? 13 : 24;
            scene.Update(new GamepadState {Buttons=key},k=>k==key ? 1 : 0);
            var pressed=Render();int changed=Difference(neutral,pressed,at,radius);
            check(changed>radius*radius*.35,"head-on cap depression visibly changes rendered pixels "+key+" pixels="+changed);
            var other=scene.Project(key==GamepadButtons.East ? GamepadButtons.Back : GamepadButtons.East);
            check(Difference(neutral,pressed,other,10)==0,"only the physical pressed key changes "+key);
            Save(pressed,Path.Combine(output,"face-motion-"+key+".png"));
            scene.Update(GamepadState.Neutral(),_=>0);
            check(Difference(neutral,Render(),at,radius)==0,"released cap restores exact neutral appearance "+key);
        }
    }
}
