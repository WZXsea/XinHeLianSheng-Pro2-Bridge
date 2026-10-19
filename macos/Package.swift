// swift-tools-version: 5.9
import PackageDescription
import Foundation
let hostInfoPath = URL(fileURLWithPath:#filePath).deletingLastPathComponent().appendingPathComponent("NativeHostInfo.plist").path

let package = Package(
    name: "Pro2Native", platforms: [.macOS(.v13)],
    products: [
        .executable(name: "pro2-diagnostics", targets: ["Pro2Diagnostics"]),
        .executable(name: "pro2-native-host", targets: ["Pro2NativeHost"])
    ],
    targets: [
        .target(name: "Pro2Core"),
        .target(name: "CPro2Broker", exclude: ["Shim"], publicHeadersPath:"include"),
        .target(name: "Pro2Broker", dependencies: ["Pro2Core", "CPro2Broker"]),
        .target(name: "CPro2USB", path: "Sources/Pro2Transport/CPro2USB", publicHeadersPath: "include",
                linkerSettings: [.linkedFramework("IOKit"), .linkedFramework("CoreFoundation")]),
        .target(name: "Pro2Transport", dependencies: ["CPro2USB"], exclude: ["CPro2USB"],
                linkerSettings: [.linkedFramework("CoreBluetooth"), .linkedFramework("IOKit")]),
        .target(name: "CPro2Virtual", publicHeadersPath: "include",
                linkerSettings: [.linkedFramework("IOKit"), .linkedFramework("CoreFoundation")]),
        .target(name: "Pro2Output", dependencies: ["Pro2Core", "CPro2Virtual"],
                linkerSettings: [.linkedFramework("ApplicationServices")]),
        .executableTarget(name: "Pro2Diagnostics", dependencies: ["Pro2Core", "Pro2Transport", "Pro2Output"]),
        .executableTarget(name: "Pro2NativeHost", dependencies: ["Pro2Core", "Pro2Transport", "Pro2Output", "Pro2Broker"], linkerSettings: [.unsafeFlags(["-Xlinker","-sectcreate","-Xlinker","__TEXT","-Xlinker","__info_plist","-Xlinker",hostInfoPath])]),
        .testTarget(name: "Pro2CoreTests", dependencies: ["Pro2Core", "Pro2Output"]),
        .testTarget(name: "Pro2TransportTests", dependencies: ["Pro2Transport"]),
    ]
)
