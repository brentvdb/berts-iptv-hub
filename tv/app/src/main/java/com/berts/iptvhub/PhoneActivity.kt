package com.berts.iptvhub

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView

/**
 * Phone / tablet home screen: search, category chips, channels with what's on now, and a Cast button that
 * sends channels to a TV running the app or to any Chromecast / Google Cast device.
 */
class PhoneActivity : Activity(), HubSync.Listener, Library.Listener {

    private sealed class Target {
        object Phone : Target()
        data class Tv(val id: String, val name: String) : Target()
        data class Chromecast(val name: String) : Target()
    }

    private lateinit var connection: TextView
    private lateinit var castBanner: LinearLayout
    private lateinit var castText: TextView
    private lateinit var nowCard: LinearLayout
    private lateinit var nowText: TextView
    private lateinit var search: EditText
    private lateinit var chips: LinearLayout
    private lateinit var emptyText: TextView
    private lateinit var adapter: ChannelAdapter

    private var category = MainActivity.CATEGORY_ALL
    private var chipKeys: List<String> = emptyList()
    private var target: Target = Target.Phone

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (!hub.config.isConfigured) {
            startActivity(Intent(this, SetupActivity::class.java))
            finish()
            return
        }
        category = hub.config.lastCategory
        setContentView(build())
        hub.sync.start()
        hub.library.refresh { error -> if (error != null) toast(error) }
    }

    override fun onResume() {
        super.onResume()
        hub.sync.addListener(this)
        hub.library.addListener(this)
        if (hub.cast.available) hub.cast.startDiscovery { renderCast() }
        onLibraryChanged()
        onConnection(hub.sync.connected)
        hub.sync.state?.let { onState(it) }
        renderCast()
    }

    override fun onPause() {
        hub.sync.removeListener(this)
        hub.library.removeListener(this)
        if (hub.cast.available) hub.cast.stopDiscovery()
        super.onPause()
    }

    // ---- layout ------------------------------------------------------------------------------

    private fun build(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Neon.BG)
            setPadding(dp(14), dp(14), dp(14), dp(8))
        }

        val header = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply { setImageResource(R.drawable.ic_launcher) }, LinearLayout.LayoutParams(dp(40), dp(40)))
        val titles = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        titles.addView(glow(label(Neon.APP_NAME, 19f, Neon.CYAN, bold = true), Neon.CYAN, 6))
        connection = label("", 12f, Neon.MUTED)
        titles.addView(connection)
        header.addView(titles, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(10) })
        header.addView(neonButton("Cast", Neon.CYAN) { pickTarget() }, wrap(right = 8))
        header.addView(neonButton("⚙", Neon.MAGENTA) { startActivity(Intent(this, SettingsActivity::class.java)) })
        root.addView(header)

        castBanner = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = panel(Neon.PANEL, Neon.CYAN, 2, 12)
            setPadding(dp(12), dp(8), dp(8), dp(8))
            visibility = View.GONE
        }
        castText = label("", 14f, Neon.TEXT)
        castBanner.addView(castText, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        castBanner.addView(neonButton("Stop casting", Neon.MAGENTA) { useTarget(Target.Phone) })
        root.addView(castBanner, full(top = 10))

        nowCard = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = panel(Neon.PANEL_DARK, Neon.GREEN, 1, 12)
            setPadding(dp(12), dp(10), dp(12), dp(10))
            visibility = View.GONE
        }
        nowText = label("", 15f, Neon.TEXT, bold = true)
        nowCard.addView(nowText)
        val nowButtons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        nowButtons.addView(neonButton("▶ Watch here", Neon.GREEN) { joinHere() }, wrap(right = 8))
        nowButtons.addView(neonButton("■ Stop all TVs", Neon.MAGENTA) { stopAll() })
        nowCard.addView(nowButtons, full(top = 8))
        root.addView(nowCard, full(top = 10))

        search = EditText(this).apply {
            hint = "Search channels"
            inputType = InputType.TYPE_CLASS_TEXT
            isSingleLine = true
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            textSize = 16f
            setTextColor(Neon.TEXT)
            setHintTextColor(Neon.MUTED)
            setPadding(dp(12), dp(9), dp(12), dp(9))
            background = this@PhoneActivity.panel(Neon.PANEL, Neon.LINE, 1, 10)
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                override fun afterTextChanged(s: Editable?) = showChannels()
            })
        }
        root.addView(search, full(top = 10))

        chips = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        root.addView(HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            addView(chips)
        }, full(top = 8))

        emptyText = label("", 15f, Neon.MUTED).apply { visibility = View.GONE; setPadding(dp(6), dp(12), dp(6), dp(12)) }
        root.addView(emptyText)

        adapter = ChannelAdapter(
            onFocus = { },
            onClick = { ch -> onChannel(ch) },
            onToggleFavorite = { ch -> toggleFavorite(ch) },
        )
        val list = RecyclerView(this).apply {
            layoutManager = LinearLayoutManager(this@PhoneActivity)
            this.adapter = this@PhoneActivity.adapter
            itemAnimator = null
            clipToPadding = false
            setPadding(0, dp(6), 0, dp(6))
        }
        root.addView(list, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = dp(6) })

        root.addView(label("Tap: watch · Hold: favourite (all TVs) · ${appVersion()}", 11f, Neon.MUTED).apply {
            gravity = Gravity.CENTER
        }, full(top = 4))
        return root
    }

    // ---- data --------------------------------------------------------------------------------

    override fun onLibraryChanged() {
        val lib = hub.library
        val counts = lib.channels.groupingBy { it.categoryId }.eachCount()
        val items = ArrayList<Pair<String, String>>()
        items.add(MainActivity.CATEGORY_FAVORITES to "★ Favourites")
        items.add(MainActivity.CATEGORY_ALL to "All")
        lib.categories.forEach { c -> if (counts.containsKey(c.id)) items.add(c.id to c.name) }
        if (items.none { it.first == category }) category = MainActivity.CATEGORY_ALL

        val keys = items.map { it.first + "|" + it.second }
        if (keys != chipKeys) {
            chipKeys = keys
            chips.removeAllViews()
            items.forEach { (id, name) ->
                val chip = neonButton(name, Neon.VIOLET) { selectCategory(id) }
                chip.tag = id
                chips.addView(chip, wrap(right = 6))
            }
        }
        highlightChips()
        adapter.favorites = lib.favorites.toSet()
        adapter.nowNext = lib.nowNext
        showChannels()
    }

    private fun highlightChips() {
        for (i in 0 until chips.childCount) {
            val chip = chips.getChildAt(i) as Button
            val selected = chip.tag == category
            chip.background = panel(if (selected) Neon.PANEL_FOCUS else Neon.PANEL, if (selected) Neon.CYAN else Neon.LINE, if (selected) 2 else 1, 10)
            chip.setTextColor(if (selected) Neon.CYAN else Neon.TEXT)
        }
    }

    private fun selectCategory(id: String) {
        category = id
        hub.config.lastCategory = id
        highlightChips()
        showChannels()
    }

    private fun showChannels() {
        val q = search.text?.toString()?.trim().orEmpty()
        val lib = hub.library
        val list = if (q.isEmpty()) lib.channelsFor(category) else lib.channels.filter { it.name.contains(q, ignoreCase = true) }
        if (adapter.items != list) adapter.submit(list) else adapter.refreshDecorations()
        emptyText.visibility = if (list.isEmpty()) View.VISIBLE else View.GONE
        emptyText.text = when {
            lib.loading && lib.channels.isEmpty() -> "Loading channels from the hub…"
            lib.channels.isEmpty() -> "No channels yet. ${lib.lastError ?: "Check the hub is running and this phone can reach it."}"
            q.isNotEmpty() -> "No channels match \"$q\"."
            category == MainActivity.CATEGORY_FAVORITES -> "No favourites yet. Hold a channel to add it - it shows on every TV."
            else -> "No channels in this category."
        }
    }

    // ---- actions -----------------------------------------------------------------------------

    private fun onChannel(ch: Channel) {
        hub.config.lastChannelId = ch.id
        when (val t = target) {
            is Target.Phone -> watchHere(ch.id, join = false)
            is Target.Tv ->
                if (hub.sync.cast(t.id, ch.id)) toast("Playing ${ch.name} on ${t.name}")
                else toast("Not connected to the hub right now")
            is Target.Chromecast -> {
                hub.cast.load(hub.api.hlsUrl(ch.id), ch.name, ch.icon)
                toast("Casting ${ch.name} to ${t.name}… (takes a few seconds)")
            }
        }
    }

    private fun watchHere(streamId: Int, join: Boolean) {
        PlayerActivity.zapList = adapter.items.map { it.id }.ifEmpty { hub.library.channels.map { it.id } }
        startActivity(
            Intent(this, PlayerActivity::class.java)
                .putExtra(PlayerActivity.EXTRA_STREAM_ID, streamId)
                .putExtra(PlayerActivity.EXTRA_JOIN, join)
        )
    }

    private fun joinHere() {
        val s = hub.sync.state ?: return
        if (s.isLive) watchHere(s.streamId!!, join = true)
    }

    private fun stopAll() {
        if (!hub.sync.stop()) Thread { runCatching { hub.api.stop() } }.start()
        toast("Stopped on all TVs")
    }

    private fun toggleFavorite(ch: Channel) {
        val adding = !hub.library.isFavorite(ch.id)
        hub.library.toggleFavorite(ch.id) { err -> toast(err) }
        toast(if (adding) "★ ${ch.name} added to favourites (all TVs)" else "${ch.name} removed from favourites")
    }

    // ---- casting -----------------------------------------------------------------------------

    private fun pickTarget() {
        val labels = ArrayList<String>()
        val actions = ArrayList<() -> Unit>()
        labels.add("📱  This phone"); actions.add { useTarget(Target.Phone) }
        hub.sync.tvs().forEach { tv ->
            labels.add("📺  ${tv.name}   ·  Bert's IPTV Hub"); actions.add { useTarget(Target.Tv(tv.id, tv.name)) }
        }
        if (hub.cast.available) {
            hub.cast.routes().forEach { r ->
                labels.add("⧉  ${r.name}   ·  Chromecast"); actions.add {
                    hub.cast.connect(r.id)
                    useTarget(Target.Chromecast(r.name))
                }
            }
        }
        val hint = buildString {
            if (hub.sync.tvs().isEmpty()) append("No TVs with the app open right now. ")
            if (!hub.cast.available) append("Chromecast needs Google Play services. ")
            else if (hub.cast.routes().isEmpty()) append("Looking for Chromecast devices… ")
        }
        AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle(if (hint.isEmpty()) "Play channels on…" else "Play channels on…\n$hint")
            .setItems(labels.toTypedArray()) { _, which -> actions[which]() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun useTarget(t: Target) {
        if (target is Target.Chromecast && t !is Target.Chromecast) hub.cast.disconnect()
        target = t
        renderCast()
        // Something already playing? Send it straight to the new screen.
        val s = hub.sync.state
        if (s != null && s.isLive) {
            val id = s.streamId!!
            when (t) {
                is Target.Tv -> hub.sync.cast(t.id, id)
                is Target.Chromecast -> hub.cast.load(hub.api.hlsUrl(id), s.name ?: "Live TV", s.icon)
                is Target.Phone -> Unit
            }
        }
    }

    private fun renderCast() {
        val t = target
        castBanner.visibility = if (t is Target.Phone) View.GONE else View.VISIBLE
        castText.text = when (t) {
            is Target.Tv -> "📺 Casting to ${t.name} - tap a channel to play it there"
            is Target.Chromecast -> {
                val state = if (hub.cast.connectedName != null) "connected" else "connecting…"
                "⧉ Casting to ${t.name} ($state) - tap a channel to play it there"
            }
            else -> ""
        }
    }

    // ---- sync --------------------------------------------------------------------------------

    override fun onState(state: HubState) {
        when {
            state.isLive -> {
                nowCard.visibility = View.VISIBLE
                nowText.text = "▶ Now playing: ${state.name ?: "Live TV"}" + if (state.viewers > 0) "  ·  ${state.viewers} watching" else ""
            }
            state.mode == "vod" -> {
                nowCard.visibility = View.VISIBLE
                nowText.text = "A ${state.vodKind ?: "movie"} is playing on a TV"
            }
            else -> nowCard.visibility = View.GONE
        }
        adapter.playingId = state.streamId.takeIf { state.isLive }
        adapter.refreshDecorations()
    }

    override fun onConnection(connected: Boolean) {
        connection.text = if (connected) "● Hub connected" else "● Connecting to hub… (on home Wi-Fi or VPN?)"
        connection.setTextColor(if (connected) Neon.GREEN else Neon.AMBER)
    }

    override fun onDevices(devices: List<HubDevice>) {
        val t = target
        if (t is Target.Tv && devices.none { it.id == t.id }) {
            toast("${t.name} closed the app")
            target = Target.Phone
            renderCast()
        }
    }

    override fun onCastResult(ok: Boolean, target: String) {
        if (!ok) toast("Couldn't reach $target - is the app open on it?")
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
    private fun full(top: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) }
    private fun wrap(right: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { rightMargin = dp(right) }
}
