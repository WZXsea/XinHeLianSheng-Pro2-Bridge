import SwiftUI
import AppKit
import Pro2Core
import Pro2Transport
import Pro2Output

enum OutputMode: String, CaseIterable { case monitor = "输入监测", keyboard = "键盘映射", gamepad = "虚拟手柄（需签名权限）" }

final class BridgeModel: ObservableObject {
    @Published var devices: [Pro2TransportDevice] = []
    @Published var selectedID = ""
    @Published var logs: [String] = []
    @Published var state = Pro2State()
    @Published var status = "点击搜索，或接入 USB 数据线"
    @Published var outputStatus = "输入监测"
    @Published var rate = 0
    @Published var packets = 0
    @Published var mode: OutputMode = .monitor
    @Published var swapABXY = UserDefaults.standard.bool(forKey:"swapABXY")
    @Published var mapping: [Int] = []
    @Published var leftStickKeys = true
    @Published var rumbleGain = 0.5
    private let ble = Pro2BLETransport()
    private let usb = Pro2USBTransport()
    private let parser = Pro2ReportParser()
    private let keyboard = Pro2KeyboardOutput()
    private let gamepad = Pro2VirtualGamepad()
    private var lists: [Pro2TransportKind:[Pro2TransportDevice]] = [:]
    private var active: Pro2TransportKind?
    private var lastInput: TimeInterval = 0
    private var epoch = ProcessInfo.processInfo.systemUptime
    private var windowPackets = 0
    private var timer: Timer?
    private var rumbleTimer: Timer?
    private var rumblePacket: UInt8 = 0
    private var lastPresentation: TimeInterval = 0
    private var parseErrors = 0
    private var totalPackets = 0

    init() {
        mapping = keyboard.mappings.map { $0.map(Int.init) ?? -1 }
        if let saved = UserDefaults.standard.array(forKey:"keyMappings") as? [Int], saved.count == mapping.count { mapping = saved }
        for (transport,kind) in [(ble as Pro2InputTransport,Pro2TransportKind.bluetooth),(usb as Pro2InputTransport,Pro2TransportKind.usb)] {
            transport.onDevices = { [weak self] devices in
                guard let self else { return }; self.lists[kind] = devices
                self.devices = (self.lists[.usb] ?? []) + (self.lists[.bluetooth] ?? [])
                if !self.devices.contains(where:{$0.id == self.selectedID}) { self.selectedID = self.devices.first?.id ?? "" }
            }
            transport.onStatus = { [weak self] in self?.log($0) }
            transport.onReport = { [weak self] in self?.receive($0,kind:kind) }
            transport.onConnectionChanged = { [weak self] connected in
                guard let self, self.active == kind, !connected else { return }
                self.stopRumble(); self.keyboard.releaseAll(); _ = self.gamepad.send(Pro2State()); self.state = Pro2State(); self.lastInput = 0
            }
        }
        saveMapping()
        timer = Timer.scheduledTimer(withTimeInterval:0.2,repeats:true) { [weak self] _ in self?.tick() }
        NotificationCenter.default.addObserver(forName:NSApplication.willTerminateNotification,object:nil,queue:.main) { [weak self] _ in self?.stop() }
        NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.willSleepNotification,object:nil,queue:.main) { [weak self] _ in self?.stop() }
    }
    func log(_ text: String) {
        status = text; logs.append(text); if logs.count > 120 { logs.removeFirst(logs.count-120) }
    }
    func search() { ble.startDiscovery(); usb.startDiscovery(); log("搜索 USB 与蓝牙设备；蓝牙请长按手柄顶部配对键") }
    func connect() {
        guard let device = devices.first(where:{$0.id == selectedID}) else { log("请选择已发现的手柄"); return }
        stopRumble(); active = nil; keyboard.releaseAll(); gamepad.stop(); mode = .monitor
        ble.disconnect(); usb.disconnect(); state = Pro2State(); lastInput = 0
        active = device.kind; epoch = ProcessInfo.processInfo.systemUptime; windowPackets = 0; packets = 0; totalPackets = 0; parseErrors = 0
        if device.kind == .usb { usb.connect(to:device.id) } else { ble.connect(to:device.id) }
    }
    func stop() {
        stopRumble(); active = nil; keyboard.releaseAll(); gamepad.stop(); ble.stop(); usb.stop(); state = Pro2State(); rate = 0
        mode = .monitor; outputStatus = "输入监测"; lastInput = 0
    }
    func applyOutput() {
        keyboard.releaseAll(); gamepad.stop()
        switch mode {
        case .monitor: outputStatus = "输入监测"
        case .keyboard:
            if !keyboard.hasPermission { keyboard.requestPermission(); outputStatus = "请在系统设置 → 隐私与安全性 → 辅助功能中允许 PRO2 手柄助手" }
            else { outputStatus = "键盘映射已启用；按键发送至当前前台应用" }
        case .gamepad:
            if gamepad.start() { outputStatus = "虚拟 HID 手柄已创建；游戏兼容性待当前游戏验证" }
            else { mode = .monitor; outputStatus = "系统要求 Apple 虚拟 HID 签名授权；当前可使用键盘映射和输入监测" }
        }
    }
    func saveMapping() {
        let allowed = Set(Pro2Controls.keyChoices.map { Int($0.1) })
        mapping = mapping.map { allowed.contains($0) ? $0 : -1 }
        keyboard.releaseAll(); keyboard.mappings = mapping.map { $0 >= 0 ? UInt16($0) : nil }
        keyboard.useLeftStick = leftStickKeys
        UserDefaults.standard.set(mapping,forKey:"keyMappings")
        UserDefaults.standard.set(swapABXY,forKey:"swapABXY")
    }
    func testRumble() {
        guard active != nil, lastInput > 0 else { log("请先连接并取得实时手柄输入"); return }
        stopRumble()
        let end = ProcessInfo.processInfo.systemUptime + 0.25
        sendRumble(Pro2RumbleEncoder.previewRaw02(),gain:rumbleGain)
        rumbleTimer = Timer.scheduledTimer(withTimeInterval:0.02,repeats:true) { [weak self] _ in
            guard let self else { return }
            if ProcessInfo.processInfo.systemUptime >= end { self.stopRumble() }
            else { self.sendRumble(Pro2RumbleEncoder.previewRaw02(),gain:self.rumbleGain) }
        }
    }
    private func stopRumble() {
        rumbleTimer?.invalidate(); rumbleTimer = nil
        if active != nil { sendRumble(Pro2RumbleEncoder.neutralRaw02(),gain:0) }
    }
    private func sendRumble(_ raw: [UInt8],gain:Double) {
        guard let active else { return }
        do {
            let packet = try active == .usb ? Pro2RumbleEncoder.usb(raw02:raw,packetID:rumblePacket,gain:gain) : Pro2RumbleEncoder.ble(raw02:raw,packetID:rumblePacket,gain:gain)
            rumblePacket &+= 1
            if active == .usb { usb.writeOutput(Data(packet.bytes)) } else { ble.writeOutput(Data(packet.bytes)) }
        } catch { log("试振封包检查：\(error)") }
    }
    private func receive(_ data: Data, kind: Pro2TransportKind) {
        guard active == kind else { return }
        do {
            let parsed = try parser.parse(data,format:kind == .usb ? .usb : .fd2)
            lastInput = ProcessInfo.processInfo.systemUptime; totalPackets += 1; windowPackets += 1
            if lastInput-lastPresentation >= 1.0/60 { state = parsed; packets = totalPackets; lastPresentation = lastInput }
            switch mode {
            case .keyboard: keyboard.send(parsed)
            case .gamepad:
                let result = gamepad.send(parsed,swapABXY:swapABXY)
                if result != 0 { log("虚拟手柄输入提交失败：\(result)"); mode = .monitor; applyOutput() }
            case .monitor: break
            }
        } catch { parseErrors += 1; if parseErrors == 1 || parseErrors % 250 == 0 { log("输入格式检查：\(error)，累计 \(parseErrors) 帧") } }
    }
    private func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        if now-epoch >= 1 { rate = Int(Double(windowPackets)/(now-epoch)); windowPackets = 0; epoch = now }
        if lastInput > 0 && now-lastInput > 0.5 {
            keyboard.releaseAll(); _ = gamepad.send(Pro2State()); state = Pro2State(); lastInput = 0
        }
    }
}

struct MainView: View {
    @ObservedObject var model: BridgeModel
    var body: some View {
        VStack(alignment:.leading,spacing:18) {
            HStack {
                Image(systemName:"gamecontroller.fill").font(.system(size:36)).foregroundStyle(.cyan)
                VStack(alignment:.leading) { Text("PRO2 手柄助手").font(.largeTitle); Text("macOS · USB / Bluetooth LE").foregroundStyle(.secondary) }
                Spacer(); Text("\(model.rate) Hz").font(.title.monospacedDigit()).foregroundStyle(.cyan)
            }
            HStack {
                Picker("手柄",selection:$model.selectedID) {
                    Text("选择设备").tag("")
                    ForEach(model.devices) { d in Text("\(d.kind == .usb ? "USB" : "蓝牙") · \(d.name)").tag(d.id) }
                }
                Button("搜索",action:model.search)
                Button("连接",action:model.connect).disabled(model.selectedID.isEmpty)
                Button("断开",action:model.stop)
            }
            Text(model.status).font(.callout).foregroundStyle(.secondary).lineLimit(2)
            TabView {
                monitor.tabItem { Label("手柄测试",systemImage:"gamecontroller") }
                mapping.tabItem { Label("输出与映射",systemImage:"keyboard") }
                ScrollView { Text(model.logs.joined(separator:"\n")).font(.system(.caption,design:.monospaced)).frame(maxWidth:.infinity,alignment:.leading).textSelection(.enabled).padding() }
                    .tabItem { Label("连接日志",systemImage:"list.bullet.rectangle") }
            }
            Text("macOS 移植预览版 · 实际连接速率和游戏兼容性以当前硬件验证为准").font(.caption).foregroundStyle(.secondary)
        }.padding(24).frame(minWidth:790,minHeight:640).preferredColorScheme(.dark)
    }
    var monitor: some View {
        VStack(alignment:.leading,spacing:18) {
            HStack {
                stick("左摇杆",x:model.state.axes.lx,y:model.state.axes.ly)
                stick("右摇杆",x:model.state.axes.rx,y:model.state.axes.ry)
                VStack(alignment:.leading,spacing:8) {
                    Text("有效报文：\(model.packets)")
                    Text(model.state.battery.level.map { "电量档位：\($0) / 9" } ?? "电量档位：等待原生电量报文")
                    if let mv = model.state.battery.voltageMillivolts { Text("电压：\(mv) mV") }
                    if let c = model.state.battery.charging { Text(c ? "充电中" : "充电状态：空闲") }
                    Text("IMU：\(model.state.motionSamples.count) 个原始采样")
                }.font(.callout)
            }
            LazyVGrid(columns:Array(repeating:GridItem(.flexible()),count:7),spacing:10) {
                ForEach(0..<Pro2Controls.buttons.count,id:\.self) { i in
                    Text(Pro2Controls.buttons[i].0).frame(maxWidth:.infinity).padding(.vertical,12)
                        .background(model.state.buttons.contains(Pro2Controls.buttons[i].1) ? Color.cyan.opacity(0.6) : Color.white.opacity(0.08),in:RoundedRectangle(cornerRadius:8))
                }
            }
            HStack { Text("试振强度"); Slider(value:$model.rumbleGain,in:0...1); Text("\(Int(model.rumbleGain*100))%").monospacedDigit(); Button("试振 250 ms",action:model.testRumble) }
            Text("100% 表示软件 1 倍增益。试振功能等待当前硬件效果确认。").font(.caption).foregroundStyle(.secondary)
            Spacer()
        }.padding()
    }
    func stick(_ title: String,x:UInt16,y:UInt16) -> some View {
        VStack {
            ZStack {
                Circle().fill(.white.opacity(0.07)); Path { p in p.move(to:CGPoint(x:65,y:0)); p.addLine(to:CGPoint(x:65,y:130)); p.move(to:CGPoint(x:0,y:65)); p.addLine(to:CGPoint(x:130,y:65)) }.stroke(.white.opacity(0.2))
                Circle().fill(.cyan).frame(width:18,height:18).offset(x:(Double(x)-2048)/2048*54,y: -(Double(y)-2048)/2048*54)
            }.frame(width:130,height:130)
            Text(title); Text("\(x), \(y)").font(.caption.monospacedDigit())
        }.frame(width:170)
    }
    var mapping: some View {
        ScrollView {
            VStack(alignment:.leading,spacing:16) {
                Picker("输出方式",selection:$model.mode) { ForEach(OutputMode.allCases,id:\.self) { Text($0.rawValue).tag($0) } }.onChange(of:model.mode) { _ in model.applyOutput() }
                Text(model.outputStatus).font(.callout).foregroundStyle(.cyan)
                Toggle("左摇杆映射为 WASD",isOn:$model.leftStickKeys).onChange(of:model.leftStickKeys) { _ in model.saveMapping() }
                Toggle("虚拟手柄交换 A/B 与 X/Y",isOn:$model.swapABXY).onChange(of:model.swapABXY) { _ in model.saveMapping() }
                Text("键盘模式适用于支持键盘操作的游戏。选择输入监测可暂停按键发送。系统虚拟手柄需要带 Apple 对应授权的签名构建。").font(.caption).foregroundStyle(.secondary)
                LazyVGrid(columns:[GridItem(.flexible()),GridItem(.flexible())],spacing:10) {
                    ForEach(0..<Pro2Controls.buttons.count,id:\.self) { i in
                        Picker(Pro2Controls.buttons[i].0,selection:$model.mapping[i]) {
                            Text("留空").tag(-1)
                            ForEach(Pro2Controls.keyChoices,id:\.1) { key in Text(key.0).tag(Int(key.1)) }
                        }.onChange(of:model.mapping[i]) { _ in model.saveMapping() }
                    }
                }
            }.padding()
        }.onAppear { model.saveMapping() }
    }
}

@main struct Pro2MacApp: App {
    @StateObject private var model = BridgeModel()
    var body: some Scene {
        WindowGroup("PRO2 手柄助手") { MainView(model:model).onDisappear { model.stop() } }
        .commands { CommandGroup(replacing:.newItem) {}; CommandMenu("连接") {
            Button("搜索手柄",action:model.search).keyboardShortcut("r")
            Button("断开并释放按键",action:model.stop).keyboardShortcut(".")
        } }
    }
}
