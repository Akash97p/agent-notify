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

// ---- provider marks (embedded vector glyphs) ---------------------------------------------

/// One provider glyph as SVG path data. The Claude starburst is a single path; the OpenAI/Codex
/// knot is one segment repeated at 60-degree steps around its center.
private struct ProviderMark {
    enum Kind { case claude, codex }

    let path: String
    let rotations: Int
    let centerX: Double
    let centerY: Double

    static func mark(_ kind: Kind) -> ProviderMark {
        switch kind {
        case .claude:
            return ProviderMark(path: claudePath, rotations: 1, centerX: 0, centerY: 0)
        case .codex:
            return ProviderMark(path: codexSegmentPath, rotations: 6, centerX: 1203, centerY: 1203)
        }
    }

    // Claude starburst, CC0 path data from simple-icons (see THIRD_PARTY_NOTICES.md).
    static let claudePath = """
    m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z
    """

    // One sixth of the OpenAI knot, from the ChatGPT mark on Wikimedia Commons (see
    // THIRD_PARTY_NOTICES.md). Six copies rotated around (1203, 1203) form the mark.
    static let codexSegmentPath = """
    M1107.3 299.1c-197.999 0-373.9 127.3-435.2 315.3L650 743.5v427.9c0 21.4 11 40.4 29.4 51.4l344.5 198.515V833.3h.1v-27.9L1372.7 604c33.715-19.52 70.44-32.857 108.47-39.828L1447.6 450.3C1361 353.5 1237.1 298.5 1107.3 299.1zm0 117.5-.6.6c79.699 0 156.3 27.5 217.6 78.4-2.5 1.2-7.4 4.3-11 6.1L952.8 709.3c-18.4 10.4-29.4 30-29.4 51.4V1248l-155.1-89.4V755.8c-.1-187.099 151.601-338.9 339-339.2z
    """
}

/// A small SVG path-data parser covering M/L/H/V/C/S/Q/T/A/Z with relative forms and implicit
/// repetition. It exists so the two provider marks can ship as path data in this single file.
private enum SvgPath {
    static func build(_ mark: ProviderMark) -> CGPath {
        let segment = parse(mark.path)
        let combined = CGMutablePath()
        combined.addPath(segment)
        for step in 1..<mark.rotations {
            var transform = CGAffineTransform(translationX: mark.centerX, y: mark.centerY)
            transform = transform.rotated(by: .pi * Double(step) * 2 / Double(mark.rotations))
            transform = transform.translatedBy(x: -mark.centerX, y: -mark.centerY)
            combined.addPath(segment, transform: transform)
        }
        return combined
    }

    /// A template image of the mark, square, sized to `size` points with the mark's bounds fitted.
    /// `alpha` dims the glyph (unconsidered menu rows) and survives template tinting.
    static func icon(_ kind: ProviderMark.Kind, size: CGFloat, alpha: CGFloat) -> NSImage {
        let key = "\(kind)-\(size)-\(alpha)"
        if let cached = cache[key] { return cached }
        let path = build(ProviderMark.mark(kind))
        let bounds = path.boundingBoxOfPath
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
            guard let context = NSGraphicsContext.current?.cgContext, bounds.width > 0, bounds.height > 0 else { return false }
            let scale = min((size - 1) / bounds.width, (size - 1) / bounds.height)
            // SVG y grows downward; the drawing context grows upward, so the mark is mirrored here.
            var transform = CGAffineTransform(a: scale, b: 0, c: 0, d: -scale,
                                              tx: size / 2 - scale * bounds.midX,
                                              ty: size / 2 + scale * bounds.midY)
            guard let scaled = path.copy(using: &transform) else { return false }
            context.setAlpha(alpha)
            context.setFillColor(CGColor(gray: 0, alpha: 1))
            context.addPath(scaled)
            context.fillPath(using: .evenOdd)
            return true
        }
        image.isTemplate = true
        cache[key] = image
        return image
    }

    private static var cache: [String: NSImage] = [:]

    private struct Tokenizer {
        private let chars: [Character]
        private var index = 0

        init(_ data: String) { chars = Array(data) }

        mutating func skipSeparators() {
            while index < chars.count && (chars[index].isWhitespace || chars[index] == ",") { index += 1 }
        }

        func atEnd() -> Bool {
            var copy = self
            copy.skipSeparators()
            return copy.index >= copy.chars.count
        }

        func atLetter() -> Bool {
            var copy = self
            copy.skipSeparators()
            return copy.index < copy.chars.count && copy.chars[copy.index].isLetter
        }

        mutating func command() -> Character? {
            skipSeparators()
            guard index < chars.count, chars[index].isLetter else { return nil }
            let value = chars[index]
            index += 1
            return value
        }

        mutating func number() -> Double? {
            skipSeparators()
            guard index < chars.count else { return nil }
            let start = index
            if chars[index] == "+" || chars[index] == "-" { index += 1 }
            var digits = 0
            while index < chars.count && chars[index].isNumber { index += 1; digits += 1 }
            if index < chars.count && chars[index] == "." {
                index += 1
                while index < chars.count && chars[index].isNumber { index += 1; digits += 1 }
            }
            if digits == 0 { index = start; return nil }
            if index < chars.count && (chars[index] == "e" || chars[index] == "E") {
                let exponentStart = index
                index += 1
                if index < chars.count && (chars[index] == "+" || chars[index] == "-") { index += 1 }
                let before = index
                while index < chars.count && chars[index].isNumber { index += 1 }
                if index == before { index = exponentStart }
            }
            return Double(String(chars[start..<index]))
        }
    }

    private static func parse(_ data: String) -> CGPath {
        var tokenizer = Tokenizer(data)
        let path = CGMutablePath()
        var current = CGPoint.zero
        var subpathStart = CGPoint.zero
        var implicitCommand: Character? = nil
        var lastControl = CGPoint.zero
        var lastWasCubic = false

        func require(_ count: Int) -> [Double]? {
            var values = [Double]()
            values.reserveCapacity(count)
            for _ in 0..<count {
                guard let value = tokenizer.number() else { return nil }
                values.append(value)
            }
            return values
        }

        func point(_ values: [Double], at offset: Int, relative: Bool) -> CGPoint {
            relative
                ? CGPoint(x: current.x + values[offset], y: current.y + values[offset + 1])
                : CGPoint(x: values[offset], y: values[offset + 1])
        }

        func line(to target: CGPoint) {
            path.addLine(to: target)
            current = target
        }

        func cubic(_ control1: CGPoint, _ control2: CGPoint, _ end: CGPoint) {
            path.addCurve(to: end, control1: control1, control2: control2)
            lastControl = control2
            lastWasCubic = true
            current = end
        }

        func quad(_ control: CGPoint, _ end: CGPoint) {
            path.addQuadCurve(to: end, control: control)
            lastControl = control
            lastWasCubic = false
            current = end
        }

        func arc(rx: Double, ry: Double, rotationDegrees: Double, largeArc: Bool, sweep: Bool, end: CGPoint) {
            let start = current
            guard !(start == end), rx > 0, ry > 0 else { line(to: end); return }
            let rotation = rotationDegrees * .pi / 180
            let cosPhi = cos(rotation), sinPhi = sin(rotation)
            let dx = (start.x - end.x) / 2
            let dy = (start.y - end.y) / 2
            let x1p = cosPhi * dx + sinPhi * dy
            let y1p = -sinPhi * dx + cosPhi * dy
            var radiusX = abs(rx)
            var radiusY = abs(ry)
            let lambda = (x1p * x1p) / (radiusX * radiusX) + (y1p * y1p) / (radiusY * radiusY)
            if lambda > 1 {
                let factor = sqrt(lambda)
                radiusX *= factor
                radiusY *= factor
            }
            let numerator = max(0, radiusX * radiusX * radiusY * radiusY - radiusX * radiusX * y1p * y1p - radiusY * radiusY * x1p * x1p)
            var root = sqrt(numerator / (radiusX * radiusX * y1p * y1p + radiusY * radiusY * x1p * x1p))
            if largeArc == sweep { root = -root }
            let cxp = root * (radiusX * y1p) / radiusY
            let cyp = -root * (radiusY * x1p) / radiusX
            let cx = cosPhi * cxp - sinPhi * cyp + (start.x + end.x) / 2
            let cy = sinPhi * cxp + cosPhi * cyp + (start.y + end.y) / 2
            func angle(_ ux: Double, _ uy: Double, _ vx: Double, _ vy: Double) -> Double {
                atan2(ux * vy - uy * vx, ux * vx + uy * vy)
            }
            let theta1 = angle(1, 0, (x1p - cxp) / radiusX, (y1p - cyp) / radiusY)
            var delta = angle((x1p - cxp) / radiusX, (y1p - cyp) / radiusY,
                              (-x1p - cxp) / radiusX, (-y1p - cyp) / radiusY)
            if !sweep && delta > 0 { delta -= 2 * .pi }
            if sweep && delta < 0 { delta += 2 * .pi }
            let segments = max(4, Int(ceil(abs(delta) / (2 * .pi) * 24)))
            for step in 1...segments {
                let theta = theta1 + delta * Double(step) / Double(segments)
                let xr = radiusX * cos(theta)
                let yr = radiusY * sin(theta)
                line(to: CGPoint(x: cosPhi * xr - sinPhi * yr + cx, y: sinPhi * xr + cosPhi * yr + cy))
            }
            line(to: end)
        }

        while true {
            guard !tokenizer.atEnd() else { break }
            var command: Character
            if tokenizer.atLetter() {
                guard let letter = tokenizer.command() else { break }
                command = letter
            } else if let implicit = implicitCommand {
                command = implicit
            } else {
                break // a number with no command: malformed
            }
            let relative = command.isLowercase
            let upper = Character(command.uppercased())
            implicitCommand = upper == "M" ? (relative ? "l" : "L") : command

            switch upper {
            case "M":
                guard let values = require(2) else { return path }
                let target = point(values, at: 0, relative: relative)
                path.move(to: target)
                subpathStart = target
                current = target
            case "L":
                guard let values = require(2) else { return path }
                line(to: point(values, at: 0, relative: relative))
            case "H":
                guard let values = require(1) else { return path }
                line(to: CGPoint(x: relative ? current.x + values[0] : values[0], y: current.y))
            case "V":
                guard let values = require(1) else { return path }
                line(to: CGPoint(x: current.x, y: relative ? current.y + values[0] : values[0]))
            case "C":
                guard let values = require(6) else { return path }
                cubic(point(values, at: 0, relative: relative),
                      point(values, at: 2, relative: relative),
                      point(values, at: 4, relative: relative))
            case "S":
                guard let values = require(4) else { return path }
                let control1 = lastWasCubic
                    ? CGPoint(x: 2 * current.x - lastControl.x, y: 2 * current.y - lastControl.y)
                    : current
                cubic(control1, point(values, at: 0, relative: relative), point(values, at: 2, relative: relative))
            case "Q":
                guard let values = require(4) else { return path }
                quad(point(values, at: 0, relative: relative), point(values, at: 2, relative: relative))
            case "T":
                guard let values = require(2) else { return path }
                let control = lastWasCubic
                    ? current
                    : CGPoint(x: 2 * current.x - lastControl.x, y: 2 * current.y - lastControl.y)
                quad(control, point(values, at: 0, relative: relative))
            case "A":
                guard let values = require(7) else { return path }
                arc(rx: values[0], ry: values[1], rotationDegrees: values[2],
                    largeArc: values[3] != 0, sweep: values[4] != 0,
                    end: point(values, at: 5, relative: relative))
            case "Z":
                path.closeSubpath()
                current = subpathStart
            default:
                return path
            }
        }
        return path
    }
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

private struct AccountBalance {
    let account: QuotaAccount
    let remainingPercent: Double?
}

private final class MenuBarApp: NSObject, NSApplicationDelegate {
    private let port: Int
    private let baseURL: URL
    private var statusItems: [NSStatusItem] = []
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
        setStatusItemCount(1)
        configurePlaceholder(statusItems[0], toolTip: "AgentNotify five-hour quota")
        statusItems[0].menu = loadingMenu("Checking Codex and Claude quotas…")
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
        let balances = selectedBalances(projection)
        setStatusItemCount(max(1, balances.count))
        if balances.isEmpty {
            configurePlaceholder(statusItems[0], toolTip: "No selected Codex or Claude account is available")
        } else {
            for (item, balance) in zip(statusItems, balances) {
                configure(item, balance: balance)
            }
        }

        for item in statusItems { item.menu = menu(for: projection) }
    }

    private func selectedBalances(_ projection: MenuBarProjection) -> [AccountBalance] {
        let selected = Set(projection.settings.accountIds)
        return projection.accounts
            .filter { selected.isEmpty || selected.contains($0.accountId) }
            .map { account in
                AccountBalance(
                    account: account,
                    remainingPercent: account.windows
                        .filter { $0.durationMinutes == 300 }
                        .map(\.remainingPercent)
                        .min())
            }
    }

    private func setStatusItemCount(_ wanted: Int) {
        let count = max(1, wanted)
        while statusItems.count < count {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
            item.button?.font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .medium)
            item.button?.imageScaling = .scaleProportionallyDown
            statusItems.append(item)
        }
        while statusItems.count > count {
            NSStatusBar.system.removeStatusItem(statusItems.removeLast())
        }
    }

    private func configure(_ item: NSStatusItem, balance: AccountBalance) {
        guard let button = item.button else { return }
        let account = balance.account
        let provider = account.provider == "claude_code" ? "Claude" : "Codex"
        button.image = SvgPath.icon(account.provider == "claude_code" ? .claude : .codex, size: 18, alpha: 1)
        if let remaining = balance.remainingPercent {
            let percent = wholePercent(remaining)
            button.title = "\(percent)%"
            button.contentTintColor = tint(for: remaining)
            let stale = account.status == "stale" ? " (stale)" : ""
            button.toolTip = "\(provider) · \(account.accountLabel): \(percent)% of the five-hour quota remaining\(stale)"
        } else {
            button.title = "--%"
            button.contentTintColor = .secondaryLabelColor
            button.toolTip = "\(provider) · \(account.accountLabel): \(account.message ?? humanized(account.status))"
        }
    }

    private func configurePlaceholder(_ item: NSStatusItem, toolTip: String) {
        item.button?.image = nil
        item.button?.title = "--%"
        item.button?.contentTintColor = .secondaryLabelColor
        item.button?.toolTip = toolTip
    }

    private func menu(for projection: MenuBarProjection) -> NSMenu {
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
        return menu
    }

    private func accountItem(_ account: QuotaAccount, headlineAccountId: String?, selectedIds: [String]) -> NSMenuItem {
        let considered = selectedIds.isEmpty || selectedIds.contains(account.accountId)
        let provider = account.provider == "claude_code" ? "Claude" : "Codex"
        let fiveHour = account.windows.filter { $0.durationMinutes == 300 }.map(\.remainingPercent).min()
        let balance = fiveHour.map { " · \(wholePercent($0))%" } ?? ""
        let dimmed = considered ? "" : " · not in menu bar"
        let parent = NSMenuItem(title: "\(provider) · \(account.accountLabel)\(balance)\(dimmed)", action: nil, keyEquivalent: "")
        parent.isEnabled = true
        parent.image = SvgPath.icon(account.provider == "claude_code" ? .claude : .codex, size: 16, alpha: considered ? 1 : 0.35)

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
        setStatusItemCount(1)
        configurePlaceholder(statusItems[0], toolTip: "AgentNotify broker unavailable")
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
        statusItems[0].menu = menu
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
