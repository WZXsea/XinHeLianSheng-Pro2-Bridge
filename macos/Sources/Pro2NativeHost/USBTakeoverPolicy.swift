import Foundation

/// The same state gates are exercised by pure tests and by native callbacks.
struct USBTakeoverPolicy {
    enum Phase { case preparing, restoringBluetooth, finished, cancelled }
    private(set) var phase: Phase = .preparing
    static func preflight(source: String?, connected: Bool, targetAvailable: Bool,
        targetReserved: Bool, switching: Bool) -> String? {
        if switching { return "usb_switch_in_progress" }
        if source == "usb" { return "source_not_bluetooth" }
        if !targetAvailable { return "usb_device_unavailable" }
        if source != "bluetooth" || !connected { return "source_not_bluetooth" }
        if targetReserved { return "device_in_use" }
        return nil
    }
    mutating func acceptUSBReady() -> Bool {
        guard phase == .preparing else { return false }; phase = .finished; return true
    }
    mutating func restoreBluetooth() -> Bool {
        guard phase == .preparing else { return false }; phase = .restoringBluetooth; return true
    }
    mutating func acceptBluetoothReport() -> Bool {
        guard phase == .restoringBluetooth else { return false }; phase = .finished; return true
    }
    mutating func cancel() { phase = .cancelled }
}
