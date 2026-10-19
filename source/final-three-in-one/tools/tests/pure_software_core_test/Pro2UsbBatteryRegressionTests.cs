using System.IO;
using Y700Switch2V60Viiper;

internal static class Pro2UsbBatteryRegressionTests
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var sample=new byte[64]; sample[0]=9; sample[2]=(6<<2)|3;
        check(Pro2UsbBatteryProbe.TryDecode(sample,out var battery) && battery.Level==6 && battery.Charging && battery.ExternalPower,
            "USB native battery report preserves level and supply bits");
        sample[0]=5;
        check(!Pro2UsbBatteryProbe.TryDecode(sample,out _),"common report counter is never mistaken for a level");
        sample[0]=9; sample[2]=0xfc;
        check(!Pro2UsbBatteryProbe.TryDecode(sample,out _) && !Pro2UsbBatteryProbe.TryDecode(sample.AsSpan(0,12),out _),
            "invalid native level and truncated primary report stay unknown");
        sample[2]=(6<<2)|3;
        var selected=new List<byte>();
        Task Select(byte id,CancellationToken token) { token.ThrowIfCancellationRequested(); selected.Add(id); return Task.CompletedTask; }
        ValueTask<int> Read(Memory<byte> memory,CancellationToken token) { sample.CopyTo(memory); return ValueTask.FromResult(64); }
        var messages=new List<string>();
        var progress=new ImmediateProgress(messages);
        var result=await Pro2UsbBatteryProbe.CaptureAsync(Select,Read,progress,CancellationToken.None);
        check(result?.Level==6 && selected.SequenceEqual(new byte[]{9,5}),"successful snapshot restores common input before gameplay");
        selected.Clear();
        async ValueTask<int> Timeout(Memory<byte> memory,CancellationToken token) { await Task.Delay(TimeoutInfinite,token); return 0; }
        result=await Pro2UsbBatteryProbe.CaptureAsync(Select,Timeout,progress,CancellationToken.None,30);
        check(result==null && selected.SequenceEqual(new byte[]{9,5}),"battery timeout restores report 05 without inventing a level");
        selected.Clear();
        ValueTask<int> Failed(Memory<byte> memory,CancellationToken token)=>throw new IOException("fixture read failure");
        result=await Pro2UsbBatteryProbe.CaptureAsync(Select,Failed,progress,CancellationToken.None);
        check(result==null && selected.SequenceEqual(new byte[]{9,5}),"read failure restores gameplay format");
        selected.Clear(); using var cancelled=new CancellationTokenSource();
        ValueTask<int> Cancel(Memory<byte> memory,CancellationToken token) { cancelled.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(0); }
        bool propagated=false;
        try { await Pro2UsbBatteryProbe.CaptureAsync(Select,Cancel,progress,cancelled.Token); }
        catch(OperationCanceledException) { propagated=true; }
        check(propagated && selected.SequenceEqual(new byte[]{9,5}),"connection cancellation still restores format using a fresh token");
        bool restoreFailed=false;
        Task RestoreFails(byte id,CancellationToken token)=>id==5?throw new IOException("fixture restore failure"):Task.CompletedTask;
        try { await Pro2UsbBatteryProbe.CaptureAsync(RestoreFails,Read,progress,CancellationToken.None); }
        catch(IOException) { restoreFailed=true; }
        check(restoreFailed,"failed format restoration blocks initialization rather than reporting ready");
        var unknown=BatteryVisual.Create(GamepadState.Neutral(),true,false,true);
        check(unknown.Level=="未知" && unknown.Segments.All(s=>!s.Filled && ((System.Windows.Media.SolidColorBrush)s.Fill).Color.R<70),
            "unknown battery is a dim empty placeholder, not a white full battery");
    }
    private const int TimeoutInfinite=-1;
    private sealed class ImmediateProgress(List<string> messages):IProgress<string>
    { public void Report(string value)=>messages.Add(value); }
}
