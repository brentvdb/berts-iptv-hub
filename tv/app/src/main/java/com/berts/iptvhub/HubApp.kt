package com.berts.iptvhub

import android.app.Activity
import android.app.Application
import android.content.Context
import android.content.Intent
import android.os.Bundle

class HubApp : Application() {
    lateinit var config: HubConfig
        private set
    lateinit var api: HubApi
        private set
    lateinit var sync: HubSync
        private set
    lateinit var library: Library
        private set

    /** True on TV boxes, false on phones/tablets (decides layout, Back behaviour and casting). */
    var tv: Boolean = true
        private set

    /** Chromecast support (phones only); created on first use. */
    val cast: CastHelper by lazy { CastHelper(this) }

    private var resumed: Activity? = null

    override fun onCreate() {
        super.onCreate()
        tv = isTv()
        config = HubConfig(this)
        api = HubApi(config)
        library = Library(this, api)
        sync = HubSync(config, api, if (tv) "tv" else "phone")
        sync.addListener(object : HubSync.Listener {
            override fun onFavorites(ids: List<Int>) = library.setFavoritesFromSync(ids)
            override fun onCastPlay(streamId: Int, from: String) = playCastOnThisTv(streamId)
        })
        registerActivityLifecycleCallbacks(object : ActivityLifecycleCallbacks {
            override fun onActivityResumed(activity: Activity) { resumed = activity }
            override fun onActivityPaused(activity: Activity) { if (resumed === activity) resumed = null }
            override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) {}
            override fun onActivityStarted(activity: Activity) {}
            override fun onActivityStopped(activity: Activity) {}
            override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) {}
            override fun onActivityDestroyed(activity: Activity) {}
        })
        library.loadCached()
        if (config.isConfigured) sync.start()
    }

    /** A phone chose this TV: open the player (if it's already playing, it simply follows the hub). */
    private fun playCastOnThisTv(streamId: Int) {
        if (!tv) return
        val current = resumed
        if (current is PlayerActivity) return
        if (PlayerActivity.zapList.isEmpty()) PlayerActivity.zapList = library.channels.map { it.id }
        val intent = Intent(current ?: this, PlayerActivity::class.java)
            .putExtra(PlayerActivity.EXTRA_STREAM_ID, streamId)
            .putExtra(PlayerActivity.EXTRA_JOIN, true)
        if (current == null) intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        // From the background Android may refuse this; the TV then just shows "Watch now" when opened.
        runCatching { (current ?: this).startActivity(intent) }
    }
}

val Context.hub: HubApp get() = applicationContext as HubApp
