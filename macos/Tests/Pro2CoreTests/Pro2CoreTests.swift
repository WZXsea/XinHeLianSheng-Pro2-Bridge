import XCTest
@testable import Pro2Core
import Pro2Output

final class Pro2CoreTests: XCTestCase {
    private let parser = Pro2ReportParser()
    private func fd2() -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 63)
        b.replaceSubrange(10..<16, with: [0,0x08,0x80,0,0x08,0x80])
        return b
    }
    func testFD2AndUSBUseSameControlsWithHeadsetAndRearButtons() throws {
        var b = fd2()
        b[4] = 0x84; b[5] = 0x40; b[6] = 0x82; b[7] = 0x13
        b[31] = 0x10; b[32] = 0x0f; b[33] = 0x31; b[34] = 0xff
        b[42] = 0x78; b[43] = 0x56; b[44] = 0x34; b[45] = 0x12
        b[48] = 0xff; b[49] = 0xff; b[54] = 0; b[55] = 0x80
        let state = try parser.parse(b, format: .fd2)
        XCTAssertEqual(state, try parser.parse([5] + b, format: .usb))
        XCTAssertEqual(state.buttons, [.south,.r2,.aux,.dpadUp,.l2,.paddleLeft,.paddleRight])
        XCTAssertEqual(state.headsetPresent, true)
        XCTAssertEqual(state.rawAxes, Pro2Axes())
        XCTAssertEqual(state.axes, Pro2Axes())
        XCTAssertEqual(state.battery.voltageMillivolts, 3856)
        XCTAssertNil(state.battery.level) // Counter bytes supply no SOC.
        XCTAssertEqual(state.battery.charging, true) // byte 34 is independent.
        XCTAssertEqual(state.battery.externalPower, true)
        XCTAssertEqual(state.motionTimestampMicroseconds, 0x12345678)
        XCTAssertEqual(state.motionSamples.count, 1)
        XCTAssertEqual(state.motionSamples[0].accelX, -1)
        XCTAssertEqual(state.motionSamples[0].gyroX, .min)
        XCTAssertEqual(state.leftTrigger, 1)
        XCTAssertEqual(state.rightTrigger, 1)
    }
    func testEveryFD2ButtonAndReservedBit() throws {
        let supported = [0,1,2,3,6,7,8,9,10,11,12,13,14,16,17,18,19,22,23,24,25]
        for bit in supported {
            var b = fd2(); b[4+bit/8] = 1 << (bit%8)
            XCTAssertEqual(try parser.parse(b, format: .fd2).buttons.rawValue, UInt32(1) << bit)
        }
        for bit in [5,20,21,26,27,29,30,31] {
            var b = fd2(); b[4+bit/8] = 1 << (bit%8)
            XCTAssertThrowsError(try parser.parse(b, format: .fd2))
        }
    }
    func testPrimaryLayoutAndNineLevelPowerInfo() throws {
        var b = [UInt8](repeating: 0, count: 11)
        b[1] = 0x1c; b[2] = 1; b[3] = 8; b[4] = 0x1c
        b.replaceSubrange(5..<11, with: [0,8,128,0,8,128])
        let s = try parser.parse(b, format: .primary)
        XCTAssertEqual(s.buttons, [.south,.dpadUp,.paddleRight,.paddleLeft,.aux])
        XCTAssertEqual(s.battery.level, 7)
        XCTAssertEqual(s.battery.approximatePercent, 78)
        XCTAssertEqual(s.battery.externalPower, false)
        b[1] = 0xff
        XCTAssertNil(try parser.parse(b, format: .primary).battery.level)
    }
    func testStandardAndLegacyLayoutsAndAxisEndpoints() throws {
        var b = [UInt8](repeating: 0, count: 49)
        b[0] = 0x30; b[3] = 4; b[4] = 0x10; b[5] = 0x40
        b.replaceSubrange(6..<12, with: [0,0,0,255,255,255])
        let s = try parser.parse(b, format: .standard)
        XCTAssertEqual(s.buttons, [.south,.home,.l1])
        XCTAssertEqual(s.axes, Pro2Axes(lx: 0, ly: 0, rx: 4095, ry: 4095))
        XCTAssertEqual(s.motionSamples.count, 3)
        var legacy = Array(b[1..<12]); legacy[0] = 0
        XCTAssertEqual(try parser.parse(legacy, format: .legacy).buttons, s.buttons)
    }
    func testMalformedAndTruncatedInput() throws {
        for format in Pro2InputFormat.allCases {
            let minimum = (format == .usb ? 64 : (format == .fd2 ? 60 : (format == .standard ? 13 : 11)))
            for count in 0..<minimum { XCTAssertThrowsError(try parser.parse([UInt8](repeating: 0, count: count), format: format)) }
        }
        XCTAssertThrowsError(try parser.parse([0]+fd2(), format: .usb))
        XCTAssertThrowsError(try parser.parse([5]+fd2()+[0], format: .usb))
        XCTAssertThrowsError(try parser.parse([UInt8](repeating: 0, count: 13), format: .standard))
        var b = fd2(); b[33] = 0x21
        XCTAssertNil(try parser.parse(b, format: .fd2).battery.charging)
    }
    func testInitializationMatchesUpstreamPolicy() {
        XCTAssertEqual(Pro2Protocol.bleStartupCommands.count, 14)
        XCTAssertEqual(Pro2Protocol.usbStartupCommands.count, 5)
        XCTAssertTrue(Pro2Protocol.bleStartupCommands.allSatisfy { $0[0] != 0x15 })
        XCTAssertEqual(Pro2Protocol.usbStartupCommands.last, Pro2Protocol.usbSelectInputReport(5))
        XCTAssertTrue(Pro2Protocol.ackMatches(command: [12,0x91,1,2,0,4,0,0], reply: [12,1,1,2,16,0x78,0,0]))
        XCTAssertFalse(Pro2Protocol.ackMatches(command: [12,0x91,1,2,0,4,0,0], reply: [12,1,1,4,16,0x78,0,0]))
        XCTAssertFalse(Pro2Protocol.ackMatches(command: [], reply: []))
    }
    func testGamepadOutputNeutralAndAxisDirection() {
        XCTAssertEqual(Pro2GamepadReport.encode(Pro2State()), [UInt8](repeating: 0, count: 11))
        var state = Pro2State()
        state.axes = Pro2Axes(lx: 0, ly: 4095, rx: 4095, ry: 0)
        XCTAssertEqual(Pro2GamepadReport.encode(state), [0,0,0,1,128,1,128,255,127,255,127])
    }
    func testGamepadOutputEveryButtonAndFaceSwap() {
        XCTAssertEqual(Pro2Controls.buttons.count, 21)
        for (index, control) in Pro2Controls.buttons.enumerated() {
            var state = Pro2State(); state.buttons = control.1
            let report = Pro2GamepadReport.encode(state)
            let bits = UInt32(report[0]) | UInt32(report[1]) << 8 | UInt32(report[2]) << 16
            XCTAssertEqual(bits, UInt32(1) << index)
        }
        var state = Pro2State(); state.buttons = [.south,.west,.l1,.aux]
        let normal = Pro2GamepadReport.encode(state)
        let swapped = Pro2GamepadReport.encode(state, swapABXY: true)
        XCTAssertEqual(normal[0], 0x15)
        XCTAssertEqual(swapped[0], 0x1a)
        XCTAssertEqual(swapped[2], normal[2])
        state.buttons = [.south,.east,.west,.north]
        XCTAssertEqual(Pro2GamepadReport.encode(state), Pro2GamepadReport.encode(state, swapABXY: true))
    }
    func testRumbleNeutralGoldenVectorAndPacketCounter() throws {
        let raw = Pro2RumbleEncoder.neutralRaw02()
        let packet = try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0xff)
        let zero: [UInt8] = [0xe1,0,0x10,0x1e,0]
        XCTAssertEqual(packet.bytes, [0] + [0x5f] + zero + zero + zero + [0x5f] + zero + zero + zero)
        XCTAssertFalse(packet.active)
        let usb = try Pro2RumbleEncoder.usb(raw02: raw, packetID: 0xff)
        XCTAssertEqual(usb.bytes.count, 64)
        XCTAssertEqual(Array(usb.bytes[1..<33]), Array(packet.bytes[1..<33]))
        XCTAssertEqual(usb.bytes[0], 2)
        XCTAssertEqual(Array(usb.bytes[33...]), [UInt8](repeating: 0, count: 31))
    }
    func testPreviewRaw02MatchesUpstreamOrdinaryPacketGoldenVector() throws {
        // Upstream integer formulas produce amplitudes 10918/4549 before
        // masking to 0xffc0, low frequency 224 and high frequency 375.
        let golden: [UInt8] = [
            2,80,119,29,1,142,42,0,0,0,0,0,0,0,0,0,
            0,80,119,29,1,142,42,0,0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
        ]
        let raw = Pro2RumbleEncoder.previewRaw02()
        XCTAssertEqual(raw, golden)
        XCTAssertTrue(try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0).active)
        var previous: UInt64 = 0
        for gain in [0.1,0.5,1,2,3] {
            let packet = try Pro2RumbleEncoder.usb(raw02: raw, packetID: 0, gain: gain)
            var value: UInt64 = 0
            for i in 0..<5 { value |= UInt64(packet.bytes[2+i]) << (i*8) }
            let amplitude = ((value >> 10) & 1023) + ((value >> 30) & 1023)
            XCTAssertTrue(amplitude > previous)
            XCTAssertLessThanOrEqual(amplitude, 511)
            previous = amplitude
        }
    }
    func testFD2RightStickOffsetMatchesSourceParser() throws {
        var payload = fd2()
        // RX = payload[13] | ((payload[14] & 0x0f) << 8), exactly as C# Unpack12X(payload,13).
        payload[13] = 0x04; payload[14] = 0x02; payload[15] = 0x80
        let state = try parser.parse(payload, format: .fd2)
        XCTAssertEqual(state.rawAxes.rx, 516)
        XCTAssertEqual(state.rawAxes.ry, 2048)
    }
    func testRumbleActiveGoldenVectorPreservesIndependentMotors() throws {
        var raw = Pro2RumbleEncoder.neutralRaw02()
        raw.replaceSubrange(2..<7, with: [0x20,0x25,0xa3,0x58,0x19])
        // Derived independently with the upstream packed field and banker-rounding formulas.
        let expected: [UInt8] = [0,83,138,145,3,146,113,225,0,16,30,0,225,0,16,30,0,83,18,1,112,24,0,225,0,16,30,0,225,0,16,30,0]
        let packet = try Pro2RumbleEncoder.ble(raw02: raw, packetID: 3)
        XCTAssertTrue(packet.active)
        XCTAssertEqual(packet.bytes, expected)
    }
    func testRumbleAmplitudeGainAndUSBBudget() throws {
        var raw = Pro2RumbleEncoder.neutralRaw02()
        raw.replaceSubrange(2..<7, with: [0xff,0xff,0xff,0xff,0xff])
        raw.replaceSubrange(18..<23, with: [0xff,0xff,0xff,0xff,0xff])
        let high = try Pro2RumbleEncoder.usb(raw02: raw, packetID: 1, gain: 3)
        XCTAssertTrue(high.active)
        for motor in 0..<2 {
            let o = 2 + motor*16
            var v: UInt64 = 0
            for i in 0..<5 { v |= UInt64(high.bytes[o+i]) << (i*8) }
            XCTAssertLessThanOrEqual(((v>>10)&1023) + ((v>>30)&1023), 511)
        }
        XCTAssertFalse(try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0, gain: -1).active)
        XCTAssertEqual(try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0, gain: .nan), try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0))
        XCTAssertThrowsError(try Pro2RumbleEncoder.ble(raw02: [], packetID: 0))
        raw[0] = 1
        XCTAssertThrowsError(try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0))
        raw[0] = 2; raw[1] = 0x40
        XCTAssertThrowsError(try Pro2RumbleEncoder.ble(raw02: raw, packetID: 0))
    }
}
