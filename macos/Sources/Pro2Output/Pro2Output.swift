import Foundation
import ApplicationServices
import Pro2Core
import CPro2Virtual

public enum Pro2Controls {
    // Stable generic HID button order; physical positions follow Xbox conventions.
    public static let buttons: [(String, Pro2Buttons)] = [
        ("B / 南", .south), ("A / 东", .east), ("Y / 西", .west), ("X / 北", .north),
        ("L", .l1), ("R", .r1), ("ZL", .l2), ("ZR", .r2),
        ("−", .back), ("+", .start), ("L3", .leftStick), ("R3", .rightStick),
        ("↓", .dpadDown), ("→", .dpadRight), ("←", .dpadLeft), ("↑", .dpadUp),
        ("Home", .home), ("Capture", .capture), ("GR", .paddleRight), ("GL", .paddleLeft), ("C", .aux)
    ]
    public static let keyChoices: [(String, CGKeyCode)] = [
        ("空格",49),("回车",36),("Esc",53),("Tab",48),
        ("W",13),("A",0),("S",1),("D",2),("Q",12),("E",14),("R",15),("F",3),
        ("Z",6),("X",7),("C",8),("V",9),("1",18),("2",19),("3",20),("4",21),
        ("↑",126),("↓",125),("←",123),("→",124)
    ]
}

public enum Pro2GamepadReport {
    public static func encode(_ state: Pro2State, swapABXY: Bool = false) -> [UInt8] {
        var buttons = state.buttons
        if swapABXY {
            let pairs: [(Pro2Buttons,Pro2Buttons)] = [(.south,.east),(.west,.north)]
            for (a,b) in pairs {
                let hasA = buttons.contains(a), hasB = buttons.contains(b)
                buttons.remove([a,b]); if hasA { buttons.insert(b) }; if hasB { buttons.insert(a) }
            }
        }
        var bits: UInt32 = 0
        for (index, control) in Pro2Controls.buttons.enumerated() where buttons.contains(control.1) { bits |= 1 << index }
        var report = [UInt8(truncatingIfNeeded: bits),UInt8(truncatingIfNeeded: bits >> 8),UInt8(truncatingIfNeeded: bits >> 16)]
        for (raw,invert) in [(state.axes.lx,false),(state.axes.ly,true),(state.axes.rx,false),(state.axes.ry,true)] {
            let centered = (Double(raw)-2048) / (raw < 2048 ? 2048 : 2047)
            let value = Int16((min(1,max(-1,centered)) * (invert ? -32767 : 32767)).rounded())
            report += [UInt8(truncatingIfNeeded: value),UInt8(truncatingIfNeeded: Int32(value) >> 8)]
        }
        return report
    }
}

public final class Pro2VirtualGamepad {
    private var handle: OpaquePointer?
    public init() {}
    public func start() -> Bool {
        stop(); handle = pro2_virtual_create(); return handle != nil
    }
    @discardableResult public func send(_ state: Pro2State, swapABXY: Bool = false) -> Int32 {
        guard let handle else { return -1 }
        let bytes = Pro2GamepadReport.encode(state,swapABXY:swapABXY)
        return bytes.withUnsafeBufferPointer { pro2_virtual_send(handle,$0.baseAddress,$0.count) }
    }
    public func stop() {
        if let handle { send(Pro2State()); pro2_virtual_destroy(handle) }; handle = nil
    }
    deinit { stop() }
}

public final class Pro2KeyboardOutput {
    private var held: Set<CGKeyCode> = []
    public var mappings: [CGKeyCode?] = [49,36,12,14,6,7,8,9,53,48,nil,nil,125,124,123,126,nil,nil,15,3,nil]
    public var useLeftStick = true
    public init() {}
    public var hasPermission: Bool { AXIsProcessTrusted() }
    public func requestPermission() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }
    public func send(_ state: Pro2State) {
        guard hasPermission else { releaseAll(); return }
        var desired = Set<CGKeyCode>()
        for (i,button) in Pro2Controls.buttons.enumerated() where state.buttons.contains(button.1) {
            if mappings.indices.contains(i), let key = mappings[i] { desired.insert(key) }
        }
        if useLeftStick {
            if state.axes.lx < 1200 { desired.insert(0) }; if state.axes.lx > 2900 { desired.insert(2) }
            if state.axes.ly < 1200 { desired.insert(1) }; if state.axes.ly > 2900 { desired.insert(13) }
        }
        for key in held.subtracting(desired) { post(key,false) }
        for key in desired.subtracting(held) { post(key,true) }
        held = desired
    }
    public func releaseAll() { for key in held { post(key,false) }; held.removeAll() }
    private func post(_ key: CGKeyCode,_ down: Bool) {
        CGEvent(keyboardEventSource:nil,virtualKey:key,keyDown:down)?.post(tap:.cghidEventTap)
    }
    deinit { releaseAll() }
}
