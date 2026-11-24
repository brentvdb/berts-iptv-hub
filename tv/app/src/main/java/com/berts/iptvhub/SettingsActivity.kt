package com.berts.iptvhub

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.text.format.DateFormat
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.Date

class SettingsActivity : Activity() {

    private lateinit var hubInfo: TextView
    private lateinit var deviceInfo: TextView
    private lateinit var playerButton: Button
    private lateinit var refreshInfo: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(build())
        render()
        // Pick up the hub's current version (it may have been upgraded since setup).
        Thread {
            val info = runCatching { hub.api.login() }.getOrNull()
            runOnUiThread {
                if (info != null && !isDestroyed) {
                    hub.config.hubVersion = info.display
                    render()
                }
            }
        }.start()
    }

    private fun build(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Neon.BG)
            setPadding(dp(48), dp(28), dp(48), dp(48))
        }
        val header = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply { setImageResource(R.drawable.ic_launcher) }, LinearLayout.LayoutParams(dp(56), dp(56)))
        header.addView(glow(label("Settings", 28f, Neon.CYAN, bold = true), Neon.CYAN), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(16) })
        root.addView(header)

        // Hub
        root.addView(section("Hub"), params(top = 24))
        hubInfo = label("", 16f, Neon.MUTED)
        root.addView(hubInfo, params(top = 6))
        root.addView(neonButton("Change hub or TV password", Neon.CYAN) {
            startActivity(Intent(this, SetupActivity::class.java))
        }.also { it.requestFocus() }, wrap(top = 10))

        // This TV
        root.addView(section("This TV"), params(top = 26))
        deviceInfo = label("", 16f, Neon.MUTED)
        root.addView(deviceInfo, params(top = 6))
        root.addView(neonButton("Rename this TV", Neon.VIOLET) { rename() }, wrap(top = 10))

        // Player
        root.addView(section("Player"), params(top = 26))
        root.addView(label(
            "Auto uses the fast built-in player and switches to the built-in VLC engine for 4K/UHD channels, " +
                "or any channel this box can't decode (it remembers them). The VLC app works too, but it can't follow other TVs.",
            15f, Neon.MUTED
        ), params(top = 6))
        playerButton = neonButton("", Neon.MAGENTA) {
            val order = listOf(HubConfig.ENGINE_AUTO, HubConfig.ENGINE_EXO, HubConfig.ENGINE_VLC, HubConfig.ENGINE_EXTERNAL)
            val next = order[(order.indexOf(hub.config.engine) + 1).mod(order.size)]
            hub.config.engine = next
            render()
        }
        root.addView(playerButton, wrap(top = 10))
        root.addView(neonButton("Forget channels that needed VLC", Neon.VIOLET) {
            hub.config.forgetVlcChannels()
            toast("Auto mode will try the built-in player first again")
            render()
        }, wrap(top = 8))

        // Channels
        root.addView(section("Channel list & guide"), params(top = 26))
        refreshInfo = label("", 15f, Neon.MUTED)
        root.addView(refreshInfo, params(top = 6))
        root.addView(neonButton("Refresh now", Neon.GREEN) {
            toast("Refreshing from the hub…")
            hub.library.refresh { err ->
                toast(err ?: "Channel list and guide updated")
                render()
            }
        }, wrap(top = 10))

        // About
        root.addView(section("About"), params(top = 26))
        root.addView(label("Bert's IPTV Hub for Android TV  ${appVersion()}", 16f, Neon.CYAN), params(top = 6))

        return ScrollView(this).apply {
            setBackgroundColor(Neon.BG)
            isFillViewport = true
            addView(root)
        }
    }

    private fun render() {
        val cfg = hub.config
        val connected = if (hub.sync.connected) "connected" else "not connected"
        hubInfo.text = "${cfg.baseUrl}  ·  ${cfg.hubVersion.ifBlank { "version unknown" }}  ·  $connected"
        deviceInfo.text = "Shown on the hub status page as: ${cfg.deviceName}"
        val remembered = cfg.vlcChannels.size
        playerButton.text = "Player: " + when (cfg.engine) {
            HubConfig.ENGINE_EXO -> "Built-in player only"
            HubConfig.ENGINE_VLC -> "Built-in VLC engine for everything"
            HubConfig.ENGINE_EXTERNAL -> "VLC app (no sync with other TVs)"
            else -> "Auto (recommended)" + if (remembered > 0) " · $remembered channel(s) on VLC" else ""
        } + "   (press to change)"
        val lib = hub.library
        refreshInfo.text = buildString {
            append("${lib.channels.size} channels")
            if (lib.lastRefreshMs > 0) append("  ·  last updated ${DateFormat.getTimeFormat(this@SettingsActivity).format(Date(lib.lastRefreshMs))}")
            append(". The hub keeps the list and guide up to date with your provider automatically.")
        }
    }

    private fun rename() {
        val input = EditText(this).apply {
            setText(hub.config.deviceName)
            inputType = InputType.TYPE_CLASS_TEXT
            isSingleLine = true
            setSelection(text.length)
        }
        AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Name for this TV")
            .setView(input)
            .setPositiveButton("Save") { _, _ ->
                val name = input.text.toString().trim()
                if (name.isNotEmpty()) {
                    hub.config.deviceName = name
                    hub.sync.restart() // re-introduce ourselves to the hub with the new name
                    render()
                }
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun section(text: String) = glow(label(text, 20f, Neon.TEXT, bold = true), Neon.MAGENTA, 6)
    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
    private fun params(top: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
    private fun wrap(top: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
}
