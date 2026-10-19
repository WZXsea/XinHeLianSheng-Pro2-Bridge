import XCTest
@testable import Pro2Transport

final class Pro2TransportTests: XCTestCase {
    func testWindowsInitializationProfileExcludesPairingCommit() {
        XCTAssertEqual(BLEStartup.commands.count, 14)
        XCTAssertEqual(BLEStartup.commands.map { $0[0] }, [3,7,22,12,17,10,12,3,16,1,1,9,2,2])
        XCTAssertEqual(BLEStartup.commands.map { $0[3] }, [13,1,1,2,3,8,4,10,1,12,1,7,4,4])
        XCTAssertFalse(BLEStartup.commands.contains { $0[0] == 0x15 })
        XCTAssertEqual(BLEStartup.commands[5].count, 28)
    }

    func testAcknowledgementMatchesIdentityAcrossTransportDirection() {
        let request = Data([12,0x91,1,2,0,4,0,0,255,0,0,0])
        XCTAssertTrue(BLEStartup.acknowledgementMatches(request: request, reply: Data([12,1,1,2,0x10,0x78,0,0])))
        XCTAssertFalse(BLEStartup.acknowledgementMatches(request: request, reply: Data([12,1,1,4,0x10,0x78,0,0])))
        XCTAssertFalse(BLEStartup.acknowledgementMatches(request: request, reply: Data([11,1,1,2,0x10,0x78,0,0])))
        XCTAssertFalse(BLEStartup.acknowledgementMatches(request: request, reply: Data([12,1,1,2])))
    }

    func testControllerCandidatesIncludeWindowsNamesAndNintendoAdvertisements() {
        for name in ["Pro Controller", "Nintendo Switch 2", "NS2Pro", "PRO2", "Pro 2 Controller"] {
            XCTAssertTrue(BLEStartup.recognizesController(name: name, manufacturer: nil))
        }
        XCTAssertTrue(BLEStartup.recognizesController(name: "", manufacturer: Data([0x53,0x05,0x7e])))
        XCTAssertFalse(BLEStartup.recognizesController(name: "Nearby Keyboard", manufacturer: Data([0x4c,0x00])))
        XCTAssertFalse(BLEStartup.recognizesController(name: "", manufacturer: Data([0x53])))
    }

    func testFlashRepliesValidateAddressAndAvailableBytes() {
        let address: UInt32 = 0x00013109
        let request = BLEStartup.flashRead(address: address)
        XCTAssertEqual(Array(request.suffix(4)), [9,49,1,0])
        var reply = Data(repeating: 0, count: 16)
        reply.replaceSubrange(12..<16, with: request[12..<16])
        reply.append(contentsOf: [1,2,3,4,5,6,7,8,9])
        XCTAssertEqual(BLEStartup.flashPayload(reply: reply, address: address, length: 9), Data([1,2,3,4,5,6,7,8,9]))
        XCTAssertNil(BLEStartup.flashPayload(reply: reply, address: address + 9, length: 9))
        XCTAssertNil(BLEStartup.flashPayload(reply: Data(reply.prefix(20)), address: address, length: 9))
        XCTAssertNil(BLEStartup.flashPayload(reply: reply, address: address, length: 10))
    }

    func testReconnectBackoffIsBoundedAndStartsImmediately() {
        XCTAssertEqual((1...6).map { BLEStartup.reconnectDelay(attempt: $0) }, [1,2,4,8,15,15])
        XCTAssertEqual(BLEStartup.reconnectDelay(attempt: 1000), 15)
        XCTAssertEqual(BLEStartup.reconnectDelay(attempt: -10), 1)
    }

    func testDiagnosticsStartWithExplicitAcceptanceState() {
        let diagnostics = Pro2BLEDiagnostics()
        XCTAssertEqual(diagnostics.phase, .idle)
        XCTAssertEqual(diagnostics.receivedFrames, 0)
        XCTAssertEqual(diagnostics.measuredHz, 0)
        XCTAssertEqual(diagnostics.initializationTotal, 14)
        XCTAssertEqual(diagnostics.requestedReportRate, 133)
        XCTAssertTrue(diagnostics.logPath.hasSuffix("Library/Logs/Pro2Bridge/bluetooth.log"))
    }

    func testFirmwareComesOnlyFromSuccessfulDeviceInfoReply() {
        var reply = Data([0x10,1,1,1,0,0x78,0,0,3,2,1,0,7,8,9,0,1,2,3])
        let firmware = Pro2BLEFirmware.decode(reply)
        XCTAssertEqual(firmware?.controller, "3.2.1")
        XCTAssertEqual(firmware?.bluetooth, "7.8.9")
        XCTAssertEqual(firmware?.dsp, "1.2.3")
        reply[5] = 0
        XCTAssertNil(Pro2BLEFirmware.decode(reply))
        reply[5] = 0x78
        reply.replaceSubrange(8..<11, with: [0xff,0xff,0xff])
        XCTAssertNil(Pro2BLEFirmware.decode(reply))
    }

    func testSessionOptionsAndRuntimeRefreshAcceptancePolicy() {
        let full = BLEStartup.initializationCommands(skipUSBInitialization: false)
        let comparison = BLEStartup.initializationCommands(skipUSBInitialization: true)
        XCTAssertEqual(full.count, 14)
        XCTAssertEqual(comparison.count, 13)
        XCTAssertEqual(comparison, Array(full.dropFirst()))
        XCTAssertFalse(comparison.contains { $0[0] == 0x15 })
        func accepts(enabled: Bool = true, ready: Bool = true, quiet: Double = 5, elapsed: Double = 600,
            age: Double = 0.1, hz: Double = 133, busy: Bool = false, suspended: Bool = false) -> Bool {
            BLEBatteryRefreshPolicy.canRefresh(enabled: enabled, ready: ready, quietSeconds: quiet,
                elapsedSeconds: elapsed, inputAge: age, measuredHz: hz, operationInFlight: busy, suspended: suspended)
        }
        XCTAssertTrue(accepts())
        XCTAssertFalse(accepts(enabled: false))
        XCTAssertFalse(accepts(ready: false))
        XCTAssertFalse(accepts(quiet: 4.99))
        XCTAssertFalse(accepts(elapsed: 599.99))
        XCTAssertFalse(accepts(age: 0.5))
        XCTAssertFalse(accepts(age: -1))
        XCTAssertFalse(accepts(hz: 9.99))
        XCTAssertFalse(accepts(busy: true))
        XCTAssertFalse(accepts(suspended: true))
        XCTAssertEqual(BLEStartup.reconnectDelay(attempt: .min), 1)
        XCTAssertNil(BLEStartup.flashPayload(reply: Data(repeating: 0, count: 25), address: 0, length: -1))
    }
}
