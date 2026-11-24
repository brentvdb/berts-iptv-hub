package com.berts.iptvhub

import android.content.Context
import android.os.Build

/** Settings stored on the TV box. */
class HubConfig(context: Context) {
    private val prefs = context.getSharedPreferences("berts-iptv-hub", Context.MODE_PRIVATE)

    var baseUrl: String
        get() = prefs.getString("baseUrl", "") ?: ""
        set(value) = prefs.edit().putString("baseUrl", value.trim().trimEnd('/')).apply()

    var username: String
        get() = prefs.getString("username", "home") ?: "home"
        set(value) = prefs.edit().putString("username", value.trim()).apply()

    var password: String
        get() = prefs.getString("password", "") ?: ""
        set(value) = prefs.edit().putString("password", value).apply()

    var deviceName: String
        get() = prefs.getString("deviceName", null) ?: defaultDeviceName()
        set(value) = prefs.edit().putString("deviceName", value.trim()).apply()

    /**
     * Which player to use: auto (ExoPlayer, switching to the built-in VLC engine for 4K/UHD channels or any
     * channel ExoPlayer can't decode), exo, vlc (built-in VLC engine for everything) or external (VLC app).
     */
    var engine: String
        get() = prefs.getString("engine", null)
            ?: if (prefs.getBoolean("useVlc", false)) ENGINE_EXTERNAL else ENGINE_AUTO
        set(value) = prefs.edit().putString("engine", value).apply()

    /** Channels that ExoPlayer couldn't play on this box; auto mode opens them with the VLC engine straight away. */
    val vlcChannels: Set<Int>
        get() = prefs.getStringSet("vlcChannels", emptySet()).orEmpty().mapNotNull { it.toIntOrNull() }.toSet()

    fun rememberNeedsVlc(streamId: Int) {
        val set = prefs.getStringSet("vlcChannels", emptySet()).orEmpty().toMutableSet()
        if (set.add(streamId.toString())) prefs.edit().putStringSet("vlcChannels", set).apply()
    }

    fun forgetVlcChannels() = prefs.edit().remove("vlcChannels").apply()

    var lastCategory: String
        get() = prefs.getString("lastCategory", MainActivity.CATEGORY_ALL) ?: MainActivity.CATEGORY_ALL
        set(value) = prefs.edit().putString("lastCategory", value).apply()

    var lastChannelId: Int
        get() = prefs.getInt("lastChannelId", -1)
        set(value) = prefs.edit().putInt("lastChannelId", value).apply()

    var hubVersion: String
        get() = prefs.getString("hubVersion", "") ?: ""
        set(value) = prefs.edit().putString("hubVersion", value).apply()

    val isConfigured: Boolean get() = baseUrl.isNotBlank() && password.isNotBlank()

    companion object {
        const val ENGINE_AUTO = "auto"
        const val ENGINE_EXO = "exo"
        const val ENGINE_VLC = "vlc"
        const val ENGINE_EXTERNAL = "external"
    }

    private fun defaultDeviceName(): String {
        val model = Build.MODEL?.trim().orEmpty()
        return if (model.isEmpty()) "Android TV" else model
    }
}
