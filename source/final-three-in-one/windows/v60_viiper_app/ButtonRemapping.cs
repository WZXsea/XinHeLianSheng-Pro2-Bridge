using System;
using System.Linq;

namespace Y700Switch2V60Viiper;

public sealed record ButtonRemap(GamepadButtons Source, GamepadButtons Target);

// Explicit physical-source overrides. An empty target disables a key; a missing
// source retains the user's existing AB/XY layout. Never cascade A->B->C.
public sealed record ButtonRemappingConfiguration
{
    public ButtonRemap[] Xbox { get; init; } = [];
    public ButtonRemap[] Ps5 { get; init; } = [];
    public ButtonRemap[] Pro1 { get; init; } = [];
    public ButtonRemap[] Pro2 { get; init; } = [];
    public static readonly GamepadButtons[] Sources = Enumerable.Range(0, 18)
        .Select(i => (GamepadButtons)(1UL << i)).ToArray();
    public ButtonRemappingConfiguration Normalize() => new()
    { Xbox = Clean(Xbox), Ps5 = Clean(Ps5), Pro1 = Clean(Pro1), Pro2 = Clean(Pro2) };
    private static ButtonRemap[] Clean(ButtonRemap[]? value) => (value ?? [])
        .Where(x => x != null && Sources.Contains(x.Source)).GroupBy(x => x.Source)
        .Select(g => new ButtonRemap(g.Key, RearButtonBinding.SafeButtons(g.Last().Target))).ToArray();
    public ButtonRemap[] ForMode(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest => Xbox,
        ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => Ps5,
        ViiperVirtualMode.Pro1 => Pro1,
        _ => Pro2
    };
    public ButtonRemap[] ForProfile(string mode) => mode switch
    { "PS5" => Ps5, "Pro1" => Pro1, "Pro2" => Pro2, _ => Xbox };
    public ButtonRemappingConfiguration WithProfile(string mode, ButtonRemap[] entries) => mode switch
    { "PS5" => this with { Ps5 = entries }, "Pro1" => this with { Pro1 = entries },
      "Pro2" => this with { Pro2 = entries }, _ => this with { Xbox = entries } };
}
