using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Input;

namespace Y700Switch2V60Viiper;

public partial class ControllerTestView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private Pro2BleInputSource? observedSource;
    private Window? owner;
    private MainViewModel? observedViewModel;
    public bool IsSampling => timer.IsEnabled;

    public ControllerTestView()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => UpdateSampling();
        DataContextChanged += (_, _) =>
        {
            if (observedViewModel != null) observedViewModel.PropertyChanged -= ViewModel_Changed;
            observedViewModel = DataContext as MainViewModel;
            if (observedViewModel != null) observedViewModel.PropertyChanged += ViewModel_Changed;
            MappingPopup.DataContext = PopupCard.DataContext = DataContext;
            CloseKeyEditor(false); UpdateMappingLabels(); Stop(); UpdateSampling();
        };
        TestSurface.KeySelected += OpenKeyEditor;
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this);
            if (owner != null) { owner.StateChanged += Owner_StateChanged; owner.Deactivated += Owner_Deactivated; }
            UpdateSampling();
        };
        Unloaded += (_, _) =>
        {
            CloseKeyEditor(false); Stop();
            if (owner != null) { owner.StateChanged -= Owner_StateChanged; owner.Deactivated -= Owner_Deactivated; }
            owner = null;
        };
    }
    private bool closingPopup;
    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RearEditingMode)) CloseKeyEditor(false);
        if (e.PropertyName is nameof(MainViewModel.SelectedConfigurationKey) or nameof(MainViewModel.ConfigurationKeys))
        {
            if (IsKeyEditorOpen) { TestSurface.SelectKey(observedViewModel!.SelectedConfigurationKey.Key); UpdateQuickTargets(); }
            UpdateMappingLabels();
        }
        if (e.PropertyName == nameof(MainViewModel.ConfigurationDraftStatus)) UpdateMappingLabels();
    }
    private void UpdateQuickTargets()
    {
        if (DataContext is MainViewModel vm)
            QuickTargetList.ItemsSource = vm.IsRearKeySelected ? vm.SelectedRearEditor.Buttons : vm.SelectedConfigurationKey.Targets;
    }
    public void OpenKeyEditor(GamepadButtons key)
    {
        if (DataContext is not MainViewModel vm || !IsVisible) return;
        MappingPopup.DataContext = PopupCard.DataContext = vm;
        closingPopup = false; PopupCard.BeginAnimation(OpacityProperty, null);
        vm.SelectConfigurationKey(key); UpdateQuickTargets(); TestSurface.SelectKey(key);
        AdvancedEditor.IsExpanded = false;
        MappingPopup.Visibility = Visibility.Visible;
        PositionEditor(); AnimatePopup();
        PopupCard.Focusable = true; PopupCard.Focus();
    }
    public bool IsKeyEditorOpen => MappingPopup.Visibility == Visibility.Visible;
    private void PositionEditor()
    {
        if (!IsKeyEditorOpen || TestSurface.EditingKey == GamepadButtons.None) return;
        double height = Math.Max(160, ConfigurationRoot.ActualHeight);
        PopupCard.MaxHeight = height - 16;
        PopupScroll.MaxHeight = Math.Max(40, Math.Min(450, height - 185));
        MappingPopup.UpdateLayout();
        var b = ControllerTestSurface.LabelBounds(TestSurface.EditingKey);
        var anchor = TestSurface.TranslatePoint(new System.Windows.Point(b.Left,b.Top),MappingPopup);
        var bottom = TestSurface.TranslatePoint(new System.Windows.Point(b.Right,b.Bottom),MappingPopup);
        double width = Math.Min(360, Math.Max(180,ConfigurationRoot.ActualWidth - 28)); PopupCard.Width = width;
        // Prefer the inside edge of a callout; clamp the complete card to this page.
        double x = anchor.X > ConfigurationRoot.ActualWidth / 2 ? anchor.X - width - 12 : bottom.X + 12;
        double y = anchor.Y;
        Canvas.SetLeft(PopupCard,Math.Clamp(x,8,Math.Max(8,ConfigurationRoot.ActualWidth-width-8)));
        Canvas.SetTop(PopupCard,Math.Clamp(y,8,Math.Max(8,height-PopupCard.ActualHeight-8)));
    }
    public void CloseKeyEditor(bool animate = true)
    {
        if (!IsKeyEditorOpen) { TestSurface.SelectKey(GamepadButtons.None); return; }
        if (!animate || !SystemParameters.ClientAreaAnimation) { HideEditor(); return; }
        if (closingPopup) return;
        closingPopup = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(95));
        fade.Completed += (_, _) => { if (closingPopup) HideEditor(); };
        PopupCard.BeginAnimation(OpacityProperty, fade);
    }
    private void HideEditor()
    {
        closingPopup = false; MappingPopup.Visibility = Visibility.Collapsed;
        TestSurface.SelectKey(GamepadButtons.None); PopupCard.BeginAnimation(OpacityProperty,null);
    }
    private void MappingBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource,MappingPopup) || ReferenceEquals(e.OriginalSource,MenuCanvas))
        { CloseKeyEditor(); e.Handled = true; }
    }
    private void AnimatePopup()
    {
        PopupCard.BeginAnimation(OpacityProperty, null); PopupCard.Opacity = 1;
        if (!SystemParameters.ClientAreaAnimation) { PopupCard.RenderTransform = Transform.Identity; return; }
        PopupCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        var transform = new TranslateTransform(); PopupCard.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void AdvancedEditor_Changed(object sender, RoutedEventArgs e)
    {
        if (QuickTargetList != null) QuickTargetList.ItemTemplate = (DataTemplate)FindResource(AdvancedEditor.IsExpanded ? "Targets" : "QuickTargets");
        if (IsKeyEditorOpen) Dispatcher.BeginInvoke(new Action(PositionEditor),DispatcherPriority.Loaded);
    }
    private void Popup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Escape) { CloseKeyEditor(); TestSurface.Focus(); e.Handled = true; } }
    private void CloseEditor_Click(object sender, RoutedEventArgs e) => CloseKeyEditor();
    private void PickTarget_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not FrameworkElement { DataContext: RearButtonChoice choice }) return;
        if (vm.IsRearKeySelected)
        {
            vm.SelectedRearEditor.Enabled = true;
            if (vm.SelectedRearEditor.Action == "禁用按键") vm.SelectedRearEditor.Action = "按住映射";
            foreach (var button in vm.SelectedRearEditor.Buttons) button.Selected = button.Key == choice.Key;
        }
        else vm.SelectedConfigurationKey.SetTarget(choice.Key);
        UpdateMappingLabels(); CloseKeyEditor();
    }
    private void RestoreKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsRearKeySelected) vm.SelectedRearEditor.Enabled = false; else vm.SelectedConfigurationKey.UseDefault = true;
        UpdateMappingLabels(); CloseKeyEditor();
    }
    private void DisableKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsRearKeySelected) { vm.SelectedRearEditor.Enabled = true; vm.SelectedRearEditor.Action = "禁用按键"; }
        else vm.SelectedConfigurationKey.SetTarget(GamepadButtons.None);
        UpdateMappingLabels(); CloseKeyEditor();
    }
    private void UpdateMappingLabels()
    {
        if (DataContext is not MainViewModel vm) return;
        TestSurface.SetMappings(vm.ConfigurationKeys.Select(key =>
        {
            if (!key.IsRear)
            {
                string output = key.Summary.Replace("默认 · ", "");
                if (output == "原始输出") output = "未分配";
                if (output == "已禁用") output = "禁用";
                return new ControllerTestSurface.MappingLabel(key.Key, key.Label, output, !key.UseDefault);
            }
            var editor = vm.RearEditors[key.Key == GamepadButtons.PaddleLeft ? 0 : key.Key == GamepadButtons.PaddleRight ? 1 : 2];
            string target = !editor.Enabled ? (vm.RearEditingMode == "Pro2" ? "原始" : "未分配") : editor.Action == "禁用按键" ? "禁用" : editor.MappingSummary;
            return new ControllerTestSurface.MappingLabel(key.Key, key.Label, target, editor.Enabled);
        }).ToArray());
    }
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DiagramViewport != null) DiagramViewport.MaxHeight = Math.Max(300, CenterScroll.ActualHeight - 80);
        if (IsKeyEditorOpen) Dispatcher.BeginInvoke(new Action(PositionEditor),DispatcherPriority.Loaded);
    }
    private void Owner_Deactivated(object? sender, EventArgs e) => CloseKeyEditor(false);
    private void Owner_StateChanged(object? sender, EventArgs e) => UpdateSampling();
    private void UpdateSampling()
    {
        if (!IsLoaded || !IsVisible || owner?.WindowState == WindowState.Minimized || DataContext is not MainViewModel)
        { CloseKeyEditor(false); Stop(); return; }
        EnsureSource();
        Refresh();
        timer.Start();
    }
    private void EnsureSource()
    {
        if (DataContext is not MainViewModel vm || SourceSlot == null) return;
        var source = vm.Pro2Slots[Math.Clamp(SourceSlot.SelectedIndex, 0, 3)].InputSource;
        if (ReferenceEquals(source, observedSource)) return;
        observedSource?.EndControllerTest();
        observedSource = source;
        observedSource.BeginControllerTest();
    }
    private void Stop()
    {
        timer.Stop(); observedSource?.EndControllerTest(); observedSource = null;
    }
    private void SourceSlot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) { EnsureSource(); Refresh(); }
    }
    private void ResetView_Click(object sender, RoutedEventArgs e) => TestSurface.ResetView();
    private void Reset_Click(object sender, RoutedEventArgs e) { observedSource?.BeginControllerTest(); Refresh(); }
    private void Refresh()
    {
        if (observedSource == null || DataContext is not MainViewModel vm) return;
        Present(observedSource.GetControllerTestSnapshot());
        TestStatus.Text = observedSource.TransportLabel + " · " + TestStatus.Text;
        if (!vm.IsXboxSelected)
        { TestOutputSummary.Text = "当前输出 · " + vm.SelectedModeLabel; TestXbox.Text = "当前为 " + vm.SelectedModeLabel + "；切换 Xbox 模式后可在此检查 Windows 输入。"; return; }
        if (!XInputTestReader.TryRead(XboxSlot.SelectedIndex, out var xbox))
        { TestOutputSummary.Text = "Windows 输出 · 此 XInput 槽未连接"; TestXbox.Text = "此 XInput 槽未连接。可选择其他槽，或先在“手柄”页启动 Xbox 模式。"; return; }
        TestOutputSummary.Text = "Windows 输出 · " + ControllerTestLabels.Xbox(xbox.Buttons) + " · LT " + xbox.LT + " / RT " + xbox.RT;
        TestXbox.Text = ControllerTestLabels.Xbox(xbox.Buttons) + " · LT " + xbox.LT + "/255 · RT " + xbox.RT +
            "/255\n左摇杆 " + xbox.LX + ", " + xbox.LY + " · 右摇杆 " + xbox.RX + ", " + xbox.RY;
    }
    // Also used by offline UI regression to render exact report and stale/disconnect cases.
    public void Present(ControllerTestSnapshot snapshot)
    {
        TestStatus.Text = snapshot.Fresh ? "输入正常 · 实时高亮" : snapshot.Connected ? "输入停顿 · 等待新报告" : "等待连接 · 请在手柄页连接";
        TestRate.Text = snapshot.RateText;
        TestGap.Text = snapshot.GapText;
        TestTiming.Text = snapshot.GapDetail;
        TestProcessing.Text = snapshot.HandlerText;
        TestSurface.Present(snapshot);
        var s = snapshot.State;
        static string Axis(ushort value) => ControllerTestSnapshot.Axis(value).ToString("+0.000;-0.000;0.000");
        TestAxes.Text = "左摇杆 X " + Axis(s.Lx) + "  Y " + Axis(s.Ly) + "    右摇杆 X " + Axis(s.Rx) + "  Y " + Axis(s.Ry);
        TestTriggers.Text = "ZL " + (s.IsPressed(GamepadButtons.L2) ? "按下" : "松开") + " · ZR " +
            (s.IsPressed(GamepadButtons.R2) ? "按下" : "松开") + "    原始轴值 " + s.Lx + ", " + s.Ly + " / " + s.Rx + ", " + s.Ry;
    }
}
