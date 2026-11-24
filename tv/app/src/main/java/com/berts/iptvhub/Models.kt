package com.berts.iptvhub

data class Category(val id: String, val name: String)

data class Channel(
    val id: Int,
    val num: Int,
    val name: String,
    val icon: String?,
    val categoryId: String?,
    val epgId: String?,
)

data class Programme(val title: String, val desc: String?, val start: Long, val end: Long) {
    fun isOnAt(timeMs: Long) = timeMs in start until end
    fun progress(timeMs: Long): Int =
        if (end <= start) 0 else (((timeMs - start).toDouble() / (end - start)) * 100).toInt().coerceIn(0, 100)
}

data class NowNext(val now: Programme?, val next: Programme?)

data class HubInfo(val name: String, val version: String, val build: Int) {
    val display: String get() = if (build > 0) "v$version (build $build)" else "v$version"
}

/** The hub's single playback slot, as pushed over the sync WebSocket. */
data class HubState(
    val mode: String,          // "idle" | "live" | "vod"
    val streamId: Int?,
    val name: String?,
    val icon: String?,
    val vodKind: String?,
    val viewers: Int,
    val version: Long,
    val reason: String?,
) {
    val isLive: Boolean get() = mode == "live" && streamId != null
    val isIdle: Boolean get() = mode == "idle"
}

/** Another app connected to the hub (from the sync "devices" list). kind: "tv" | "phone" | "unknown". */
data class HubDevice(val id: String, val name: String, val kind: String)
