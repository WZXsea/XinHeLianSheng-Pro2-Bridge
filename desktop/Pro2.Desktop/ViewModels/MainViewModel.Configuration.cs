using System.Collections.ObjectModel;
using System.Windows.Input;
using Pro2.Desktop.Views;
using Pro2.Application;
using Y700Switch2V60Viiper;
namespace Pro2.Desktop.ViewModels;
public sealed partial class MainViewModel {
 public string ConfigurationMode {get=>configurationMode;set{if(value==configurationMode)return;drafts[configurationMode]=(BuildRemaps(),BuildRearProfile(),draftSwapAB,draftSwapXY);configurationMode=value;LoadDraft();Changed(null);}}
 public string ConfigurationModeLabel=>configurationMode switch{"xbox"=>"Xbox","dualsense"=>"PS5","pro1"=>"Pro1",_=>"Pro2"};
 public string ConfigurationStatus=>configurationStatus;
 public string ConfigurationDraftStatus=>"修改保留为草稿 · 保存并应用后生效";
 public int SelectedControl {get=>selectedControl;set{if(Set(ref selectedControl,Math.Clamp(value,0,20))){UpdateTargets();Changed(null);}}}
 public ConfigurationKeyRow SelectedConfigurationKey=>ConfigurationKeys[selectedControl];
 public bool IsKeyEditorOpen{get=>keyEditor;set=>Set(ref keyEditor,value);}
 public bool AdvancedEditorExpanded{get=>advancedEditor;set=>Set(ref advancedEditor,value);}
 public bool IsStandardKeySelected=>selectedControl<18;
 public bool IsRearKeySelected=>selectedControl>=18;
 public RearActionDraft SelectedRearEditor=>rearEditors[selectedControl==19?0:selectedControl==18?1:2];
 public string TestStatus=>string.IsNullOrEmpty(testStatus)?IsConnected?"实体输入已接入 · 点选标签编辑":"等待实体手柄输入":testStatus;
 public string TestRate=>$"{Current.Hertz:F0} Hz";
 public string TestGap=>Current.Gaps.Count>0?$"{Current.GapP50:F2} ms":"测量中";
 public string TestOutputSummary=>Current.OutputReady?$"已提交输出：{ControllerService.Profile(Settings.SelectedModeKey).Label} · {outputState.Buttons}":$"映射预览：{mappedState.Buttons} · {Service.BackendStatus}";
 public string TestTiming=>$"有效输入 {Current.Frames:N0} 帧 · 最新输入年龄 {CompactInputAge} · P95 {Current.GapP95:F2} ms";
 public string TestAxes=>$"L: {state.Lx}, {state.Ly}   R: {state.Rx}, {state.Ry}";
 public string TestTriggers=>$"ZL: {state.L2}  ZR: {state.R2}  已测：{seen}";
 public string TestProcessing=>$"摇杆处理：{Settings.StickProcessingLabel} · {Current.Parser.StickCalibrationSummary} · {Current.Parser.GyroCalibrationSummary}";
 public ICommand SearchCommand=>new UiCommand(_=>Run(()=>Service.SearchAsync(true)));
 public ICommand ConnectDeviceCommand=>new UiCommand(p=>{if(p is ControllerDevice d){SelectedDevice=d;Run(()=>Service.ConnectAsync(selectedSlot,d.Id));}});
 public ICommand RefreshEnvironmentCommand=>new UiCommand(_=>Run(()=>Service.InspectBackendAsync()));
 public ICommand CopyEnvironmentCommand=>CopyDiagnosisCommand;
 public ICommand SaveEnvironmentCommand=>ExportDiagnosticsLogCommand;
 public string EnvironmentInfoStatus=>Service.BackendStatus;
 public string EnvironmentReportText=>DiagnosticsText;
 public string AppVersion=>"v"+(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]??"1.4.0");
 void LoadDraft(){
  var mode=ControllerService.Profile(configurationMode).Mode;
  var label=ConfigurationModeLabel;
  var cached=drafts.GetValueOrDefault(configurationMode);
  var remaps=cached.Item1??Settings.ButtonRemapping.ForMode(mode);
  var rear=cached.Item2??Settings.RearButtons.ForMode(mode)??new RearButtonProfile();
  draftSwapAB=cached.Item1!=null?cached.Item3:Settings.SwapABButtons;draftSwapXY=cached.Item1!=null?cached.Item4:Settings.SwapXYButtons;
  var face=new FaceButtonMapping(draftSwapAB,draftSwapXY);
  draftChords.Clear();for(int i=0;i<21;i++){var source=(GamepadButtons)(1UL<<i);draftChords[i]=remaps.LastOrDefault(x=>x.Source==source)?.Target??face.DefaultTarget(source);}
  rearEditors=[Draft(rear.Left),Draft(rear.Right),Draft(rear.C)];UpdateTargets();Changed(null);
 }
 void UpdateTargets(){
  QuickTargets.Clear();
  var selected=IsRearKeySelected?SelectedRearEditor.Targets.Where(x=>x.Selected).Select(x=>x.Index).ToHashSet():Enumerable.Range(0,18).Where(i=>(draftChords.GetValueOrDefault(selectedControl)&(GamepadButtons)(1UL<<i))!=0).ToHashSet();
  for(int i=0;i<18;i++)QuickTargets.Add(new TargetChoice{Index=i,Label=ViewChoices.TargetLabel(i,configurationMode),Selected=selected.Contains(i)});
  Refresh();
 }
 string ChordLabel(GamepadButtons target)=>target==GamepadButtons.None?"已禁用":string.Join(" + ",Enumerable.Range(0,21).Where(i=>(target&(GamepadButtons)(1UL<<i))!=0).Select(i=>ViewChoices.TargetLabel(i,configurationMode)));
 void ChooseTarget(ChooseOutputRequest choice){
  if(choice.Index<0||choice.Index>=18)return;
  if(IsRearKeySelected){var target=SelectedRearEditor.Targets.First(x=>x.Index==choice.Index);if(!choice.Multiple)foreach(var item in SelectedRearEditor.Targets)item.Selected=false;target.Selected=!choice.Multiple||!target.Selected;SelectedRearEditor.Enabled=true;}
  else{var flag=(GamepadButtons)(1UL<<choice.Index);var previous=draftChords.GetValueOrDefault(selectedControl);draftChords[selectedControl]=choice.Multiple?previous^flag:flag;}
  configurationStatus="按键草稿已更新，保存并应用后生效";UpdateTargets();Changed(null);
 }
 void RestoreControl(){draftChords[selectedControl]=(GamepadButtons)(1UL<<selectedControl);if(IsRearKeySelected)rearEditors[selectedControl==19?0:selectedControl==18?1:2]=Draft(new RearButtonBinding());UpdateTargets();}
 void DisableControl(){draftChords[selectedControl]=GamepadButtons.None;if(IsRearKeySelected){SelectedRearEditor.Enabled=true;SelectedRearEditor.Action="disabled";}UpdateTargets();}
 void SetLetterLayout(bool letter){bool swap=letter&&configurationMode=="xbox";draftSwapAB=swap;draftSwapXY=swap;for(int i=0;i<4;i++)draftChords[i]=(GamepadButtons)(1UL<<(swap?new[]{1,0,3,2}[i]:i));configurationStatus=letter?"字母布局已设置，保存后应用":"位置布局已设置，保存后应用";UpdateTargets();}
 ButtonRemap[] BuildRemaps(){var defaults=new FaceButtonMapping(draftSwapAB,draftSwapXY);return draftChords.Where(x=>x.Key<18&&x.Value!=defaults.DefaultTarget((GamepadButtons)(1UL<<x.Key))).Select(x=>new ButtonRemap((GamepadButtons)(1UL<<x.Key),x.Value)).ToArray();}
 RearButtonProfile BuildRearProfile()=>new(){Left=Binding(rearEditors[0]),Right=Binding(rearEditors[1]),C=Binding(rearEditors[2])};
 bool TryBuildConfiguration(out ButtonRemap[] remaps,out RearButtonProfile profile,out string error){
  remaps=[];profile=new();error="";
  if(Busy){error="请等待连接或模式切换完成后再保存。";return false;}
  foreach(var key in draftChords.Where(x=>x.Key<18))
   if(RearButtonBinding.HasOppositeDirections(key.Value)){error=$"{ViewChoices.PhysicalNames[key.Key]}：请选择同一轴上的一个方向。";return false;}
  var bindings=new List<RearButtonBinding>();
  for(int index=0;index<rearEditors.Length;index++){
   var draft=rearEditors[index];string title=new[]{"GL","GR","C"}[index];
   if(draft.Action is not ("follow" or "tap" or "turbo" or "sequence" or "disabled")){error=$"{title}：请从列表选择动作类型。";return false;}
   var selected=draft.Targets.Where(x=>x.Selected).ToArray();
   if(draft.Enabled&&draft.Action!="disabled"&&selected.Any(x=>x.Index<0||x.Index>=18)){error=$"{title}：输出键请选择标准游戏按键。";return false;}
   var keys=draft.Action=="disabled"?GamepadButtons.None:selected.Where(x=>x.Index>=0&&x.Index<18).Aggregate(GamepadButtons.None,(mask,x)=>mask|(GamepadButtons)(1UL<<x.Index));
   if(draft.Enabled&&draft.Action!="disabled"&&keys==GamepadButtons.None){error=$"{title}：请选择至少一个输出按键。";return false;}
   if(draft.Enabled&&RearButtonBinding.HasOppositeDirections(keys)){error=$"{title}：请选择同一轴上的一个方向。";return false;}
   bool delayOk=draft.DelayMS is >=0 and <=30000,pressOk=draft.PressMS is >=30 and <=30000,gapOk=draft.GapMS is >=30 and <=30000,countOk=draft.RepeatCount is >=0 and <=1000;
   if(draft.Enabled&&draft.Action!="disabled"&&(!delayOk||(draft.IsTimed&&!pressOk)||(draft.IsRepeat&&(!gapOk||!countOk)))){
    error=$"{title}：触发延迟范围为 0–30000 ms，按下时长与间隔为 30–30000 ms，连发次数为 0–1000。";return false;
   }
   var steps=new List<RearButtonStep>();
   if(draft.Enabled&&draft.IsSequence){
    if(draft.Steps.Count>16){error=$"{title}：后续动作最多保留 16 步。";return false;}
    for(int stepIndex=0;stepIndex<draft.Steps.Count;stepIndex++){
     var step=draft.Steps[stepIndex];string stepTitle=$"{title} 第 {stepIndex+2} 步";
     if(step.First is <-1 or >=18||step.Second is <-1 or >=18){error=$"{stepTitle}：请从列表选择输出键。";return false;}
     var stepKeys=(step.First>=0?(GamepadButtons)(1UL<<step.First):GamepadButtons.None)|(step.Second>=0?(GamepadButtons)(1UL<<step.Second):GamepadButtons.None);
     if(stepKeys==GamepadButtons.None){error=$"{stepTitle}：请选择至少一个输出按键。";return false;}
     if(RearButtonBinding.HasOppositeDirections(stepKeys)){error=$"{stepTitle}：请选择同一轴上的一个方向。";return false;}
     if(step.DelayMS is <0 or >30000||step.PressMS is <30 or >30000){error=$"{stepTitle}：延迟范围为 0–30000 ms，按下时长为 30–30000 ms。";return false;}
     steps.Add(new RearButtonStep{Buttons=stepKeys,DelayMs=step.DelayMS,PressMs=step.PressMS});
    }
   }
   bindings.Add(new RearButtonBinding{Enabled=draft.Enabled,Action=draft.Action switch{"tap"=>RearButtonAction.Tap,"turbo"=>RearButtonAction.Repeat,"sequence"=>RearButtonAction.Sequence,"disabled"=>RearButtonAction.Disabled,_=>RearButtonAction.Hold},Buttons=keys,DelayMs=delayOk?draft.DelayMS:0,PressMs=pressOk?draft.PressMS:80,GapMs=gapOk?draft.GapMS:80,RepeatCount=countOk?draft.RepeatCount:0,Steps=steps.ToArray()});
  }
  remaps=BuildRemaps();profile=new(){Left=bindings[0],Right=bindings[1],C=bindings[2]};return true;
 }
 void SaveConfiguration(){
  if(!TryBuildConfiguration(out var remaps,out var rear,out var error)){configurationStatus=error;Changed(null);return;}
  var previousRemapping=Settings.ButtonRemapping;var previousRear=Settings.RearButtons;bool previousAB=Settings.SwapABButtons,previousXY=Settings.SwapXYButtons;
  Settings.SwapABButtons=draftSwapAB;Settings.SwapXYButtons=draftSwapXY;
  Settings.ButtonRemapping=previousRemapping.WithProfile(ConfigurationModeLabel,remaps).Normalize();
  Settings.RearButtons=(configurationMode switch{"xbox"=>previousRear with{Xbox=rear},"dualsense"=>previousRear with{Ps5=rear},"pro1"=>previousRear with{Pro1=rear},_=>previousRear with{Pro2=rear}}).Normalize();
  try{Service.SaveSettings();}
  catch(Exception ex){Settings.ButtonRemapping=previousRemapping;Settings.RearButtons=previousRear;Settings.SwapABButtons=previousAB;Settings.SwapXYButtons=previousXY;configurationStatus="配置保存遇到错误："+ex.Message;Changed(null);return;}
  Service.CancelActions();drafts.Remove(configurationMode);configurationStatus=$"{ConfigurationModeLabel} 配置已保存并应用";Changed(null);
 }
 void ResetConfiguration(){Service.CancelActions();draftChords.Clear();for(int i=0;i<21;i++)draftChords[i]=(GamepadButtons)(1UL<<i);draftSwapAB=false;draftSwapXY=false;rearEditors=[Draft(new()),Draft(new()),Draft(new())];configurationStatus="当前模式已恢复默认，保存后应用";UpdateTargets();Changed(null);}
 RearActionDraft Draft(RearButtonBinding binding){
  var d=new RearActionDraft{Enabled=binding.Enabled,Action=binding.Action switch{RearButtonAction.Tap=>"tap",RearButtonAction.Repeat=>"turbo",RearButtonAction.Sequence=>"sequence",RearButtonAction.Disabled=>"disabled",_=>"follow"},DelayMS=binding.DelayMs,PressMS=binding.PressMs,GapMS=binding.GapMs,RepeatCount=binding.RepeatCount};
  for(int i=0;i<18;i++)d.Targets.Add(new TargetChoice{Index=i,Label=ViewChoices.TargetLabel(i,configurationMode),Selected=(binding.Buttons&(GamepadButtons)(1UL<<i))!=0});
  foreach(var step in binding.Steps){var targets=Enumerable.Range(0,18).Where(i=>(step.Buttons&(GamepadButtons)(1UL<<i))!=0).ToArray();var row=new SequenceStepRow{First=targets.FirstOrDefault(),Second=targets.Skip(1).FirstOrDefault(-1),DelayMS=step.DelayMs,PressMS=step.PressMs,Choices=d.Targets.ToArray()};row.RemoveCommand=new UiCommand(_=>d.Steps.Remove(row));d.Steps.Add(row);}
  return d;
 }
 static RearButtonBinding Binding(RearActionDraft draft)=>new(){Enabled=draft.Enabled,Action=draft.Action switch{"tap"=>RearButtonAction.Tap,"turbo"=>RearButtonAction.Repeat,"sequence"=>RearButtonAction.Sequence,"disabled"=>RearButtonAction.Disabled,_=>RearButtonAction.Hold},Buttons=draft.Targets.Where(x=>x.Selected).Aggregate(GamepadButtons.None,(a,x)=>a|(GamepadButtons)(1UL<<x.Index)),DelayMs=draft.DelayMS,PressMs=draft.PressMS,GapMs=draft.GapMS,RepeatCount=draft.RepeatCount,Steps=draft.Steps.Select(x=>new RearButtonStep{Buttons=(x.First>=0?(GamepadButtons)(1UL<<x.First):GamepadButtons.None)|(x.Second>=0?(GamepadButtons)(1UL<<x.Second):GamepadButtons.None),DelayMs=x.DelayMS,PressMs=x.PressMS}).ToArray()};
}
