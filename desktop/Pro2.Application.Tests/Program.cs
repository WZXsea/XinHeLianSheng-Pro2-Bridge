using System.Collections.Concurrent;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.Buffers.Binary;
using Pro2.Application;
using Y700Switch2V60Viiper;

internal static class Program {
 static int groups, assertions, failures;
 static readonly TimeSpan Deadline=TimeSpan.FromSeconds(15);
 static void Check(bool condition,string message){Interlocked.Increment(ref assertions);if(!condition)throw new InvalidOperationException(message);}
 static Dictionary<string,object?> Request(string command,params (string,object?)[] extra){var result=new Dictionary<string,object?>{{"command",command}};foreach(var(key,value)in extra)result[key]=value;return result;}
 static string Text(JsonElement value,string key)=>value.TryGetProperty(key,out var result)?result.ToString():"";
 static async Task Group(string name,Func<Task> test){try{await test();groups++;Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+error.Message);}}
 static async Task WaitUntil(Func<bool> condition,string description){var until=DateTime.UtcNow+Deadline;while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException(description);await Task.Delay(10);}}
 public static async Task<int> Main(string[] args){
  if(Environment.GetEnvironmentVariable("PRO2_APPLICATION_TEST_PEER")=="1")return await PolicyPeer();
  if(args.FirstOrDefault()=="--installer-peer")return await MacBackendInstallerTests.RunPeer(args[1],args[2],args[3]);
  if(args.FirstOrDefault()=="--installer-only")return await MacBackendInstallerTests.RunStandalone(args.Length>1?args[1]:"/tmp/pro2-libvirtualhid-macOS-universal-verified.dmg");
  if(args.Length>0)Environment.SetEnvironmentVariable("PRO2_NATIVE_HOST",Path.GetFullPath(args[0]));
  var host=PlatformBridge.ResolveHostPath();
  if(!File.Exists(host)){Console.Error.WriteLine("Build the real native host or set PRO2_NATIVE_HOST first: "+host);return 2;}
  Console.WriteLine("native_host="+host);
  await Group("shared service initialization, real backend status, self-test and complete EOF release",ServiceLifecycle);
  await Group("parallel first requests start one helper and preserve all request correlations",ColdParallelRequests);
  await Group("string-id unsolicited events preserve the numeric request reader",StringIDThenRequest);
  await Group("subscriber exceptions preserve the command-response channel",ThrowingSubscriber);
  await Group("all UI output modes report exact supported identity or explicit backend errors",ModeCapabilities);
  await Group("stop request stays reusable and shutdown response drains before process exit",StopAndShutdown);
  await Group("eight real helper restart cycles release sessions and pipe handles",RepeatedRestart);
  await Group("pending connections retain their original slot and disabled slots remain paused",PendingSlotPolicy);
  await Group("disconnect and output changes release rumble and discard prior input/haptic state",SessionResetPolicy);
  await Group("helper exit clears all shared session readiness before explicit restart",HostExitPolicy);
  await Group("USB handover preserves output, resolves failure and rejects ambiguity or late completion",UsbHandoverPolicy);
  Console.WriteLine($"Application/native IPC: {groups} passed groups, {failures} failed groups, {assertions} assertions.");
  return failures==0?0:1;
 }
 static async Task ServiceLifecycle(){
  var service=new ControllerService();
  // Exercise the real service without initiating discovery or changing saved settings.
  service.Settings.AutoReconnectOnStartupEnabled=false;
  var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  service.Bridge.EventReceived+=e=>{if(Text(e,"event")=="stopped")stopped.TrySetResult();};
  try{
   await service.InitializeAsync();
   Check(service.Bridge.IsRunning,"Initialize starts the real helper");
   Check(service.Sessions.Length==4&&service.Sessions.All(s=>!s.Connected&&!s.OutputReady),"four idle real-device slots");
   Check(!string.IsNullOrWhiteSpace(service.BackendStatus),"backend availability reaches service state");
   var ping=await service.Bridge.RequestAsync(Request("ping"));
   Check(Text(ping,"command")=="ping"&&ping.GetProperty("ok").GetBoolean(),"service ping correlation");
   var self=await service.Bridge.RequestAsync(Request("selfTest"));
   Check(self.GetProperty("passed").GetBoolean()&&self.GetProperty("logicalButtonsTested").GetInt32()==21,"native adapter tests execute through shared bridge");
   var scales=await service.Bridge.RequestAsync(Request("settings",("ps5GyroScalePitch",1.234),("ps5GyroScaleYaw",-1),("ps5GyroScaleRoll",6)));
   Check(scales.GetProperty("gyroScales").GetProperty("pitch").GetDouble()==1.23&&scales.GetProperty("gyroScales").GetProperty("yaw").GetDouble()==0.1&&scales.GetProperty("gyroScales").GetProperty("roll").GetDouble()==4,"shared numeric settings preserve original gyro normalization");
   var scaledSelf=await service.Bridge.RequestAsync(Request("selfTest"));
   Check(scaledSelf.GetProperty("groups").GetInt32()==9&&scaledSelf.GetProperty("gyroScales").GetProperty("roll").GetDouble()==4,"native state adapter verifies target gyro scaling exactly once");
   Check(scaledSelf.GetProperty("usbTakeoverPolicy").GetBoolean(),"native takeover guards reject late and cancelled callbacks");
   var missingUsb=await service.Bridge.RequestAsync(Request("switchToUsb",("slot",3),("deviceId","missing-target")));
   Check(Text(missingUsb,"code")=="usb_device_unavailable","native takeover preflight reports unavailable USB target");
   await service.StopAsync();
   Check(service.Sessions.All(s=>!s.Connected&&!s.OutputReady),"stop clears all slot/output states");
  }finally{await service.DisposeAsync();}
  await stopped.Task.WaitAsync(Deadline);
  Check(!service.Bridge.IsRunning,"disposed bridge reports stopped without accessing a closed Process");
 }
 static async Task ColdParallelRequests(){
  var bridge=new PlatformBridge();var ready=0;var observed=new ConcurrentBag<long>();
  bridge.EventReceived+=e=>{if(Text(e,"event")=="ready")Interlocked.Increment(ref ready);};
  var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  try{
   var tasks=Enumerable.Range(0,32).Select(i=>Task.Run(async()=>{await start.Task;var command=i%2==0?"ping":"selfTest";var response=await bridge.RequestAsync(Request(command),Deadline);Check(Text(response,"command")==command,"concurrent response command");observed.Add(response.GetProperty("requestId").GetInt64());})).ToArray();
   start.SetResult();await Task.WhenAll(tasks).WaitAsync(Deadline);
   await WaitUntil(()=>Volatile.Read(ref ready)>0,"helper ready event");
   Check(ready==1,"exactly one helper starts under concurrent first requests; observed "+ready);
   Check(observed.Count==32&&observed.Distinct().Count()==32,"all request IDs remain distinct and correlated");
  }finally{await bridge.DisposeAsync();}
 }
 static async Task StringIDThenRequest(){
  await using var bridge=new PlatformBridge();
  var seen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  bridge.EventReceived+=e=>{if(e.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String&&id.GetString()=="external-string")seen.TrySetResult();};
  await bridge.SendAsync(new{command="ping",id="external-string"});
  await seen.Task.WaitAsync(Deadline);
  var result=await bridge.RequestAsync(Request("ping"),Deadline);
  Check(result.GetProperty("ok").GetBoolean(),"numeric request after string event succeeds");
 }
 static async Task ThrowingSubscriber(){
  await using var bridge=new PlatformBridge();
  bridge.EventReceived+=_=>throw new InvalidOperationException("intentional event observer failure");
  await bridge.StartAsync();
  var result=await bridge.RequestAsync(Request("ping"),Deadline);
  Check(result.GetProperty("ok").GetBoolean(),"observer exception does not terminate stdout reader");
 }
 static async Task ModeCapabilities(){
  await using var bridge=new PlatformBridge();
  var backend=await bridge.RequestAsync(Request("backendStatus"));
  Check(Text(backend,"event")=="backendStatus","backend status event contract");
  Check(backend.GetProperty("pro2").GetProperty("available").GetBoolean()==false,"actual broker advertises Pro 2 implementation boundary");
  foreach(var mode in new[]{"pro2","pro1","xbox","dualsense","xboxproimu","dualsenseproimu"}){
   var result=await bridge.RequestAsync(Request("createOutput",("slot",0),("mode",mode),("backend","broker")),Deadline);
   var error=Text(result,"event")=="error";
   if(mode is "pro2" or "xboxproimu" or "dualsenseproimu")Check(error&&Text(result,"code")=="profile_unsupported","unsupported identity is explicit: "+mode);
   else if(error)Check(Text(result,"code").StartsWith("broker_")||Text(result,"code").StartsWith("backend_"),"supported identity reports actual service/license failure: "+mode);
   else Check(result.GetProperty("ok").GetBoolean()&&Text(result,"mode")==mode,"created output preserves exact mode: "+mode);
   await bridge.RequestAsync(Request("destroyOutput",("slot",0)));
  }
  var invalid=await bridge.RequestAsync(Request("connect",("slot",4),("deviceId","test")));
  Check(Text(invalid,"code")=="invalid_slot","invalid slot errors return correlated response");
  var next=await bridge.RequestAsync(Request("ping"));Check(next.GetProperty("ok").GetBoolean(),"channel remains usable after capability/errors");
 }
 static async Task StopAndShutdown(){
  var bridge=new PlatformBridge();var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  bridge.EventReceived+=e=>{if(Text(e,"event")=="stopped")stopped.TrySetResult();};
  try{
   var stop=await bridge.RequestAsync(Request("stop"));Check(stop.GetProperty("ok").GetBoolean(),"stop acknowledges");
   var ping=await bridge.RequestAsync(Request("ping"));Check(ping.GetProperty("ok").GetBoolean(),"stop retains helper for further commands");
   var shutdown=await bridge.RequestAsync(Request("shutdown"));Check(shutdown.GetProperty("ok").GetBoolean(),"final response drains before exit failure handler");
   await stopped.Task.WaitAsync(Deadline);
  }finally{await bridge.DisposeAsync();}
 }

 static async Task RepeatedRestart(){
  await using var bridge=new PlatformBridge();int exited=0,ready=0;
  bridge.EventReceived+=e=>{if(Text(e,"event")=="hostExited")Interlocked.Increment(ref exited);if(Text(e,"event")=="ready")Interlocked.Increment(ref ready);};
  var before=Directory.Exists("/dev/fd")?Directory.GetFileSystemEntries("/dev/fd").Length:0;
  for(var iteration=0;iteration<8;iteration++){
   var responses=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>bridge.RequestAsync(Request("ping"),Deadline)));
   Check(responses.All(r=>r.GetProperty("ok").GetBoolean()),"live requests after restart "+iteration);
   var exit=await bridge.RequestAsync(Request("shutdown"));Check(exit.GetProperty("ok").GetBoolean(),"final shutdown reply "+iteration);
   var expected=iteration+1;
   await WaitUntil(()=>Volatile.Read(ref exited)==expected,"host exit event "+iteration);
   Check(!bridge.IsRunning,"helper reports stopped after cycle "+iteration);
  }
  Check(ready==8&&exited==8,"one startup/exit pair per real process");
  var sessionList=typeof(PlatformBridge).GetField("sessions",BindingFlags.Instance|BindingFlags.NonPublic)!;
  Check(((System.Collections.ICollection)sessionList.GetValue(bridge)!).Count==0,"exited process sessions are removed before bridge disposal");
  if(before>0)await WaitUntil(()=>Directory.GetFileSystemEntries("/dev/fd").Length<=before+4,"pipe descriptor cleanup after eight restarts");
 }

 static async Task WithPolicyPeer(Func<ControllerService,ConcurrentQueue<JsonElement>,Task> test){
  var originalHost=Environment.GetEnvironmentVariable("PRO2_NATIVE_HOST");
  var originalPeer=Environment.GetEnvironmentVariable("PRO2_APPLICATION_TEST_PEER");
  Environment.SetEnvironmentVariable("PRO2_NATIVE_HOST",Environment.ProcessPath!);
  Environment.SetEnvironmentVariable("PRO2_APPLICATION_TEST_PEER","1");
  var service=new ControllerService();var commands=new ConcurrentQueue<JsonElement>();
  service.Settings.AutoReconnectOnStartupEnabled=false;service.Settings.InputTransport=Pro2InputTransport.Usb;
  service.Bridge.EventReceived+=e=>{if(Text(e,"event")=="testCommand")commands.Enqueue(e.GetProperty("payload").Clone());};
  try{await service.Bridge.StartAsync();await test(service,commands);}
  finally{
   await service.DisposeAsync();
   Environment.SetEnvironmentVariable("PRO2_NATIVE_HOST",originalHost);
   Environment.SetEnvironmentVariable("PRO2_APPLICATION_TEST_PEER",originalPeer);
  }
 }
 static void Inject(ControllerService service,object value){
  using var document=JsonDocument.Parse(JsonSerializer.Serialize(value));
  typeof(ControllerService).GetMethod("Handle",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,[document.RootElement]);
 }
 static async Task PendingSlotPolicy()=>await WithPolicyPeer(async(service,commands)=>{
  await service.ConnectAsync(2);
  var pending=(ConcurrentDictionary<int,string>)typeof(ControllerService).GetField("pendingConnections",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
  Check(pending.ContainsKey(2)&&!pending.ContainsKey(0),"missing-device request retains slot 2");
  Inject(service,new{@event="devices",slot=0,transport="usb",devices=new[]{new{id="policy-controller",name="Policy fixture",transport="usb"}}});
  await WaitUntil(()=>commands.Any(e=>Text(e,"command")=="connect"),"pending target sends connect command");
  var request=commands.Last(e=>Text(e,"command")=="connect");
  Check(request.GetProperty("slot").GetInt32()==2,"discovery retries original target slot");
  Check(service.Session(0).DeviceId==""&&service.Session(2).DeviceId=="policy-controller","other slots retain their state");
  await service.SetSlotEnabledAsync(2,false);
  Check(!service.Session(2).Enabled&&!service.Session(2).Connected&&!service.Session(2).Busy&&!service.Session(2).OutputReady,"disabled slot fully pauses");
  Check(!pending.ContainsKey(2),"disabling cancels pending connect ownership");
  var count=commands.Count(e=>Text(e,"command")=="connect");
  await service.ConnectAsync(2);await service.Bridge.RequestAsync(Request("ping"));
  Check(commands.Count(e=>Text(e,"command")=="connect")==count,"disabled slot does not issue reconnect");
  await service.SetSlotEnabledAsync(2,true);Check(service.Session(2).Enabled,"explicit enable restores availability");
  await service.DisconnectAsync(2);service.Devices.Clear();
  count=commands.Count(e=>Text(e,"command")=="connect");
  await service.ConnectAsync(1,"explicit-selection");
  Inject(service,new{@event="devices",slot=0,transport="usb",devices=new[]{new{id="another-controller",name="Other fixture",transport="usb"}}});
  await service.Bridge.RequestAsync(Request("ping"));
  Check(commands.Count(e=>Text(e,"command")=="connect")==count,"manual selection waits for the selected controller");
  Check(pending.TryGetValue(1,out var selected)&&selected=="explicit-selection","unrelated discovery retains selected device ownership");
  Inject(service,new{@event="devices",slot=0,transport="usb",devices=new[]{new{id="explicit-selection",name="Selected fixture",transport="usb"}}});
  await WaitUntil(()=>commands.Any(e=>Text(e,"command")=="connect"&&Text(e,"deviceId")=="explicit-selection"),"selected device becomes discoverable");
  Check(commands.Last(e=>Text(e,"command")=="connect").GetProperty("slot").GetInt32()==1,"manual selection retains the chosen slot");
 });
 static async Task SessionResetPolicy()=>await WithPolicyPeer(async(service,commands)=>{
  var slot=service.Session(1);var reports=new List<byte[]>();slot.Connected=true;slot.OutputReady=true;
  slot.Physical=new(){Buttons=GamepadButtons.South};slot.Mapped=slot.Physical.Clone();slot.Submitted=slot.Physical.Clone();slot.LastInput=123;
  slot.Preview=new RumblePreviewMixer(()=>slot.Connected,(byte[] report,out string error)=>{error="";reports.Add(report.ToArray());return true;});
  var now=Stopwatch.GetTimestamp();slot.Filter.Process(GamepadState.Neutral(),now);
  var spike=GamepadState.Neutral();spike.Lx=4095;
  Check(slot.Filter.Process(spike,now+Stopwatch.Frequency/250).HasHoldOrReject,"fixture has a held previous-session spike");
  var frame=new byte[388];frame[0]=2;BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1),384);
  slot.Haptics.TryProcess(frame,out _,out _,out _);Check(slot.Haptics.HasNativeAudio,"fixture has previous-session audio history");
  slot.Preview.WriteGame(Pro2OutputPacketMapper.BuildOrdinaryPacket(100,150,"test-active").Report,out _);reports.Clear();
  await service.DisconnectAsync(1);
  Check(reports.Count>0&&reports.All(IsNeutralRumble),"disconnect explicitly releases physical rumble");
  Check(!slot.Connected&&!slot.OutputReady&&slot.LastInput==0&&slot.Physical.Buttons==0&&slot.Mapped.Buttons==0&&slot.Submitted.Buttons==0,"disconnect clears input/output readiness and held state");
  Check(!slot.Haptics.HasNativeAudio&&!slot.Haptics.NativeOutputActive,"disconnect clears haptic scheduling history");
  Check(slot.Filter.Process(spike,now+Stopwatch.Frequency/125).AcceptedState.Lx==4095,"first sample of next connection follows new physical state");
  slot.Connected=true;slot.OutputReady=true;service.Settings.SelectedModeKey="pro2";
  slot.Haptics.TryProcess(frame,out _,out _,out _);reports.Clear();
  await (Task)typeof(ControllerService).GetMethod("CreateOutputAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,[slot])!;
  Check(reports.Count>0&&reports.All(IsNeutralRumble),"output reconfiguration releases previous rumble");
  Check(!slot.OutputReady&&!slot.Haptics.HasNativeAudio,"failed replacement output cannot retain previous readiness/history");
  slot.Connected=false; // policy fixture never represents a hardware connection
 });
 static bool IsNeutralRumble(byte[] report){
  var encoded=new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
  return Pro2BleRumblePacketEncoder.TryEncodeRaw02(report,0,encoded,out var active,out _)&&!active;
 }
 static async Task UsbHandoverPolicy()=>await WithPolicyPeer(async(service,commands)=>{
  service.Settings.InputTransport=Pro2InputTransport.Auto;
  var slot=service.Session(1);slot.Connected=true;slot.Transport="bluetooth";slot.DeviceId="ble-fixture";slot.OutputReady=true;
  var rumble=new List<byte[]>();slot.Preview=new RumblePreviewMixer(()=>slot.Connected,(byte[] report,out string error)=>{error="";rumble.Add(report.ToArray());return true;});
  async Task TrySwitch()=>await(Task)typeof(ControllerService).GetMethod("TryAutoSwitchUsbAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,null)!;
  int Switches()=>commands.Count(e=>Text(e,"command")=="switchToUsb");
  service.Devices["usb:first"]=new("usb-first","First USB","usb");service.Devices["usb:second"]=new("usb-second","Second USB","usb");
  await TrySwitch();Check(Switches()==0,"multiple USB candidates retain manual selection");
  service.Devices.TryRemove("usb:second",out _);await TrySwitch();await service.Bridge.RequestAsync(Request("ping"));
  Check(Switches()==1&&slot.SwitchingTransport&&slot.Busy,"one BLE and one USB start one handover");
  Check(slot.OutputReady&&slot.PendingUsbDeviceId=="usb-first","handover retains existing output ownership");
  Check(rumble.Count>0&&rumble.All(IsNeutralRumble),"handover begins with physical rumble neutral");
  var switched=commands.Last(e=>Text(e,"command")=="switchToUsb");Check(switched.GetProperty("slot").GetInt32()==1&&Text(switched,"deviceId")=="usb-first","handover addresses the same occupied slot");
  Inject(service,new{@event="transportChanged",slot=1,transport="usb",deviceId="usb-first"});
  Check(slot.Connected&&slot.OutputReady&&!slot.Busy&&!slot.SwitchingTransport&&slot.Transport=="usb","USB readiness retains the virtual device");
  Check(!commands.Any(e=>Text(e,"command")=="createOutput"),"successful handover leaves output instance intact");
  slot.Transport="bluetooth";slot.DeviceId="ble-fixture";await TrySwitch();
  Inject(service,new{@event="transportSwitchFailed",slot=1,deviceId="usb-first",connected=true,message="BLE retained"});
  Check(slot.Connected&&slot.OutputReady&&!slot.Busy&&!slot.SwitchingTransport,"failed switch keeps verified BLE and output available");
  int failedCount=Switches();await TrySwitch();await service.Bridge.RequestAsync(Request("ping"));
  Check(Switches()==failedCount,"a failed USB path stays paused until removal or manual reconnection");
  Inject(service,new{@event="devices",slot=0,transport="usb",devices=Array.Empty<object>()});
  Check(slot.FailedUsbDeviceId=="","USB removal clears the failed path for a new insertion");
  service.Devices["usb:first"]=new("usb-first","First USB","usb");
  service.Session(2).Connected=true;service.Session(2).Transport="bluetooth";service.Session(2).DeviceId="other-ble";int before=Switches();await TrySwitch();
  Check(Switches()==before,"multiple physical sessions retain manual transport choice");service.Session(2).Connected=false;
  await TrySwitch();await service.DisconnectAsync(1);
  Inject(service,new{@event="transportChanged",slot=1,transport="usb",deviceId="usb-first"});
  Check(!slot.Connected&&!slot.OutputReady,"completion after stop cannot restore an old session");
 });
 static async Task HostExitPolicy()=>await WithPolicyPeer(async(service,commands)=>{
  foreach(var slot in service.Sessions){slot.Connected=true;slot.Busy=true;slot.OutputReady=true;slot.LastInput=123;slot.Physical=new(){Buttons=GamepadButtons.East};slot.Mapped=slot.Physical.Clone();slot.Submitted=slot.Physical.Clone();}
  var shutdown=await service.Bridge.RequestAsync(Request("shutdown"));Check(shutdown.GetProperty("ok").GetBoolean(),"peer final response precedes session reset");
  await WaitUntil(()=>service.Sessions.All(slot=>!slot.Connected&&!slot.Busy&&!slot.OutputReady),"hostExited resets every shared slot");
  Check(service.Sessions.All(slot=>slot.LastInput==0&&slot.Physical.Buttons==0&&slot.Mapped.Buttons==0&&slot.Submitted.Buttons==0),"exited helper releases stale input and submitted holds");
  Check(service.Sessions.All(slot=>slot.Generation>0),"exited helper invalidates outstanding output ownership");
  Check(!service.Bridge.IsRunning,"host exit stays stopped until an explicit request");
  var restarted=await service.Bridge.RequestAsync(Request("ping"));Check(restarted.GetProperty("ok").GetBoolean(),"same bridge can restart helper explicitly");
  Check(service.Sessions.All(slot=>!slot.Connected&&!slot.OutputReady),"helper restart retains honest physical/output readiness");
 });
 static async Task<int> PolicyPeer(){
  async Task Emit(object value){await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));await Console.Out.FlushAsync();}
  await Emit(new{@event="ready",protocolVersion=1});
  while(await Console.In.ReadLineAsync()is{}line){
   using var document=JsonDocument.Parse(line);var request=document.RootElement;var command=Text(request,"command");
   await Emit(new{@event="testCommand",payload=request});
   var response=new Dictionary<string,object?>{{"event","commandResult"},{"command",command},{"ok",true}};
   if(request.TryGetProperty("requestId",out var id))response["requestId"]=id.Clone();
   if(command=="createOutput"){response["event"]="error";response["code"]="profile_unsupported";response["message"]="Policy peer explicit output failure";}
   await Emit(response);
   if(command=="enumerate")await Emit(new{@event="devices",slot=0,transport="usb",devices=Array.Empty<object>()});
   if(command=="shutdown")break;
  }
  await Emit(new{@event="stopped"});return 0;
 }
}
