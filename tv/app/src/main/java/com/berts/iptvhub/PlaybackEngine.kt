package com.berts.iptvhub

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.View
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Tracks
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer
import org.videolan.libvlc.util.VLCVideoLayout

/** A video engine inside the app: ExoPlayer (Media3) or VLC (libVLC). */
interface PlaybackEngine {
    interface Listener {
        fun onBuffering(buffering: Boolean)
        fun onPlaying()
        /** Stream ended or the connection dropped: the player decides whether to retry, follow or leave. */
        fun onEnded()
        /** This engine can't decode the stream (e.g. a 4K/HEVC format the box's decoder won't accept). */
        fun onUnsupported(reason: String)
        /** The video format is known; hdr = HDR10/HLG content. */
        fun onVideoFormat(hdr: Boolean) {}
    }

    val kind: String
    val view: View
    var listener: Listener?

    /** UHD channel: insist on the box's hardware decoder drawing straight to the screen so HDR reaches the TV. */
    var uhd: Boolean

    /** One line for the player overlay, e.g. "HEVC · 3840×2160 · HDR10". */
    fun info(): String

    fun play(url: String)
    fun stop()
    fun release()

    companion object {
        const val EXO = "exo"
        const val VLC = "vlc"

        fun create(kind: String, context: Context): PlaybackEngine =
            if (kind == VLC) VlcEngine(context) else ExoEngine(context)
    }
}

/** Media3 ExoPlayer: light and fast, but strict about what the box's hardware decoder reports it supports. */
class ExoEngine(context: Context) : PlaybackEngine {
    override val kind = PlaybackEngine.EXO
    override var listener: PlaybackEngine.Listener? = null
    override var uhd: Boolean = false

    private val handler = Handler(Looper.getMainLooper())
    private val playerView = PlayerView(context).apply {
        useController = false
        keepScreenOn = true
        setShutterBackgroundColor(android.graphics.Color.BLACK)
    }
    private val player: ExoPlayer = ExoPlayer.Builder(
        context,
        DefaultRenderersFactory(context).setEnableDecoderFallback(true),
    ).build()

    private var hasVideo = false
    private var firstFrame = false
    private var reportedUnsupported = false

    /** Audio plays but no picture ever appears: treat as unsupported video (common with 4K HEVC on some boxes). */
    private val noPictureCheck = Runnable {
        if (hasVideo && !firstFrame && player.playbackState == Player.STATE_READY) unsupported("no picture from the video decoder")
    }

    override val view: View get() = playerView

    init {
        playerView.player = player
        player.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(playbackState: Int) {
                when (playbackState) {
                    Player.STATE_BUFFERING -> listener?.onBuffering(true)
                    Player.STATE_READY -> {
                        listener?.onBuffering(false)
                        listener?.onPlaying()
                        handler.removeCallbacks(noPictureCheck)
                        if (!firstFrame) handler.postDelayed(noPictureCheck, 8_000L)
                    }
                    Player.STATE_ENDED -> listener?.onEnded()
                    else -> Unit
                }
            }

            override fun onTracksChanged(tracks: Tracks) {
                hasVideo = tracks.groups.any { it.type == C.TRACK_TYPE_VIDEO }
                if (hasVideo && !tracks.isTypeSupported(C.TRACK_TYPE_VIDEO, true)) {
                    unsupported("video format not supported by this box's decoder")
                }
            }

            override fun onRenderedFirstFrame() {
                firstFrame = true
                handler.removeCallbacks(noPictureCheck)
            }

            override fun onVideoSizeChanged(videoSize: androidx.media3.common.VideoSize) {
                if (videoSize.width > 0) listener?.onVideoFormat(isHdr())
            }

            override fun onPlayerError(error: PlaybackException) {
                if (error.errorCode in 4000..4999) unsupported(error.errorCodeName) // decoder / format errors
                else listener?.onEnded() // network: let the player retry / follow the hub
            }
        })
    }

    private fun isHdr(): Boolean {
        val t = player.videoFormat?.colorInfo?.colorTransfer
        return t == C.COLOR_TRANSFER_ST2084 || t == C.COLOR_TRANSFER_HLG
    }

    override fun info(): String {
        val f = player.videoFormat ?: return "Built-in player · waiting for video"
        val codec = codecName(f.sampleMimeType ?: f.codecs)
        val hdr = when (f.colorInfo?.colorTransfer) {
            C.COLOR_TRANSFER_ST2084 -> "HDR10"
            C.COLOR_TRANSFER_HLG -> "HLG"
            else -> "SDR"
        }
        return "Built-in player · $codec · ${f.width}×${f.height} · $hdr"
    }

    private fun unsupported(reason: String) {
        if (reportedUnsupported) return
        reportedUnsupported = true
        handler.removeCallbacks(noPictureCheck)
        listener?.onUnsupported(reason)
    }

    override fun play(url: String) {
        hasVideo = false
        firstFrame = false
        reportedUnsupported = false
        handler.removeCallbacks(noPictureCheck)
        player.setMediaItem(MediaItem.fromUri(url))
        player.prepare()
        player.playWhenReady = true
    }

    override fun stop() {
        handler.removeCallbacks(noPictureCheck)
        player.stop()
    }

    override fun release() {
        handler.removeCallbacks(noPictureCheck)
        listener = null
        player.release()
    }
}

/** libVLC (the engine inside VLC): plays what VLC plays, including 4K/HEVC streams ExoPlayer rejects. */
class VlcEngine(context: Context) : PlaybackEngine {
    override val kind = PlaybackEngine.VLC
    override var listener: PlaybackEngine.Listener? = null
    override var uhd: Boolean = false

    private var currentUrl: String? = null
    private var playedOnce = false
    private var hwFallback = false

    private val layout = VLCVideoLayout(context).apply { keepScreenOn = true }
    private val libVlc = LibVLC(
        context.applicationContext,
        arrayListOf("--network-caching=1500", "--live-caching=1500", "--clock-jitter=0", "--clock-synchro=0", "--no-stats"),
    )
    private val player = MediaPlayer(libVlc)
    private var attached = false
    private var released = false
    private var pendingUrl: String? = null

    override val view: View get() = layout

    init {
        player.setEventListener { event ->
            if (released) return@setEventListener
            when (event.type) {
                MediaPlayer.Event.Buffering -> listener?.onBuffering(event.buffering < 100f)
                MediaPlayer.Event.Playing -> {
                    playedOnce = true
                    listener?.onBuffering(false)
                    listener?.onPlaying()
                }
                MediaPlayer.Event.Vout -> if (event.voutCount > 0) listener?.onVideoFormat(uhd)
                MediaPlayer.Event.EndReached -> listener?.onEnded()
                MediaPlayer.Event.EncounteredError -> {
                    val url = currentUrl
                    if (uhd && !hwFallback && !playedOnce && url != null) {
                        // The box's decoder refused it outright: let VLC decode it itself (HDR may then look flat).
                        hwFallback = true
                        startMedia(url)
                    } else {
                        listener?.onEnded()
                    }
                }
            }
        }
    }

    override fun play(url: String) {
        if (released) return
        if (!layout.isAttachedToWindow) {
            // The video surface must exist before VLC can draw into it: start once the view is on screen.
            pendingUrl = url
            layout.post { pendingUrl?.let { u -> pendingUrl = null; play(u) } }
            return
        }
        if (!attached) {
            // SurfaceView, no subtitle layer: lets the hardware decoder render directly (needed for HDR).
            player.attachViews(layout, null, false, false)
            attached = true
        }
        if (url != currentUrl) {
            currentUrl = url
            playedOnce = false
            hwFallback = false
        }
        startMedia(url)
    }

    private fun startMedia(url: String) {
        val media = Media(libVlc, Uri.parse(url))
        // UHD: force MediaCodec (no silent switch to software decoding, which loses HDR and drops frames at 4K).
        media.setHWDecoderEnabled(true, uhd && !hwFallback)
        media.addOption(":network-caching=1500")
        player.setMedia(media)
        media.release()
        listener?.onBuffering(true)
        player.play()
    }

    override fun info(): String {
        val t = try { player.currentVideoTrack } catch (_: Exception) { null }
        val decoding = when {
            !uhd -> "hardware (auto)"
            hwFallback -> "software (box decoder refused it)"
            else -> "hardware, direct to screen"
        }
        return if (t == null) "VLC engine · $decoding · waiting for video"
        else "VLC engine · ${codecName(t.codec)} · ${t.width}×${t.height} · $decoding"
    }

    override fun stop() {
        player.stop()
    }

    override fun release() {
        if (released) return
        released = true
        listener = null
        player.stop()
        if (attached) player.detachViews()
        player.release()
        libVlc.release()
    }
}

/** "video/hevc" / "hevc" / "h265" → "HEVC", etc. */
internal fun codecName(raw: String?): String {
    val c = raw?.lowercase().orEmpty()
    return when {
        c.contains("hevc") || c.contains("h265") || c.contains("hvc1") || c.contains("hev1") -> "HEVC"
        c.contains("avc") || c.contains("h264") -> "H.264"
        c.contains("mpeg2") || c.contains("mpgv") || c.contains("mp2v") -> "MPEG-2"
        c.contains("av01") || c.contains("av1") -> "AV1"
        c.contains("vp9") -> "VP9"
        c.isBlank() -> "?"
        else -> raw!!.removePrefix("video/").uppercase()
    }
}
