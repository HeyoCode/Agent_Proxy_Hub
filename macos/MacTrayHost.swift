import Cocoa
import WebKit

// macOS 原生轻量级菜单栏托盘 NSPopover 快捷面板 + 无边框圆角 WKWebView 主控制台宿主
// 自动拉起同目录下的 AgentProxyHub.Host (.NET 10 arm64/x64 原生内核)
class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSPopoverDelegate, WKScriptMessageHandler {
    let appVersion = "0.8.0"
    var apiBaseUrl = "http://127.0.0.1:17880"
    var statusItem: NSStatusItem!
    var popover: NSPopover!
    var popoverWebView: WKWebView!
    var window: NSWindow!
    var webView: WKWebView!
    var hostProcess: Process?
    lazy var probeSession: URLSession = {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.connectionProxyDictionary = [:]
        cfg.timeoutIntervalForRequest = 0.25
        return URLSession(configuration: cfg)
    }()

    func resolveConfiguredApiPort() -> Int {
        let stateUrl = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".agent-proxy-hub")
            .appendingPathComponent("state.json")
        if let data = try? Data(contentsOf: stateUrl),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let port = json["hostApiPort"] as? Int,
           port >= 1024 && port <= 65535 {
            return port
        }
        return 17880
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let port = resolveConfiguredApiPort()
        apiBaseUrl = "http://127.0.0.1:\(port)"
        startBackendHost()

        // 1. 初始化菜单栏图标与原生 NSPopover 快捷面板
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "APH"
        statusItem.button?.toolTip = "Agent Proxy Hub v\(appVersion) · 物理直出独立代理"
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
        popover.delegate = self
        popover.contentViewController = vc

        // 2. 创建无边框全尺寸内容视图主窗口，隐藏系统默认标题栏与红黄绿按钮
        let rect = NSRect(x: 0, y: 0, width: 1360, height: 850)
        window = NSWindow(
            contentRect: rect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "Agent Proxy Hub v\(appVersion)"
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

        waitForBackendReadyAndLoad(attempt: 0)
        window.makeKeyAndOrderFront(nil)
    }

    func waitForBackendReadyAndLoad(attempt: Int) {
        if attempt >= 60 {
            loadHostedViews()
            return
        }

        let healthStr = "\(apiBaseUrl)/api/health"
        guard let healthUrl = URL(string: healthStr) else {
            loadHostedViews()
            return
        }

        probeSession.dataTask(with: healthUrl) { [weak self] _, resp, err in
            guard let self = self else { return }
            if let httpResp = resp as? HTTPURLResponse, err == nil, (200..<300).contains(httpResp.statusCode) {
                DispatchQueue.main.async {
                    self.loadHostedViews()
                }
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.06) {
                    self.waitForBackendReadyAndLoad(attempt: attempt + 1)
                }
            }
        }.resume()
    }

    func loadHostedViews() {
        if let url = URL(string: "\(apiBaseUrl)/?v=\(appVersion)") {
            webView.load(URLRequest(url: url))
        }
        if let trayUrl = URL(string: "\(apiBaseUrl)/?tray=1&v=\(appVersion)") {
            popoverWebView.load(URLRequest(url: trayUrl))
        }
    }

    @objc func togglePopover(_ sender: AnyObject?) {
        guard let button = statusItem.button else { return }
        if popover.isShown {
            popover.performClose(sender)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    func popoverDidShow(_ notification: Notification) {
        popoverWebView.evaluateJavaScript("window.postMessage({type:'tray:shown'}, '*'); window.dispatchEvent(new Event('focus'));", completionHandler: nil)
    }

    func popoverDidClose(_ notification: Notification) {
        popoverWebView.evaluateJavaScript("window.postMessage({type:'tray:hidden'}, '*');", completionHandler: nil)
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
