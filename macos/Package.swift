// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Pro2Mac", platforms: [.macOS(.v13)],
    products: [
        .executable(name: "Pro2Mac", targets: ["Pro2Mac"]),
        .executable(name: "pro2-diagnostics", targets: ["Pro2Diagnostics"])
    ],
    targets: [
        .target(name: "Pro2Core"),
        .target(name: "CPro2USB", path: "Sources/Pro2Transport/CPro2USB", publicHeadersPath: "include",
                linkerSettings: [.linkedFramework("IOKit"), .linkedFramework("CoreFoundation")]),
        .target(name: "Pro2Transport", dependencies: ["CPro2USB"], exclude: ["CPro2USB"],
                linkerSettings: [.linkedFramework("CoreBluetooth"), .linkedFramework("IOKit")]),
        .target(name: "CPro2Virtual", publicHeadersPath: "include",
                linkerSettings: [.linkedFramework("IOKit"), .linkedFramework("CoreFoundation")]),
        .target(name: "Pro2Output", dependencies: ["Pro2Core", "CPro2Virtual"],
                linkerSettings: [.linkedFramework("ApplicationServices")]),
        .executableTarget(name: "Pro2Mac", dependencies: ["Pro2Core", "Pro2Transport", "Pro2Output"]),
        .executableTarget(name: "Pro2Diagnostics", dependencies: ["Pro2Core", "Pro2Transport", "Pro2Output"]),
        .testTarget(name: "Pro2CoreTests", dependencies: ["Pro2Core", "Pro2Output"])
    ]
)
