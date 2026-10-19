import Foundation

public enum Pro2BLEPhase: String, Sendable {
    case idle = "蓝牙待机", waitingForBluetooth = "等待蓝牙", scanning = "搜索手柄"
    case connecting = "连接手柄", discovering = "发现服务", subscribingACK = "订阅命令回复"
    case initializing = "初始化手柄", readingCalibration = "读取原厂校准", readingBattery = "读取连接电量"
    case configuringRate = "设置输入报告率", subscribingInput = "订阅 FD2 输入", waitingForInput = "等待真实输入"
    case ready = "蓝牙输入就绪", reconnecting = "自动重新连接", failed = "连接需要处理"
}

public struct Pro2BLEDiagnostics: Sendable, Equatable {
    public var phase: Pro2BLEPhase = .idle
    public var bluetoothState = "初始化"
    public var scannedAdvertisements = 0
    public var candidateCount = 0
    public var serviceCount = 0
    public var characteristicSummary = "等待 GATT 服务"
    public var initializationStep = 0
    public var initializationTotal = BLEStartup.commandCount
    public var acknowledgedCommands = 0
    public var ackTimeouts = 0
    public var receivedFrames = 0
    public var malformedFrames = 0
    public var measuredHz = 0.0
    public var maximumGapMilliseconds = 0.0
    public var requestedReportRate = 133
    public var reportRateStatus = "等待连接"
    public var controllerFirmware = "等待手柄返回"
    public var bluetoothFirmware = "等待手柄返回"
    public var dspFirmware = "等待手柄返回"
    public var runtimeBatteryStatus = "等待连接"
    public var reconnectAttempt = 0
    public var lastError = ""
    public var logPath = BLEEventLog.defaultURL.path
    public init() {}
}

public struct Pro2BLEFirmware: Sendable, Equatable {
    public let controller: String
    public let bluetooth: String?
    public let dsp: String?
    public static func decode(_ reply: Data) -> Pro2BLEFirmware? {
        let bytes = Array(reply)
        guard bytes.count >= 16, bytes[0] == 0x10, bytes[1] == 1, bytes[2] == 1,
              bytes[3] == 1, bytes[5] == 0x78,
              bytes[8..<11].contains(where: { $0 != 0xff }) else { return nil }
        func version(_ offset: Int) -> String? {
            guard bytes.count >= offset + 3, bytes[offset..<(offset + 3)].contains(where: { $0 != 0xff }) else { return nil }
            return "\(bytes[offset]).\(bytes[offset+1]).\(bytes[offset+2])"
        }
        return Pro2BLEFirmware(controller: version(8)!, bluetooth: version(12), dsp: version(16))
    }
}

/// Bounded control events and aggregate counters. UUIDs, radio addresses, input
/// payloads and unrelated nearby devices stay out of the diagnostic file.
final class BLEEventLog {
    static let defaultURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/Pro2Bridge/bluetooth.log")
    private let queue = DispatchQueue(label: "io.github.wzxsea.pro2bridge.ble-log", qos: .utility)
    func event(_ name: String, _ details: String = "") {
        queue.async {
            let url = Self.defaultURL, fm = FileManager.default
            do {
                try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true,
                                       attributes: [.posixPermissions: 0o700])
                if let size = try? fm.attributesOfItem(atPath: url.path)[.size] as? NSNumber,
                   size.intValue > 1_048_576 {
                    let previous = url.appendingPathExtension("previous")
                    try? fm.removeItem(at: previous)
                    try fm.moveItem(at: url, to: previous)
                }
                if !fm.fileExists(atPath: url.path) {
                    fm.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600])
                }
                let handle = try FileHandle(forWritingTo: url)
                defer { try? handle.close() }
                try handle.seekToEnd()
                let timestamp = ISO8601DateFormatter().string(from: Date())
                let clean = details.replacingOccurrences(of: "\n", with: " ")
                try handle.write(contentsOf: Data("\(timestamp) \(name) \(clean)\n".utf8))
            } catch { /* Diagnostics must never interrupt controller input. */ }
        }
    }
}
