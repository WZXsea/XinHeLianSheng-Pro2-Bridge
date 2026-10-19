namespace Y700Switch2V60Viiper;

// The parser keeps physical positions: Nintendo B=South, A=East, Y=West, X=North.
// Transform a copy at output time; source observations and rear-key targets stay intact.
public sealed record FaceButtonMapping(bool SwapAB = false, bool SwapXY = false,
    ButtonRemappingConfiguration? Remapping = null)
{
    public GamepadState Apply(GamepadState physical, ViiperVirtualMode mode = ViiperVirtualMode.Xbox)
    {
        var overrides = Remapping?.ForMode(mode);
        if (overrides is { Length: > 0 }) return ApplyOverrides(physical, overrides);
        var buttons = physical.Buttons;
        if (SwapAB) buttons = Swap(buttons, GamepadButtons.South, GamepadButtons.East);
        if (SwapXY) buttons = Swap(buttons, GamepadButtons.West, GamepadButtons.North);
        if (buttons == physical.Buttons) return physical;
        var mapped = physical.Clone();
        mapped.Buttons = buttons;
        return mapped;
    }

    public GamepadButtons DefaultTarget(GamepadButtons source)
    {
        if (SwapAB) source = Swap(source, GamepadButtons.South, GamepadButtons.East);
        if (SwapXY) source = Swap(source, GamepadButtons.West, GamepadButtons.North);
        return source;
    }

    private GamepadState ApplyOverrides(GamepadState physical, ButtonRemap[] overrides)
    {
        // Rear/C flags are preserved for the independent macro state machine.
        var buttons = physical.Buttons & ~RearButtonBinding.AllowedButtons;
        ushort left = 0, right = 0;
        foreach (var source in ButtonRemappingConfiguration.Sources)
        {
            bool pressed = physical.IsPressed(source) ||
                (source == GamepadButtons.L2 && physical.L2 > 0) ||
                (source == GamepadButtons.R2 && physical.R2 > 0);
            if (!pressed) continue;
            var target = DefaultTarget(source);
            foreach (var entry in overrides)
                if (entry.Source == source) { target = entry.Target; break; }
            buttons |= target;
            if ((target & GamepadButtons.L2) != 0)
                left = Math.Max(left, source == GamepadButtons.L2 && physical.L2 > 0 ? physical.L2 : GamepadState.TriggerMax);
            if ((target & GamepadButtons.R2) != 0)
                right = Math.Max(right, source == GamepadButtons.R2 && physical.R2 > 0 ? physical.R2 : GamepadState.TriggerMax);
        }
        buttons = (buttons & ~RearButtonBinding.AllowedButtons) | RearButtonBinding.SafeButtons(buttons);
        if (buttons == physical.Buttons && left == physical.L2 && right == physical.R2) return physical;
        var mapped = physical.Clone();
        mapped.Buttons = buttons; mapped.L2 = left; mapped.R2 = right;
        return mapped;
    }

    private static GamepadButtons Swap(GamepadButtons buttons, GamepadButtons first, GamepadButtons second) =>
        (buttons & ~(first | second)) |
        ((buttons & first) != 0 ? second : GamepadButtons.None) |
        ((buttons & second) != 0 ? first : GamepadButtons.None);
}
