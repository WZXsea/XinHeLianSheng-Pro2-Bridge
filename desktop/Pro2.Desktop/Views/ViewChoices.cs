using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
namespace Pro2.Desktop.Views;

public sealed record UiChoice(string Id,string Label);
public sealed record ChooseOutputRequest(int Index,bool Multiple);
public static class ViewChoices
{
    public static bool IsMacOS=>OperatingSystem.IsMacOS();
    public static IReadOnlyList<UiChoice> ProfileModes { get; } = [new("xbox","Xbox"),new("dualsense","PS5"),new("pro1","Pro1"),new("pro2","Pro2")];
    public static IReadOnlyList<UiChoice> Actions { get; } = [new("follow","按住映射"),new("tap","单次按键"),new("turbo","按住连发"),new("sequence","顺序按键"),new("disabled","禁用按键")];
    public static string[] PhysicalNames { get; } = ["B","A","Y","X","L","R","ZL","ZR","−","+","L3","R3","↓","→","←","↑","Home","截图","GR","GL","C"];
    public static string TargetLabel(int index,string mode)
    {
        string[] face=mode is "dualsense" or "PS5" ? ["×","○","□","△","L1","R1","L2","R2","Create","Options","L3","R3","↓","→","←","↑","PS","触摸板点击"] : mode is "xbox" or "Xbox" ? ["A","B","X","Y","LB","RB","LT","RT","View","Menu","LS","RS","↓","→","←","↑","Xbox","截图"] : PhysicalNames;
        return index >=0 && index<face.Length ? face[index] : "无";
    }
}

public abstract class UiObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName]string? property=null)=>PropertyChanged?.Invoke(this,new(property));
    protected bool Set<T>(ref T field,T value,[CallerMemberName]string? property=null)
    { if(EqualityComparer<T>.Default.Equals(field,value))return false;field=value;Changed(property);return true; }
}
public sealed class UiCommand(Action<object?> execute,Func<object?,bool>? canExecute=null) : ICommand
{
    public bool CanExecute(object? parameter)=>canExecute?.Invoke(parameter)??true;
    public void Execute(object? parameter)=>execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void Refresh()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
}
public sealed class TargetChoice : UiObservable
{
    public int Index { get; init; }
    public string Label { get; init; }="";
    private bool selected;
    public bool Selected { get=>selected;set=>Set(ref selected,value); }
}
public sealed class ConfigurationKeyRow : UiObservable
{
    public int Index { get; init; }
    public string Label { get; init; }="";
    private string summary="默认";
    private bool pressed,seen,selected,customized;
    public string Summary { get=>summary;set=>Set(ref summary,value); }
    public bool Pressed { get=>pressed;set=>Set(ref pressed,value); }
    public bool Seen { get=>seen;set=>Set(ref seen,value); }
    public bool Selected { get=>selected;set=>Set(ref selected,value); }
    public bool Customized { get=>customized;set=>Set(ref customized,value); }
}
public sealed class SequenceStepRow : UiObservable
{
    private int first,second=-1,delay=100,duration=80;
    private IReadOnlyList<TargetChoice> choices=[new(){Index=-1,Label="无"}];
    public int First { get=>first;set=>Set(ref first,value); }
    public int Second { get=>second;set=>Set(ref second,value); }
    public int DelayMS { get=>delay;set=>Set(ref delay,value); }
    public int PressMS { get=>duration;set=>Set(ref duration,value); }
    public IReadOnlyList<TargetChoice> Choices { get=>choices; init=>choices=new[]{new TargetChoice{Index=-1,Label="无"}}.Concat(value.Where(x=>x.Index>=0)).ToArray(); }
    public ICommand? RemoveCommand { get; set; }
}
public sealed class RearActionDraft : UiObservable
{
    private bool enabled;
    private string action="follow";
    private int delay,press=80,gap=80,count;
    public bool Enabled { get=>enabled;set=>Set(ref enabled,value); }
    public string Action { get=>action;set { if(!Set(ref action,value))return;foreach(string name in new[]{nameof(IsTimed),nameof(IsRepeat),nameof(IsSequence),nameof(HasOutput),nameof(BehaviorHint)})Changed(name); } }
    public int DelayMS { get=>delay;set=>Set(ref delay,value); }
    public int PressMS { get=>press;set=>Set(ref press,value); }
    public int GapMS { get=>gap;set=>Set(ref gap,value); }
    public int RepeatCount { get=>count;set=>Set(ref count,value); }
    public ObservableCollection<TargetChoice> Targets { get; }=[];
    public ObservableCollection<SequenceStepRow> Steps { get; }=[];
    public bool IsTimed=>Action is "tap" or "turbo" or "sequence";
    public bool IsRepeat=>Action=="turbo";
    public bool IsSequence=>Action=="sequence";
    public bool HasOutput=>Action!="disabled";
    public string MappingSummary=>string.Join(" + ",Targets.Where(x=>x.Selected).Select(x=>x.Label)) is {Length:>0} summary?summary:"未分配按键";
    public string BehaviorHint=>Action switch {"turbo"=>"按住时连发；松开停止，0 表示持续。","tap"=>"触发一次；执行中重复按下合并处理。","sequence"=>"触发一次顺序动作；可随时停止背键。","disabled"=>"实体按键仅保留在输入监测中。",_=>"按住生效，松开停止。"};
    public ICommand AddStepCommand { get; }
    public RearActionDraft()
    {
        AddStepCommand=new UiCommand(_=>{
            if(Steps.Count>=16)return;
            var step=new SequenceStepRow { Choices=Targets.ToArray() };
            step.RemoveCommand=new UiCommand(_=>Steps.Remove(step));Steps.Add(step);
        });
        Targets.CollectionChanged+=(_,e)=>{
            if(e.NewItems!=null)foreach(TargetChoice target in e.NewItems)target.PropertyChanged+=(_,_)=>Changed(nameof(MappingSummary));
            Changed(nameof(MappingSummary));
        };
    }
}
