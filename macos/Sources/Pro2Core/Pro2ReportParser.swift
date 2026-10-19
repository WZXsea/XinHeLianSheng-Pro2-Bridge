// Swift adaptation of Pro2HidReportParser.cs, Pro2BatteryStatus.cs and
// Pro2ChargeStatus.cs from Y700Switch2V60Viiper, licensed Apache-2.0.
import Foundation

public struct Pro2ReportParser: Sendable {
    public init() {}
    /// Explicit transport format keeps primary, legacy and FD2 button layouts distinct.
    /// Each call is independent. Axes use the source default center/deadzone/range;
    /// raw values remain available for later per-device calibration.
    public func parse(_ data: Data, format: Pro2InputFormat) throws -> Pro2State {
        try parse(Array(data), format: format)
    }
    public func parse(_ input: [UInt8], format: Pro2InputFormat) throws -> Pro2State {
        var b = input
        var state = Pro2State()
        if format == .usb {
            guard b.count == 64 else { throw Pro2ProtocolError.invalidLength(expectedAtLeast: 64, actual: b.count) }
            guard b[0] == 5 else { throw Pro2ProtocolError.invalidReportID }
            b.removeFirst()
        }
        let minimum = (format == .fd2 || format == .usb) ? 60 : (format == .standard ? 13 : 11)
        guard b.count >= minimum else { throw Pro2ProtocolError.invalidLength(expectedAtLeast: minimum, actual: b.count) }
        let stickOffset: Int
        switch format {
        case .usb, .fd2:
            let bits = u32(b, 4)
            guard bits & 0xEC300020 == 0 else { throw Pro2ProtocolError.reservedButtonBits }
            state.buttons = Pro2Buttons(rawValue: bits & 0x03CF7FCF)
            state.headsetPresent = bits & 0x10000000 != 0
            state.battery.voltageMillivolts = u16(b, 31)
            let code = b[33]
            if code == 0 || code == 0x20 || (0x30...0x3f).contains(code) {
                state.battery.charging = (0x30...0x3f).contains(code)
                state.battery.externalPower = code != 0
            }
            state.motionTimestampMicroseconds = u32(b, 42)
            state.motionSamples = samples(b, offset: 48, maximum: 3)
            stickOffset = 10
        case .primary:
            let mapping: [(Int, Int, Pro2Buttons)] = [
                (2,0,.south),(2,1,.east),(2,2,.west),(2,3,.north),(2,4,.r1),(2,5,.r2),(2,6,.start),(2,7,.rightStick),
                (3,0,.dpadDown),(3,1,.dpadRight),(3,2,.dpadLeft),(3,3,.dpadUp),(3,4,.l1),(3,5,.l2),(3,6,.back),(3,7,.leftStick),
                (4,0,.home),(4,1,.capture),(4,2,.paddleRight),(4,3,.paddleLeft),(4,4,.aux)]
            for (offset, bit, button) in mapping where b[offset] & (1 << bit) != 0 { state.buttons.insert(button) }
            let power = b[1], level = (power >> 2) & 15
            state.battery.level = level <= 9 ? level : nil
            state.battery.externalPower = power & 1 != 0
            state.battery.charging = power & 2 != 0
            stickOffset = 5
        case .standard:
            guard [0x30,0x31,0x21,0x23].contains(b[0]) else { throw Pro2ProtocolError.invalidReportID }
            state.buttons = standardButtons(b[3], b[4], b[5])
            state.motionSamples = samples(b, offset: 13, maximum: b.count >= 49 ? 3 : 1)
            stickOffset = 6
        case .legacy:
            guard ![0x30,0x31,0x21,0x23].contains(b[0]) else { throw Pro2ProtocolError.invalidReportID }
            state.buttons = standardButtons(b[2], b[3], b[4])
            stickOffset = 5
        }
        let left = unpack(b, stickOffset), right = unpack(b, stickOffset + 3)
        state.rawAxes = Pro2Axes(lx: left.0, ly: left.1, rx: right.0, ry: right.1)
        state.axes = Pro2Axes(lx: normalize(left.0), ly: normalize(left.1), rx: normalize(right.0), ry: normalize(right.1))
        return state
    }
    private func standardButtons(_ right: UInt8, _ shared: UInt8, _ left: UInt8) -> Pro2Buttons {
        Pro2Buttons(rawValue: (UInt32(right) | UInt32(shared & 0x3f) << 8 | UInt32(left) << 16) & 0x00CF3FCF)
    }
    private func unpack(_ b: [UInt8], _ o: Int) -> (UInt16, UInt16) {
        (UInt16(b[o]) | UInt16(b[o+1] & 15) << 8, UInt16(b[o+1] >> 4) | UInt16(b[o+2]) << 4)
    }
    private func normalize(_ raw: UInt16) -> UInt16 {
        let delta = Int(raw) - 2048, magnitude = abs(delta)
        guard magnitude > 64 else { return 2048 }
        let target = delta < 0 ? 2048 : 2047
        let scaled = min(target, ((magnitude - 64) * target + 768) / 1536)
        return UInt16(delta < 0 ? 2048 - scaled : 2048 + scaled)
    }
    private func samples(_ b: [UInt8], offset: Int, maximum: Int) -> [Pro2MotionSample] {
        guard b.count >= offset + 12 else { return [] }
        return (0..<min(maximum, (b.count-offset)/12)).map { i in
            let o = offset + i * 12
            return Pro2MotionSample(accelX: Int16(bitPattern: u16(b,o)), accelY: Int16(bitPattern: u16(b,o+2)), accelZ: Int16(bitPattern: u16(b,o+4)), gyroX: Int16(bitPattern: u16(b,o+6)), gyroY: Int16(bitPattern: u16(b,o+8)), gyroZ: Int16(bitPattern: u16(b,o+10)))
        }
    }
    private func u16(_ b: [UInt8], _ o: Int) -> UInt16 { UInt16(b[o]) | UInt16(b[o+1]) << 8 }
    private func u32(_ b: [UInt8], _ o: Int) -> UInt32 { UInt32(b[o]) | UInt32(b[o+1]) << 8 | UInt32(b[o+2]) << 16 | UInt32(b[o+3]) << 24 }
}
