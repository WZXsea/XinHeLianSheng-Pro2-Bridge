import Foundation
import IOKit
import IOKit.hid
import CPro2USB

private final class USBInitializationToken: @unchecked Sendable {
    let pointer: OpaquePointer
    init?() {
        guard let pointer = pro2_usb_cancellation_create() else { return nil }
        self.pointer = pointer
    }
    func cancel() { pro2_usb_cancellation_cancel(pointer) }
    deinit { pro2_usb_cancellation_destroy(pointer) }
}

public final class Pro2USBTransport: Pro2InputTransport {
    public var onConnectionChanged: ((Bool) -> Void)?
    public var onReport: ((Data) -> Void)?
    public var onStatus: ((String) -> Void)?
    public var onDevices: (([Pro2TransportDevice]) -> Void)?
    public var onPrimaryReport: ((Data) -> Void)?
    private var manager: IOHIDManager?
    private var devices: [String: IOHIDDevice] = [:]
    private var selected: IOHIDDevice?
    private var desiredID: String?
    private var desiredLocation: UInt32?
    private var selectedID: String?
    private var buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 128)
    private var session = 0
    private var initialization: USBInitializationToken?
    private var receivedInput = false
    private var ready = false
    private var batteryCapture = false
    private var batteryRestore = false
    private var batteryTimeout: DispatchWorkItem?
    private var watchdog: DispatchSourceTimer?
    private var lastInput: TimeInterval = 0
    private let commands = DispatchQueue(label: "pro2.usb.commands")
    public init() {}
    deinit { stop(); buffer.deallocate() }
    public func startDiscovery() {
        if manager != nil { publishDevices(); return }
        let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
        self.manager = manager
        IOHIDManagerSetDeviceMatching(manager, [kIOHIDVendorIDKey: 0x057e, kIOHIDProductIDKey: 0x2069] as CFDictionary)
        let context = Unmanaged.passUnretained(self).toOpaque()
        IOHIDManagerRegisterDeviceMatchingCallback(manager, { context, result, _, device in
            guard let context, result == kIOReturnSuccess else { return }
            Unmanaged<Pro2USBTransport>.fromOpaque(context).takeUnretainedValue().found(device)
        }, context)
        IOHIDManagerRegisterDeviceRemovalCallback(manager, { context, _, _, device in
            guard let context else { return }
            Unmanaged<Pro2USBTransport>.fromOpaque(context).takeUnretainedValue().removed(device)
        }, context)
        IOHIDManagerScheduleWithRunLoop(manager, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
        let result = IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
        onStatus?(result == kIOReturnSuccess ? "USB 扫描中；请通过数据线连接手柄。" : "USB 枚举错误：\(hex(result))")
    }
    private func id(_ device: IOHIDDevice) -> String {
        var value: UInt64 = 0
        IORegistryEntryGetRegistryEntryID(IOHIDDeviceGetService(device), &value)
        return String(value)
    }
    private func found(_ device: IOHIDDevice) {
        let transport = IOHIDDeviceGetProperty(device, kIOHIDTransportKey as CFString) as? String ?? ""
        let page = IOHIDDeviceGetProperty(device, kIOHIDPrimaryUsagePageKey as CFString) as? NSNumber
        let usage = IOHIDDeviceGetProperty(device, kIOHIDPrimaryUsageKey as CFString) as? NSNumber
        guard transport == "USB", page?.intValue == 1, [4, 5].contains(usage?.intValue ?? 0) else { return }
        let key = id(device)
        devices[key] = device; publishDevices()
        let location = (IOHIDDeviceGetProperty(device, kIOHIDLocationIDKey as CFString) as? NSNumber)?.uint32Value
        if selected == nil && (desiredID == key || (desiredLocation != nil && desiredLocation == location)) { connect(to: key) }
    }
    private func removed(_ device: IOHIDDevice) {
        let key = id(device)
        devices.removeValue(forKey: key)
        if selectedID == key {
            closeSelected()
            onStatus?("USB 手柄已拔出；相同 USB 位置重新插接后将自动重连。")
        }
        publishDevices()
    }
    private func publishDevices() {
        onDevices?(devices.map { key, device in
            Pro2TransportDevice(id: key, name: IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String ?? "Nintendo Switch 2 Pro", kind: .usb)
        }.sorted { $0.id < $1.id })
    }
    public func connect(to id: String) {
        closeSelected()
        guard let device = devices[id] else { onStatus?("所选 USB 设备待重新发现。"); return }
        desiredID = id
        guard let location = IOHIDDeviceGetProperty(device, kIOHIDLocationIDKey as CFString) as? NSNumber,
              location.uint32Value != 0 else { onStatus?("USB 设备需要有效的物理 locationID。"); return }
        desiredLocation = location.uint32Value
        let result = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
        guard result == kIOReturnSuccess else {
            onStatus?("USB HID 打开错误：\(hex(result))。请检查输入监控权限及设备占用。"); return
        }
        selected = device; selectedID = id; receivedInput = false; ready = false
        let context = Unmanaged.passUnretained(self).toOpaque()
        IOHIDDeviceRegisterInputReportCallback(device, buffer, 128, { context, result, sender, _, reportID, report, length in
            guard let context, result == kIOReturnSuccess else { return }
            let owner = Unmanaged<Pro2USBTransport>.fromOpaque(context).takeUnretainedValue()
            guard let sender, let selected = owner.selected, owner.ready, reportID == 5 || reportID == 9 else { return }
            let source = Unmanaged<IOHIDDevice>.fromOpaque(sender).takeUnretainedValue()
            guard CFEqual(selected, source) else { return }
            var data = Data(bytes: report, count: length)
            // IOHIDDevice callbacks expose complete reports on macOS; account for hosts
            // returning a payload without its ID only when exactly 63 bytes arrived.
            if data.count == 63 { data.insert(UInt8(reportID), at: 0) }
            if reportID == 9, owner.batteryCapture, data.count == 64, data.first == 9 {
                let power = data[2], level = (power >> 2) & 15
                if level <= 9 { owner.onPrimaryReport?(Data(data.dropFirst())); owner.restoreCommonReport() }
                return
            }
            guard !owner.batteryCapture else { return }
            guard data.count == 64, data.first == 5 else { return }
            if !owner.receivedInput { owner.receivedInput = true; owner.startWatchdog(); owner.onConnectionChanged?(true); owner.onStatus?("USB report 05 输入已就绪。") }
            owner.lastInput = ProcessInfo.processInfo.systemUptime
            owner.onReport?(data)
        }, context)
        IOHIDDeviceScheduleWithRunLoop(device, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
        let currentSession = session
        let locationID = location.uint32Value
        onStatus?("USB 已打开，正在初始化同一设备的 interface 1 bulk OUT。")
        guard let token = USBInitializationToken() else {
            onStatus?("USB 初始化内存分配失败。")
            closeSelected(); return
        }
        initialization = token
        commands.async { [weak self] in
            let status = pro2_usb_initialize(locationID, token.pointer)
            DispatchQueue.main.async { [weak self] in
                if self?.initialization === token { self?.initialization = nil }
                guard let self, self.session == currentSession, self.selectedID == id else { return }
                guard status == kIOReturnSuccess else {
                    self.onStatus?("USB 命令接口初始化错误：\(self.hex(status))。需要 interface 1 的用户空间访问权限。")
                    self.closeSelected(); return
                }
                self.ready = true
                self.onStatus?("USB 初始化完成，读取连接电量。")
                self.refreshBattery()
                DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [weak self] in
                    guard let self, self.session == currentSession, !self.receivedInput else { return }
                    self.onStatus?("USB 首帧超时；请重新插接手柄。")
                    self.closeSelected()
                }
            }
        }
    }
    public func writeOutput(_ report: Data) {
        guard let selected, ready, report.count == 64, report.first == 2 else { return }
        let status = report.withUnsafeBytes { bytes in
            IOHIDDeviceSetReport(selected, kIOHIDReportTypeOutput, 2,
                                 bytes.bindMemory(to: UInt8.self).baseAddress!, report.count)
        }
        if status != kIOReturnSuccess { onStatus?("USB 震动写入错误：\(hex(status))") }
    }
    /// Call only before gameplay or after the owner verifies five seconds of idle input.
    public func refreshBattery() {
        guard selected != nil, ready, !batteryCapture, let location = desiredLocation else { return }
        batteryCapture = true; batteryRestore = false
        let generation = session
        commands.async { [weak self] in
            let result = pro2_usb_select_input_report(location, 9, nil)
            DispatchQueue.main.async { [weak self] in
                guard let self, self.session == generation, self.batteryCapture else { return }
                if result != kIOReturnSuccess { self.onStatus?("USB 电量请求：\(self.hex(result))"); self.restoreCommonReport(); return }
                let timeout = DispatchWorkItem { [weak self] in self?.restoreCommonReport() }
                self.batteryTimeout = timeout
                DispatchQueue.main.asyncAfter(deadline:.now()+0.75,execute:timeout)
            }
        }
    }
    private func restoreCommonReport() {
        guard batteryCapture, !batteryRestore, let location = desiredLocation else { return }
        batteryRestore = true; batteryTimeout?.cancel(); batteryTimeout = nil
        let generation = session
        commands.async { [weak self] in
            let result = pro2_usb_select_input_report(location, 5, nil)
            DispatchQueue.main.async { [weak self] in
                guard let self, self.session == generation else { return }
                self.batteryTimeout?.cancel(); self.batteryTimeout = nil
                self.batteryCapture = false; self.batteryRestore = false
                if result == kIOReturnSuccess { self.onStatus?("USB 电量采样结束，持续 report 05 已恢复。") }
                else { self.onStatus?("USB 持续输入恢复：\(self.hex(result))，请重新连接。"); self.closeSelected() }
            }
        }
    }
    private func startWatchdog() {
        lastInput = ProcessInfo.processInfo.systemUptime
        let source = DispatchSource.makeTimerSource(queue: .main)
        source.schedule(deadline: .now() + 1, repeating: 1)
        source.setEventHandler { [weak self] in
            guard let self, !self.batteryCapture, ProcessInfo.processInfo.systemUptime - self.lastInput > 3 else { return }
            self.onStatus?("USB 输入中断超过 3 秒，请重新插接手柄。")
            self.closeSelected()
        }
        watchdog = source; source.resume()
    }
    private func closeSelected() {
        batteryTimeout?.cancel(); batteryTimeout = nil
        if batteryCapture, let location = desiredLocation {
            commands.async { _ = pro2_usb_select_input_report(location, 5, nil) }
        }
        batteryCapture = false; batteryRestore = false
        initialization?.cancel()
        initialization = nil
        watchdog?.cancel(); watchdog = nil
        onConnectionChanged?(false)
        session += 1; ready = false; receivedInput = false
        if let selected {
            IOHIDDeviceRegisterInputReportCallback(selected, buffer, 128, nil, nil)
            IOHIDDeviceUnscheduleFromRunLoop(selected, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
            IOHIDDeviceClose(selected, IOOptionBits(kIOHIDOptionsTypeNone))
        }
        selected = nil; selectedID = nil
    }
    public func disconnect() {
        closeSelected(); desiredID = nil; desiredLocation = nil
    }
    public func stop() {
        disconnect()
        if let manager {
            IOHIDManagerRegisterDeviceMatchingCallback(manager, nil, nil)
            IOHIDManagerRegisterDeviceRemovalCallback(manager, nil, nil)
            IOHIDManagerUnscheduleFromRunLoop(manager, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
            IOHIDManagerClose(manager, IOOptionBits(kIOHIDOptionsTypeNone))
        }
        manager = nil; devices.removeAll(); publishDevices(); onStatus?("USB 已停止。")
    }
    private func hex(_ value: Int32) -> String { String(format: "0x%08X", UInt32(bitPattern: value)) }
}
