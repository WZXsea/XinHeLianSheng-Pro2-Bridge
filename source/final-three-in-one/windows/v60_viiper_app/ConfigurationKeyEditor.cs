using System;
using System.Collections.Generic;
using System.Linq;

namespace Y700Switch2V60Viiper;

public sealed class ConfigurationKeyEditor : RearEditorObservable
{
    public GamepadButtons Key { get; }
    public string Label { get; }
    public bool IsRear => Key is GamepadButtons.PaddleLeft or GamepadButtons.PaddleRight or GamepadButtons.Aux;
    public bool IsStandard => !IsRear;
    public IReadOnlyList<RearButtonChoice> Targets { get; }
    private readonly string defaultLabel;
    private readonly GamepadButtons defaultTarget;
    private bool resetting;
    private bool useDefault, selected;
    public bool UseDefault
    {
        get => useDefault;
        set
        {
            if (useDefault == value) return;
            useDefault = value;
            if (value)
            {
                resetting = true;
                foreach (var item in Targets) item.Selected = (defaultTarget & item.Key) != 0;
                resetting = false;
            }
            Changed(); Changed(nameof(Summary));
        }
    }
    public bool IsSelected { get => selected; set { selected = value; Changed(); } }
    public GamepadButtons Target => Targets.Where(x => x.Selected).Aggregate(GamepadButtons.None, (m, x) => m | x.Key);
    public string Summary => IsRear ? "进阶动作" : UseDefault ? "默认 · " + defaultLabel :
        Target == GamepadButtons.None ? "已禁用" : string.Join(" + ", Targets.Where(x => x.Selected).Select(x => x.Label));
    public ConfigurationKeyEditor(GamepadButtons key, string label, bool ps5, bool nintendo,
        GamepadButtons defaultTarget, ButtonRemap? saved)
    {
        Key = key; Label = label;
        this.defaultTarget = defaultTarget;
        Targets = RearButtonChoice.Choices(ps5, nintendo).Where(x => x.Key != GamepadButtons.None).ToArray();
        defaultLabel = Targets.FirstOrDefault(x => x.Key == defaultTarget)?.Label ?? "原始输出";
        useDefault = saved == null;
        foreach (var item in Targets) item.Selected = ((saved?.Target ?? defaultTarget) & item.Key) != 0;
        foreach (var item in Targets) item.PropertyChanged += (_, _) =>
        { if (resetting) return; UseDefault = false; Changed(nameof(Target)); Changed(nameof(Summary)); };
    }
    public void SetTarget(GamepadButtons target)
    {
        foreach (var item in Targets) item.Selected = (target & item.Key) != 0;
        UseDefault = false; Changed(nameof(Summary));
    }
}
