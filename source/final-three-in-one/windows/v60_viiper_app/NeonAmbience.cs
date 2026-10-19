using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Color=System.Windows.Media.Color;
using ColorConverter=System.Windows.Media.ColorConverter;
using Point=System.Windows.Point;
using Rectangle=System.Windows.Shapes.Rectangle;
using HorizontalAlignment=System.Windows.HorizontalAlignment;

namespace Y700Switch2V60Viiper;

// A few cached gradients and one narrow animated accent. No input hooks or timers.
public sealed class NeonAmbience : Grid
{
    private readonly Rectangle accent;
    private readonly TranslateTransform flow=new();
    public bool AnimationRunning { get; private set; }
    public NeonAmbience()
    {
        IsHitTestVisible=false; ClipToBounds=true;
        Background=new LinearGradientBrush(Color.FromRgb(8,12,23),Color.FromRgb(16,16,34),45);
        AddGlow("#48C7D6",HorizontalAlignment.Left,VerticalAlignment.Top,620,450,new Thickness(-280,-180,0,0));
        AddGlow("#8B58E8",HorizontalAlignment.Right,VerticalAlignment.Top,720,600,new Thickness(0,-180,-300,0));
        AddGlow("#E355AE",HorizontalAlignment.Right,VerticalAlignment.Bottom,650,380,new Thickness(0,0,-220,-170));
        var spectrum=new LinearGradientBrush { StartPoint=new Point(0,0),EndPoint=new Point(1,0),
            SpreadMethod=GradientSpreadMethod.Repeat,RelativeTransform=flow };
        string[] colors={"#65EADD","#68AFFF","#AC83FF","#EF79C3","#F4B780","#65EADD"};
        for(int i=0;i<colors.Length;i++) spectrum.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]),i/5.0));
        accent=new Rectangle { Height=2,Fill=spectrum,VerticalAlignment=VerticalAlignment.Top,Opacity=.65 };
        Children.Add(accent);
        Loaded+=(_,_)=>UpdateAnimation();
        IsVisibleChanged+=(_,_)=>UpdateAnimation();
        Unloaded+=(_,_)=>StopAnimation();
    }
    private void AddGlow(string color,HorizontalAlignment horizontal,VerticalAlignment vertical,double width,double height,Thickness margin)
    {
        var tone=(Color)ColorConverter.ConvertFromString(color);
        var brush=new RadialGradientBrush(tone,Color.FromArgb(0,tone.R,tone.G,tone.B)); brush.Freeze();
        Children.Add(new Ellipse { Width=width,Height=height,HorizontalAlignment=horizontal,VerticalAlignment=vertical,
            Fill=brush,Opacity=.13,Margin=margin });
    }
    private void UpdateAnimation()
    {
        if(!IsVisible || !IsLoaded || !SystemParameters.ClientAreaAnimation) { StopAnimation(); return; }
        if(AnimationRunning)return;
        AnimationRunning=true;
        accent.BeginAnimation(OpacityProperty,new DoubleAnimation(.38,.92,TimeSpan.FromSeconds(4.5))
            { AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever });
        flow.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,1,TimeSpan.FromSeconds(14))
            { RepeatBehavior=RepeatBehavior.Forever });
    }
    private void StopAnimation()
    {
        accent.BeginAnimation(OpacityProperty,null);
        flow.BeginAnimation(TranslateTransform.XProperty,null);
        AnimationRunning=false;
    }
}
