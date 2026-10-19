using System.Text.Json;
using System.IO;
using Y700Switch2V60Viiper;

internal static class BleFastReconnectRegressionTests
{
    private const string Address = "02:00:00:00:00:01";
    public static async Task RunAsync(Action<bool,string> check,string output)
    {
        var environment = new BleAdaptiveEnvironmentKey(22621,new string('A',64),"adapter-id","adapter-set","provider","1.0","date","firmware-2.1.9");
        BleAdaptiveMeasurement Sample(double seconds=2,int count=256,double age=4,double p95=15,double gap=30,bool connected=true) =>
            new(new(seconds,count,count,age,connected,true,p95,6),0,gap);
        var profile = new BleReconnectProfile(Address,environment,BleAdaptivePreset.Win11Fixed7p5,Sample(10,1270),DateTimeOffset.UtcNow);
        check(BleFastReconnectPolicy.ProfileMatches(profile,Address.ToLowerInvariant(),environment,DateTimeOffset.UtcNow,out _),"same address/environment accepts a full historical preference");
        check(!BleFastReconnectPolicy.ProfileMatches(profile,"02:00:00:00:00:02",environment,DateTimeOffset.UtcNow,out _),"another controller cannot borrow the fast profile");
        foreach (var changed in new[] { environment with { WindowsBuild=26100 }, environment with { DriverVersion="2.0" },
            environment with { AdapterPnpId="other" }, environment with { BluetoothStackSha256=new string('B',64) },
            environment with { PolicyRevision="other-firmware" }, environment with { AdapterSetFingerprint="two-radios" } })
            check(!BleFastReconnectPolicy.ProfileMatches(profile,Address,changed,DateTimeOffset.UtcNow,out _),"environment change forces full connection qualification");
        check(!BleFastReconnectPolicy.ProfileMatches(profile with { VerifiedAtUtc=DateTimeOffset.UtcNow.AddDays(-8) },Address,environment,DateTimeOffset.UtcNow,out _),"expired profile does not skip full testing");
        check(!BleFastReconnectPolicy.ProfileMatches(profile with { Reference=Sample() },Address,environment,DateTimeOffset.UtcNow,out _),"a short window never replaces full historical evidence in cache");
        var normal = new Backend(Sample()); var result = await BleFastReconnectRunner.TryAsync(profile,normal,CancellationToken.None);
        check(result.Success && result.Live!.Sample.DurationSeconds==2 && normal.Phases.SequenceEqual([BleFastReconnectPolicy.MeasurementPhase]) &&
            normal.Requests.SequenceEqual(new BleAdaptivePreset?[]{BleAdaptivePreset.Win11Fixed7p5}) && normal.Active==BleAdaptivePreset.Win11Fixed7p5,
            "known controller restores exactly one preset and becomes ready after one two-second LIVE window");
        foreach (var bad in new[] { Sample(count:32),Sample(age:100),Sample(seconds:1.999),Sample(seconds:double.NaN),Sample(connected:false),
            Sample(p95:40), Sample(gap:120), Sample() with { Sample=Sample().Sample with { ParsedCount=100 } },
            Sample() with { CompletedAgeSeconds=2 },Sample() with { MaximumGapMs=null },Sample() with { Sample=Sample().Sample with { RequestedSuccessfully=false } } })
        {
            var backend = new Backend(bad); var failed = await BleFastReconnectRunner.TryAsync(profile,backend,CancellationToken.None);
            check(!failed.Success && (backend.Connected ? backend.Requests.Last()==null && backend.Active==null : backend.Requests.Count==1),
                "bad live rate/freshness/parse/gap evidence cannot be replaced by historical 127Hz; restore when connected");
        }
        var rejected = new Backend(Sample()) { Reject=true };
        check(!(await BleFastReconnectRunner.TryAsync(profile,rejected,CancellationToken.None)).Success && rejected.Phases.Count==0 && rejected.Requests.Last()==null,
            "request rejection restores original preference without a fake measurement success");
        var exception = new Backend(Sample()) { ThrowMeasure=true,RejectRestore=true };
        check(!(await BleFastReconnectRunner.TryAsync(profile,exception,CancellationToken.None)).Success && exception.Released,
            "measurement exception and restore rejection release the request before full fallback");
        using (var cancelled = new CancellationTokenSource())
        {
            var backend = new Backend(Sample()) { AfterRequest=()=>cancelled.Cancel() };
            bool threw=false; try { await BleFastReconnectRunner.TryAsync(profile,backend,cancelled.Token); } catch(OperationCanceledException) { threw=true; }
            check(threw && backend.Requests.Last()==null && backend.Active==null,"cancellation after request application restores it and propagates; no full fallback");
        }
        var native = new Backend(Sample());
        check((await BleFastReconnectRunner.TryAsync(profile with { Preset=BleAdaptivePreset.Native },native,CancellationToken.None)).Success && native.Requests.Count==0,
            "known healthy native links need only a live check, with no parameter mutation");
        var known=new HashSet<string>{Address};
        check(BleFastReconnectPolicy.EndScanEarly(Address,known,new HashSet<string>()) &&
            !BleFastReconnectPolicy.EndScanEarly(Address,known,known) && !BleFastReconnectPolicy.EndScanEarly("other",known,null),
            "early scan completion only uses a recognized, preferred and unoccupied address");
        string directory=Path.Combine(output,"fast-reconnect-profiles");
        BleReconnectProfileStore.Save(profile,directory);
        check(BleReconnectProfileStore.Load(Address,environment,out _,directory)==profile,"per-controller profile JSON roundtrip preserves full reference");
        var other=profile with { ControllerAddress="02:00:00:00:00:02" }; BleReconnectProfileStore.Save(other,directory);
        BleReconnectProfileStore.Disable(profile,directory);
        check(BleReconnectProfileStore.Load(Address,environment,out _,directory)==null && BleReconnectProfileStore.Load(other.ControllerAddress,environment,out _,directory)==other,
            "failed fast route is disabled persistently without deleting another controller's preference");
        check(BleReconnectProfileStore.Load("../../outside",environment,out _,directory)==null,"invalid address cannot become a filesystem path");
        await File.WriteAllTextAsync(Path.Combine(directory,"020000000001.json"),"{bad json");
        check(BleReconnectProfileStore.Load(Address,environment,out _,directory)==null,"corrupt profile safely falls back rather than blocking connection");
        var source=new Pro2BleInputSource();
        using var backgroundCancellation=new CancellationTokenSource();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(Pro2BleInputSource).GetField("fastReconnectBackgroundCts",flags)!.SetValue(source,backgroundCancellation);
        typeof(Pro2BleInputSource).GetField("fastReconnectBackgroundTask",flags)!.SetValue(source,Task.Delay(Timeout.Infinite,backgroundCancellation.Token).ContinueWith(_=>{}));
        await source.StopAsync();
        check(backgroundCancellation.IsCancellationRequested && typeof(Pro2BleInputSource).GetField("fastReconnectBackgroundTask",flags)!.GetValue(source)==null,
            "disconnect cancels and joins background observations before reusing the source");
        await source.DisposeAsync();
    }
    private sealed class Backend(BleAdaptiveMeasurement sample) : IBleAdaptiveTrialBackend
    {
        public bool Connected {get;private set;}=true;
        public List<string> Phases {get;}=[];
        public List<BleAdaptivePreset?> Requests {get;}=[];
        public BleAdaptivePreset? Active {get;private set;}
        public bool Reject,ThrowMeasure,RejectRestore,Released;
        public Action? AfterRequest;
        public Task<BleAdaptiveMeasurement> MeasureAsync(string phase,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Phases.Add(phase); if(ThrowMeasure)throw new IOException(); Connected=sample.Sample.Connected;return Task.FromResult(sample); }
        public Task<bool> RequestAsync(BleAdaptivePresetCandidate? candidate,CancellationToken token)
        { token.ThrowIfCancellationRequested();Requests.Add(candidate?.Preset);if(candidate==null){Active=null;return Task.FromResult(!RejectRestore);}Active=candidate.Preset;AfterRequest?.Invoke();token.ThrowIfCancellationRequested();return Task.FromResult(!Reject); }
        public void ReleaseTrialRequest(){Released=true;Active=null;}
        public void Report(string message){}
    }
}
