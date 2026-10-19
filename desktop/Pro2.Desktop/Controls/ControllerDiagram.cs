using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pro2.Desktop.Views;
using Y700Switch2V60Viiper;
namespace Pro2.Desktop.Controls;

internal static class SharedControllerFont
{
    internal static readonly FontFamily Family=new("avares://PRO2ControllerAssistant/Assets/Fonts#Noto Sans SC");
}

public sealed class ControllerPreview : Control
{
    private readonly ProjectedControllerScene controller=new(false);
    public ControllerPreview()=>IsHitTestVisible=false;
    public override void Render(DrawingContext context)
    {
        base.Render(context);controller.Overview(Bounds.Width/Math.Max(1,Bounds.Height));
        controller.Draw(context,new Rect(Bounds.Size),GamepadState.Neutral());
    }
}

public sealed class ControllerDiagram : Control
{
    public static readonly StyledProperty<GamepadState?> StateProperty=AvaloniaProperty.Register<ControllerDiagram,GamepadState?>(nameof(State));
    public static readonly StyledProperty<GamepadButtons> SeenButtonsProperty=AvaloniaProperty.Register<ControllerDiagram,GamepadButtons>(nameof(SeenButtons));
    public static readonly StyledProperty<IReadOnlyList<ConfigurationKeyRow>?> MappingsProperty=AvaloniaProperty.Register<ControllerDiagram,IReadOnlyList<ConfigurationKeyRow>?>(nameof(Mappings));
    public static readonly StyledProperty<int> SelectedControlProperty=AvaloniaProperty.Register<ControllerDiagram,int>(nameof(SelectedControl),-1);
    public static readonly StyledProperty<bool> IsEditingProperty=AvaloniaProperty.Register<ControllerDiagram,bool>(nameof(IsEditing));
    public static readonly StyledProperty<ICommand?> SelectControlCommandProperty=AvaloniaProperty.Register<ControllerDiagram,ICommand?>(nameof(SelectControlCommand));
    public GamepadState? State {get=>GetValue(StateProperty);set=>SetValue(StateProperty,value);}
    public GamepadButtons SeenButtons {get=>GetValue(SeenButtonsProperty);set=>SetValue(SeenButtonsProperty,value);}
    public IReadOnlyList<ConfigurationKeyRow>? Mappings {get=>GetValue(MappingsProperty);set=>SetValue(MappingsProperty,value);}
    public int SelectedControl {get=>GetValue(SelectedControlProperty);set=>SetValue(SelectedControlProperty,value);}
    public bool IsEditing {get=>GetValue(IsEditingProperty);set=>SetValue(IsEditingProperty,value);}
    public ICommand? SelectControlCommand {get=>GetValue(SelectControlCommandProperty);set=>SetValue(SelectControlCommandProperty,value);}
    private readonly ProjectedControllerScene front=new(false),back=new(true);
    private static readonly Rect FrontBounds=new(205,74,660,510),BackBounds=new(857,242,350,330);
    private ProjectedControllerScene? orbiting;
    private Point dragAt;
    private int hovered=-1;
    private static readonly (int Key,Rect Bounds,int Side)[] Labels=[
        (6,new(0,110,188,42),0),(4,new(0,174,188,42),0),(8,new(0,238,188,42),0),(10,new(0,302,188,42),0),(15,new(0,366,188,42),0),(14,new(0,430,188,42),0),(12,new(0,494,188,42),0),
        (7,new(1212,110,188,42),1),(5,new(1212,174,188,42),1),(3,new(1212,238,188,42),1),(1,new(1212,302,188,42),1),(0,new(1212,366,188,42),1),(2,new(1212,430,188,42),1),(11,new(1212,494,188,42),1),
        (17,new(300,14,188,42),2),(16,new(600,14,188,42),2),(9,new(900,14,188,42),2),
        (13,new(230,610,188,42),3),(20,new(480,610,188,42),3),(18,new(980,610,188,42),3),(19,new(1212,610,188,42),3)];
    static ControllerDiagram()=>AffectsRender<ControllerDiagram>(StateProperty,SeenButtonsProperty,MappingsProperty,SelectedControlProperty,IsEditingProperty);
    public ControllerDiagram(){Focusable=true;Width=1400;Height=670;ClipToBounds=false;}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==MappingsProperty)
        {
            if(change.OldValue is IReadOnlyList<ConfigurationKeyRow> old)foreach(var row in old)row.PropertyChanged-=MappingChanged;
            if(change.NewValue is IReadOnlyList<ConfigurationKeyRow> next)foreach(var row in next)row.PropertyChanged+=MappingChanged;
        }
    }
    private void MappingChanged(object? sender,PropertyChangedEventArgs e)=>InvalidateVisual();
    public void ResetView(){front.Reset();back.Reset();InvalidateVisual();}
    public override void Render(DrawingContext context)
    {
        base.Render(context);var state=State??GamepadState.Neutral();
        int selection=IsEditing?SelectedControl:-1;
        front.Draw(context,FrontBounds,state,selection);back.Draw(context,BackBounds,state,selection);
        foreach(var item in Labels.OrderBy(x=>x.Key==selection||x.Key==hovered))
        {
            int key=item.Key;var rect=item.Bounds;bool down=state.IsPressed((GamepadButtons)(1UL<<key)),selected=selection==key,hover=hovered==key;
            var row=Mappings?.FirstOrDefault(x=>x.Index==key);string output=row?.Summary??"默认";
            var tone=Color.Parse(selected?"#A4B5FF":hover?"#8DABC7":"#34465D");
            var projected=key is 18 or 19?back.Project(key,state):front.Project(key,state);
            var origin=key is 18 or 19?BackBounds.Position:FrontBounds.Position;
            var anchor=new Point(projected.X+origin.X,projected.Y+origin.Y);
            Point start=item.Side switch{0=>new(rect.Right,rect.Center.Y),1=>new(rect.Left,rect.Center.Y),2=>new(rect.Center.X,rect.Bottom),_=>new(rect.Center.X,rect.Top)};
            var elbow=item.Side<2?new Point(start.X+(item.Side==0?17:-17),anchor.Y):new Point(anchor.X,start.Y+(item.Side==2?14:-14));
            var elbow2=item.Side<2?new Point(elbow.X,start.Y):new Point(start.X,elbow.Y);
            var pen=new Pen(new SolidColorBrush(tone,selected||hover ? 1 : .65),selected?2:1);
            context.DrawLine(pen,anchor,elbow);context.DrawLine(pen,elbow,elbow2);context.DrawLine(pen,elbow2,start);context.DrawEllipse(new SolidColorBrush(tone),null,anchor,selected||hover?3:2,selected||hover?3:2);
            var border=new Pen(new SolidColorBrush(Color.Parse(selected?"#A4B5FF":hover?"#7487AB":"#2C3B53")),1);
            context.DrawRectangle(new SolidColorBrush(Color.Parse(selected?"#23304B":down?"#173A3A":"#152136")),border,rect,9,9);
            if(down)context.DrawRectangle(new SolidColorBrush(Color.Parse("#6DE7CC")),null,new Rect(rect.Left+1,rect.Top+11,3,20),1.5,1.5);
            var sourceText=Text(key==16?"⌂":ViewChoices.PhysicalNames[key],22,"#E2EAF6");sourceText.MaxTextWidth=51;sourceText.MaxLineCount=1;sourceText.Trimming=TextTrimming.CharacterEllipsis;
            context.DrawText(sourceText,new Point(rect.Left+13,rect.Center.Y-sourceText.Height/2));
            var arrow=Text("→",15,"#637794");context.DrawText(arrow,new Point(rect.Left+67,rect.Center.Y-arrow.Height/2));
            var targetText=Text(output,22,down?"#8DF1D7":"#A9B8CD");targetText.MaxTextWidth=rect.Width-108;targetText.MaxLineCount=1;targetText.Trimming=TextTrimming.CharacterEllipsis;
            context.DrawText(targetText,new Point(rect.Left+92,rect.Center.Y-targetText.Height/2));
            if(row?.Customized==true)context.DrawEllipse(new SolidColorBrush(Color.Parse("#A4B5FF")),null,new Point(rect.Right-7,rect.Top+8),2,2);
        }
        context.DrawText(Text("FRONT / 3D",12,"#607590"),new Point(470,565));context.DrawText(Text("REAR / GL · GR",12,"#607590"),new Point(963,558));
    }
    private static FormattedText Text(string text,double size,string color)=>new(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(SharedControllerFont.Family),size,new SolidColorBrush(Color.Parse(color)));
    private int Hit(Point p)
    {
        foreach(var item in Labels)if(item.Bounds.Contains(p))return item.Key;
        if(FrontBounds.Contains(p))return front.Hit(new Point(p.X-FrontBounds.X,p.Y-FrontBounds.Y));
        if(BackBounds.Contains(p))return back.Hit(new Point(p.X-BackBounds.X,p.Y-BackBounds.Y));
        return -1;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);var p=e.GetPosition(this);var props=e.GetCurrentPoint(this).Properties;
        if(props.IsRightButtonPressed){dragAt=p;orbiting=FrontBounds.Contains(p)?front:BackBounds.Contains(p)?back:null;if(orbiting!=null){e.Pointer.Capture(this);e.Handled=true;}return;}
        if(props.IsLeftButtonPressed){Focus();int key=Hit(p);if(key>=0)Select(key);else if(e.ClickCount==2)ResetView();e.Handled=true;}
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);var p=e.GetPosition(this);
        if(orbiting!=null){var delta=p-dragAt;orbiting.Orbit(delta.X*.3,delta.Y*.23);dragAt=p;InvalidateVisual();return;}
        int key=Hit(p);if(key==hovered)return;hovered=key;Cursor=new Cursor(key>=0?StandardCursorType.Hand:StandardCursorType.Arrow);
        ToolTip.SetTip(this,key<0?null:$"实体 {ViewChoices.PhysicalNames[key]} → {Mappings?.FirstOrDefault(x=>x.Index==key)?.Summary??"默认"}");InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e){base.OnPointerExited(e);hovered=-1;ToolTip.SetTip(this,null);InvalidateVisual();}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);orbiting=null;e.Pointer.Capture(null);}
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){base.OnPointerCaptureLost(e);orbiting=null;}
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);var p=e.GetPosition(this);var target=FrontBounds.Contains(p)?front:BackBounds.Contains(p)?back:null;
        if(target!=null){target.Zoom(e.Delta.Y>0?-7:7);InvalidateVisual();e.Handled=true;}
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(e.Key is Key.Left or Key.Up or Key.Right or Key.Down){int current=Array.FindIndex(Labels,x=>x.Key==hovered);hovered=Labels[(current+(e.Key is Key.Left or Key.Up?-1:1)+Labels.Length)%Labels.Length].Key;InvalidateVisual();e.Handled=true;}
        else if(e.Key is Key.Enter or Key.Space){Select(hovered<0?1:hovered);e.Handled=true;}
        else if(e.Key==Key.Home){ResetView();e.Handled=true;}
    }
    private void Select(int key){SetCurrentValue(SelectedControlProperty,key);SelectControlCommand?.Execute(key);InvalidateVisual();}
}
