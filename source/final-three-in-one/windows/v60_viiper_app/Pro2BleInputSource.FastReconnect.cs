using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

public sealed partial class Pro2BleInputSource
{
    private bool reconnectKnownController;
    private BleReconnectProfile? pendingFastReconnectProfile;
    private CancellationTokenSource? fastReconnectBackgroundCts;
    private Task? fastReconnectBackgroundTask;

    private void RecordFastReconnectEvidence(AdaptiveDiagnosticCapture capture, BleReconnectProfile profile,
        BleAdaptiveMeasurement live)
    {
        lock (gate)
        {
            capture.Snapshot = capture.Snapshot with
            {
                State = "completed", FinalCode = "fast_reconnect_ready", Native = null, Final = live,
                FinalVerified = false, SelectedPreset = BleAdaptivePresetPolicy.FindCandidate(profile.Preset)?.Id ?? "native",
                Stage = "快速重连短窗口通过；10 秒稳定性观察不阻挡进入游戏"
            };
            PublishAdaptiveEvidenceNoLock(capture);
        }
    }

    private void StartFastReconnectBackground(IProgress<string> progress)
    {
        var profile = pendingFastReconnectProfile; pendingFastReconnectProfile = null;
        if (profile == null || device == null || !IsRunning) return;
        var owner = device; long sequence = connectionSequence;
        var cancellation = new CancellationTokenSource();
        fastReconnectBackgroundCts = cancellation;
        fastReconnectBackgroundTask = Task.Run(async () =>
        {
            bool Same() => IsRunning && transportConnected && ReferenceEquals(device,owner) && sequence == connectionSequence;
            try
            {
                if (!Same()) return;
                progress.Report("[BLE_FAST_RECONNECT] background_observation=start blocks_gameplay=false changes_parameters=false");
                // Observe the existing FD2 subscription only. No GATT queries,
                // parameter requests, input-queue reads or virtual writes here.
                var measured = await CaptureParameterTrialAsync("RECONNECT_BACKGROUND",10000,progress,cancellation.Token);
                var live = new BleAdaptiveMeasurement(ToMeasuredTrialSample(measured,Same()),MaximumGapMs: measured.Gaps.MaxMs);
                bool accepted = Same() && BleFastReconnectPolicy.ValidateLive(profile,live).Retain &&
                    BleAdaptivePresetPolicy.IsPlayable(live);
                if (!Same()) return;
                AdoptAdaptiveMeasurement(measured);
                if (accepted) BleReconnectProfileStore.Save(profile with { Reference = live, VerifiedAtUtc = DateTimeOffset.UtcNow });
                else BleReconnectProfileStore.Disable(profile);
                compatibilityOptimizationSummary = accepted
                    ? "快速重连 · 后台观察通过 · " + live.EffectiveHz.ToString("F1") + " Hz"
                    : "后台观察发现输入波动；当前输入继续监测，下次连接回退完整测试";
                progress.Report("[BLE_FAST_RECONNECT] background_verified=" + accepted + " parsed_hz=" + live.EffectiveHz.ToString("F2") +
                    " parameter_changes=0 virtual_reports_fabricated=0");
                lock (gate)
                {
                    adaptiveDiagnosticSnapshot = adaptiveDiagnosticSnapshot with
                    {
                        Final = live, FinalVerified = accepted,
                        FinalCode = accepted ? "fast_reconnect_background_verified" : "fast_reconnect_background_degraded",
                        Stage = compatibilityOptimizationSummary
                    };
                }
                ConnectionStateChanged?.Invoke();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { progress.Report("[BLE_FAST_RECONNECT] background_observation_error=" + ex.GetType().Name + " gameplay_not_restarted=true"); }
        });
    }

    private async Task StopFastReconnectBackgroundAsync()
    {
        var cancellation = fastReconnectBackgroundCts;
        cancellation?.Cancel();
        if (fastReconnectBackgroundTask != null) await fastReconnectBackgroundTask.ConfigureAwait(false);
        cancellation?.Dispose(); fastReconnectBackgroundCts = null; fastReconnectBackgroundTask = null;
        pendingFastReconnectProfile = null;
    }
}
