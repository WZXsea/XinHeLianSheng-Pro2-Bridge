using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Pro2.Desktop.ViewModels;
namespace Pro2.Desktop.Views;
public partial class RumbleView : UserControl
{
    private bool dragging;
    public RumbleView()
    {
        AvaloniaXamlLoader.Load(this);
        if(this.FindControl<Slider>("RumbleStrengthSlider") is not {} slider)return;
        slider.AddHandler(PointerPressedEvent,(_,_)=>{dragging=true;},RoutingStrategies.Tunnel,true);
        slider.AddHandler(PointerReleasedEvent,(_,_)=>Finish(),RoutingStrategies.Tunnel,true);
        slider.PointerCaptureLost+=(_,_)=>Finish();
        slider.ValueChanged+=(_,_)=>{if(dragging&&DataContext is MainViewModel model)model.RumbleGestureChanged();};
        slider.AddHandler(KeyDownEvent,(_,e)=>{if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End&&DataContext is MainViewModel model)model.RumbleGestureChanged();},RoutingStrategies.Bubble,true);
        slider.AddHandler(KeyUpEvent,(_,_)=>Finish(),RoutingStrategies.Bubble,true);
    }
    private void Finish(){dragging=false;if(DataContext is MainViewModel model)model.FinishRumbleGesture();}
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){Finish();base.OnDetachedFromVisualTree(e);}
}
