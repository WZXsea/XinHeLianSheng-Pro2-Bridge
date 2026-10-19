using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class Pro2UsbRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        byte[] report = new byte[64]; report[0] = 5;
        check(Pro2UsbProtocol.IsInputReport(report), "USB accepts exact 64-byte common HID reports");
        foreach (int size in new[] { 0, 1, 60, 63, 65 })
            check(!Pro2UsbProtocol.IsInputReport(new byte[size]), "USB refuses truncated/padded input " + size);
        report[0] = 9;
        check(!Pro2UsbProtocol.IsInputReport(report), "USB ignores old primary reports until common-report selection takes effect");
        var commands = Pro2UsbProtocol.StartupCommands();
        check(commands.Length == 5 && commands.All(c => c[1] == 0x91 && c[2] == 0 && c.Length == c[5] + 8) &&
            commands.All(c => c[0] is 3 or 12 || c[0]==10 && c[3]==8) && commands[^1][3] == 0x0a && commands[^1][8] == 5,
            "USB initialization has USB headers and selects common input without pairing/flash writes");
        byte[] raw = new byte[64]; raw[0] = 2; raw[1] = 0x50;
        check(Pro2UsbProtocol.TryEncodeRumble(raw, 2, 0, out var neutral, out _) && neutral.Length == 64 &&
            neutral[0] == 2 && neutral[1] == 0x52 && neutral[17] == 0x52,
            "USB motor output has physical report id and correct stereo block positions");
        raw[2] = 0xff; raw[3] = 0xff; raw[4] = 0xff; raw[5] = 0xff; raw[6] = 0xff;
        Array.Copy(raw, 2, raw, 18, 5);
        check(Pro2UsbProtocol.TryEncodeRumble(raw, 15, 3, out var strong, out _), "USB rumble uses the existing raw02 gain conversion");
        for (int motor = 0; motor < 2; motor++) for (int frame = 0; frame < 3; frame++)
        {
            int offset = 2 + motor * 16 + frame * 5; ulong bits = 0;
            for (int i = 0; i < 5; i++) bits |= (ulong)strong[offset + i] << (8 * i);
            check(((bits >> 10) & 1023) + ((bits >> 30) & 1023) <= 511, "USB motor amplitude bounded " + motor + "/" + frame);
        }
        check(!Pro2UsbProtocol.TryEncodeRumble(new byte[63], 0, 1, out _, out _), "USB refuses malformed rumble");
        var settings = JsonSerializer.Deserialize<V60UserSettings>("{}")!;
        check(settings.InputTransport == Pro2InputTransport.Auto, "legacy settings default to automatic USB/BLE selection");
        settings.InputTransport = Pro2InputTransport.Usb;
        check(JsonSerializer.Deserialize<V60UserSettings>(JsonSerializer.Serialize(settings))!.InputTransport == Pro2InputTransport.Usb,
            "USB-only selection persists");

        var source = new Pro2BleInputSource();
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string key, object value) => typeof(Pro2BleInputSource).GetField(key, flags)!.SetValue(source, value);
            Set("usbActive", true); Set("transportConnected", true);
            typeof(Pro2BleInputSource).GetProperty(nameof(Pro2BleInputSource.IsRunning))!.SetValue(source, true);
            source.BeginControllerTest();
            var payload = new byte[63];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x08); // physical Nintendo A
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(52), 4096);
            typeof(Pro2BleInputSource).GetMethod("ProcessInputPayload", flags)!.Invoke(source,
                [payload, true, false, Guid.Empty, (ushort)0, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow]);
            check(source.IsUsb && source.TransportLabel == "USB" && !source.CanOptimizeConnectionCompatibility,
                "USB input cannot trigger BLE parameter optimization");
            var snapshot = source.GetControllerTestSnapshot();
            check(snapshot.Fresh && snapshot.Seen.HasFlag(GamepadButtons.East) && snapshot.State.IsPressed(GamepadButtons.East),
                "USB uses the shared physical parser and per-report test markers");
            check(source.TryGetNextFresh(out var state, out _) &&
                new FaceButtonMapping(true, true).Apply(state).IsPressed(GamepadButtons.South),
                "USB fresh queue feeds the existing same-letter ABXY mapping");
            check(!source.TryGetNextFresh(out _, out _), "USB never manufactures repeated source frames");
            source.StopAsync().GetAwaiter().GetResult();
            check(!source.IsUsb && !source.IsRunning && !source.IsTransportConnected && !source.TryGetLatest(out _, out _),
                "USB stop clears current state and transport identity");
        }
        finally { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
