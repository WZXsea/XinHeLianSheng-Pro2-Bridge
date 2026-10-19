using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Y700Switch2V60Viiper;

internal static class HomeDashboardRegressionTests
{
    internal static void Run(MainWindow window, MainViewModel vm, Action<bool,string> check,
        Action pump, Action<string> save)
    {
        var tabs=(TabControl)window.FindName("MainTabs"); tabs.SelectedIndex=0; pump();
        var rail=(Grid)window.FindName("HomeModeRail");
        var pill=(Border)window.FindName("ActiveModePill");
        var move=(TranslateTransform)window.FindName("HomeModePillTransform");
        var buttons=Children<Button>(rail).ToArray();
        check(buttons.Length==4 && !rail.IsEnabled==vm.Busy,"mode rail exposes four guarded actions");
        for(int i=0;i<4;i++)
        {
            string key=new[]{"Pro2","Pro1","Xbox","PS5"}[i];
            vm.SelectCompactModeCommand.Execute(key); pump();
            var timer=System.Diagnostics.Stopwatch.StartNew();
            // WPF's animation clock advances on rendering ticks; software 3D
            // rendering may postpone a tick under CI load.
            while(timer.ElapsedMilliseconds<2000)
            {
                pump(); Thread.Sleep(10);
                if(timer.ElapsedMilliseconds>=250 && Math.Abs(move.X-(rail.ActualWidth/4*i+2))<1)break;
            }
            check(vm.HomeModeIndex==i && buttons.Count(b=>b.Tag is true)==1 && buttons[i].Tag is true,
                key+" has exactly one selection");
            check(Math.Abs(move.X-(rail.ActualWidth/4*i+2))<1 && pill.ActualWidth>0,
                key+" sliding selection reaches its target");
            check(((SolidColorBrush)buttons[i].Foreground).Color==Color.FromRgb(178,255,227),
                key+" selected text and icon have bright contrast");
            save("home-mode-"+key+".png");
        }
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm,true); pump();
        vm.SelectCompactModeCommand.Execute("Xbox"); pump();
        check(!rail.IsEnabled && vm.HomeModeIndex==3,"busy operation cannot switch output mode");
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Busy))!.SetValue(vm,false); pump();
        vm.SelectCompactModeCommand.Execute("Pro2"); pump();
        var scroll=(ScrollViewer)window.FindName("HomeScrollViewer");
        check(scroll.ComputedHorizontalScrollBarVisibility!=Visibility.Visible &&
            scroll.ComputedVerticalScrollBarVisibility!=Visibility.Visible,"default home fits without scrolling");
        check(Children<TextBlock>((DependencyObject)tabs.Items[0]).All(t=>
            t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path is not
            ("Status" or "LowRateEvidence" or "LowRateEnvironmentSummary" or "CompactSystemText" or
             "CompactBatteryVisual.Caption" or "CompactFirmware" or "BleStabilitySummary")),
            "home contains no diagnostic fields");
        tabs.SelectedIndex=3; vm.ShowDiagnostics=true; pump();
        check(((FrameworkElement)window.FindName("LowRateDiagnosticPanel")).Parent!=null &&
            ((TextBlock)window.FindName("BleOptimizationSummaryText")).GetBindingExpression(TextBlock.TextProperty)!=null,
            "measured diagnostic evidence remains available in settings");
        vm.ShowDiagnostics=false; tabs.SelectedIndex=0; pump();
        check(window.FindName("HomeDashboard") is Border { Opacity:1 },"tab return never fades the dashboard to white");
        var ambience=(NeonAmbience)window.FindName("WindowAmbience");
        check(ambience.IsHitTestVisible==false,"ambient graphics never intercept controller or UI interaction");
        window.Hide();pump();
        check(!ambience.AnimationRunning,"hidden software stops decorative animation");
        window.Show();pump();
        check(ambience.AnimationRunning==SystemParameters.ClientAreaAnimation,"decorative motion respects Windows reduced motion preference");
        for(int i=0;i<4;i++)
        {
            tabs.SelectedIndex=i;pump();save("neon-page-"+i+".png");
            check(Children<TextBox>(tabs).Where(t=>t.IsVisible).All(t=>
                t.Foreground is SolidColorBrush brush && brush.Color.R>140),
                "visible text fields remain readable on dark page "+i);
        }
        tabs.SelectedIndex=0;pump();
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T match)yield return match;
            foreach(var matchChild in Children<T>(child))yield return matchChild;
        }
    }
}
