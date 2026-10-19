using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace Pro2.Desktop.Controls;
// Same colours and viewport-relative glow locations as the original WPF ambience.
public sealed class NeonAmbience : Control
{
    public NeonAmbience() => IsHitTestVisible = false;
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(new LinearGradientBrush { StartPoint = new RelativePoint(0,0,RelativeUnit.Relative), EndPoint = new RelativePoint(1,1,RelativeUnit.Relative), GradientStops = new GradientStops { new(Color.Parse("#080C17"),0), new(Color.Parse("#101022"),1) } }, null, new Rect(Bounds.Size));
        Glow(context,"#48C7D6",new Rect(-280,-180,620,450));
        Glow(context,"#8B58E8",new Rect(Bounds.Width-420,-180,720,600));
        Glow(context,"#E355AE",new Rect(Bounds.Width-430,Bounds.Height-210,650,380));
        var line = new LinearGradientBrush { StartPoint = new RelativePoint(0,0,RelativeUnit.Relative), EndPoint = new RelativePoint(1,0,RelativeUnit.Relative), Opacity=.65,
            GradientStops = new GradientStops { new(Color.Parse("#65EADD"),0),new(Color.Parse("#68AFFF"),.2),new(Color.Parse("#AC83FF"),.4),new(Color.Parse("#EF79C3"),.6),new(Color.Parse("#F4B780"),.8),new(Color.Parse("#65EADD"),1) } };
        context.DrawRectangle(line,null,new Rect(0,0,Bounds.Width,2));
    }
    private static void Glow(DrawingContext context,string color,Rect rect)
    {
        var c=Color.Parse(color);
        context.DrawEllipse(new RadialGradientBrush { Center = new RelativePoint(.5,.5,RelativeUnit.Relative), GradientOrigin = new RelativePoint(.5,.5,RelativeUnit.Relative), RadiusX=new RelativeScalar(.5,RelativeUnit.Relative), RadiusY=new RelativeScalar(.5,RelativeUnit.Relative), Opacity=.13,
            GradientStops = new GradientStops { new(c,0),new(Color.FromArgb(0,c.R,c.G,c.B),1) } },null,rect);
    }
}
