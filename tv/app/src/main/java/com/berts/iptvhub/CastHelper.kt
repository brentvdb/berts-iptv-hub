package com.berts.iptvhub

import android.content.Context
import android.net.Uri
import androidx.mediarouter.media.MediaRouteSelector
import androidx.mediarouter.media.MediaRouter
import com.google.android.gms.cast.CastMediaControlIntent
import com.google.android.gms.cast.MediaInfo
import com.google.android.gms.cast.MediaLoadRequestData
import com.google.android.gms.cast.MediaMetadata
import com.google.android.gms.cast.framework.CastContext
import com.google.android.gms.cast.framework.CastOptions
import com.google.android.gms.cast.framework.CastSession
import com.google.android.gms.cast.framework.OptionsProvider
import com.google.android.gms.cast.framework.SessionManagerListener
import com.google.android.gms.cast.framework.SessionProvider
import com.google.android.gms.common.images.WebImage

/** Google Cast setup: the standard Default Media Receiver plays the hub's HLS stream on any Cast device. */
class CastOptionsProvider : OptionsProvider {
    override fun getCastOptions(context: Context): CastOptions =
        CastOptions.Builder()
            .setReceiverApplicationId(CastMediaControlIntent.DEFAULT_MEDIA_RECEIVER_APPLICATION_ID)
            .build()

    override fun getAdditionalSessionProviders(context: Context): List<SessionProvider>? = null
}

/**
 * Finds Chromecast / Google Cast devices and plays a channel on them. Uses plain MediaRouter calls so the
 * neon screens don't need AppCompat's cast button. Main thread only.
 */
class CastHelper(context: Context) {

    data class Route(val id: String, val name: String)

    private val appContext = context.applicationContext
    private val castContext: CastContext? = try {
        @Suppress("DEPRECATION")
        CastContext.getSharedInstance(appContext)
    } catch (_: Exception) {
        null // no Google Play services (some boxes / de-Googled phones)
    }
    private val router: MediaRouter = MediaRouter.getInstance(appContext)
    private val selector: MediaRouteSelector? = castContext?.mergedSelector

    private var onChange: (() -> Unit)? = null
    private var pending: Triple<String, String, String?>? = null

    val available: Boolean get() = castContext != null && selector != null

    /** Name of the Cast device we're connected to, or null. */
    val connectedName: String?
        get() = castContext?.sessionManager?.currentCastSession?.castDevice?.friendlyName

    private val routerCallback = object : MediaRouter.Callback() {
        override fun onRouteAdded(router: MediaRouter, route: MediaRouter.RouteInfo) { onChange?.invoke() }
        override fun onRouteRemoved(router: MediaRouter, route: MediaRouter.RouteInfo) { onChange?.invoke() }
        override fun onRouteChanged(router: MediaRouter, route: MediaRouter.RouteInfo) { onChange?.invoke() }
    }

    private val sessionListener = object : SessionManagerListener<CastSession> {
        override fun onSessionStarting(session: CastSession) {}
        override fun onSessionStarted(session: CastSession, sessionId: String) {
            pending?.let { (url, title, icon) -> pending = null; load(url, title, icon) }
            onChange?.invoke()
        }
        override fun onSessionStartFailed(session: CastSession, error: Int) { pending = null; onChange?.invoke() }
        override fun onSessionEnding(session: CastSession) {}
        override fun onSessionEnded(session: CastSession, error: Int) { onChange?.invoke() }
        override fun onSessionResuming(session: CastSession, sessionId: String) {}
        override fun onSessionResumed(session: CastSession, wasSuspended: Boolean) { onChange?.invoke() }
        override fun onSessionResumeFailed(session: CastSession, error: Int) {}
        override fun onSessionSuspended(session: CastSession, reason: Int) {}
    }

    init {
        castContext?.sessionManager?.addSessionManagerListener(sessionListener, CastSession::class.java)
    }

    fun startDiscovery(changed: () -> Unit) {
        onChange = changed
        selector?.let { router.addCallback(it, routerCallback, MediaRouter.CALLBACK_FLAG_REQUEST_DISCOVERY) }
    }

    fun stopDiscovery() {
        router.removeCallback(routerCallback)
        onChange = null
    }

    fun routes(): List<Route> {
        val sel = selector ?: return emptyList()
        return router.routes
            .filter { !it.isDefault && it.isEnabled && it.matchesSelector(sel) }
            .map { Route(it.id, it.name) }
    }

    fun connect(routeId: String) {
        router.routes.firstOrNull { it.id == routeId }?.select()
    }

    fun disconnect() {
        pending = null
        castContext?.sessionManager?.endCurrentSession(true)
    }

    /** Play a live HLS URL on the connected Cast device (queued until the connection is ready). */
    fun load(url: String, title: String, icon: String?) {
        val client = castContext?.sessionManager?.currentCastSession?.remoteMediaClient
        if (client == null) {
            pending = Triple(url, title, icon)
            return
        }
        val meta = MediaMetadata(MediaMetadata.MEDIA_TYPE_MOVIE).apply {
            putString(MediaMetadata.KEY_TITLE, title)
            putString(MediaMetadata.KEY_SUBTITLE, Neon.APP_NAME)
            if (icon != null) addImage(WebImage(Uri.parse(icon)))
        }
        val info = MediaInfo.Builder(url)
            .setStreamType(MediaInfo.STREAM_TYPE_LIVE)
            .setContentType("application/x-mpegURL")
            .setMetadata(meta)
            .build()
        client.load(MediaLoadRequestData.Builder().setMediaInfo(info).setAutoplay(true).build())
    }
}
