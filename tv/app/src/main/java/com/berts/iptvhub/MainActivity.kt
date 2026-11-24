package com.berts.iptvhub

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.format.DateFormat
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import coil.load
import java.util.Date

/** Channel browser: categories on the left, channels in the middle, now/next on the right. */
class MainActivity : Activity(), HubSync.Listener, Library.Listener {

    companion object {
        const val CATEGORY_FAVORITES = "__fav"
        const val CATEGORY_ALL = "__all"
        const val VLC_PACKAGE = "org.videolan.vlc"
    }

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var categoryList: RecyclerView
    private lateinit var channelList: RecyclerView
    private lateinit var categoryAdapter: CategoryAdapter
    private lateinit var channelAdapter: ChannelAdapter
    private lateinit var nowPlaying: Button
    private lateinit var connection: TextView
    private lateinit var listTitle: TextView
    private lateinit var emptyText: TextView

    private lateinit var dLogo: ImageView
    private lateinit var dName: TextView
    private lateinit var dNowLabel: TextView
    private lateinit var dNowTitle: TextView
    private lateinit var dProgress: ProgressBar
    private lateinit var dDesc: TextView
    private lateinit var dNextLabel: TextView
    private lateinit var dNextTitle: TextView

    private var currentCategory = CATEGORY_ALL
    private var focusedChannel: Channel? = null
    private var pendingCategory: Runnable? = null
    private var initialFocusDone = false

    /** Join whatever is already playing when the app is opened (not when coming back from our own player). */
    private var autoJoinPending = true
    private var returningFromPlayer = false

    private val ticker = object : Runnable {
        override fun run() {
            hub.library.refreshEpg()
            focusedChannel?.let { showDetails(it) }
            handler.postDelayed(this, 30_000L)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (!hub.config.isConfigured) {
            startActivity(Intent(this, SetupActivity::class.java))
            finish()
            return
        }
        currentCategory = hub.config.lastCategory
        setContentView(build())
        hub.sync.start()
        hub.library.refresh { error -> if (error != null) toast(error) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        autoJoinPending = true
    }

    override fun onRestart() {
        super.onRestart()
        // Coming back from the launcher/home screen counts as "opening the app" again.
        if (!returningFromPlayer) autoJoinPending = true
        returningFromPlayer = false
    }

    override fun onResume() {
        super.onResume()
        hub.sync.addListener(this)
        hub.library.addListener(this)
        onLibraryChanged()
        updateConnection(hub.sync.connected)
        hub.sync.state?.let { onState(it) }
        handler.post(ticker)
    }

    override fun onPause() {
        hub.sync.removeListener(this)
        hub.library.removeListener(this)
        handler.removeCallbacks(ticker)
        super.onPause()
    }

    // ---- layout ------------------------------------------------------------------------------

    private fun build(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Neon.BG)
            setPadding(dp(32), dp(18), dp(32), dp(14))
        }

        // Header
        val header = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply { setImageResource(R.drawable.ic_launcher) }, LinearLayout.LayoutParams(dp(52), dp(52)))
        header.addView(glow(label(Neon.APP_NAME, 26f, Neon.CYAN, bold = true), Neon.CYAN), LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = dp(14) })
        header.addView(View(this), LinearLayout.LayoutParams(0, 1, 1f))
        nowPlaying = neonButton("", Neon.GREEN) { joinNowPlaying() }.apply { visibility = View.GONE }
        header.addView(nowPlaying, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { rightMargin = dp(12) })
        connection = label("", 14f, Neon.MUTED)
        header.addView(connection, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { rightMargin = dp(12) })
        header.addView(neonButton("Settings", Neon.MAGENTA) { startActivity(Intent(this, SettingsActivity::class.java)) })
        root.addView(header)

        // Body: categories | channels | details
        val body = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }

        categoryAdapter = CategoryAdapter(
            onFocus = { item -> scheduleCategory(item.id) },
            onClick = { item ->
                selectCategory(item.id)
                channelList.post { channelList.getChildAt(0)?.requestFocus() }
            },
        )
        categoryList = RecyclerView(this).apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
            adapter = categoryAdapter
            itemAnimator = null
            clipToPadding = false
            setPadding(dp(4), dp(4), dp(4), dp(4))
        }
        body.addView(categoryList, LinearLayout.LayoutParams(dp(250), ViewGroup.LayoutParams.MATCH_PARENT))

        val middle = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        listTitle = glow(label("", 18f, Neon.TEXT, bold = true), Neon.MAGENTA, 6)
        middle.addView(listTitle, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = dp(6); bottomMargin = dp(6) })
        emptyText = label("", 16f, Neon.MUTED).apply { visibility = View.GONE; setPadding(dp(8), dp(12), dp(8), dp(12)) }
        middle.addView(emptyText)
        channelAdapter = ChannelAdapter(
            onFocus = { ch -> focusedChannel = ch; showDetails(ch) },
            onClick = { ch -> play(ch) },
            onToggleFavorite = { ch -> toggleFavorite(ch) },
        )
        channelList = RecyclerView(this).apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
            adapter = channelAdapter
            itemAnimator = null
            clipToPadding = false
            setPadding(dp(4), dp(4), dp(4), dp(4))
        }
        middle.addView(channelList, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        body.addView(middle, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f).apply { leftMargin = dp(14) })

        body.addView(buildDetails(), LinearLayout.LayoutParams(dp(340), ViewGroup.LayoutParams.MATCH_PARENT).apply { leftMargin = dp(14) })
        root.addView(body, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = dp(14) })

        // Footer
        val footer = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        footer.addView(label("OK: watch   ·   Hold OK or ☰ Menu: add/remove favourite   ·   Back in player: stop on all TVs", 13f, Neon.MUTED),
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        footer.addView(label(appVersion(), 13f, Neon.CYAN))
        root.addView(footer, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) })
        return root
    }

    private fun buildDetails(): View {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = panel(Neon.PANEL_DARK, Neon.LINE, 1, 14)
            setPadding(dp(18), dp(18), dp(18), dp(18))
        }
        dLogo = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_START }
        box.addView(dLogo, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(80)))
        dName = glow(label("", 20f, Neon.TEXT, bold = true), Neon.CYAN, 6)
        box.addView(dName, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(10) })

        dNowLabel = label("", 13f, Neon.CYAN, bold = true)
        box.addView(dNowLabel, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(16) })
        dNowTitle = label("", 17f, Neon.TEXT, bold = true)
        box.addView(dNowTitle)
        dProgress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = ColorStateList.valueOf(Neon.CYAN)
            progressBackgroundTintList = ColorStateList.valueOf(Neon.LINE)
        }
        box.addView(dProgress, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(6)).apply { topMargin = dp(6) })
        dDesc = label("", 14f, Neon.MUTED).apply { maxLines = 6; ellipsize = android.text.TextUtils.TruncateAt.END }
        box.addView(dDesc, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(8) })

        dNextLabel = label("", 13f, Neon.MAGENTA, bold = true)
        box.addView(dNextLabel, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(16) })
        dNextTitle = label("", 16f, Neon.TEXT)
        box.addView(dNextTitle)
        return box
    }

    // ---- data --------------------------------------------------------------------------------

    override fun onLibraryChanged() {
        val lib = hub.library
        val items = ArrayList<CategoryItem>()
        items.add(CategoryItem(CATEGORY_FAVORITES, "★  Favourites", lib.favorites.count { lib.byId.containsKey(it) }))
        items.add(CategoryItem(CATEGORY_ALL, "All channels", lib.channels.size))
        val counts = lib.channels.groupingBy { it.categoryId }.eachCount()
        lib.categories.forEach { c -> counts[c.id]?.let { n -> items.add(CategoryItem(c.id, c.name, n)) } }
        if (items.none { it.id == currentCategory }) currentCategory = CATEGORY_ALL

        if (categoryAdapter.items != items) categoryAdapter.submit(items)
        categoryAdapter.selectedId = currentCategory

        channelAdapter.favorites = lib.favorites.toSet()
        channelAdapter.nowNext = lib.nowNext
        channelAdapter.playingId = hub.sync.state?.takeIf { it.isLive }?.streamId
        val list = lib.channelsFor(currentCategory)
        if (channelAdapter.items != list) channelAdapter.submit(list) else channelAdapter.refreshDecorations()
        updateListTitle(list.size)

        if (!initialFocusDone && list.isNotEmpty()) {
            initialFocusDone = true
            val index = list.indexOfFirst { it.id == hub.config.lastChannelId }.coerceAtLeast(0)
            channelList.scrollToPosition(index)
            channelList.post { channelList.findViewHolderForAdapterPosition(index)?.itemView?.requestFocus() }
        }
        focusedChannel?.let { showDetails(it) }
    }

    private fun updateListTitle(count: Int) {
        val lib = hub.library
        val name = categoryAdapter.items.firstOrNull { it.id == currentCategory }?.name ?: "All channels"
        listTitle.text = "$name  ·  $count"
        emptyText.visibility = if (count == 0) View.VISIBLE else View.GONE
        emptyText.text = when {
            lib.loading && lib.channels.isEmpty() -> "Loading channels from the hub…"
            lib.channels.isEmpty() -> "No channels yet. ${lib.lastError ?: "Check the hub is running."}"
            currentCategory == CATEGORY_FAVORITES -> "No favourites yet. Hold OK (or press ☰ Menu) on a channel to add it - it'll show on every TV."
            else -> "No channels in this category."
        }
    }

    private fun scheduleCategory(id: String) {
        pendingCategory?.let { handler.removeCallbacks(it) }
        val r = Runnable { selectCategory(id) }
        pendingCategory = r
        handler.postDelayed(r, 250)
    }

    private fun selectCategory(id: String) {
        pendingCategory?.let { handler.removeCallbacks(it) }
        if (id == currentCategory) return
        currentCategory = id
        hub.config.lastCategory = id
        categoryAdapter.selectedId = id
        val list = hub.library.channelsFor(id)
        channelAdapter.submit(list)
        channelList.scrollToPosition(0)
        updateListTitle(list.size)
    }

    private fun showDetails(ch: Channel) {
        dName.text = ch.name
        dLogo.load(ch.icon) { crossfade(true) }
        val nn = hub.library.nowNext[ch.id]
        val now = nn?.now
        val next = nn?.next
        val t = System.currentTimeMillis()
        if (now != null) {
            dNowLabel.text = "NOW  ·  ${time(now.start)} – ${time(now.end)}"
            dNowTitle.text = now.title
            dProgress.progress = now.progress(t)
            dProgress.visibility = View.VISIBLE
            dDesc.text = now.desc ?: ""
        } else {
            dNowLabel.text = "NOW"
            dNowTitle.text = if (ch.epgId == null) "No guide for this channel" else "No guide information right now"
            dProgress.visibility = View.GONE
            dDesc.text = ""
        }
        if (next != null) {
            dNextLabel.text = "NEXT  ·  ${time(next.start)}"
            dNextTitle.text = next.title
        } else {
            dNextLabel.text = ""
            dNextTitle.text = ""
        }
    }

    private fun time(ms: Long): String = DateFormat.getTimeFormat(this).format(Date(ms))

    // ---- playback ----------------------------------------------------------------------------

    private fun play(ch: Channel) {
        hub.config.lastChannelId = ch.id
        PlayerActivity.zapList = channelAdapter.items.map { it.id }
        openPlayer(ch.id, join = false)
    }

    private fun joinNowPlaying() {
        val s = hub.sync.state ?: return
        if (!s.isLive) return
        if (PlayerActivity.zapList.isEmpty()) PlayerActivity.zapList = channelAdapter.items.map { it.id }
        openPlayer(s.streamId!!, join = true)
    }

    private fun openPlayer(streamId: Int, join: Boolean) {
        autoJoinPending = false
        returningFromPlayer = true // VLC or ours: coming back must not count as "opening the app"
        if (hub.config.engine == HubConfig.ENGINE_EXTERNAL) {
            val name = hub.library.byId[streamId]?.name ?: "Live TV"
            val intent = Intent(Intent.ACTION_VIEW)
                .setDataAndType(Uri.parse(hub.api.streamUrl(streamId)), "video/*")
                .setPackage(VLC_PACKAGE)
                .putExtra("title", name)
            try {
                startActivity(intent)
                return
            } catch (_: ActivityNotFoundException) {
                toast("VLC isn't installed - using the built-in player")
            }
        }
        startActivity(
            Intent(this, PlayerActivity::class.java)
                .putExtra(PlayerActivity.EXTRA_STREAM_ID, streamId)
                .putExtra(PlayerActivity.EXTRA_JOIN, join)
        )
    }

    private fun toggleFavorite(ch: Channel) {
        val adding = !hub.library.isFavorite(ch.id)
        hub.library.toggleFavorite(ch.id) { err -> toast(err) }
        toast(if (adding) "★ Added ${ch.name} to favourites (all TVs)" else "Removed ${ch.name} from favourites")
    }

    // ---- sync --------------------------------------------------------------------------------

    override fun onState(state: HubState) {
        when {
            state.isLive -> {
                val name = state.name ?: hub.library.byId[state.streamId ?: -1]?.name ?: "Live TV"
                nowPlaying.text = "▶  Watch now: $name" + if (state.viewers > 0) "  ·  ${state.viewers} watching" else ""
                nowPlaying.visibility = View.VISIBLE
                if (autoJoinPending) {
                    autoJoinPending = false
                    joinNowPlaying()
                }
            }
            state.mode == "vod" -> {
                nowPlaying.text = "A ${state.vodKind ?: "movie"} is playing on another TV"
                nowPlaying.visibility = View.VISIBLE
                autoJoinPending = false
            }
            else -> {
                nowPlaying.visibility = View.GONE
                autoJoinPending = false
            }
        }
        channelAdapter.playingId = state.streamId.takeIf { state.isLive }
        channelAdapter.refreshDecorations()
    }

    override fun onConnection(connected: Boolean) = updateConnection(connected)

    private fun updateConnection(connected: Boolean) {
        connection.text = if (connected) "● Hub connected" else "● Connecting to hub…"
        connection.setTextColor(if (connected) Neon.GREEN else Neon.AMBER)
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
}
