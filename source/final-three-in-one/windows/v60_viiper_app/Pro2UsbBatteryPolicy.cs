using System;

namespace Y700Switch2V60Viiper;

public static class Pro2UsbBatteryPolicy
{
    public const int FirstRefreshSeconds=30,IntervalSeconds=300,CaptureTimeoutMs=30;
    public const double IdleSeconds=5,MaximumGapMs=24,LevelFreshSeconds=420;
    public static bool IsIdle(GamepadState state) => state.Buttons==GamepadButtons.None &&
        state.L2==0 && state.R2==0 &&
        Math.Abs(state.Lx-GamepadState.AxisCenter)<64 && Math.Abs(state.Ly-GamepadState.AxisCenter)<64 &&
        Math.Abs(state.Rx-GamepadState.AxisCenter)<64 && Math.Abs(state.Ry-GamepadState.AxisCenter)<64 &&
        (!state.GyroValid || Math.Abs((int)state.GyroX)<150 && Math.Abs((int)state.GyroY)<150 && Math.Abs((int)state.GyroZ)<150);
    public static bool CanRefresh(bool enabled,bool fresh,bool suspended,double idleSeconds,double rateHz) =>
        enabled && fresh && !suspended && double.IsFinite(idleSeconds) && idleSeconds>=IdleSeconds &&
        double.IsFinite(rateHz) && rateHz>=240;
    public static bool CanCommit(bool sameSession,Pro2BatteryStatus? battery,double gapMs) =>
        sameSession && battery is { HasLevel:true } && double.IsFinite(gapMs) && gapMs>0 && gapMs<=MaximumGapMs;
}
