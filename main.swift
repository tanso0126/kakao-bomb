// 카톡 자폭 메시지
// 채팅창이 앞에 오면 창 옆에 트레이가 자동으로 뜸. 💣 자폭(또는 ⌃⌥D)으로 장전/해제 토글
// → 해제할 때까지 그 채팅창에서 보내는 내 메시지마다 설정한 시간(기본 0.5초) 뒤 "모두에게서 삭제".
// 지연 시간은 트레이 −/+ 버튼이나 메뉴바 💣 > 삭제 지연에서 조절 (재실행해도 유지).
// 빌드: swiftc -O -swift-version 5 main.swift -o kakao-bomb
// 실행: ./kakao-bomb        (--debug: 다이얼로그 구조를 stderr로 출력)

import Cocoa
import ApplicationServices
import Carbon.HIToolbox
import ServiceManagement

let defaultDelay: TimeInterval = 0.5
let maxDelay: TimeInterval = 10
let delayPresets: [TimeInterval] = [0, 0.3, 0.5, 1, 2, 3, 5]
let mainWindowTitle = "카카오톡"
let pollInterval: TimeInterval = 0.05
let deleteQueue = DispatchQueue(label: "kakao-bomb.delete", qos: .userInteractive)
let kakaoBundleID = "com.kakao.KakaoTalkMac"
let everyoneMenuID = "deleteToAllMessage:"   // 카톡 컨텍스트 메뉴 항목 identifier
let everyoneTitle = "모두에게서 삭제"
let confirmTitles: Set<String> = ["삭제", "확인"]
let debug = CommandLine.arguments.contains("--debug")

func log(_ s: String) {
    if debug { FileHandle.standardError.write((s + "\n").data(using: .utf8)!) }
}

// MARK: - AX helpers

func attr(_ e: AXUIElement, _ name: String) -> AnyObject? {
    var v: AnyObject?
    return AXUIElementCopyAttributeValue(e, name as CFString, &v) == .success ? v : nil
}
/// AX 속성을 타입 확인 후 변환 (카톡 UI가 바뀌는 중이면 엉뚱한 타입/nil이 올 수 있어 강제 캐스트 금지)
func elem(_ o: AnyObject?) -> AXUIElement? {
    guard let o, CFGetTypeID(o) == AXUIElementGetTypeID() else { return nil }
    return (o as! AXUIElement)
}
func axValue(_ o: AnyObject?) -> AXValue? {
    guard let o, CFGetTypeID(o) == AXValueGetTypeID() else { return nil }
    return (o as! AXValue)
}
func str(_ e: AXUIElement, _ name: String) -> String { attr(e, name) as? String ?? "" }
func role(_ e: AXUIElement) -> String { str(e, kAXRoleAttribute) }
func kids(_ e: AXUIElement) -> [AXUIElement] { attr(e, kAXChildrenAttribute) as? [AXUIElement] ?? [] }
func windows(_ app: AXUIElement) -> [AXUIElement] { attr(app, kAXWindowsAttribute) as? [AXUIElement] ?? [] }
func labels(_ e: AXUIElement) -> [String] {
    [kAXTitleAttribute, kAXDescriptionAttribute, kAXValueAttribute].map { str(e, $0) }
}
func actions(_ e: AXUIElement) -> [String] {
    var names: CFArray?
    AXUIElementCopyActionNames(e, &names)
    return names as? [String] ?? []
}
func frame(_ e: AXUIElement) -> CGRect? {
    guard let p = axValue(attr(e, kAXPositionAttribute)), let s = axValue(attr(e, kAXSizeAttribute)) else { return nil }
    var pt = CGPoint.zero, sz = CGSize.zero
    AXValueGetValue(p, .cgPoint, &pt)
    AXValueGetValue(s, .cgSize, &sz)
    return CGRect(origin: pt, size: sz)
}
func contains(_ list: [AXUIElement], _ e: AXUIElement) -> Bool { list.contains { CFEqual($0, e) } }

// BFS. 채팅 기록(AXTable)은 수천 개라 건너뜀.
func findAll(_ roots: [AXUIElement], depth: Int = 8, _ pred: (AXUIElement) -> Bool) -> [AXUIElement] {
    var out: [AXUIElement] = []
    var queue = roots.map { ($0, 0) }
    while !queue.isEmpty {
        let (e, d) = queue.removeFirst()
        if pred(e) { out.append(e) }
        if d < depth && role(e) != kAXTableRole { queue += kids(e).map { ($0, d + 1) } }
    }
    return out
}

func dump(_ e: AXUIElement, _ d: Int = 0) {
    guard debug, d < 8 else { return }
    log(String(repeating: "  ", count: d) + role(e) + " " + labels(e).filter { !$0.isEmpty }.joined(separator: " | ")
        + " " + actions(e).joined(separator: ","))
    if role(e) != kAXTableRole { kids(e).forEach { dump($0, d + 1) } }
}

// MARK: - Input fallback (AX 액션이 안 먹힐 때)

func click(_ e: AXUIElement) {
    guard let f = frame(e) else { return }
    let pt = CGPoint(x: f.midX, y: f.midY)
    let saved = CGEvent(source: nil)?.location
    CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown, mouseCursorPosition: pt, mouseButton: .left)?.post(tap: .cghidEventTap)
    CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp, mouseCursorPosition: pt, mouseButton: .left)?.post(tap: .cghidEventTap)
    if let saved {
        CGEvent(mouseEventSource: nil, mouseType: .mouseMoved, mouseCursorPosition: saved, mouseButton: .left)?.post(tap: .cghidEventTap)
    }
}

func activate(_ e: AXUIElement) {
    if actions(e).contains(kAXPressAction), AXUIElementPerformAction(e, kAXPressAction as CFString) == .success { return }
    click(e)
}

func waitFor<T>(_ timeout: TimeInterval, _ probe: () -> T?) -> T? {
    let deadline = Date().addingTimeInterval(timeout)
    repeat {
        if let v = probe() { return v }
        usleep(30_000)
    } while Date() < deadline
    return nil
}

// MARK: - Chat model

struct Bubble {
    let row: AXUIElement
    let index: Int                // 테이블 행 번호 (카톡이 행을 다시 그려도 유지됨)
    let menuTarget: AXUIElement   // AXShowMenu 지원하는 말풍선 요소
    let shape: AXUIElement        // 위치 판정용 (말풍선 배경 이미지)
    var text: String { str(menuTarget, kAXValueAttribute) }
}

func chatTable(_ win: AXUIElement) -> AXUIElement? {
    for sa in kids(win) where role(sa) == kAXScrollAreaRole {
        if let t = kids(sa).first(where: { role($0) == kAXTableRole }) { return t }
    }
    return nil
}

func rowCount(_ t: AXUIElement) -> Int {
    var n: CFIndex = 0
    AXUIElementGetAttributeValueCount(t, kAXRowsAttribute as CFString, &n)
    return n
}

func bubble(at i: Int, in t: AXUIElement) -> Bubble? {
    var arr: CFArray?
    guard i >= 0, AXUIElementCopyAttributeValues(t, kAXRowsAttribute as CFString, i, 1, &arr) == .success,
          let row = (arr as? [AXUIElement])?.first, let cell = kids(row).first else { return nil }
    let items = kids(cell)
    guard let target = items.first(where: { actions($0).contains("AXShowMenu") }) else { return nil }
    return Bubble(row: row, index: i, menuTarget: target, shape: items.first { role($0) == kAXImageRole } ?? target)
}

func lastBubble(_ t: AXUIElement) -> Bubble? {
    let n = rowCount(t)
    // lazy.compactMap.first는 변환을 두 번 돌리고 두 번째를 강제 언랩함 → 카톡이 그 사이 행을 다시 그리면 크래시. 일반 루프로.
    for i in stride(from: n - 1, through: max(0, n - 4), by: -1) {
        if let b = bubble(at: i, in: t) { return b }
    }
    return nil
}

// 남의 메시지: 왼쪽 정렬(+프로필). 내 메시지: 오른쪽 정렬.
func isMine(_ b: Bubble, in table: AXUIElement) -> Bool {
    guard let cell = kids(b.row).first else { return false }
    if kids(cell).contains(where: { role($0) == kAXButtonRole && str($0, kAXDescriptionAttribute) == "프로필" }) {
        return false
    }
    guard let tf = frame(table), let bf = frame(b.shape) else { return false }
    return tf.maxX - bf.maxX < bf.minX - tf.minX
}

// 화면 맨 아래에 실제로 보이는 말풍선인지 (위로 스크롤해 과거 기록 로딩될 때 오탐 방지)
func isVisible(_ b: Bubble, in table: AXUIElement) -> Bool {
    guard let sa = elem(attr(table, kAXParentAttribute)), let sf = frame(sa),
          let bf = frame(b.shape) else { return false }
    return sf.intersects(bf)
}

// MARK: - Armed window

func isChatWindow(_ win: AXUIElement) -> Bool {
    str(win, kAXTitleAttribute) != mainWindowTitle && chatTable(win) != nil
}

/// 장전된 채팅창 하나. 장전 시점 상태를 기준으로 새로 올라온 내 말풍선을 감지.
final class Armed {
    let window: AXUIElement
    let table: AXUIElement
    var row: AXUIElement?
    var count: Int

    init?(window: AXUIElement) {
        guard let t = chatTable(window) else { return nil }
        self.window = window
        table = t
        row = lastBubble(t)?.row
        count = rowCount(t)
    }

    func poll() -> Bubble? {
        let n = rowCount(table)
        defer { count = n }
        guard n > count, let b = lastBubble(table), row.map({ !CFEqual($0, b.row) }) ?? true else { return nil }
        row = b.row
        return isMine(b, in: table) && isVisible(b, in: table) ? b : nil
    }
}

// MARK: - Delete for everyone

/// 감지 후 카톡이 행을 다시 그리거나 앞쪽에 행을 끼워 넣어도(단톡) 같은 메시지를 다시 찾음.
/// 최근 행부터 내 말풍선 중 내용이 같은 것 (같은 말 연속으로 보냈으면 가장 최근 것).
func findSent(_ sent: Bubble, text: String, in t: AXUIElement) -> Bubble? {
    let n = rowCount(t)
    let mine = stride(from: n - 1, through: max(0, sent.index - 5), by: -1)  // n이 순간 0으로 읽혀도 빈 범위
        .compactMap { bubble(at: $0, in: t) }.filter { isMine($0, in: t) }
    if let hit = text.isEmpty ? mine.first : mine.first(where: { $0.text == text }) { return hit }
    log("내용 일치 없음: 감지 행 \(sent.index) len\(text.count), 지금 rows \(n), 내 말풍선 "
        + mine.map { "\($0.index):len\($0.text.count)" }.joined(separator: " "))
    return nil
}

// 말풍선 우클릭 메뉴(NSMenu)는 AXShowMenu 대상 요소의 자식으로 붙음
func contextMenus(_ app: AXUIElement, near target: AXUIElement) -> [AXUIElement] {
    var roots = kids(target) + kids(app)   // 메뉴바(AXMenuBar)는 role이 달라 제외됨
    if let parent = elem(attr(target, kAXParentAttribute)) { roots += kids(parent) }
    return roots.filter { role($0) == kAXMenuRole }
}

func everyoneMenuItem(_ menus: [AXUIElement]) -> AXUIElement? {
    menus.lazy.flatMap(kids).first {
        str($0, "AXIdentifier") == everyoneMenuID || str($0, kAXTitleAttribute) == everyoneTitle
    }
}

func deleteForEveryone(app: AXUIElement, table: AXUIElement, sent: Bubble, window win: AXUIElement) -> Bool {
    let before = windows(app)
    let sentText = sent.text

    // 1) 말풍선 메뉴 → "모두에게서 삭제". 감지 때 잡은 요소는 카톡이 다시 그리면 죽으므로 매번 새로 찾음.
    var found: AXUIElement?
    for attempt in 1...4 {
        guard let b = findSent(sent, text: sentText, in: table) else {
            log("[\(attempt)] 보낸 말풍선 다시 못 찾음, 재시도")
            usleep(150_000)
            continue
        }
        if b.index != sent.index { log("행 이동: \(sent.index) → \(b.index)") }
        AXUIElementSetMessagingTimeout(b.menuTarget, 0.3)  // 메뉴 추적 중 응답 지연 대비
        let err = AXUIElementPerformAction(b.menuTarget, "AXShowMenu" as CFString)
        let menus = waitFor(0.5) { () -> [AXUIElement]? in
            let m = contextMenus(app, near: b.menuTarget)
            return m.isEmpty ? nil : m
        } ?? []
        log("[\(attempt)] AXShowMenu=\(err.rawValue) 메뉴 \(menus.count)개")
        if let item = everyoneMenuItem(menus), (attr(item, kAXEnabledAttribute) as? Bool) != false {
            found = item
            break
        }
        if !menus.isEmpty {
            log("메뉴 항목: " + menus.flatMap(kids).map { str($0, kAXTitleAttribute) }.filter { !$0.isEmpty }.joined(separator: ", "))
        }
        menus.forEach { AXUIElementPerformAction($0, kAXCancelAction as CFString) }
        usleep(150_000)
    }
    guard let item = found else {
        log("'\(everyoneTitle)' 못 찾음")
        return false
    }
    log("'\(everyoneTitle)' 클릭")
    AXUIElementPerformAction(item, kAXPressAction as CFString)

    // 2) 확인창 → "삭제"/"확인" (여러 번 떠도 처리, "나에게서만 삭제"는 정확히 일치 안 해서 안 눌림)
    func scope() -> [AXUIElement] {
        let all = windows(app)
        var s = all.filter { !contains(before, $0) }
        for w in all { s += kids(w).filter { role($0) == kAXSheetRole } }
        return s + [win]
    }
    func confirmButton() -> AXUIElement? {
        let hits = findAll(scope()) {
            [kAXButtonRole, kAXStaticTextRole].contains(role($0)) && !labels($0).filter(confirmTitles.contains).isEmpty
        }
        return hits.first { actions($0).contains(kAXPressAction) } ?? hits.first
    }

    let start = Date()
    var lastPress: Date?
    var dumped = false
    while Date().timeIntervalSince(start) < 3 {
        if let btn = confirmButton() {
            log("확인 버튼: \(labels(btn).filter { !$0.isEmpty })")
            activate(btn)
            lastPress = Date()
            usleep(200_000)
            continue
        }
        if let t = lastPress, Date().timeIntervalSince(t) > 0.6 { return true }  // 더 누를 확인창 없음
        if lastPress == nil, Date().timeIntervalSince(start) > 1.5 {
            log("확인창 없이 끝남 (바로 삭제된 것으로 간주)")
            return true
        }
        if debug && !dumped && Date().timeIntervalSince(start) > 0.5 {
            dumped = true
            log("--- 확인창 후보 ---")
            scope().dropLast().forEach { dump($0) }
        }
        usleep(50_000)
    }
    log("시간 초과: 확인창이 계속 남아 있음")
    return false
}

// MARK: - Hotkey (⌃⌥D)

var hotKeyHandler: (() -> Void)?
var hotKeyRef: EventHotKeyRef?

func registerHotKey(_ handler: @escaping () -> Void) {
    hotKeyHandler = handler
    var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
        DispatchQueue.main.async { hotKeyHandler?() }
        return noErr
    }, 1, &spec, nil, nil)
    let id = EventHotKeyID(signature: OSType(0x4B42_4F4D), id: 1)  // 'KBOM'
    RegisterEventHotKey(UInt32(kVK_ANSI_D), UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &hotKeyRef)
}

// MARK: - Tray (채팅창 옆 플로팅 패널)

final class TrayButton: NSButton {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

final class Tray: NSPanel {
    let bomb = TrayButton(title: "", target: nil, action: nil)
    let delayLabel = NSTextField(labelWithString: "")

    init(owner: AppDelegate) {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 210, height: 34),
                   styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: true)
        level = .floating
        isFloatingPanel = true
        hidesOnDeactivate = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = true

        let bg = NSVisualEffectView(frame: NSRect(origin: .zero, size: frame.size))
        bg.material = .hudWindow
        bg.state = .active
        bg.wantsLayer = true
        bg.layer?.cornerRadius = 9
        bg.layer?.masksToBounds = true
        contentView = bg

        func button(_ title: String, _ action: Selector) -> TrayButton {
            let b = TrayButton(title: title, target: owner, action: action)
            b.bezelStyle = .rounded
            b.controlSize = .small
            return b
        }
        bomb.target = owner
        bomb.action = #selector(AppDelegate.bombClicked)
        bomb.bezelStyle = .rounded
        bomb.controlSize = .small
        bomb.widthAnchor.constraint(equalToConstant: 92).isActive = true
        delayLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        delayLabel.alignment = .center
        delayLabel.widthAnchor.constraint(equalToConstant: 38).isActive = true

        let stack = NSStackView(views: [bomb, button("−", #selector(AppDelegate.decDelay)), delayLabel,
                                        button("+", #selector(AppDelegate.incDelay))])
        stack.spacing = 2
        stack.translatesAutoresizingMaskIntoConstraints = false
        bg.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerYAnchor.constraint(equalTo: bg.centerYAnchor),
            stack.centerXAnchor.constraint(equalTo: bg.centerXAnchor),
        ])
    }

    /// 채팅창(AX 좌표: 좌상단 원점) 위쪽 오른편에 붙임. 공간 없으면 오른쪽 → 왼쪽 → 창 안쪽.
    func place(near win: CGRect) {
        guard let primaryH = NSScreen.screens.first?.frame.height else { return }
        let w = frame.width, h = frame.height, gap: CGFloat = 4
        let candidates = [
            CGRect(x: win.maxX - w, y: win.minY - h - gap, width: w, height: h),
            CGRect(x: win.maxX + gap, y: win.minY, width: w, height: h),
            CGRect(x: win.minX - w - gap, y: win.minY, width: w, height: h),
            CGRect(x: win.maxX - w - 8, y: win.minY + 40, width: w, height: h),
        ].map { CGRect(x: $0.minX, y: primaryH - $0.maxY, width: w, height: h) }
        let pick = candidates.first { c in NSScreen.screens.contains { $0.visibleFrame.contains(c) } } ?? candidates.last!
        if frame.origin != pick.origin { setFrameOrigin(pick.origin) }
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let delayMenu = NSMenu()
    lazy var tray = Tray(owner: self)
    var armed: [Armed] = []
    var flash: (window: AXUIElement, text: String)?  // ⏳/✅/❌ 잠깐 표시
    var current: AXUIElement?                         // 트레이가 붙어 있는 채팅창
    var kakao: (pid: pid_t, app: AXUIElement)?
    var trusted = false
    var ticks = 0
    let permissionItem = NSMenuItem(title: "⚠️ 손쉬운 사용 권한 필요 — 설정 열기", action: #selector(openAccessibilitySettings), keyEquivalent: "")
    let loginItem = NSMenuItem(title: "로그인 시 자동 실행", action: #selector(toggleLoginItem), keyEquivalent: "")

    var delay: TimeInterval = UserDefaults.standard.object(forKey: "deleteDelay") as? Double ?? defaultDelay {
        didSet {
            delay = min(maxDelay, max(0, (delay * 10).rounded() / 10))
            UserDefaults.standard.set(delay, forKey: "deleteDelay")
            refresh()
        }
    }

    func applicationDidFinishLaunching(_ note: Notification) {
        // 권한 없으면 시스템이 "손쉬운 사용" 허용 창을 띄움. 허용하면 재시작 없이 바로 동작.
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        trusted = AXIsProcessTrustedWithOptions(opts)

        let menu = NSMenu()
        menu.delegate = self
        permissionItem.target = self
        menu.addItem(permissionItem)
        let hint = NSMenuItem(title: "카톡 채팅창 옆 💣 버튼 또는 ⌃⌥D로 장전/해제", action: nil, keyEquivalent: "")
        hint.isEnabled = false
        menu.addItem(hint)
        menu.addItem(.separator())
        let delayItem = NSMenuItem(title: "삭제 지연", action: nil, keyEquivalent: "")
        delayMenu.delegate = self
        delayItem.submenu = delayMenu
        menu.addItem(delayItem)
        loginItem.target = self
        menu.addItem(loginItem)
        menu.addItem(.separator())
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        let versionItem = NSMenuItem(title: "kakao-bomb \(version)", action: nil, keyEquivalent: "")
        versionItem.isEnabled = false
        menu.addItem(versionItem)
        menu.addItem(NSMenuItem(title: "종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        status.menu = menu
        registerHotKey { [weak self] in self?.hotKey() }

        let timer = Timer(timeInterval: pollInterval, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(timer, forMode: .common)
        refresh()
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === delayMenu else {
            permissionItem.isHidden = trusted
            loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
            return
        }
        menu.removeAllItems()
        for d in delayPresets {
            let item = NSMenuItem(title: String(format: "%.1f초", d), action: #selector(pickDelay(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = d
            item.state = abs(d - delay) < 0.01 ? .on : .off
            menu.addItem(item)
        }
    }

    // MARK: actions

    @objc func openAccessibilitySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    @objc func toggleLoginItem() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            NSSound.beep()
            log("로그인 항목 설정 실패: \(error)")
        }
    }

    @objc func pickDelay(_ sender: NSMenuItem) { delay = sender.representedObject as! Double }
    @objc func decDelay() { delay -= 0.1 }
    @objc func incDelay() { delay += 0.1 }
    @objc func bombClicked() { if let w = current { toggle(w) } }

    func hotKey() {
        guard let app = kakaoApp(), let w = elem(attr(app, kAXFocusedWindowAttribute)),
              isChatWindow(w) else { NSSound.beep(); return }
        toggle(w)
    }

    func toggle(_ win: AXUIElement) {
        if let i = armed.firstIndex(where: { CFEqual($0.window, win) }) {
            armed.remove(at: i)
        } else if let a = Armed(window: win) {
            armed.append(a)
            NSSound(named: "Tink")?.play()
        }
        refresh()
    }

    // MARK: loop

    func kakaoApp() -> AXUIElement? {
        guard let pid = NSRunningApplication.runningApplications(withBundleIdentifier: kakaoBundleID).first?.processIdentifier else {
            kakao = nil
            return nil
        }
        if kakao?.pid != pid { kakao = (pid, AXUIElementCreateApplication(pid)) }
        return kakao?.app
    }

    func tick() {
        ticks += 1
        if !trusted || ticks % 20 == 0 {  // 권한은 1초마다만 다시 확인
            let now = AXIsProcessTrusted()
            if now != trusted { trusted = now; refresh() }
        }
        guard trusted, let app = kakaoApp() else {
            armed.removeAll()
            hideTray()
            return
        }
        let wins = windows(app)
        armed.removeAll { !contains(wins, $0.window) }  // 닫힌 채팅창
        for a in armed {
            if let b = a.poll() { fire(app: app, armed: a, bubble: b) }
        }
        followFocusedChat(app)
    }

    func followFocusedChat(_ app: AXUIElement) {
        guard NSWorkspace.shared.frontmostApplication?.bundleIdentifier == kakaoBundleID,
              let w = elem(attr(app, kAXFocusedWindowAttribute)),
              isChatWindow(w), (attr(w, kAXMinimizedAttribute) as? Bool) != true,
              let f = frame(w) else {
            hideTray()
            return
        }
        if current.map({ !CFEqual($0, w) }) ?? true {
            current = w
            refresh()
        }
        tray.place(near: f)
        if !tray.isVisible { tray.orderFrontRegardless() }
    }

    func hideTray() {
        current = nil
        if tray.isVisible { tray.orderOut(nil) }
    }

    func fire(app: AXUIElement, armed a: Armed, bubble: Bubble) {
        // 장전은 유지 (토글). 연달아 보내도 메뉴 조작이 겹치지 않게 삭제는 한 번에 하나씩.
        setFlash(a.window, "⏳")
        deleteQueue.asyncAfter(deadline: .now() + delay) {
            let ok = deleteForEveryone(app: app, table: a.table, sent: bubble, window: a.window)
            DispatchQueue.main.async {
                NSSound(named: ok ? "Pop" : "Basso")?.play()
                self.setFlash(a.window, ok ? "✅ 삭제됨" : "❌ 실패")
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                    if let f = self.flash, CFEqual(f.window, a.window) { self.flash = nil; self.refresh() }
                }
            }
        }
    }

    func setFlash(_ win: AXUIElement, _ text: String) {
        flash = (win, text)
        refresh()
    }

    func refresh() {
        status.button?.title = !trusted ? "⚠️" : armed.isEmpty ? "💣" : "🔥"
        tray.delayLabel.stringValue = String(format: "%.1f초", delay)
        guard let w = current else { return }
        if let f = flash, CFEqual(f.window, w) {
            tray.bomb.title = f.text
        } else {
            tray.bomb.title = armed.contains { CFEqual($0.window, w) } ? "🔥 장전됨" : "💣 자폭"
        }
    }
}

// --check: 삭제 없이 각 채팅창 마지막 말풍선 판정만 출력
if CommandLine.arguments.contains("--check") {
    guard let pid = NSRunningApplication.runningApplications(withBundleIdentifier: kakaoBundleID).first?.processIdentifier else {
        print("카카오톡 실행 안 됨"); exit(1)
    }
    let kakao = AXUIElementCreateApplication(pid)
    for win in windows(kakao) {
        guard let t = chatTable(win) else { continue }
        let title = str(win, kAXTitleAttribute)
        if let b = lastBubble(t) {
            print("\(title): rows=\(rowCount(t)) 마지막 말풍선 mine=\(isMine(b, in: t)) visible=\(isVisible(b, in: t))")
        } else {
            print("\(title): rows=\(rowCount(t)) 말풍선 없음")
        }
    }
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
