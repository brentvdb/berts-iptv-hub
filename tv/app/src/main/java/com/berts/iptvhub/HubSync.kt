package com.berts.iptvhub

import android.os.Handler
import android.os.Looper
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.TimeUnit

/**
 * Live connection to the hub's /sync WebSocket. Every TV gets the same "now playing" state and
 * favourites; any TV can send play/stop. Reconnects automatically.
 */
class HubSync(private val config: HubConfig, private val api: HubApi) {

    interface Listener {
        fun onState(state: HubState) {}
        fun onFavorites(ids: List<Int>) {}
        fun onConnection(connected: Boolean) {}
    }

    @Volatile var state: HubState? = null
        private set
    @Volatile var connected: Boolean = false
        private set

    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<Listener>()
    private val client = api.http.newBuilder()
        .pingInterval(20, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    private var socket: WebSocket? = null
    private var generation = 0
    private var started = false
    private var backoffMs = 1000L
    private var lastVersion = -1L

    fun addListener(l: Listener) { listeners.add(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    fun start() {
        main.post {
            if (started) return@post
            started = true
            connect()
        }
    }

    /** Reconnect with new settings (e.g. after changing the hub address). */
    fun restart() {
        main.post {
            started = true
            socket?.cancel()
            socket = null
            state = null
            connect()
        }
    }

    fun play(streamId: Int): Boolean = send(JSONObject().put("cmd", "play").put("streamId", streamId))

    fun stop(): Boolean = send(JSONObject().put("cmd", "stop"))

    private fun send(json: JSONObject): Boolean = socket?.send(json.toString()) ?: false

    private fun connect() {
        if (!config.isConfigured) return
        val gen = ++generation
        lastVersion = -1L
        val request = try {
            Request.Builder().url(api.syncUrl()).build()
        } catch (_: Exception) {
            return
        }
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                main.post {
                    if (gen != generation) return@post
                    backoffMs = 1000L
                    connected = true
                    webSocket.send(JSONObject().put("cmd", "hello").put("device", config.deviceName).toString())
                    listeners.forEach { it.onConnection(true) }
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val json = runCatching { JSONObject(text) }.getOrNull() ?: return
                main.post {
                    if (gen != generation) return@post
                    when (json.optString("type")) {
                        "state" -> {
                            val s = parseState(json)
                            if (s.version > lastVersion) {
                                lastVersion = s.version
                                state = s
                                listeners.forEach { it.onState(s) }
                            }
                        }
                        "favorites" -> {
                            val arr = json.optJSONArray("live")
                            val ids = if (arr == null) emptyList() else (0 until arr.length()).map { arr.optInt(it) }
                            listeners.forEach { it.onFavorites(ids) }
                        }
                    }
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = dropped(gen)

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = dropped(gen)
        })
    }

    private fun dropped(gen: Int) {
        main.post {
            if (gen != generation) return@post
            socket = null
            if (connected) {
                connected = false
                listeners.forEach { it.onConnection(false) }
            }
            main.postDelayed({ if (gen == generation && started) connect() }, backoffMs)
            backoffMs = (backoffMs * 2).coerceAtMost(15_000L)
        }
    }

    private fun parseState(o: JSONObject) = HubState(
        mode = o.optString("mode", "idle"),
        streamId = if (o.isNull("streamId")) null else o.optInt("streamId"),
        name = if (o.isNull("name")) null else o.optString("name"),
        icon = if (o.isNull("icon")) null else o.optString("icon").takeIf { it.startsWith("http") },
        vodKind = if (o.isNull("vodKind")) null else o.optString("vodKind"),
        viewers = o.optInt("viewers", 0),
        version = o.optLong("version", 0L),
        reason = if (o.isNull("reason")) null else o.optString("reason"),
    )
}
