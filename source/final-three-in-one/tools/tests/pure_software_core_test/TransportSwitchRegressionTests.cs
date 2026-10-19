using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Y700Switch2V60Viiper;

internal static class TransportSwitchRegressionTests
{
    public static void Run(Action<bool,string> check)
    {
        check(Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,false,true,1,1,false),"one active controller and one physical USB device can hand over in auto mode");
        foreach (var preference in new[]{Pro2InputTransport.Bluetooth,Pro2InputTransport.Usb})
            check(!Pro2TransportSwitchPolicy.CanAutoSwitch(preference,false,true,1,1,false),"explicit connection preference is never overridden by hotplug");
        check(!Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,false,true,2,1,false) &&
            !Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,false,true,1,2,false),"ambiguous controller identity never silently changes a player's device");
        check(!Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,false,true,1,1,true) &&
            !Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,true,true,1,1,false) &&
            !Pro2TransportSwitchPolicy.CanAutoSwitch(Pro2InputTransport.Auto,false,false,1,1,false),"busy, already-wired and intentionally stopped sessions cannot start a handover");
        check(Pro2TransportSwitchPolicy.ActiveLabel(false,false,true).Contains("蓝牙无线") &&
            Pro2TransportSwitchPolicy.ActiveLabel(false,true,true).Contains("USB 有线") &&
            Pro2TransportSwitchPolicy.ActiveLabel(false,true,false).Contains("未连接"),"status follows verified physical transport rather than cable/charging or virtual USB identity");
    }
    public static void RunUi(Window window,MainViewModel vm,Action<bool,string> check,Action pump)
    {
        var source=vm.Pro2Slots[0].InputSource;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var connected=typeof(Pro2BleInputSource).GetField("transportConnected",flags)!;
        var usb=typeof(Pro2BleInputSource).GetField("usbActive",flags)!;
        object oldConnected=connected.GetValue(source)!, oldUsb=usb.GetValue(source)!;
        try
        {
            connected.SetValue(source,true);usb.SetValue(source,false);vm.RefreshCompactTelemetry();pump();
            var label=(TextBlock)window.FindName("ActiveInputTransportText");
            check(label.IsVisible && label.Text.Contains("蓝牙无线"),"always-visible badge shows physical Bluetooth even when USB is preferred");
            usb.SetValue(source,true);vm.RefreshCompactTelemetry();pump();
            check(label.Text.Contains("USB 有线"),"badge immediately follows verified USB source after handover");
            connected.SetValue(source,false);vm.RefreshCompactTelemetry();pump();
            check(label.Text.Contains("未连接"),"USB cable/old transport flag alone never claims working wired input");
        }
        finally {connected.SetValue(source,oldConnected);usb.SetValue(source,oldUsb);vm.RefreshCompactTelemetry();pump();}
    }
}
