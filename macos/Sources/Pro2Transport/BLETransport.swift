import Foundation
import CoreBluetooth

/// CoreBluetooth owns the radio and pairing UI. A connection becomes ready only
/// after the controller has delivered a structurally valid FD2 input report.
public final class Pro2BLETransport: NSObject, Pro2InputTransport, CBCentralManagerDelegate, CBPeripheralDelegate {
    public var onConnectionChanged: ((Bool) -> Void)?
    public var onReport: ((Data) -> Void)?
    public var onStatus: ((String) -> Void)?
    public var onDevices: (([Pro2TransportDevice]) -> Void)?
    public var onDiagnostics: ((Pro2BLEDiagnostics) -> Void)?
    public var onPrimaryReport: ((Data) -> Void)?
    public var onFactoryCalibration: ((UInt32, Data) -> Void)?
    public private(set) var diagnostics = Pro2BLEDiagnostics()
    public var automaticallyReconnect = true
    /// Options are captured when the next connection session starts.
    public var runtimeBatteryRefreshEnabled = true
    public var autoOptimizeReportRate = true
    public var skipInitialization = false

    private static let commandID = CBUUID(string: "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005")
    private static let ackID = CBUUID(string: "c765a961-d9d8-4d36-a20a-5315b111836a")
    private static let inputID = CBUUID(string: "ab7de9be-89fe-49ad-828f-118f09df7fd2")
    private static let primaryID = CBUUID(string: "7492866c-ec3e-4619-8258-32755ffcc0f8")
    private static let rumbleID = CBUUID(string: "cc483f51-9258-427d-a939-630c31f72b05")
    private static let knownIdentifierKey = "Pro2BLE.lastVerifiedIdentifier"
    private static let knownServicesKey = "Pro2BLE.lastVerifiedServices"
    private let log = BLEEventLog()
    private var central: CBCentralManager!
    private var peripherals: [String: CBPeripheral] = [:]
    private var names: [String: String] = [:]
    private var selected: CBPeripheral?
    private var releasing: Set<ObjectIdentifier> = []
    private var command: CBCharacteristic?
    private var ack: CBCharacteristic?
    private var input: CBCharacteristic?
    private var primary: CBCharacteristic?
    private var rumble: CBCharacteristic?
    private var desiredID: String?
    private var discovering = false
    private var includeUnmatched = false
    private var servicesRemaining = 0
    private var commandIndex = 0
    private var sessionCommands = BLEStartup.commands
    private var sessionRuntimeBattery = true
    private var sessionOptimizeReportRate = true
    private var currentCommand: Data?
    private var commandCompletion: ((Data?) -> Void)?
    private var commandReply: Data?
    private var writeComplete = false
    private var commandWriteAttempts = 0
    private var stageTimer: DispatchWorkItem?
    private var reconnectTimer: DispatchWorkItem?
    private var watchdog: DispatchSourceTimer?
    private var generation = 0
    private var receivedInput = false
    private var lastInput: TimeInterval = 0
    private var rateWindowStart: TimeInterval = 0
    private var rateWindowFrames = 0
    private var lastAggregateLog: TimeInterval = 0
    private var rateDescriptor: CBDescriptor?
    private var rateDescriptorStage = 0
    private var pendingRumble: Data?
    private var rumbleWritePending = false
    private var batteryTimer: DispatchWorkItem?
    private var batteryStage = 0
    private var batteryPendingReport: Data?
    private var batterySuspended = false
    private var lastBatteryAttempt: TimeInterval = 0
    private var lastControlActivity: TimeInterval = 0
    private var batteryBaselineHz = 0.0
    private var batteryObservationUntil: TimeInterval = 0
    private var batteryObservationStart: TimeInterval = 0
    private var batteryObservationFrames = 0
    private var batteryOperationMaxGap = 0.0

    public override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main,
            options: [CBCentralManagerOptionShowPowerAlertKey: true])
        log.event("transport_created", "platform=macOS protocol=FD2")
    }

    public func startDiscovery() { startDiscovery(includeUnmatched: false) }
    /// Manual discovery exposes other advertisements for exact UUID validation
    /// after explicit selection, matching the Windows manual connection flow.
    public func startDiscovery(includeUnmatched: Bool) {
        discovering = true
        self.includeUnmatched = includeUnmatched
        if central.state == .poweredOn { scan() }
        else { setPhase(.waitingForBluetooth, "等待系统蓝牙可用。") }
    }

    public func reconnectKnownController() {
        guard let identifier = UserDefaults.standard.string(forKey: Self.knownIdentifierKey) else {
            startDiscovery(); return
        }
        connect(to: identifier)
    }

    private func scan() {
        guard central.state == .poweredOn else { return }
        recoverKnownPeripherals()
        if !central.isScanning {
            // The proprietary service may be absent from an advertisement.
            central.scanForPeripherals(withServices: nil,
                options: [CBCentralManagerScanOptionAllowDuplicatesKey: true])
            log.event("scan_started", "manual=\(includeUnmatched)")
        }
        if selected == nil && desiredID == nil {
            setPhase(.scanning, "正在搜索蓝牙手柄；长按手柄顶部配对键，直到指示灯滚动。")
        }
        startWatchdog()
    }

    private func recoverKnownPeripherals() {
        if let text = UserDefaults.standard.string(forKey: Self.knownIdentifierKey), let id = UUID(uuidString: text) {
            for peripheral in central.retrievePeripherals(withIdentifiers: [id]) { remember(peripheral, name: peripheral.name) }
        }
        // System-connected controllers can stop advertising. They require a
        // local connect() even when another app has already connected them.
        let saved = UserDefaults.standard.stringArray(forKey: Self.knownServicesKey) ?? []
        let services = (saved + ["1800", "1801", "180A", "1812"]).map { CBUUID(string: $0) }
        for peripheral in central.retrieveConnectedPeripherals(withServices: services) {
            if BLEStartup.recognizesController(name: peripheral.name ?? "", manufacturer: nil)
                || peripheral.identifier.uuidString == desiredID {
                remember(peripheral, name: peripheral.name)
            }
        }
        publishDevices()
    }

    public func connect(to id: String) {
        guard UUID(uuidString: id) != nil else {
            diagnostics.lastError = "请从手柄列表选择有效的蓝牙设备。"; publishDiagnostics(); return
        }
        disconnect()
        desiredID = id
        discovering = true
        diagnostics.reconnectAttempt = 0
        diagnostics.lastError = ""
        if central.state == .poweredOn { beginConnection() }
        else { setPhase(.waitingForBluetooth, "连接请求已保存，等待系统蓝牙可用。") }
    }

    private func beginConnection() {
        guard central.state == .poweredOn, let id = desiredID, selected == nil else { return }
        if peripherals[id] == nil, let uuid = UUID(uuidString: id) {
            for peripheral in central.retrievePeripherals(withIdentifiers: [uuid]) { remember(peripheral, name: peripheral.name) }
        }
        guard let peripheral = peripherals[id] else {
            scan()
            setPhase(.reconnecting, "正在搜索所选手柄，等待配对广播。")
            return
        }
        if peripheral.state == .disconnecting || releasing.contains(ObjectIdentifier(peripheral)) {
            scheduleReconnect(after: 0.3)
            return
        }
        central.stopScan()
        selected = peripheral
        peripheral.delegate = self
        resetSession()
        setPhase(.connecting, "正在建立手柄蓝牙连接。")
        log.event("connect_requested", "attempt=\(diagnostics.reconnectAttempt) cached_state=\(peripheral.state.rawValue)")
        // .connected can mean connected to the system, rather than this client.
        central.connect(peripheral, options: nil)
        deadline(seconds: 20) { [weak self] in self?.fail("蓝牙连接超时；请唤醒手柄并保持配对指示灯滚动。", retry: true) }
        startWatchdog()
    }

    public func disconnect() {
        desiredID = nil
        generation += 1
        reconnectTimer?.cancel(); reconnectTimer = nil
        stageTimer?.cancel(); stageTimer = nil
        let previous = selected
        selected = nil
        resetSession()
        onConnectionChanged?(false)
        if let previous {
            previous.delegate = nil
            if previous.state != .disconnected { releasing.insert(ObjectIdentifier(previous)) }
            central.cancelPeripheralConnection(previous)
        }
        diagnostics.reconnectAttempt = 0
        setPhase(.idle, "手柄连接已释放。")
        log.event("user_disconnect")
    }

    public func stop() {
        discovering = false
        disconnect()
        central.stopScan()
        watchdog?.cancel(); watchdog = nil
        setPhase(.idle, "蓝牙服务已停止。")
    }

    private func resetSession() {
        command = nil; ack = nil; input = nil; primary = nil; rumble = nil
        servicesRemaining = 0; commandIndex = 0
        currentCommand = nil; commandCompletion = nil; commandReply = nil; writeComplete = false
        sessionCommands = BLEStartup.initializationCommands(skipUSBInitialization: skipInitialization)
        sessionRuntimeBattery = runtimeBatteryRefreshEnabled
        sessionOptimizeReportRate = autoOptimizeReportRate
        commandWriteAttempts = 0; receivedInput = false; lastInput = 0
        rateDescriptor = nil; rateDescriptorStage = 0
        pendingRumble = nil; rumbleWritePending = false
        batteryTimer?.cancel(); batteryTimer = nil; batteryStage = 0; batteryPendingReport = nil
        batterySuspended = false; batteryObservationUntil = 0; lastBatteryAttempt = 0; lastControlActivity = 0
        diagnostics.initializationStep = 0; diagnostics.acknowledgedCommands = 0; diagnostics.ackTimeouts = 0
        diagnostics.initializationTotal = sessionCommands.count
        diagnostics.receivedFrames = 0; diagnostics.malformedFrames = 0
        diagnostics.measuredHz = 0; diagnostics.maximumGapMilliseconds = 0
        diagnostics.reportRateStatus = "等待 FD2 descriptor"
        diagnostics.controllerFirmware = "等待手柄返回"
        diagnostics.bluetoothFirmware = "等待手柄返回"
        diagnostics.dspFirmware = "等待手柄返回"
        diagnostics.runtimeBatteryStatus = sessionRuntimeBattery ? "等待 10 分钟及操作空闲" : "本次连接已关闭定期更新"
        diagnostics.serviceCount = 0; diagnostics.characteristicSummary = "等待 GATT 服务"
        rateWindowStart = ProcessInfo.processInfo.systemUptime; rateWindowFrames = 0
    }

    public func writeOutput(_ report: Data) {
        guard receivedInput, selected?.state == .connected, rumble != nil else { return }
        pendingRumble = report
        flushRumble()
    }

    private func flushRumble() {
        guard let peripheral = selected, let rumble, let report = pendingRumble else { return }
        let type: CBCharacteristicWriteType = rumble.properties.contains(.writeWithoutResponse) ? .withoutResponse : .withResponse
        guard rumble.properties.contains(.writeWithoutResponse) || rumble.properties.contains(.write) else { return }
        guard report.count <= peripheral.maximumWriteValueLength(for: type) else {
            diagnostics.lastError = "当前震动报文长度超过蓝牙单次写入容量。"; publishDiagnostics(); return
        }
        if type == .withoutResponse && !peripheral.canSendWriteWithoutResponse { return }
        if type == .withResponse && rumbleWritePending { return }
        pendingRumble = nil
        rumbleWritePending = type == .withResponse
        peripheral.writeValue(report, for: rumble, type: type)
    }

    public func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
        guard peripheral === selected else { return }; flushRumble()
    }

    /// A fresh primary notification supplies native battery status. It never
    /// enters the gameplay input route, and its subscription is immediately
    /// released. The FD2 channel remains continuously subscribed.
    public func refreshBatteryIfIdle() {
        let now = ProcessInfo.processInfo.systemUptime
        guard BLEBatteryRefreshPolicy.canRefresh(enabled: sessionRuntimeBattery,
            ready: diagnostics.phase == .ready, quietSeconds: now - lastControlActivity,
            elapsedSeconds: now - lastBatteryAttempt, inputAge: now - lastInput,
            measuredHz: diagnostics.measuredHz, operationInFlight: batteryStage != 0 || currentCommand != nil,
            suspended: batterySuspended),
            let peripheral = selected, let primary, primary.properties.contains(.notify), !primary.isNotifying else { return }
        lastBatteryAttempt = now
        batteryStage = 1; batteryPendingReport = nil
        batteryBaselineHz = diagnostics.measuredHz
        batteryObservationStart = now; batteryObservationFrames = diagnostics.receivedFrames
        batteryOperationMaxGap = 0
        diagnostics.runtimeBatteryStatus = "正在读取一次原生电量通知"
        log.event("battery_runtime_start", "baseline_hz=\(String(format: "%.2f", batteryBaselineHz))")
        peripheral.setNotifyValue(true, for: primary)
        batteryDeadline(seconds: 2) { [weak self] in self?.releaseBatterySubscription() }
        publishDiagnostics()
    }

    private func releaseBatterySubscription() {
        guard batteryStage != 0, let peripheral = selected, let primary else { return }
        batteryStage = 3
        peripheral.setNotifyValue(false, for: primary)
        batteryDeadline(seconds: 2) { [weak self] in
            guard let self else { return }
            self.batterySuspended = true
            self.diagnostics.runtimeBatteryStatus = "电量通知释放等待超时；本次连接保留被动电源监测"
            self.batteryStage = 0; self.batteryPendingReport = nil
            self.log.event("battery_runtime_cleanup", "confirmed=false suspended=true")
            self.publishDiagnostics()
        }
    }

    private func finishBatterySnapshot(released: Bool) {
        batteryTimer?.cancel(); batteryTimer = nil
        batteryStage = 0
        if released, let report = batteryPendingReport {
            onPrimaryReport?(report)
            diagnostics.runtimeBatteryStatus = "运行时电量已更新，原生通知已释放"
        } else {
            diagnostics.runtimeBatteryStatus = "本次电量快照等待后续时机，FD2 输入继续"
        }
        batteryPendingReport = nil
        if !released { batterySuspended = true }
        batteryObservationUntil = ProcessInfo.processInfo.systemUptime + 20
        log.event("battery_runtime_cleanup", "confirmed=\(released)")
        publishDiagnostics()
    }

    private func batteryDeadline(seconds: TimeInterval, _ action: @escaping () -> Void) {
        batteryTimer?.cancel()
        let expected = generation
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.generation == expected, self.selected != nil, self.batteryStage != 0 else { return }
            action()
        }
        batteryTimer = item
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: item)
    }

    public func centralManagerDidUpdateState(_ central: CBCentralManager) {
        diagnostics.bluetoothState = String(describing: central.state)
        log.event("central_state", "state=\(central.state.rawValue) authorization=\(CBManager.authorization.rawValue)")
        switch central.state {
        case .poweredOn:
            if desiredID != nil { beginConnection() }
            else if discovering { scan() }
        case .unauthorized:
            fail("请在系统设置 → 隐私与安全性 → 蓝牙中，开启 PRO2 手柄助手的蓝牙权限。", retry: false)
        case .poweredOff:
            releaseForRadioChange()
            setPhase(.waitingForBluetooth, "请开启 Mac 蓝牙；已选择手柄将在蓝牙恢复后重新连接。")
        case .unsupported:
            fail("当前系统蓝牙适配器需要具备 BLE 中央设备能力。", retry: false)
        default:
            releaseForRadioChange()
            setPhase(.waitingForBluetooth, "系统蓝牙服务正在初始化。")
        }
        publishDiagnostics()
    }

    private func releaseForRadioChange() {
        stageTimer?.cancel(); reconnectTimer?.cancel()
        selected?.delegate = nil; selected = nil
        releasing.removeAll()
        resetSession(); onConnectionChanged?(false)
    }

    public func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
        advertisementData: [String: Any], rssi RSSI: NSNumber) {
        diagnostics.scannedAdvertisements += 1
        let name = (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? peripheral.name ?? ""
        let manufacturer = advertisementData[CBAdvertisementDataManufacturerDataKey] as? Data
        let known = peripheral.identifier.uuidString == UserDefaults.standard.string(forKey: Self.knownIdentifierKey)
        guard includeUnmatched || known || BLEStartup.recognizesController(name: name, manufacturer: manufacturer) else { return }
        let isNew = peripherals[peripheral.identifier.uuidString] == nil
        remember(peripheral, name: name)
        if isNew {
            log.event("candidate_found", "identified=\(known || BLEStartup.recognizesController(name: name, manufacturer: manufacturer)) rssi=\(RSSI.intValue)")
            publishDevices()
        }
        if desiredID == peripheral.identifier.uuidString && selected == nil { beginConnection() }
    }

    private func remember(_ peripheral: CBPeripheral, name: String?) {
        let id = peripheral.identifier.uuidString
        peripherals[id] = peripheral
        if let name, !name.isEmpty { names[id] = name }
        diagnostics.candidateCount = peripherals.count
    }

    private func publishDevices() {
        onDevices?(peripherals.values.map {
            Pro2TransportDevice(id: $0.identifier.uuidString, name: names[$0.identifier.uuidString] ?? $0.name ?? "BLE 设备", kind: .bluetooth)
        }.sorted { $0.name == $1.name ? $0.id < $1.id : $0.name < $1.name })
        publishDiagnostics()
    }

    public func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard peripheral === selected, desiredID != nil else { return }
        setPhase(.discovering, "蓝牙连接已建立，正在验证 Pro 2 命令、ACK 和 FD2 服务。")
        log.event("link_connected", "mtu_write=\(peripheral.maximumWriteValueLength(for: .withResponse))")
        peripheral.discoverServices(nil)
        deadline(seconds: 15) { [weak self] in self?.fail("GATT 服务发现超时，正在重新连接。", retry: true) }
    }

    public func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        if releasing.remove(ObjectIdentifier(peripheral)) != nil { return }
        guard peripheral === selected else { return }
        recordError("connect_failed", error)
        fail(connectionError(error, fallback: "蓝牙连接失败，正在重新尝试。"), retry: true)
    }

    public func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        if releasing.remove(ObjectIdentifier(peripheral)) != nil { return }
        guard peripheral === selected else { return }
        recordError("link_disconnected", error)
        selected = nil; onConnectionChanged?(false)
        guard desiredID != nil else { return }
        fail(connectionError(error, fallback: "手柄蓝牙已断开，正在重新连接。"), retry: true)
    }

    public func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard peripheral === selected else { return }
        if let error { recordError("services_error", error); fail("GATT 服务读取失败：\(error.localizedDescription)", retry: true); return }
        let services = peripheral.services ?? []
        diagnostics.serviceCount = services.count
        log.event("services_discovered", "count=\(services.count)")
        servicesRemaining = services.count
        guard !services.isEmpty else { fail("手柄 GATT 服务列表为空，正在重新发现。", retry: true); return }
        for service in services { peripheral.discoverCharacteristics(nil, for: service) }
    }

    public func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard peripheral === selected else { return }
        if let error { recordError("characteristics_error", error) }
        for characteristic in service.characteristics ?? [] {
            switch characteristic.uuid {
            case Self.commandID: command = characteristic
            case Self.ackID: ack = characteristic
            case Self.inputID: input = characteristic
            case Self.primaryID: primary = characteristic
            case Self.rumbleID: rumble = characteristic
            default: break
            }
        }
        servicesRemaining -= 1
        guard servicesRemaining == 0 else { return }
        diagnostics.characteristicSummary = "命令 \(command != nil ? "✓" : "待确认") · ACK \(ack != nil ? "✓" : "待确认") · FD2 \(input != nil ? "✓" : "待确认") · 震动 \(rumble != nil ? "✓" : "待确认")"
        log.event("gatt_identity", "command=\(command != nil) ack=\(ack != nil) fd2=\(input != nil) primary=\(primary != nil) rumble=\(rumble != nil)")
        guard let command, let ack, let input,
              command.properties.contains(.write),
              ack.properties.contains(.notify) || ack.properties.contains(.indicate),
              input.properties.contains(.notify) || input.properties.contains(.indicate) else {
            fail("所选设备需要提供匹配的 Pro 2 命令、ACK 和 FD2 通道；请重新选择手柄。", retry: false); return
        }
        setPhase(.subscribingACK, "Pro 2 身份已验证，正在订阅命令回复。")
        peripheral.setNotifyValue(true, for: ack)
        deadline(seconds: 10) { [weak self] in self?.fail("ACK 通道订阅超时，正在重新连接。", retry: true) }
    }

    public func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if characteristic.uuid == Self.primaryID {
            guard batteryStage != 0 else { return }
            if let error {
                recordError("battery_runtime_subscription_error", error)
                if batteryStage < 3 { releaseBatterySubscription() }
                else { finishBatterySnapshot(released: !characteristic.isNotifying) }
            } else if batteryStage == 1 && characteristic.isNotifying {
                batteryStage = 2
                batteryDeadline(seconds: 0.5) { [weak self] in self?.releaseBatterySubscription() }
            } else if batteryStage == 3 && !characteristic.isNotifying { finishBatterySnapshot(released: true) }
            return
        }
        if let error { recordError("subscription_error", error); fail(connectionError(error, fallback: "蓝牙通知订阅失败。"), retry: true); return }
        guard characteristic.isNotifying else { fail("手柄输入通知已关闭，正在重新连接。", retry: true); return }
        if characteristic.uuid == Self.ackID && diagnostics.phase == .subscribingACK {
            stageTimer?.cancel(); commandIndex = 0; sendNextStartupCommand()
        } else if characteristic.uuid == Self.inputID && diagnostics.phase == .subscribingInput {
            setPhase(.waitingForInput, "FD2 通知已订阅，正在等待真实按键和陀螺仪输入。")
            deadline(seconds: 6) { [weak self] in self?.fail("FD2 首帧等待超时；请唤醒手柄并保持蓝牙连接。", retry: true) }
        }
    }

    private func sendNextStartupCommand() {
        guard commandIndex < sessionCommands.count else { readCalibration(block: 0, offset: 0, collected: Data()); return }
        diagnostics.initializationStep = commandIndex + 1
        setPhase(.initializing, "正在初始化手柄：\(commandIndex + 1)/\(sessionCommands.count)。")
        sendCommand(sessionCommands[commandIndex]) { [weak self] _ in
            guard let self else { return }; self.commandIndex += 1; self.sendNextStartupCommand()
        }
    }

    private func sendCommand(_ request: Data, completion: @escaping (Data?) -> Void) {
        guard let peripheral = selected, let command, peripheral.state == .connected else { return }
        currentCommand = request; commandCompletion = completion; commandReply = nil
        writeComplete = false; commandWriteAttempts = 1
        let bytes = Array(request)
        log.event("command_write", "command=\(bytes[0]) subcommand=\(bytes[3]) bytes=\(bytes.count)")
        peripheral.writeValue(request, for: command, type: .withResponse)
        deadline(seconds: 4) { [weak self] in self?.fail("蓝牙命令写入确认超时，正在重新连接。", retry: true) }
    }

    public func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if characteristic.uuid == Self.rumbleID {
            rumbleWritePending = false
            if let error { recordError("rumble_write_error", error) }
            flushRumble(); return
        }
        guard characteristic.uuid == Self.commandID, let request = currentCommand else { return }
        if let error {
            recordError("command_write_error", error)
            if commandWriteAttempts < 3 {
                commandWriteAttempts += 1
                deadline(seconds: 0.3 * Double(commandWriteAttempts)) { [weak self, weak peripheral] in
                    guard let self, let peripheral, let command = self.command, self.currentCommand == request else { return }
                    peripheral.writeValue(request, for: command, type: .withResponse)
                    self.deadline(seconds: 4) { [weak self] in self?.fail("蓝牙命令重试超时。", retry: true) }
                }
            } else { fail(connectionError(error, fallback: "蓝牙命令写入失败。"), retry: true) }
            return
        }
        writeComplete = true
        if commandReply != nil { finishCommand() }
        else {
            deadline(seconds: 1.5) { [weak self] in
                guard let self, self.currentCommand != nil else { return }
                self.diagnostics.ackTimeouts += 1
                self.log.event("command_ack_timeout", "step=\(self.diagnostics.initializationStep) continue=true")
                // Matches Windows: a successful ATT write with a missing optional
                // reply may continue; live FD2 input remains the acceptance gate.
                self.finishCommand()
            }
        }
    }

    private func finishCommand() {
        stageTimer?.cancel()
        let completion = commandCompletion, reply = commandReply
        currentCommand = nil; commandCompletion = nil; commandReply = nil; writeComplete = false
        completion?(reply)
    }

    private func readCalibration(block index: Int, offset: Int, collected: Data) {
        guard index < BLEStartup.calibrationBlocks.count else { readInitialBattery(); return }
        let (base, length) = BLEStartup.calibrationBlocks[index]
        if offset >= length {
            onFactoryCalibration?(base, collected)
            log.event("factory_calibration", "block=\(index) bytes=\(collected.count)")
            readCalibration(block: index + 1, offset: 0, collected: Data()); return
        }
        setPhase(.readingCalibration, "正在读取原厂陀螺仪校准数据。")
        let address = base + UInt32(offset), expectedLength = min(9, length - offset)
        sendCommand(BLEStartup.flashRead(address: address)) { [weak self] reply in
            guard let self else { return }
            if let reply, let chunk = BLEStartup.flashPayload(reply: reply, address: address, length: expectedLength) {
                self.readCalibration(block: index, offset: offset + expectedLength, collected: collected + chunk)
            } else {
                self.log.event("factory_calibration_deferred", "block=\(index)")
                self.readCalibration(block: index + 1, offset: 0, collected: Data())
            }
        }
    }

    private func readInitialBattery() {
        guard let peripheral = selected else { return }
        guard let primary, primary.properties.contains(.read) else { configureReportRate(); return }
        setPhase(.readingBattery, "正在读取连接电量。")
        peripheral.readValue(for: primary)
        deadline(seconds: 2) { [weak self] in
            self?.log.event("connection_battery", "result=timeout")
            self?.configureReportRate()
        }
    }

    private func configureReportRate() {
        guard let peripheral = selected, let input else { return }
        guard sessionOptimizeReportRate else {
            diagnostics.reportRateStatus = "本次连接采用系统协商报告率"
            subscribeInput(); return
        }
        setPhase(.configuringRate, "正在设置 FD2 报告率 133 Hz。")
        peripheral.discoverDescriptors(for: input)
        deadline(seconds: 3) { [weak self] in
            self?.diagnostics.reportRateStatus = "descriptor 发现超时；使用实测频率"
            self?.subscribeInput()
        }
    }

    public func peripheral(_ peripheral: CBPeripheral, didDiscoverDescriptorsFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected, characteristic.uuid == Self.inputID, diagnostics.phase == .configuringRate else { return }
        let descriptors = characteristic.descriptors ?? []
        log.event("fd2_descriptors", "count=\(descriptors.count) uuids=\(descriptors.map { $0.uuid.uuidString }.joined(separator: ","))")
        if let error { recordError("descriptor_discovery_error", error) }
        // CoreBluetooth hides ATT handles. The Windows fallback also selects a
        // sole non-CCCD descriptor, so use that policy for the same FD2 channel.
        let candidates = descriptors.filter { $0.uuid != CBUUID(string: "2902") }
        guard error == nil, candidates.count == 1, let descriptor = candidates.first else {
            diagnostics.reportRateStatus = "使用系统协商频率；报告率 descriptor 待确认"
            subscribeInput(); return
        }
        rateDescriptor = descriptor; rateDescriptorStage = 1
        peripheral.readValue(for: descriptor)
        deadline(seconds: 2) { [weak self] in self?.writeReportRate() }
    }

    private func writeReportRate() {
        guard let peripheral = selected, let descriptor = rateDescriptor, diagnostics.phase == .configuringRate else { return }
        rateDescriptorStage = 2
        peripheral.writeValue(Data([0x85, 0x00]), for: descriptor)
        deadline(seconds: 2) { [weak self] in
            self?.diagnostics.reportRateStatus = "133 Hz 请求等待确认；使用实测频率"
            self?.subscribeInput()
        }
    }

    public func peripheral(_ peripheral: CBPeripheral, didWriteValueFor descriptor: CBDescriptor, error: Error?) {
        guard peripheral === selected, descriptor === rateDescriptor, diagnostics.phase == .configuringRate else { return }
        if let error {
            recordError("rate_descriptor_write_error", error)
            diagnostics.reportRateStatus = "系统保留当前报告率；使用实测频率"
            subscribeInput(); return
        }
        diagnostics.reportRateStatus = "133 Hz 请求写入成功；等待读回"
        rateDescriptorStage = 3
        peripheral.readValue(for: descriptor)
        deadline(seconds: 2) { [weak self] in self?.subscribeInput() }
    }

    public func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor descriptor: CBDescriptor, error: Error?) {
        guard peripheral === selected, descriptor === rateDescriptor, diagnostics.phase == .configuringRate else { return }
        if rateDescriptorStage == 1 { writeReportRate(); return }
        guard rateDescriptorStage == 3 else { return }
        if let value = descriptor.value as? Data, Array(value).prefix(2) == [0x85, 0x00] {
            diagnostics.reportRateStatus = "133 Hz 已写入并读回确认"
        } else { diagnostics.reportRateStatus = "133 Hz 已请求；实际频率以输入测量为准" }
        log.event("report_rate", "requested_hz=133 readback_verified=\(diagnostics.reportRateStatus.contains("读回确认"))")
        subscribeInput()
    }

    private func subscribeInput() {
        guard let peripheral = selected, let input else { return }
        stageTimer?.cancel()
        setPhase(.subscribingInput, "初始化完成，正在订阅独占 FD2 输入。")
        peripheral.setNotifyValue(true, for: input)
        deadline(seconds: 10) { [weak self] in self?.fail("FD2 通知订阅超时，正在重新连接。", retry: true) }
    }

    public func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard peripheral === selected else { return }
        if characteristic.uuid == Self.primaryID {
            if batteryStage == 2, error == nil, let value = characteristic.value,
               value.count >= 11, ((value[1] >> 2) & 15) <= 9 {
                batteryPendingReport = value
                releaseBatterySubscription()
                return
            }
            if diagnostics.phase == .readingBattery {
                stageTimer?.cancel()
                if error == nil, let value = characteristic.value, value.count >= 11 { onPrimaryReport?(value) }
                log.event("connection_battery", "read_success=\(error == nil) bytes=\(characteristic.value?.count ?? 0)")
                configureReportRate()
            }
            return
        }
        if let error { recordError("notification_error", error); return }
        guard let value = characteristic.value else { return }
        if characteristic.uuid == Self.ackID {
            if let firmware = Pro2BLEFirmware.decode(value) {
                diagnostics.controllerFirmware = firmware.controller
                diagnostics.bluetoothFirmware = firmware.bluetooth ?? "手柄回复待提供"
                diagnostics.dspFirmware = firmware.dsp ?? "手柄回复待提供"
            }
            guard let request = currentCommand, BLEStartup.acknowledgementMatches(request: request, reply: value) else { return }
            // Flash replies share command/subcommand, so also match the address.
            if request.count == 16 && request[0] == 0x02 && value.count >= 16 && request[12..<16] != value[12..<16] { return }
            commandReply = value
            diagnostics.acknowledgedCommands += 1
            if writeComplete { finishCommand() }
            return
        }
        guard characteristic.uuid == Self.inputID,
              [.subscribingInput, .waitingForInput, .ready].contains(diagnostics.phase) else { return }
        // Parser accepts the FD2 motion-bearing format from 60 bytes onward.
        let bytes = Array(value)
        guard bytes.count >= 60,
              (UInt32(bytes[4]) | UInt32(bytes[5]) << 8 | UInt32(bytes[6]) << 16 | UInt32(bytes[7]) << 24) & 0xEC300020 == 0 else {
            diagnostics.malformedFrames += 1; return
        }
        let now = ProcessInfo.processInfo.systemUptime
        if lastInput > 0 {
            let gap = (now - lastInput) * 1000
            diagnostics.maximumGapMilliseconds = max(diagnostics.maximumGapMilliseconds, gap)
            if batteryStage != 0 { batteryOperationMaxGap = max(batteryOperationMaxGap, gap) }
        }
        let buttons = UInt32(bytes[4]) | UInt32(bytes[5]) << 8 | UInt32(bytes[6]) << 16 | UInt32(bytes[7]) << 24
        if buttons & 0x03CF7FCF != 0 || sticksActive(bytes) { lastControlActivity = now }
        lastInput = now
        diagnostics.receivedFrames += 1
        if !receivedInput {
            receivedInput = true; stageTimer?.cancel()
            rateWindowStart = now; rateWindowFrames = diagnostics.receivedFrames
            diagnostics.reconnectAttempt = 0; diagnostics.lastError = ""
            lastBatteryAttempt = now; lastControlActivity = now
            UserDefaults.standard.set(peripheral.identifier.uuidString, forKey: Self.knownIdentifierKey)
            UserDefaults.standard.set((peripheral.services ?? []).map { $0.uuid.uuidString }, forKey: Self.knownServicesKey)
            setPhase(.ready, "蓝牙 FD2 输入已就绪，按键、摇杆及陀螺仪开始实时接收。")
            log.event("fd2_first_input", "bytes=\(value.count) acked=\(diagnostics.acknowledgedCommands) ack_timeouts=\(diagnostics.ackTimeouts)")
            onConnectionChanged?(true)
        }
        onReport?(value)
    }

    private func sticksActive(_ bytes: [UInt8]) -> Bool {
        let leftX = Int(bytes[10]) | Int(bytes[11] & 15) << 8
        let leftY = Int(bytes[11] >> 4) | Int(bytes[12]) << 4
        let rightX = Int(bytes[13]) | Int(bytes[14] & 15) << 8
        let rightY = Int(bytes[14] >> 4) | Int(bytes[15]) << 4
        return [leftX, leftY, rightX, rightY].contains { abs($0 - 2048) > 96 }
    }

    private func startWatchdog() {
        guard watchdog == nil else { return }
        let source = DispatchSource.makeTimerSource(queue: .main)
        source.schedule(deadline: .now() + 1, repeating: 1)
        source.setEventHandler { [weak self] in
            guard let self else { return }
            let now = ProcessInfo.processInfo.systemUptime
            if self.receivedInput && now - self.lastInput > 3 {
                self.fail("蓝牙输入间隔超过 3 秒，正在重新连接。", retry: true)
            }
            if self.receivedInput, now > self.rateWindowStart {
                self.diagnostics.measuredHz = Double(self.diagnostics.receivedFrames - self.rateWindowFrames) / (now - self.rateWindowStart)
                self.rateWindowStart = now; self.rateWindowFrames = self.diagnostics.receivedFrames
            }
            if self.receivedInput && self.batteryObservationUntil > 0 && now >= self.batteryObservationUntil {
                self.batteryObservationUntil = 0
                let hz = Double(self.diagnostics.receivedFrames - self.batteryObservationFrames) / (now - self.batteryObservationStart)
                let retained = hz >= self.batteryBaselineHz * 0.75 && self.batteryOperationMaxGap <= 45
                if !retained {
                    self.batterySuspended = true
                    self.diagnostics.runtimeBatteryStatus = "电量刷新关联输入波动；本次连接保留被动电源监测"
                }
                self.log.event("battery_runtime_observation", "retained=\(retained) hz=\(String(format: "%.2f", hz)) operation_gap_ms=\(String(format: "%.2f", self.batteryOperationMaxGap))")
            }
            self.publishDiagnostics()
            if now - self.lastAggregateLog >= 10 {
                self.lastAggregateLog = now
                self.log.event("aggregate", "phase=\(self.diagnostics.phase) advertisements=\(self.diagnostics.scannedAdvertisements) candidates=\(self.diagnostics.candidateCount) frames=\(self.diagnostics.receivedFrames) malformed=\(self.diagnostics.malformedFrames) hz=\(String(format: "%.2f", self.diagnostics.measuredHz)) max_gap_ms=\(String(format: "%.2f", self.diagnostics.maximumGapMilliseconds))")
            }
        }
        watchdog = source; source.resume()
    }

    private func fail(_ message: String, retry: Bool) {
        stageTimer?.cancel(); stageTimer = nil
        diagnostics.lastError = message
        onConnectionChanged?(false)
        let previous = selected
        selected = nil; previous?.delegate = nil
        if let previous {
            if previous.state != .disconnected { releasing.insert(ObjectIdentifier(previous)) }
            central.cancelPeripheralConnection(previous)
        }
        receivedInput = false; currentCommand = nil; commandCompletion = nil
        pendingRumble = nil; rumbleWritePending = false
        batteryTimer?.cancel(); batteryTimer = nil; batteryStage = 0; batteryPendingReport = nil
        log.event("session_failure", "phase=\(diagnostics.phase) retry=\(retry) attempt=\(diagnostics.reconnectAttempt)")
        if retry && automaticallyReconnect && desiredID != nil && central.state == .poweredOn {
            diagnostics.reconnectAttempt += 1
            setPhase(.reconnecting, message)
            scheduleReconnect(after: BLEStartup.reconnectDelay(attempt: diagnostics.reconnectAttempt))
        } else { desiredID = nil; setPhase(.failed, message) }
    }

    private func scheduleReconnect(after seconds: TimeInterval) {
        reconnectTimer?.cancel()
        let expected = generation
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.generation == expected, self.desiredID != nil else { return }
            self.scan(); self.beginConnection()
        }
        reconnectTimer = item
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: item)
    }

    private func deadline(seconds: TimeInterval, _ action: @escaping () -> Void) {
        stageTimer?.cancel()
        let expected = generation
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.generation == expected, self.selected != nil else { return }
            action()
        }
        stageTimer = item
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: item)
    }

    private func setPhase(_ phase: Pro2BLEPhase, _ status: String) {
        diagnostics.phase = phase
        onStatus?(status)
        publishDiagnostics()
    }
    private func publishDiagnostics() { onDiagnostics?(diagnostics) }
    private func recordError(_ event: String, _ error: Error?) {
        let value = error as NSError?
        log.event(event, "domain=\(value?.domain ?? "none") code=\(value?.code ?? 0)")
    }
    private func connectionError(_ error: Error?, fallback: String) -> String {
        guard let error = error as NSError? else { return fallback }
        if error.domain == CBATTErrorDomain && [5, 8, 15].contains(error.code) {
            return "手柄要求蓝牙认证。请完成 macOS 配对提示，随后重新连接。"
        }
        if error.domain == CBErrorDomain && error.code == CBError.peerRemovedPairingInformation.rawValue {
            return "手柄配对记录已更新。请在系统蓝牙设置中忽略旧手柄记录，再长按配对键连接。"
        }
        return fallback + "（\(error.domain) \(error.code)）"
    }
}
