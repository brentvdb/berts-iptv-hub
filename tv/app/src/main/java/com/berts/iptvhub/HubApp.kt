package com.berts.iptvhub

import android.app.Application
import android.content.Context

class HubApp : Application() {
    lateinit var config: HubConfig
        private set
    lateinit var api: HubApi
        private set
    lateinit var sync: HubSync
        private set
    lateinit var library: Library
        private set

    override fun onCreate() {
        super.onCreate()
        config = HubConfig(this)
        api = HubApi(config)
        library = Library(this, api)
        sync = HubSync(config, api)
        sync.addListener(object : HubSync.Listener {
            override fun onFavorites(ids: List<Int>) = library.setFavoritesFromSync(ids)
        })
        library.loadCached()
        if (config.isConfigured) sync.start()
    }
}

val Context.hub: HubApp get() = applicationContext as HubApp
