using System.Diagnostics;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class UsbPeriodicBatteryRegressionTests
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var state=GamepadState.Neutral();
        check(Pro2UsbBatteryPolicy.IsIdle(state),"neutral controller permits idle detection");
        state.Buttons=GamepadButtons.South;
        check(!Pro2UsbBatteryPolicy.IsIdle(state),"held button postpones battery sampling");
        state.Buttons=0;state.Rx=3000;
        check(!Pro2UsbBatteryPolicy.IsIdle(state),"active stick postpones battery sampling");
        state.Rx=2048;state.GyroValid=true;state.GyroY=500;
        check(!Pro2UsbBatteryPolicy.IsIdle(state),"gyro-only play postpones battery sampling");
        state.GyroY=5;state.Lx=2100;
        check(Pro2UsbBatteryPolicy.IsIdle(state),"small stationary sensor noise does not indefinitely block updates");
        check(Pro2UsbBatteryPolicy.CanRefresh(true,true,false,5,250),"healthy idle USB permits periodic update");
        check(!Pro2UsbBatteryPolicy.CanRefresh(false,true,false,30,250) &&
            !Pro2UsbBatteryPolicy.CanRefresh(true,false,false,30,250) &&
            !Pro2UsbBatteryPolicy.CanRefresh(true,true,true,30,250) &&
            !Pro2UsbBatteryPolicy.CanRefresh(true,true,false,4.9,250) &&
            !Pro2UsbBatteryPolicy.CanRefresh(true,true,false,30,239),"disabled/stale/suspended/active/slow source cannot probe");
        var battery=new Pro2BatteryStatus(0x20);
        check(Pro2UsbBatteryPolicy.CanCommit(true,battery,8),"bounded native sample can commit");
        check(!Pro2UsbBatteryPolicy.CanCommit(false,battery,8) && !Pro2UsbBatteryPolicy.CanCommit(true,null,8) &&
            !Pro2UsbBatteryPolicy.CanCommit(true,battery,25) && !Pro2UsbBatteryPolicy.CanCommit(true,battery,double.NaN),
            "changed session/missing sample/excessive gap never replaces cached level");
        var tap=new Pro2UsbBatteryTap();
        byte[] common=new byte[64];common[0]=5;
        long before=Stopwatch.GetTimestamp();
        tap.Observe(common,before,before-100);
        using(var timeout=new CancellationTokenSource(20))
        {
            bool cancelled=false;
            try { await tap.ReadAsync(new byte[64],timeout.Token); }
            catch(OperationCanceledException) { cancelled=true; }
            check(cancelled,"common gameplay report cannot satisfy native battery tap");
        }
        byte[] native=new byte[64];native[0]=9;native[2]=0x20;
        tap.Observe(native,before+Stopwatch.Frequency/250,before);
        native[2]=0;
        var copy=new byte[64];int count=await tap.ReadAsync(copy,CancellationToken.None);
        check(count==64 && copy[2]==0x20,"single-reader tap owns an immutable primary snapshot");
        tap.Observe(common,before+Stopwatch.Frequency/125,before);
        double gap=await tap.WaitForCommonAsync(CancellationToken.None);
        check(gap is >7.9 and <8.1,"tap measures actual common-to-common input gap across sampling");
        var source=new Pro2BleInputSource();
        try
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            void Set(string name,object value)=>typeof(Pro2BleInputSource).GetField(name,flags)!.SetValue(source,value);
            Set("usbActive",true);Set("transportConnected",true);Set("batteryStatus",battery);
            Set("batteryStatusTicks",Stopwatch.GetTimestamp());Set("batteryLevelSource","usb_runtime");
            typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source,true);
            var snapshot=source.GetBatterySnapshot();
            check(snapshot.RuntimeLevel && snapshot.LevelFresh && snapshot.State.BatteryLevel==8 && snapshot.Transport.StartsWith("usb_periodic"),
                "accepted periodic USB snapshot is exposed as a fresh native level");
            Set("batteryStatusTicks",Stopwatch.GetTimestamp()-Stopwatch.Frequency*500);
            snapshot=source.GetBatterySnapshot();
            check(snapshot.HasConnectionLevel && snapshot.RuntimeLevel && !snapshot.LevelFresh && snapshot.State.BatteryLevel==8,
                "expired USB sample preserves history without claiming freshness");
        }
        finally { await source.DisposeAsync(); }
    }
}
