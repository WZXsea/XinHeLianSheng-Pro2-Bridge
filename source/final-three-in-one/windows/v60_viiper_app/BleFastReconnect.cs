using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// A historical preference, never a substitute for today's live input. Legacy
// environment-only hints are bound to an address only after live confirmation.
public sealed record BleReconnectProfile(string ControllerAddress, BleAdaptiveEnvironmentKey Environment,
    BleAdaptivePreset Preset, BleAdaptiveMeasurement Reference, DateTimeOffset VerifiedAtUtc, bool Enabled = true);

public static class BleFastReconnectPolicy
{
    public const int SampleMilliseconds = 2000;
    public const int NegotiationMilliseconds = 500;
    public const string MeasurementPhase = "FAST_RECONNECT";
    public static string NormalizeAddress(string? address) => V60UserSettings.NormalizeBleAddress(address);
    public static bool ValidAddress(string? value) => BleDiagnosticTargetResolver.TryParsePublicAddress(NormalizeAddress(value), out _);
    public static bool ProfileMatches(BleReconnectProfile? profile, string address, BleAdaptiveEnvironmentKey? environment,
        DateTimeOffset now, out string code)
    {
        code = "reconnect_profile_invalid";
        if (profile == null || !profile.Enabled || !ValidAddress(address) || !ValidAddress(profile.ControllerAddress)) return false;
        if (NormalizeAddress(profile.ControllerAddress) != NormalizeAddress(address)) { code = "reconnect_other_controller"; return false; }
        if (environment == null || profile.Environment != environment) { code = "reconnect_environment_changed"; return false; }
        if (!BleAdaptivePresetPolicy.ValidEnvironment(environment)) return false;
        double ageDays = (now - profile.VerifiedAtUtc).TotalDays;
        if (ageDays < 0 || ageDays > BleAdaptivePresetPolicy.CacheLifetimeDays) { code = "reconnect_profile_expired"; return false; }
        if (!BleAdaptivePresetPolicy.IsPlayable(profile.Reference) ||
            (profile.Preset != BleAdaptivePreset.Native && BleAdaptivePresetPolicy.FindCandidate(profile.Preset) == null)) return false;
        code = "reconnect_exact_controller_environment"; return true;
    }
    public static BleAdaptivePresetDecision ValidateLive(BleReconnectProfile profile, BleAdaptiveMeasurement live)
    {
        var s = live?.Sample;
        if (s == null || !s.Connected || !s.RequestedSuccessfully || !double.IsFinite(s.DurationSeconds) ||
            s.DurationSeconds < 2 || s.DurationSeconds > 30 || s.Fd2Count < 120 || s.ParsedCount < 120 ||
            s.ParsedCount > s.Fd2Count || s.ParsedHz < s.Fd2Hz * .95 || !double.IsFinite(s.LastInputAgeMs) ||
            s.LastInputAgeMs < 0 || s.LastInputAgeMs >= 100 || !double.IsFinite(live!.CompletedAgeSeconds) ||
            live.CompletedAgeSeconds is < 0 or > 1 || !s.P95GapMs.HasValue || !live.MaximumGapMs.HasValue ||
            !double.IsFinite(s.P95GapMs.Value) || !double.IsFinite(live.MaximumGapMs.Value) ||
            s.P95GapMs <= 0 || live.MaximumGapMs < s.P95GapMs)
            return new(false,"reconnect_input_invalid","快速重连输入窗口无效，回退完整测试。");
        double minimum = Math.Max(60, profile.Reference.EffectiveHz * .85);
        if (live.EffectiveHz < minimum)
            return new(false,"reconnect_rate_low","本次输入低于上次正常速率，回退完整测试。");
        double p95Limit = Math.Max(25, (profile.Reference.Sample.P95GapMs ?? 15) * 1.5 + 2);
        double maxLimit = Math.Max(80, (profile.Reference.MaximumGapMs ?? 30) * 2 + 10);
        if (s.P95GapMs > p95Limit || live.MaximumGapMs > maxLimit)
            return new(false,"reconnect_gaps_unstable","快速重连间隙异常，回退完整测试。");
        return new(true,"fast_reconnect_ready","已恢复上次参数，2 秒真实输入检查通过。");
    }
    public static bool EndScanEarly(string address, System.Collections.Generic.IReadOnlySet<string>? preferred,
        System.Collections.Generic.IReadOnlySet<string>? excluded) => preferred?.Contains(address) == true && excluded?.Contains(address) != true;
}

public sealed record BleFastReconnectResult(bool Success, string Code, BleAdaptiveMeasurement? Live);
public static class BleFastReconnectRunner
{
    public static async Task<BleFastReconnectResult> TryAsync(BleReconnectProfile profile,
        IBleAdaptiveTrialBackend backend, CancellationToken token)
    {
        bool changed = false, retained = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!backend.Connected) return new(false,"connection_changed",null);
            backend.Report("恢复此手柄上次成功参数 · 快速检查 2 秒");
            if (profile.Preset != BleAdaptivePreset.Native)
            {
                var candidate = BleAdaptivePresetPolicy.FindCandidate(profile.Preset);
                if (candidate == null) return new(false,"reconnect_preset_invalid",null);
                changed = true; // A cancelled request may already have changed the link.
                if (!await backend.RequestAsync(candidate, token)) return new(false,"reconnect_request_rejected",null);
            }
            var live = await backend.MeasureAsync(BleFastReconnectPolicy.MeasurementPhase, token);
            token.ThrowIfCancellationRequested();
            if (!backend.Connected) return new(false,"connection_changed",live);
            var decision = BleFastReconnectPolicy.ValidateLive(profile,live);
            retained = decision.Retain;
            return new(retained,decision.Code,live);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { backend.Report("快速重连异常：" + ex.GetType().Name + "，回退完整测试。"); return new(false,"reconnect_error",null); }
        finally
        {
            if (changed && !retained && backend.Connected)
            {
                try { if (!await backend.RequestAsync(null,CancellationToken.None)) backend.ReleaseTrialRequest(); }
                catch { backend.ReleaseTrialRequest(); }
            }
        }
    }
}

public static class BleReconnectProfileStore
{
    private static readonly object Gate = new();
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2WirelessReceiverControlBoard","ble-reconnect-profiles");
    private static string PathFor(string address,string? directory) => Path.Combine(directory ?? DefaultDirectory,
        BleFastReconnectPolicy.NormalizeAddress(address).Replace(":", "") + ".json");
    public static BleReconnectProfile? Load(string address, BleAdaptiveEnvironmentKey? environment,out string code,string? directory = null)
    {
        code = "reconnect_profile_absent";
        if (!BleFastReconnectPolicy.ValidAddress(address)) { code = "reconnect_address_invalid"; return null; }
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(PathFor(address,directory));
                if (!file.Exists) return null;
                if (file.Length > 65536) { code = "reconnect_profile_oversized"; return null; }
                var profile = JsonSerializer.Deserialize<BleReconnectProfile>(File.ReadAllText(file.FullName));
                return BleFastReconnectPolicy.ProfileMatches(profile,address,environment,DateTimeOffset.UtcNow,out code) ? profile : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            { code = "reconnect_read_" + ex.GetType().Name; return null; }
        }
    }
    public static void Save(BleReconnectProfile profile, string? directory = null)
    {
        if (!BleFastReconnectPolicy.ProfileMatches(profile,profile.ControllerAddress,profile.Environment,DateTimeOffset.UtcNow,out _))
            throw new ArgumentException("Only exact-identity profiles with full historical evidence can be persisted.");
        lock (Gate) Write(profile,directory);
    }
    // Preserve failed preferences as disabled evidence instead of deleting files.
    public static void Disable(BleReconnectProfile profile,string? directory = null)
    {
        lock (Gate)
        {
            var existing = Load(profile.ControllerAddress,profile.Environment,out _,directory);
            if (existing == profile) Write(profile with { Enabled = false },directory);
        }
    }
    private static void Write(BleReconnectProfile profile,string? directory)
    {
        string path = PathFor(profile.ControllerAddress,directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(profile)); File.Move(path+".tmp",path,true);
    }
}
