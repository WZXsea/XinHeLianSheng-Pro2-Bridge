using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// A tap on the sole USB reader, never a competing ReadFile or a gameplay frame.
public sealed class Pro2UsbBatteryTap
{
    private readonly TaskCompletionSource<byte[]> sample=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<long> resumed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long before;
    public void Observe(ReadOnlySpan<byte> report,long ticks,long lastCommonTicks)
    {
        if(Pro2UsbBatteryProbe.TryDecode(report,out _))
        {
            if(!sample.Task.IsCompleted) { before=lastCommonTicks;sample.TrySetResult(report.ToArray()); }
        }
        else if(Pro2UsbProtocol.IsInputReport(report) && sample.Task.IsCompleted)
            resumed.TrySetResult(ticks);
    }
    public async ValueTask<int> ReadAsync(Memory<byte> output,CancellationToken token)
    {
        byte[] report=await sample.Task.WaitAsync(token).ConfigureAwait(false);
        report.CopyTo(output);return report.Length;
    }
    public async Task<double> WaitForCommonAsync(CancellationToken token)
    {
        long after=await resumed.Task.WaitAsync(TimeSpan.FromMilliseconds(150),token).ConfigureAwait(false);
        return before>0 && after>before ? (after-before)*1000.0/Stopwatch.Frequency : double.PositiveInfinity;
    }
}
