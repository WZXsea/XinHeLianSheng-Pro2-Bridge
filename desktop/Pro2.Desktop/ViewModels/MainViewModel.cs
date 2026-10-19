using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pro2.Application;
using Pro2.Desktop.Views;
using Y700Switch2V60Viiper;
namespace Pro2.Desktop.ViewModels;

public sealed partial class MainViewModel : UiObservable {
 public ControllerService Service {get;}=new();
 public ObservableCollection<ControllerDevice> Devices {get;}=[];
 public ObservableCollection<ConfigurationKeyRow> ConfigurationKeys {get;}=[];
 public ObservableCollection<TargetChoice> QuickTargets {get;}=[];
 public ObservableCollection<SlotRow> Pro2Slots {get;}=[];
 readonly List<string> logs=[];
 readonly Dictionary<int,GamepadButtons> draftChords=[];
 readonly Dictionary<string,(ButtonRemap[],RearButtonProfile,bool,bool)> drafts=[];
 RearActionDraft[] rearEditors=[new(),new(),new()];
 bool draftSwapAB,draftSwapXY;
 int uiScheduled,selectedPage,selectedControl,selectedSlot;
 string configurationMode="pro2",configurationStatus="配置按输出模式独立保存",calibrationStatus="",testStatus="";
 bool keyEditor,advancedEditor;
 ControllerDevice? selectedDevice;
 GamepadState state=GamepadState.Neutral(),outputState=GamepadState.Neutral(),mappedState=GamepadState.Neutral();
 GamepadButtons seen;
 bool closed,installingBackend;
 string overrideStatus="";
 Window? Owner=>(Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
 ControllerSession Current=>Service.Session(selectedSlot);
 V60UserSettings Settings=>Service.Settings;
 public MainViewModel(bool initialize=true){
  for(int i=0;i<21;i++)ConfigurationKeys.Add(new ConfigurationKeyRow{Index=i,Label=ViewChoices.PhysicalNames[i]});
  for(int i=0;i<4;i++){var slot=i;Pro2Slots.Add(new SlotRow(i+1){EnabledChanged=enabled=>Run(()=>Service.SetSlotEnabledAsync(slot,enabled))});}
  Service.Logged+=value=>Dispatcher.UIThread.Post(()=>{logs.Add($"{DateTime.Now:HH:mm:ss} {value}");if(logs.Count>500)logs.RemoveRange(0,logs.Count-500);Changed(nameof(LogText));});
  Service.Changed+=()=>{if(Interlocked.Exchange(ref uiScheduled,1)==0)Dispatcher.UIThread.Post(()=>{Interlocked.Exchange(ref uiScheduled,0);Refresh();});};
  ConnectPro2InputCommand=Async(_=>Service.ConnectAsync(selectedSlot,SelectedDevice?.Id));
  CompactStopCommand=Async(_=>Service.StopAsync());CancelConnectionCommand=CompactStopCommand;DisconnectPro2InputCommand=Async(_=>Service.DisconnectAsync(selectedSlot));
  ConnectPro2SlotCommand=Async(p=>Service.ConnectAsync(SlotParameter(p),SelectedDevice?.Id));DisconnectPro2SlotCommand=Async(p=>Service.DisconnectAsync(SlotParameter(p)));
  SelectCompactModeCommand=Async(p=>Service.SelectOutputAsync(p?.ToString()??"pro2"));
  OpenConfigurationCommand=Command(_=>SelectedPage=2);
  OpenManualConnectCommand=Async(async _=>{await Service.SearchAsync(true);if(Owner is{}owner)await new ManualConnectWindow{DataContext=this}.ShowDialog(owner);});
  OpenEnvironmentCommand=Async(async _=>{await Service.InspectBackendAsync();if(Owner is{}owner)await new EnvironmentInfoWindow{DataContext=this}.ShowDialog(owner);});
  PreviewRumbleCommand=Command(_=>{if(!Service.PreviewRumble(selectedSlot,out var error))SetStatus(error);});
  CalibrateStickCenterSlotCommand=Async(async _=>{calibrationStatus="请松开摇杆，正在采样零位 2 秒";Changed(nameof(CalibrationStatus));SetStatus(await Service.CalibrateAsync(selectedSlot,"center"));});
  CalibrateStickRangeSlotCommand=Async(async _=>{calibrationStatus="请沿外圈旋转两个摇杆，正在采样行程 8 秒";Changed(nameof(CalibrationStatus));SetStatus(await Service.CalibrateAsync(selectedSlot,"range"));});
  CalibrateSourceGyroCommand=Async(async _=>SetStatus(await Service.CalibrateAsync(selectedSlot,"gyro")));
  SaveConfigurationCommand=Command(_=>SaveConfiguration());ResetConfigurationCommand=Command(_=>ResetConfiguration());CancelRearButtonsCommand=Command(_=>{Service.CancelActions();configurationStatus="当前背键动作已释放";Changed(null);});
  SelectControlCommand=Command(p=>{if(int.TryParse(p?.ToString(),out var i)){SelectedControl=i;IsKeyEditorOpen=true;}});
  ChooseTargetCommand=Command(p=>{if(p is ChooseOutputRequest choice)ChooseTarget(choice);});CloseKeyEditorCommand=Command(_=>IsKeyEditorOpen=false);
  RestoreSelectedControlCommand=Command(_=>RestoreControl());DisableSelectedControlCommand=Command(_=>DisableControl());ClearSeenCommand=Command(_=>Service.ClearSeen(selectedSlot));
  LetterLayoutCommand=Command(_=>SetLetterLayout(true));PositionLayoutCommand=Command(_=>SetLetterLayout(false));
  ClearLogCommand=Command(_=>{logs.Clear();Changed(nameof(LogText));});ExportDiagnosticsLogCommand=Async(_=>ExportAsync());
  CopyDiagnosisCommand=Async(async _=>{if(Owner?.Clipboard is{}clipboard)await clipboard.SetTextAsync(DiagnosticsText);});
  RunWin10BleDiagnosticCommand=Async(async _=>{var count=Current.Frames;testStatus="BLE 实测采样中 · 15 秒";Changed(null);await Task.Delay(15000);testStatus=$"15 秒实测 · {Current.Frames-count} 帧 · {(Current.Frames-count)/15d:F1} Hz";Changed(null);});
  RestartBluetoothCommand=Command(_=>Open(OperatingSystem.IsMacOS()?"x-apple.systempreferences:com.apple.BluetoothSettings":"ms-settings:bluetooth"));
  InstallUsbipCommand=Async(async _=>{if(OperatingSystem.IsWindows())await Service.Bridge.SendAsync(new{command="installDriver"});else await InstallMacBackendAsync();});
  SelectBackendPathCommand=Async(_=>SelectBackendPathAsync());ResetUsbipPathCommand=Async(async _=>{Settings.UsbipExecutablePath="";Service.SaveSettings();await Service.InspectBackendAsync();});
  CleanupStaleVirtualDevicesCommand=Async(async _=>{foreach(var s in Service.Sessions)if(!s.Connected)await Service.Bridge.SendAsync(new{command="destroyOutput",slot=s.Index});await Service.InspectBackendAsync();});
  RefreshSteamCacheCommand=Async(_=>NativeCommand("refreshSteamCache"));OptimizeBluetoothCommand=Async(_=>NativeCommand("optimizeBluetooth"));
  ConfigureBluetoothCommand=Async(_=>NativeCommand("configureBluetooth"));RestoreBluetoothCommand=Async(_=>NativeCommand("restoreBluetooth"));
  ToggleWindowsBluetoothPanelCommand=Command(_=>{ShowWin10BleHomePanel=!ShowWin10BleHomePanel;Changed(nameof(ShowWin10BleHomePanel));});
  OpenBackendLicenseCommand=Command(_=>Open("https://docs.lizardbyte.dev/projects/libvirtualhid/master/md_docs_2macos-gamepad.html"));
  configurationMode=Settings.SelectedModeKey is "xboxproimu"?"xbox":Settings.SelectedModeKey is "dualsenseproimu"?"dualsense":Settings.SelectedModeKey;
  LoadDraft();if(initialize)Dispatcher.UIThread.Post(()=>Run(()=>Service.InitializeAsync()));
 }
 UiCommand Command(Action<object?> action)=>new(action);
 UiCommand Async(Func<object?,Task> action)=>new(p=>Run(()=>action(p)));
 async void Run(Func<Task> action){try{await action();}catch(Exception ex){SetStatus(ex.Message);} }
 void SetStatus(string value){overrideStatus=value;calibrationStatus=value;Changed(null);}
 int SlotParameter(object? p)=>Math.Clamp((int.TryParse(p?.ToString(),out var n)?n:1)-1,0,3);
 async Task NativeCommand(string command){
  if(OperatingSystem.IsWindows()){
   if(command=="configureBluetooth"){
    var preset=SelectedWin10BlePreset.Title=="兼容预设"?"flexible":SelectedWin10BlePreset.Title=="保守预设"?"15":"7p5";
    var preview=await Service.Bridge.RequestAsync(new(){{"command",command},{"slot",selectedSlot},{"preset",preset},{"confirmed",false}});
    var message=ControllerService.GetString(preview,"message");
    if(ControllerService.GetString(preview,"code")!="confirmation_required"){SetStatus(message);return;}
    if(!await ConfirmPlatformChangeAsync("应用 Windows 蓝牙参数",message))return;
    await Service.Bridge.SendAsync(new{command,slot=selectedSlot,preset,confirmed=true,experimentalConsent=false});
   }else{
    if(command=="refreshSteamCache"&&!await ConfirmPlatformChangeAsync("刷新 Steam 控制器缓存","此操作将退出并重新启动 Steam。请先保存游戏进度并结束当前游戏。"))return;
    await Service.Bridge.SendAsync(new{command,slot=selectedSlot});
   }
  }else if(command=="optimizeBluetooth"){Settings.AutoOptimizeBle=true;Service.SaveSettings();SetStatus("本次采用手柄报告率协商；连接阶段与实测速率见诊断。");}
  else SetStatus("当前系统的对应设置通过蓝牙与驱动设置管理。");
 }
 async Task<bool> ConfirmPlatformChangeAsync(string title,string message){
  if(Owner is not{}owner)return false;
  var dialog=new Window{Title=title,Width=480,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
  var apply=new Button{Content="确认并继续"};var cancel=new Button{Content="取消"};
  apply.Click+=(_,_)=>dialog.Close(true);cancel.Click+=(_,_)=>dialog.Close(false);
  dialog.Content=new StackPanel{Margin=new Thickness(24),Spacing=16,Children={new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap},new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right,Children={cancel,apply}}}};
  return await dialog.ShowDialog<bool>(owner);
 }
 async Task InstallMacBackendAsync(){
  if(installingBackend)return;installingBackend=true;Changed(nameof(CanManageRuntime));
  try{
   SetStatus("正在获取官方虚拟手柄安装包…");
   var path=await new MacBackendInstaller().DownloadAsync(new Progress<double>(progress=>SetStatus($"官方虚拟手柄安装包 · {progress:P0}")));
   Open(path);SetStatus("校验已通过，官方安装包已打开。请运行其中的安装程序，并在 Virtual HID Control 中完成机器许可证激活。");
  }finally{installingBackend=false;Changed(nameof(CanManageRuntime));}
 }
 public async Task ShutdownAsync(){if(closed)return;closed=true;try{await Service.DisposeAsync();}catch{} }
 public int SelectedPage{get=>selectedPage;set{if(Set(ref selectedPage,value))Service.MonitorActualOutput=value==2;}}
 public int SelectedSlot{get=>selectedSlot;set{if(Set(ref selectedSlot,Math.Clamp(value,0,3))){Service.MonitoredSlot=selectedSlot;Refresh();}}}
 public ControllerDevice? SelectedDevice{get=>selectedDevice;set=>Set(ref selectedDevice,value);}
 public GamepadState State=>state;
 public GamepadState OutputState=>outputState;
 public GamepadState MappedState=>mappedState;
 public GamepadButtons SeenButtons=>seen;
 public string Status=>string.IsNullOrEmpty(overrideStatus)?Service.LastStatus:overrideStatus;
 public bool IsConnected=>Current.Connected;
 public bool Busy=>Current.Busy;
 public bool IsBusy=>Busy;
 public bool CanOperate=>!Busy;
 public bool CanManageRuntime=>!Busy&&!installingBackend;
 public bool CompactCanConnect=>!Busy&&!Current.Connected;
 public bool CompactCanStop=>Service.Sessions.Any(s=>s.Connected||s.Busy);
 public bool CanPreviewRumble=>IsConnected;
 public string CompactConnectText=>IsConnected?"已连接":Busy?"连接中…":"连接 / 进入游戏";
 public string ActiveInputTransportText=>IsConnected?Current.Transport=="usb"?"USB 有线输入":"蓝牙无线输入":Busy?"正在连接":"等待 NS2 PRO";
 public string ActiveInputTransportHint=>Current.Status;
 public string CompactStateText=>Current.OutputReady?"游戏输出已就绪":IsConnected?"真实输入已就绪":Busy?"连接处理中":"待连接";
 public string BleConnectionTitle=>Current.OutputReady?"已连接，开始游戏吧":IsConnected?"手柄已连接":Busy?"正在寻找你的 Pro2":"让 Pro2，成为你的 PC 手柄";
 public string HomeConnectionHint=>IsConnected?Current.OutputReady?"切换兼容模式无需重新配对手柄。":Current.Status:"接入 USB 数据线，或长按手柄顶部配对键，再点击连接。";
 public string CompactInputRate=>Current.Hertz>0?Current.Hertz.ToString("F0"):"—";
 public string HomeInputSummary=>$"有效输入 {Current.Frames:N0} 帧";
 public string HomeOutputLabel=>Current.OutputReady?ControllerService.Profile(Settings.SelectedModeKey).Label:"尚未启动";
 public string CompactHeadset=>state.HeadsetPresenceKnown?state.HeadsetPresent?"已插入":"空闲":"读取中";
 public string HomeBatteryStatus=>state.BatteryCharging?"正在充电":state.BatteryExternalPower?"外接电源":"电池供电";
 public BatteryVisual CompactBatteryVisual=>new(state);
 public string CompactBatteryHint=>state.BatteryLevel==255?"电量档位等待原生电量快照":$"手柄原生 {state.BatteryLevel}/9 档；电压 {state.BatteryVoltageMillivolts} mV";
 public bool IsPro2Selected=>Settings.SelectedModeKey=="pro2";
 public bool IsPro1Selected=>Settings.SelectedModeKey=="pro1";
 public bool IsXboxSelected=>Settings.SelectedModeKey.StartsWith("xbox");
 public bool CompactPs5Selected=>Settings.SelectedModeKey.StartsWith("dualsense");
 public string HomeModeDescription=>Settings.SelectedModeKey switch{"xbox"=>"Xbox / XInput · 标准布局与游戏震动反馈","dualsense"=>"PS5 兼容模式 · 体感与触觉转换","pro1"=>"Pro1 / Switch Pro · Nintendo 布局与体感输入",_=>"Steam / Pro2 · NS2 Pro 原生布局、体感与背键"};
 public string HomeSessionSummary=>$"{ActiveInputTransportText} · {CompactStateText} · {Current.Frames:N0} 帧";
 public double RumblePercent{get=>Settings.RumbleMultiplier*100;set{Settings.RumbleMultiplier=Math.Round(Math.Clamp(value,0,300)/10)/10;Service.SaveSettings();Changed(nameof(RumblePercentText));Changed();}}
 public string RumblePercentText=>$"{RumblePercent:F0}%";
 public string RumblePreviewHint=>"100% = 软件 1 倍增益；拖动滑块或点击试振，可试听当前强度。";
 public string CompactHapticSource=>Current.OutputReady?ControllerService.Profile(Settings.SelectedModeKey).Label+" 游戏反馈":"等待游戏输出反馈";
 public void RumbleGestureChanged(){if(IsConnected)Service.PreviewRumble(selectedSlot,out _);}
 public void FinishRumbleGesture()=>Service.FinishRumbleGesture(selectedSlot);
 public IReadOnlyList<string> InputTransportOptions{get;}=["自动（USB 优先）","仅 USB 有线","仅蓝牙无线"];
 public string SelectedInputTransport{get=>InputTransportOptions[Settings.InputTransport==Pro2InputTransport.Usb?1:Settings.InputTransport==Pro2InputTransport.Bluetooth?2:0];set{Settings.InputTransport=value==InputTransportOptions[1]?Pro2InputTransport.Usb:value==InputTransportOptions[2]?Pro2InputTransport.Bluetooth:Pro2InputTransport.Auto;Service.SaveSettings();Changed();}}
 public bool LaunchAtLoginEnabled{get=>Settings.LaunchAtLoginEnabled;set{Settings.LaunchAtLoginEnabled=value;SetLogin(value);Service.SaveSettings();Changed();}}
 public bool AutoReconnectOnStartupEnabled{get=>Settings.AutoReconnectOnStartupEnabled;set{Settings.AutoReconnectOnStartupEnabled=value;Service.SaveSettings();Changed();}}
 public bool RuntimeBatteryRefreshEnabled{get=>Settings.RuntimeBatteryRefreshEnabled;set{Settings.RuntimeBatteryRefreshEnabled=value;Service.SaveSettings();Changed();}}
 public bool AutoOptimizeBle{get=>Settings.AutoOptimizeBle;set{Settings.AutoOptimizeBle=value;Service.SaveSettings();Changed();}}
 public bool SkipUsbBleInitialization{get=>Service.SkipInitialization;set{Service.SkipInitialization=value;_=Service.SendSettingsAsync();Changed();}}
 public IReadOnlyList<string> StickProcessingChoices=>StickProcessingOption.All.Select(x=>x.Label).ToArray();
 public string SelectedStickProcessing{get=>Settings.StickProcessingLabel;set{Settings.StickProcessingLabel=value;Service.SaveSettings();Changed();}}
 public double Ps5GyroScale{get=>Settings.Ps5GyroScalePitch;set{Settings.Ps5GyroScalePitch=Settings.Ps5GyroScaleYaw=Settings.Ps5GyroScaleRoll=V60UserSettings.NormalizePs5GyroScale(value);Service.SaveSettings();Changed(nameof(Ps5GyroScaleText));}}
 public string Ps5GyroScaleText=>$"Pitch / Yaw / Roll ×{Ps5GyroScale:F2}";
 public bool ShowDiagnostics{get;set;}
 public bool ShowWin10BleHomePanel{get;set;}
 public bool ShowWin10BleHomeAction=>OperatingSystem.IsWindows()&&Environment.OSVersion.Version.Build<22000;
 public bool CanConfigureWin10Stack=>ShowWin10BleHomeAction&&CanOperate;
 public bool CanChangeBleOptimizationSettings=>CanOperate;
 public bool CanTryWin11Compatibility=>OperatingSystem.IsWindows()&&Environment.OSVersion.Version.Build>=22000&&IsConnected;
 public bool ShowWin11CompatibilityAction=>OperatingSystem.IsWindows();
 public bool ShowBleOptimizationSummary=>Current.Transport=="bluetooth";
 public IReadOnlyList<Win10Preset> Win10BlePresetOptions{get;}=[new("7.5ms 优先"),new("兼容预设"),new("保守预设")];
 public Win10Preset SelectedWin10BlePreset{get;set;}=new("7.5ms 优先");
 public string Win10BleHomeHint=>"在当前连接环境中测试蓝牙参数，并根据真实输入评估效果。";
 public string Win10BlePresetSafetyNote=>"全局蓝牙参数由原 Windows 平台服务负责管理。";
 public string RuntimeReadinessText=>Service.BackendStatus;
 public string UsbipLocationText=>OperatingSystem.IsWindows()?"Windows · VIIPER / USBIP":"macOS · Virtual HID Broker";
 public string CompactFirmware=>Current.Firmware;
 public string CompactFirmwareHint=>"由手柄初始化命令回复读取";
 public string BleStabilitySummary=>$"回报率 {Current.Hertz:F1} Hz · 间隔中位数 {Current.GapP50:F2} ms · P95 {Current.GapP95:F2} ms";
 public string InputStatus=>Current.Status;
 public string CompactRawRate=>$"{Current.Hertz:F1} Hz";
 public string CompactParsedRate=>CompactRawRate;
 public string CompactInputAge=>Current.LastInput==0?"测量中":$"{(Stopwatch.GetTimestamp()-Current.LastInput)*1000d/Stopwatch.Frequency:F1} ms";
 public string CompactActualMode=>ControllerService.Profile(Settings.SelectedModeKey).Label;
 public string CompatibilityTitle=>Current.OutputReady?"游戏输出已就绪":"虚拟手柄驱动状态";
 public string CompatibilityGuidance=>Current.OutputReady?"可在游戏或 Steam 的控制器页面验证输入和震动。":Service.BackendStatus;
 public bool HasLowRateWarning=>IsConnected&&Current.Hertz>0&&Current.Hertz<60;
 public string LowRateTitle=>"输入回报率偏低";
 public string HomeLowRateHint=>"当前按真实输入速率转发；可查看连接诊断与报告间隔。";
 public string LowRateMeasuredSummary=>BleStabilitySummary;
 public string LowRateSummary=>Current.Status;
 public string LowRateAdvice=>"保持手柄靠近电脑，检查蓝牙占用与当前报告率协商结果。";
 public string LowRateEvidence=>Current.Diagnostic;
 public string LowRateEnvironmentSummary=>EnvironmentHomeSummaryHint;
 public string EnvironmentHomeSummaryHint=>$"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
 public string BleOptimizationSummary=>Current.Diagnostic;
 public string BluetoothRestartStatus=>"";
 public bool HasBluetoothRestartStatus=>false;
 public string LogText=>string.Join(Environment.NewLine,logs);
 public string DiagnosticsText=>$"{EnvironmentHomeSummaryHint}\n{Service.BackendStatus}\n{BleStabilitySummary}\n{Current.FactoryCalibration}\n{Current.Diagnostic}\n\n{LogText}";
 public string CalibrationStatus=>calibrationStatus;
 public ICommand ConnectPro2InputCommand{get;}public ICommand CompactStopCommand{get;}public ICommand CancelConnectionCommand{get;}public ICommand DisconnectPro2InputCommand{get;}public ICommand ConnectPro2SlotCommand{get;}public ICommand DisconnectPro2SlotCommand{get;}public ICommand SelectCompactModeCommand{get;}public ICommand OpenConfigurationCommand{get;}public ICommand OpenManualConnectCommand{get;}public ICommand OpenEnvironmentCommand{get;}public ICommand PreviewRumbleCommand{get;}public ICommand CalibrateStickCenterSlotCommand{get;}public ICommand CalibrateStickRangeSlotCommand{get;}public ICommand CalibrateSourceGyroCommand{get;}public ICommand SaveConfigurationCommand{get;}public ICommand ResetConfigurationCommand{get;}public ICommand CancelRearButtonsCommand{get;}public ICommand SelectControlCommand{get;}public ICommand ChooseTargetCommand{get;}public ICommand CloseKeyEditorCommand{get;}public ICommand RestoreSelectedControlCommand{get;}public ICommand DisableSelectedControlCommand{get;}public ICommand ClearSeenCommand{get;}public ICommand LetterLayoutCommand{get;}public ICommand PositionLayoutCommand{get;}public ICommand ClearLogCommand{get;}public ICommand ExportDiagnosticsLogCommand{get;}public ICommand CopyDiagnosisCommand{get;}public ICommand RunWin10BleDiagnosticCommand{get;}public ICommand RestartBluetoothCommand{get;}public ICommand InstallUsbipCommand{get;}public ICommand SelectBackendPathCommand{get;}public ICommand ResetUsbipPathCommand{get;}public ICommand CleanupStaleVirtualDevicesCommand{get;}public ICommand RefreshSteamCacheCommand{get;}public ICommand OptimizeBluetoothCommand{get;}public ICommand ConfigureBluetoothCommand{get;}public ICommand RestoreBluetoothCommand{get;}public ICommand ToggleWindowsBluetoothPanelCommand{get;}public ICommand OpenBackendLicenseCommand{get;}
 void Refresh(){
  Service.Snapshot(_=>{state=Current.Physical.Clone();mappedState=Current.Mapped.Clone();outputState=Current.Submitted.Clone();seen=Current.Seen;});
  var candidates=Service.Devices.Values.OrderBy(x=>x.Transport=="usb"?0:1).ThenBy(x=>x.Name).ToArray();
  if(!Devices.Select(x=>x.Id).SequenceEqual(candidates.Select(x=>x.Id))){Devices.Clear();foreach(var d in candidates)Devices.Add(d);if(SelectedDevice==null||!Devices.Contains(SelectedDevice))SelectedDevice=Devices.FirstOrDefault();}
  for(int i=0;i<4;i++)Pro2Slots[i].Status=Service.Sessions[i].Status;
  var faceLayout=new FaceButtonMapping(draftSwapAB,draftSwapXY);
  foreach(var row in ConfigurationKeys){
   var physical=(GamepadButtons)(1UL<<row.Index);row.Pressed=state.IsPressed(physical);row.Seen=(seen&physical)!=0;row.Selected=row.Index==selectedControl;
   if(row.Index>=18){
    var rear=rearEditors[row.Index==19?0:row.Index==18?1:2];
    row.Summary=!rear.Enabled?configurationMode=="pro2"?"原始":"未分配":rear.Action=="disabled"?"禁用":rear.MappingSummary;
    row.Customized=rear.Enabled;
   }else{
    var target=draftChords.GetValueOrDefault(row.Index,physical);row.Summary=ChordLabel(target).Replace("已禁用","禁用");row.Customized=target!=faceLayout.DefaultTarget(physical);
   }
  }
  Changed(null);
 }
 async Task ExportAsync(){if(Owner?.StorageProvider is not{}storage)return;var file=await storage.SaveFilePickerAsync(new(){Title="导出诊断",SuggestedFileName=$"PRO2-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.txt"});if(file!=null){await using var output=await file.OpenWriteAsync();await using var writer=new StreamWriter(output);await writer.WriteAsync(DiagnosticsText);}}
 async Task SelectBackendPathAsync(){
  if(OperatingSystem.IsMacOS()){var control="/Applications/VirtualHIDControl.app";if(Directory.Exists(control))Open(control);else await InstallMacBackendAsync();return;}
  if(Owner?.StorageProvider is not{}storage)return;var files=await storage.OpenFilePickerAsync(new(){Title="选择虚拟手柄运行时",AllowMultiple=false});if(files.Count>0){var path=files[0].TryGetLocalPath();if(path!=null){Settings.UsbipExecutablePath=path;Service.SaveSettings();await Service.InspectBackendAsync();}}
 }
 public string DriverPathActionText=>OperatingSystem.IsMacOS()?"打开驱动管理…":"选择驱动路径…";
 static void Open(string path){try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch{}}
 void SetLogin(bool enable){
  try{
   if(OperatingSystem.IsMacOS()){
    var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","LaunchAgents");Directory.CreateDirectory(directory);var path=Path.Combine(directory,"io.github.wzxsea.pro2bridge.plist");
    if(enable){var bundle=Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName??AppContext.BaseDirectory;var escaped=System.Security.SecurityElement.Escape(bundle);File.WriteAllText(path,$"<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>io.github.wzxsea.pro2bridge</string><key>ProgramArguments</key><array><string>/usr/bin/open</string><string>{escaped}</string></array><key>RunAtLoad</key><true/></dict></plist>");}else if(File.Exists(path))File.Delete(path);
   }else if(OperatingSystem.IsWindows()){
    using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);if(enable)key?.SetValue("PRO2ControllerAssistant",$"\"{Environment.ProcessPath}\"");else key?.DeleteValue("PRO2ControllerAssistant",false);
   }
  }catch(Exception ex){SetStatus("登录启动设置："+ex.Message);}
 }
}
public sealed class SlotRow(int index):UiObservable{
 public int Index{get;}=index;public string Name=>$"手柄 {Index}";
 bool enabled=true;public bool Enabled{get=>enabled;set{if(Set(ref enabled,value))EnabledChanged?.Invoke(value);}}
 public Action<bool>? EnabledChanged{get;set;}
 string status="等待连接";public string Status{get=>status;set=>Set(ref status,value);}
}
public sealed record Win10Preset(string Title){public override string ToString()=>Title;}
public sealed record BatterySegment(IBrush Fill);
public sealed class BatteryVisual{
 public string Level{get;}public string Caption{get;}public IReadOnlyList<BatterySegment> Segments{get;}
 public BatteryVisual(GamepadState state){var known=state.BatteryLevel!=255;Level=known?$"{state.BatteryLevel}/9":"—";Caption=known?$"原生电量 {Level}":$"电量档位读取中 · {state.BatteryVoltageMillivolts} mV";Segments=Enumerable.Range(0,9).Select(i=>new BatterySegment(Brush.Parse(known&&i<state.BatteryLevel?"#75E6C4":"#263A43"))).ToArray();}
}
