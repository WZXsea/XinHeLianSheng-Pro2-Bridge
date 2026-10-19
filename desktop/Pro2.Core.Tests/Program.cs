using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Y700Switch2V60Viiper;

internal static class Program
{
    private static int assertions, groups;
    private static void Check(bool success, string message)
    {
        assertions++;
        if (!success) throw new InvalidOperationException(message);
    }
    private static long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
    private static GamepadState State(GamepadButtons buttons = GamepadButtons.None) => new()
    {
        Buttons = buttons, SourceTimestampTicks = 123, MotionTimestampUs = 456, Updates = 7,
        AccelValid = true, GyroValid = true, AccelZ = 4096, GyroX = 9
    };
    private static void Run(string name, Action test)
    {
        test(); groups++; Console.WriteLine("PASS " + name);
    }
    public static int Main()
    {
        string output = Path.Combine(Path.GetTempPath(), "pro2-shared-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            Run("original face-button regressions", () => FaceButtonMappingRegressionTests.Run(Check));
            Run("original gyro/haptic quality regressions", () => QualityRegressionTests.Run(Check, output));
            Run("all FD2 buttons and six virtual report formats", ParserAndVirtualFormats);
            Run("noncascading overrides, trigger chords and mode isolation", MappingOverrides);
            Run("rear-key hold/tap/repeat/sequence and cancellation", RearMacros);
            Run("direction conflicts and preserved independent physical controls", DirectionSafety);
            Run("source freshness cancels delayed macros", Freshness);
            Run("shared stick center and complete-travel calibration", StickCalibration);
            Run("stability guard holds isolated corruption and follows real motion", StabilityGuard);
            Run("settings JSON compatibility and path normalization", SettingsRoundTrip);
            Run("raw rumble to BLE encoding and neutral release", RumbleWire);
            Run("input queue preserves button edges", QueueEdges);
            Console.WriteLine($"Passed {groups} shared-core test groups / {assertions} assertions on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(output, recursive: true); }
    }
    private static byte[] Payload(ushort lx = 2048, ushort ly = 2048, ushort rx = 2048, ushort ry = 2048)
    {
        var report = new byte[63];
        static void Pack(byte[] target, int offset, ushort x, ushort y)
        {
            target[offset] = (byte)x; target[offset+1] = (byte)((x >> 8) | ((y & 15) << 4)); target[offset+2] = (byte)(y >> 4);
        }
        Pack(report, 10, lx, ly); Pack(report, 13, rx, ry);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(52),4096);
        return report;
    }
    private static void ParserAndVirtualFormats()
    {
        (uint Physical, GamepadButtons Logical)[] buttons = [
            (1u<<0,GamepadButtons.West),(1u<<1,GamepadButtons.North),(1u<<2,GamepadButtons.South),(1u<<3,GamepadButtons.East),
            (1u<<6,GamepadButtons.R1),(1u<<7,GamepadButtons.R2),(1u<<8,GamepadButtons.Back),(1u<<9,GamepadButtons.Start),
            (1u<<10,GamepadButtons.RightStick),(1u<<11,GamepadButtons.LeftStick),(1u<<12,GamepadButtons.Home),(1u<<13,GamepadButtons.Capture),
            (1u<<14,GamepadButtons.Aux),(1u<<16,GamepadButtons.DPadDown),(1u<<17,GamepadButtons.DPadUp),(1u<<18,GamepadButtons.DPadRight),(1u<<19,GamepadButtons.DPadLeft),
            (1u<<22,GamepadButtons.L1),(1u<<23,GamepadButtons.L2),(1u<<24,GamepadButtons.PaddleRight),(1u<<25,GamepadButtons.PaddleLeft)];
        foreach (var (physical,logical) in buttons)
        {
            var payload = Payload(); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),physical);
            var parser = new Pro2HidReportParser(true);
            Check(parser.TryParseFd2Payload(payload,out var state,out _),"real FD2 fixture parses");
            Check(state.Buttons==logical,"physical bit converts to same logical control " + logical);
        }
        foreach (var profile in new[] {ViiperDeviceProfile.Xbox,ViiperDeviceProfile.DualSenseLike,ViiperDeviceProfile.Pro1,ViiperDeviceProfile.Pro2,ViiperDeviceProfile.XboxProfessionalImuTest,ViiperDeviceProfile.DualSenseProfessionalImuTest})
        {
            Check(VirtualPadPackets.FromGamepad(profile,State()).Length==profile.InputSize,"virtual packet input length " + profile.Mode);
            Check(VirtualPadPackets.NeutralInput(profile).Length==profile.InputSize,"neutral packet input length " + profile.Mode);
        }
        var invalid = Payload(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(4),0x20);
        Check(!new Pro2HidReportParser(true).TryParseFd2Payload(invalid,out _,out _),"reserved FD2 bit rejected");
    }
    private static void MappingOverrides()
    {
        var mappings = new ButtonRemappingConfiguration
        {
            Xbox = [new(GamepadButtons.South,GamepadButtons.West|GamepadButtons.L2),new(GamepadButtons.West,GamepadButtons.North),new(GamepadButtons.East,GamepadButtons.None)],
            Ps5 = [new(GamepadButtons.South,GamepadButtons.East)]
        }.Normalize();
        var mapping = new FaceButtonMapping(true,true,mappings);
        var physical = State(GamepadButtons.South|GamepadButtons.East|GamepadButtons.PaddleLeft);
        var output = mapping.Apply(physical,ViiperVirtualMode.Xbox);
        Check(output.Buttons==(GamepadButtons.West|GamepadButtons.L2|GamepadButtons.PaddleLeft),"standard mapping emits direct targets and preserves rear controls");
        Check(output.L2==4095 && physical.L2==0,"chord target raises trigger only in output copy");
        Check(mapping.Apply(State(GamepadButtons.South),ViiperVirtualMode.Pro2).Buttons==GamepadButtons.East,"missing mode override inherits AB layout");
        Check(mapping.Apply(State(GamepadButtons.South),ViiperVirtualMode.DualSenseLike).Buttons==GamepadButtons.East,"PS5 keeps independent profile");
        var duplicate = new ButtonRemappingConfiguration { Xbox=[new(GamepadButtons.South,GamepadButtons.East),new(GamepadButtons.South,GamepadButtons.North)] }.Normalize();
        Check(duplicate.Xbox.Length==1 && duplicate.Xbox[0].Target==GamepadButtons.North,"last explicit source override wins normalization");
    }
    private static RearButtonConfiguration Config(RearButtonBinding left,RearButtonBinding? right=null) => new RearButtonConfiguration
    { Xbox = new(){Left=left,Right=right??new()}, Pro2 = new(){Left=left,Right=right??new()} }.Normalize();
    private static void RearMacros()
    {
        foreach (var action in new[]{RearButtonAction.Hold,RearButtonAction.Tap,RearButtonAction.Repeat,RearButtonAction.Sequence})
        {
            var mapper = new RearButtonMapper(); var config=Config(new(){Enabled=true,Action=action,Buttons=GamepadButtons.South,DelayMs=30,PressMs=30,GapMs=30,RepeatCount=2,Steps=[new(){Buttons=GamepadButtons.East,DelayMs=30,PressMs=30}]});
            mapper.Apply(State(),ViiperVirtualMode.Xbox,config,Tick(0));
            GamepadState Frame(double ms,bool pressed=true) => mapper.Apply(State(pressed?GamepadButtons.PaddleLeft:GamepadButtons.None),ViiperVirtualMode.Xbox,config,Tick(ms));
            Check(Frame(7.5).Buttons==0,"macro initial delay " + action); Check(Frame(38).IsPressed(GamepadButtons.South),"macro first press " + action);
            var after = Frame(68);
            if(action==RearButtonAction.Hold) Check(after.IsPressed(GamepadButtons.South) && Frame(75,false).Buttons==0,"hold releases with physical key");
            else Check(after.Buttons==0,"timed macro releases its first pulse " + action);
            if(action==RearButtonAction.Repeat) Check(Frame(98).IsPressed(GamepadButtons.South),"repeat second pulse");
            if(action==RearButtonAction.Sequence) Check(Frame(98).IsPressed(GamepadButtons.East),"sequence emits independent second target");
        }
        var hold=Config(new(){Enabled=true,Buttons=GamepadButtons.South}); var engine=new RearButtonMapper();
        Check(engine.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,hold,Tick(0)).Buttons==0,"session start held key waits for release");
        engine.Apply(State(),ViiperVirtualMode.Xbox,hold,Tick(4));
        engine.Apply(State(GamepadButtons.PaddleLeft|GamepadButtons.South),ViiperVirtualMode.Xbox,hold,Tick(8));
        Check(engine.TryCancelOutput(out var release) && release.Buttons==GamepadButtons.South,"cancel preserves separately pressed physical key");
    }
    private static void DirectionSafety()
    {
        var config=Config(new(){Enabled=true,Buttons=GamepadButtons.DPadUp},new(){Enabled=true,Buttons=GamepadButtons.DPadDown});
        var mapper=new RearButtonMapper();mapper.Apply(State(),ViiperVirtualMode.Xbox,config,Tick(0));
        Check(mapper.Apply(State(GamepadButtons.PaddleLeft|GamepadButtons.PaddleRight),ViiperVirtualMode.Xbox,config,Tick(4)).Buttons==0,"opposing rear macros neutralize");
        Check(mapper.Apply(State(GamepadButtons.PaddleLeft|GamepadButtons.DPadDown),ViiperVirtualMode.Xbox,config,Tick(8)).Buttons==GamepadButtons.DPadDown,"physical direction wins opposing macro");
    }
    private static void Freshness()
    {
        var config=Config(new(){Enabled=true,Action=RearButtonAction.Sequence,Buttons=GamepadButtons.South,PressMs=500});
        var mapper=new RearButtonMapper();mapper.Apply(State(),ViiperVirtualMode.Xbox,config,Tick(0));
        Check(mapper.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,config,Tick(4)).IsPressed(GamepadButtons.South),"sequence begins on fresh physical edge");
        Check(mapper.Apply(State(),ViiperVirtualMode.Xbox,config,Tick(200)).Buttons==0,"stale interval cancels pending action without catch-up");
        var repeat=Config(new(){Enabled=true,Action=RearButtonAction.Repeat,Buttons=GamepadButtons.South,PressMs=80,GapMs=80});
        mapper.Reset();mapper.Apply(State(),ViiperVirtualMode.Xbox,repeat,Tick(0));mapper.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,repeat,Tick(10));mapper.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,repeat,Tick(80));
        Check(mapper.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,repeat,Tick(175)).Buttons==0,"missed repeat gap exposes a release frame");
        Check(mapper.Apply(State(GamepadButtons.PaddleLeft),ViiperVirtualMode.Xbox,repeat,Tick(179)).IsPressed(GamepadButtons.South),"repeat resumes after real release frame");
    }
    private static void StickCalibration()
    {
        var parser=new Pro2HidReportParser(true);parser.StartManualStickCenterCalibration();
        for(int i=0;i<20;i++)Check(parser.TryParseFd2Payload(Payload(2100,2050,2000,2040),out _,out _),"center capture accepts original raw report");
        var center=parser.CompleteManualStickCenterCalibration(); Check(center.Success && center.Profile?.CenterLx==2100,"center captures measured zero");
        parser.StartManualStickRangeCalibration();
        for(int i=0;i<60;i++) { int delta=i%2==0?-1100:1100;parser.TryParseFd2Payload(Payload((ushort)(2100+delta),(ushort)(2050+delta),(ushort)(2000+delta),(ushort)(2040+delta)),out _,out _); }
        var range=parser.CompleteManualStickRangeCalibration();Check(range.Success && range.Profile!.RangeCalibrated,"all four directional ranges captured");
        parser.TryParseFd2Payload(Payload(3200,3150,3100,3140),out var maximum,out _);Check(maximum.Lx==4095 && maximum.Ry==4095,"calibrated endpoint reaches full scale");
        parser.StartManualStickCenterCalibration();for(int i=0;i<20;i++)parser.TryParseFd2Payload(Payload(i%2==0?(ushort)1800:(ushort)2300),out _,out _);
        Check(!parser.CompleteManualStickCenterCalibration().Success && parser.StickCalibrationProfile.CenterLx==2100,"moving center capture retains previous profile");
    }
    private static void StabilityGuard()
    {
        var filter=new Pro2InputStabilityFilter();var initial=State();filter.Process(initial,Tick(1));
        var corrupt=State();corrupt.Lx=4095;
        var held=filter.Process(corrupt,Tick(5));Check(held.AcceptedState.Lx==2048 && held.HasHoldOrReject,"isolated idle spike held");
        var returned=filter.Process(State(),Tick(9));Check(returned.AcceptedState.Lx==2048 && returned.Events.Any(e=>e.Decision==Pro2AxisFilterDecisionKind.Reject),"return to good identifies isolated corruption");
        filter.Reset();filter.Process(State(),Tick(1));var fast=State();fast.Lx=3500;filter.Process(fast,Tick(5));
        var confirmed=filter.Process(fast,Tick(9));Check(confirmed.AcceptedState.Lx==3500,"second consistent frame follows actual stick travel");
        var reverse=State();reverse.Lx=500;Check(filter.Process(reverse,Tick(13)).AcceptedState.Lx==500,"active fast reversal remains responsive");
        filter.Reset();filter.Process(State(),Tick(1));Check(filter.Process(fast,Tick(100)).AcceptedState.Lx==3500,"link recovery follows newest state");
        Check(corrupt.Lx==4095 && corrupt.GyroX==9,"filter leaves original physical observation intact");
    }
    private static void SettingsRoundTrip()
    {
        var original=new V60UserSettings { SwapABButtons=true,SwapXYButtons=true,InputTransport=Pro2InputTransport.Usb,RumbleMultiplier=1.7,SelectedModeKey="pro2",RearButtons=Config(new(){Enabled=true,Action=RearButtonAction.Sequence,Buttons=GamepadButtons.East,Steps=[new(){Buttons=GamepadButtons.R1,DelayMs=30,PressMs=60}]}) };
        string serialized=JsonSerializer.Serialize(original);var loaded=JsonSerializer.Deserialize<V60UserSettings>(serialized)!;
        Check(loaded.SwapABButtons && loaded.SwapXYButtons && loaded.RumbleMultiplier==1.7 && loaded.InputTransport==Pro2InputTransport.Usb,"original settings JSON retains platform-neutral values");
        Check(loaded.RearButtons.Pro2.Left.Steps[0].Buttons==GamepadButtons.R1,"Pro2 macro step round-trip");
        var profile=new Pro2StickCalibrationProfile{CenterCalibrated=true,CenterLx=2100};loaded.SetStickCalibration("aa-bb",profile);
        Check(loaded.TryGetStickCalibration("AA:BB",out var saved) && saved.CenterLx==2100,"per-device calibration address normalization");
        Check(V60UserSettings.NormalizeRumbleMultiplier(double.NaN)==1 && V60UserSettings.NormalizeRumbleMultiplier(8)==3,"rumble gain safely normalizes original settings");
        string path=Path.Combine(Path.GetTempPath(),"some folder","usbip.exe");
        Check(SettingsPathNormalization.NormalizeUsbipExecutablePath("\""+path+"\"")==Path.GetFullPath(path),"quoted absolute usbip path normalization");
        Check(SettingsPathNormalization.NormalizeUsbipExecutablePath("usbip.exe")=="" && SettingsPathNormalization.NormalizeUsbipExecutablePath(Path.Combine(Path.GetTempPath(),"other.exe"))=="","relative and other executable settings stay empty");
    }
    private static void RumbleWire()
    {
        var ordinary=Pro2OutputPacketMapper.BuildOrdinaryPacket(40,96,"cross-platform-test");
        Check(ordinary.Report.Length==64 && ordinary.Report[0]==2,"ordinary game rumble produces original USB report");
        var packet=new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
        Check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(ordinary.Report,7,packet,out var active,out _) && active,"same USB rumble converts to real BLE report");
        Check(Pro2BleRumblePacketEncoder.TryEncodeRaw02(ordinary.Report,8,packet,out var muted,out _,0) && !muted,"zero gain emits neutral BLE command");
        Check(!Pro2BleRumblePacketEncoder.TryEncodeRaw02(new byte[3],0,packet,out _,out _),"malformed rumble report rejected");
    }
    private static void QueueEdges()
    {
        var queue=new Pro2SequentialInputQueue();queue.Enqueue(State(),Tick(1));queue.Enqueue(State(),Tick(2));queue.Enqueue(State(GamepadButtons.South),Tick(3));queue.Enqueue(State(),Tick(4));
        Check(queue.TryDequeueRealtimeEdges(out var neutral,out _,out var skipped) && neutral.Buttons==0 && skipped==1,"same-controls frames coalesce");
        Check(queue.TryDequeueRealtimeEdges(out var pressed,out _,out _) && pressed.IsPressed(GamepadButtons.South),"press edge preserved");
        Check(queue.TryDequeueRealtimeEdges(out var released,out _,out _) && released.Buttons==0,"release edge preserved");
    }
}
