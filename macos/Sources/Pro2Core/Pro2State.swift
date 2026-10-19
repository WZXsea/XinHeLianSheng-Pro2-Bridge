// Swift adaptation of Y700Switch2V60Viiper (Apache-2.0), see repository LICENSE.
// Protocol source: source/final-three-in-one/windows/v60_viiper_app/Pro2HidReportParser.cs.
import Foundation

public struct Pro2Buttons: OptionSet, Sendable, Equatable {
    public let rawValue: UInt32
    public init(rawValue: UInt32) { self.rawValue = rawValue }
    public static let west = Self(rawValue: 1 << 0)
    public static let north = Self(rawValue: 1 << 1)
    public static let south = Self(rawValue: 1 << 2)
    public static let east = Self(rawValue: 1 << 3)
    public static let r1 = Self(rawValue: 1 << 6)
    public static let r2 = Self(rawValue: 1 << 7)
    public static let back = Self(rawValue: 1 << 8)
    public static let start = Self(rawValue: 1 << 9)
    public static let rightStick = Self(rawValue: 1 << 10)
    public static let leftStick = Self(rawValue: 1 << 11)
    public static let home = Self(rawValue: 1 << 12)
    public static let capture = Self(rawValue: 1 << 13)
    public static let aux = Self(rawValue: 1 << 14)
    public static let dpadDown = Self(rawValue: 1 << 16)
    public static let dpadUp = Self(rawValue: 1 << 17)
    public static let dpadRight = Self(rawValue: 1 << 18)
    public static let dpadLeft = Self(rawValue: 1 << 19)
    public static let l1 = Self(rawValue: 1 << 22)
    public static let l2 = Self(rawValue: 1 << 23)
    public static let paddleRight = Self(rawValue: 1 << 24)
    public static let paddleLeft = Self(rawValue: 1 << 25)
}

public struct Pro2Axes: Sendable, Equatable {
    /// Native 12-bit coordinates, 0...4095, center 2048. Positive Y points up.
    public var lx: UInt16 = 2048
    public var ly: UInt16 = 2048
    public var rx: UInt16 = 2048
    public var ry: UInt16 = 2048
    public init(lx: UInt16 = 2048, ly: UInt16 = 2048, rx: UInt16 = 2048, ry: UInt16 = 2048) {
        self.lx = lx; self.ly = ly; self.rx = rx; self.ry = ry
    }
}

public struct Pro2MotionSample: Sendable, Equatable {
    /// Signed raw sensor counts, before factory or stationary bias calibration.
    public let accelX: Int16, accelY: Int16, accelZ: Int16
    public let gyroX: Int16, gyroY: Int16, gyroZ: Int16
}

public struct Pro2Battery: Sendable, Equatable {
    public var level: UInt8? = nil // Primary power-info scale: 0...9.
    public var voltageMillivolts: UInt16? = nil
    public var externalPower: Bool? = nil
    public var charging: Bool? = nil
    public var approximatePercent: Int? { level.map { (Int($0) * 100 + 4) / 9 } }
    public init() {}
}

public struct Pro2State: Sendable, Equatable {
    public var buttons: Pro2Buttons = []
    public var rawAxes = Pro2Axes()
    public var axes = Pro2Axes()
    public var battery = Pro2Battery()
    public var headsetPresent: Bool? = nil
    public var motionTimestampMicroseconds: UInt32? = nil
    public var motionSamples: [Pro2MotionSample] = []
    public var leftTrigger: Double { buttons.contains(.l2) ? 1 : 0 }
    public var rightTrigger: Double { buttons.contains(.r2) ? 1 : 0 }
    public init() {}
}

public enum Pro2InputFormat: String, Sendable, CaseIterable {
    case fd2, usb, primary, standard, legacy
}
public enum Pro2ProtocolError: Error, Equatable {
    case invalidLength(expectedAtLeast: Int, actual: Int)
    case invalidReportID
    case reservedButtonBits
    case invalidRumbleBlock
}
