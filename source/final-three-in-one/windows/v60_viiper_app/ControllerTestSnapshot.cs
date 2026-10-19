using System;
using System.Diagnostics;
using System.Linq;

namespace Y700Switch2V60Viiper;

// UI observations never consume the gameplay queue or write controller commands.
public sealed record ControllerTestSnapshot(bool Connected, bool Fresh, GamepadState State,
    GamepadButtons Seen, double AgeMs, Pro2RateEvidence Rate)
{
    public static ControllerTestSnapshot Create(bool connected, GamepadState? state,
        GamepadButtons seen, long nowTicks, Pro2RateEvidence rate)
    {
        double age = state?.SourceTimestampTicks > 0 && nowTicks >= state.SourceTimestampTicks
            ? (nowTicks - state.SourceTimestampTicks) * 1000.0 / Stopwatch.Frequency : double.PositiveInfinity;
        bool fresh = connected && state != null && age < 500;
        return new(connected, fresh, fresh ? state!.Clone() : GamepadState.Neutral(), seen, age, rate);
    }

    public static double Axis(ushort value) => Math.Clamp((value - 2048.0) / (value >= 2048 ? 2047 : 2048), -1, 1);
    public string RateText => Fresh && Rate.ParsedHz > 0 ? Rate.ParsedHz.ToString("F1") + " Hz" : "—";
    public string AgeText => Connected && double.IsFinite(AgeMs) ? AgeMs.ToString("F1") + " ms" : "—";
    public string GapText => Fresh && Rate.RecentWindow && Rate.Gaps.Samples > 0
        ? Rate.Gaps.P50Ms.ToString("F2") + " ms" : "—";
    public string GapDetail => Fresh && Rate.RecentWindow && Rate.Gaps.Samples > 0
        ? "回报间隔 P95 " + Rate.Gaps.P95Ms.ToString("F2") + " ms · 最大 " + Rate.Gaps.MaxMs.ToString("F2") + " ms"
        : "回报间隔：等待约 5 秒测量窗口";
    public string HandlerText => Connected && Rate.HandlerSamples > 0
        ? "通知处理平均 " + (Rate.HandlerAverageUs / 1000).ToString("F3") + " ms（本次连接累计）" : "通知处理：—";
    public string Status => Fresh ? "输入正常 · 绿色高亮为当前按下，轮廓为本轮已按到" :
        Connected ? "输入已停顿 · 当前按键已清空，等待新报告" : "等待连接 · 请先在“手柄”页连接";
}

public sealed partial class Pro2BleInputSource
{
    private bool controllerTestActive;
    private GamepadButtons controllerTestSeen;

    public void BeginControllerTest()
    {
        lock (gate) { controllerTestSeen = GamepadButtons.None; controllerTestActive = true; }
    }
    public void EndControllerTest()
    {
        lock (gate) controllerTestActive = false;
    }
    public ControllerTestSnapshot GetControllerTestSnapshot()
    {
        lock (gate)
            return ControllerTestSnapshot.Create(IsRunning && transportConnected,
                latestAt == default ? null : latest, controllerTestSeen, Stopwatch.GetTimestamp(), GetRateEvidence());
    }
}

public static class ControllerTestLabels
{
    public static readonly (GamepadButtons Key, string Label)[] Physical =
    [
        (GamepadButtons.South, "B"), (GamepadButtons.East, "A"),
        (GamepadButtons.West, "Y"), (GamepadButtons.North, "X"),
        (GamepadButtons.L1, "L"), (GamepadButtons.R1, "R"),
        (GamepadButtons.L2, "ZL"), (GamepadButtons.R2, "ZR"),
        (GamepadButtons.Back, "−"), (GamepadButtons.Start, "+"),
        (GamepadButtons.LeftStick, "L3"), (GamepadButtons.RightStick, "R3"),
        (GamepadButtons.DPadUp, "↑"), (GamepadButtons.DPadDown, "↓"),
        (GamepadButtons.DPadLeft, "←"), (GamepadButtons.DPadRight, "→"),
        (GamepadButtons.Home, "Home"), (GamepadButtons.Capture, "截图"),
        (GamepadButtons.PaddleLeft, "GL"), (GamepadButtons.PaddleRight, "GR"), (GamepadButtons.Aux, "C")
    ];
    public static string Xbox(GamepadButtons buttons) => buttons == 0 ? "无按键" : string.Join(" / ",
        Physical.Where(item => (buttons & item.Key) != 0).Select(item => item.Key switch
        {
            GamepadButtons.South => "A", GamepadButtons.East => "B", GamepadButtons.West => "X",
            GamepadButtons.North => "Y", GamepadButtons.L1 => "LB", GamepadButtons.R1 => "RB",
            GamepadButtons.L2 => "LT", GamepadButtons.R2 => "RT", _ => item.Label
        }));
}
