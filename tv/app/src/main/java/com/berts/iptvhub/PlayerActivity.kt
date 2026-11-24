package com.berts.iptvhub

import android.app.Activity
import android.content.res.ColorStateList
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.format.DateFormat
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import coil.load
import java.util.Date

/**
 * Full-screen live player that follows the hub: if another TV switches channel this one follows,
 * if any TV stops (Back) every TV leaves the player. Up/Down change channel for everyone.
 */
class PlayerActivity : Activity(), HubSync.Listener {

    companion object {
        const val EXTRA_STREAM_ID = "streamId"
        const val EXTRA_JOIN = "join"

        /** Channel order for Up/Down, set by the browser before opening the player. */
        @JvmStatic
        var zapList: List<Int> = emptyList()

        private const val OVERLAY_MS = 6_000L
        private const val ZAP_DELAY_MS = 650L
        private const val LOCAL_SWITCH_GRACE_MS = 2_500L
        private const val MAX_RETRIES = 8
    }

    private val handler = Handler(Looper.getMainLooper())
    private var player: ExoPlayer? = null
    private lateinit var playerView: PlayerView
    private lateinit var spinner: ProgressBar
    private lateinit var message: TextView
    private lateinit var overlay: LinearLayout
    private lateinit var oLogo: ImageView
    private lateinit var oNum: TextView
    private lateinit var oName: TextView
    private lateinit var oNow: TextView
    private lateinit var oProgress: ProgressBar
    private lateinit var oNext: TextView
    private lateinit var oViewers: TextView

    private var currentId = -1
    private var pendingZapId = -1
    private var localSwitchAt = 0L
    private var retries = 0
    private var stopping = false

    private val hideOverlay = Runnable { overlay.animate().alpha(0f).setDuration(250).withEndAction { overlay.visibility = View.GONE }.start() }
    private val hideMessage = Runnable { message.visibility = View.GONE }
    private val applyZap = Runnable {
        if (pendingZapId >= 0 && pendingZapId != currentId) startStream(pendingZapId, local = true)
        pendingZapId = -1
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(build())
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_STABLE)

        player = ExoPlayer.Builder(this).build().also { p ->
            playerView.player = p
            p.addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(playbackState: Int) {
                    spinner.visibility = if (playbackState == Player.STATE_BUFFERING) View.VISIBLE else View.GONE
                    if (playbackState == Player.STATE_READY) retries = 0
                    if (playbackState == Player.STATE_ENDED) onStreamInterrupted()
                }

                override fun onPlayerError(error: PlaybackException) = onStreamInterrupted()
            })
        }

        val id = intent.getIntExtra(EXTRA_STREAM_ID, -1)
        if (id < 0) { finish(); return }
        startStream(id, local = !intent.getBooleanExtra(EXTRA_JOIN, false))
    }

    override fun onResume() {
        super.onResume()
        hub.sync.addListener(this)
        hub.sync.state?.let { onState(it) }
    }

    override fun onPause() {
        hub.sync.removeListener(this)
        super.onPause()
    }

    override fun onStop() {
        super.onStop()
        // Home button / another app: leave quietly. Other TVs keep watching; the hub closes the
        // provider connection by itself if nobody is left.
        if (!isFinishing) finish()
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        player?.release()
        player = null
        super.onDestroy()
    }

    // ---- layout ------------------------------------------------------------------------------

    private fun build(): View {
        val root = FrameLayout(this).apply { setBackgroundColor(android.graphics.Color.BLACK) }
        playerView = PlayerView(this).apply {
            useController = false
            keepScreenOn = true
            setShutterBackgroundColor(android.graphics.Color.BLACK)
        }
        root.addView(playerView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        spinner = ProgressBar(this).apply {
            isIndeterminate = true
            indeterminateTintList = ColorStateList.valueOf(Neon.CYAN)
            visibility = View.GONE
        }
        root.addView(spinner, FrameLayout.LayoutParams(dp(64), dp(64), Gravity.CENTER))

        message = glow(label("", 18f, Neon.TEXT, bold = true), Neon.MAGENTA, 6).apply {
            background = panel(0xCC0B1626.toInt(), Neon.MAGENTA, 1, 10)
            setPadding(dp(18), dp(10), dp(18), dp(10))
            visibility = View.GONE
        }
        root.addView(message, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = dp(28) })

        overlay = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = panel(0xE6060A16.toInt(), Neon.CYAN, 1, 16)
            setPadding(dp(22), dp(16), dp(22), dp(16))
        }
        oLogo = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_CENTER }
        overlay.addView(oLogo, LinearLayout.LayoutParams(dp(120), dp(72)))
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val titleRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        oNum = label("", 20f, Neon.MUTED, bold = true)
        oName = glow(label("", 24f, Neon.CYAN, bold = true), Neon.CYAN, 8)
        titleRow.addView(oNum)
        titleRow.addView(oName, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = dp(12) })
        texts.addView(titleRow)
        oNow = label("", 17f, Neon.TEXT)
        texts.addView(oNow, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(4) })
        oProgress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = ColorStateList.valueOf(Neon.CYAN)
            progressBackgroundTintList = ColorStateList.valueOf(Neon.LINE)
        }
        texts.addView(oProgress, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(5)).apply { topMargin = dp(6) })
        oNext = label("", 15f, Neon.MUTED)
        texts.addView(oNext, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(6) })
        overlay.addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(18) })
        oViewers = label("", 14f, Neon.GREEN, bold = true).apply { gravity = Gravity.END }
        overlay.addView(oViewers, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = dp(12) })
        root.addView(overlay, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM).apply {
            leftMargin = dp(40); rightMargin = dp(40); bottomMargin = dp(32)
        })
        return root
    }

    // ---- playback ----------------------------------------------------------------------------

    /** local = this TV chose the channel (the hub switches every other TV to it). */
    private fun startStream(id: Int, local: Boolean) {
        currentId = id
        if (local) {
            localSwitchAt = System.currentTimeMillis()
            hub.config.lastChannelId = id
        }
        retries = 0
        val p = player ?: return
        p.setMediaItem(MediaItem.fromUri(hub.api.streamUrl(id)))
        p.prepare()
        p.playWhenReady = true
        showOverlay(id)
    }

    /** The stream ended or failed: work out from the hub's state whether to follow, retry or leave. */
    private fun onStreamInterrupted() {
        if (stopping || isFinishing) return
        handler.postDelayed({
            if (stopping || isFinishing) return@postDelayed
            val s = hub.sync.state
            when {
                s != null && s.isLive && s.streamId != currentId -> startStream(s.streamId!!, local = false)
                s != null && s.isIdle && hub.sync.connected -> leave(idleMessage(s))
                retries < MAX_RETRIES -> {
                    retries++
                    spinner.visibility = View.VISIBLE
                    player?.let { it.prepare(); it.playWhenReady = true }
                }
                else -> leave("Lost the stream from the hub")
            }
        }, 1_500L)
    }

    private fun zap(direction: Int) {
        val list = zapList.ifEmpty { hub.library.channels.map { it.id } }
        if (list.isEmpty()) return
        val from = if (pendingZapId >= 0) pendingZapId else currentId
        val index = list.indexOf(from)
        val nextIndex = if (index < 0) 0 else (index + direction + list.size) % list.size
        pendingZapId = list[nextIndex]
        showOverlay(pendingZapId)
        handler.removeCallbacks(applyZap)
        handler.postDelayed(applyZap, ZAP_DELAY_MS)
    }

    /** Back: stop playback on every TV and close the provider connection. */
    private fun stopEverywhere() {
        stopping = true
        if (!hub.sync.stop()) Thread { runCatching { hub.api.stop() } }.start()
        player?.stop()
        finish()
    }

    private fun leave(text: String) {
        if (stopping) return
        stopping = true
        Toast.makeText(this, text, Toast.LENGTH_LONG).show()
        player?.stop()
        finish()
    }

    // ---- sync --------------------------------------------------------------------------------

    override fun onState(state: HubState) {
        if (stopping) return
        val inGrace = System.currentTimeMillis() - localSwitchAt < LOCAL_SWITCH_GRACE_MS
        when {
            state.isLive && state.streamId == currentId -> updateViewers(state)
            state.isLive -> if (!inGrace) {
                showMessage("Another TV switched to ${state.name ?: "a new channel"}")
                startStream(state.streamId!!, local = false)
            }
            state.isIdle -> if (!inGrace) leave(idleMessage(state))
            state.mode == "vod" -> leave("A ${state.vodKind ?: "movie"} was started on another TV")
        }
    }

    private fun idleMessage(s: HubState): String = when {
        s.reason?.startsWith("stopped by") == true -> "Playback was ${s.reason}"
        s.reason == "upstream_failed" -> "The IPTV provider stopped sending this channel"
        else -> "Playback stopped"
    }

    private fun updateViewers(state: HubState) {
        oViewers.text = if (state.viewers > 1) "${state.viewers} TVs watching" else ""
    }

    // ---- overlay -----------------------------------------------------------------------------

    private fun showOverlay(id: Int) {
        val ch = hub.library.byId[id]
        oNum.text = ch?.num?.toString() ?: ""
        oName.text = ch?.name ?: hub.sync.state?.takeIf { it.streamId == id }?.name ?: "Channel $id"
        oLogo.load(ch?.icon) { crossfade(false) }
        val nn = hub.library.nowNext[id]
        val now = nn?.now
        val t = System.currentTimeMillis()
        if (now != null) {
            oNow.text = "${time(now.start)} – ${time(now.end)}   ${now.title}"
            oProgress.progress = now.progress(t)
            oProgress.visibility = View.VISIBLE
        } else {
            oNow.text = "No guide information"
            oProgress.visibility = View.GONE
        }
        oNext.text = nn?.next?.let { "Next  ${time(it.start)}   ${it.title}" } ?: ""
        hub.sync.state?.let { updateViewers(it) }
        hub.library.refreshEpg()

        handler.removeCallbacks(hideOverlay)
        overlay.animate().cancel()
        overlay.alpha = 1f
        overlay.visibility = View.VISIBLE
        handler.postDelayed(hideOverlay, OVERLAY_MS)
    }

    private fun showMessage(text: String) {
        message.text = text
        message.visibility = View.VISIBLE
        handler.removeCallbacks(hideMessage)
        handler.postDelayed(hideMessage, 4_000L)
    }

    private fun time(ms: Long): String = DateFormat.getTimeFormat(this).format(Date(ms))

    // ---- remote ------------------------------------------------------------------------------

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP -> { zap(+1); return true }
            KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN -> { zap(-1); return true }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_INFO,
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT -> {
                if (overlay.visibility == View.VISIBLE && keyCode != KeyEvent.KEYCODE_INFO) hideOverlay.run() else showOverlay(currentId)
                return true
            }
            KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_BOOKMARK -> {
                val id = currentId
                val adding = !hub.library.isFavorite(id)
                hub.library.toggleFavorite(id) { err -> Toast.makeText(this, err, Toast.LENGTH_SHORT).show() }
                showMessage(if (adding) "★ Added to favourites on all TVs" else "Removed from favourites")
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        stopEverywhere()
    }
}
