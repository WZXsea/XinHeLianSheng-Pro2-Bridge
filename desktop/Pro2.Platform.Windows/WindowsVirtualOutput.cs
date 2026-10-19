using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Y700Switch2V60Viiper;

namespace Pro2.Platform.Windows;

internal sealed class WindowsRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private Process? ownedServer;
    private int apiPort=3242;
    private string? configuredViiper,configuredUsbip;
    public Ps5OutputImuTuning MotionTuning { get;private set; }=Ps5OutputImuTuning.Default;
    public GyroAxisInversion ProfessionalInversion { get;private set; }=default;
    public void ApplySettings(JsonElement values)
    {
        if(values.TryGetProperty("viiperPath",out var viiper))configuredViiper=viiper.GetString();
        if(values.TryGetProperty("usbipPath",out var usbip))configuredUsbip=usbip.GetString();
        double Scale(string name,double fallback)=>values.TryGetProperty(name,out var value)?V60UserSettings.NormalizePs5GyroScale(value.GetDouble()):fallback;
        bool Invert(string name,bool fallback)=>values.TryGetProperty(name,out var value)?value.GetBoolean():fallback;
        MotionTuning=new(Scale("ps5GyroScalePitch",MotionTuning.GyroScalePitch),Scale("ps5GyroScaleYaw",MotionTuning.GyroScaleYaw),Scale("ps5GyroScaleRoll",MotionTuning.GyroScaleRoll));
        ProfessionalInversion=new(Invert("professionalInvertGyroPitch",ProfessionalInversion.InvertX),Invert("professionalInvertGyroYaw",ProfessionalInversion.InvertY),Invert("professionalInvertGyroRoll",ProfessionalInversion.InvertZ));
        if(values.TryGetProperty("apiPort",out var port)){int p=port.GetInt32();if(p is <2 or >65535)throw new HostFailure("invalid_settings","apiPort 取值范围为 2...65535。");apiPort=p;}
    }
    public async Task<Dictionary<string,object?>> StatusAsync(CancellationToken token)
    {
        var usbip=UsbipRuntimeLocator.Find(configuredUsbip);string? exe=FindViiper();bool service=false;string message="等待 VIIPER 服务。";
        try{await new ViiperProtocolClient("127.0.0.1",apiPort).PingAsync(token);service=true;message="VIIPER 服务已就绪。";}catch(Exception e)when(e is not OperationCanceledException){message=e.Message;}
        if(usbip==null)message="USBIP 运行时待安装或配置；"+(service?"VIIPER 服务已就绪。":"VIIPER 服务将在选择输出后启动。");
        else if(!service && exe!=null)message="USBIP 运行时已找到；VIIPER 服务将在选择输出后启动。";
        return new(){["ready"]=service && usbip!=null,["message"]=message,["viiper"]=new{installed=exe!=null,serviceAvailable=service,apiPort,message,supportedModes=new[]{"pro2","pro1","xbox","dualsense","dualsenseproimu","xboxproimu"}},["usbip"]=new{installed=usbip!=null,path=usbip?.ExePath??"",requiresAdministrator=true},["pro2"]=new{available=service && usbip!=null,identity="057E:2069"}};
    }
    public async Task<ViiperProtocolClient> EnsureAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var usbip=UsbipRuntimeLocator.Find(configuredUsbip)??throw new HostFailure("usbip_missing","请先安装原版 USBIP 运行时，或在设置中选择 usbip.exe。");
            Environment.SetEnvironmentVariable("PATH",UsbipRuntimeLocator.BuildPathWithUsbipDirectory(Environment.GetEnvironmentVariable("PATH")??"",usbip),EnvironmentVariableTarget.Process);
            var client=new ViiperProtocolClient("127.0.0.1",apiPort);
            try{await client.PingAsync(token);return client;}catch(Exception e)when(e is not OperationCanceledException){}
            string executable=FindViiper()??throw new HostFailure("viiper_missing","原版 VIIPER 服务运行时待配置。");
            if(ownedServer is {HasExited:false})throw new HostFailure("viiper_starting","VIIPER 已启动，请稍候重试输出。");
            int selected=new[]{apiPort,3252,3262,3272}.Distinct().FirstOrDefault(p=>Available(p)&&Available(p-1));
            if(selected==0)throw new HostFailure("viiper_ports_unavailable","VIIPER 本地端口等待释放。");
            apiPort=selected;client=new ViiperProtocolClient("127.0.0.1",apiPort);
            string logDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2Bridge","logs");Directory.CreateDirectory(logDirectory);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)??AppContext.BaseDirectory};
            foreach(string argument in new[]{"server","--api.addr=127.0.0.1:"+apiPort,"--usb.addr=127.0.0.1:"+(apiPort-1),"--api.device-handler-connect-timeout=60s","--usb.write-batch-flush-interval=0ms","--update-notify=none","--log.level=info","--log.file="+Path.Combine(logDirectory,"viiper-host.log")})start.ArgumentList.Add(argument);
            ownedServer=Process.Start(start)??throw new HostFailure("viiper_start_failed","VIIPER 进程启动失败。");
            ownedServer.OutputDataReceived+=(_,e)=>{if(e.Data!=null)Console.Error.WriteLine(e.Data);};ownedServer.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)Console.Error.WriteLine(e.Data);};ownedServer.BeginOutputReadLine();ownedServer.BeginErrorReadLine();
            Exception? last=null;
            for(int i=0;i<40;i++)
            {
                token.ThrowIfCancellationRequested();if(ownedServer.HasExited)throw new HostFailure("viiper_start_failed","VIIPER 服务已退出，退出码 "+ownedServer.ExitCode+"。");
                try{await client.PingAsync(token);return client;}catch(Exception e)when(e is not OperationCanceledException){last=e;}
                await Task.Delay(250,token);
            }
            throw new HostFailure("viiper_timeout",last?.Message??"VIIPER 服务等待启动完成。");
        }
        finally{gate.Release();}
    }
    private string? FindViiper()
    {
        if(!string.IsNullOrWhiteSpace(configuredViiper) && Path.IsPathFullyQualified(configuredViiper) && File.Exists(configuredViiper))return configuredViiper;
        // The executable/license are the original WPF assembly's signed release resources.
        Assembly original=typeof(Pro2BleInputSource).Assembly;
        using Stream? embedded=original.GetManifestResourceStream("Embedded.viiper.haptic.exe");
        if(embedded!=null)
        {
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PRO2Bridge","runtime","viiper-haptic-v0.8.0");Directory.CreateDirectory(directory);
            string destination=Path.Combine(directory,"viiper-haptic-pro1.exe");byte[] expected=SHA256.HashData(embedded);embedded.Position=0;
            bool same=false;if(File.Exists(destination)){using var file=File.OpenRead(destination);same=CryptographicOperations.FixedTimeEquals(expected,SHA256.HashData(file));}
            if(!same){string temp=destination+".tmp";using(var target=File.Create(temp))embedded.CopyTo(target);File.Move(temp,destination,true);}
            using Stream? license=original.GetManifestResourceStream("Embedded.viiper.haptic.license");if(license!=null){using var target=File.Create(Path.Combine(directory,"LICENSE.txt"));license.CopyTo(target);}
            return destination;
        }
        for(string? directory=AppContext.BaseDirectory;directory!=null;directory=Directory.GetParent(directory)?.FullName)
        {
            string path=Path.Combine(directory,"source","final-three-in-one","tools","viiper","haptic-v0.8.0","viiper-haptic-pro1.exe");if(File.Exists(path))return path;
            path=Path.Combine(directory,"tools","viiper","haptic-v0.8.0","viiper-haptic-pro1.exe");if(File.Exists(path))return path;
        }
        return null;
    }
    private static bool Available(int port){try{var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();listener.Stop();return true;}catch(SocketException){return false;}}
    public async ValueTask DisposeAsync()
    {
        if(ownedServer!=null){try{if(!ownedServer.HasExited){ownedServer.Kill(entireProcessTree:true);await ownedServer.WaitForExitAsync();}}catch(InvalidOperationException){}ownedServer.Dispose();ownedServer=null;}gate.Dispose();
    }
}

internal sealed class WindowsVirtualOutput : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private readonly ConcurrentQueue<Dictionary<string,object?>> feedback=new();
    private ViiperProtocolClient? client;
    private ViiperDevice? device;
    private ViiperDeviceStream? stream;
    private ViiperDeviceProfile? profile;
    private uint? bus;
    private string mode="";
    private CancellationTokenSource? cancellation;
    private Task reader=Task.CompletedTask;
    private IProgress<string>? progress;
    private Ps5OutputImuTuning motionTuning=Ps5OutputImuTuning.Default;
    private GyroAxisInversion professionalInversion;
    public void SetMotionTuning(Ps5OutputImuTuning tuning,GyroAxisInversion inversion){motionTuning=tuning.Normalize();professionalInversion=inversion;}
    public async Task NeutralAsync(CancellationToken token)
    {
        if(stream!=null && profile!=null)await stream.WriteAsync(VirtualPadPackets.NeutralInput(profile),token);
    }
    public async Task CreateAsync(ViiperProtocolClient client,string mode,int slot,IProgress<string> progress,CancellationToken token)
    {
        await StopAsync();this.client=client;this.mode=mode;this.progress=progress;
        profile=Profile(mode) with {DeviceSpecificSerialNumber=ViiperDeviceProfile.SlotSerialNumber(Profile(mode).Mode,slot+1),SourcePaced=true};
        try
        {
            bus=await client.BusCreateAsync(token);device=await client.AddDeviceAsync(bus.Value,profile.DeviceType,profile.DeviceSpecificArguments(),token);
            if(!profile.MatchesIdentity(device))throw new HostFailure("virtual_identity_mismatch","VIIPER 创建的设备身份与所选模式不一致。");
            var repair=await ControllerEnumerationDiagnostics.EnsureSingleUsbipAttachmentAsync(device,client.Host,client.Port,token);
            foreach(string line in repair.Lines)progress.Report(line);
            if(repair.FinalPortCount!=1)throw new HostFailure("usbip_attachment_failed","USBIP 实体枚举需要恰好一个虚拟设备实例；请检查驱动安装和管理员权限。");
            stream=await client.OpenStreamAsync(device.BusId,device.DevId,token);await stream.WriteAsync(VirtualPadPackets.NeutralInput(profile),token);
            cancellation=new CancellationTokenSource();reader=Task.Run(()=>ReadLoopAsync(cancellation.Token));
        }
        catch{await StopAsync();throw;}
    }
    public async Task SubmitAsync(JsonElement json,CancellationToken token)
    {
        if(stream==null || profile==null)throw new HostFailure("output_inactive","请先创建此通道的虚拟输出。");
        if(json.ValueKind!=JsonValueKind.Object)throw new HostFailure("invalid_state","state 需要对象。");
        var state=JsonSerializer.Deserialize<GamepadState>(json,JsonOptions)??throw new HostFailure("invalid_state","state 需要有效的共享 GamepadState。");
        if(((ulong)state.Buttons & ~0x1fffffUL)!=0 || new[]{state.Lx,state.Ly,state.Rx,state.Ry,state.L2,state.R2}.Any(v=>v>4095))throw new HostFailure("invalid_state","逻辑按键和摇杆/扳机需要共享协议定义范围。");
        await stream.WriteAsync(VirtualPadPackets.FromGamepad(profile,state,profile.IsProfessionalImuTest?professionalInversion:default,ps5OutputImuTuning:motionTuning),token);
    }
    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested && stream!=null && profile!=null)
            {
                byte[] raw=await stream.ReadExactAsync(profile.FeedbackSize,token);
                var evt=new Dictionary<string,object?>{["kind"]=3,["wireFormat"]="viiper",["mode"]=mode,["rawBase64"]=Convert.ToBase64String(raw)};
                if(profile.Mode is ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest && raw.Length>=2){evt["kind"]=0;evt["lowRumble"]=raw[0]/255.0;evt["highRumble"]=raw[1]/255.0;}
                while(feedback.Count>=64)feedback.TryDequeue(out _);feedback.Enqueue(evt);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(ObjectDisposedException)when(token.IsCancellationRequested){}
        catch(Exception e){progress?.Report("[VIIPER_FEEDBACK] "+e.Message);}
    }
    public Dictionary<string,object?>[] ReadFeedback(int limit)
    {
        var events=new List<Dictionary<string,object?>>();for(int i=0;i<Math.Clamp(limit,0,64) && feedback.TryDequeue(out var evt);i++)events.Add(evt);return events.ToArray();
    }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if(stream!=null){if(profile!=null){using var neutral=new CancellationTokenSource(500);try{await stream.WriteAsync(VirtualPadPackets.NeutralInput(profile),neutral.Token);}catch(Exception){}}await stream.DisposeAsync();stream=null;}
        try{await reader;}catch(OperationCanceledException){}reader=Task.CompletedTask;cancellation?.Dispose();cancellation=null;
        if(client!=null)
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if(device!=null)
            {
                try{foreach(string line in await ControllerEnumerationDiagnostics.DetachViiperDeviceAsync(device,client.Host,client.Port,cleanup.Token))progress?.Report(line);}catch(Exception e){progress?.Report("[VIIPER_STOP] detach "+e.Message);}
                try{await client.RemoveDeviceAsync(device.BusId,device.DevId,cleanup.Token);}catch(Exception e){progress?.Report("[VIIPER_STOP] device "+e.Message);}
            }
            if(bus.HasValue)try{await client.RemoveBusAsync(bus.Value,cleanup.Token);}catch(Exception e){progress?.Report("[VIIPER_STOP] bus "+e.Message);}
        }
        client=null;device=null;bus=null;profile=null;mode="";while(feedback.TryDequeue(out _)){}
    }
    public ValueTask DisposeAsync()=>new(StopAsync());
    private static ViiperDeviceProfile Profile(string mode)=>mode switch
    {
        "pro2"=>ViiperDeviceProfile.Pro2,"pro1"=>ViiperDeviceProfile.Pro1,"xbox"=>ViiperDeviceProfile.Xbox,"dualsense"=>ViiperDeviceProfile.DualSenseLike,"dualsenseproimu"=>ViiperDeviceProfile.DualSenseProfessionalImuTest,"xboxproimu"=>ViiperDeviceProfile.XboxProfessionalImuTest,
        _=>throw new HostFailure("profile_unsupported","输出模式需要 pro2、pro1、xbox 或 dualsense。")
    };
    public static Dictionary<string,object?> SelfTest()
    {
        WindowsTransportHandover.SelfTest();
        foreach(string mode in new[]{"pro2","pro1","xbox","dualsense","dualsenseproimu","xboxproimu"})
        {
            var p=Profile(mode);if(VirtualPadPackets.FromGamepad(p,new GamepadState{Buttons=GamepadButtons.South,L2=4095}).Length!=p.InputSize)throw new HostFailure("self_test_failed","虚拟报文长度校验失败："+mode);
        }
        for(int bit=0;bit<21;bit++)
        {
            var state=new GamepadState{Buttons=(GamepadButtons)(1UL<<bit)};
            foreach(string mode in new[]{"pro2","pro1","xbox","dualsense","dualsenseproimu","xboxproimu"})
                if(VirtualPadPackets.FromGamepad(Profile(mode),state).Length!=Profile(mode).InputSize)throw new HostFailure("self_test_failed","逻辑按键状态校验失败。");
        }
        var motion=new GamepadState{GyroValid=true,GyroX=100,GyroY=200,GyroZ=300};
        var tuned=VirtualPadPackets.FromGamepad(ViiperDeviceProfile.DualSenseLike,motion,ps5OutputImuTuning:new Ps5OutputImuTuning(2,3,4));
        if(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(tuned.AsSpan(21))!=200 || System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(tuned.AsSpan(23))!=900 || System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(tuned.AsSpan(25))!=-800)throw new HostFailure("self_test_failed","PS5 三轴灵敏度应用次数校验失败。");
        if((int)Pro2InputTransport.Auto!=0 || (int)Pro2InputTransport.Bluetooth!=1 || (int)Pro2InputTransport.Usb!=2)throw new HostFailure("self_test_failed","持久化传输枚举值校验失败。");
        return new(){["passed"]=true,["logicalButtonsTested"]=21,["profiles"]=6,["motionTuningTested"]=true,["transportSwitchPolicyTested"]=true,["stateContract"]="GamepadState-v1",["scope"]="offline_serialization_and_profile_packers"};
    }
}
