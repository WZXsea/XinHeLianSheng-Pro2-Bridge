using System.Reflection;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class RumblePreviewUiRegressionTests
{
    internal static void Run(MainWindow window,MainViewModel vm,Action<bool,string> check,Action pump,Action<string> save)
    {
        var tabs=(TabControl)window.FindName("MainTabs");tabs.SelectedIndex=1;pump();
        var slider=(Slider)window.FindName("RumbleStrengthSlider");
        double original=slider.Value;
        check(slider.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path==nameof(MainViewModel.RumblePercent),
            "visible strength slider binds the actual runtime gain");
        slider.SetCurrentValue(Slider.ValueProperty,60.0);slider.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();pump();
        check(vm.RumblePercent==60 && vm.Pro2Slots.All(s=>Math.Abs(s.InputSource.RumbleGain-.6)<.001),
            "changing strength reaches every physical transport");
        var mixer=(RumblePreviewMixer)typeof(Pro2BleInputSource).GetField("rumblePreview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm.Pro2Slots[0].InputSource)!;
        check(!mixer.IsActive,"binding/startup/programmatic changes do not automatically actuate motors");
        check(!vm.CanPreviewRumble && !vm.PreviewRumbleCommand.CanExecute(null),"offline test button cannot report an unconnected motor preview");
        save("rumble-preview-page.png");
        slider.SetCurrentValue(Slider.ValueProperty,original);slider.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
        tabs.SelectedIndex=0;pump();
    }
}
