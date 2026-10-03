import ExpoModulesCore
import Foundation

public final class MeshlineRelaySocketModule: Module {
  private let lock = NSLock()
  private var sockets: [Double: RelaySocketConnection] = [:]

  public func definition() -> ModuleDefinition {
    Name("MeshlineRelaySocket")
    Events("socket")
    Function("connect") { (id: Double, endpoint: String) in try self.connect(id, endpoint) }
    Function("send") { (id: Double, text: String) in
      guard let socket = self.socket(id) else { throw SocketError("WebSocket is not open.") }
      try socket.send(text)
    }
    Function("close") { (id: Double, code: Int, reason: String) throws -> Void in
      if let socket = self.socket(id) { try socket.close(code, reason) }
    }
    Function("cancel") { (id: Double) -> Void in
      if let socket = self.remove(id) { socket.cancel() }
    }
    Function("bufferedAmount") { (id: Double) -> Double in Double(self.socket(id)?.bufferedAmount ?? 0) }
    OnDestroy {
      self.lock.lock(); let closing = Array(self.sockets.values); self.sockets.removeAll(); self.lock.unlock()
      closing.forEach { $0.cancel() }
    }
  }

  private func socket(_ id: Double) -> RelaySocketConnection? {
    lock.lock(); defer { lock.unlock() }; return sockets[id]
  }
  private func remove(_ id: Double) -> RelaySocketConnection? {
    lock.lock(); defer { lock.unlock() }; return sockets.removeValue(forKey: id)
  }
  private func connect(_ id: Double, _ endpoint: String) throws {
    guard let url = URL(string: endpoint), url.scheme == "wss", url.host != nil,
      url.user == nil, url.password == nil, url.query == nil, url.fragment == nil else {
      throw SocketError("Expected a credential-free WSS endpoint.")
    }
    let connection = RelaySocketConnection(url: url) { [weak self] type, fields in
      guard let self, self.socket(id) != nil else { return }
      self.sendEvent("socket", fields.merging(["id": id, "type": type]) { _, value in value })
      if type == "close" { _ = self.remove(id) }
    }
    lock.lock()
    guard sockets[id] == nil else { lock.unlock(); throw SocketError("Duplicate native WebSocket identifier.") }
    sockets[id] = connection; lock.unlock()
    connection.start()
  }
}

private struct SocketError: LocalizedError {
  let errorDescription: String?
  init(_ message: String) { errorDescription = message }
}

private final class RelaySocketConnection: NSObject, URLSessionWebSocketDelegate, @unchecked Sendable {
  private let lock = NSRecursiveLock()
  private let url: URL
  private let event: (String, [String: Any]) -> Void
  private var session: URLSession?
  private var task: URLSessionWebSocketTask?
  private var ended = false
  private var queued = 0

  init(url: URL, event: @escaping (String, [String: Any]) -> Void) { self.url = url; self.event = event }
  var bufferedAmount: Int { lock.lock(); defer { lock.unlock() }; return queued }

  func start() {
    lock.lock(); defer { lock.unlock() }
    guard !ended else { return }
    // An ephemeral, dedicated session does not share the app's cookies, credentials or cache.
    let configuration = URLSessionConfiguration.ephemeral
    configuration.httpCookieStorage = nil
    configuration.httpShouldSetCookies = false
    configuration.httpCookieAcceptPolicy = .never
    configuration.urlCredentialStorage = nil
    configuration.urlCache = nil
    configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
    configuration.timeoutIntervalForRequest = 60
    let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
    let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
    var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 60)
    request.httpShouldHandleCookies = false
    let task = session.webSocketTask(with: request)
    // The RPC layer enforces 1 MiB UTF-8 text; this also bounds native receive buffering.
    task.maximumMessageSize = 1_048_576
    self.session = session; self.task = task; task.resume()
  }

  func send(_ text: String) throws {
    lock.lock(); defer { lock.unlock() }
    guard !ended, let task else { throw SocketError("WebSocket is not open.") }
    let size = text.utf8.count; queued += size
    task.send(.string(text)) { [weak self] error in
      guard let self else { return }
      self.lock.lock(); self.queued = max(0, self.queued - size); self.lock.unlock()
      if let error { self.fail(error) }
    }
  }
  func close(_ code: Int, _ reason: String) throws {
    guard let closeCode = URLSessionWebSocketTask.CloseCode(rawValue: code) else { throw SocketError("Invalid WebSocket close code.") }
    lock.lock(); defer { lock.unlock() }
    guard !ended else { return }
    task?.cancel(with: closeCode, reason: Data(reason.utf8))
  }
  func cancel() { finish(1006, "Native WebSocket canceled.") }

  private func receive() {
    lock.lock(); defer { lock.unlock() }
    guard !ended, let task else { return }
    task.receive { [weak self] result in
      guard let self else { return }
      self.lock.lock(); let live = !self.ended; self.lock.unlock()
      guard live else { return }
      switch result {
      case .success(let message):
        switch message {
        case .string(let text): self.event("text", ["text": text])
        case .data: self.event("binary", [:])
        @unknown default: self.fail(SocketError("Unknown native WebSocket message kind.")); return
        }
        self.receive()
      case .failure(let error): self.fail(error)
      }
    }
  }
  private func fail(_ error: Error) {
    lock.lock(); defer { lock.unlock() }
    guard !ended else { return }
    event("error", ["message": error.localizedDescription]); finish(1006, "Native WebSocket failed.")
  }
  private func finish(_ code: Int, _ reason: String) {
    lock.lock(); defer { lock.unlock() }
    guard !ended else { return }
    ended = true; queued = 0
    task?.cancel(); session?.invalidateAndCancel(); task = nil; session = nil
    event("close", ["code": code, "reason": reason])
  }

  func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
    lock.lock(); let live = !ended; lock.unlock()
    if live { event("open", [:]); receive() }
  }
  func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
    finish(closeCode.rawValue, reason.flatMap { String(data: $0, encoding: .utf8) } ?? "")
  }
  func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
    if let error { fail(error) } else { finish(1006, "WebSocket ended without a close frame.") }
  }
  func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
    completionHandler(nil)
  }
  func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
    completionHandler(challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust ? .performDefaultHandling : .cancelAuthenticationChallenge, nil)
  }
  func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
    completionHandler(challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust ? .performDefaultHandling : .cancelAuthenticationChallenge, nil)
  }
}
