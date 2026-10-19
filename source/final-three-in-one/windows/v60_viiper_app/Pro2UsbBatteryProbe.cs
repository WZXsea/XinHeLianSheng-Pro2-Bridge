using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// Read the native level only before the gameplay reader starts. Always restore
// report 05, even on timeout/cancellation; no polling or report switching in-game.
public static class Pro2UsbBatteryProbe
{
    public static async Task<Pro2BatteryStatus?> CaptureAsync(
        Func<byte,CancellationToken,Task> selectReport,
        Func<Memory<byte>,CancellationToken,ValueTask<int>> read,
        IProgress<string> progress,CancellationToken token,int timeoutMs=750)
    {
        Pro2BatteryStatus? result=null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            await selectReport(9,timeout.Token).ConfigureAwait(false);
            byte[] report=new byte[64];
            while(!timeout.IsCancellationRequested)
            {
                int count=await read(report,timeout.Token).ConfigureAwait(false);
                if(count==0)throw new IOException("USB battery report ended");
                if(!TryDecode(report.AsSpan(0,count),out var battery))continue;
                result=battery;
                progress.Report("[PRO2_USB_BATTERY] native_snapshot=true "+battery.Telemetry);
                break;
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
        catch(Exception ex) when(ex is OperationCanceledException or IOException or Win32Exception or TimeoutException)
        { progress.Report("[PRO2_USB_BATTERY] unavailable="+ex.GetType().Name+" keeping_unknown=true"); }
        finally
        {
            using var restore=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await selectReport(5,restore.Token).ConfigureAwait(false); }
            catch(Exception ex) { throw new Pro2UsbReportRestoreException(ex); }
            progress.Report("[PRO2_USB_BATTERY] common_report_restored=true");
        }
        return result;
    }
    public static bool TryDecode(ReadOnlySpan<byte> report,out Pro2BatteryStatus battery)
    {
        battery=default;
        return report.Length==64 && report[0]==9 &&
            Pro2BatteryStatus.TryDecodePrimary(report[1..],out battery) && battery.HasLevel;
    }
}

public sealed class Pro2UsbReportRestoreException(Exception inner):IOException("USB common report restoration failed",inner);
