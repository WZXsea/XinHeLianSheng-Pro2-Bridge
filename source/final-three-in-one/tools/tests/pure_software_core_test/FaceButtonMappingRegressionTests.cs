using System.Buffers.Binary;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class FaceButtonMappingRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        var extras = GamepadButtons.L1 | GamepadButtons.R2 | GamepadButtons.Home |
            GamepadButtons.Aux | GamepadButtons.PaddleLeft;
        for (int config = 0; config < 4; config++)
        for (int mask = 0; mask < 16; mask++)
        {
            var mapping = new FaceButtonMapping((config & 1) != 0, (config & 2) != 0);
            var physical = new GamepadState { Buttons = (GamepadButtons)mask | extras,
                Lx = 1234, R2 = 3072, Updates = 123, RawNotificationSequence = 456,
                MotionTimestampUs = 789, SourceTimestampTicks = 111, GyroValid = true, GyroX = 42 };
            int expectedMask = 0;
            int[] destinations = [(config & 1) != 0 ? 1 : 0, (config & 1) != 0 ? 0 : 1,
                (config & 2) != 0 ? 3 : 2, (config & 2) != 0 ? 2 : 3];
            for (int bit = 0; bit < 4; bit++) if ((mask & (1 << bit)) != 0) expectedMask |= 1 << destinations[bit];
            var mapped = mapping.Apply(physical);
            check(mapped.Buttons == ((GamepadButtons)expectedMask | extras), "all face chords and independent swaps " + config + "/" + mask);
            check(physical.Buttons == ((GamepadButtons)mask | extras) && mapped.Lx == 1234 && mapped.R2 == 3072 &&
                mapped.Updates == 123 && mapped.RawNotificationSequence == 456 && mapped.MotionTimestampUs == 789 &&
                mapped.SourceTimestampTicks == 111 && mapped.GyroValid && mapped.GyroX == 42,
                "face mapping preserves physical observations, triggers and motion " + config + "/" + mask);
            check(mapping.Apply(mapped).Buttons == physical.Buttons, "face swaps are reversible " + config + "/" + mask);
        }

        foreach (var item in new[] { (0x08u, 0x1000u), (0x04u, 0x2000u), (0x02u, 0x4000u), (0x01u, 0x8000u) })
        {
            byte[] payload = new byte[63];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), item.Item1);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(52), 4096);
            var parser = new Pro2HidReportParser(true);
            check(parser.TryParseFd2Payload(payload, out var parsed, out _), "physical FD2 face-button fixture accepted");
            var mapped = new FaceButtonMapping(true, true).Apply(parsed);
            check(BinaryPrimitives.ReadUInt32LittleEndian(VirtualPadPackets.FromGamepad(ViiperDeviceProfile.Xbox, mapped)) == item.Item2,
                "actual Nintendo FD2 A/B/X/Y reaches same-letter Xbox wire bit");
        }

        var disabled = new FaceButtonMapping();
        var neutral = GamepadState.Neutral();
        check(ReferenceEquals(disabled.Apply(neutral), neutral) &&
            ReferenceEquals(new FaceButtonMapping(true, true).Apply(neutral), neutral), "neutral/unchanged input allocates no clone");
        var legacy = JsonSerializer.Deserialize<V60UserSettings>("{}")!;
        check(!legacy.SwapABButtons && !legacy.SwapXYButtons, "old settings preserve existing positional mapping");
        var settings = JsonSerializer.Deserialize<V60UserSettings>(JsonSerializer.Serialize(new V60UserSettings
            { SwapABButtons = true, SwapXYButtons = true, RumbleMultiplier = .9 }))!;
        check(settings.SwapABButtons && settings.SwapXYButtons && settings.RumbleMultiplier == .9,
            "mapping settings roundtrip independently of other saved options");
    }
}
