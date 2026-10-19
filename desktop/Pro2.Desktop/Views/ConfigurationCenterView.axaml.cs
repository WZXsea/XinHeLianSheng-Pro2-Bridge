using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Pro2.Desktop.Views;
public partial class ConfigurationCenterView : UserControl
{
    public ConfigurationCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        if(this.FindControl<ScrollViewer>("CenterScroll") is {} scroll)scroll.SizeChanged+=(_,_)=>UpdateDiagramSize();
        AttachedToVisualTree+=(_,_)=>UpdateDiagramSize();
    }
    private void UpdateDiagramSize()
    {
        if(this.FindControl<ScrollViewer>("CenterScroll") is {} scroll&&this.FindControl<Viewbox>("DiagramViewport") is {} viewport)
            viewport.MaxHeight=System.Math.Max(300,scroll.Bounds.Height-96);
    }
    private void ResetView_Click(object? sender,RoutedEventArgs e)=>this.FindControl<Controls.ControllerDiagram>("TestSurface")?.ResetView();
    private void ChooseTarget_Click(object? sender,RoutedEventArgs e)
    {
        if(sender is Button { Tag:int index })Command("ChooseTargetCommand")?.Execute(new ChooseOutputRequest(index,this.FindControl<Expander>("AdvancedEditor")?.IsExpanded==true));
    }
    private void MappingBackdrop_PointerPressed(object? sender,PointerPressedEventArgs e)
    {
        if(ReferenceEquals(e.Source,sender))Command("CloseKeyEditorCommand")?.Execute(null);
    }
    private ICommand? Command(string name)=>DataContext?.GetType().GetProperty(name)?.GetValue(DataContext) as ICommand;
}
