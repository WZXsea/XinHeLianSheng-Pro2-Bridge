import Foundation

public enum Pro2TransportKind: String, Sendable { case bluetooth, usb }
public struct Pro2TransportDevice: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let kind: Pro2TransportKind
    public init(id: String, name: String, kind: Pro2TransportKind) {
        self.id = id; self.name = name; self.kind = kind
    }
}
/// Invoke transport methods from the main thread. All callbacks arrive on the main thread.
public protocol Pro2InputTransport: AnyObject {
    var onConnectionChanged: ((Bool) -> Void)? { get set }
    var onReport: ((Data) -> Void)? { get set }
    var onStatus: ((String) -> Void)? { get set }
    var onDevices: (([Pro2TransportDevice]) -> Void)? { get set }
    func startDiscovery()
    func connect(to id: String)
    func disconnect()
    func stop()
    /// Transport-native rumble packet; USB includes report ID 02.
    func writeOutput(_ report: Data)
}
