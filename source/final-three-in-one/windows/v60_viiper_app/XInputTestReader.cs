using System;
using System.Runtime.InteropServices;

namespace Y700Switch2V60Viiper;

public readonly record struct XInputTestState(GamepadButtons Buttons, byte LT, byte RT, short LX, short LY, short RX, short RY);

// Read-only. Polling XInput does not measure physical report rate or end-to-end latency.
public static class XInputTestReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Packet; public Pad Pad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint index, out State state);

    public static bool TryRead(int index, out XInputTestState state)
    {
        state = default;
        if (index is < 0 or > 3 || GetState((uint)index, out var native) != 0) return false;
        var p = native.Pad;
        state = new(DecodeButtons(p.Buttons, p.LT, p.RT), p.LT, p.RT, p.LX, p.LY, p.RX, p.RY);
        return true;
    }

    public static GamepadButtons DecodeButtons(ushort bits, byte lt, byte rt)
    {
        GamepadButtons result = 0;
        (ushort Mask, GamepadButtons Key)[] mapping =
        [
            (0x1000, GamepadButtons.South), (0x2000, GamepadButtons.East),
            (0x4000, GamepadButtons.West), (0x8000, GamepadButtons.North),
            (0x0100, GamepadButtons.L1), (0x0200, GamepadButtons.R1),
            (0x0010, GamepadButtons.Start), (0x0020, GamepadButtons.Back),
            (0x0040, GamepadButtons.LeftStick), (0x0080, GamepadButtons.RightStick),
            (1, GamepadButtons.DPadUp), (2, GamepadButtons.DPadDown),
            (4, GamepadButtons.DPadLeft), (8, GamepadButtons.DPadRight)
        ];
        foreach (var item in mapping) if ((bits & item.Mask) != 0) result |= item.Key;
        if (lt > 0) result |= GamepadButtons.L2;
        if (rt > 0) result |= GamepadButtons.R2;
        return result;
    }
}
