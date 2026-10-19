using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Pro2.Desktop.Views;
public partial class EnvironmentInfoWindow : Window
{
    public EnvironmentInfoWindow()=>AvaloniaXamlLoader.Load(this);
    private void Return_Click(object? sender,RoutedEventArgs e)=>Close();
}
