import XCTest
import Foundation
import Pro2Core
import CPro2Broker
import Pro2Broker

final class Pro2BrokerTests: XCTestCase {
    private var shimPath: String {
        Pro2BrokerPreflight.defaultShimURL()!.path
    }
    func testFiveOfficialProfilePackersUsingActualClientLibrary() {
        var status = Pro2BrokerStatus()
        let code = shimPath.withCString { pro2_broker_client_self_test($0,&status) }
        XCTAssertEqual(code,0)
    }
    func testMissingAndIncompatibleShimProduceExplicitErrors() {
        var status = Pro2BrokerStatus()
        XCTAssertEqual(pro2_broker_probe(nil,&status),-1)
        let code = "/usr/lib/libSystem.B.dylib".withCString { pro2_broker_probe($0,&status) }
        XCTAssertEqual(code,-2)
        XCTAssertEqual(pro2_broker_send(nil,nil,&status),3)
        XCTAssertEqual(pro2_broker_read_feedback(nil,nil),0)
    }
    func testEncodingPreservesButtonsAxesAndTriggerUnitRanges() {
        var state = Pro2State(); state.buttons = [.east,.l2,.aux,.paddleLeft]
        state.axes = .init(lx:0,ly:4095,rx:2048,ry:2048)
        let input = Pro2BrokerGamepad.encode(state)
        XCTAssertEqual(input.buttons,state.buttons.rawValue)
        XCTAssertEqual(input.lx,-1); XCTAssertEqual(input.ly,1); XCTAssertEqual(input.rx,0); XCTAssertEqual(input.ry,0)
        XCTAssertEqual(input.left_trigger,1); XCTAssertEqual(input.right_trigger,0)
        XCTAssertEqual(input.motion_valid,0); XCTAssertEqual(input.battery_percent,-1)
    }
    func testMotionUnitsAndNintendoToPortableCoordinateTransform() {
        var state = Pro2State(); state.motionSamples = [.init(accelX:4096,accelY:-4096,accelZ:4096,gyroX:16384,gyroY:-8192,gyroZ:4096)]
        let input = Pro2BrokerGamepad.encode(state)
        XCTAssertEqual(input.motion_valid,1)
        XCTAssertTrue(abs(input.accel_x-9.80665) < 0.0001)
        XCTAssertTrue(abs(input.accel_y-9.80665) < 0.0001)
        XCTAssertTrue(abs(input.accel_z+9.80665) < 0.0001)
        XCTAssertTrue(abs(input.gyro_x-500) < 0.0001); XCTAssertTrue(abs(input.gyro_y-250) < 0.0001); XCTAssertTrue(abs(input.gyro_z+1000) < 0.0001)
    }
    func testServicePreflightUsesExistingLocalStateWithoutCreation() {
        let result = Pro2BrokerPreflight.inspect()
        XCTAssertTrue(result.shimURL != nil)
        if !result.serviceAvailable { XCTAssertFalse(result.ready); XCTAssertEqual(result.licenseState,.unavailable) }
        if result.ready { XCTAssertEqual(result.licenseState,.licensed); XCTAssertEqual(result.code,0) }
        XCTAssertEqual(Pro2BrokerProfile.allCases.count,5)
    }
}
