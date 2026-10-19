// Port of Pro2BleRumblePacketEncoder.cs, Pro2UsbProtocol.TryEncodeRumble,
// and Pro2OutputPacketMapper.BuildOrdinaryPacket preview formulas,
// Y700Switch2V60Viiper (Apache-2.0). See repository LICENSE.
import Foundation

public struct Pro2RumblePacket: Sendable, Equatable {
    public let bytes: [UInt8]
    public let active: Bool
}
public enum Pro2RumbleEncoder {
    /// Converts a 64-byte Switch 2 raw report 0x02 into a 33-byte GATT value.
    public static func ble(raw02: [UInt8], packetID: UInt8, gain: Double = 1) throws -> Pro2RumblePacket {
        guard raw02.count == 64 else { throw Pro2ProtocolError.invalidLength(expectedAtLeast: 64, actual: raw02.count) }
        guard raw02[0] == 2 else { throw Pro2ProtocolError.invalidReportID }
        guard raw02[1] & 0xf0 == 0x50 else { throw Pro2ProtocolError.invalidRumbleBlock }
        let g = clampGain(gain)
        let neutral: [UInt8] = [0x87,0x01,0x20,0x11,0]
        let bothNeutral = Array(raw02[2..<7]) == neutral && Array(raw02[18..<23]) == neutral
        let active = g > 0 && (hasEffect(raw02,2) || hasEffect(raw02,18))
        let zero = packed(lowFrequency: 0xe1, lowAmplitude: 0, highFrequency: 0x1e1, highAmplitude: 0)
        var output = [UInt8](repeating: 0, count: 33)
        for motor in 0..<2 {
            let o = 1 + motor * 16
            output[o] = 0x50 | (packetID & 15)
            let first = active && !bothNeutral ? convert(raw02,2+motor*16,g) : zero
            output.replaceSubrange(o+1..<o+6, with: first)
            output.replaceSubrange(o+6..<o+11, with: zero)
            output.replaceSubrange(o+11..<o+16, with: zero)
        }
        return Pro2RumblePacket(bytes: output, active: active)
    }
    /// Physical USB report 0x02, padded to 64 bytes; per-frame amplitude sum <=511.
    public static func usb(raw02: [UInt8], packetID: UInt8, gain: Double = 1) throws -> Pro2RumblePacket {
        let encoded = try ble(raw02: raw02, packetID: packetID)
        let g = clampGain(gain)
        var output = encoded.bytes + [UInt8](repeating: 0, count: 31)
        output[0] = 2
        for motor in 0..<2 {
            for frame in 0..<3 {
                let o = 2 + motor * 16 + frame * 5
                var value: UInt64 = 0
                for i in 0..<5 { value |= UInt64(output[o+i]) << (8*i) }
                var low = UInt64((Double((value >> 10) & 1023) * 511 / 2046 * g).rounded(.toNearestOrEven))
                var high = UInt64((Double((value >> 30) & 1023) * 511 / 2046 * g).rounded(.toNearestOrEven))
                if low + high > 511 {
                    let total = low + high
                    low = low * 511 / total; high = high * 511 / total
                }
                value = (value & ~((UInt64(1023) << 10) | (UInt64(1023) << 30))) | low << 10 | high << 30
                for i in 0..<5 { output[o+i] = UInt8(truncatingIfNeeded: value >> (8*i)) }
            }
        }
        return Pro2RumblePacket(bytes: output, active: encoded.active && g > 0)
    }
    /// Matches Pro2OutputPacketMapper.BuildOrdinaryPacket(40,96,"slider-preview").Report.
    /// Both motors receive the same bounded preview; callers schedule its stop separately.
    public static func previewRaw02() -> [UInt8] {
        let weak = 40, strong = 96
        // Source integer rounding precedes the original 64-count amplitude quantization.
        let lowAmplitude = ((strong * 29000 + 127) / 255) & 0xffc0
        let highAmplitude = ((weak * 29000 + 127) / 255) & 0xffc0
        let lowFrequency = 0x0b8 + ((0x122 - 0x0b8) * strong + 127) / 255
        let highFrequency = 0x160 + ((0x1f0 - 0x160) * weak + 127) / 255
        let frame: [UInt8] = [
            UInt8(highFrequency & 0xff),
            UInt8(((highFrequency >> 8) & 3) | ((highAmplitude >> 4) & 0xfc)),
            UInt8(((highAmplitude >> 12) & 15) | ((lowFrequency & 15) << 4)),
            UInt8(((lowFrequency >> 4) & 0x3f) | (lowAmplitude & 0xc0)),
            UInt8((lowAmplitude >> 8) & 0xff)
        ]
        var report = [UInt8](repeating: 0, count: 64)
        report[0] = 2; report[1] = 0x50; report[17] = 0x50
        report.replaceSubrange(2..<7, with: frame)
        report.replaceSubrange(18..<23, with: frame)
        return report
    }
    public static func neutralRaw02() -> [UInt8] {
        var report = [UInt8](repeating: 0, count: 64)
        report[0] = 2; report[1] = 0x50
        report.replaceSubrange(2..<7, with: [0x87,1,0x20,0x11,0])
        report.replaceSubrange(18..<23, with: [0x87,1,0x20,0x11,0])
        return report
    }
    private static func clampGain(_ gain: Double) -> Double { gain.isFinite ? min(3,max(0,gain)) : 1 }
    private static func hasEffect(_ b: [UInt8], _ o: Int) -> Bool {
        ((Int(b[o+1] & 0xfc) << 4) | (Int(b[o+2] & 15) << 12)) != 0 || ((Int(b[o+3] & 0xc0)) | Int(b[o+4]) << 8) != 0
    }
    private static func convert(_ b: [UInt8], _ o: Int, _ gain: Double) -> [UInt8] {
        let highFrequency = Int(b[o]) | Int(b[o+1] & 3) << 8
        let highAmplitude = Int(b[o+1] & 0xfc) << 4 | Int(b[o+2] & 15) << 12
        let lowFrequency = Int(b[o+2] >> 4) | Int(b[o+3] & 0x3f) << 4
        let lowAmplitude = Int(b[o+3] & 0xc0) | Int(b[o+4]) << 8
        func mapped(_ value: Int) -> Int { min(1023,max(0,Int((Double(value)*1023*gain/29000).rounded(.toNearestOrEven)))) }
        return packed(lowFrequency: lowFrequency, lowAmplitude: mapped(lowAmplitude), highFrequency: highFrequency, highAmplitude: mapped(highAmplitude))
    }
    private static func packed(lowFrequency: Int, lowAmplitude: Int, highFrequency: Int, highAmplitude: Int) -> [UInt8] {
        let value = UInt64(lowFrequency & 511) | UInt64(lowAmplitude & 1023) << 10 | UInt64(highFrequency & 511) << 20 | UInt64(highAmplitude & 1023) << 30
        return (0..<5).map { UInt8(truncatingIfNeeded: value >> (8*$0)) }
    }
}
