using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Y700Switch2V60Viiper;


// The historical BLE source owns the shared parser/queue. USB publishes through that
// same path so mapping, motion, rear keys and all virtual modes receive real frames.
public sealed partial class Pro2BleInputSource
{
    private static readonly object UsbClaimGate = new();
    private static readonly HashSet<string> UsbClaims = new(StringComparer.OrdinalIgnoreCase);
    private Pro2UsbDevice? usbDevice;
    private CancellationTokenSource? usbCts;
    private Task? usbReadTask;
    private Task? usbWriteTask;
    private Channel<byte[]>? usbOutput;
    private string? usbClaim;
    private volatile bool usbActive;
    public Pro2InputTransport RequestedTransport { get; set; } = Pro2InputTransport.Bluetooth;
    public bool IsUsb => usbActive;
    public string TransportLabel => IsUsb ? "USB" : "BLE";

    public static bool UsbPathIsClaimed(string path) { lock (UsbClaimGate) return UsbClaims.Contains(path); }

    // The VM pauses connection owners before calling this. The input-source
    // instance stays the same, so the existing virtual device receives a neutral
    // transition and then real USB frames without being removed/recreated.
    public async Task<bool> SwitchToUsbAsync(Pro2UsbCandidate candidate,IProgress<string> progress,CancellationToken token)
    {
        if (RequestedTransport != Pro2InputTransport.Auto || IsUsb) return false;
        if (UsbPathIsClaimed(candidate.Path)) return false;
        // Check accessibility before releasing a working Bluetooth link.
        await using (var probe=Pro2UsbDevice.Open(candidate)) { }
        token.ThrowIfCancellationRequested();
        await CloseCurrentAsync("正在从蓝牙切换到 USB 有线。");
        bool started=await TryStartUsbAsync(progress,new HashSet<string>(),token,candidate.Path);
        if (started) Status="USB 有线已连接；已从蓝牙切换，虚拟手柄保持不变。";
        return started;
    }

    private async Task<bool> TryStartUsbAsync(IProgress<string> progress, IReadOnlySet<string> excluded, CancellationToken token,
        string? exactPath = null)
    {
        Pro2UsbCandidate? candidate = null;
        foreach (var item in await Task.Run(Pro2UsbDevice.Enumerate, token))
        {
            if (exactPath != null && !string.Equals(exactPath,item.Path,StringComparison.OrdinalIgnoreCase)) continue;
            if (excluded.Contains("USB:" + item.ContainerId)) continue;
            lock (UsbClaimGate)
                if (UsbClaims.Add(item.Path)) { candidate = item; usbClaim = item.Path; break; }
        }
        if (candidate == null) return false;
        try
        {
            usbActive = true;
            connectionProgress = progress;
            SetConnectionPhase(Pro2ConnectionPhase.Initializing);
            progress.Report("[PRO2_USB] physical HID selected container=" + candidate.ContainerId + " input_size=" + candidate.InputLength);
            usbDevice = Pro2UsbDevice.Open(candidate);
            await Task.Run(() => usbDevice.InitializeAsync(progress, token), token);
            usbDevice.FlushInput();
            usbCts = new CancellationTokenSource();
            usbBatterySuspended=false;
            usbLastCommonTicks=0;usbLastActivityTicks=Stopwatch.GetTimestamp();
            lock (gate)
            {
                connectedAddress = "USB:" + candidate.ContainerId;
                transportConnected = true;
                IsRunning = true;
                connectionIntervalUnits = 0;
                connectionPreferenceStatus = "usb_no_ble_parameters";
                fd2ReportRateStatus = "usb_report05_selected";
                win11ParameterRouteStatus = "not_applicable_usb";
                linkRateClass = "usb_measuring";
                parser.SetStickCalibration(null); // Do not apply another Bluetooth device's profile.
                if(usbDevice.InitialBatteryStatus is { HasLevel:true } battery)
                {
                    batteryStatus=battery;
                    batteryStatusTicks=Stopwatch.GetTimestamp();
                    batteryLevelAtConnection=battery.Level;
                    batteryLevelSource="usb_connection";
                    batteryLevelReadStatus="usb_native_snapshot";
                }
            }
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = usbDevice;
            usbOutput = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
                { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
            usbWriteTask = Task.Run(() => UsbWriteLoopAsync(opened, usbOutput.Reader, usbCts.Token));
            usbReadTask = Task.Run(() => UsbReadLoopAsync(opened, first, usbCts.Token));
            SetConnectionPhase(Pro2ConnectionPhase.Measuring);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
            await Task.Delay(1000, token);
            var evidence = GetRateEvidence();
            if (!IsRunning || !evidence.GameplayReady) throw new IOException("USB 已打开，但尚未取得连续有效的手柄输入。");
            lock (gate)
            {
                rateAccepted = evidence.ParsedHz >= 120;
                linkRateClass = evidence.ParsedHz.ToString("F0") + "hz";
            }
            SetConnectionPhase(rateAccepted ? Pro2ConnectionPhase.Ready : Pro2ConnectionPhase.RateLimited);
            progress.Report("[PRO2_USB] input_verified=true parsed_hz=" + evidence.ParsedHz.ToString("F2") +
                " age_ms=" + evidence.LastInputAgeMs.ToString("F2") + " transport=usb bluetooth_input=false");
            usbBatteryTask=Task.Run(()=>UsbBatteryLoopAsync(opened,progress,usbCts.Token));
            return true;
        }
        catch
        {
            await CloseCurrentAsync("USB 连接失败，已释放句柄。");
            throw;
        }
    }

    private async Task UsbReadLoopAsync(Pro2UsbDevice opened, TaskCompletionSource first, CancellationToken token)
    {
        byte[] report = new byte[64];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = await opened.ReadAsync(report, token).ConfigureAwait(false);
                if (count == 0) throw new IOException("USB HID 输入已结束。");
                long now=Stopwatch.GetTimestamp();
                Volatile.Read(ref usbBatteryTap)?.Observe(report.AsSpan(0,count),now,Interlocked.Read(ref usbLastCommonTicks));
                if (!Pro2UsbProtocol.IsInputReport(report.AsSpan(0, count))) continue;
                Interlocked.Exchange(ref usbLastCommonTicks,now);
                using var timing = new NotifyHandlerScope(this, Stopwatch.GetTimestamp());
                // HID adds exactly one report-id byte to the existing 63-byte common format.
                ProcessInputPayload(report.AsSpan(1, 63).ToArray(), true, false, NotifyFd2Uuid, 0,
                    Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
                lock(gate)
                    if(!Pro2UsbBatteryPolicy.IsIdle(rawState))Interlocked.Exchange(ref usbLastActivityTicks,now);
                first.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            first.TrySetException(ex);
            connectionProgress?.Report("[PRO2_USB] disconnected: " + ex.Message);
        }
        finally
        {
            lock (gate) { transportConnected = false; IsRunning = false; rateAccepted = false; }
            inputSignal.Notify();
            if (!token.IsCancellationRequested) SetConnectionPhase(Pro2ConnectionPhase.Disconnected);
        }
    }

    private async Task StopUsbAsync()
    {
        if (usbActive)
        {
            lock (gate) { IsRunning = false; transportConnected = false; rateAccepted = false; }
            inputSignal.Notify();
        }
        usbCts?.Cancel();
        if(usbBatteryTask!=null)await usbBatteryTask.ConfigureAwait(false);
        usbBatteryTask=null;usbBatteryTap=null;
        if (usbReadTask != null) await usbReadTask.ConfigureAwait(false);
        usbOutput?.Writer.TryComplete();
        if (usbWriteTask != null) await usbWriteTask.ConfigureAwait(false);
        usbWriteTask = null; usbOutput = null;
        usbReadTask = null;
        if (usbDevice != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(250);
                byte[] stop = new byte[64]; stop[0] = 2; stop[1] = 0x50;
                if (Pro2UsbProtocol.TryEncodeRumble(stop, 0, 0, out var neutral, out _))
                    await usbDevice.WriteAsync(neutral, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { connectionProgress?.Report("[PRO2_USB] stop_output=" + ex.Message); }
            await usbDevice.DisposeAsync().ConfigureAwait(false);
        }
        usbDevice = null;
        usbCts?.Dispose(); usbCts = null;
        if (usbClaim != null) { lock (UsbClaimGate) UsbClaims.Remove(usbClaim); usbClaim = null; }
        usbActive = false;
    }

    private bool TryQueueUsbOutput(ReadOnlySpan<byte> raw02, out string error)
    {
        lock (writeGate)
        {
            if (!Pro2UsbProtocol.TryEncodeRumble(raw02, (byte)(rumblePacketId++ & 15), rumbleGain, out var packet, out error)) return false;
            if (usbOutput?.Writer.TryWrite(packet) == true) return true;
            error = "USB 震动输出未就绪"; return false;
        }
    }
    private async Task UsbWriteLoopAsync(Pro2UsbDevice opened, ChannelReader<byte[]> packets, CancellationToken token)
    {
        long previewWrites=0;
        bool previousPreview=false;
        try
        {
            await foreach (byte[] packet in packets.ReadAllAsync(token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(300);
                bool preview=rumblePreview.IsActive;
                await opened.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
                if(preview)
                {
                    previewWrites++;
                    if(previewWrites==1 || previewWrites%20==0)
                        connectionProgress?.Report("[RUMBLE_PREVIEW_USB] native_write_success=true count="+previewWrites);
                }
                else if(previousPreview)
                    connectionProgress?.Report("[RUMBLE_PREVIEW_USB] preview_ended=true final_packet_written=true");
                previousPreview=preview;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { connectionProgress?.Report("[PRO2_USB_RUMBLE] output_stopped=" + ex.Message); }
        finally { usbOutput?.Writer.TryComplete(); }
    }
}
