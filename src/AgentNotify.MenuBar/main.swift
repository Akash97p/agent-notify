import AppKit
import Foundation

private struct MenuBarProjection: Decodable {
    let contractVersion: String
    let checkedAt: String
    let settings: MenuBarSettings
    let headline: MenuBarHeadline?
    let accounts: [QuotaAccount]
}

private struct MenuBarSettings: Decodable {
    let enabled: Bool
    let refreshMinutes: Int
    let accountIds: [String]
}

private struct MenuBarHeadline: Decodable {
    let accountId: String
    let accountLabel: String
    let provider: String
    let windowLabel: String
    let remainingPercent: Double
    let resetsAt: String?
    let stale: Bool
}

private struct QuotaAccount: Decodable {
    let provider: String
    let status: String
    let source: String
    let fetchedAt: String?
    let plan: String?
    let creditBalance: Decimal?
    let windows: [QuotaWindow]
    let message: String?
    let accountId: String
    let accountLabel: String
}

private struct QuotaWindow: Decodable {
    let key: String
    let label: String
    let usedPercent: Double
    let remainingPercent: Double
    let durationMinutes: Int?
    let resetsAt: String?
}

private enum ClientError: LocalizedError {
    case invalidResponse
    case incompatibleContract
    case server(String)

    var errorDescription: String? {
        switch self {
        case .invalidResponse: return "The broker returned an invalid response."
        case .incompatibleContract: return "The broker and menu bar use different quota contracts."
        case .server(let message): return message
        }
    }
}

private final class MenuBarApp: NSObject, NSApplicationDelegate {
    private let port: Int
    private let baseURL: URL
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let session: URLSession
    private let decoder: JSONDecoder
    private var refreshTimer: Timer?
    private var projection: MenuBarProjection?
    private var loading = false

    init(port: Int) {
        self.port = port
        self.baseURL = URL(string: "http://127.0.0.1:\(port)/ui/api/")!
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 300
        configuration.timeoutIntervalForResource = 300
        configuration.connectionProxyDictionary = [:]
        self.session = URLSession(configuration: configuration)
        self.decoder = JSONDecoder()
        self.decoder.keyDecodingStrategy = .convertFromSnakeCase
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let button = statusItem.button else { return }
        button.title = "--%"
        button.font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        button.toolTip = "AgentNotify five-hour quota"
        statusItem.menu = loadingMenu("Checking Codex and Claude quotas…")
        load()
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
        session.invalidateAndCancel()
    }

    @objc private func refreshNow() {
        load(force: true)
    }

    @objc private func openLiveQuota() {
        guard let url = URL(string: "http://127.0.0.1:\(port)/ui/#/quota") else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func disableMenuBar() {
        var request = URLRequest(url: baseURL.appendingPathComponent("menu-bar/disable"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "X-AgentNotify-UI")
        request.httpBody = Data("{}".utf8)
        session.dataTask(with: request) { _, response, _ in
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else { return }
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }.resume()
    }

    @objc private func quitForNow() {
        NSApp.terminate(nil)
    }

    private func load(force: Bool = false) {
        guard !loading else { return }
        loading = true
        refreshTimer?.invalidate()
        let path = force ? "menu-bar/refresh" : "menu-bar"
        var request = URLRequest(url: baseURL.appendingPathComponent(path))
        request.httpMethod = force ? "POST" : "GET"
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if force {
            request.setValue("1", forHTTPHeaderField: "X-AgentNotify-UI")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data("{}".utf8)
        }

        session.dataTask(with: request) { [weak self] data, response, error in
            guard let self else { return }
            let result: Result<MenuBarProjection, Error>
            do {
                if let error { throw error }
                guard let http = response as? HTTPURLResponse, let data else { throw ClientError.invalidResponse }
                guard (200..<300).contains(http.statusCode) else {
                    let body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                    throw ClientError.server(body?["error"] as? String ?? "The broker could not load quota.")
                }
                let decoded = try self.decoder.decode(MenuBarProjection.self, from: data)
                guard decoded.contractVersion == "1" else { throw ClientError.incompatibleContract }
                result = .success(decoded)
            } catch {
                result = .failure(error)
            }
            DispatchQueue.main.async { self.finish(result) }
        }.resume()
    }

    private func finish(_ result: Result<MenuBarProjection, Error>) {
        loading = false
        switch result {
        case .success(let projection):
            guard projection.settings.enabled else {
                NSApp.terminate(nil)
                return
            }
            self.projection = projection
            render(projection)
            schedule(afterMinutes: projection.settings.refreshMinutes)
        case .failure(let error):
            renderError(error.localizedDescription)
            schedule(afterMinutes: 1)
        }
    }

    private func schedule(afterMinutes minutes: Int) {
        refreshTimer?.invalidate()
        refreshTimer = Timer.scheduledTimer(timeInterval: TimeInterval(max(1, minutes) * 60),
                                             target: self,
                                             selector: #selector(timerFired),
                                             userInfo: nil,
                                             repeats: false)
    }

    @objc private func timerFired() {
        load()
    }

    private func render(_ projection: MenuBarProjection) {
        if let headline = projection.headline {
            let remaining = wholePercent(headline.remainingPercent)
            statusItem.button?.title = "\(remaining)%"
            statusItem.button?.contentTintColor = tint(for: headline.remainingPercent)
            statusItem.button?.toolTip = "\(headline.accountLabel): \(remaining)% of the five-hour quota remaining"
        } else {
            statusItem.button?.title = "--%"
            statusItem.button?.contentTintColor = .secondaryLabelColor
            statusItem.button?.toolTip = "No five-hour quota is available"
        }

        let menu = NSMenu()
        menu.autoenablesItems = false
        let title = disabledItem("AgentNotify quota")
        title.attributedTitle = NSAttributedString(
            string: "AgentNotify quota",
            attributes: [.font: NSFont.systemFont(ofSize: 13, weight: .semibold)])
        menu.addItem(title)

        if let headline = projection.headline {
            let stale = headline.stale ? " · stale" : ""
            menu.addItem(disabledItem("Five-hour balance: \(wholePercent(headline.remainingPercent))% · \(headline.accountLabel)\(stale)"))
        } else {
            menu.addItem(disabledItem("No five-hour balance is available"))
        }
        menu.addItem(.separator())

        if projection.accounts.isEmpty {
            menu.addItem(disabledItem("No Codex or Claude accounts are monitored"))
        } else {
            for account in projection.accounts {
                menu.addItem(accountItem(account, headlineAccountId: projection.headline?.accountId,
                                         selectedIds: projection.settings.accountIds))
            }
        }

        menu.addItem(.separator())
        menu.addItem(disabledItem("Checked \(relativeTime(projection.checkedAt))"))
        let refresh = NSMenuItem(title: "Refresh now", action: #selector(refreshNow), keyEquivalent: "r")
        refresh.target = self
        refresh.isEnabled = true
        menu.addItem(refresh)
        let open = NSMenuItem(title: "Open Live Quota…", action: #selector(openLiveQuota), keyEquivalent: "")
        open.target = self
        open.isEnabled = true
        menu.addItem(open)
        menu.addItem(.separator())
        let disable = NSMenuItem(title: "Disable Menu Bar", action: #selector(disableMenuBar), keyEquivalent: "")
        disable.target = self
        disable.isEnabled = true
        menu.addItem(disable)
        let quit = NSMenuItem(title: "Quit for now", action: #selector(quitForNow), keyEquivalent: "q")
        quit.target = self
        quit.isEnabled = true
        menu.addItem(quit)
        statusItem.menu = menu
    }

    private func accountItem(_ account: QuotaAccount, headlineAccountId: String?, selectedIds: [String]) -> NSMenuItem {
        let considered = selectedIds.isEmpty || selectedIds.contains(account.accountId)
        let marker = considered ? "●" : "○"
        let provider = account.provider == "claude_code" ? "Claude" : "Codex"
        let fiveHour = account.windows.filter { $0.durationMinutes == 300 }.map(\.remainingPercent).min()
        let balance = fiveHour.map { " · \(wholePercent($0))%" } ?? ""
        let parent = NSMenuItem(title: "\(marker) \(provider) · \(account.accountLabel)\(balance)", action: nil, keyEquivalent: "")
        parent.isEnabled = true

        let submenu = NSMenu(title: account.accountLabel)
        submenu.autoenablesItems = false
        if account.status != "ok" {
            submenu.addItem(disabledItem(account.status == "stale" ? "Last known value (stale)" : humanized(account.status)))
        }
        if let plan = account.plan, !plan.isEmpty { submenu.addItem(disabledItem("Plan: \(plan)")) }
        if let credit = account.creditBalance { submenu.addItem(disabledItem("Credits: \(credit)")) }
        if account.windows.isEmpty {
            submenu.addItem(disabledItem(account.message ?? "No quota windows are available"))
        } else {
            for window in account.windows {
                let item = disabledItem("\(window.label): \(wholePercent(window.remainingPercent))% remaining")
                if window.durationMinutes == 300 && account.accountId == headlineAccountId {
                    item.state = .on
                }
                submenu.addItem(item)
                if let reset = window.resetsAt {
                    submenu.addItem(disabledItem("    Resets \(absoluteTime(reset))"))
                }
            }
            if let message = account.message, !message.isEmpty {
                submenu.addItem(.separator())
                submenu.addItem(disabledItem(message))
            }
        }
        submenu.addItem(.separator())
        submenu.addItem(disabledItem(account.source))
        parent.submenu = submenu
        return parent
    }

    private func renderError(_ message: String) {
        statusItem.button?.title = "--%"
        statusItem.button?.contentTintColor = .secondaryLabelColor
        statusItem.button?.toolTip = "AgentNotify broker unavailable"
        let menu = loadingMenu(message)
        menu.addItem(.separator())
        let retry = NSMenuItem(title: "Retry", action: #selector(refreshNow), keyEquivalent: "r")
        retry.target = self
        retry.isEnabled = true
        menu.addItem(retry)
        let quit = NSMenuItem(title: "Quit for now", action: #selector(quitForNow), keyEquivalent: "q")
        quit.target = self
        quit.isEnabled = true
        menu.addItem(quit)
        statusItem.menu = menu
    }

    private func loadingMenu(_ message: String) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.addItem(disabledItem("AgentNotify quota"))
        menu.addItem(disabledItem(message))
        return menu
    }

    private func disabledItem(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private func wholePercent(_ value: Double) -> Int {
        Int(min(100, max(0, value)).rounded())
    }

    private func tint(for remaining: Double) -> NSColor {
        if remaining < 20 { return .systemRed }
        if remaining < 40 { return .systemOrange }
        return .labelColor
    }

    private func humanized(_ value: String) -> String {
        value.replacingOccurrences(of: "_", with: " ").capitalized
    }

    private func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: value) { return date }
        let ordinary = ISO8601DateFormatter()
        ordinary.formatOptions = [.withInternetDateTime]
        return ordinary.date(from: value)
    }

    private func relativeTime(_ value: String) -> String {
        guard let date = parseDate(value) else { return "recently" }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter.localizedString(for: date, relativeTo: Date())
    }

    private func absoluteTime(_ value: String) -> String {
        guard let date = parseDate(value) else { return value }
        let formatter = DateFormatter()
        formatter.dateStyle = Calendar.current.isDateInToday(date) ? .none : .medium
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }
}

private func portArgument() -> Int {
    let arguments = CommandLine.arguments
    guard let index = arguments.firstIndex(of: "--port"), index + 1 < arguments.count,
          let port = Int(arguments[index + 1]), (1...65535).contains(port) else {
        return 47821
    }
    return port
}

let application = NSApplication.shared
application.setActivationPolicy(.accessory)
private let delegate = MenuBarApp(port: portArgument())
application.delegate = delegate
application.run()
