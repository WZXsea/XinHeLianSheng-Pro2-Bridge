import Foundation
import Pro2Core
import Pro2Transport
import Pro2Output
import GameController

let args = CommandLine.arguments
if args.contains("--probe-ble") {
    setbuf(stdout,nil)
    let ble = Pro2BLETransport()
    var attempt = false
    var frames = 0
    var phase: Pro2BLEPhase?
    ble.onStatus = { print("BLE \($0)") }
    ble.onDevices = { devices in
        print("ble_candidates=\(devices.count)")
        if !attempt, let candidate = devices.first { attempt = true; DispatchQueue.main.async { ble.connect(to:candidate.id) } }
    }
    ble.onConnectionChanged = { print("ble_connected=\($0)") }
    ble.onDiagnostics = { value in
        if phase != value.phase { phase = value.phase; print("phase=\(value.phase.rawValue) error=\(value.lastError)") }
        if value.phase == .ready { print("BLE input_frames=\(value.receivedFrames) measured_hz=\(value.measuredHz)") }
    }
    ble.onReport = { data in
        if let state = try? Pro2ReportParser().parse(data,format:.fd2) {
            frames += 1
            if frames == 1 { print("first_fd2_bytes=\(data.count) buttons=\(state.buttons.rawValue) axes=\(state.rawAxes)") }
        }
    }
    ble.startDiscovery()
    var stopFile: String?
    if let i = args.firstIndex(of:"--until-file"), args.count > i+1 { stopFile = args[i+1] }
    print("BLE continuous_probe_started=true")
    let limit = Date(timeIntervalSinceNow:3600)
    while Date() < limit && !(stopFile.map { FileManager.default.fileExists(atPath:$0) } ?? false) {
        RunLoop.main.run(until:Date(timeIntervalSinceNow:1))
    }
    ble.stop(); print("BLE SUMMARY valid_frames=\(frames)"); exit(frames > 0 ? 0 : 3)
}
if args.contains("--native-controllers") {
    RunLoop.main.run(until:Date(timeIntervalSinceNow:2))
    for c in GCController.controllers() { print("native_controller=\(c.vendorName ?? "unknown") category=\(c.productCategory) extended=\(c.extendedGamepad != nil)") }
    print("native_controller_count=\(GCController.controllers().count)"); exit(0)
}
if args.contains("--virtual-hid") {
    let gamepad = Pro2VirtualGamepad()
    let created = gamepad.start()
    print("virtual_hid_created=\(created)")
    if created { print("neutral_report_result=\(gamepad.send(Pro2State()))") }
    else { print("required_entitlement=com.apple.developer.hid.virtual.device") }
    gamepad.stop()
    exit(created ? 0 : 2)
}
if args.contains("--decode"), let index = args.firstIndex(of:"--decode"), args.count > index+2 {
    guard let format = Pro2InputFormat(rawValue:args[index+1]) else { fputs("Unknown format\n",stderr); exit(1) }
    let hex = args[index+2].filter { !$0.isWhitespace }
    guard hex.count % 2 == 0 else { fputs("Hex byte pairs required\n",stderr); exit(1) }
    var bytes: [UInt8] = []; var cursor = hex.startIndex
    while cursor < hex.endIndex {
        let end = hex.index(cursor,offsetBy:2)
        guard let value = UInt8(hex[cursor..<end],radix:16) else { fputs("Invalid hex\n",stderr); exit(1) }
        bytes.append(value); cursor = end
    }
    do {
        let state = try Pro2ReportParser().parse(bytes,format:format)
        print("buttons=\(String(state.buttons.rawValue,radix:16)) axes=\(state.axes) battery=\(state.battery)")
    } catch { fputs("Decode error: \(error)\n",stderr); exit(1) }
    exit(0)
}
print("Usage: pro2-diagnostics --virtual-hid | --decode fd2|usb|primary|standard|legacy HEX | --scan-usb | --probe-usb")
if args.contains("--scan-usb") || args.contains("--probe-usb") {
    let usb = Pro2USBTransport()
    var connected = false
    var count = 0
    var attempted = false
    var found = 0
    var firstInput: TimeInterval?
    var lastInput: TimeInterval = 0
    var lastButtons: UInt32?
    var allButtons: UInt32 = 0
    var minima = [UInt16](repeating:4095,count:4)
    var maxima = [UInt16](repeating:0,count:4)
    usb.onStatus = { print($0) }
    usb.onConnectionChanged = { connected = $0; print("connected=\($0)") }
    usb.onReport = { data in
        do {
            let state = try Pro2ReportParser().parse(data,format:.usb)
            count += 1; let now = ProcessInfo.processInfo.systemUptime
            let axes = [state.rawAxes.lx,state.rawAxes.ly,state.rawAxes.rx,state.rawAxes.ry]
            for i in 0..<4 { minima[i] = min(minima[i],axes[i]); maxima[i] = max(maxima[i],axes[i]) }
            allButtons |= state.buttons.rawValue
            if firstInput == nil { firstInput = now }; lastInput = now
            if count == 1 || lastButtons != state.buttons.rawValue {
                print("report=\(count) bytes=\(data.count) buttons=\(String(state.buttons.rawValue,radix:16)) axes=\(state.rawAxes) battery=\(state.battery)")
            }
            lastButtons = state.buttons.rawValue
        } catch { print("parse_error=\(error)") }
    }
    usb.onDevices = { devices in
        found = devices.count
        print("usb_candidates=\(devices.count)"); for d in devices { print("\(d.id) \(d.name)") }
        if args.contains("--probe-usb"), !attempted, let device = devices.first {
            attempted = true; DispatchQueue.main.async { usb.connect(to:device.id) }
        }
    }
    usb.startDiscovery()
    var durationSeconds: TimeInterval = args.contains("--probe-usb") ? 15 : 3
    if let i = args.firstIndex(of:"--duration"), args.count > i+1, let value = Double(args[i+1]) { durationSeconds = max(1,min(120,value)) }
    RunLoop.main.run(until:Date(timeIntervalSinceNow:durationSeconds))
    let duration = firstInput.map { lastInput-$0 } ?? 0
    print("SUMMARY candidates=\(found) connected=\(connected) valid_reports=\(count) measured_hz=\(duration > 0 ? Double(max(0,count-1))/duration : 0)")
    print("button_union=\(String(allButtons,radix:16)) axis_min=\(minima) axis_max=\(maxima)")
    usb.stop()
    if args.contains("--probe-usb") { exit(count > 0 ? 0 : 3) }
}
