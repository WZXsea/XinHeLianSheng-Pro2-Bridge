using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using Pro2.Desktop.ViewModels;
using Pro2.Desktop.Views;
using Y700Switch2V60Viiper;

namespace Pro2.Desktop.Tests;

// Exercise the real editor commands and its validation boundary on the UI thread.
// Successful profiles are built in memory; Save is invoked only for a rejected draft.
internal static class ConfigurationValidation
{
    internal sealed record Report(int Checks, string[] Passed, bool SettingsFileUnchanged, bool NativeHostInactive);

    public static Report Run()
    {
        Dispatcher.UIThread.VerifyAccess();
        var model = new MainViewModel(initialize: false);
        var validation = typeof(MainViewModel).GetMethod("TryBuildConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(MainViewModel), "TryBuildConfiguration");
        var settingsPath = (string?)typeof(V60UserSettings).GetProperty("SettingsPath", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException(nameof(V60UserSettings), "SettingsPath");
        var settingsBefore = FileSnapshot.Read(settingsPath);
        var committedBefore = JsonSerializer.Serialize(model.Service.Settings);
        var passed = new List<string>();

        RearActionDraft Reset(string mode = "pro2")
        {
            model.ConfigurationMode = mode;
            model.ResetConfigurationCommand.Execute(null);
            model.SelectedControl = 19; // The original GL editor; GR and C share the same rules.
            return model.SelectedRearEditor;
        }

        RearActionDraft Enable(string action = "follow")
        {
            var draft = Reset();
            draft.Enabled = true;
            draft.Action = action;
            draft.Targets[0].Selected = true;
            return draft;
        }

        RearButtonProfile Verify(string name, bool expected)
        {
            object?[] arguments = [null, null, null];
            var actual = (bool)validation.Invoke(model, arguments)!;
            var error = (string)arguments[2]!;
            Check(actual == expected, $"{name}: expected valid={expected}, received {actual}; {error}");
            Check(expected ? error.Length == 0 : error.Length > 0, $"{name}: validation message matches its result");
            passed.Add(name);
            return (RearButtonProfile)arguments[1]!;
        }

        void CheckCase(string name, Action test)
        {
            test();
            passed.Add(name);
        }

        // The original 18 acceptance cases, now retained in the shared desktop suite.
        var draft = Reset();
        Verify("default profile", true);
        draft.Enabled = true;
        Verify("enabled rear requires target", false);

        Reset();
        model.SelectedControl = 0;
        model.ChooseTargetCommand.Execute(new ChooseOutputRequest(13, false));
        model.ChooseTargetCommand.Execute(new ChooseOutputRequest(14, true));
        Verify("standard opposite directions", false);

        draft = Enable();
        draft.DelayMS = 30001;
        Verify("delay upper bound", false);
        draft.DelayMS = 0;
        draft.PressMS = 0;
        draft.GapMS = 0;
        draft.RepeatCount = -1;
        var built = Verify("follow ignores hidden duration", true);
        Check(built.Left.PressMs == 80 && built.Left.GapMs == 80 && built.Left.RepeatCount == 0, "Hidden timing fields use safe defaults");

        draft.Action = "tap";
        draft.PressMS = 29;
        Verify("tap press lower bound", false);
        draft.PressMS = 30;
        Verify("tap lower boundary", true);
        draft.Action = "turbo";
        draft.GapMS = 29;
        Verify("repeat gap lower bound", false);
        draft.GapMS = 30;
        draft.RepeatCount = 1001;
        Verify("repeat count upper bound", false);
        draft.RepeatCount = 0;
        Verify("continuous repeat", true);

        draft.Action = "sequence";
        for (int i = 0; i < 17; i++) draft.Steps.Add(new SequenceStepRow());
        Verify("sequence 16-step maximum", false);
        draft.Steps.Clear();
        var step = new SequenceStepRow { First = -1 };
        draft.Steps.Add(step);
        Verify("sequence step needs output", false);
        step.First = 14;
        step.Second = 13;
        Verify("sequence opposite directions", false);
        step.First = 0;
        step.Second = -1;
        step.DelayMS = 30001;
        Verify("sequence delay bound", false);
        step.DelayMS = 0;
        step.PressMS = 29;
        Verify("sequence press bound", false);
        step.PressMS = 30;
        built = Verify("valid sequence", true);
        Check(built.Left.Steps.Length == 1 && built.Left.Steps[0].PressMs == 30, "Built sequence retains its valid step");

        draft.Action = "disabled";
        draft.DelayMS = 99999;
        draft.PressMS = 0;
        Verify("disabled mapping ignores hidden drafts", true);

        draft.Action = "tap";
        // Verify rejection before invoking the real Save command: this path returns
        // before persistence. Also compare the file stamp/content for this entire run.
        object?[] rejectedArguments = [null, null, null];
        Check(!(bool)validation.Invoke(model, rejectedArguments)!, "Invalid-save setup is rejected");
        var draftBefore = JsonSerializer.Serialize(draft);
        var rearBefore = model.Service.Settings.RearButtons;
        var remapsBefore = model.Service.Settings.ButtonRemapping;
        var swapsBefore = (model.Service.Settings.SwapABButtons, model.Service.Settings.SwapXYButtons);
        CheckCase("invalid save retains draft and committed profile", () =>
        {
            model.SaveConfigurationCommand.Execute(null);
            Check(ReferenceEquals(draft, model.SelectedRearEditor), "Invalid save retains the editor instance");
            Check(JsonSerializer.Serialize(draft) == draftBefore, "Invalid save retains all draft values");
            Check(ReferenceEquals(rearBefore, model.Service.Settings.RearButtons), "Invalid save retains the committed rear profile");
            Check(ReferenceEquals(remapsBefore, model.Service.Settings.ButtonRemapping), "Invalid save retains committed remapping");
            Check(swapsBefore == (model.Service.Settings.SwapABButtons, model.Service.Settings.SwapXYButtons), "Invalid save retains committed face layout");
            Check(model.ConfigurationStatus == (string)rejectedArguments[2]!, "Invalid save displays the specific validation error");
        });

        // Explicit opposite bounds and allowed maxima protect future UI/Core changes.
        draft = Enable();
        draft.DelayMS = -1;
        Verify("delay lower bound", false);
        draft.DelayMS = 30000;
        Verify("delay maximum accepted", true);
        draft.Action = "tap";
        draft.PressMS = 30000;
        Verify("tap maximum accepted", true);
        draft.PressMS = 30001;
        Verify("tap maximum exceeded", false);
        draft.Action = "turbo";
        draft.PressMS = 30;
        draft.GapMS = 30000;
        draft.RepeatCount = 1000;
        Verify("repeat gap and count maxima accepted", true);
        draft.GapMS = 30001;
        Verify("repeat gap maximum exceeded", false);
        draft.GapMS = 30;
        draft.RepeatCount = -1;
        Verify("repeat count lower bound", false);

        draft = Enable("sequence");
        for (int i = 0; i < 16; i++) draft.Steps.Add(new SequenceStepRow { First = 0, DelayMS = 30000, PressMS = 30000 });
        built = Verify("sequence exactly 16 steps with maximum timing", true);
        Check(built.Left.Steps.Length == 16, "All 16 sequence steps reach the built profile");
        draft.AddStepCommand.Execute(null);
        CheckCase("add-step command stops at 16", () => Check(draft.Steps.Count == 16, "Editor step-count limit"));
        step = draft.Steps[0];
        step.First = 18;
        Verify("sequence rejects target outside standard controls", false);
        step.First = 0;
        step.Second = -2;
        Verify("sequence rejects invalid empty-target index", false);
        step.Second = -1;
        step.DelayMS = -1;
        Verify("sequence delay lower bound", false);
        step.DelayMS = 0;
        step.PressMS = 30001;
        Verify("sequence press maximum exceeded", false);

        draft = Enable();
        draft.Targets[0].Selected = false;
        draft.Targets[12].Selected = true;
        draft.Targets[15].Selected = true;
        Verify("rear rejects vertical opposite directions", false);
        draft.Targets[12].Selected = false;
        draft.Targets[15].Selected = false;
        draft.Targets.Add(new TargetChoice { Index = 18, Label = "GR", Selected = true });
        Verify("rear rejects nonstandard target", false);
        Reset();
        model.Service.Session(0).Busy = true;
        Verify("save waits for an active connection operation", false);
        model.Service.Session(0).Busy = false;

        // Original Windows semantics: only Xbox's letter layout exchanges AB/XY.
        foreach (var mode in new[] { "pro2", "pro1", "xbox", "dualsense" })
        {
            Reset(mode);
            model.LetterLayoutCommand.Execute(null);
            CheckCase($"{mode} letter layout", () => AssertFaceTargets(model, mode == "xbox" ? [1, 0, 3, 2] : [0, 1, 2, 3]));
            model.PositionLayoutCommand.Execute(null);
            CheckCase($"{mode} position layout", () => AssertFaceTargets(model, [0, 1, 2, 3]));
        }

        Check(JsonSerializer.Serialize(model.Service.Settings) == committedBefore, "Configuration checks retain all committed settings");
        Check(FileSnapshot.Read(settingsPath) == settingsBefore, "Settings file remains byte-for-byte and timestamp unchanged");
        Check(!model.Service.Bridge.IsRunning, "Native host remains inactive");
        model.ShutdownAsync().GetAwaiter().GetResult();
        return new Report(passed.Count, passed.ToArray(), true, true);
    }

    private static void AssertFaceTargets(MainViewModel model, int[] expected)
    {
        for (int source = 0; source < 4; source++)
        {
            model.SelectedControl = source;
            var actual = model.QuickTargets.Where(x => x.Selected).Select(x => x.Index).ToArray();
            Check(actual.Length == 1 && actual[0] == expected[source], $"{model.ConfigurationMode} face control {source} target");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Configuration regression: " + message);
    }

    private sealed record FileSnapshot(bool Exists, long Length, DateTime LastWriteUtc, string Sha256)
    {
        public static FileSnapshot Read(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new(true, info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                : new(false, 0, default, "");
        }
    }
}
