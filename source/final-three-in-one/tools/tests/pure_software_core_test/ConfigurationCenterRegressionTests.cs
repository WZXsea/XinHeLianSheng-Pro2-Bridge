using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Y700Switch2V60Viiper;

internal static class ConfigurationCenterRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var modes = new[] { ViiperVirtualMode.Xbox, ViiperVirtualMode.DualSenseLike, ViiperVirtualMode.Pro1, ViiperVirtualMode.Pro2 };
        foreach (var mode in modes)
        foreach (var source in ButtonRemappingConfiguration.Sources)
        foreach (var target in new[] { GamepadButtons.None, GamepadButtons.South, GamepadButtons.L2,
            GamepadButtons.R2, GamepadButtons.L1 | GamepadButtons.North })
        {
            var entries = new[] { new ButtonRemap(source, target) };
            var config = new ButtonRemappingConfiguration { Xbox = entries, Ps5 = entries, Pro1 = entries, Pro2 = entries }.Normalize();
            var mapper = new FaceButtonMapping(true, true, config);
            var state = new GamepadState { Buttons = source, L2 = source == GamepadButtons.L2 ? (ushort)4095 : (ushort)0,
                R2 = source == GamepadButtons.R2 ? (ushort)4095 : (ushort)0, Lx = 1234, SourceTimestampTicks = 99, GyroValid = true, GyroX = 78 };
            var mapped = mapper.Apply(state, mode);
            check(mapped.Buttons == target, "per-physical-source non-cascading mapping " + mode + "/" + source + "/" + target);
            check(mapped.L2 == ((target & GamepadButtons.L2) != 0 ? 4095 : 0) && mapped.R2 == ((target & GamepadButtons.R2) != 0 ? 4095 : 0),
                "remap keeps digital and analog triggers consistent " + source + "/" + target);
            check(mapped.Lx == 1234 && mapped.SourceTimestampTicks == 99 && mapped.GyroValid && mapped.GyroX == 78 && state.Buttons == source,
                "remap preserves source snapshot, axes, motion and timestamp");
            check(mapper.Apply(GamepadState.Neutral(), mode).Buttons == 0 && mapper.Apply(GamepadState.Neutral(), mode).L2 == 0,
                "all remapped keys and trigger values release on neutral");
        }
        var config2 = new ButtonRemappingConfiguration { Xbox = [new(GamepadButtons.East, GamepadButtons.South),
            new(GamepadButtons.South, GamepadButtons.R1), new(GamepadButtons.L2, GamepadButtons.None)] }.Normalize();
        var map = new FaceButtonMapping(false, false, config2);
        check(map.Apply(new() { Buttons = GamepadButtons.East }).Buttons == GamepadButtons.South, "A->B and B->R do not cascade");
        var both = map.Apply(new() { Buttons = GamepadButtons.East | GamepadButtons.South | GamepadButtons.L2, L2 = 4095 });
        check(both.Buttons == (GamepadButtons.South | GamepadButtons.R1) && both.L2 == 0, "simultaneous sources OR together and disabled trigger clears");
        check(map.Apply(new() { Buttons = GamepadButtons.East }, ViiperVirtualMode.Pro2).Buttons == GamepadButtons.East,
            "mode profiles remain independent");
        var serialized = JsonSerializer.Serialize(new V60UserSettings { ButtonRemapping = config2,
            RearButtons = new() { Pro2 = new() { C = new() { Enabled = true, Action = RearButtonAction.Disabled } } } });
        var loaded = JsonSerializer.Deserialize<V60UserSettings>(serialized)!;
        check(loaded.ButtonRemapping.Xbox.SequenceEqual(config2.Xbox) && loaded.RearButtons.Pro2.C.Action == RearButtonAction.Disabled,
            "custom mappings and native-Pro2 C settings survive JSON roundtrip");
        var bad = new ButtonRemappingConfiguration { Xbox = [new(0, GamepadButtons.South),
            new(GamepadButtons.East | GamepadButtons.South, GamepadButtons.R1), new(GamepadButtons.L1, GamepadButtons.South),
            new(GamepadButtons.L1, GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.PaddleLeft)] }.Normalize();
        check(bad.Xbox.Length == 1 && bad.Xbox[0].Target == 0, "invalid source, duplicate source and invalid target normalize safely");
        var rear = new RearButtonMapper();
        var rearConfig = new RearButtonConfiguration { Pro2 = new() { C = new() { Enabled = true, Action = RearButtonAction.Disabled } } };
        check(rear.Apply(new() { Buttons = GamepadButtons.Aux | GamepadButtons.East }, ViiperVirtualMode.Pro2, rearConfig, 100).Buttons == GamepadButtons.East,
            "Pro2 C can be disabled without consuming an independent physical key");
        foreach (var mode in modes)
        {
            var disabled = new RearButtonProfile
            {
                Left = new() { Enabled = true, Action = RearButtonAction.Disabled },
                Right = new() { Enabled = true, Action = RearButtonAction.Disabled },
                C = new() { Enabled = true, Action = RearButtonAction.Disabled }
            };
            var allDisabled = new RearButtonConfiguration { Xbox = disabled, Ps5 = disabled, Pro1 = disabled, Pro2 = disabled };
            var engine = new RearButtonMapper();
            for (int i = 0; i < 10; i++)
                check(engine.Apply(new() { Buttons = GamepadButtons.PaddleLeft | GamepadButtons.PaddleRight | GamepadButtons.Aux | GamepadButtons.R1 },
                    mode, allDisabled, 100 + i).Buttons == GamepadButtons.R1 && !engine.HasPendingAction,
                    "disabled GL/GR/C never schedule actions or consume other held keys " + mode);
        }
        var disabledEditor = new RearButtonEditor("C", false, new() { Enabled = true });
        disabledEditor.Action = "禁用按键"; disabledEditor.Delay = "invalid";
        disabledEditor.Buttons.First(x => x.Key == GamepadButtons.DPadUp).Selected = true;
        disabledEditor.Buttons.First(x => x.Key == GamepadButtons.DPadDown).Selected = true;
        check(disabledEditor.TryBuild(out var disabledBinding, out _) && disabledBinding.Enabled && disabledBinding.Buttons == 0 &&
            disabledBinding.Action == RearButtonAction.Disabled && !disabledEditor.HasOutput,
            "disabled rear action ignores unused target and timing drafts");
        foreach (var item in ControllerTestLabels.Physical)
        {
            var editor = new ConfigurationKeyEditor(item.Key, item.Label, false, false, item.Key, null);
            check(editor.UseDefault, "old settings retain default for " + item.Label);
            editor.SetTarget(GamepadButtons.L2); check(!editor.UseDefault && editor.Target == GamepadButtons.L2, "editor custom target " + item.Label);
            editor.UseDefault = true; check(editor.UseDefault, "restore default " + item.Label);
        }

    }
    public static void RunUi(Window window, ControllerTestView view, MainViewModel vm, Action<bool, string> check, Action pump, Action<string> save, string output)
    {
        var diagram = (ControllerTestSurface)view.FindName("TestSurface");
        check(Descendants<System.Windows.Controls.Viewport3D>(diagram).Count()==2,"front and rear use real native 3D viewports");
        foreach (var physical in ControllerTestLabels.Physical)
        {
            var b = ControllerTestSurface.LabelBounds(physical.Key);
            check(diagram.HitDiagramKey(new(b.X + b.Width / 2,b.Y + b.Height / 2)) == physical.Key,
                "compact label hit target " + physical.Label);
        }
        foreach(var key in new[] {GamepadButtons.East,GamepadButtons.North,GamepadButtons.LeftStick,GamepadButtons.PaddleLeft,GamepadButtons.PaddleRight})
            check(diagram.HitDiagramKey(diagram.ModelAnchor(key))==key,"3D ray hit follows projected mechanical anchor "+key);
        vm.RearEditingMode = "Xbox"; view.OpenKeyEditor(GamepadButtons.East); pump();
        check(view.IsKeyEditorOpen && diagram.EditingKey == GamepadButtons.East,
            "physical key opens anchored editor instead of a lower page editor");
        for (int i = 0; i < 12; i++) { pump(); System.Threading.Thread.Sleep(30); }
        check(view.IsKeyEditorOpen,"editor remains open after opening click and animation settle");
        var card = (FrameworkElement)view.FindName("PopupCard");
        var overlay = (FrameworkElement)view.FindName("MappingPopup");
        var position = card.TranslatePoint(new Point(0,0),overlay);
        check(position.X >= 0 && position.Y >= 0 && position.X + card.ActualWidth <= overlay.ActualWidth + 1 &&
            position.Y + card.ActualHeight <= overlay.ActualHeight + 1,"anchored editor stays entirely inside configuration page");
        var choicesList = (ItemsControl)view.FindName("QuickTargetList");
        var pick = Descendants<Button>(choicesList).First(x => x.DataContext is RearButtonChoice { Key: GamepadButtons.L2 });
        pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 5; i++) { pump(); System.Threading.Thread.Sleep(40); }
        check(vm.SelectedConfigurationKey.Target == GamepadButtons.L2 && !view.IsKeyEditorOpen && diagram.EditingKey == GamepadButtons.None,
            "one click chooses actual target, closes menu and clears editing outline");
        view.OpenKeyEditor(GamepadButtons.East); pump();
        view.CloseKeyEditor(false); pump();
        check(!view.IsKeyEditorOpen && diagram.EditingKey == GamepadButtons.None && vm.SelectedConfigurationKey.Target == GamepadButtons.L2,
            "outside dismissal clears selection without discarding draft");
        view.OpenKeyEditor(GamepadButtons.East); pump();
        check(vm.IsStandardKeySelected && !vm.IsRearKeySelected, "standard key uses common mapping editor");
        vm.DisableConfigurationKeyCommand.Execute(null); pump();
        check(vm.SelectedConfigurationKey.Summary == "已禁用" && vm.ConfigurationDraftStatus.Contains("未保存"), "disable marks an editable draft");
        vm.SelectedConfigurationKey.SetTarget(GamepadButtons.L2 | GamepadButtons.R1);
        vm.SaveConfigurationCommand.Execute(null);
        var settings = (V60UserSettings)typeof(MainViewModel).GetField("userSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        check(vm.ConfigurationStatus.Contains("已保存并应用") && settings.ButtonRemapping.Xbox.Single(x => x.Source == GamepadButtons.East).Target ==
            (GamepadButtons.L2 | GamepadButtons.R1), "center save writes actual multi-key configuration");
        vm.SelectedConfigurationKey.SetTarget(GamepadButtons.DPadUp | GamepadButtons.DPadDown);
        vm.SaveConfigurationCommand.Execute(null);
        check(vm.ConfigurationStatus.Contains("相反方向") && settings.ButtonRemapping.Xbox[0].Target == (GamepadButtons.L2 | GamepadButtons.R1),
            "invalid direction chord rejects entire transaction");
        vm.SelectedConfigurationKey.SetTarget(GamepadButtons.R2);
        vm.RearEditors[0].Enabled = true; vm.RearEditors[0].Delay = "bad";
        vm.SaveConfigurationCommand.Execute(null);
        check(settings.ButtonRemapping.Xbox[0].Target == (GamepadButtons.L2 | GamepadButtons.R1) && vm.ConfigurationStatus.Contains("GL"),
            "invalid rear draft cannot partially save ordinary-key changes");
        vm.RearEditors[0].Enabled = false; vm.RearEditors[0].Delay = "0";
        vm.SelectedConfigurationKey.SetTarget(GamepadButtons.DPadUp | GamepadButtons.DPadDown);
        vm.RearEditingMode = "PS5"; pump();
        check(vm.SelectedConfigurationKey.Key == GamepadButtons.East && vm.SelectedConfigurationKey.UseDefault &&
            vm.SelectedConfigurationKey.Targets.Any(x => x.Label == "○"), "mode switch changes target labels and preserves source selection");
        vm.RearEditingMode = "Xbox";
        check(vm.SelectedConfigurationKey.Target == (GamepadButtons.DPadUp | GamepadButtons.DPadDown), "invalid unsaved draft survives mode switch");
        vm.ResetConfigurationCommand.Execute(null); vm.SaveConfigurationCommand.Execute(null); pump();
        check(settings.ButtonRemapping.Xbox.Length == 0 && vm.ConfigurationDraftStatus == "配置已保存", "reset is saved as defaults");
        view.OpenKeyEditor(GamepadButtons.PaddleLeft);
        ((Expander)view.FindName("AdvancedEditor")).IsExpanded = true; pump();
        check(vm.IsRearKeySelected && vm.SelectedRearIndex == 0 && ((ContentControl)view.FindName("RearEditorContent")).IsVisible,
            "GL popup contains advanced actions in place");
        ((ContentControl)view.FindName("RearEditorContent")).UpdateLayout(); pump();
        SaveVisual((FrameworkElement)view.FindName("PopupCard"), output, "popup-debug-rear.png");
        var actionPicker = Descendants<ComboBox>((ContentControl)view.FindName("RearEditorContent")).First();
        actionPicker.SelectedIndex = 4; pump();
        check(vm.SelectedRearEditor.Action == "禁用按键" && !vm.SelectedRearEditor.HasOutput, "real action dropdown switches to disabled state");
        vm.SelectedRearEditor.Action = "按住映射";
        vm.SelectConfigurationKey(GamepadButtons.East); vm.LetterLayoutCommand.Execute(null); vm.SaveConfigurationCommand.Execute(null); pump();
        check(settings.ButtonRemapping.Xbox.Single(x => x.Source == GamepadButtons.East).Target == GamepadButtons.South,
            "one-click Xbox letter preset preserves Nintendo physical identity");
        view.OpenKeyEditor(GamepadButtons.East); pump();
        for (int i = 0; i < 5; i++) { pump(); System.Threading.Thread.Sleep(40); }
        SaveVisual((FrameworkElement)view.FindName("PopupCard"), output, "popup-standard.png");
        save("ui-configuration-standard.png");
        vm.RearEditingMode = "PS5"; pump();
        check(!view.IsKeyEditorOpen && diagram.EditingKey == GamepadButtons.None,"profile switch dismisses popup and selection");
        vm.RearEditingMode = "Xbox";
        view.OpenKeyEditor(GamepadButtons.Aux); ((Expander)view.FindName("AdvancedEditor")).IsExpanded = true; pump();
        for (int i = 0; i < 5; i++) { pump(); System.Threading.Thread.Sleep(40); }
        SaveVisual((FrameworkElement)view.FindName("PopupCard"), output, "popup-c-advanced.png");
        view.CloseKeyEditor(false);
        var rate = ControllerTestRegressionTests.Fixture().Rate;
        var animatedKeys=new[] {GamepadButtons.East,GamepadButtons.South,GamepadButtons.West,GamepadButtons.North,
            GamepadButtons.Back,GamepadButtons.Start,GamepadButtons.Home,GamepadButtons.Capture,GamepadButtons.Aux,
            GamepadButtons.L1,GamepadButtons.L2,GamepadButtons.R1,GamepadButtons.R2};
        var keys = animatedKeys.Aggregate(GamepadButtons.None,(all,key)=>all|key);
        // Stop only the offline UI sampler; fixture frames never enter the gameplay pipe.
        var timer = (System.Windows.Threading.DispatcherTimer)typeof(ControllerTestView).GetField("timer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
        timer.Stop();
        diagram.Present(new(true,true,new GamepadState { Buttons = keys,Lx=2048,Ly=2048,Rx=2048,Ry=2048 },keys,0,rate));
        for (int i = 0; i < 8; i++) { pump(); System.Threading.Thread.Sleep(35); }
        check(animatedKeys.All(k => diagram.PressTravel(k) > .97),
            "face, shoulder and trigger caps animate to physically depressed state");
        SaveVisual(diagram,output,"diagram-pressed.png");
        diagram.Present(new(true,true,GamepadState.Neutral(),keys,0,rate));
        for (int i = 0; i < 10; i++) { pump(); System.Threading.Thread.Sleep(35); }
        check(animatedKeys.All(k => diagram.PressTravel(k) == 0),
            "release returns caps fully despite tested history");
        SaveVisual(diagram,output,"diagram-released.png");
        // One UI observation of a quick tap still needs a visible down/up cycle.
        diagram.Present(new(true,true,new GamepadState {Buttons=GamepadButtons.Start},0,0,rate));
        diagram.Present(new(true,true,GamepadState.Neutral(),0,0,rate));
        for(int i=0;i<2;i++){pump();System.Threading.Thread.Sleep(20);}
        check(diagram.PressTravel(GamepadButtons.Start)>.3,"quick tap remains visible long enough to see the cap depress");
        for(int i=0;i<10;i++){pump();System.Threading.Thread.Sleep(30);}
        check(diagram.PressTravel(GamepadButtons.Start)==0,"quick tap automatically rebounds without another input report");
        diagram.Present(new(true,true,new GamepadState { Buttons = keys },keys,0,rate)); pump();
        diagram.Present(new(false,false,new GamepadState { Buttons = keys },keys,0,rate));
        check(diagram.PressTravel(GamepadButtons.East) == 0 && diagram.PressTravel(GamepadButtons.L2) == 0,
            "stale or disconnected input immediately clears physical travel");
        timer.Start();
        for (int i = 0; i < 6; i++) { pump(); System.Threading.Thread.Sleep(40); }
        view.CloseKeyEditor(false); pump();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(17,28,46)),null,new Rect(0,0,1400,670));
            drawing.DrawRectangle(new VisualBrush(diagram),null,new Rect(0,0,1400,670));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1400,670,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = System.IO.File.Create(System.IO.Path.Combine(output,"mapping-diagram-preview.png"))) encoder.Save(file);
        vm.ResetConfigurationCommand.Execute(null); vm.SaveConfigurationCommand.Execute(null); pump();
    }
    private static void SaveVisual(FrameworkElement element, string output, string name)
    {
        element.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new VisualBrush(element),null,new Rect(0,0,element.ActualWidth,element.ActualHeight));
        image.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = System.IO.File.Create(System.IO.Path.Combine(output,name)); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var value2 in Descendants<T>(child)) yield return value2;
        }
    }
}
