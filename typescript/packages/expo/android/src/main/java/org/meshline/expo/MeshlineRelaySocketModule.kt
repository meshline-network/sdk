package org.meshline.expo

import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import okhttp3.Authenticator
import okhttp3.CookieJar
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.net.URI
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

class MeshlineRelaySocketModule : Module() {
  private class Entry { @Volatile var socket: WebSocket? = null }
  private val sockets = ConcurrentHashMap<Double, Entry>()
  private val client by lazy {
    // A dedicated client: no React Native interceptors, cookie jar or credentials.
    OkHttpClient.Builder()
      .cookieJar(CookieJar.NO_COOKIES).cache(null)
      .authenticator(Authenticator.NONE).proxyAuthenticator(Authenticator.NONE)
      .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false)
      .connectTimeout(60, TimeUnit.SECONDS).readTimeout(0, TimeUnit.MILLISECONDS)
      .build()
  }

  override fun definition() = ModuleDefinition {
    Name("MeshlineRelaySocket")
    Events("socket")
    Function("connect") { id: Double, endpoint: String -> connect(id, endpoint) }
    Function("send") { id: Double, text: String ->
      val socket = sockets[id]?.socket ?: throw IllegalStateException("WebSocket is not open.")
      check(socket.send(text)) { "WebSocket rejected the outgoing message." }
    }
    Function("close") { id: Double, code: Int, reason: String ->
      sockets[id]?.socket?.close(code, reason)
      Unit
    }
    Function("cancel") { id: Double ->
      sockets.remove(id)?.socket?.cancel()
      Unit
    }
    Function("bufferedAmount") { id: Double -> sockets[id]?.socket?.queueSize()?.toDouble() ?: 0.0 }
    OnDestroy {
      val closing = sockets.values.toList(); sockets.clear()
      closing.forEach { it.socket?.cancel() }
      client.dispatcher.executorService.shutdown()
      client.connectionPool.evictAll()
    }
  }

  private fun connect(id: Double, endpoint: String) {
    val uri = URI(endpoint)
    require(uri.scheme == "wss" && uri.host != null && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null) { "Expected a credential-free WSS endpoint." }
    val entry = Entry()
    check(sockets.putIfAbsent(id, entry) == null) { "Duplicate native WebSocket identifier." }
    fun emit(type: String, fields: Map<String, Any> = emptyMap()) {
      if (sockets[id] === entry) sendEvent("socket", mapOf("id" to id, "type" to type) + fields)
    }
    try {
      val socket = client.newWebSocket(Request.Builder().url(endpoint).build(), object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) { emit("open") }
        override fun onMessage(webSocket: WebSocket, text: String) { emit("text", mapOf("text" to text)) }
        override fun onMessage(webSocket: WebSocket, bytes: ByteString) { emit("binary") }
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(code, reason) }
        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
          emit("close", mapOf("code" to code, "reason" to reason)); sockets.remove(id, entry)
        }
        override fun onFailure(webSocket: WebSocket, error: Throwable, response: Response?) {
          emit("error", mapOf("message" to (error.message ?: "Native WebSocket failed.")))
          emit("close", mapOf("code" to 1006, "reason" to "Native WebSocket failed.")); sockets.remove(id, entry)
          response?.close()
        }
      })
      entry.socket = socket
      if (sockets[id] !== entry) socket.cancel()
    } catch (error: Throwable) { sockets.remove(id, entry); throw error }
  }
}
