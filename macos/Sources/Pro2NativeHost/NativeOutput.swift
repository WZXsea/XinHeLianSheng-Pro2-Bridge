import Foundation
import Pro2Core
import Pro2Output
import CPro2Broker

struct NativeHostFailure: Error {
    let code: String
    let message: String
    init(_ code: String, _ message: String) { self.code = code; self.message = message }
}

struct NativeGyroScales {
    var pitch = 1.0, yaw = 1.0, roll = 1.0
    static func normalized(_ value: Double) -> Double {
        guard value.isFinite else { return 1 }
        return (min(4,max(0.1,value)) * 100).rounded(.toNearestOrEven) / 100
    }
    var json: [String:Double] { ["pitch":pitch,"yaw":yaw,"roll":roll] }
}

enum NativeStateAdapter {
    // Exact constants from the pinned MIT client's profile report packers.
    static let dualSenseGyroPackingScale: Float = 1145 * .pi / 180
    static let dualSenseAccelPackingScale: Float = 9.80665 * 100
    static let switchGyroPackingScale: Float = 14.2842
    // Shared C# GamepadButtons logical positions to the physical FD2 flag space.
    static let physicalBits = [2,3,0,1,22,6,23,7,8,9,11,10,16,18,19,17,12,13,24,25,14]
    static func physicalButtons(_ logical: UInt64) -> UInt32 {
        var physical: UInt32 = 0
        for (index, bit) in physicalBits.enumerated() where logical & (UInt64(1) << index) != 0 { physical |= UInt32(1) << bit }
        return physical
    }
    static func number(_ state: [String: Any], _ key: String, default fallback: Double = 0) throws -> Double {
        guard let value = state[key] else { return fallback }
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite else {
            throw NativeHostFailure("invalid_state", "state.\(key) 需要有限数值。")
        }
        return number.doubleValue
    }
    static func bool(_ state: [String: Any], _ key: String) -> Bool { (state[key] as? NSNumber)?.boolValue ?? false }
    static func logicalButtons(_ state: [String: Any]) throws -> UInt64 {
        guard let value = state["buttons"] else { return 0 }
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue >= 0, number.doubleValue <= Double(0x1fffff), number.doubleValue.rounded() == number.doubleValue else {
            throw NativeHostFailure("invalid_state", "state.buttons 需要 21 位非负逻辑按键标志。")
        }
        return number.uint64Value
    }
    static func rawAxis(_ state: [String: Any], _ key: String, center: Double = 2048) throws -> UInt16 {
        let value = try number(state, key, default: center)
        guard (0...4095).contains(value) else { throw NativeHostFailure("invalid_state", "state.\(key) 取值范围为 0...4095。") }
        return UInt16(value.rounded())
    }
    static func sensor(_ state: [String: Any], _ key: String) throws -> Int16 {
        let value = try number(state, key)
        guard (-32768...32767).contains(value) else { throw NativeHostFailure("invalid_state", "state.\(key) 需要 Int16 范围数值。") }
        return Int16(value.rounded())
    }
    static func nativeState(_ state: [String: Any]) throws -> Pro2State {
        var result = Pro2State()
        result.buttons = Pro2Buttons(rawValue: physicalButtons(try logicalButtons(state)))
        result.axes = try .init(lx: rawAxis(state,"lx"), ly: rawAxis(state,"ly"), rx: rawAxis(state,"rx"), ry: rawAxis(state,"ry"))
        result.rawAxes = result.axes
        if let level = state["batteryLevel"] as? NSNumber, (0...9).contains(level.intValue) { result.battery.level = level.uint8Value }
        result.battery.charging = state["batteryCharging"].map { ($0 as? NSNumber)?.boolValue ?? false }
        if bool(state,"accelValid") || bool(state,"gyroValid") {
            result.motionSamples = [try .init(accelX:sensor(state,"accelX"),accelY:sensor(state,"accelY"),accelZ:sensor(state,"accelZ"),gyroX:sensor(state,"gyroX"),gyroY:sensor(state,"gyroY"),gyroZ:sensor(state,"gyroZ"))]
        }
        return result
    }
    static func brokerInput(_ state: [String: Any], scales: NativeGyroScales = .init(), mode: String = "generic") throws -> Pro2BrokerInput {
        var result = Pro2BrokerInput()
        result.buttons = physicalButtons(try logicalButtons(state))
        func axis(_ key: String) throws -> Float {
            let raw = Int(try rawAxis(state,key)), delta = raw - 2048
            return Float(delta) / Float(raw < 2048 ? 2048 : 2047)
        }
        result.lx = try axis("lx"); result.ly = try axis("ly"); result.rx = try axis("rx"); result.ry = try axis("ry")
        result.left_trigger = Float(try rawAxis(state,"l2",center:0)) / 4095
        result.right_trigger = Float(try rawAxis(state,"r2",center:0)) / 4095
        let accelValid = bool(state,"accelValid"), gyroValid = bool(state,"gyroValid")
        result.motion_valid = accelValid || gyroValid ? 1 : 0
        if mode == "dualsense" {
            // Adapt the pinned MIT client's exact DS5 packing scales to shared
            // Windows final HID counts: (+X,+Z,-Y), gyro raw unchanged and accel
            // raw doubled. This profile-specific compensation preserves the
            // original application's axis and sensitivity contract.
            if accelValid {
                result.accel_x = Float(try sensor(state,"accelX")) * 2 / dualSenseAccelPackingScale
                result.accel_y = Float(try sensor(state,"accelZ")) * 2 / dualSenseAccelPackingScale
                result.accel_z = -Float(try sensor(state,"accelY")) * 2 / dualSenseAccelPackingScale
            }
            if gyroValid {
                result.gyro_x = Float(try sensor(state,"gyroX")) / dualSenseGyroPackingScale * Float(scales.pitch)
                result.gyro_y = Float(try sensor(state,"gyroZ")) / dualSenseGyroPackingScale * Float(scales.yaw)
                result.gyro_z = -Float(try sensor(state,"gyroY")) / dualSenseGyroPackingScale * Float(scales.roll)
            }
        } else {
        if accelValid {
            result.accel_x = -Float(try sensor(state,"accelY")) * 9.80665 / 4096
            result.accel_y = Float(try sensor(state,"accelZ")) * 9.80665 / 4096
            result.accel_z = -Float(try sensor(state,"accelX")) * 9.80665 / 4096
        }
        if gyroValid {
            let gyroScale: Float = mode == "pro1" ? switchGyroPackingScale : 16.384
            result.gyro_x = -Float(try sensor(state,"gyroY")) / gyroScale * Float(scales.pitch)
            result.gyro_y = Float(try sensor(state,"gyroZ")) / gyroScale * Float(scales.yaw)
            result.gyro_z = -Float(try sensor(state,"gyroX")) / gyroScale * Float(scales.roll)
        }
        }
        result.battery_percent = -1
        if let level = state["batteryLevel"] as? NSNumber, (0...9).contains(level.intValue) { result.battery_percent = Int32((level.intValue * 100 + 4) / 9) }
        result.charging = bool(state,"batteryCharging") ? 1 : 0
        return result
    }
}

/// Native API adaptation only. Profile selection and state mapping are supplied
/// by the shared C# application before submitOutput reaches this boundary.
final class NativeOutput {
    private var broker: Pro2BrokerRef?
    private var generic: Pro2VirtualGamepad?
    private(set) var backend = ""
    private(set) var mode = ""
    var gyroScales = NativeGyroScales()
    var running: Bool { broker != nil || generic != nil }

    static func shimURL() -> URL? {
        var candidates: [URL] = []
        if let explicit = ProcessInfo.processInfo.environment["PRO2_BROKER_SHIM"], explicit.hasPrefix("/") {
            candidates.append(URL(fileURLWithPath: explicit))
        }
        if let frameworks = Bundle.main.privateFrameworksURL { candidates.append(frameworks.appendingPathComponent("libPro2BrokerShim.dylib")) }
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("libPro2BrokerShim.dylib")) }
        let executable = URL(fileURLWithPath:CommandLine.arguments[0]).standardizedFileURL
        candidates.append(executable.deletingLastPathComponent().appendingPathComponent("libPro2BrokerShim.dylib"))
        candidates.append(executable.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Frameworks/libPro2BrokerShim.dylib"))
        let source = URL(fileURLWithPath:#filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        candidates.append(source.appendingPathComponent("Vendor/libvirtualhid/lib/libPro2BrokerShim.dylib"))
        return candidates.first { FileManager.default.fileExists(atPath:$0.path) }
    }
    static func status() -> [String: Any] {
        var status = Pro2BrokerStatus()
        let shim = shimURL()
        var code: Int32 = -1
        if #available(macOS 14.2,*) {
            if let shim { code = shim.path.withCString { pro2_broker_probe($0, &status) } }
        } else { code = -5 }
        let ready = code == 0 && status.service_available != 0 && status.license_state == 2
        let message: String
        if code == -5 { message = "虚拟输出客户端要求 macOS 14.2 或更高版本。" }
        else if shim == nil { message = "Virtual HID Broker 客户端组件待配置。" }
        else if status.service_available == 0 { message = "Virtual HID Broker 服务待安装或启动。" }
        else if status.license_state != 2 { message = "Virtual HID Broker 机器许可证待激活。" }
        else { message = ready ? "Virtual HID Broker 已就绪。" : cString(status.message) }
        return ["ready":ready,"message":message,"broker": ["shimAvailable":shim != nil,"installed":FileManager.default.fileExists(atPath:"/Applications/VirtualHIDBroker.app"),
            "serviceAvailable":status.service_available != 0,"licenseState":Int(status.license_state),
            "code":Int(code),"message":cString(status.message),"activeDevices":Int(status.active_devices),
            "activationLimit":Int(status.activation_limit),"activationUsage":Int(status.activation_usage),
            "supportedModes":["pro1","xbox","dualsense"],"purchaseURL":cString(status.purchase_url)],
            "nativeHID":["supportedModes":["generic"],"requiredEntitlement":"com.apple.developer.hid.virtual.device"],
            "pro2":["available":false,"code":"profile_unsupported","message":"原生 Pro 2 虚拟身份需要专用输出实现。"]]
    }
    func create(backend requestedBackend: String, mode requestedMode: String, slot: Int) throws {
        stop()
        if requestedBackend == "broker" {
            let profiles: [String: Int32] = ["generic":0,"xbox":1,"pro1":4,"dualsense":3]
            guard let profile = profiles[requestedMode] else { throw NativeHostFailure("profile_unsupported", "Broker 当前支持 pro1、xbox 和 dualsense 身份；\(requestedMode) 需要对应原生输出实现。") }
            guard #available(macOS 14.2,*) else { throw NativeHostFailure("backend_os_version", "当前 Broker 客户端要求 macOS 14.2 或更高版本。") }
            guard let shim = Self.shimURL() else { throw NativeHostFailure("backend_component_missing", "Virtual HID Broker 客户端组件待配置。") }
            var status = Pro2BrokerStatus()
            let stableID = "io.github.wzxsea.pro2bridge.slot\(slot)"
            broker = shim.path.withCString { path in stableID.withCString { pro2_broker_create(path,profile,$0,&status) } }
            guard broker != nil else { throw NativeHostFailure("broker_\(status.code)", Self.cString(status.message)) }
        } else if requestedBackend == "nativeHID" {
            guard requestedMode == "generic" else { throw NativeHostFailure("profile_unsupported", "现有原生 HID 接口提供 generic 身份；\(requestedMode) 需要对应的报告描述符与输出实现。") }
            let gamepad = Pro2VirtualGamepad()
            guard gamepad.start() else { throw NativeHostFailure("native_hid_authorization", "系统虚拟 HID 创建需要 com.apple.developer.hid.virtual.device 签名授权。") }
            generic = gamepad
        } else { throw NativeHostFailure("invalid_backend", "backend 取值为 broker 或 nativeHID。") }
        backend = requestedBackend; mode = requestedMode
    }
    func submit(_ state: [String: Any]) throws {
        if let broker {
            var input = try NativeStateAdapter.brokerInput(state,scales:mode == "dualsense" ? gyroScales : .init(),mode:mode), status = Pro2BrokerStatus()
            let code = pro2_broker_send(broker,&input,&status)
            guard code == 0 else { throw NativeHostFailure("broker_\(code)",Self.cString(status.message)) }
        } else if let generic {
            let code = generic.send(try NativeStateAdapter.nativeState(state))
            guard code == 0 else { throw NativeHostFailure("native_hid_\(code)", "系统虚拟 HID 写入返回 \(code)。") }
        } else { throw NativeHostFailure("output_inactive", "请先创建此通道的虚拟输出。") }
    }
    func feedback(limit: Int) -> [[String: Any]] {
        guard let broker else { return [] }
        var events: [[String: Any]] = []
        for _ in 0..<min(64,max(0,limit)) {
            var event = Pro2BrokerFeedback()
            guard pro2_broker_read_feedback(broker,&event) != 0 else { break }
            let raw = withUnsafeBytes(of:&event.raw_report) { Data($0.prefix(min(Int(event.raw_size),Int(PRO2_BROKER_MAX_RAW_REPORT)))) }
            events.append(["kind":Int(event.kind),"lowRumble":Double(event.low_rumble)/65535,"highRumble":Double(event.high_rumble)/65535,
                "leftTriggerRumble":Double(event.left_trigger_rumble)/65535,"rightTriggerRumble":Double(event.right_trigger_rumble)/65535,
                "red":Int(event.red),"green":Int(event.green),"blue":Int(event.blue),"playerLEDs":Int(event.player_leds),
                "flashingPlayerLEDs":Int(event.flashing_player_leds),"rawBase64":raw.base64EncodedString()])
        }
        return events
    }
    func stop() {
        if let broker { pro2_broker_destroy(broker) }; broker = nil
        generic?.stop(); generic = nil; backend = ""; mode = ""
    }
    deinit { stop() }
    private static func cString<T>(_ tuple: T) -> String {
        withUnsafeBytes(of:tuple) { String(decoding:$0.prefix(while: { $0 != 0 }),as:UTF8.self) }
    }
}
