import Foundation
import CoreGraphics
import Darwin

@main
struct HoverChecks {
    static func main() {
        let compact = CGRect(x: 570, y: 920, width: 331, height: 36)
        let expanded = CGRect(x: 525.5, y: 620, width: 420, height: 336)
        var count = 0
        func check(_ name: String, _ body: () -> Bool) {
            guard body() else { print("FAIL: \(name)"); exit(1) }
            count += 1
            print("PASS: \(name)")
        }

        check("整个收起区域，包括中央刘海和留白，都能进入") {
            for x in stride(from: compact.minX, through: compact.maxX, by: 1) {
                for y in stride(from: compact.minY, through: compact.maxY, by: 1) {
                    var tracker = IslandHoverTracking()
                    if tracker.update(pointer: CGPoint(x: x, y: y), region: compact) != true { return false }
                }
            }
            return true
        }
        check("最顶部、四角和右边界均可触发") {
            let points = [CGPoint(x: 570, y: 920), CGPoint(x: 570, y: 956),
                          CGPoint(x: 901, y: 920), CGPoint(x: 901, y: 956), CGPoint(x: 735.5, y: 956)]
            return points.allSatisfy { point in
                var tracker = IslandHoverTracking()
                return tracker.update(pointer: point, region: compact) == true
            }
        }
        check("灵动岛以外不会触发") {
            let points = [CGPoint(x: 569, y: 938), CGPoint(x: 902, y: 938),
                          CGPoint(x: 735, y: 919), CGPoint(x: 735, y: 957)]
            return points.allSatisfy { point in
                var tracker = IslandHoverTracking()
                return tracker.update(pointer: point, region: compact) == nil && !tracker.isInside
            }
        }
        check("持续悬停不反复重启延迟，手动收起后需重新进入") {
            var tracker = IslandHoverTracking()
            let point = CGPoint(x: 735, y: 940)
            guard tracker.update(pointer: point, region: compact) == true else { return false }
            for _ in 0..<20 {
                if tracker.update(pointer: point, region: compact) != nil { return false }
            }
            return tracker.update(pointer: CGPoint(x: 0, y: 0), region: compact) == false
                && tracker.update(pointer: point, region: compact) == true
        }
        check("展开动画中移入详情区域仍保持在岛内") {
            var tracker = IslandHoverTracking()
            _ = tracker.update(pointer: CGPoint(x: 600, y: 940), region: compact)
            return tracker.update(pointer: CGPoint(x: 540, y: 650), region: compact.union(expanded)) == nil
                && tracker.isInside
        }
        check("展开后只有离开整块面板才收起") {
            var tracker = IslandHoverTracking()
            _ = tracker.update(pointer: CGPoint(x: 600, y: 940), region: expanded)
            guard tracker.update(pointer: CGPoint(x: 930, y: 635), region: expanded) == nil else { return false }
            return tracker.update(pointer: CGPoint(x: 950, y: 635), region: expanded) == false
        }
        check("取消固定可以重放当前的离开状态") {
            var tracker = IslandHoverTracking()
            return tracker.update(pointer: .zero, region: compact, force: true) == false
        }
        check("隐藏后不保留悬停范围") {
            var tracker = IslandHoverTracking()
            let point = CGPoint(x: 600, y: 940)
            _ = tracker.update(pointer: point, region: compact)
            return tracker.update(pointer: point, region: nil) == false
                && tracker.update(pointer: point, region: .zero) == nil
        }
        print("\(count) 项悬停检查全部通过")
    }
}
