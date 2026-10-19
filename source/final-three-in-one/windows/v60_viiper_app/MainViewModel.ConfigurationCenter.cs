using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, ObservableCollection<ConfigurationKeyEditor>> configurationEditors = new();
    private readonly HashSet<string> configurationDirty = new();
    private GamepadButtons selectedConfigurationKey = GamepadButtons.East;
    private string configurationStatus = "";
    public ObservableCollection<ConfigurationKeyEditor> ConfigurationKeys => configurationEditors[RearEditingMode];
    public ConfigurationKeyEditor SelectedConfigurationKey
    {
        get => ConfigurationKeys.First(x => x.Key == selectedConfigurationKey);
        set
        {
            if (value == null) return;
            selectedConfigurationKey = value.Key;
            if (value.IsRear) SelectedRearIndex = value.Key == GamepadButtons.PaddleLeft ? 0 : value.Key == GamepadButtons.PaddleRight ? 1 : 2;
            foreach (var key in ConfigurationKeys) key.IsSelected = key.Key == selectedConfigurationKey;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsStandardKeySelected)); OnPropertyChanged(nameof(IsRearKeySelected));
        }
    }
    public bool IsStandardKeySelected => SelectedConfigurationKey.IsStandard;
    public bool IsRearKeySelected => SelectedConfigurationKey.IsRear;
    public string ConfigurationDraftStatus => configurationDirty.Contains(RearEditingMode) || rearDirtyModes.Contains(RearEditingMode)
        ? "有未保存的修改" : "配置已保存";
    public string ConfigurationStatus { get => configurationStatus; private set { configurationStatus = value; OnPropertyChanged(); } }
    public string ConfigurationProfileHint => "正在编辑 " + RearEditingMode + " 配置 · 保存后在对应输出模式生效";
    public ICommand SaveConfigurationCommand { get; private set; } = null!;
    public ICommand ResetConfigurationCommand { get; private set; } = null!;
    public ICommand SelectConfigurationKeyCommand { get; private set; } = null!;
    public ICommand DisableConfigurationKeyCommand { get; private set; } = null!;
    public ICommand RestoreConfigurationKeyCommand { get; private set; } = null!;
    public ICommand LetterLayoutCommand { get; private set; } = null!;
    public ICommand PositionLayoutCommand { get; private set; } = null!;

    private void InitializeConfigurationCenter()
    {
        foreach (string mode in RearModes) CreateConfigurationEditors(mode, userSettings.ButtonRemapping.ForProfile(mode));
        SelectConfigurationKeyCommand = new RelayCommand(p =>
        { if (p is ConfigurationKeyEditor key) SelectedConfigurationKey = key; });
        SaveConfigurationCommand = new RelayCommand(_ => SaveConfiguration(), _ => !Busy);
        ResetConfigurationCommand = new RelayCommand(_ =>
        {
            CreateConfigurationEditors(RearEditingMode, []);
            ResetRearButtonsCommand.Execute(null);
            configurationDirty.Add(RearEditingMode); RefreshConfigurationProfile();
            ConfigurationStatus = "已恢复当前配置的默认值；保存后生效。";
        }, _ => !Busy);
        DisableConfigurationKeyCommand = new RelayCommand(_ => SelectedConfigurationKey.SetTarget(GamepadButtons.None));
        RestoreConfigurationKeyCommand = new RelayCommand(_ => SelectedConfigurationKey.UseDefault = true);
        LetterLayoutCommand = new RelayCommand(_ => SetConfigurationFaceLayout(true));
        PositionLayoutCommand = new RelayCommand(_ => SetConfigurationFaceLayout(false));
        SelectedConfigurationKey = SelectedConfigurationKey;
    }
    private void CreateConfigurationEditors(string mode, ButtonRemap[] entries)
    {
        var layout = new FaceButtonMapping(SwapABButtons, SwapXYButtons);
        var keys = new ObservableCollection<ConfigurationKeyEditor>(ControllerTestLabels.Physical.Select(x =>
            new ConfigurationKeyEditor(x.Key, x.Label, mode == "PS5", mode is "Pro1" or "Pro2", layout.DefaultTarget(x.Key),
                entries.FirstOrDefault(e => e.Source == x.Key))));
        configurationEditors[mode] = keys;
        foreach (var key in keys) key.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigurationKeyEditor.IsSelected)) return;
            configurationDirty.Add(mode);
            if (mode == RearEditingMode) { ConfigurationStatus = ""; RefreshConfigurationStatus(); }
        };
    }
    private void SetConfigurationFaceLayout(bool letters)
    {
        bool swap = letters && RearEditingMode == "Xbox";
        var layout = new FaceButtonMapping(swap, swap);
        foreach (var key in ConfigurationKeys.Where(k => k.Key is GamepadButtons.South or GamepadButtons.East or GamepadButtons.West or GamepadButtons.North))
            key.SetTarget(layout.DefaultTarget(key.Key));
        ConfigurationStatus = letters ? "已设置 ABXY 字母布局；保存后生效。" : "已设置 ABXY 位置布局；保存后生效。";
    }
    public void SelectConfigurationKey(GamepadButtons key)
    {
        var editor = ConfigurationKeys.FirstOrDefault(x => x.Key == key);
        if (editor != null) SelectedConfigurationKey = editor;
    }
    private void RefreshConfigurationProfile()
    {
        if (configurationEditors.Count == 0) return;
        OnPropertyChanged(nameof(ConfigurationKeys)); SelectedConfigurationKey = SelectedConfigurationKey;
        OnPropertyChanged(nameof(ConfigurationProfileHint)); RefreshConfigurationStatus(); ConfigurationStatus = "";
    }
    private void RefreshConfigurationStatus() => OnPropertyChanged(nameof(ConfigurationDraftStatus));
    private void SaveConfiguration()
    {
        if (Busy) { ConfigurationStatus = "请等待连接或模式切换完成后再保存。"; return; }
        foreach (var key in ConfigurationKeys.Where(x => x.IsStandard && !x.UseDefault))
            if (RearButtonBinding.HasOppositeDirections(key.Target))
            { ConfigurationStatus = key.Label + "：不能同时输出相反方向。"; return; }
        var bindings = new List<RearButtonBinding>();
        foreach (var editor in RearEditors)
        {
            if (!editor.TryBuild(out var binding, out var error)) { ConfigurationStatus = error; return; }
            bindings.Add(binding);
        }
        var profile = new RearButtonProfile { Left = bindings[0], Right = bindings[1], C = bindings[2] };
        var previousRear = userSettings.RearButtons; var previousMapping = userSettings.ButtonRemapping;
        userSettings.RearButtons = (RearEditingMode switch
        {
            "PS5" => previousRear with { Ps5 = profile }, "Pro1" => previousRear with { Pro1 = profile },
            "Pro2" => previousRear with { Pro2 = profile }, _ => previousRear with { Xbox = profile }
        }).Normalize();
        userSettings.ButtonRemapping = previousMapping.WithProfile(RearEditingMode, ConfigurationKeys
            .Where(x => x.IsStandard && !x.UseDefault).Select(x => new ButtonRemap(x.Key, x.Target)).ToArray()).Normalize();
        try { if (!previewOnly) userSettings.Save(); }
        catch (Exception ex)
        {
            userSettings.RearButtons = previousRear; userSettings.ButtonRemapping = previousMapping;
            ConfigurationStatus = "保存失败：" + FirstLine(ex.Message); return;
        }
        session?.SetFaceButtonMapping(CurrentFaceButtonMapping); session?.SetRearButtons(userSettings.RearButtons);
        foreach (var slot in pro2Slots)
        { slot.Session?.SetFaceButtonMapping(CurrentFaceButtonMapping); slot.Session?.SetRearButtons(userSettings.RearButtons); }
        configurationDirty.Remove(RearEditingMode); rearDirtyModes.Remove(RearEditingMode);
        RefreshConfigurationStatus(); OnPropertyChanged(nameof(RearDraftStatus));
        ConfigurationStatus = "已保存并应用 · " + RearEditingMode;
        AppendLog("[CONFIGURATION_CENTER] profile=" + RearEditingMode + " saved=true reconnect=false");
    }
}
