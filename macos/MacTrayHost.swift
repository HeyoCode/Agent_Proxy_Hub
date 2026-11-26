import Cocoa
import WebKit

// macOS 原生轻量级菜单栏托盘 NSPopover 快捷面板 + 无边框圆角 WKWebView 主控制台宿主
// 自动拉起同目录下的 AgentProxyHub.Host (.NET 10 arm64/x64 原生内核)
class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKScriptMessageHandler {
    var statusItem: NSStatusItem!
    var popover: NSPopover!
    var popoverWebView: WKWebView!
    var window: NSWindow!
    var webView: WKWebView!
    var hostProcess: Process?

    func applicationDidFinishLaunching(_ notification: Notification) {
        startBackendHost()

        // 1. 初始化菜单栏图标与原生 NSPopover 快捷面板
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "APH"
        statusItem.button?.toolTip = "Agent Proxy Hub v0.4.2 · 物理直出独立代理"
        statusItem.button?.target = self
        statusItem.button?.action = #selector(togglePopover(_:))

        let popoverController = WKUserContentController()
        popoverController.add(self, name: "windowControl")
        let popoverConfig = WKWebViewConfiguration()
        popoverConfig.userContentController = popoverController

        let popoverRect = NSRect(x: 0, y: 0, width: 384, height: 472)
        popoverWebView = WKWebView(frame: popoverRect, configuration: popoverConfig)
        let vc = NSViewController()
        vc.view = popoverWebView

        popover = NSPopover()
        popover.contentSize = NSSize(width: 384, height: 472)
        popover.behavior = .transient
        popover.animates = true
        popover.contentViewController = vc

        // 2. 创建无边框全尺寸内容视图主窗口，隐藏系统默认标题栏与红黄绿按钮
        let rect = NSRect(x: 0, y: 0, width: 1360, height: 850)
        window = NSWindow(
            contentRect: rect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "Agent Proxy Hub v0.4.2"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isMovableByWindowBackground = false
        window.minSize = NSSize(width: 1080, height: 700)
        window.standardWindowButton(.closeButton)?.isHidden = true
        window.standardWindowButton(.miniaturizeButton)?.isHidden = true
        window.standardWindowButton(.zoomButton)?.isHidden = true
        window.center()
        window.delegate = self

        let contentController = WKUserContentController()
        contentController.add(self, name: "windowControl")

        let config = WKWebViewConfiguration()
        config.userContentController = contentController

        webView = WKWebView(frame: rect, configuration: config)
        window.contentView = webView

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) {
            if let url = URL(string: "http://127.0.0.1:17880/?v=0.4.2") {
                self.webView.load(URLRequest(url: url))
            }
            if let trayUrl = URL(string: "http://127.0.0.1:17880/?tray=1&v=0.4.2") {
                self.popoverWebView.load(URLRequest(url: trayUrl))
            }
        }
        window.makeKeyAndOrderFront(nil)
    }

    @objc func togglePopover(_ sender: AnyObject?) {
        guard let button = statusItem.button else { return }
        if popover.isShown {
            popover.performClose(sender)
        } else {
            popoverWebView.evaluateJavaScript("window.dispatchEvent(new Event('focus'));", completionHandler: nil)
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "windowControl",
              let dict = message.body as? [String: Any],
              let type = dict["type"] as? String else {
            return
        }

        switch type {
        case "window:drag":
            if let event = NSApp.currentEvent {
                window.performDrag(with: event)
            }
        case "window:minimize":
            window.miniaturize(nil)
        case "window:toggleMaximize":
            window.zoom(nil)
        case "window:hideToTray":
            window.orderOut(nil)
        case "tray:openMain":
            popover.performClose(nil)
            showWindow()
        case "tray:hide":
            popover.performClose(nil)
        case "tray:exitApp":
            popover.performClose(nil)
            quitAll()
        case "tray:resize":
            if let h = dict["height"] as? CGFloat {
                let clamped = max(340, min(620, h))
                popover.contentSize = NSSize(width: 384, height: clamped)
            }
        case "window:openUrl":
            if let raw = dict["url"] as? String, let url = URL(string: raw) {
                NSWorkspace.shared.open(url)
            }
        default:
            break
        }
    }

    func startBackendHost() {
        let exeDir = Bundle.main.executableURL?.deletingLastPathComponent()
        let hostUrl = exeDir?.appendingPathComponent("AgentProxyHub.Host")
        guard let target = hostUrl, FileManager.default.isExecutableFile(atPath: target.path) else {
            return
        }
        let proc = Process()
        proc.executableURL = target
        proc.currentDirectoryURL = exeDir
        try? proc.run()
        hostProcess = proc
    }

    @objc func showWindow() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        // 关闭窗口时隐藏到顶部菜单栏，保持独立代理不断线
        window.orderOut(nil)
        return false
    }

    @objc func quitAll() {
        hostProcess?.terminate()
        NSApp.terminate(nil)
    }

    func applicationWillTerminate(_ notification: Notification) {
        hostProcess?.terminate()
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
