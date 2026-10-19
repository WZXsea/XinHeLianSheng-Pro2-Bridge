import Foundation
import CoreBluetooth

public final class Pro2BLETransport: NSObject, Pro2InputTransport, CBCentralManagerDelegate, CBPeripheralDelegate {
    public var onConnectionChanged: ((Bool) -> Void)?
    public var onReport: ((Data) -> Void)?
    public var onStatus: ((String) -> Void)?
    public var onDevices: (([Pro2TransportDevice]) -> Void)?
    private static let commandID = CBUUID(string: "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005")
    private static let ackID = CBUUID(string: "c765a961-d9d8-4d36-a20a-5315b111836a")
    private static let inputID = CBUUID(string: "ab7de9be-89fe-49ad-828f-118f09df7fd2")
    private static let rumbleID = CBUUID(string: "cc483f51-9258-427d-a939-630c31f72b05")
    private var central: CBCentralManager!
    private var peripherals: [String: CBPeripheral] = [:]
    private var selected: CBPeripheral?
    private var command: CBCharacteristic?
    private var ack: CBCharacteristic?
    private var input: CBCharacteristic?
    private var rumble: CBCharacteristic?
    private var discovering = false
    private var desiredID: String?
    private var servicesRemaining = 0
    private var commandIndex = 0
    private var waitingAck = false
    private var writeComplete = false
    private var matchedAck = false
    private var timer: DispatchWorkItem?
    private var watchdog: DispatchSourceTimer?
    private var lastInput: TimeInterval = 0
    private var generation = 0
    private var receivedInput = false
    private var initializationComplete = false
    public override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main)
    }
    public func startDiscovery() {
        discovering = true
        if central.state == .poweredOn { scan() }
    }
    private func scan() {
        // Pro 2 advertises manufacturer data; its service UUID may be absent.
        central.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        onStatus?("蓝牙扫描中；请长按手柄配对键。")
    }
    public func connect(to id: String) {
        guard peripherals[id] != nil else { onStatus?("所选蓝牙设备待重新发现。"); return }
        disconnect()
        desiredID = id
        central.stopScan()
        beginConnection(id: id, generation: generation, attempts: 0)
    }
    private func beginConnection(id: String, generation expected: Int, attempts: Int) {
        guard generation == expected, desiredID == id, let peripheral = peripherals[id] else { return }
        // CoreBluetooth can deliver cancellation after the user selects the same
        // peripheral again. Wait for the previous session to finish first.
        guard peripheral.state == .disconnected else {
            guard attempts < 75 else { fail("前一蓝牙会话释放超时，请重新开启蓝牙。"); return }
            if peripheral.state != .disconnecting { central.cancelPeripheralConnection(peripheral) }
            deadline(seconds: 0.2) { [weak self] in
                self?.beginConnection(id: id, generation: expected, attempts: attempts + 1)
            }
            return
        }
        selected = peripheral; peripheral.delegate = self
        resetCharacteristics()
        onStatus?("蓝牙连接中：\(peripheral.name ?? "Pro Controller 2")")
        central.connect(peripheral)
        deadline(seconds: 15) { [weak self] in self?.fail("蓝牙连接超时；请检查配对模式和蓝牙权限。") }
    }
    public func disconnect() {
        onConnectionChanged?(false)
        desiredID = nil; generation += 1; timer?.cancel()
        let current = selected
        selected = nil; resetCharacteristics()
        if let current { central.cancelPeripheralConnection(current) }
    }
    public func stop() {
        disconnect(); discovering = false
        central.stopScan()
        onStatus?("蓝牙已停止。")
    }
    private func resetCharacteristics() {
        watchdog?.cancel(); watchdog = nil
        command = nil; ack = nil; input = nil; rumble = nil
        servicesRemaining = 0; commandIndex = 0; waitingAck = false; receivedInput = false; initializationComplete = false
    }
    public func writeOutput(_ report: Data) {
        guard let peripheral = selected, let rumble, peripheral.state == .connected else { return }
        let type: CBCharacteristicWriteType = rumble.properties.contains(.writeWithoutResponse) ? .withoutResponse : .withResponse
        guard report.count <= peripheral.maximumWriteValueLength(for: type) else {
            onStatus?("蓝牙输出长度超过当前链路容量。请检查震动封包。"); return
        }
        if type == .withoutResponse && !peripheral.canSendWriteWithoutResponse { return }
        peripheral.writeValue(report, for: rumble, type: type)
    }
    public func centralManagerDidUpdateState(_ central: CBCentralManager) {
        switch central.state {
        case .poweredOn: if discovering { scan() }
        case .unauthorized: onStatus?("请在系统设置的隐私与安全性中授予蓝牙权限。")
        case .poweredOff: onConnectionChanged?(false); onStatus?("请开启 Mac 蓝牙。")
        case .unsupported: onStatus?("当前蓝牙适配器需要支持 BLE。")
        default: onStatus?("蓝牙服务初始化中。")
        }
    }
    public func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                               advertisementData: [String: Any], rssi RSSI: NSNumber) {
        let name = (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? peripheral.name ?? ""
        let manufacturer = advertisementData[CBAdvertisementDataManufacturerDataKey] as? Data
        let nintendo = manufacturer.map { $0.count >= 2 && $0[$0.startIndex] == 0x53 && $0[$0.startIndex + 1] == 0x05 } ?? false
        guard nintendo || name.localizedCaseInsensitiveContains("Pro Controller") || name.localizedCaseInsensitiveContains("Pro 2") else { return }
        let id = peripheral.identifier.uuidString
        peripherals[id] = peripheral
        onDevices?(peripherals.values.map { Pro2TransportDevice(id: $0.identifier.uuidString, name: $0.name ?? "Nintendo BLE", kind: .bluetooth) }.sorted { $0.id < $1.id })
        if desiredID == id && selected == nil { connect(to: id) }
    }
    public func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard peripheral === selected else { return }
        onStatus?("蓝牙已连接，正在核验 Pro 2 GATT 特征。")
        peripheral.discoverServices(nil)
        deadline(seconds: 15) { [weak self] in self?.fail("蓝牙服务发现超时。") }
    }
    public func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        guard peripheral === selected else { return }
        reconnect(error?.localizedDescription ?? "连接失败")
    }
    public func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        guard peripheral === selected else { return }
        reconnect(error?.localizedDescription ?? "连接已断开")
    }
    private func reconnect(_ reason: String) {
        onConnectionChanged?(false)
        timer?.cancel(); selected = nil; resetCharacteristics()
        guard desiredID != nil else { return }
        onStatus?("蓝牙断开：\(reason)。正在重新扫描。")
        discovering = true
        deadline(seconds: 2) { [weak self] in if self?.central.state == .poweredOn { self?.scan() } }
    }
    public func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard peripheral === selected else { return }
        if let error { fail(error.localizedDescription); return }
        let services = peripheral.services ?? []
        servicesRemaining = services.count
        guard !services.isEmpty else { fail("设备的 GATT 服务列表为空。"); return }
        for service in services { peripheral.discoverCharacteristics(nil, for: service) }
    }
    public func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard peripheral === selected else { return }
        if let error { fail(error.localizedDescription); return }
        for characteristic in service.characteristics ?? [] {
            switch characteristic.uuid {
            case Self.commandID: command = characteristic
            case Self.ackID: ack = characteristic
            case Self.inputID: input = characteristic
            case Self.rumbleID: rumble = characteristic
            default: break
            }
        }
        servicesRemaining -= 1
        guard servicesRemaining == 0 else { return }
        guard let command, let ack, let input,
              command.properties.contains(.write), ack.properties.contains(.notify), input.properties.contains(.notify) else {
            fail("设备需提供匹配的 Pro 2 命令、ACK 和 FD2 通道。"); return
        }
        peripheral.setNotifyValue(true, for: ack)
    }
    public func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if let error { fail(error.localizedDescription); return }
        guard characteristic.isNotifying else { fail("手柄通知订阅已关闭。"); return }
        if characteristic.uuid == Self.ackID {
            if let input { peripheral.setNotifyValue(true, for: input) }
        } else if characteristic.uuid == Self.inputID {
            timer?.cancel(); sendNextCommand()
        }
    }
    private func sendNextCommand() {
        guard let peripheral = selected, let command else { return }
        guard commandIndex < BLEStartup.commands.count else {
            initializationComplete = true
            onStatus?("蓝牙初始化完成，等待首个 FD2 输入帧。")
            deadline(seconds: 6) { [weak self] in
                guard let self, !self.receivedInput else { return }
                self.fail("FD2 首帧超时，请重新配对手柄。")
            }
            return
        }
        waitingAck = true; writeComplete = false; matchedAck = false
        peripheral.writeValue(BLEStartup.commands[commandIndex], for: command, type: .withResponse)
        deadline(seconds: 2) { [weak self] in self?.fail("初始化命令 \(self?.commandIndex ?? 0) 的 ACK 超时。") }
    }
    public func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if let error { fail("蓝牙写入：\(error.localizedDescription)"); return }
        if characteristic.uuid == Self.commandID { writeComplete = true; advanceCommandIfReady() }
    }
    private func advanceCommandIfReady() {
        guard waitingAck && writeComplete && matchedAck else { return }
        timer?.cancel(); waitingAck = false; commandIndex += 1; sendNextCommand()
    }
    public func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if let error { fail(error.localizedDescription); return }
        guard let value = characteristic.value else { return }
        if characteristic.uuid == Self.ackID, waitingAck, value.count >= 8, commandIndex < BLEStartup.commands.count {
            let request = BLEStartup.commands[commandIndex]
            if value[0] == request[0] && value[3] == request[3] { matchedAck = true; advanceCommandIfReady() }
        } else if characteristic.uuid == Self.inputID && initializationComplete && value.count == 63 {
            if !receivedInput { receivedInput = true; startWatchdog(); onConnectionChanged?(true); timer?.cancel(); onStatus?("蓝牙 FD2 输入已就绪。") }
            lastInput = ProcessInfo.processInfo.systemUptime
            onReport?(value)
        }
    }
    private func startWatchdog() {
        lastInput = ProcessInfo.processInfo.systemUptime
        let source = DispatchSource.makeTimerSource(queue: .main)
        source.schedule(deadline: .now() + 1, repeating: 1)
        source.setEventHandler { [weak self] in
            guard let self, ProcessInfo.processInfo.systemUptime - self.lastInput > 3 else { return }
            self.onConnectionChanged?(false)
            self.onStatus?("蓝牙输入中断超过 3 秒，正在重连。")
            self.watchdog?.cancel(); self.watchdog = nil
            if let peripheral = self.selected { self.central.cancelPeripheralConnection(peripheral) }
        }
        watchdog = source; source.resume()
    }
    private func fail(_ message: String) {
        onConnectionChanged?(false)
        onStatus?(message)
        timer?.cancel(); desiredID = nil
        if let peripheral = selected { central.cancelPeripheralConnection(peripheral) }
        selected = nil; resetCharacteristics()
    }
    private func deadline(seconds: Double, _ action: @escaping () -> Void) {
        timer?.cancel()
        let item = DispatchWorkItem(block: action); timer = item
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: item)
    }
}
