using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Pro2.Desktop.ViewModels;
using Pro2.Desktop.Views;
namespace Pro2.Desktop;
public partial class App : Avalonia.Application {
 public override void Initialize() => AvaloniaXamlLoader.Load(this);
 public override void OnFrameworkInitializationCompleted() {
  if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
   var model = new MainViewModel();
   using var iconSource = AssetLoader.Open(new Uri("avares://PRO2ControllerAssistant/Assets/AppIcon-v2.ico"));
   var icon = new WindowIcon(iconSource);
   var window = new MainWindow { DataContext = model, Icon=icon };
   desktop.MainWindow = window;
   desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   void ShowWindow(){window.Show();if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;window.Activate();}
   var open=new NativeMenuItem("显示主窗口");open.Click+=(_,_)=>ShowWindow();
   var stop=new NativeMenuItem("断开全部手柄");stop.Click+=async(_,_)=>{try{await model.Service.StopAsync();}catch{}};
   var exit=new NativeMenuItem("退出 PRO2 手柄助手");exit.Click+=(_,_)=>desktop.TryShutdown();
   tray=new TrayIcon{Icon=icon,ToolTipText="PRO2 手柄助手",IsVisible=true,Menu=new NativeMenu{Items={open,stop,new NativeMenuItemSeparator(),exit}}};
   tray.Clicked+=(_,_)=>ShowWindow();
   TrayIcon.SetIcons(this,new TrayIcons{tray});
   window.Closing+=(_,e)=>{if(!closing){e.Cancel=true;window.Hide();}};
   desktop.ShutdownRequested += async (_,e) => {
    if (closing) return;
    e.Cancel = true;
    closing = true;
    await model.ShutdownAsync();
    tray?.Dispose();tray=null;
    desktop.Shutdown();
   };
  }
  base.OnFrameworkInitializationCompleted();
 }
 bool closing;
 TrayIcon? tray;
}
