import Cocoa
import WebKit

// macOS 原生轻量级菜单栏托盘 + WKWebView 桌面宿主
// 自动拉起同目录下的 AgentProxyHub.Host (.NET 10 arm64/x64 原生内核)
class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var statusItem: NSStatusItem!
    var window: NSWindow!
    var webView: WKWebView!
    var hostProcess: Process?

    func applicationDidFinishLaunching(_ notification: Notification) {
        startBackendHost()

        // 初始化菜单栏图标 (NSStatusItem)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "APH"
        statusItem.button?.toolTip = "Agent Proxy Hub · 物理直出独立代理"

        let menu = NSMenu()
        menu.addItem(NSMenuItem(title: "打开 Agent Proxy Hub 控制台", action: #selector(showWindow), keyEquivalent: "o"))
        menu.addItem(NSMenuItem(title: "访问 GitHub 仓库 (HeyoCode/Agent_Proxy_Hub)", action: #selector(openRepo), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "退出 Agent Proxy Hub", action: #selector(quitAll), keyEquivalent: "q"))
        statusItem.menu = menu

        // 创建 WKWebView 主窗口
        let rect = NSRect(x: 0, y: 0, width: 1340, height: 860)
        window = NSWindow(
            contentRect: rect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Agent Proxy Hub v0.2.0 — HeyoCode/Agent_Proxy_Hub"
        window.center()
        window.delegate = self

        let config = WKWebViewConfiguration()
        webView = WKWebView(frame: rect, configuration: config)
        window.contentView = webView

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) {
            if let url = URL(string: "http://127.0.0.1:17880/") {
                self.webView.load(URLRequest(url: url))
            }
        }
        window.makeKeyAndOrderFront(nil)
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

    @objc func openRepo() {
        if let url = URL(string: "https://github.com/HeyoCode/Agent_Proxy_Hub") {
            NSWorkspace.shared.open(url)
        }
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
