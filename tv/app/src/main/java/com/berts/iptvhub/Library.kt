package com.berts.iptvhub

import android.content.Context
import android.os.Handler
import android.os.Looper
import java.io.File
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.Executors

/**
 * Channel list, categories, favourites and now/next, kept on the TV box so the app opens instantly.
 * The cached copy is shown first, then refreshed from the hub (which itself stays up to date with the provider).
 */
class Library(private val context: Context, private val api: HubApi) {

    fun interface Listener {
        fun onLibraryChanged()
    }

    @Volatile var categories: List<Category> = emptyList()
        private set
    @Volatile var channels: List<Channel> = emptyList()
        private set
    @Volatile var byId: Map<Int, Channel> = emptyMap()
        private set
    @Volatile var favorites: List<Int> = emptyList()
        private set
    @Volatile var nowNext: Map<Int, NowNext> = emptyMap()
        private set
    @Volatile var lastError: String? = null
        private set
    @Volatile var loading: Boolean = false
        private set
    @Volatile var lastRefreshMs: Long = 0L
        private set

    private val executor = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArraySet<Listener>()
    private var lastEpgMs = 0L

    private val categoriesFile get() = File(context.filesDir, "categories.json")
    private val channelsFile get() = File(context.filesDir, "channels.json")

    fun addListener(l: Listener) { listeners.add(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    fun loadCached() {
        executor.execute {
            runCatching {
                if (categoriesFile.exists()) categories = api.parseCategories(categoriesFile.readText())
                if (channelsFile.exists()) setChannels(api.parseChannels(channelsFile.readText()))
            }
            notifyChanged()
        }
    }

    /** Pull the latest channel list, favourites and now/next from the hub. */
    fun refresh(onDone: ((String?) -> Unit)? = null) {
        loading = true
        notifyChanged()
        executor.execute {
            var error: String? = null
            try {
                val catJson = api.categoriesJson()
                val chJson = api.channelsJson()
                val newCategories = api.parseCategories(catJson)
                val newChannels = api.parseChannels(chJson)
                if (newChannels.isNotEmpty()) {
                    categories = newCategories
                    setChannels(newChannels)
                    runCatching { categoriesFile.writeText(catJson); channelsFile.writeText(chJson) }
                }
                favorites = api.favorites()
                lastRefreshMs = System.currentTimeMillis()
            } catch (e: Exception) {
                error = e.message ?: e.javaClass.simpleName
            }
            loadEpg()
            lastError = error
            loading = false
            notifyChanged()
            if (onDone != null) main.post { onDone(error) }
        }
    }

    /** Now/next for every channel; cheap because the hub has already worked it out. */
    fun refreshEpg(force: Boolean = false) {
        executor.execute {
            if (!force && System.currentTimeMillis() - lastEpgMs < EPG_REFRESH_MS) return@execute
            loadEpg()
            notifyChanged()
        }
    }

    fun setFavoritesFromSync(ids: List<Int>) {
        favorites = ids
        notifyChanged()
    }

    fun isFavorite(id: Int) = favorites.contains(id)

    /** Optimistic toggle; the hub then pushes the shared list to every TV. */
    fun toggleFavorite(id: Int, onError: (String) -> Unit) {
        val makeFavourite = !favorites.contains(id)
        val before = favorites
        favorites = if (makeFavourite) favorites + id else favorites - id
        notifyChanged()
        executor.execute {
            try {
                api.setFavorite(id, makeFavourite)
            } catch (e: Exception) {
                favorites = before
                notifyChanged()
                main.post { onError(e.message ?: "Couldn't update favourites") }
            }
        }
    }

    fun channelsFor(category: String): List<Channel> = when (category) {
        MainActivity.CATEGORY_FAVORITES -> favorites.mapNotNull { byId[it] }
        MainActivity.CATEGORY_ALL -> channels
        else -> channels.filter { it.categoryId == category }
    }

    private fun loadEpg() {
        try {
            nowNext = api.epgNow()
            lastEpgMs = System.currentTimeMillis()
        } catch (_: Exception) {
            // Keep the previous now/next; the guide is a nice-to-have.
        }
    }

    private fun setChannels(list: List<Channel>) {
        channels = list
        byId = list.associateBy { it.id }
    }

    private fun notifyChanged() {
        main.post { listeners.forEach { it.onLibraryChanged() } }
    }

    companion object {
        private const val EPG_REFRESH_MS = 5 * 60 * 1000L
    }
}
