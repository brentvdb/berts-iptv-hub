package com.berts.iptvhub

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** First run (and "Change hub" in Settings): find the hub and enter the TV password. */
class SetupActivity : Activity() {

    private lateinit var foundContainer: LinearLayout
    private lateinit var scanStatus: TextView
    private lateinit var address: EditText
    private lateinit var username: EditText
    private lateinit var password: EditText
    private lateinit var deviceName: EditText
    private lateinit var status: TextView
    @Volatile private var scanning = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(build())
        val cfg = hub.config
        address.setText(cfg.baseUrl)
        username.setText(cfg.username)
        password.setText(cfg.password)
        deviceName.setText(cfg.deviceName)
        scan()
    }

    private fun build(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Neon.BG)
            setPadding(dp(48), dp(28), dp(48), dp(48))
        }

        val header = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply { setImageResource(R.drawable.ic_launcher) }, LinearLayout.LayoutParams(dp(64), dp(64)))
        header.addView(glow(label(Neon.APP_NAME, 30f, Neon.CYAN, bold = true), Neon.CYAN), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(16) })
        header.addView(label(appVersion(), 14f, Neon.MUTED))
        root.addView(header)

        root.addView(label(
            "Connect this TV to Bert's IPTV Hub on your server PC. The hub logs into your IPTV account once and shares it with every TV.",
            16f, Neon.MUTED
        ), params(top = 14))

        // --- found on the network
        root.addView(sectionTitle("Hubs on your network"), params(top = 26))
        scanStatus = label("Searching…", 15f, Neon.MUTED)
        root.addView(scanStatus, params(top = 6))
        foundContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(foundContainer, params(top = 6))
        root.addView(neonButton("Search again", Neon.VIOLET) { scan() }, wrap(top = 10))

        // --- manual / credentials
        root.addView(sectionTitle("Connection"), params(top = 28))
        address = field("Hub address, e.g. 192.168.1.20:8090", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        username = field("Username (normally: home)", InputType.TYPE_CLASS_TEXT)
        password = field("TV password (set in the hub installer)", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        deviceName = field("Name for this TV, e.g. Lounge TV", InputType.TYPE_CLASS_TEXT)
        root.addView(caption("Hub address"), params(top = 8)); root.addView(address, params(top = 4))
        root.addView(caption("Username"), params(top = 12)); root.addView(username, params(top = 4))
        root.addView(caption("TV password"), params(top = 12)); root.addView(password, params(top = 4))
        root.addView(caption("This TV's name (shown on the hub status page)"), params(top = 12)); root.addView(deviceName, params(top = 4))

        status = label("", 16f, Neon.MUTED)
        root.addView(neonButton("Connect", Neon.GREEN) { connect() }, wrap(top = 22))
        root.addView(status, params(top = 12))

        return ScrollView(this).apply {
            setBackgroundColor(Neon.BG)
            isFillViewport = true
            addView(root)
        }
    }

    private fun scan() {
        if (scanning) return
        scanning = true
        scanStatus.text = "Searching your network for the hub…"
        scanStatus.setTextColor(Neon.MUTED)
        foundContainer.removeAllViews()
        Thread {
            val found = runCatching { Discovery.scan(hub.api.http) }.getOrDefault(emptyList())
            runOnUiThread {
                scanning = false
                if (isFinishing || isDestroyed) return@runOnUiThread
                if (found.isEmpty()) {
                    scanStatus.text = "No hub found automatically. Check the PC is on and the hub is running, or type its address below."
                    scanStatus.setTextColor(Neon.AMBER)
                } else {
                    scanStatus.text = "Found ${found.size} - select it, then enter the TV password."
                    scanStatus.setTextColor(Neon.GREEN)
                    found.forEach { f ->
                        foundContainer.addView(neonButton("${f.info.name}  ·  ${f.baseUrl.removePrefix("http://")}  ·  ${f.info.display}", Neon.CYAN) {
                            address.setText(f.baseUrl)
                            password.requestFocus()
                        }, wrap(top = 6))
                    }
                    if (address.text.isNullOrBlank()) address.setText(found.first().baseUrl)
                    foundContainer.getChildAt(0)?.requestFocus()
                }
            }
        }.start()
    }

    private fun connect() {
        val url = normalize(address.text.toString())
        if (url.isEmpty()) { showStatus("Enter the hub address.", Neon.RED); return }
        if (password.text.isNullOrEmpty()) { showStatus("Enter the TV password.", Neon.RED); password.requestFocus(); return }

        val cfg = hub.config
        cfg.baseUrl = url
        cfg.username = username.text.toString().ifBlank { "home" }
        cfg.password = password.text.toString()
        cfg.deviceName = deviceName.text.toString().ifBlank { cfg.deviceName }
        showStatus("Connecting to $url…", Neon.MUTED)

        Thread {
            val result = runCatching { hub.api.login() }
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                result.onSuccess { info ->
                    cfg.hubVersion = info.display
                    showStatus("Connected to ${info.name} ${info.display}", Neon.GREEN)
                    hub.sync.restart()
                    hub.library.refresh()
                    startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP))
                    finish()
                }.onFailure { e ->
                    showStatus(e.message ?: "Couldn't connect", Neon.RED)
                }
            }
        }.start()
    }

    private fun normalize(raw: String): String {
        var s = raw.trim().trimEnd('/')
        if (s.isEmpty()) return ""
        if (!s.startsWith("http://", true) && !s.startsWith("https://", true)) s = "http://$s"
        val hostPart = s.substringAfter("://")
        if (!hostPart.contains(':')) s += ":${Discovery.DEFAULT_PORT}"
        return s
    }

    private fun showStatus(text: String, color: Int) {
        status.text = text
        status.setTextColor(color)
    }

    private fun sectionTitle(text: String) = glow(label(text, 20f, Neon.TEXT, bold = true), Neon.MAGENTA, 6)

    private fun caption(text: String) = label(text, 14f, Neon.MUTED)

    private fun field(hint: String, type: Int): EditText = EditText(this).apply {
        this.hint = hint
        inputType = type
        isSingleLine = true
        imeOptions = EditorInfo.IME_ACTION_NEXT
        textSize = 18f
        setTextColor(Neon.TEXT)
        setHintTextColor(Neon.MUTED)
        setPadding(dp(14), dp(10), dp(14), dp(10))
        val ctx = this@SetupActivity
        background = ctx.panel(Neon.PANEL, Neon.LINE, 1, 10)
        setOnFocusChangeListener { v, focused ->
            v.background = if (focused) ctx.panel(Neon.PANEL_FOCUS, Neon.FOCUS, 3, 10) else ctx.panel(Neon.PANEL, Neon.LINE, 1, 10)
        }
    }

    private fun params(top: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
    private fun wrap(top: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
}
