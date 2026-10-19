import Foundation
import Darwin
import Pro2Core
import Pro2Transport

private final class JSONLineWriter {
    private let queue = DispatchQueue(label:"io.github.wzxsea.pro2bridge.nativehost.stdout")
    private let lock = NSLock()
    private var pendingRaw = 0
    private var droppedRaw = 0
    var failure: (() -> Void)?
    func emit(_ object: [String: Any], rawInput: Bool = false) {
        guard JSONSerialization.isValidJSONObject(object), let data = try? JSONSerialization.data(withJSONObject:object,options:[.sortedKeys]) else { return }
        if rawInput {
            lock.lock()
            guard pendingRaw < 1024 else { droppedRaw += 1; lock.unlock(); return }
            pendingRaw += 1; lock.unlock()
        }
        queue.async { [weak self] in
            guard let self else { return }
            do {
                var line = data; line.append(10)
                try FileHandle.standardOutput.write(contentsOf:line)
            } catch { DispatchQueue.main.async { self.failure?() } }
            if rawInput {
                self.lock.lock(); self.pendingRaw -= 1
                let dropped = self.pendingRaw < 512 ? self.droppedRaw : 0
                if dropped > 0 { self.droppedRaw = 0 }; self.lock.unlock()
                if dropped > 0 {
                    let event: [String:Any] = ["event":"error","code":"stdout_backpressure","message":"接收端读取延迟导致原始输入队列达到容量。","droppedFrames":dropped]
                    self.emit(event)
                }
            }
        }
    }
    func finish(_ code: Int32) { queue.async { Darwin.exit(code) } }
}

private struct HostSettings {
    var runtimeBattery = true
    var autoOptimize = true
    var skipInitialization = false
    var autoReconnect = true
    var gyroScales = NativeGyroScales()
    func apply(_ transport: Pro2BLETransport) {
        transport.runtimeBatteryRefreshEnabled = runtimeBattery
        transport.autoOptimizeReportRate = autoOptimize
        transport.skipInitialization = skipInitialization
        transport.automaticallyReconnect = autoReconnect
    }
}

private final class USBTakeover {
    let usbDeviceID: String
    let bluetoothDeviceID: String
    let originalAutoReconnect: Bool
    var bluetoothConnected = true
    var usbConnectReturned = false
    var bluetoothReconnectRequested = false
    var pendingPrimary: Data?
    var policy = USBTakeoverPolicy()
    var timer: DispatchWorkItem?
    init(usb: String, bluetooth: String, autoReconnect: Bool) {
        usbDeviceID = usb; bluetoothDeviceID = bluetooth; originalAutoReconnect = autoReconnect
    }
    func cancel() { timer?.cancel(); timer = nil; policy.cancel() }
}

private final class NativeSlot {
    let index: Int
    var bluetooth: Pro2BLETransport?
    var usb: Pro2USBTransport?
    var selectedKind: Pro2TransportKind?
    var selectedDeviceID: String?
    var connected = false
    var pendingUSB: String?
    var pendingUSBTimer: DispatchWorkItem?
    var devices: [Pro2TransportKind: [Pro2TransportDevice]] = [:]
    var packetID: UInt8 = 0
    var takeover: USBTakeover?
    let output = NativeOutput()
    init(_ index: Int) { self.index = index }
    var selectedTransport: Pro2InputTransport? {
        switch selectedKind { case .bluetooth: return bluetooth; case .usb: return usb; case nil: return nil }
    }
    func stop() {
        takeover?.cancel(); takeover = nil
        pendingUSBTimer?.cancel(); pendingUSBTimer = nil; pendingUSB = nil
        bluetooth?.stop(); usb?.stop(); output.stop()
        bluetooth = nil; usb = nil; connected = false
        selectedKind = nil; selectedDeviceID = nil; devices.removeAll()
    }
}

private final class NativeHost {
    let writer = JSONLineWriter()
    private var slots: [Int:NativeSlot] = [:]
    private var settings = HostSettings()
    private var shuttingDown = false
    private let inputQueue = DispatchQueue(label:"io.github.wzxsea.pro2bridge.nativehost.stdin")
    private var signalSources: [DispatchSourceSignal] = []

    func run() {
        signal(SIGPIPE,SIG_IGN)
        for signalNumber in [SIGINT,SIGTERM] {
            signal(signalNumber,SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal:signalNumber,queue:.main)
            source.setEventHandler { [weak self] in self?.shutdown() }
            signalSources.append(source); source.resume()
        }
        writer.failure = { [weak self] in self?.shutdown(code:1) }
        writer.emit(["event":"ready","protocolVersion":1,"platform":"macOS","slots":4,
            "inputFormats":["usb","fd2"],"stateContract":"GamepadState-v1"])
        inputQueue.async { [weak self] in
            var buffered = Data()
            var chunk = [UInt8](repeating:0,count:16384)
            do {
                // POSIX read returns the bytes currently available on a pipe.
                // Foundation's sized read can accumulate until its requested
                // length or EOF, which stalls an interactive JSONL connection.
                while true {
                    let count = chunk.withUnsafeMutableBytes { Darwin.read(STDIN_FILENO,$0.baseAddress,$0.count) }
                    if count == 0 { break }
                    if count < 0 {
                        if errno == EINTR { continue }
                        throw NSError(domain:NSPOSIXErrorDomain,code:Int(errno))
                    }
                    buffered.append(contentsOf:chunk.prefix(count))
                    while let newline = buffered.firstIndex(of:10) {
                        let line = Data(buffered[..<newline]); buffered.removeSubrange(...newline)
                        guard !line.isEmpty else { continue }
                        DispatchQueue.main.async { self?.process(line) }
                    }
                    if buffered.count > 1_048_576 {
                        buffered.removeAll(keepingCapacity:true)
                        DispatchQueue.main.async { self?.writer.emit(["event":"error","code":"command_too_large","message":"单条 JSON 命令容量上限为 1 MiB。"] ) }
                    }
                }
                if !buffered.isEmpty { let last = buffered; DispatchQueue.main.async { self?.process(last) } }
                DispatchQueue.main.async { self?.shutdown() }
            } catch {
                DispatchQueue.main.async { self?.writer.emit(["event":"error","code":"stdin_error","message":"标准输入读取失败。"]); self?.shutdown(code:1) }
            }
        }
        RunLoop.main.run()
    }

    private func process(_ data: Data) {
        guard !shuttingDown else { return }
        var request: [String:Any] = [:]
        do {
            guard data.count <= 1_048_576, let object = try JSONSerialization.jsonObject(with:data) as? [String:Any] else {
                throw NativeHostFailure("invalid_json", "每行需要一个 JSON 对象。")
            }
            request = object
            let command = (request["command"] ?? request["cmd"] ?? request["type"]) as? String ?? ""
            switch command {
            case "enumerate": try enumerate(request)
            case "connect": try connect(request)
            case "switchToUsb": try switchToUSB(request)
            case "disconnect":
                let index = try slotIndex(request)
                if let slot = slots.removeValue(forKey:index) { slot.stop() }
                result(request, command, ["slot":index])
            case "stop":
                if request["slot"] != nil {
                    let index = try slotIndex(request)
                    if let slot = slots.removeValue(forKey:index) { slot.stop() }
                    result(request,command,["slot":index])
                } else {
                    for slot in slots.values { slot.stop() }; slots.removeAll()
                    result(request,command)
                }
            case "shutdown": result(request,command); shutdown()
            case "rumble": try rumble(request)
            case "settings": try updateSettings(request)
            case "refreshBattery":
                let slot = try existingSlot(request)
                if slot.selectedKind == .bluetooth { slot.bluetooth?.refreshBatteryIfIdle() }
                else { slot.usb?.refreshBattery() }
                result(request,command,["slot":slot.index])
            case "backendStatus":
                var event = NativeOutput.status(); event["event"] = "backendStatus"; correlate(request,&event); writer.emit(event)
            case "createOutput":
                let slot = try getSlot(slotIndex(request))
                let mode = request["mode"] as? String ?? "pro2", backend = request["backend"] as? String ?? "broker"
                try slot.output.create(backend:backend,mode:mode,slot:slot.index)
                result(request,command,["slot":slot.index,"backend":backend,"mode":mode])
            case "submitOutput":
                let slot = try existingSlot(request)
                guard let state = request["state"] as? [String:Any] else { throw NativeHostFailure("invalid_state", "submitOutput 需要 state 对象。") }
                try slot.output.submit(state)
                // Per-frame output acknowledges only correlated requests to keep
                // the 250 Hz data path independent of command-response traffic.
                if request["id"] != nil || request["requestId"] != nil { result(request,command,["slot":slot.index]) }
            case "readFeedback":
                let slot = try existingSlot(request)
                var event: [String:Any] = ["event":"feedback","slot":slot.index,"events":slot.output.feedback(limit:(request["limit"] as? NSNumber)?.intValue ?? 64)]
                correlate(request,&event); writer.emit(event)
            case "destroyOutput":
                let slot = try existingSlot(request); slot.output.stop(); result(request,command,["slot":slot.index])
            case "ping": result(request,command,["protocolVersion":1])
            case "selfTest":
                let report = try NativeHostSelfTest.run(scales:settings.gyroScales)
                result(request,command,report)
            default: throw NativeHostFailure("unknown_command", "command 需要受支持的原生接口名称。")
            }
        } catch let error as NativeHostFailure {
            var event: [String:Any] = ["event":"error","code":error.code,"message":error.message]
            if let slot = request["slot"] { event["slot"] = slot }
            correlate(request,&event); writer.emit(event)
        } catch {
            var event: [String:Any] = ["event":"error","code":"invalid_json","message":"JSON 命令解析失败。"]
            correlate(request,&event); writer.emit(event)
        }
    }

    private func enumerate(_ request: [String:Any]) throws {
        let slot = try getSlot(slotIndex(request)), transport = request["transport"] as? String ?? "auto"
        guard ["auto","usb","bluetooth"].contains(transport) else { throw NativeHostFailure("invalid_transport", "transport 取值为 auto、usb 或 bluetooth。") }
        if transport != "bluetooth" { getUSB(slot).startDiscovery() }
        if transport != "usb" { getBLE(slot).startDiscovery(includeUnmatched:(request["manual"] as? Bool) ?? false) }
        result(request,"enumerate",["slot":slot.index,"transport":transport])
    }

    private func connect(_ request: [String:Any]) throws {
        let index = try slotIndex(request)
        guard let deviceID = request["deviceId"] as? String, !deviceID.isEmpty else { throw NativeHostFailure("invalid_device", "connect 需要枚举得到的 deviceId。") }
        let transport = request["transport"] as? String ?? "auto"
        var kind = Pro2TransportKind(rawValue:transport)
        if transport == "auto" {
            kind = slots.values.flatMap { $0.devices.values.flatMap { $0 } }.first(where:{ $0.id == deviceID })?.kind
                ?? (UUID(uuidString:deviceID) == nil ? .usb : .bluetooth)
        }
        guard let kind else { throw NativeHostFailure("invalid_transport", "transport 取值为 auto、usb 或 bluetooth。") }
        guard !slots.values.contains(where:{ $0.index != index && $0.selectedKind == kind && $0.selectedDeviceID == deviceID }) else {
            throw NativeHostFailure("device_in_use", "此手柄已由其他通道连接或等待重连。")
        }
        let slot = getSlot(index)
        slot.takeover?.cancel(); slot.takeover = nil
        slot.pendingUSBTimer?.cancel(); slot.pendingUSBTimer = nil; slot.pendingUSB = nil
        slot.bluetooth?.stop(); slot.usb?.stop()
        slot.selectedKind = kind; slot.selectedDeviceID = deviceID; slot.connected = false
        if kind == .bluetooth {
            let ble = getBLE(slot); settings.apply(ble); ble.connect(to:deviceID)
        } else {
            slot.pendingUSB = deviceID
            let usb = getUSB(slot)
            usb.startDiscovery()
            if slot.pendingUSB != nil, slot.devices[.usb]?.contains(where:{$0.id == deviceID}) == true { beginUSB(slot) }
            if slot.pendingUSB != nil {
                let timeout = DispatchWorkItem { [weak self,weak slot] in
                    guard let self, let slot, slot.pendingUSB == deviceID else { return }
                    slot.pendingUSB = nil
                    self.writer.emit(["event":"error","slot":slot.index,"code":"usb_device_unavailable","message":"所选 USB 手柄等待重新枚举，请检查数据线连接。"])
                }
                slot.pendingUSBTimer = timeout
                DispatchQueue.main.asyncAfter(deadline:.now()+10,execute:timeout)
            }
        }
        result(request,"connect",["slot":index,"deviceId":deviceID,"transport":kind.rawValue,"accepted":true])
    }

    private func beginUSB(_ slot: NativeSlot) {
        guard let device = slot.pendingUSB else { return }
        slot.pendingUSB = nil; slot.pendingUSBTimer?.cancel(); slot.pendingUSBTimer = nil
        slot.usb?.connect(to:device)
        if let takeover = slot.takeover, takeover.usbDeviceID == device { takeover.usbConnectReturned = true }
    }

    private func switchToUSB(_ request: [String:Any]) throws {
        let slot = getSlot(try slotIndex(request))
        guard let target = request["deviceId"] as? String, !target.isEmpty else {
            throw NativeHostFailure("invalid_device", "USB 接管需要指定枚举得到的 deviceId。")
        }
        let discovered = slots.values.contains { $0.devices[.usb]?.contains(where:{$0.id == target}) == true }
        let reserved = slots.values.contains { $0 !== slot && $0.selectedKind == .usb && $0.selectedDeviceID == target }
        if let code = USBTakeoverPolicy.preflight(source:slot.selectedKind?.rawValue,connected:slot.connected,
            targetAvailable:discovered,targetReserved:reserved,switching:slot.takeover != nil) {
            let messages = ["usb_switch_in_progress":"当前通道正在切换传输。", "usb_device_unavailable":"目标 USB 手柄需要先完成枚举。",
                "source_not_bluetooth":"USB 接管需要当前通道具有实时蓝牙输入。", "device_in_use":"目标 USB 手柄已由其他通道占用。"]
            throw NativeHostFailure(code,messages[code] ?? "USB 接管条件需要核对。")
        }
        guard let ble = slot.bluetooth, let original = slot.selectedDeviceID else {
            throw NativeHostFailure("source_not_bluetooth", "USB 接管需要当前通道具有实时蓝牙输入。")
        }
        let takeover = USBTakeover(usb:target,bluetooth:original,autoReconnect:ble.automaticallyReconnect)
        slot.takeover = takeover; ble.automaticallyReconnect = false
        slot.pendingUSB = target
        writer.emit(["event":"status","slot":slot.index,"transport":"usb","message":"正在验证 USB 输入；虚拟手柄继续保持连接。"])
        takeoverDeadline(slot,takeover,seconds:15) { [weak self,weak slot] in
            guard let self,let slot else { return }; self.restoreBluetoothAfterTakeover(slot,"USB 输入就绪等待超时。")
        }
        getUSB(slot).startDiscovery()
        if slot.pendingUSB != nil,slot.devices[.usb]?.contains(where:{$0.id == target}) == true { beginUSB(slot) }
        result(request,"switchToUsb",["slot":slot.index,"deviceId":target,"accepted":true])
    }

    private func takeoverDeadline(_ slot: NativeSlot, _ takeover: USBTakeover, seconds: TimeInterval, _ action: @escaping () -> Void) {
        takeover.timer?.cancel()
        let item = DispatchWorkItem { [weak self,weak slot,weak takeover] in
            guard let self,let slot,let takeover,self.slots[slot.index] === slot,slot.takeover === takeover else { return }
            action()
        }
        takeover.timer = item; DispatchQueue.main.asyncAfter(deadline:.now()+seconds,execute:item)
    }

    private func completeUSBTakeover(_ slot: NativeSlot) {
        guard let takeover = slot.takeover,takeover.policy.acceptUSBReady() else { return }
        takeover.timer?.cancel(); slot.takeover = nil
        slot.pendingUSB = nil; slot.pendingUSBTimer?.cancel(); slot.pendingUSBTimer = nil
        slot.selectedKind = .usb; slot.selectedDeviceID = takeover.usbDeviceID; slot.connected = true
        slot.bluetooth?.automaticallyReconnect = takeover.originalAutoReconnect
        slot.bluetooth?.stop()
        // Keep NativeOutput and its broker/native device handle untouched.
        writer.emit(["event":"transportChanged","slot":slot.index,"transport":"usb","deviceId":takeover.usbDeviceID])
        if let primary = takeover.pendingPrimary { self.primary(primary,slot:slot,kind:.usb) }
    }

    private func restoreBluetoothAfterTakeover(_ slot: NativeSlot, _ reason: String) {
        guard let takeover = slot.takeover,takeover.policy.restoreBluetooth() else { return }
        slot.pendingUSB = nil; slot.pendingUSBTimer?.cancel(); slot.pendingUSBTimer = nil
        takeover.usbConnectReturned = false
        slot.usb?.disconnect()
        slot.bluetooth?.automaticallyReconnect = takeover.originalAutoReconnect
        writer.emit(["event":"status","slot":slot.index,"transport":"bluetooth","message":"\(reason) 正在核对蓝牙实时输入。"])
        takeoverDeadline(slot,takeover,seconds:20) { [weak self,weak slot] in
            guard let self,let slot else { return }; self.finishFailedTakeover(slot,connected:false)
        }
        if !takeover.bluetoothConnected || slot.bluetooth?.diagnostics.phase != .ready { reconnectTakeoverBluetooth(slot) }
    }

    private func reconnectTakeoverBluetooth(_ slot: NativeSlot) {
        guard let takeover = slot.takeover,takeover.policy.phase == .restoringBluetooth,!takeover.bluetoothReconnectRequested else { return }
        takeover.bluetoothReconnectRequested = true
        slot.bluetooth?.connect(to:takeover.bluetoothDeviceID)
    }

    private func finishFailedTakeover(_ slot: NativeSlot, connected: Bool) {
        guard let takeover = slot.takeover else { return }
        if connected, !takeover.policy.acceptBluetoothReport() { return }
        takeover.cancel(); slot.takeover = nil
        slot.connected = connected; slot.selectedKind = .bluetooth; slot.selectedDeviceID = takeover.bluetoothDeviceID
        slot.bluetooth?.automaticallyReconnect = takeover.originalAutoReconnect
        writer.emit(["event":"transportSwitchFailed","slot":slot.index,"deviceId":takeover.usbDeviceID,"connected":connected,
            "message":connected ? "USB 接管尝试已结束，蓝牙实时输入已恢复。" : "USB 和蓝牙实时输入均等待重新连接。"])
        if !connected { slot.bluetooth?.stop() }
    }

    private func rumble(_ request: [String:Any]) throws {
        let slot = try existingSlot(request)
        guard slot.connected, let transport = slot.selectedTransport else { throw NativeHostFailure("input_inactive", "真实手柄输入就绪后可发送震动。") }
        let raw: [UInt8]
        if let values = request["raw02"] as? [NSNumber], values.allSatisfy({ $0.intValue >= 0 && $0.intValue <= 255 && $0.doubleValue.rounded() == $0.doubleValue }) {
            raw = values.map { $0.uint8Value }
        } else if let base64 = request["base64"] as? String, let value = Data(base64Encoded:base64) { raw = Array(value) }
        else { throw NativeHostFailure("invalid_rumble", "rumble 需要 raw02 字节数组或 base64。") }
        let gain = (request["gain"] as? NSNumber)?.doubleValue ?? 1
        guard gain.isFinite, (0...3).contains(gain) else { throw NativeHostFailure("invalid_rumble", "gain 取值范围为 0...3。") }
        let packet: Pro2RumblePacket
        do {
            packet = try slot.selectedKind == .usb ? Pro2RumbleEncoder.usb(raw02:raw,packetID:slot.packetID,gain:gain)
                : Pro2RumbleEncoder.ble(raw02:raw,packetID:slot.packetID,gain:gain)
        } catch { throw NativeHostFailure("invalid_rumble", "raw02 需要完整且有效的 64 字节 Pro 2 震动报告。") }
        slot.packetID = (slot.packetID + 1) & 15
        transport.writeOutput(Data(packet.bytes))
        if request["id"] != nil || request["requestId"] != nil { result(request,"rumble",["slot":slot.index,"active":packet.active]) }
    }

    private func updateSettings(_ request: [String:Any]) throws {
        let values = request["settings"] as? [String:Any] ?? request
        if let value = (values["runtimebattery"] ?? values["runtimeBatteryRefreshEnabled"]) as? Bool { settings.runtimeBattery = value }
        if let value = (values["autooptimize"] ?? values["autoOptimizeReportRate"]) as? Bool { settings.autoOptimize = value }
        if let value = (values["skip"] ?? values["skipInitialization"]) as? Bool { settings.skipInitialization = value }
        if let value = (values["autoReconnect"] ?? values["automaticallyReconnect"]) as? Bool { settings.autoReconnect = value }
        if let value = values["ps5GyroScalePitch"] as? NSNumber { settings.gyroScales.pitch = NativeGyroScales.normalized(value.doubleValue) }
        if let value = values["ps5GyroScaleYaw"] as? NSNumber { settings.gyroScales.yaw = NativeGyroScales.normalized(value.doubleValue) }
        if let value = values["ps5GyroScaleRoll"] as? NSNumber { settings.gyroScales.roll = NativeGyroScales.normalized(value.doubleValue) }
        for slot in slots.values {
            if let ble = slot.bluetooth {
                settings.apply(ble)
                if slot.takeover?.policy.phase == .preparing { ble.automaticallyReconnect = false }
            }
            slot.output.gyroScales = settings.gyroScales
        }
        result(request,"settings",["appliesTo":"nextConnection","runtimebattery":settings.runtimeBattery,"autooptimize":settings.autoOptimize,"skip":settings.skipInitialization,"autoReconnect":settings.autoReconnect,"gyroAppliesTo":"nextSubmit","gyroScales":settings.gyroScales.json])
    }

    private func getSlot(_ index: Int) -> NativeSlot {
        if let slot = slots[index] { return slot }
        let slot = NativeSlot(index); slot.output.gyroScales = settings.gyroScales; slots[index] = slot; return slot
    }
    private func existingSlot(_ request: [String:Any]) throws -> NativeSlot {
        let index = try slotIndex(request)
        guard let slot = slots[index] else { throw NativeHostFailure("slot_inactive", "通道 \(index) 需要先连接设备或创建输出。") }
        return slot
    }
    private func slotIndex(_ request: [String:Any]) throws -> Int {
        guard let value = request["slot"] else { return 0 }
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
            (0...3).contains(number.intValue), number.doubleValue.rounded() == number.doubleValue else {
            throw NativeHostFailure("invalid_slot", "slot 取值为 0、1、2 或 3。")
        }
        return number.intValue
    }
    private func getUSB(_ slot: NativeSlot) -> Pro2USBTransport {
        if let usb = slot.usb { return usb }
        let usb = Pro2USBTransport(); slot.usb = usb
        bind(usb,kind:.usb,slot:slot)
        usb.onPrimaryReport = { [weak self,weak slot] data in guard let self,let slot else { return }; self.primary(data,slot:slot,kind:.usb) }
        return usb
    }
    private func getBLE(_ slot: NativeSlot) -> Pro2BLETransport {
        if let ble = slot.bluetooth { return ble }
        let ble = Pro2BLETransport(); slot.bluetooth = ble; settings.apply(ble)
        bind(ble,kind:.bluetooth,slot:slot)
        ble.onPrimaryReport = { [weak self,weak slot] data in guard let self,let slot else { return }; self.primary(data,slot:slot,kind:.bluetooth) }
        ble.onFactoryCalibration = { [weak self,weak slot] address,data in
            guard let self,let slot else { return }
            self.writer.emit(["event":"factoryCalibration","slot":slot.index,"address":address,"base64":data.base64EncodedString()])
        }
        ble.onDiagnostics = { [weak self,weak slot] value in
            guard let self,let slot else { return }
            self.writer.emit(["event":"diagnostics","slot":slot.index,"transport":"bluetooth","diagnostics":[
                "phase":String(describing:value.phase),"phaseLabel":value.phase.rawValue,"bluetoothState":value.bluetoothState,
                "scannedAdvertisements":value.scannedAdvertisements,"candidateCount":value.candidateCount,"serviceCount":value.serviceCount,
                "characteristicSummary":value.characteristicSummary,"initializationStep":value.initializationStep,"initializationTotal":value.initializationTotal,
                "acknowledgedCommands":value.acknowledgedCommands,"ackTimeouts":value.ackTimeouts,"receivedFrames":value.receivedFrames,
                "malformedFrames":value.malformedFrames,"measuredHz":value.measuredHz,"maximumGapMilliseconds":value.maximumGapMilliseconds,
                "requestedReportRate":value.requestedReportRate,"reportRateStatus":value.reportRateStatus,"reconnectAttempt":value.reconnectAttempt,
                "lastError":value.lastError,"controllerFirmware":value.controllerFirmware,"bluetoothFirmware":value.bluetoothFirmware,
                "dspFirmware":value.dspFirmware,"runtimeBatteryStatus":value.runtimeBatteryStatus,"logPath":value.logPath]])
        }
        return ble
    }
    private func bind(_ transport: Pro2InputTransport, kind: Pro2TransportKind, slot: NativeSlot) {
        transport.onDevices = { [weak self,weak slot] devices in
            guard let self,let slot else { return }
            slot.devices[kind] = devices
            self.writer.emit(["event":"devices","slot":slot.index,"transport":kind.rawValue,"devices":devices.map { ["id":$0.id,"name":$0.name,"transport":$0.kind.rawValue] }])
            if kind == .usb, let target = slot.pendingUSB, devices.contains(where:{$0.id == target}) { self.beginUSB(slot) }
        }
        transport.onStatus = { [weak self,weak slot] status in
            guard let self,let slot else { return }; self.writer.emit(["event":"status","slot":slot.index,"transport":kind.rawValue,"message":status])
        }
        transport.onConnectionChanged = { [weak self,weak slot] connected in
            guard let self,let slot else { return }
            if let takeover = slot.takeover {
                if kind == .usb {
                    if connected { self.completeUSBTakeover(slot) }
                    else if takeover.usbConnectReturned && takeover.policy.phase == .preparing {
                        self.restoreBluetoothAfterTakeover(slot,"USB 初始化或连接已中断。")
                    }
                } else {
                    takeover.bluetoothConnected = connected
                    if !connected && takeover.policy.phase == .restoringBluetooth { self.reconnectTakeoverBluetooth(slot) }
                }
                return
            }
            guard slot.selectedKind == kind else { return }
            slot.connected = connected
            self.writer.emit(["event":"connection","slot":slot.index,"transport":kind.rawValue,"connected":connected,"deviceId":slot.selectedDeviceID ?? ""])
        }
        transport.onReport = { [weak self,weak slot] data in
            guard let self,let slot,slot.selectedKind == kind else { return }
            if kind == .bluetooth,slot.takeover?.policy.phase == .restoringBluetooth {
                self.finishFailedTakeover(slot,connected:true)
            }
            self.writer.emit(["event":"rawInput","slot":slot.index,"format":kind == .usb ? "usb" : "fd2", "base64":data.base64EncodedString(),"hostUptimeSeconds":ProcessInfo.processInfo.systemUptime],rawInput:true)
        }
    }
    private func primary(_ data: Data, slot: NativeSlot, kind: Pro2TransportKind) {
        if kind == .usb,let takeover = slot.takeover,takeover.policy.phase == .preparing {
            takeover.pendingPrimary = data; return
        }
        writer.emit(["event":"primary","slot":slot.index,"transport":kind.rawValue,"format":"primary","base64":data.base64EncodedString()])
    }
    private func correlate(_ request: [String:Any], _ event: inout [String:Any]) {
        if let id = request["id"] { event["id"] = id }
        if let id = request["requestId"] { event["requestId"] = id }
    }
    private func result(_ request: [String:Any], _ command: String, _ values: [String:Any] = [:]) {
        var event = values; event["event"] = "commandResult"; event["command"] = command; event["ok"] = true
        correlate(request,&event); writer.emit(event)
    }
    private func shutdown(code: Int32 = 0) {
        guard !shuttingDown else { return }; shuttingDown = true
        for slot in slots.values { slot.stop() }; slots.removeAll()
        writer.emit(["event":"stopped","reason":"hostShutdown"])
        writer.finish(code)
    }
}

private enum NativeHostSelfTest {
    static func run(scales: NativeGyroScales) throws -> [String:Any] {
        func check(_ value: Bool, _ label: String) throws {
            if !value { throw NativeHostFailure("self_test_failed",label) }
        }
        for bit in 0..<21 {
            let physical = NativeStateAdapter.physicalButtons(UInt64(1) << bit)
            try check(physical == UInt32(1) << NativeStateAdapter.physicalBits[bit],"logical button \(bit)")
        }
        let state: [String:Any] = ["buttons":0x100001,"lx":0,"ly":4095,"rx":2048,"ry":2048,"l2":4095,"r2":0,
            "accelValid":true,"gyroValid":true,"accelX":4096,"accelY":-4096,"accelZ":2048,"gyroX":16384,"gyroY":-16384,"gyroZ":8192,"batteryLevel":9,"batteryCharging":true]
        let input = try NativeStateAdapter.brokerInput(state)
        try check(input.lx == -1 && input.ly == 1 && input.rx == 0,"axis range")
        try check(input.left_trigger == 1 && input.right_trigger == 0,"trigger range")
        try check(abs(input.gyro_x - 1000) < 0.01 && abs(input.gyro_y - 500) < 0.01 && abs(input.gyro_z + 1000) < 0.01,"motion adapter")
        try check(input.battery_percent == 100 && input.charging == 1,"battery native scale")
        let scaled = try NativeStateAdapter.brokerInput(state,scales:.init(pitch:2,yaw:0.5,roll:3))
        try check(abs(scaled.gyro_x - 2000) < 0.01 && abs(scaled.gyro_y - 250) < 0.01 && abs(scaled.gyro_z + 3000) < 0.01,"target gyro scales applied once after coordinate and unit conversion")
        try check(NativeGyroScales.normalized(-10) == 0.1 && NativeGyroScales.normalized(8) == 4 && NativeGyroScales.normalized(.nan) == 1,"gyro scale normalization")
        let ds5State: [String:Any] = ["gyroX":100,"gyroY":200,"gyroZ":300,"accelX":1000,"accelY":2000,"accelZ":3000,"gyroValid":true,"accelValid":true]
        let ds5 = try NativeStateAdapter.brokerInput(ds5State,scales:.init(pitch:2,yaw:3,roll:4),mode:"dualsense")
        try check([ds5.gyro_x,ds5.gyro_y,ds5.gyro_z].map { Int(($0 * NativeStateAdapter.dualSenseGyroPackingScale).rounded()) } == [200,900,-800],"DS5 final gyro HID counts preserve shared Windows axes/scales")
        try check([ds5.accel_x,ds5.accel_y,ds5.accel_z].map { Int(($0 * NativeStateAdapter.dualSenseAccelPackingScale).rounded()) } == [2000,6000,-4000],"DS5 final accel HID counts preserve shared Windows sensitivity")
        let pro1 = try NativeStateAdapter.brokerInput(ds5State,mode:"pro1")
        try check([-pro1.gyro_z,-pro1.gyro_x,pro1.gyro_y].map { Int(($0 * NativeStateAdapter.switchGyroPackingScale).rounded()) } == [100,200,300],"Pro1 final gyro counts preserve the shared native axes and sensitivity")
        var invalid = state; invalid["lx"] = 4096
        var rejected = false
        do { _ = try NativeStateAdapter.brokerInput(invalid) } catch { rejected = true }
        try check(rejected,"invalid axis rejection")
        try check(USBTakeoverPolicy.preflight(source:"bluetooth",connected:true,targetAvailable:true,targetReserved:false,switching:false) == nil,"USB switch requires explicit discovered target and live BLE")
        try check(USBTakeoverPolicy.preflight(source:"bluetooth",connected:true,targetAvailable:false,targetReserved:false,switching:false) == "usb_device_unavailable","USB target discovery is a precondition")
        try check(USBTakeoverPolicy.preflight(source:"usb",connected:true,targetAvailable:true,targetReserved:false,switching:false) == "source_not_bluetooth","USB switch is specific to BLE source")
        try check(USBTakeoverPolicy.preflight(source:"bluetooth",connected:true,targetAvailable:true,targetReserved:true,switching:false) == "device_in_use","USB switch respects other slot ownership")
        var committed = USBTakeoverPolicy()
        try check(committed.acceptUSBReady() && !committed.acceptUSBReady(),"USB first frame commits exactly once")
        var cancelled = USBTakeoverPolicy();cancelled.cancel()
        try check(!cancelled.acceptUSBReady() && !cancelled.acceptBluetoothReport(),"stop cancels late takeover and recovery callbacks")
        var restored = USBTakeoverPolicy()
        try check(restored.restoreBluetooth() && !restored.acceptUSBReady() && restored.acceptBluetoothReport(),"failed USB handoff resumes only after fresh BLE report")
        return ["passed":true,"groups":9,"logicalButtonsTested":21,"gyroScales":scales.json,"usbTakeoverPolicy":true,
            "dualsenseFixture":["gyroX":ds5.gyro_x,"gyroY":ds5.gyro_y,"gyroZ":ds5.gyro_z,"accelX":ds5.accel_x,"accelY":ds5.accel_y,"accelZ":ds5.accel_z],
            "pro1Fixture":["gyroX":pro1.gyro_x,"gyroY":pro1.gyro_y,"gyroZ":pro1.gyro_z,"accelX":pro1.accel_x,"accelY":pro1.accel_y,"accelZ":pro1.accel_z]]
    }
}

private let host = NativeHost()
host.run()
