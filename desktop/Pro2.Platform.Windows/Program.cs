using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Y700Switch2V60Viiper;

namespace Pro2.Platform.Windows;

internal static class Program
{
    public static async Task<int> Main()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        await using var host = new NativeHost();
        try { await host.RunAsync(); return 0; }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.ToString()); return 1; }
    }
}
internal sealed class HostFailure(string code,string message) : Exception(message) { public string Code { get; } = code; }
internal sealed class JsonLineWriter : IAsyncDisposable
{
    private readonly record struct Entry(string JSON,bool Raw);
    private readonly Channel<Entry> lines = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=false});
    private readonly Task worker;
    private int dropped,pendingRaw;
    public Action? Failed;
    public JsonLineWriter() { worker=Task.Run(async()=>{try{await WriteLoopAsync();}catch(IOException){Failed?.Invoke();}}); }
    public void Emit(Dictionary<string,object?> message,bool raw=false)
    {
        if(raw && Interlocked.Increment(ref pendingRaw)>1024){Interlocked.Decrement(ref pendingRaw);Interlocked.Increment(ref dropped);return;}
        string json=JsonSerializer.Serialize(message);
        if(!lines.Writer.TryWrite(new(json,raw)) && raw)Interlocked.Decrement(ref pendingRaw);
    }
    private async Task WriteLoopAsync()
    {
        await foreach(var entry in lines.Reader.ReadAllAsync())
        {
            await Console.Out.WriteLineAsync(entry.JSON);
            if(entry.Raw)Interlocked.Decrement(ref pendingRaw);
            int lost=Interlocked.Exchange(ref dropped,0);
            if(lost>0) await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { @event="error",code="stdout_backpressure",message="接收端读取延迟达到原始输入队列容量。",droppedFrames=lost }));
        }
    }
    public async ValueTask DisposeAsync(){lines.Writer.TryComplete();await worker;}
}
internal sealed class NativeSlot(int index) : IAsyncDisposable
{
    public int Index { get; }=index;
    public Pro2BleInputSource Source { get; }=new();
    public WindowsVirtualOutput Output { get; }=new();
    public CancellationTokenSource? ConnectionCancellation,ScanCancellation;
    public Task ConnectionTask=Task.CompletedTask,ScanTask=Task.CompletedTask,TransportSwitchTask=Task.CompletedTask;
    public readonly SemaphoreSlim PhysicalOperations=new(1,1);
    public int SwitchingTransport;
    public string DeviceID="",Transport="auto";
    public bool LastConnected;
    public string LastBatterySignature="";
    public async Task DisconnectAsync()
    {
        ScanCancellation?.Cancel();ConnectionCancellation?.Cancel();
        try { await ScanTask; } catch(OperationCanceledException){}
        try { await TransportSwitchTask; } catch(OperationCanceledException){}
        try { await ConnectionTask; } catch(OperationCanceledException){}
        await PhysicalOperations.WaitAsync();
        try{await Source.StopAsync();}finally{PhysicalOperations.Release();}
        LastConnected=false;LastBatterySignature="";
        ConnectionCancellation?.Dispose();ConnectionCancellation=null;ScanCancellation?.Dispose();ScanCancellation=null;
    }
    public async ValueTask DisposeAsync(){await DisconnectAsync();await Output.DisposeAsync();await Source.DisposeAsync();PhysicalOperations.Dispose();}
}
internal sealed class NativeHost : IAsyncDisposable
{
    private readonly JsonLineWriter writer=new();
    private readonly ConcurrentDictionary<int,NativeSlot> slots=new();
    private readonly ConcurrentDictionary<string,Pro2ManualCandidate> bleCandidates=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,Pro2UsbCandidate> usbCandidates=new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime=new();
    private bool runtimeBattery=true,autoOptimize=true,autoReconnect=true,skipInitialization,shuttingDown;
    private Pro2InputTransport inputTransport=Pro2InputTransport.Auto;
    private readonly WindowsRuntime runtime=new();
    private int maintenanceRunning;
    private volatile bool globalMaintenance;
    private Task maintenanceTask=Task.CompletedTask;
    public NativeHost(){writer.Failed=()=>lifetime.Cancel();}
    public async Task RunAsync()
    {
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;lifetime.Cancel();};
        Emit(new(){["event"]="ready",["protocolVersion"]=1,["platform"]="Windows",["slots"]=4,["inputFormats"]=new[]{"usb","fd2","primary"},["stateContract"]="GamepadState-v1"});
        while(!lifetime.IsCancellationRequested)
        {
            string? line;
            try { line=await Console.In.ReadLineAsync(lifetime.Token); } catch(OperationCanceledException){break;}
            if(line==null)break;
            if(string.IsNullOrWhiteSpace(line))continue;
            if(line.Length>1_048_576){Error(default,"command_too_large","单条 JSON 命令容量上限为 1 MiB。");continue;}
            try { using var document=JsonDocument.Parse(line);await CommandAsync(document.RootElement); }
            catch(HostFailure e){Error(default,e.Code,e.Message);}
            catch(JsonException){Error(default,"invalid_json","每行需要一个有效 JSON 对象。");}
        }
    }
    private async Task CommandAsync(JsonElement request)
    {
        try
        {
            string command=Text(request,"command",Text(request,"cmd",Text(request,"type","")));
            switch(command)
            {
                case "ping":Result(request,command,new(){["protocolVersion"]=1});break;
                case "readActualOutput":
                    var actual=WindowsMaintenance.ReadActualOutput(Number(request,"index",0));actual["slot"]=Index(request);Correlate(request,actual);Emit(actual);break;
                case "optimizeBluetooth":
                    var optimize=Existing(request);
                    if(optimize.Source.IsUsb)throw new HostFailure("bluetooth_required","当前通道使用 USB；蓝牙优化适用于无线连接。");
                    StartMaintenance(request,command,false,async()=>{
                        var outcome=await OptimizeAsync(optimize,optimize.ConnectionCancellation?.Token??lifetime.Token);
                        return new(){["success"]=true,["retained"]=outcome.Retained,["code"]=outcome.Code,["message"]=outcome.Summary};
                    });break;
                case "configureBluetooth":case "restoreBluetooth":
                    var plan=WindowsMaintenance.PrepareConfiguration(request,command=="restoreBluetooth");
                    StartMaintenance(request,command,true,async()=>{await ReleaseSlotsAsync();return await WindowsMaintenance.ConfigureAsync(plan,lifetime.Token);});break;
                case "refreshSteamCache":StartMaintenance(request,command,false,()=>WindowsMaintenance.RefreshSteamCacheAsync(lifetime.Token));break;
                case "installDriver":StartMaintenance(request,command,true,async()=>{await ReleaseSlotsAsync();return await WindowsMaintenance.InstallDriverAsync(lifetime.Token);});break;
                case "enumerate":Enumerate(request);break;
                case "connect":await ConnectAsync(request);break;
                case "switchToUsb":
                    try{SwitchToUsb(request);}catch(Exception error){Error(request,"usb_switch_failed",error.Message);}break;
                case "disconnect":case "stop":
                    if(request.TryGetProperty("slot",out _) || command=="disconnect") {int index=Index(request);if(slots.TryRemove(index,out var slot))await slot.DisposeAsync();Result(request,command,new(){["slot"]=index});}
                    else {foreach(var slot in slots.Values)await slot.DisposeAsync();slots.Clear();Result(request,command);}
                    break;
                case "shutdown":Result(request,command);lifetime.Cancel();break;
                case "settings":ApplySettings(request);Result(request,command,new(){["runtimebattery"]=runtimeBattery,["autooptimize"]=autoOptimize,["autoReconnect"]=autoReconnect,["skip"]=skipInitialization,["inputTransport"]=WindowsTransportHandover.PreferenceKey(inputTransport),["initializationAppliesTo"]="nextConnection"});break;
                case "backendStatus":
                    var status=await runtime.StatusAsync(lifetime.Token);status["event"]="backendStatus";Correlate(request,status);Emit(status);break;
                case "createOutput":
                    RequireNoGlobalMaintenance();var outputSlot=GetSlot(Index(request));string backend=Text(request,"backend","viiper");
                    if(backend is not("viiper" or "windows"))throw new HostFailure("invalid_backend","Windows 输出使用 viiper 后端。");
                    var client=await runtime.EnsureAsync(lifetime.Token);
                    await outputSlot.Output.CreateAsync(client,Text(request,"mode","pro2"),outputSlot.Index,Progress(outputSlot),lifetime.Token);
                    Result(request,command,new(){["slot"]=outputSlot.Index,["backend"]="viiper",["mode"]=Text(request,"mode","pro2")});break;
                case "submitOutput":
                    var submit=Existing(request);if(!request.TryGetProperty("state",out var state))throw new HostFailure("invalid_state","submitOutput 需要 state 对象。");
                    if(Volatile.Read(ref submit.SwitchingTransport)==0)await submit.Output.SubmitAsync(state,lifetime.Token);
                    if(Correlated(request))Result(request,command,new(){["slot"]=submit.Index});break;
                case "readFeedback":
                    var feedback=Existing(request);var evt=new Dictionary<string,object?>{["event"]="feedback",["slot"]=feedback.Index,["events"]=feedback.Output.ReadFeedback(Number(request,"limit",64))};Correlate(request,evt);Emit(evt);break;
                case "destroyOutput":var destroy=Existing(request);await destroy.Output.StopAsync();Result(request,command,new(){["slot"]=destroy.Index});break;
                case "rumble":Rumble(request);break;
                case "refreshBattery":
                    var battery=Existing(request);Result(request,command,new(){["slot"]=battery.Index,["status"]=battery.Source.RuntimeBatteryRefreshStatus,["policy"]="existing_idle_refresh_worker"});break;
                case "selfTest":Result(request,command,WindowsVirtualOutput.SelfTest());break;
                default:throw new HostFailure("unknown_command","command 需要受支持的原生接口名称。");
            }
        }
        catch(HostFailure e){Error(request,e.Code,e.Message);}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception e){Error(request,"native_operation_failed",e.Message);}
    }
    private NativeSlot GetSlot(int index)=>slots.GetOrAdd(index,i=>
    {
        var slot=new NativeSlot(i);Apply(slot);
        slot.Source.RawInputReceived+=(data,isFd2,isPrimary)=>Raw(slot,data,isFd2,isPrimary);
        slot.Source.FactoryCalibrationReceived+=(address,data)=>Emit(new(){["event"]="factoryCalibration",["slot"]=slot.Index,["address"]=address,["base64"]=Convert.ToBase64String(data),["transport"]="bluetooth",["purpose"]="factory_evidence"});
        return slot;
    });
    private NativeSlot Existing(JsonElement request)=>slots.TryGetValue(Index(request),out var slot)?slot:throw new HostFailure("slot_inactive","此通道需要先连接设备或创建输出。");
    private void Enumerate(JsonElement request)
    {
        RequireNoGlobalMaintenance();var slot=GetSlot(Index(request));string transport=Text(request,"transport","auto");
        if(transport is not("auto" or "usb" or "bluetooth"))throw new HostFailure("invalid_transport","transport 取值为 auto、usb 或 bluetooth。");
        slot.ScanCancellation?.Cancel();slot.ScanCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=slot.ScanCancellation.Token;
        bool manual=Flag(request,"manual",false);
        slot.ScanTask=Task.Run(async()=>
        {
            try
            {
                if(transport!="bluetooth")
                {
                    var found=await Task.Run(Pro2UsbDevice.Enumerate,token);
                    foreach(var device in found)usbCandidates[device.Path]=device;
                    Emit(new(){["event"]="devices",["slot"]=slot.Index,["transport"]="usb",["devices"]=found.Select(d=>new{id=d.Path,name="NS2 Pro USB",transport="usb",containerId=d.ContainerId}).ToArray()});
                }
                if(transport!="usb")
                {
                    var observed=new ConcurrentDictionary<string,Pro2ManualCandidate>(StringComparer.OrdinalIgnoreCase);
                    var progress=new DirectProgress<Pro2ManualCandidate>(candidate=>
                    {
                        if(!manual && candidate.Score<30)return;
                        bleCandidates[candidate.AddressText]=candidate;observed[candidate.AddressText]=candidate;
                        EmitDevices(slot.Index,observed.Values);
                    });
                    var found=await slot.Source.ScanManualAsync(Progress(slot),progress,token);
                    foreach(var candidate in found.Where(c=>manual || c.Score>=30)){bleCandidates[candidate.AddressText]=candidate;observed[candidate.AddressText]=candidate;}
                    EmitDevices(slot.Index,observed.Values);
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){}
            catch(Exception e){ErrorForSlot(slot,"enumeration_failed",e.Message);}
        },token);
        Result(request,"enumerate",new(){["slot"]=slot.Index,["transport"]=transport});
    }
    private void EmitDevices(int index,IEnumerable<Pro2ManualCandidate> candidates)=>Emit(new(){["event"]="devices",["slot"]=index,["transport"]="bluetooth",["devices"]=Pro2ManualCandidate.Sort(candidates).Select(d=>new{id=d.AddressText,name=d.DisplayName,transport="bluetooth",rssi=d.Rssi,nintendoManufacturer=d.NintendoManufacturer,score=d.Score,addressType=d.AddressType.ToString()}).ToArray()});
    private async Task ConnectAsync(JsonElement request)
    {
        RequireNoGlobalMaintenance();int index=Index(request);string id=Text(request,"deviceId","");if(id.Length==0)throw new HostFailure("invalid_device","connect 需要枚举得到的 deviceId。");
        string transport=Text(request,"transport","auto");if(transport=="auto")transport=usbCandidates.ContainsKey(id)?"usb":"bluetooth";
        if(transport is not("usb" or "bluetooth"))throw new HostFailure("invalid_transport","transport 取值为 usb 或 bluetooth。");
        if(slots.Values.Any(s=>s.Index!=index && s.DeviceID.Equals(id,StringComparison.OrdinalIgnoreCase) && s.Transport==transport && s.ConnectionCancellation!=null))throw new HostFailure("device_in_use","此手柄已分配到其他通道。");
        if(transport=="bluetooth" && !bleCandidates.ContainsKey(id))throw new HostFailure("invalid_device","请先枚举 BLE 设备以确认地址类型。");
        if(transport=="usb" && !usbCandidates.ContainsKey(id))throw new HostFailure("invalid_device","请先枚举 USB 设备以确认实体设备路径。");
        var slot=GetSlot(index);await slot.DisconnectAsync();slot.DeviceID=id;slot.Transport=transport;Apply(slot);
        slot.ConnectionCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=slot.ConnectionCancellation.Token;
        slot.ConnectionTask=Task.Run(()=>ConnectLoopAsync(slot,token),token);
        Result(request,"connect",new(){["slot"]=index,["deviceId"]=id,["transport"]=transport,["accepted"]=true});
    }
    private void SwitchToUsb(JsonElement request)
    {
        RequireNoGlobalMaintenance();var slot=Existing(request);string id=Text(request,"deviceId","");
        if(id.Length==0 || !usbCandidates.TryGetValue(id,out var candidate))throw new HostFailure("invalid_device","switchToUsb 需要枚举确认的 USB deviceId。");
        if(slot.ConnectionCancellation==null || !slot.Source.IsRunning || !slot.Source.IsTransportConnected || slot.Source.IsUsb)
            throw new HostFailure("bluetooth_required","接管需要此通道具有有效的蓝牙连接。");
        if(inputTransport!=Pro2InputTransport.Auto)throw new HostFailure("auto_transport_required","自动连接偏好允许运行中的 USB 接管。");
        if(Interlocked.CompareExchange(ref slot.SwitchingTransport,1,0)!=0)throw new HostFailure("transport_switch_busy","此通道正在切换实体连接。");
        var copy=request.Clone();var token=slot.ConnectionCancellation.Token;
        slot.TransportSwitchTask=Task.Run(async()=>
        {
            bool entered=false;
            try
            {
                await slot.PhysicalOperations.WaitAsync(token);entered=true;
                var available=await Task.Run(()=>Pro2UsbDevice.Enumerate().Where(x=>!Pro2BleInputSource.UsbPathIsClaimed(x.Path)).ToArray(),token);
                int participants=slots.Values.Count(x=>x.ConnectionCancellation!=null);
                bool busy=globalMaintenance || Volatile.Read(ref maintenanceRunning)!=0 || slot.Source.IsCompatibilityOptimizationRunning;
                if(!WindowsTransportHandover.Eligible(inputTransport,slot.Source.IsUsb,slot.Source.IsRunning&&slot.Source.IsTransportConnected,participants,available.Length,busy))
                    throw new HostFailure("transport_switch_ineligible","接管条件为自动偏好、一个有效通道及一个可用 USB 手柄；当前连接继续保留。");
                candidate=available.FirstOrDefault(x=>x.Path.Equals(id,StringComparison.OrdinalIgnoreCase))
                    ??throw new HostFailure("usb_candidate_changed","指定 USB 候选已改变，当前蓝牙连接继续保留。");
                usbCandidates[candidate.Path]=candidate;
                await slot.Output.NeutralAsync(token);
                slot.Source.TryWriteOutputReport(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"transport-switch").Report,out _);
                await slot.Source.SwitchToUsbAsync(candidate,Progress(slot),token);
                token.ThrowIfCancellationRequested();
                bool connected=slot.Source.IsRunning&&slot.Source.IsTransportConnected;
                string actual=WindowsTransportHandover.ActualTransport(connected,slot.Source.IsUsb);
                if(actual=="usb")
                {
                    slot.Transport="usb";slot.DeviceID=candidate.Path;slot.LastBatterySignature="";slot.LastConnected=true;
                    var changed=new Dictionary<string,object?>{["event"]="transportChanged",["slot"]=slot.Index,["connected"]=true,["transport"]="usb",["deviceId"]=candidate.Path,["outputRetained"]=true};Correlate(copy,changed);Emit(changed);EmitBattery(slot);
                    var completed=new Dictionary<string,object?>{["event"]="operationResult",["command"]="switchToUsb",["success"]=true,["slot"]=slot.Index,["transport"]="usb",["deviceId"]=candidate.Path,["message"]="已接管为 USB 有线输入，游戏中的虚拟手柄实例保持连接。"};Correlate(copy,completed);Emit(completed);
                }
                else throw new HostFailure("usb_takeover_failed",actual=="bluetooth"?"USB 接管等待可用通道，原蓝牙连接继续保留。":"USB 实体输入等待重新连接；原虚拟手柄已归中并保留。");
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){}
            catch(Exception error)
            {
                Error(copy,"usb_switch_failed",error.Message);
                if(!slot.Source.IsRunning || !slot.Source.IsTransportConnected)
                {slot.LastConnected=false;Connection(slot,false);}
            }
            finally{if(entered)slot.PhysicalOperations.Release();Interlocked.Exchange(ref slot.SwitchingTransport,0);}
        });
        Result(request,"switchToUsb",new(){["slot"]=slot.Index,["accepted"]=true,["pending"]=true});
    }
    private async Task<Pro2CompatibilityTrialOutcome> OptimizeAsync(NativeSlot slot,CancellationToken token)
    {
        await slot.PhysicalOperations.WaitAsync(token);
        try{return await slot.Source.OptimizeConnectionCompatibilityAsync(Progress(slot),token);}
        finally{slot.PhysicalOperations.Release();}
    }
    private async Task ConnectLoopAsync(NativeSlot slot,CancellationToken token)
    {
        int attempt=0;
        while(!token.IsCancellationRequested)
        {
            try
            {
                var excluded=slots.Values.Where(s=>s.Index!=slot.Index && s.DeviceID.Length>0).Select(s=>s.DeviceID).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(slot.Transport=="bluetooth")await slot.Source.StartManualAsync(bleCandidates[slot.DeviceID],excluded,null,Progress(slot),token);
                else
                {
                    var expected=usbCandidates[slot.DeviceID];foreach(var other in Pro2UsbDevice.Enumerate().Where(x=>!x.Path.Equals(expected.Path,StringComparison.OrdinalIgnoreCase)))excluded.Add("USB:"+other.ContainerId);
                    // Exact USB reconnection stays on USB; the persisted Auto preference
                    // is restored once initialization has finished for future handover.
                    slot.Source.RequestedTransport=Pro2InputTransport.Usb;
                    try{await slot.Source.StartAsync(Progress(slot),excluded,token);}
                    finally{slot.Source.RequestedTransport=inputTransport;}
                }
                if(!slot.Source.IsRunning)throw new HostFailure("input_inactive",slot.Source.Status);
                attempt=0;slot.LastConnected=true;Connection(slot,true);EmitBattery(slot);
                while((slot.Source.IsRunning || Volatile.Read(ref slot.SwitchingTransport)!=0) && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000,token);
                    if(Volatile.Read(ref slot.SwitchingTransport)!=0)continue;
                    slot.Source.RecheckLiveRate();var evidence=slot.Source.GetRateEvidence();EmitBattery(slot);
                    Emit(new(){["event"]="diagnostics",["slot"]=slot.Index,["transport"]=slot.Transport,["diagnostics"]=new{phase=evidence.Phase.ToString(),measuredHz=evidence.ParsedHz,receivedFrames=evidence.ParsedCount,maximumGapMilliseconds=evidence.Gaps.MaxMs,runtimeBatteryStatus=evidence.BatteryRefreshStatus,controllerFirmware=slot.Source.FirmwareInfo?.ControllerVersion??"",protocolAudit=slot.Source.ProtocolDiagnosticText,compatibilityOptimization=slot.Source.CompatibilityOptimizationSummary}});
                    if(autoOptimize && Volatile.Read(ref maintenanceRunning)==0 && slot.Source.CanOptimizeConnectionCompatibility &&
                        BleLowRateDiagnoser.Evaluate(evidence,BleCompatibilityInspector.Current,null).Visible)
                    {
                        var outcome=await OptimizeAsync(slot,token);
                        Emit(new(){["event"]="status",["slot"]=slot.Index,["message"]=outcome.Summary,["source"]="confirmed_live_low_rate_automatic_optimization"});
                    }
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception e){ErrorForSlot(slot,"connection_failed",e.Message);}
            finally
            {
                await slot.TransportSwitchTask;
                if(slot.LastConnected){slot.LastConnected=false;Connection(slot,false);}
                await slot.PhysicalOperations.WaitAsync();
                try{await slot.Source.StopAsync();}finally{slot.PhysicalOperations.Release();}
            }
            if(!autoReconnect)break;
            attempt++;await Task.Delay(TimeSpan.FromSeconds(Math.Min(8,attempt)),token);
        }
    }
    private void Raw(NativeSlot slot,byte[] data,bool fd2,bool primary)
    {
        string transport=slot.Source.IsUsb?"usb":"bluetooth";
        if(primary){Emit(new(){["event"]="primary",["slot"]=slot.Index,["transport"]=transport,["format"]="primary",["base64"]=Convert.ToBase64String(data)},raw:true);return;}
        if(!fd2)return;
        byte[] payload=data;
        if(slot.Source.IsUsb){payload=new byte[data.Length+1];payload[0]=5;data.CopyTo(payload,1);}
        Emit(new(){["event"]="rawInput",["slot"]=slot.Index,["format"]=slot.Source.IsUsb?"usb":"fd2",["base64"]=Convert.ToBase64String(payload),["hostUptimeSeconds"]=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency},raw:true);
    }
    private void EmitBattery(NativeSlot slot)
    {
        var snapshot=slot.Source.GetBatterySnapshot();if(!snapshot.HasConnectionLevel)return;
        var state=snapshot.State;
        string signature=$"{state.BatteryLevel}:{state.BatteryPercent}:{state.BatteryCharging}:{state.BatteryExternalPower}:{state.BatteryPowerKnown}:{snapshot.Transport}";
        if(signature==slot.LastBatterySignature)return;slot.LastBatterySignature=signature;
        Emit(new(){["event"]="battery",["slot"]=slot.Index,["level"]=(int)state.BatteryLevel,["percent"]=(int)state.BatteryPercent,["charging"]=state.BatteryCharging,["externalPower"]=state.BatteryExternalPower,["powerKnown"]=state.BatteryPowerKnown,["voltageMillivolts"]=(int)state.BatteryVoltageMillivolts,["source"]=snapshot.Transport,["levelAgeSeconds"]=double.IsFinite(snapshot.LevelAgeSeconds)?snapshot.LevelAgeSeconds:0,["fresh"]=snapshot.Fresh,["runtimeLevel"]=snapshot.RuntimeLevel,["levelFresh"]=snapshot.LevelFresh});
    }
    private void Rumble(JsonElement request)
    {
        var slot=Existing(request);byte[] report;
        if(Volatile.Read(ref slot.SwitchingTransport)!=0){if(Correlated(request))Result(request,"rumble",new(){["slot"]=slot.Index,["deferred"]=true});return;}
        if(request.TryGetProperty("base64",out var base64))report=Convert.FromBase64String(base64.GetString()??"");
        else if(request.TryGetProperty("raw02",out var bytes))report=bytes.EnumerateArray().Select(v=>v.GetByte()).ToArray();
        else throw new HostFailure("invalid_rumble","rumble 需要 raw02 字节数组或 base64。");
        double gain=request.TryGetProperty("gain",out var g)?g.GetDouble():1;
        if(!double.IsFinite(gain) || gain<0 || gain>3)throw new HostFailure("invalid_rumble","gain 取值范围为 0...3。");
        slot.Source.SetRumbleGain(gain);
        if(!slot.Source.TryWriteOutputReport(report,out string error))throw new HostFailure("rumble_unavailable",error);
        if(Correlated(request))Result(request,"rumble",new(){["slot"]=slot.Index});
    }
    private void ApplySettings(JsonElement request)
    {
        var values=request.TryGetProperty("settings",out var s)?s:request;
        runtimeBattery=Flag(values,"runtimebattery",Flag(values,"runtimeBatteryRefreshEnabled",runtimeBattery));autoOptimize=Flag(values,"autooptimize",Flag(values,"autoOptimizeReportRate",autoOptimize));autoReconnect=Flag(values,"autoReconnect",Flag(values,"automaticallyReconnect",autoReconnect));
        skipInitialization=Flag(values,"skip",Flag(values,"skipInitialization",skipInitialization));
        if(values.TryGetProperty("inputTransport",out var transport))inputTransport=WindowsTransportHandover.Preference(transport.GetString()??"");
        runtime.ApplySettings(values);foreach(var slot in slots.Values)Apply(slot);
    }
    private void Apply(NativeSlot slot){slot.Output.SetMotionTuning(runtime.MotionTuning,runtime.ProfessionalInversion);slot.Source.RequestedTransport=inputTransport;slot.Source.AutoOptimizeBleEnabled=autoOptimize;slot.Source.SkipUsbInitializationOnNextConnect=skipInitialization;slot.Source.SetRuntimeBatteryRefreshEnabled(runtimeBattery);slot.Source.SetStickProcessingMode(StickProcessingMode.RawDirect);}
    private void RequireNoGlobalMaintenance(){if(globalMaintenance)throw new HostFailure("maintenance_busy","全局驱动或蓝牙配置正在执行，请等待该操作完成。");}
    private void StartMaintenance(JsonElement request,string command,bool global,Func<Task<Dictionary<string,object?>>> action)
    {
        if(Interlocked.CompareExchange(ref maintenanceRunning,1,0)!=0)throw new HostFailure("maintenance_busy","当前系统维护操作正在执行。");
        globalMaintenance=global;var copy=request.Clone();
        Result(copy,command,new(){["accepted"]=true,["pending"]=true});
        maintenanceTask=Task.Run(async()=>{
            try
            {
                var result=await action();result["event"]="operationResult";result["command"]=command;Correlate(copy,result);Emit(result);
                if(result.TryGetValue("message",out var message))Emit(new(){["event"]="status",["slot"]=Index(copy),["message"]=message});
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(HostFailure e){Error(copy,e.Code,e.Message);}
            catch(Exception e){Error(copy,"native_operation_failed",e.Message);}
            finally{globalMaintenance=false;Interlocked.Exchange(ref maintenanceRunning,0);}
        });
    }
    private async Task ReleaseSlotsAsync()
    {
        foreach(var pair in slots.ToArray())if(slots.TryRemove(pair.Key,out var slot))
        {
            slot.LastConnected=false;Connection(slot,false);await slot.DisposeAsync();
        }
    }
    private IProgress<string> Progress(NativeSlot slot)=>new DirectProgress<string>(message=>Emit(new(){["event"]="status",["slot"]=slot.Index,["transport"]=slot.Transport,["message"]=message}));
    private void Connection(NativeSlot slot,bool connected)=>Emit(new(){["event"]="connection",["slot"]=slot.Index,["transport"]=slot.Transport,["connected"]=connected,["deviceId"]=slot.DeviceID});
    private void ErrorForSlot(NativeSlot slot,string code,string message)=>Emit(new(){["event"]="error",["slot"]=slot.Index,["code"]=code,["message"]=message});
    private void Emit(Dictionary<string,object?> value,bool raw=false)=>writer.Emit(value,raw);
    private void Result(JsonElement request,string command,Dictionary<string,object?>? values=null){values??=new();values["event"]="commandResult";values["command"]=command;values["ok"]=true;Correlate(request,values);Emit(values);}
    private void Error(JsonElement request,string code,string message){var value=new Dictionary<string,object?>{["event"]="error",["code"]=code,["message"]=message};Correlate(request,value);Emit(value);}
    private static void Correlate(JsonElement request,Dictionary<string,object?> result){if(request.ValueKind!=JsonValueKind.Object)return;foreach(string key in new[]{"id","requestId","slot"})if(request.TryGetProperty(key,out var v))result[key]=v.Clone();}
    private static bool Correlated(JsonElement request)=>request.TryGetProperty("id",out _) || request.TryGetProperty("requestId",out _);
    private static int Index(JsonElement request){int index=Number(request,"slot",0);return index is >=0 and <=3?index:throw new HostFailure("invalid_slot","slot 取值为 0、1、2 或 3。");}
    private static string Text(JsonElement request,string name,string fallback)=>request.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
    private static int Number(JsonElement request,string name,int fallback)=>request.TryGetProperty(name,out var v)?v.GetInt32():fallback;
    private static bool Flag(JsonElement request,string name,bool fallback)=>request.TryGetProperty(name,out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
    public async ValueTask DisposeAsync()
    {
        if(shuttingDown)return;shuttingDown=true;lifetime.Cancel();try{await maintenanceTask;}catch(OperationCanceledException){}foreach(var slot in slots.Values)await slot.DisposeAsync();slots.Clear();await runtime.DisposeAsync();
        Emit(new(){["event"]="stopped",["reason"]="hostShutdown"});await writer.DisposeAsync();lifetime.Dispose();
    }
}
