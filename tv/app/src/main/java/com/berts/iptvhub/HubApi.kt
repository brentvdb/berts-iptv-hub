package com.berts.iptvhub

import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

class HubException(message: String) : IOException(message)

/** Talks to Bert's IPTV Hub on the server PC. All calls block: run them off the main thread. */
class HubApi(private val config: HubConfig) {

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    // ---- account -----------------------------------------------------------------------------

    /** Checks the address + TV password and that it really is a Bert's IPTV Hub. */
    fun login(): HubInfo {
        val json = JSONObject(get(url("/player_api.php")))
        val userInfo = json.optJSONObject("user_info")
        if (userInfo != null && userInfo.optInt("auth", 1) == 0) throw HubException("Wrong TV password")
        val hub = json.optJSONObject("hub") ?: throw HubException("That address is an IPTV server, but not Bert's IPTV Hub")
        return HubInfo(hub.optString("name", Neon.APP_NAME), hub.optString("version", "?"), hub.optInt("build", 0))
    }

    // ---- catalogue ---------------------------------------------------------------------------

    fun categoriesJson(): String = get(url("/player_api.php", "action" to "get_live_categories"))
    fun channelsJson(): String = get(url("/player_api.php", "action" to "get_live_streams"))

    fun parseCategories(raw: String): List<Category> {
        val arr = runCatching { JSONArray(raw) }.getOrNull() ?: return emptyList()
        return (0 until arr.length()).mapNotNull { i ->
            val o = arr.optJSONObject(i) ?: return@mapNotNull null
            val id = o.optString("category_id").takeIf { it.isNotBlank() } ?: return@mapNotNull null
            Category(id, o.optString("category_name", "Category $id"))
        }
    }

    fun parseChannels(raw: String): List<Channel> {
        val arr = runCatching { JSONArray(raw) }.getOrNull() ?: return emptyList()
        val list = ArrayList<Channel>(arr.length())
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            val id = o.optString("stream_id").toIntOrNull() ?: continue
            list.add(
                Channel(
                    id = id,
                    num = o.optString("num").toIntOrNull() ?: (i + 1),
                    name = o.optString("name", "Channel $id"),
                    icon = o.optString("stream_icon").takeIf { it.startsWith("http") },
                    categoryId = o.optString("category_id").takeIf { it.isNotBlank() && it != "null" },
                    epgId = o.optString("epg_channel_id").takeIf { it.isNotBlank() && it != "null" },
                )
            )
        }
        return list
    }

    // ---- EPG (worked out on the hub) -----------------------------------------------------------

    fun epgNow(): Map<Int, NowNext> {
        val json = JSONObject(get(url("/api/epg/now")))
        val channels = json.optJSONObject("channels") ?: return emptyMap()
        val map = HashMap<Int, NowNext>(channels.length())
        val keys = channels.keys()
        while (keys.hasNext()) {
            val key = keys.next()
            val id = key.toIntOrNull() ?: continue
            val o = channels.optJSONObject(key) ?: continue
            map[id] = NowNext(programme(o.optJSONObject("now")), programme(o.optJSONObject("next")))
        }
        return map
    }

    fun epgFor(streamId: Int, hours: Int = 12): List<Programme> {
        val json = JSONObject(get(url("/api/epg/$streamId", "hours" to hours.toString())))
        val arr = json.optJSONArray("programmes") ?: return emptyList()
        return (0 until arr.length()).mapNotNull { programme(arr.optJSONObject(it)) }
    }

    private fun programme(o: JSONObject?): Programme? {
        if (o == null) return null
        val title = o.optString("title").takeIf { it.isNotBlank() } ?: return null
        val desc = if (o.isNull("desc")) null else o.optString("desc").takeIf { it.isNotBlank() }
        return Programme(title, desc, o.optLong("start"), o.optLong("end"))
    }

    // ---- favourites (shared by every TV) ------------------------------------------------------

    fun favorites(): List<Int> {
        val json = JSONObject(get(url("/api/favorites")))
        val arr = json.optJSONArray("live") ?: return emptyList()
        return (0 until arr.length()).map { arr.optInt(it) }
    }

    fun setFavorite(streamId: Int, favourite: Boolean) {
        val request = Request.Builder().url(url("/api/favorites/live/$streamId"))
        if (favourite) request.post(ByteArray(0).toRequestBody(null)) else request.delete()
        execute(request.build())
    }

    // ---- playback control (HTTP fallback when the sync socket is down) ---------------------

    fun stop() {
        execute(Request.Builder().url(url("/api/stop")).post("".toRequestBody("text/plain".toMediaType())).build())
    }

    fun streamUrl(streamId: Int): String =
        "${config.baseUrl}/live/${enc(config.username)}/${enc(config.password)}/$streamId.ts"

    /** Live HLS for Chromecast / Cast devices (the hub slices the same stream, no transcoding). */
    fun hlsUrl(streamId: Int): String =
        "${config.baseUrl}/hls/${enc(config.username)}/${enc(config.password)}/$streamId/index.m3u8"

    fun syncUrl(): HttpUrl = url("/sync", "device" to config.deviceName)

    // ---- plumbing ----------------------------------------------------------------------------

    fun url(path: String, vararg params: Pair<String, String>): HttpUrl {
        val base = (config.baseUrl + path).toHttpUrlOrNull() ?: throw HubException("The hub address isn't valid")
        val b = base.newBuilder()
            .addQueryParameter("username", config.username)
            .addQueryParameter("password", config.password)
        params.forEach { b.addQueryParameter(it.first, it.second) }
        return b.build()
    }

    private fun get(url: HttpUrl): String = execute(Request.Builder().url(url).build())

    private fun execute(request: Request): String {
        try {
            http.newCall(request).execute().use { r ->
                when {
                    r.code == 401 -> throw HubException("Wrong TV password")
                    r.code == 503 -> throw HubException("The hub isn't set up yet (provider details missing)")
                    r.code == 502 -> throw HubException("The hub can't reach your IPTV provider right now")
                    !r.isSuccessful -> throw HubException("Hub error ${r.code}")
                }
                return r.body?.string().orEmpty()
            }
        } catch (e: HubException) {
            throw e
        } catch (e: IOException) {
            throw HubException("Can't reach the hub at ${config.baseUrl} (${e.message ?: e.javaClass.simpleName})")
        }
    }

    private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    companion object {
        /** Unauthenticated probe used by the network scan. */
        fun probe(client: OkHttpClient, baseUrl: String): HubInfo? {
            val url = "$baseUrl/api/version".toHttpUrlOrNull() ?: return null
            return try {
                client.newCall(Request.Builder().url(url).build()).execute().use { r ->
                    if (!r.isSuccessful) {
                        null
                    } else {
                        val o = JSONObject(r.body?.string().orEmpty())
                        val name = o.optString("name")
                        if (!name.contains("IPTV Hub", ignoreCase = true)) null
                        else HubInfo(name, o.optString("version", "?"), o.optInt("build", 0))
                    }
                }
            } catch (_: Exception) {
                null
            }
        }
    }
}
