package com.berts.iptvhub

import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.TextView

/** Neon palette shared with Bert's Capture Cast and the hub's status page. */
object Neon {
    const val APP_NAME = "Bert's IPTV Hub"

    val BG = Color.rgb(6, 10, 22)
    val PANEL = Color.rgb(12, 22, 39)
    val PANEL_DARK = Color.rgb(9, 16, 30)
    val PANEL_FOCUS = Color.rgb(22, 70, 94)
    val LINE = Color.rgb(31, 53, 80)
    val TEXT = Color.rgb(234, 246, 255)
    val MUTED = Color.rgb(148, 169, 195)
    val CYAN = Color.rgb(34, 230, 255)
    val FOCUS = Color.rgb(102, 247, 255)
    val MAGENTA = Color.rgb(255, 43, 214)
    val VIOLET = Color.rgb(155, 123, 255)
    val GREEN = Color.rgb(125, 255, 176)
    val AMBER = Color.rgb(255, 189, 107)
    val RED = Color.rgb(255, 138, 154)
}

fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

fun Context.appVersion(): String = "v${BuildConfig.VERSION_NAME} (build ${BuildConfig.VERSION_CODE})"

fun Context.label(text: CharSequence, sizeSp: Float, color: Int, bold: Boolean = false): TextView =
    TextView(this).apply {
        this.text = text
        textSize = sizeSp
        setTextColor(color)
        if (bold) typeface = Typeface.create(Typeface.DEFAULT, Typeface.BOLD)
        isFocusable = false
    }

fun Context.glow(view: TextView, color: Int, radiusDp: Int = 8): TextView = view.apply {
    setShadowLayer(dp(radiusDp).toFloat(), 0f, 0f, color)
}

fun Context.panel(fill: Int, stroke: Int, strokeWidthDp: Int = 1, radiusDp: Int = 12): GradientDrawable =
    GradientDrawable().apply {
        cornerRadius = dp(radiusDp).toFloat()
        setColor(fill)
        setStroke(dp(strokeWidthDp), stroke)
    }

/** Gives a focusable view the neon focus treatment used throughout the app. */
fun Context.neonFocusable(view: View, accent: Int = Neon.FOCUS, radiusDp: Int = 10, normalFill: Int = Neon.PANEL, scale: Boolean = true) {
    view.isFocusable = true
    view.isFocusableInTouchMode = false
    view.background = panel(normalFill, Neon.LINE, 1, radiusDp)
    view.setOnFocusChangeListener { v, focused ->
        v.background = if (focused) panel(Neon.PANEL_FOCUS, accent, 3, radiusDp) else panel(normalFill, Neon.LINE, 1, radiusDp)
        if (scale) v.animate().scaleX(if (focused) 1.02f else 1f).scaleY(if (focused) 1.02f else 1f).setDuration(90).start()
    }
}

fun Context.neonButton(text: String, accent: Int, onClick: () -> Unit): Button = Button(this).apply {
    this.text = text
    isAllCaps = false
    textSize = 15f
    setTextColor(Neon.TEXT)
    gravity = Gravity.CENTER
    minHeight = 0
    minimumHeight = 0
    setPadding(dp(16), dp(9), dp(16), dp(9))
    stateListAnimator = null
    val ctx = this@neonButton
    background = ctx.panel(Neon.PANEL, accent, 1, 10)
    setOnFocusChangeListener { v, focused ->
        v.background = if (focused) ctx.panel(Neon.PANEL_FOCUS, accent, 3, 10) else ctx.panel(Neon.PANEL, accent, 1, 10)
        v.animate().scaleX(if (focused) 1.04f else 1f).scaleY(if (focused) 1.04f else 1f).setDuration(90).start()
    }
    setOnClickListener { onClick() }
}
