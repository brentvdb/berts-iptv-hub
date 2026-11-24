package com.berts.iptvhub

import android.text.TextUtils
import android.view.Gravity
import android.view.KeyEvent
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import coil.load

data class CategoryItem(val id: String, val name: String, val count: Int)

class CategoryAdapter(
    private val onFocus: (CategoryItem) -> Unit,
    private val onClick: (CategoryItem) -> Unit,
) : RecyclerView.Adapter<CategoryAdapter.VH>() {

    var items: List<CategoryItem> = emptyList()
        private set
    var selectedId: String = ""
        set(value) {
            if (field == value) return
            val old = items.indexOfFirst { it.id == field }
            field = value
            if (old >= 0) notifyItemChanged(old)
            val now = items.indexOfFirst { it.id == value }
            if (now >= 0) notifyItemChanged(now)
        }

    init { setHasStableIds(true) }

    fun submit(list: List<CategoryItem>) {
        items = list
        notifyDataSetChanged()
    }

    class VH(val root: LinearLayout, val name: TextView, val count: TextView) : RecyclerView.ViewHolder(root)

    override fun getItemId(position: Int): Long = items[position].id.hashCode().toLong()
    override fun getItemCount(): Int = items.size

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
        val ctx = parent.context
        val root = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(ctx.dp(14), ctx.dp(10), ctx.dp(12), ctx.dp(10))
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = ctx.dp(6)
            }
        }
        ctx.neonFocusable(root, Neon.FOCUS, 10, Neon.PANEL_DARK, scale = false)
        val name = ctx.label("", 16f, Neon.TEXT).apply {
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }
        val count = ctx.label("", 13f, Neon.MUTED)
        root.addView(name, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(count, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = ctx.dp(8) })
        return VH(root, name, count)
    }

    override fun onBindViewHolder(holder: VH, position: Int) {
        val item = items[position]
        holder.name.text = item.name
        holder.count.text = item.count.toString()
        val selected = item.id == selectedId
        holder.name.setTextColor(if (selected) Neon.CYAN else Neon.TEXT)
        holder.name.setShadowLayer(if (selected) 12f else 0f, 0f, 0f, Neon.CYAN)
        val ctx = holder.root.context
        holder.root.setOnClickListener { onClick(item) }
        holder.root.setOnFocusChangeListener { v, focused ->
            v.background = if (focused) ctx.panel(Neon.PANEL_FOCUS, Neon.FOCUS, 3, 10) else ctx.panel(Neon.PANEL_DARK, Neon.LINE, 1, 10)
            if (focused) onFocus(item)
        }
    }
}

class ChannelAdapter(
    private val onFocus: (Channel) -> Unit,
    private val onClick: (Channel) -> Unit,
    private val onToggleFavorite: (Channel) -> Unit,
) : RecyclerView.Adapter<ChannelAdapter.VH>() {

    var items: List<Channel> = emptyList()
        private set
    var favorites: Set<Int> = emptySet()
    var nowNext: Map<Int, NowNext> = emptyMap()
    var playingId: Int? = null

    init { setHasStableIds(true) }

    fun submit(list: List<Channel>) {
        items = list
        notifyDataSetChanged()
    }

    fun refreshDecorations() = notifyItemRangeChanged(0, items.size)

    class VH(
        val root: LinearLayout,
        val num: TextView,
        val logo: ImageView,
        val name: TextView,
        val now: TextView,
        val badge: TextView,
    ) : RecyclerView.ViewHolder(root)

    override fun getItemId(position: Int): Long = items[position].id.toLong()
    override fun getItemCount(): Int = items.size

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
        val ctx = parent.context
        val root = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(ctx.dp(12), ctx.dp(8), ctx.dp(14), ctx.dp(8))
            layoutParams = RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                bottomMargin = ctx.dp(6)
            }
            isLongClickable = true
        }
        ctx.neonFocusable(root, Neon.FOCUS, 10, Neon.PANEL, scale = false)

        val num = ctx.label("", 14f, Neon.MUTED).apply { gravity = Gravity.END }
        val logo = ImageView(ctx).apply {
            scaleType = ImageView.ScaleType.FIT_CENTER
            background = ctx.panel(Neon.BG, Neon.LINE, 1, 8)
            setPadding(ctx.dp(3), ctx.dp(3), ctx.dp(3), ctx.dp(3))
        }
        val texts = LinearLayout(ctx).apply { orientation = LinearLayout.VERTICAL }
        val name = ctx.label("", 17f, Neon.TEXT, bold = true).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END }
        val now = ctx.label("", 13f, Neon.MUTED).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END }
        texts.addView(name)
        texts.addView(now)
        val badge = ctx.label("", 15f, Neon.MAGENTA, bold = true)

        root.addView(num, LinearLayout.LayoutParams(ctx.dp(44), ViewGroup.LayoutParams.WRAP_CONTENT))
        root.addView(logo, LinearLayout.LayoutParams(ctx.dp(64), ctx.dp(40)).apply { leftMargin = ctx.dp(10) })
        root.addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = ctx.dp(12) })
        root.addView(badge, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { leftMargin = ctx.dp(8) })
        return VH(root, num, logo, name, now, badge)
    }

    override fun onBindViewHolder(holder: VH, position: Int) {
        val ch = items[position]
        val ctx = holder.root.context
        holder.num.text = ch.num.toString()
        holder.name.text = ch.name
        holder.logo.load(ch.icon) { crossfade(false) }

        val nowProg = nowNext[ch.id]?.now
        holder.now.text = nowProg?.title ?: ""
        holder.now.visibility = if (nowProg == null) android.view.View.GONE else android.view.View.VISIBLE

        val isPlaying = playingId == ch.id
        val isFav = favorites.contains(ch.id)
        holder.badge.text = when {
            isPlaying && isFav -> "▶ ★"
            isPlaying -> "▶"
            isFav -> "★"
            else -> ""
        }
        holder.badge.setTextColor(if (isPlaying) Neon.GREEN else Neon.MAGENTA)

        holder.root.setOnClickListener { onClick(ch) }
        holder.root.setOnLongClickListener { onToggleFavorite(ch); true }
        holder.root.setOnKeyListener { _, keyCode, event ->
            if (keyCode == KeyEvent.KEYCODE_MENU || keyCode == KeyEvent.KEYCODE_BOOKMARK) {
                if (event.action == KeyEvent.ACTION_UP) onToggleFavorite(ch)
                true
            } else false
        }
        holder.root.setOnFocusChangeListener { v, focused ->
            v.background = if (focused) ctx.panel(Neon.PANEL_FOCUS, Neon.FOCUS, 3, 10) else ctx.panel(Neon.PANEL, Neon.LINE, 1, 10)
            if (focused) onFocus(ch)
        }
    }
}
