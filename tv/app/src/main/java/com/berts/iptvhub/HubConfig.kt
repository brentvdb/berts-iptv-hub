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

    /** Hand playback to VLC instead of the built-in player. */
    var useVlc: Boolean
        get() = prefs.getBoolean("useVlc", false)
        set(value) = prefs.edit().putBoolean("useVlc", value).apply()

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

    private fun defaultDeviceName(): String {
        val model = Build.MODEL?.trim().orEmpty()
        return if (model.isEmpty()) "Android TV" else model
    }
}
