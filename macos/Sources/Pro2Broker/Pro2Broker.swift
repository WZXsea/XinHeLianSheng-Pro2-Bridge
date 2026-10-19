// Apache-2.0. Optional system-wide virtual gamepad output through the official signed broker.
import Foundation
import Pro2Core
import CPro2Broker

public enum Pro2BrokerProfile: Int32, CaseIterable, Sendable {
    case generic = 0, xbox360, dualShock4, dualSense, switchPro
    public var label: String {
        switch self { case .generic: return "通用 HID 游戏手柄"; case .xbox360: return "Xbox 360 HID"; case .dualShock4: return "DualShock 4"; case .dualSense: return "DualSense"; case .switchPro: return "Switch Pro" }
    }
}
public enum Pro2BrokerLicenseState: Int32, Sendable { case unavailable, unlicensed, licensed, expired, disabled, invalid }
public struct Pro2BrokerAvailability: Sendable {
    public let shimURL: URL?
    public let brokerInstalled: Bool
    public let serviceAvailable: Bool
    public let licenseState: Pro2BrokerLicenseState
    public let activeDevices: UInt32
    public let activationLimit: UInt32
    public let activationUsage: UInt32
    public let code: Int32
    public let message: String
    public let purchaseURL: URL?
    public var ready: Bool { shimURL != nil && serviceAvailable && licenseState == .licensed && code == 0 }
    public var summary: String {
        if code == -5 { return "Virtual HID Broker 输出需要 macOS 14.2 或更高版本" }
        if shimURL == nil { return "虚拟手柄客户端组件待配置" }
        if !serviceAvailable { return brokerInstalled ? "Virtual HID Broker 服务待启动" : "Virtual HID Broker 待安装" }
        if licenseState != .licensed { return "Virtual HID Broker 机器许可证待激活" }
        return "Virtual HID Broker 已就绪"
    }
}
public struct Pro2BrokerError: Error, LocalizedError, Sendable {
    public let code: Int32
    public let message: String
    public var errorDescription: String? { message }
}
public struct Pro2BrokerFeedbackEvent: Sendable {
    public let kind: UInt32
    public let lowRumble, highRumble, leftTriggerRumble, rightTriggerRumble: Double
    public let red, green, blue, playerLEDs, flashingPlayerLEDs: UInt8
    public let rawReport: Data
}

public enum Pro2BrokerPreflight {
    public static let releaseURL = URL(string:"https://github.com/LizardByte/libvirtualhid/releases/tag/v2026.1016.55.45")!
    public static let documentationURL = URL(string:"https://docs.lizardbyte.dev/projects/libvirtualhid/master/md_docs_2macos-gamepad.html")!
    public static let controlApplicationURL = URL(fileURLWithPath:"/Applications/VirtualHIDControl.app")
    public static func defaultShimURL() -> URL? {
        var candidates: [URL] = []
        if let frameworks = Bundle.main.privateFrameworksURL { candidates.append(frameworks.appendingPathComponent("libPro2BrokerShim.dylib")) }
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("libPro2BrokerShim.dylib")) }
        let source = URL(fileURLWithPath:#filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        candidates.append(source.appendingPathComponent("Vendor/libvirtualhid/lib/libPro2BrokerShim.dylib"))
        return candidates.first { FileManager.default.fileExists(atPath:$0.path) }
    }
    /// Reads the local broker status. Installation and license activation remain explicit user actions.
    public static func inspect(shimURL: URL? = nil) -> Pro2BrokerAvailability {
        let shim = shimURL ?? defaultShimURL()
        let installed = FileManager.default.fileExists(atPath:"/Applications/VirtualHIDBroker.app")
        guard #available(macOS 14.2,*) else {
            return .init(shimURL:shim,brokerInstalled:installed,serviceAvailable:false,licenseState:.unavailable,activeDevices:0,activationLimit:0,activationUsage:0,code:-5,message:"官方客户端发布包的最低系统版本为 macOS 14.2。",purchaseURL:nil)
        }
        guard let shim else {
            return .init(shimURL:nil,brokerInstalled:installed,serviceAvailable:false,licenseState:.unavailable,activeDevices:0,activationLimit:0,activationUsage:0,code:-1,message:"请构建或安装与 libvirtualhid v2026.1016.55.45 对应的客户端组件。",purchaseURL:nil)
        }
        var status = Pro2BrokerStatus()
        _ = shim.path.withCString { pro2_broker_probe($0,&status) }
        let message = brokerString(status.message), purchase = brokerString(status.purchase_url)
        return .init(shimURL:shim,brokerInstalled:installed,serviceAvailable:status.service_available != 0,licenseState:Pro2BrokerLicenseState(rawValue:status.license_state) ?? .invalid,activeDevices:status.active_devices,activationLimit:status.activation_limit,activationUsage:status.activation_usage,code:status.code,message:message,purchaseURL:URL(string:purchase))
    }
}

/// Serial-consumer output. Keep send/readFeedback/stop on the same output queue.
public final class Pro2BrokerGamepad {
    private var handle: Pro2BrokerRef?
    public private(set) var profile: Pro2BrokerProfile?
    public init() {}
    public var isRunning: Bool { handle != nil }
    public func start(profile: Pro2BrokerProfile, stableID: String, shimURL: URL? = nil) throws {
        stop()
        let availability = Pro2BrokerPreflight.inspect(shimURL:shimURL)
        guard availability.ready, let shim = availability.shimURL else {
            throw Pro2BrokerError(code:availability.code == 0 ? 5 : availability.code,message:availability.summary + (availability.message.isEmpty ? "" : "：" + availability.message))
        }
        var status = Pro2BrokerStatus()
        let device = shim.path.withCString { path in stableID.withCString { pro2_broker_create(path,profile.rawValue,$0,&status) } }
        guard let device else { throw Pro2BrokerError(code:status.code,message:brokerString(status.message)) }
        handle = device; self.profile = profile
    }
    public func send(_ state: Pro2State) throws {
        guard let handle else { throw Pro2BrokerError(code:3,message:"请先启动虚拟手柄输出。") }
        var input = Self.encode(state), status = Pro2BrokerStatus()
        let code = pro2_broker_send(handle,&input,&status)
        guard code == 0 else { throw Pro2BrokerError(code:code,message:brokerString(status.message)) }
    }
    public func readFeedback(limit: Int = 64) -> [Pro2BrokerFeedbackEvent] {
        guard let handle else { return [] }
        var events: [Pro2BrokerFeedbackEvent] = []
        for _ in 0..<min(64,max(0,limit)) {
            var event = Pro2BrokerFeedback()
            guard pro2_broker_read_feedback(handle,&event) != 0 else { break }
            let data = withUnsafeBytes(of:&event.raw_report) { Data($0.prefix(min(Int(event.raw_size),Int(PRO2_BROKER_MAX_RAW_REPORT)))) }
            events.append(.init(kind:event.kind,lowRumble:Double(event.low_rumble)/65535,highRumble:Double(event.high_rumble)/65535,leftTriggerRumble:Double(event.left_trigger_rumble)/65535,rightTriggerRumble:Double(event.right_trigger_rumble)/65535,red:event.red,green:event.green,blue:event.blue,playerLEDs:event.player_leds,flashingPlayerLEDs:event.flashing_player_leds,rawReport:data))
        }
        return events
    }
    public func stop() { if let handle { pro2_broker_destroy(handle) }; handle = nil; profile = nil }
    deinit { stop() }
    public static func encode(_ state: Pro2State) -> Pro2BrokerInput {
        func axis(_ raw: UInt16) -> Float { let d = Float(Int(raw)-2048); return min(1,max(-1,d / (raw < 2048 ? 2048 : 2047))) }
        var result = Pro2BrokerInput()
        result.buttons = state.buttons.rawValue
        result.lx = axis(state.axes.lx); result.ly = axis(state.axes.ly); result.rx = axis(state.axes.rx); result.ry = axis(state.axes.ry)
        result.left_trigger = Float(state.leftTrigger); result.right_trigger = Float(state.rightTrigger)
        result.battery_percent = Int32(state.battery.approximatePercent ?? -1); result.charging = state.battery.charging == true ? 1 : 0
        if let motion = state.motionSamples.last {
            result.motion_valid = 1
            // libvirtualhid uses PlayStation-style coordinates. Its Switch packer
            // maps canonical (X,Y,Z) to native (-Z,-X,Y), so invert that transform.
            result.accel_x = -Float(motion.accelY) * 9.80665 / 4096; result.accel_y = Float(motion.accelZ) * 9.80665 / 4096; result.accel_z = -Float(motion.accelX) * 9.80665 / 4096
            result.gyro_x = -Float(motion.gyroY) / 16.384; result.gyro_y = Float(motion.gyroZ) / 16.384; result.gyro_z = -Float(motion.gyroX) / 16.384
        }
        return result
    }
}
private func brokerString<T>(_ tuple: T) -> String {
    withUnsafeBytes(of:tuple) { bytes in
        let chars = bytes.bindMemory(to:CChar.self)
        return String(decoding:chars.prefix(while:{ $0 != 0 }).map { UInt8(bitPattern:$0) },as:UTF8.self)
    }
}
