// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CodexIsland",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "CodexIsland", targets: ["CodexIsland"])],
    targets: [
        .executableTarget(name: "CodexIsland")
    ]
)
