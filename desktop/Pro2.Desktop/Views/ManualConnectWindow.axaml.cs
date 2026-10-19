using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace Pro2.Desktop.Views;
public partial class ManualConnectWindow : Window
{
    public ManualConnectWindow()=>AvaloniaXamlLoader.Load(this);
    private void Return_Click(object? sender,RoutedEventArgs e)=>Close();
}
