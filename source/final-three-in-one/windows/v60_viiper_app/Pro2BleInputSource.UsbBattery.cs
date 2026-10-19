using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private Task? usbBatteryTask;
    private Pro2UsbBatteryTap? usbBatteryTap;
    private long usbLastCommonTicks,usbLastActivityTicks;
    private bool usbBatterySuspended;

    private async Task UsbBatteryLoopAsync(Pro2UsbDevice opened,IProgress<string> progress,CancellationToken token)
    {
        long due=Stopwatch.GetTimestamp()+Pro2UsbBatteryPolicy.FirstRefreshSeconds*Stopwatch.Frequency;
        try
        {
            while(!token.IsCancellationRequested)
            {
                await Task.Delay(1000,token).ConfigureAwait(false);
                long now=Stopwatch.GetTimestamp();
                if(now<due)continue;
                var rate=GetRateEvidence();
                bool fresh=IsUsb && IsTransportConnected && IsRunning && ReferenceEquals(usbDevice,opened) &&
                    rate.LastInputAgeMs is >=0 and <50;
                double idle=Stopwatch.GetElapsedTime(Interlocked.Read(ref usbLastActivityTicks),now).TotalSeconds;
                if(!Pro2UsbBatteryPolicy.CanRefresh(runtimeBatteryRefreshEnabled,fresh,usbBatterySuspended,idle,rate.ParsedHz))continue;
                var tap=new Pro2UsbBatteryTap();
                Volatile.Write(ref usbBatteryTap,tap);
                try
                {
                    progress.Report("[PRO2_USB_BATTERY_RUNTIME] started=true idle_seconds="+idle.ToString("F1"));
                    bool StillIdle()
                    {
                        lock(gate)
                            return !token.IsCancellationRequested && IsUsb && IsRunning && transportConnected &&
                                ReferenceEquals(usbDevice,opened) && Pro2UsbBatteryPolicy.IsIdle(rawState) &&
                                Stopwatch.GetElapsedTime(Interlocked.Read(ref usbLastActivityTicks)).TotalSeconds>=Pro2UsbBatteryPolicy.IdleSeconds;
                    }
                    var battery=await opened.ReadRuntimeBatteryAsync(tap.ReadAsync,StillIdle,progress,token).ConfigureAwait(false);
                    double gap=battery.HasValue ? await tap.WaitForCommonAsync(token).ConfigureAwait(false) : double.PositiveInfinity;
                    bool sameSession=!token.IsCancellationRequested && IsUsb && IsRunning && IsTransportConnected && ReferenceEquals(usbDevice,opened);
                    bool accepted=Pro2UsbBatteryPolicy.CanCommit(sameSession,battery,gap);
                    lock(gate)
                    {
                        if(accepted && battery is { } value)
                        {
                            batteryStatus=value;batteryStatusTicks=Stopwatch.GetTimestamp();
                            batteryLevelSource="usb_runtime";batteryLevelReadStatus="usb_periodic_native";
                        }
                        else usbBatterySuspended=true;
                    }
                    progress.Report("[PRO2_USB_BATTERY_RUNTIME] accepted="+accepted+" gap_ms="+gap.ToString("F2")+
                        " level="+(battery.HasValue?battery.Value.Level+"/9":"unknown")+" suspended="+usbBatterySuspended);
                    if(usbBatterySuspended)return;
                    due=Stopwatch.GetTimestamp()+Pro2UsbBatteryPolicy.IntervalSeconds*Stopwatch.Frequency;
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
                catch(Pro2UsbBatteryDeferredException)
                { progress.Report("[PRO2_USB_BATTERY_RUNTIME] deferred=true activity_resumed=true"); }
                catch(Pro2UsbReportRestoreException ex)
                {
                    // A format restore failure is a disconnected input, never a
                    // silent switch to primary reports or a self-awaiting Close.
                    usbBatterySuspended=true;
                    lock(gate) { IsRunning=false;transportConnected=false;rateAccepted=false; }
                    usbCts?.Cancel();inputSignal.Notify();SetConnectionPhase(Pro2ConnectionPhase.Disconnected);
                    progress.Report("[PRO2_USB_BATTERY_RUNTIME] restore_failed=true error="+ex.GetType().Name);
                    return;
                }
                catch(Exception ex)
                {
                    usbBatterySuspended=true;
                    progress.Report("[PRO2_USB_BATTERY_RUNTIME] suspended=true error="+ex.GetType().Name);
                    return;
                }
                finally { Volatile.Write(ref usbBatteryTap,null); }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
    }
}
