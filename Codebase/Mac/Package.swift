// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "CtrlLink",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "CtrlLinkCore"),
        .executableTarget(name: "CtrlLink", dependencies: ["CtrlLinkCore"]),
        .testTarget(name: "CtrlLinkCoreTests", dependencies: ["CtrlLinkCore"]),
    ]
)
