using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Y700Switch2V60Viiper;
namespace Pro2.Application;

public sealed record ControllerDevice(string Id,string Name,string Transport,int Slot=0,int? Rssi=null,int? Score=null,string Evidence="",string AddressType="") {
 public string SignalText=>Transport=="usb"?"USB 有线":Rssi is{}r?$"{r} dBm":"等待信号数据";
 public string ConfidenceText=>Transport=="usb"?"VID/PID 匹配":Score is{}s?$"候选评分 {s}":"GATT 服务核验";
 public string AddressDetail=>string.IsNullOrWhiteSpace(AddressType)?Id:$"{Id} · {AddressType}";
}
public sealed class ControllerSession {
 public int Index {get;init;}
 public string DeviceId {get;set;}="";
 public string Transport {get;set;}="";
 public bool Connected {get;set;}
 public bool Busy {get;set;}
 public bool SwitchingTransport {get;set;}
 public string PendingUsbDeviceId {get;set;}="";
 public string FailedUsbDeviceId {get;set;}="";
 public long SwitchGeneration {get;set;}
 public bool OutputReady {get;set;}
 public bool Enabled {get;set;}=true;
 public long Generation;
 public string Status {get;set;}="等待连接";
 public GamepadState Physical {get;set;}=GamepadState.Neutral();
 public GamepadState Mapped {get;set;}=GamepadState.Neutral();
 public GamepadState Submitted {get;set;}=GamepadState.Neutral();
 public GamepadState ActualOutput {get;set;}=GamepadState.Neutral();
 public bool ActualOutputAvailable {get;set;}
 public GamepadButtons Seen {get;set;}
 public Pro2HidReportParser Parser {get;set;}=new();
 public Pro2InputStabilityFilter Filter {get;}=new();
 public RearButtonMapper Rear {get;}=new();
 public DualSenseHapticRumbleScheduler Haptics {get;set;}=new(true);
 public Queue<long> FrameTimes {get;}=new();
 public Queue<double> Gaps {get;}=new();
 public long LastInput {get;set;}
 public long LastActivity {get;set;}
 public long LastBattery {get;set;}
 public long LastSubmit {get;set;}
 public long Frames {get;set;}
 public double Hertz {get;set;}
 public double GapP50 {get;set;}
 public double GapP95 {get;set;}
 public string Diagnostic {get;set;}="";
 public string FactoryCalibration {get;set;}="";
 public string Firmware {get;set;}="等待手柄返回";
 public RumblePreviewMixer Preview {get;set;}=null!;
}

/// Shared application state and output mapping. Both desktop packages use this assembly.
/// The native host is limited to OS device I/O and virtual-device creation.
public sealed class ControllerService : IAsyncDisposable {
 public PlatformBridge Bridge {get;}=new();
 public V60UserSettings Settings {get;}=V60UserSettings.Load();
 public ControllerSession[] Sessions {get;}
 public ConcurrentDictionary<string,ControllerDevice> Devices {get;}=new();
 public event Action<string>? Logged;
 public event Action? Changed;
 public string BackendStatus {get;private set;}="正在检查虚拟手柄驱动";
 public string LastStatus {get;private set;}="等待连接手柄";
 public string LastError {get;private set;}="";
 public string Backend {get;set;}=OperatingSystem.IsWindows()?"viiper":"broker";
 public bool SkipInitialization {get;set;}
 public bool MonitorActualOutput {get;set;}
 public int ActualOutputIndex {get;set;}
 public int MonitoredSlot {get;set;}
 readonly object gate=new();
 readonly CancellationTokenSource lifetime=new();
 readonly SemaphoreSlim outputOperations=new(1,1);
 readonly SemaphoreSlim connectionOperations=new(1,1);
 readonly ConcurrentDictionary<int,string> pendingConnections=new();
 Task? pump;
 int disposed;
 int usbSwitchRequest;
 long usbDiscoveryAt;
 public ControllerService(){
  Sessions=Enumerable.Range(0,4).Select(i=>new ControllerSession{Index=i}).ToArray();
  foreach(var s in Sessions)s.Preview=new RumblePreviewMixer(()=>s.Connected,(byte[] data,out string error)=>{error="";if(!s.Connected){error="请先连接手柄";return false;}_=Bridge.SendAsync(new{command="rumble",slot=s.Index,raw02=data.Select(x=>(int)x).ToArray(),gain=Settings.RumbleMultiplier});return true;});
  Bridge.Log+=Log;Bridge.EventReceived+=Handle;
 }
 public async Task InitializeAsync(){await Bridge.StartAsync();await SendSettingsAsync();await InspectBackendAsync();pump??=PumpAsync(lifetime.Token);if(Settings.AutoReconnectOnStartupEnabled){pendingConnections[0]="";await SearchAsync();}}
 public async Task SearchAsync(bool manual=false){await Bridge.SendAsync(new{command="enumerate",slot=0,transport=TransportKey(Settings.InputTransport),manual});}
 public static string TransportKey(Pro2InputTransport kind)=>kind==Pro2InputTransport.Usb?"usb":kind==Pro2InputTransport.Bluetooth?"bluetooth":"auto";
 public static ViiperDeviceProfile Profile(string key)=>key switch{"xbox"=>ViiperDeviceProfile.Xbox,"pro1"=>ViiperDeviceProfile.Pro1,"dualsense"=>ViiperDeviceProfile.DualSenseLike,"xboxproimu"=>ViiperDeviceProfile.XboxProfessionalImuTest,"dualsenseproimu"=>ViiperDeviceProfile.DualSenseProfessionalImuTest,_=>ViiperDeviceProfile.Pro2};
 public async Task ConnectAsync(int slot=0,string? id=null){
  var s=Session(slot);if(!s.Enabled)return;
  await connectionOperations.WaitAsync(lifetime.Token);
  try{
  if(!s.Enabled)return;
  var reserved=Sessions.Where(x=>x.Index!=slot&&(x.Connected||x.Busy)).Select(x=>x.DeviceId).ToHashSet();
  var candidates=Devices.Values.Where(x=>!reserved.Contains(x.Id)&&(Settings.InputTransport==Pro2InputTransport.Auto||x.Transport==TransportKey(Settings.InputTransport)));
  var device=!string.IsNullOrEmpty(id)?candidates.FirstOrDefault(x=>x.Id==id):candidates.FirstOrDefault(x=>x.Id==Settings.LastConnectedPro2Addresses[slot])??candidates.OrderBy(x=>x.Transport=="usb"?0:1).FirstOrDefault();
  if(device==null){pendingConnections[slot]=id??"";await SearchAsync();Log($"手柄 {slot+1} 正在寻找设备，请开启配对广播或接入数据线。");return;}
  pendingConnections.TryRemove(slot,out _);
  await DisconnectAsync(slot);if(!s.Enabled)return;s.DeviceId=device.Id;s.Transport=device.Transport;s.Busy=true;s.Status="正在连接";Changed?.Invoke();
  await SendSettingsAsync();await Bridge.SendAsync(new{command="connect",slot,deviceId=device.Id,transport=device.Transport});
  }finally{connectionOperations.Release();}
 }
 public async Task SetSlotEnabledAsync(int slot,bool enabled){var s=Session(slot);s.Enabled=enabled;if(!enabled){pendingConnections.TryRemove(slot,out _);await DisconnectAsync(slot);}Changed?.Invoke();}
 async Task NeutralRumbleAsync(ControllerSession s){
  if(s.Connected)s.Preview.WriteGame(Pro2OutputPacketMapper.BuildOrdinaryPacket(0,0,"session-stop").Report,out _);
  await s.Preview.StopAsync(false);s.Haptics=new(true);
 }
 async Task TryAutoSwitchUsbAsync(){
  if(Settings.InputTransport!=Pro2InputTransport.Auto||Sessions.Count(s=>s.Connected||s.Busy)!=1||Interlocked.CompareExchange(ref usbSwitchRequest,1,0)!=0)return;
  try{
   var source=Sessions.SingleOrDefault(s=>s.Connected&&!s.Busy&&!s.SwitchingTransport&&s.Transport=="bluetooth");
   var usb=Devices.Values.Where(d=>d.Transport=="usb"&&!Sessions.Any(s=>(s.Connected||s.Busy)&&s.DeviceId==d.Id)).ToArray();
   if(source==null||usb.Length!=1||source.FailedUsbDeviceId==usb[0].Id)return;
   source.Busy=true;source.SwitchingTransport=true;source.PendingUsbDeviceId=usb[0].Id;source.SwitchGeneration=Interlocked.Read(ref source.Generation);source.Status="正在从蓝牙切换到 USB";source.LastInput=0;source.Rear.Reset();
   await NeutralRumbleAsync(source);if(source.OutputReady)await SubmitAsync(source,GamepadState.Neutral());
   Changed?.Invoke();
   var result=await Bridge.RequestAsync(new(){{"command","switchToUsb"},{"slot",source.Index},{"deviceId",usb[0].Id}},TimeSpan.FromSeconds(15));
   if(GetString(result,"event")=="error"){source.Busy=false;source.SwitchingTransport=false;source.FailedUsbDeviceId=usb[0].Id;source.PendingUsbDeviceId="";source.Status=GetString(result,"message");Log(source.Status);Changed?.Invoke();}
  }catch(Exception ex){foreach(var s in Sessions.Where(s=>s.SwitchingTransport)){s.Busy=false;s.SwitchingTransport=false;s.FailedUsbDeviceId=s.PendingUsbDeviceId;s.PendingUsbDeviceId="";s.Status=ex.Message;}Log("USB 接管："+ex.Message);Changed?.Invoke();}
  finally{Interlocked.Exchange(ref usbSwitchRequest,0);}
 }
 public async Task DisconnectAsync(int slot){
  var s=Session(slot);pendingConnections.TryRemove(slot,out _);
  Interlocked.Increment(ref s.Generation);
  await NeutralRumbleAsync(s);
  if(Bridge.IsRunning)await Bridge.SendAsync(new{command="disconnect",slot});
  lock(gate){s.Connected=false;s.Busy=false;s.SwitchingTransport=false;s.PendingUsbDeviceId="";s.FailedUsbDeviceId="";s.OutputReady=false;s.Rear.Reset();s.Filter.Reset();s.Physical=GamepadState.Neutral();s.Mapped=GamepadState.Neutral();s.Submitted=GamepadState.Neutral();s.LastInput=0;s.Status=s.Enabled?"已停止 / 暂停重连":"通道已禁用";}
  Changed?.Invoke();
 }
 public async Task StopAsync(){pendingConnections.Clear();foreach(var s in Sessions)await DisconnectAsync(s.Index);if(Bridge.IsRunning)await Bridge.SendAsync(new{command="stop"});LastStatus="已停止 / 暂停重连";Changed?.Invoke();}
 public async Task SelectOutputAsync(string key){Settings.SelectedModeKey=V60UserSettings.NormalizeModeKey(key);CancelActions();SaveSettings();foreach(var s in Sessions.Where(x=>x.Connected))await CreateOutputAsync(s);Changed?.Invoke();}
 public async Task InspectBackendAsync(){try{var result=await Bridge.RequestAsync(new(){{"command","backendStatus"}});BackendStatus=FormatBackend(result);Changed?.Invoke();}catch(Exception ex){BackendStatus=ex.Message;Log(ex.Message);}}
 static string FormatBackend(JsonElement e){if(e.TryGetProperty("message",out var m)&&!string.IsNullOrWhiteSpace(m.GetString()))return m.GetString()!;return e.GetRawText();}
 async Task CreateOutputAsync(ControllerSession s){
  var generation=Interlocked.Read(ref s.Generation);var mode=Settings.SelectedModeKey;
  await outputOperations.WaitAsync(lifetime.Token);
  try{
   if(!s.Connected||Interlocked.Read(ref s.Generation)!=generation)return;
   s.OutputReady=false;await NeutralRumbleAsync(s);
   var response=await Bridge.RequestAsync(new(){{"command","createOutput"},{"slot",s.Index},{"mode",mode},{"backend",Backend}});
   if(GetString(response,"event")=="error")throw new InvalidOperationException(GetString(response,"message"));
   if(!s.Connected||Interlocked.Read(ref s.Generation)!=generation||Settings.SelectedModeKey!=mode){await Bridge.SendAsync(new{command="destroyOutput",slot=s.Index});return;}
   s.OutputReady=true;s.Status="真实输入与游戏输出已就绪";Log($"手柄 {s.Index+1} · {Profile(Settings.SelectedModeKey).Label} 输出已创建");
  }catch(Exception ex){s.OutputReady=false;BackendStatus=ex.Message;s.Status="真实输入已就绪 · 游戏输出等待驱动";Log(ex.Message);}finally{outputOperations.Release();Changed?.Invoke();}
 }
 public async Task SendSettingsAsync()=>await Bridge.SendAsync(new{command="settings",inputTransport=TransportKey(Settings.InputTransport),runtimeBatteryRefreshEnabled=Settings.RuntimeBatteryRefreshEnabled,autoOptimizeReportRate=Settings.AutoOptimizeBle,skipInitialization=SkipInitialization,automaticallyReconnect=true,usbipPath=Settings.UsbipExecutablePath,ps5GyroScalePitch=Settings.Ps5GyroScalePitch,ps5GyroScaleYaw=Settings.Ps5GyroScaleYaw,ps5GyroScaleRoll=Settings.Ps5GyroScaleRoll,professionalInvertGyroPitch=Settings.ProfessionalInvertGyroPitch,professionalInvertGyroYaw=Settings.ProfessionalInvertGyroYaw,professionalInvertGyroRoll=Settings.ProfessionalInvertGyroRoll});
 public void SaveSettings(){Settings.Save();if(Volatile.Read(ref disposed)==0&&Bridge.IsRunning)_=SendSettingsAsync();}
 public void CancelActions(){lock(gate)foreach(var s in Sessions)if(s.Rear.TryCancelOutput(out var release,true)){s.Mapped=release;if(s.OutputReady)_=SubmitAsync(s,release);}Changed?.Invoke();}
 public void ClearSeen(int slot){lock(gate)Session(slot).Seen=GamepadButtons.None;Changed?.Invoke();}
 public bool PreviewRumble(int slot,out string error)=>Session(slot).Preview.Request(250,out error);
 public void FinishRumbleGesture(int slot)=>Session(slot).Preview.FinishGesture();
 public async Task<string> CalibrateAsync(int slot,string kind){
  var s=Session(slot);if(!s.Connected)return "连接手柄后可开始校准。";
  if(kind=="gyro")return s.Parser.StartManualGyroCalibration();
  var message=kind=="range"?s.Parser.StartManualStickRangeCalibration():s.Parser.StartManualStickCenterCalibration();Changed?.Invoke();
  await Task.Delay(kind=="range"?8000:2000,lifetime.Token);
  var result=kind=="range"?s.Parser.CompleteManualStickRangeCalibration():s.Parser.CompleteManualStickCenterCalibration();
  if(result.Success&&result.Profile!=null){Settings.SetStickCalibration(s.DeviceId,result.Profile);SaveSettings();}return message+"\n"+result.Message;
 }
 public ControllerSession Session(int index)=>Sessions[Math.Clamp(index,0,3)];
 public void Snapshot(Action<ControllerSession[]> action){lock(gate)action(Sessions);}
 void Handle(JsonElement e){
  try{
   var type=GetString(e,"event");var slot=e.TryGetProperty("slot",out var slotValue)?Math.Clamp(slotValue.GetInt32(),0,3):0;var s=Sessions[slot];
   switch(type){
    case "hostExited":
     pendingConnections.Clear();lock(gate)foreach(var lost in Sessions){Interlocked.Increment(ref lost.Generation);lost.Connected=false;lost.Busy=false;lost.SwitchingTransport=false;lost.OutputReady=false;lost.LastInput=0;lost.Rear.Reset();lost.Filter.Reset();lost.Haptics=new(true);lost.Physical=GamepadState.Neutral();lost.Mapped=GamepadState.Neutral();lost.Submitted=GamepadState.Neutral();lost.Status="通信服务已退出，点击连接可重新启动";_=lost.Preview.StopAsync(false);}
     Log("通信服务已退出，手柄输入和游戏输出已释放。");Changed?.Invoke();break;
    case "devices":
     var transport=GetString(e,"transport");foreach(var pair in Devices.Where(x=>x.Value.Slot==slot&&x.Value.Transport==transport).ToArray())Devices.TryRemove(pair.Key,out _);
     foreach(var d in e.GetProperty("devices").EnumerateArray()){var item=new ControllerDevice(GetString(d,"id"),GetString(d,"name"),GetString(d,"transport"),slot,d.TryGetProperty("rssi",out var rssi)&&rssi.TryGetInt32(out var r)?r:null,d.TryGetProperty("score",out var score)&&score.TryGetInt32(out var sc)?sc:null,GetString(d,"evidence"),GetString(d,"addressType"));Devices[item.Transport+":"+item.Id]=item;}
     if(transport=="usb")foreach(var candidateSession in Sessions)if(!Devices.Values.Any(d=>d.Transport=="usb"&&d.Id==candidateSession.FailedUsbDeviceId))candidateSession.FailedUsbDeviceId="";
     Changed?.Invoke();if(!Devices.IsEmpty)foreach(var pending in pendingConnections.ToArray()){
      var suitable=Devices.Values.Any(d=>(string.IsNullOrEmpty(pending.Value)||pending.Value==d.Id)&&(Settings.InputTransport==Pro2InputTransport.Auto||d.Transport==TransportKey(Settings.InputTransport))&&!Sessions.Any(other=>other.Index!=pending.Key&&(other.Connected||other.Busy)&&other.DeviceId==d.Id));
      if(suitable&&pendingConnections.TryRemove(pending.Key,out var requested))_=ConnectAsync(pending.Key,string.IsNullOrEmpty(requested)?null:requested);
     }if(transport=="usb")_=TryAutoSwitchUsbAsync();break;
    case "status":LastStatus=GetString(e,"message");s.Status=LastStatus;Log($"手柄 {slot+1} · {LastStatus}");break;
    case "error":LastError=GetString(e,"message");if(!s.SwitchingTransport||GetString(e,"code")=="usb_switch_failed")s.Busy=false;if(GetString(e,"code")=="usb_switch_failed"){s.SwitchingTransport=false;s.FailedUsbDeviceId=s.PendingUsbDeviceId;s.PendingUsbDeviceId="";}Log(LastError);Changed?.Invoke();break;
    case "transportChanged":
     if(!s.Enabled||!s.Connected||s.PendingUsbDeviceId!=GetString(e,"deviceId")||s.SwitchGeneration!=Interlocked.Read(ref s.Generation))break;
     s.Transport=GetString(e,"transport");s.DeviceId=GetString(e,"deviceId");s.Busy=false;s.SwitchingTransport=false;s.PendingUsbDeviceId="";s.LastInput=0;s.Filter.Reset();s.Haptics=new(true);s.Rear.Reset();s.Parser=new();s.Parser.SetStickCalibration(Settings.TryGetStickCalibration(s.DeviceId,out var switchedProfile)?switchedProfile:null);s.Physical=GamepadState.Neutral();s.Mapped=GamepadState.Neutral();s.Status="USB 已接管输入，虚拟手柄保持连接";Log(s.Status);Changed?.Invoke();break;
    case "transportSwitchFailed":
     if(s.PendingUsbDeviceId!=GetString(e,"deviceId")||s.SwitchGeneration!=Interlocked.Read(ref s.Generation))break;
     s.Busy=false;s.SwitchingTransport=false;s.FailedUsbDeviceId=s.PendingUsbDeviceId;s.PendingUsbDeviceId="";s.Connected=e.TryGetProperty("connected",out var retained)&&retained.GetBoolean();if(!s.Connected)s.OutputReady=false;s.LastInput=0;s.Status=GetString(e,"message");Log(s.Status);Changed?.Invoke();break;
    case "connection":
     if(!e.GetProperty("connected").GetBoolean()&&s.SwitchingTransport)s.FailedUsbDeviceId=s.PendingUsbDeviceId;
     var connected=e.GetProperty("connected").GetBoolean();Interlocked.Increment(ref s.Generation);s.Connected=connected;s.Busy=false;s.SwitchingTransport=false;s.Transport=GetString(e,"transport");s.DeviceId=GetString(e,"deviceId");
     s.Filter.Reset();s.Haptics=new(true);s.Parser=new();s.LastInput=0;
     if(connected&&!s.Enabled){_=DisconnectAsync(slot);break;}
     if(connected){s.Parser.SetStickCalibration(Settings.TryGetStickCalibration(s.DeviceId,out var profile)?profile:null);Settings.LastConnectedPro2Addresses[slot]=s.DeviceId;Settings.Save();_=CreateOutputAsync(s);}else{s.OutputReady=false;s.Rear.Reset();s.Physical=GamepadState.Neutral();s.Mapped=GamepadState.Neutral();s.Submitted=GamepadState.Neutral();}
     Changed?.Invoke();break;
    case "rawInput":ParseInput(s,GetString(e,"format"),Convert.FromBase64String(GetString(e,"base64")));break;
    case "actualOutput":
     s.ActualOutputAvailable=e.TryGetProperty("available",out var available)&&available.GetBoolean();
     if(s.ActualOutputAvailable)s.ActualOutput=JsonSerializer.Deserialize<GamepadState>(e.GetRawText(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??GamepadState.Neutral();
     else s.ActualOutput=GamepadState.Neutral();Changed?.Invoke();break;
    case "operationResult":Log(GetString(e,"message"));Changed?.Invoke();break;
    case "primary":
     if(s.Parser.TryParsePrimaryProPayload(Convert.FromBase64String(GetString(e,"base64")),out var battery,out _))lock(gate){s.Physical.BatteryLevel=battery.BatteryLevel;s.Physical.BatteryPercent=battery.BatteryPercent;s.Physical.BatteryCharging=battery.BatteryCharging;s.LastBattery=Stopwatch.GetTimestamp();}break;
    case "battery":
     lock(gate){
      if(e.TryGetProperty("level",out var level))s.Physical.BatteryLevel=level.GetByte();
      if(e.TryGetProperty("percent",out var percent))s.Physical.BatteryPercent=percent.GetByte();
      if(e.TryGetProperty("charging",out var charging))s.Physical.BatteryCharging=charging.GetBoolean();
      if(e.TryGetProperty("externalPower",out var power))s.Physical.BatteryExternalPower=power.GetBoolean();
      if(e.TryGetProperty("powerKnown",out var powerKnown))s.Physical.BatteryPowerKnown=powerKnown.GetBoolean();
      if(e.TryGetProperty("voltageMillivolts",out var voltage))s.Physical.BatteryVoltageMillivolts=voltage.GetUInt16();
      s.LastBattery=Stopwatch.GetTimestamp();
     }break;
    case "diagnostics":
     if(e.TryGetProperty("diagnostics",out var diagnostic)){s.Diagnostic=diagnostic.ToString();s.Firmware=GetString(diagnostic,"controllerFirmware");}break;
    case "factoryCalibration":
     var factoryBytes=Convert.FromBase64String(GetString(e,"base64"));s.FactoryCalibration=$"手柄闪存块 {GetString(e,"address")} · {factoryBytes.Length} 字节 · 来源为初始化读取";break;
    case "feedback":HandleFeedback(s,e);break;
   }
  }catch(Exception ex){Log("平台事件处理："+ex.Message);}
 }
 void ParseInput(ControllerSession s,string format,byte[] bytes){
  lock(gate){
   if(s.SwitchingTransport)return;
   if(format=="usb"&&(bytes.Length!=64||bytes[0]!=5))return;
   var payload=format=="usb"?bytes.AsSpan(1):bytes.AsSpan();
   if(!s.Parser.TryParseFd2Payload(payload,out var physical,out _))return;
   var now=Stopwatch.GetTimestamp();if(s.LastInput!=0){s.Gaps.Enqueue((now-s.LastInput)*1000d/Stopwatch.Frequency);while(s.Gaps.Count>1000)s.Gaps.Dequeue();}
   s.LastInput=now;s.Frames++;s.FrameTimes.Enqueue(now);s.Seen|=physical.Buttons;
   if(physical.BatteryLevel==GamepadState.BatteryUnknown)physical.BatteryLevel=s.Physical.BatteryLevel;
   if(physical.BatteryPercent==GamepadState.BatteryUnknown)physical.BatteryPercent=s.Physical.BatteryPercent;
   if(!Pro2UsbBatteryPolicy.IsIdle(physical))s.LastActivity=now;
   s.Physical=physical;
   var processing=StickProcessingOption.FromLabel(Settings.StickProcessingLabel);
   var source=processing.Mode==StickProcessingMode.StabilityGuard?s.Filter.Process(physical,now).AcceptedState:physical;
   var profile=Profile(Settings.SelectedModeKey);
   var face=new FaceButtonMapping(Settings.SwapABButtons,Settings.SwapXYButtons,Settings.ButtonRemapping).Apply(source,profile.Mode);
   s.Mapped=s.Rear.Apply(face,profile.Mode,Settings.RearButtons,now);
   if(s.OutputReady&&ViiperPushRateOption.FromLabel(Settings.PushRateLabel).Mode==ViiperPushRateMode.SourceAdaptive)_=SubmitAsync(s,s.Mapped);
  }
 }
 async Task SubmitAsync(ControllerSession s,GamepadState value){
  if(!s.OutputReady)return;
  try{
   await Bridge.SendAsync(new{command="submitOutput",slot=s.Index,state=new{buttons=(ulong)value.Buttons,lx=value.Lx,ly=value.Ly,rx=value.Rx,ry=value.Ry,l2=value.L2,r2=value.R2,accelX=value.AccelX,accelY=value.AccelY,accelZ=value.AccelZ,gyroX=value.GyroX,gyroY=value.GyroY,gyroZ=value.GyroZ,accelValid=value.AccelValid,gyroValid=value.GyroValid,batteryLevel=value.BatteryLevel,batteryCharging=value.BatteryCharging,motionTimestampUs=value.MotionTimestampUs}});
   s.Submitted=value;s.LastSubmit=Stopwatch.GetTimestamp();
  }catch(Exception ex){s.OutputReady=false;Log("游戏输出："+ex.Message);}
 }
 void HandleFeedback(ControllerSession s,JsonElement value){
  if(!value.TryGetProperty("events",out var events))return;
  foreach(var e in events.EnumerateArray()){
   if(e.TryGetProperty("wireFormat",out var wire)&&wire.GetString()=="viiper"&&e.TryGetProperty("rawBase64",out var raw)){
    var data=Convert.FromBase64String(raw.GetString()??"");
    if(Profile(Settings.SelectedModeKey).IsPs5Family){if(s.Haptics.TryProcess(data,out var haptic,out _,out _))s.Preview.WriteGame(haptic.Report,out _);}
    else if(Pro2OutputPacketMapper.TryMapFeedback(Profile(Settings.SelectedModeKey),data,out var mapped,out _))s.Preview.WriteGame(mapped.Report,out _);
    continue;
   }
   if(e.TryGetProperty("kind",out var kind)&&kind.GetInt32()!=0)continue;
   var weak=(byte)Math.Clamp((int)Math.Round((e.TryGetProperty("highRumble",out var high)?high.GetDouble():0)*255),0,255);
   var strong=(byte)Math.Clamp((int)Math.Round((e.TryGetProperty("lowRumble",out var low)?low.GetDouble():0)*255),0,255);
   var packet=Pro2OutputPacketMapper.BuildOrdinaryPacket(weak,strong,"native-feedback");s.Preview.WriteGame(packet.Report,out _);
  }
 }
 async Task PumpAsync(CancellationToken token){
  using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(4));long metricAt=0,feedbackAt=0;
  try{while(await timer.WaitForNextTickAsync(token)){
   var now=Stopwatch.GetTimestamp();
   if(Settings.InputTransport==Pro2InputTransport.Auto&&now-usbDiscoveryAt>Stopwatch.Frequency&&Sessions.Count(s=>s.Connected||s.Busy)==1&&Sessions.Any(s=>s.Connected&&!s.Busy&&s.Transport=="bluetooth")){
    usbDiscoveryAt=now;await Bridge.SendAsync(new{command="enumerate",slot=0,transport="usb",manual=false});
   }
   foreach(var s in Sessions){
    if(!s.Connected||s.SwitchingTransport)continue;
    if(s.Haptics.TryStopIdle(out var idle))s.Preview.WriteGame(idle.Report,out _);
    if(s.OutputReady){
     var age=s.LastInput==0?double.PositiveInfinity:(now-s.LastInput)/(double)Stopwatch.Frequency;
     var option=ViiperPushRateOption.FromLabel(Settings.PushRateLabel);
     if(option.Mode!=ViiperPushRateMode.SourceAdaptive&&(now-s.LastSubmit)*1000d/Stopwatch.Frequency>=option.Interval.TotalMilliseconds)await SubmitAsync(s,age<0.1?s.Mapped:GamepadState.Neutral());
     else if(age>=0.1&&(now-s.LastSubmit)*1000d/Stopwatch.Frequency>=100){s.Rear.Reset();await SubmitAsync(s,GamepadState.Neutral());}
     if(now-feedbackAt>Stopwatch.Frequency/125)await Bridge.SendAsync(new{command="readFeedback",slot=s.Index,limit=32});
    }
    if(Settings.RuntimeBatteryRefreshEnabled&&s.LastInput!=0&&(now-s.LastActivity)/(double)Stopwatch.Frequency>=5&&(now-s.LastBattery)/(double)Stopwatch.Frequency>=(s.Transport=="usb"?300:600)){s.LastBattery=now;await Bridge.SendAsync(new{command="refreshBattery",slot=s.Index});}
   }
   if(now-feedbackAt>Stopwatch.Frequency/125)feedbackAt=now;
   if(now-metricAt>=Stopwatch.Frequency/30){
    if(OperatingSystem.IsWindows()&&MonitorActualOutput)await Bridge.SendAsync(new{command="readActualOutput",slot=MonitoredSlot,index=Math.Clamp(ActualOutputIndex,0,3)});
    lock(gate)foreach(var s in Sessions){while(s.FrameTimes.Count>0&&now-s.FrameTimes.Peek()>Stopwatch.Frequency)s.FrameTimes.Dequeue();s.Hertz=s.FrameTimes.Count;var gaps=s.Gaps.Order().ToArray();if(gaps.Length>0){s.GapP50=gaps[(gaps.Length-1)/2];s.GapP95=gaps[(int)((gaps.Length-1)*0.95)];}}
    metricAt=now;Changed?.Invoke();
   }
  }}catch(OperationCanceledException){}catch(Exception ex){Log("共享输出调度："+ex.Message);}
 }
 public static string GetString(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var x)?x.ValueKind==JsonValueKind.String?x.GetString()??"":x.ToString():"";
 void Log(string value){LastStatus=value;Logged?.Invoke(value);}
 public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;lifetime.Cancel();try{await StopAsync();}catch{}if(pump!=null)try{await pump;}catch{}await Bridge.DisposeAsync();}
}
