using System.Diagnostics;
using System.Reflection;
using Y700Switch2V60Viiper;

internal static class ControllerTestRegressionTests
{
    public static ControllerTestSnapshot Fixture(bool fresh = true)
    {
        long now = Stopwatch.GetTimestamp();
        var physical = new GamepadState { Buttons = GamepadButtons.East | GamepadButtons.L2 | GamepadButtons.PaddleLeft,
            Lx = 0, Ly = 4095, Rx = 4095, Ry = 0, SourceTimestampTicks = now - Stopwatch.Frequency / (fresh ? 100 : 1) };
        var rate = new Pro2RateEvidence(Pro2ConnectionPhase.Ready, true, true, true, 128, 128, 640, 640,
            10, 6, "ok", "ok", "test", new NotifyGapSummary(640, 7.5, 15, 22.5, 1, 30, 0, 0, 0), true,
            640, 70, 150);
        return ControllerTestSnapshot.Create(true, physical, physical.Buttons | GamepadButtons.Aux, now, rate);
    }
    public static void Run(Action<bool, string> check)
    {
        var snapshot = Fixture();
        check(snapshot.Fresh && snapshot.State.IsPressed(GamepadButtons.East) && snapshot.RateText == "128.0 Hz" &&
            snapshot.GapText == "7.50 ms", "tester shows measured rate and gaps instead of UI polling rate");
        var stale = Fixture(false);
        check(!stale.Fresh && stale.State.Buttons == 0 && stale.State.Lx == 2048 && stale.RateText == "—" &&
            stale.Seen.HasFlag(GamepadButtons.Aux), "stalled input clears held buttons and axes but preserves tested markers");
        var disconnected = ControllerTestSnapshot.Create(false, snapshot.State, snapshot.Seen, Stopwatch.GetTimestamp(), snapshot.Rate);
        check(!disconnected.Fresh && disconnected.State.Buttons == 0 && disconnected.AgeText == "—" &&
            disconnected.RateText == "—", "disconnection cannot show old held controls or live latency");
        var missing = ControllerTestSnapshot.Create(true, new GamepadState(), 0, Stopwatch.GetTimestamp(), snapshot.Rate);
        check(!missing.Fresh && missing.AgeText == "—", "missing timestamp cannot manufacture zero latency");
        var physical = snapshot.State.Clone();
        var copy = ControllerTestSnapshot.Create(true, physical, 0, physical.SourceTimestampTicks, snapshot.Rate);
        copy.State.Buttons = 0;
        check(physical.Buttons != 0, "UI snapshot cannot mutate gameplay state");
        check(ControllerTestSnapshot.Axis(0) == -1 && ControllerTestSnapshot.Axis(2048) == 0 &&
            ControllerTestSnapshot.Axis(4095) == 1 && ControllerTestSnapshot.Axis(65535) == 1,
            "stick visualization has exact asymmetric 12-bit endpoints and clamps invalid values");
        check(ControllerTestLabels.Physical.Length == 21 && ControllerTestLabels.Physical.Select(x => x.Key).Distinct().Count() == 21 &&
            ControllerTestLabels.Physical.Single(x => x.Key == GamepadButtons.East).Label == "A",
            "all physical keys including C GL GR use Nintendo labels");
        ushort[] masks = [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0x10, 0x20, 0x40, 0x80, 1, 2, 4, 8];
        GamepadButtons[] expected = [GamepadButtons.South, GamepadButtons.East, GamepadButtons.West, GamepadButtons.North,
            GamepadButtons.L1, GamepadButtons.R1, GamepadButtons.Start, GamepadButtons.Back,
            GamepadButtons.LeftStick, GamepadButtons.RightStick, GamepadButtons.DPadUp, GamepadButtons.DPadDown,
            GamepadButtons.DPadLeft, GamepadButtons.DPadRight];
        for (int i = 0; i < masks.Length; i++)
            check(XInputTestReader.DecodeButtons(masks[i], 0, 0) == expected[i], "native XInput key decoding " + i);
        check(XInputTestReader.DecodeButtons(0, 255, 1) == (GamepadButtons.L2 | GamepadButtons.R2) &&
            ControllerTestLabels.Xbox(GamepadButtons.South | GamepadButtons.West) == "A / X",
            "XInput triggers and output labels are independent of Nintendo input labels");

        var source = new Pro2BleInputSource();
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(Pro2BleInputSource).GetField(name, flags)!.SetValue(source, value);
            Set("latest", snapshot.State); Set("latestAt", DateTimeOffset.UtcNow); Set("transportConnected", true);
            typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
            var queue = (Pro2SequentialInputQueue)typeof(Pro2BleInputSource).GetField("sequentialInput", flags)!.GetValue(source)!;
            queue.Enqueue(snapshot.State, Stopwatch.GetTimestamp());
            var counters = source.GetCompactCounters();
            source.BeginControllerTest();
            for (int i = 0; i < 30; i++) source.GetControllerTestSnapshot();
            check(queue.Count == 1 && queue.DequeuedCount == 0 && source.GetCompactCounters() == counters,
                "tester observation never drains or creates gameplay frames");
            source.EndControllerTest();
            check(!(bool)typeof(Pro2BleInputSource).GetField("controllerTestActive", flags)!.GetValue(source)!,
                "tester hot-path capture disabled when page closes");
        }
        finally { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
