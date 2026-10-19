using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;

namespace Y700Switch2V60Viiper;

// Two real, retained-mode 3D scenes, with a 2D annotation layer projected from
// the moving meshes. The gameplay input queue is never touched by this view.
public sealed class ControllerTestSurface : Grid
{
    private readonly Ns2ProScene front = new(false), back = new(true);
    private static readonly Rect FrontBounds = new(205,74,660,510), BackBounds = new(857,242,350,330);
    private readonly DiagramOverlay overlay;
    private ControllerTestSnapshot? snapshot;
    public sealed record MappingLabel(GamepadButtons Key, string Source, string Output, bool Customized);
    private MappingLabel[] mappings = [];
    private static readonly Typeface Font = new("Segoe UI, Microsoft YaHei UI");
    private GamepadButtons selectedKey, hoveredKey;
    public GamepadButtons EditingKey => selectedKey;
    private readonly Dictionary<GamepadButtons,double> travel = new();
    private readonly Dictionary<GamepadButtons,long> pressVisibleUntil = new();
    private readonly DispatcherTimer motionTimer = new(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(16) };
    private long motionAt;
    private bool animating;
    private Ns2ProScene? orbiting;
    private Point dragAt;
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal",typeof(double),typeof(ControllerTestSurface),
        new FrameworkPropertyMetadata(1.0,OnOverlayAnimation));
    private static readonly DependencyProperty HoverProperty = DependencyProperty.Register("Hover",typeof(double),typeof(ControllerTestSurface),
        new FrameworkPropertyMetadata(1.0,OnOverlayAnimation));
    private static void OnOverlayAnimation(DependencyObject obj,DependencyPropertyChangedEventArgs e) => ((ControllerTestSurface)obj).overlay?.InvalidateVisual();
    private static readonly (GamepadButtons Key, Rect Bounds, int Side)[] Labels =
    [
        (GamepadButtons.L2, new(0,110,188,42),0), (GamepadButtons.L1,new(0,174,188,42),0),
        (GamepadButtons.Back,new(0,238,188,42),0), (GamepadButtons.LeftStick,new(0,302,188,42),0),
        (GamepadButtons.DPadUp,new(0,366,188,42),0), (GamepadButtons.DPadLeft,new(0,430,188,42),0),
        (GamepadButtons.DPadDown,new(0,494,188,42),0),
        (GamepadButtons.R2,new(1212,110,188,42),1), (GamepadButtons.R1,new(1212,174,188,42),1),
        (GamepadButtons.North,new(1212,238,188,42),1), (GamepadButtons.East,new(1212,302,188,42),1),
        (GamepadButtons.South,new(1212,366,188,42),1), (GamepadButtons.West,new(1212,430,188,42),1),
        (GamepadButtons.RightStick,new(1212,494,188,42),1),
        (GamepadButtons.Capture,new(300,14,188,42),2), (GamepadButtons.Home,new(600,14,188,42),2),
        (GamepadButtons.Start,new(900,14,188,42),2),
        (GamepadButtons.DPadRight,new(230,610,188,42),3), (GamepadButtons.Aux,new(480,610,188,42),3),
        (GamepadButtons.PaddleRight,new(980,610,188,42),3), (GamepadButtons.PaddleLeft,new(1212,610,188,42),3)
    ];
    public ControllerTestSurface()
    {
        Focusable=true; Background=Brushes.Transparent; ClipToBounds=false;
        var canvas=new Canvas { Width=1400,Height=670,ClipToBounds=false };
        void Scene(Ns2ProScene scene,Rect bounds)
        {
            scene.Viewport.Width=bounds.Width;scene.Viewport.Height=bounds.Height;
            Canvas.SetLeft(scene.Viewport,bounds.Left);Canvas.SetTop(scene.Viewport,bounds.Top);canvas.Children.Add(scene.Viewport);
        }
        Scene(front,FrontBounds);Scene(back,BackBounds);Children.Add(canvas);
        overlay=new DiagramOverlay(this) { IsHitTestVisible=false };Children.Add(overlay);
        motionTimer.Tick+=AdvanceMotion;
        MouseLeftButtonUp+=(_,e)=>
        {
            var key=HitDiagramKey(e.GetPosition(this));
            if(key!=0) { Focus();KeySelected?.Invoke(key);e.Handled=true; }
        };
        MouseRightButtonDown+=(_,e)=>
        {
            dragAt=e.GetPosition(this);orbiting=FrontBounds.Contains(dragAt) ? front : BackBounds.Contains(dragAt) ? back : null;
            if(orbiting!=null) { CaptureMouse();Cursor=Cursors.SizeAll;e.Handled=true; }
        };
        MouseRightButtonUp+=(_,e)=> { if(orbiting!=null) {orbiting=null;ReleaseMouseCapture();Cursor=Cursors.Arrow;e.Handled=true;} };
        LostMouseCapture+=(_,_)=>orbiting=null;
        MouseDown+=(_,e)=> {if(e.ChangedButton==MouseButton.Left && e.ClickCount==2 && HitDiagramKey(e.GetPosition(this))==0)ResetView();};
        MouseMove+=(_,e)=>
        {
            var point=e.GetPosition(this);
            if(orbiting!=null)
            {var delta=point-dragAt;dragAt=point;orbiting.Orbit(delta.X*.3,delta.Y*.23);Redraw();return;}
            var key=HitDiagramKey(point);if(key==hoveredKey)return;
            hoveredKey=key;Cursor=key==0 ? Cursors.Arrow : Cursors.Hand;
            var mapping=mappings.FirstOrDefault(x=>x.Key==key);
            ToolTip=mapping==null ? null : "实体 "+mapping.Source+" → "+mapping.Output;
            Animate(HoverProperty,130);Redraw();
        };
        MouseWheel+=(_,e)=>
        {
            var p=e.GetPosition(this); var scene=FrontBounds.Contains(p) ? front : BackBounds.Contains(p) ? back : null;
            if(scene!=null) {scene.Zoom(e.Delta>0 ? -7 : 7);Redraw();e.Handled=true;}
        };
        MouseLeave+=(_,_)=> {hoveredKey=0;Redraw();};
        KeyDown+=(_,e)=>
        {
            if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int index=Array.FindIndex(Labels,x=>x.Key==hoveredKey);
                hoveredKey=Labels[(index+(e.Key is Key.Left or Key.Up ? -1 : 1)+Labels.Length)%Labels.Length].Key;
                Redraw();e.Handled=true;
            }
            else if(e.Key is Key.Enter or Key.Space) {KeySelected?.Invoke(hoveredKey==0 ? GamepadButtons.East : hoveredKey);e.Handled=true;}
            else if(e.Key==Key.Home) {ResetView();e.Handled=true;}
        };
        Unloaded+=(_,_)=>StopMotion();
        IsVisibleChanged+=(_,_)=> {if(!IsVisible)StopMotion();};
    }
    public event Action<GamepadButtons>? KeySelected;
    public static Rect LabelBounds(GamepadButtons key) => Labels.First(x=>x.Key==key).Bounds;
    public Point ModelAnchor(GamepadButtons key)
    {
        bool rear=key is GamepadButtons.PaddleLeft or GamepadButtons.PaddleRight;
        var p=(rear ? back : front).Project(key);var b=rear ? BackBounds : FrontBounds;
        return new(p.X+b.X,p.Y+b.Y);
    }
    public GamepadButtons HitDiagramKey(Point point)
    {
        foreach(var item in Labels)if(item.Bounds.Contains(point))return item.Key;
        if(FrontBounds.Contains(point))return front.Hit(new(point.X-FrontBounds.X,point.Y-FrontBounds.Y));
        if(BackBounds.Contains(point))return back.Hit(new(point.X-BackBounds.X,point.Y-BackBounds.Y));
        return 0;
    }
    public void ResetView() {front.ResetView();back.ResetView();Redraw();}
    public void SetMappings(MappingLabel[] labels) {mappings=labels;Redraw();}
    public void SelectKey(GamepadButtons key) {if(selectedKey==key)return;selectedKey=key;Animate(RevealProperty,190);Redraw();}
    private void Animate(DependencyProperty property,int ms)
    {
        if(!SystemParameters.ClientAreaAnimation) {BeginAnimation(property,null);SetValue(property,1.0);return;}
        BeginAnimation(property,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(ms)) {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}});
    }
    public double PressTravel(GamepadButtons key)=>travel.GetValueOrDefault(key);
    public void Present(ControllerTestSnapshot value)
    {
        var previous=snapshot?.Fresh==true ? snapshot.State.Buttons : GamepadButtons.None;
        snapshot=value.Fresh ? value : value with {State=GamepadState.Neutral()};
        if(!value.Fresh || !SystemParameters.ClientAreaAnimation || !IsVisible)
        {
            pressVisibleUntil.Clear();
            StopMotion();foreach(var item in ControllerTestLabels.Physical)travel[item.Key]=snapshot.State.IsPressed(item.Key) ? 1 : 0;
        }
        else
        {
            long now=Stopwatch.GetTimestamp();
            foreach(var item in ControllerTestLabels.Physical)
                if(snapshot.State.IsPressed(item.Key) && (previous&item.Key)==0)
                    pressVisibleUntil[item.Key]=now+Stopwatch.Frequency*75/1000;
            if(!animating && ControllerTestLabels.Physical.Any(x=>Math.Abs(PressTravel(x.Key)-PressTarget(x.Key,now))>.001))
            {motionAt=now;animating=true;motionTimer.Start();}
        }
        UpdateMechanics();Redraw();
    }
    private void UpdateMechanics()
    {
        var state=snapshot?.State ?? GamepadState.Neutral();front.Update(state,PressTravel);back.Update(state,PressTravel);
    }
    private void StopMotion() {motionTimer.Stop();animating=false;}
    private double PressTarget(GamepadButtons key,long now) => snapshot?.Fresh==true &&
        (snapshot.State.IsPressed(key) || pressVisibleUntil.GetValueOrDefault(key)>now) ? 1 : 0;
    private void AdvanceMotion(object? sender,EventArgs e)
    {
        long now=Stopwatch.GetTimestamp();double dt=Math.Clamp((now-motionAt)*1000.0/Stopwatch.Frequency,0,50);motionAt=now;
        bool moving=false;
        foreach(var item in ControllerTestLabels.Physical)
        {
            double target=PressTarget(item.Key,now),before=PressTravel(item.Key);
            double next=before+(target-before)*(1-Math.Exp(-dt/(target>before ? 18 : 30)));
            if(Math.Abs(next-target)<.008)next=target;else moving=true;travel[item.Key]=next;
            if(snapshot?.Fresh==true && !snapshot.State.IsPressed(item.Key) && pressVisibleUntil.GetValueOrDefault(item.Key)>now) moving=true;
        }
        UpdateMechanics();Redraw();if(!moving)StopMotion();
    }
    private static Brush Solid(string color)
    {var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();return brush;}
    private void Redraw() {InvalidateVisual();overlay?.InvalidateVisual();}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var shadow=new RadialGradientBrush(Color.FromArgb(75,0,0,0),Color.FromArgb(0,0,0,0));
        dc.DrawEllipse(shadow,null,new Point(525,553),290,29);dc.DrawEllipse(shadow,null,new Point(1034,546),150,17);
    }
    private sealed class DiagramOverlay(ControllerTestSurface owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            owner.DrawMappingLines(dc,false);owner.DrawMappingLines(dc,true);
            void Caption(string text,Point at)
            {
                var label=new FormattedText(text,CultureInfo.CurrentUICulture,System.Windows.FlowDirection.LeftToRight,Font,13,Solid("#7186A2"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label,at);
            }
            Caption("FRONT / 3D",new(470,565));Caption("REAR / GL · GR",new(963,558));
        }
    }
    private void DrawMappingLines(DrawingContext dc, bool foreground)
    {
        foreach (var item in Labels.OrderBy(x => x.Key == selectedKey || x.Key == hoveredKey))
        {
            bool selected = item.Key == selectedKey, hover = item.Key == hoveredKey;
            bool pressed = snapshot?.Fresh == true && snapshot.State.IsPressed(item.Key);
            if (foreground != (selected || hover)) continue;
            var mapping = mappings.FirstOrDefault(x => x.Key == item.Key);
            var anchor = ModelAnchor(item.Key);
            var b = item.Bounds;
            var end = item.Side switch { 0 => new Point(b.Right,b.Y+b.Height/2), 1 => new Point(b.Left,b.Y+b.Height/2),
                2 => new Point(b.X+b.Width/2,b.Bottom), _ => new Point(b.X+b.Width/2,b.Top) };
            var elbow = item.Side is 0 or 1 ? new Point(end.X + (item.Side == 0 ? 17 : -17), anchor.Y) :
                new Point(anchor.X, end.Y + (item.Side == 2 ? 14 : -14));
            var elbow2 = item.Side is 0 or 1 ? new Point(elbow.X, end.Y) : new Point(end.X, elbow.Y);
            var color = selected ? Solid("#A4B5FF") : hover ? Solid("#8DABC7") : Solid("#34465D");
            double reveal = selected ? (double)GetValue(RevealProperty) : 1;
            dc.PushOpacity(selected ? .5 + .5 * reveal : hover ? .75 + .25 * (double)GetValue(HoverProperty) : .65);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) { ctx.BeginFigure(anchor,false,false); ctx.LineTo(elbow,true,false); ctx.LineTo(elbow2,true,false); ctx.LineTo(end,true,false); }
            dc.DrawGeometry(null, new Pen(color, selected ? 2 : 1), geometry);
            dc.DrawEllipse(color,null,anchor,selected || hover ? 3 : 2,selected || hover ? 3 : 2);
            dc.Pop();
            dc.DrawRoundedRectangle(selected ? Solid("#23304B") : pressed ? Solid("#173A3A") : Solid("#152136"),
                new Pen(selected ? Solid("#A4B5FF") : hover ? Solid("#7487AB") : Solid("#2C3B53"),1),b,9,9);
            if (pressed) dc.DrawRoundedRectangle(Solid("#6DE7CC"),null,new Rect(b.Left+1,b.Top+11,3,20),1.5,1.5);
            void LabelText(string text, double x, double width, double size, Brush brush)
            {
                var formatted = new FormattedText(text,CultureInfo.CurrentUICulture,System.Windows.FlowDirection.LeftToRight,
                    Font,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(formatted,new Point(x,b.Top+(b.Height-formatted.Height)/2));
            }
            string sourceLabel = mapping?.Source ?? ControllerTestLabels.Physical.First(x => x.Key == item.Key).Label;
            LabelText(sourceLabel == "Home" ? "⌂" : sourceLabel,b.Left+13,51,22,Solid("#E2EAF6"));
            LabelText("→",b.Left+67,20,15,Solid("#637794"));
            LabelText(mapping?.Output ?? "默认",b.Left+92,b.Width-108,22,pressed ? Solid("#8DF1D7") : Solid("#A9B8CD"));
            if (mapping?.Customized == true) dc.DrawEllipse(Solid("#A4B5FF"),null,new Point(b.Right-7,b.Top+8),2,2);
        }
    }
}
