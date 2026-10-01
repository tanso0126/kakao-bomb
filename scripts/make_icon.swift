// 💣 이모지로 AppIcon.iconset 생성. 사용: swift scripts/make_icon.swift <출력 .iconset 폴더>
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)
    // macOS 아이콘 그리드: 824/1024 크기의 둥근 사각형
    let inset = s * 100 / 1024
    let box = NSRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let path = NSBezierPath(roundedRect: box, xRadius: box.width * 0.225, yRadius: box.width * 0.225)
    NSGradient(starting: NSColor(red: 1.0, green: 0.86, blue: 0.2, alpha: 1),
               ending: NSColor(red: 1.0, green: 0.62, blue: 0.1, alpha: 1))!.draw(in: path, angle: -90)
    let font = NSFont.systemFont(ofSize: box.width * 0.62)
    let str = NSAttributedString(string: "💣", attributes: [.font: font])
    let sz = str.size()
    str.draw(at: NSPoint(x: box.midX - sz.width / 2, y: box.midY - sz.height / 2))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try render(base).write(to: out.appendingPathComponent("icon_\(base)x\(base).png"))
    try render(base * 2).write(to: out.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
