import Foundation
import CoreGraphics
import Darwin

@main
struct PlacementChecks {
    static func main() {
        let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
        let compactSize = CGSize(width: 224, height: 36)
        let expandedSize = CGSize(width: 420, height: 336)
        let suiteName = "CodexIsland.PlacementChecks.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let store = IslandPositionStore(defaults: defaults)
        let compact = CGRect(x: 608, y: 864, width: 224, height: 36)
        let saved = SavedIslandPosition(displayID: "external-display", frame: compact, screenFrame: screen)
        var count = 0

        func check(_ name: String, _ body: () -> Bool) {
            guard body() else { print("FAIL: \(name)"); exit(1) }
            count += 1
            print("PASS: \(name)")
        }
        func close(_ a: CGFloat, _ b: CGFloat) -> Bool { abs(a - b) < 0.0001 }
        func contains(_ outer: CGRect, _ inner: CGRect) -> Bool {
            inner.minX >= outer.minX && inner.maxX <= outer.maxX
                && inner.minY >= outer.minY && inner.maxY <= outer.maxY
        }

        check("首次运行没有自定义位置") { store.load() == nil }
        check("保存位置后重建存储对象仍可恢复") {
            store.save(saved)
            return IslandPositionStore(defaults: UserDefaults(suiteName: suiteName)!).load() == saved
        }
        check("相同屏幕恢复到原位置") { saved.frame(in: screen, size: compactSize) == compact }
        check("展开后保持顶部位置与横向中心") {
            let expanded = saved.frame(in: screen, size: expandedSize)
            return close(expanded.midX, compact.midX) && close(expanded.maxY, compact.maxY)
                && expanded.size == expandedSize
        }
        check("手动下移后恢复时保留顶部距离") {
            let moved = compact.offsetBy(dx: 150, dy: -80)
            let position = SavedIslandPosition(displayID: "external-display", frame: moved, screenFrame: screen)
            return close(CGFloat(position.topOffset), 80)
                && position.frame(in: screen, size: compactSize) == moved
        }
        check("左侧负原点显示器可正确保存和恢复") {
            let leftScreen = CGRect(x: -1920, y: -240, width: 1920, height: 1080)
            let moved = CGRect(x: -1672, y: 760, width: 224, height: 36)
            let position = SavedIslandPosition(displayID: "left-display", frame: moved, screenFrame: leftScreen)
            return position.frame(in: leftScreen, size: compactSize) == moved
                && close(CGFloat(position.topOffset), 44)
        }
        check("分辨率改变后保留横向比例和顶部距离") {
            let moved = CGRect(x: 248, y: 804, width: 224, height: 36)
            let position = SavedIslandPosition(displayID: "external-display", frame: moved, screenFrame: screen)
            let largerScreen = CGRect(x: 0, y: 0, width: 2560, height: 1440)
            let restored = position.frame(in: largerScreen, size: compactSize)
            return close(restored.midX, 640) && close(largerScreen.maxY - restored.maxY, 60)
        }
        check("四个方向的屏外位置都会收回屏幕") {
            let origins = [CGPoint(x: -500, y: 400), CGPoint(x: 1600, y: 400),
                           CGPoint(x: 400, y: 1100), CGPoint(x: 400, y: -500)]
            return origins.allSatisfy {
                contains(screen, IslandPlacement.clampedFrame(CGRect(origin: $0, size: expandedSize), in: screen))
            }
        }
        check("在屏幕边缘展开也不会出屏") {
            let origins = [CGPoint(x: 0, y: 0), CGPoint(x: 1216, y: 0),
                           CGPoint(x: 0, y: 864), CGPoint(x: 1216, y: 864)]
            return origins.allSatisfy {
                let position = SavedIslandPosition(displayID: "edge", frame: CGRect(origin: $0, size: compactSize), screenFrame: screen)
                return contains(screen, position.frame(in: screen, size: expandedSize))
            }
        }
        check("窗口碰到真实刘海时下移至刘海底边") {
            let notch = CGRect(x: 620, y: 868, width: 200, height: 32)
            let moved = IslandPlacement.clampedFrame(compact, in: screen, notchRect: notch)
            return moved.maxY == notch.minY && moved.midX == compact.midX
                && !moved.intersects(notch) && contains(screen, moved)
        }
        check("刘海旁边的窗口保留原高度") {
            let notch = CGRect(x: 620, y: 868, width: 200, height: 32)
            let side = CGRect(x: 60, y: 864, width: 224, height: 36)
            return IslandPlacement.clampedFrame(side, in: screen, notchRect: notch) == side
        }
        check("展开面板也会避开刘海") {
            let notch = CGRect(x: 620, y: 868, width: 200, height: 32)
            let expanded = saved.frame(in: screen, size: expandedSize)
            let moved = IslandPlacement.clampedFrame(expanded, in: screen, notchRect: notch)
            return moved.maxY == notch.minY && moved.size == expandedSize && contains(screen, moved)
        }
        check("大窗口和小屏幕组合仍保持在屏幕内") {
            let smallScreen = CGRect(x: -100, y: -50, width: 300, height: 200)
            let result = IslandPlacement.clampedFrame(CGRect(origin: .zero, size: expandedSize), in: smallScreen)
            return result == smallScreen
        }
        check("小屏幕也不会因刘海避让而越过底边") {
            let smallScreen = CGRect(x: -100, y: -50, width: 300, height: 200)
            let notch = CGRect(x: 0, y: 125, width: 100, height: 25)
            let result = IslandPlacement.clampedFrame(CGRect(origin: .zero, size: expandedSize), in: smallScreen, notchRect: notch)
            return contains(smallScreen, result) && result.maxY == notch.minY && !result.intersects(notch)
        }
        check("非有限坐标不会产生无效窗口位置") {
            let invalid = CGRect(x: CGFloat.nan, y: CGFloat.infinity, width: 224, height: 36)
            let result = IslandPlacement.clampedFrame(invalid, in: screen)
            return result == compact && result.origin.x.isFinite && result.origin.y.isFinite
        }
        check("非有限尺寸和无效屏幕安全回退") {
            let invalidSize = CGRect(x: 20, y: 20, width: CGFloat.infinity, height: CGFloat.nan)
            let result = IslandPlacement.clampedFrame(invalidSize, in: screen)
            let invalidScreen = CGRect(x: 0, y: 0, width: CGFloat.nan, height: 900)
            return result.width.isFinite && result.height.isFinite && contains(screen, result)
                && IslandPlacement.clampedFrame(compact, in: invalidScreen) == .zero
                && saved.frame(in: invalidScreen, size: expandedSize) == .zero
        }
        check("损坏存储、空显示器编号及非法比例均不会被采用") {
            let corrupted = [
                "invalid JSON", "{}",
                #"{"displayID":"","horizontalFraction":0.5,"topOffset":0}"#,
                #"{"displayID":"test","horizontalFraction":1.5,"topOffset":0}"#,
                #"{"displayID":"test","horizontalFraction":0.5,"topOffset":-1}"#,
                #"{"displayID":"test","horizontalFraction":"NaN","topOffset":0}"#,
                #"{"displayID":"test","horizontalFraction":0.5,"topOffset":1e999}"#
            ]
            for value in corrupted {
                defaults.set(Data(value.utf8), forKey: "island.placement.v1")
                if store.load() != nil { return false }
            }
            defaults.set("wrong storage type", forKey: "island.placement.v1")
            return store.load() == nil
        }
        check("重置只删除位置，保留其他设置") {
            defaults.set(true, forKey: "island.other-setting")
            store.save(saved)
            store.reset()
            return store.load() == nil && defaults.bool(forKey: "island.other-setting")
        }
        print("\(count) 项位置检查全部通过")
    }
}
