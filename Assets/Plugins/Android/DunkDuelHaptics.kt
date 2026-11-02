// Dunk Duel haptics and thermal bridge for Android.
// Called from C# (DunkDuel.Presentation.Haptics) through AndroidJavaClass static calls.
// Kinds mirror HapticKind: 0 tick, 1 light, 2 medium, 3 heavy, 4 success, 5 warning.
package com.dunkduel.haptics

import android.app.Activity
import android.content.Context
import android.os.Build
import android.os.PowerManager
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

object DunkDuelHaptics {

    private fun vibrator(context: Context): Vibrator? {
        return if (Build.VERSION.SDK_INT >= 31) {
            val manager = context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as? VibratorManager
            manager?.defaultVibrator
        } else {
            @Suppress("DEPRECATION")
            context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
        }
    }

    @JvmStatic
    fun play(activity: Activity, kind: Int, intensity: Float) {
        val v = vibrator(activity) ?: return
        if (!v.hasVibrator()) return
        val scale = intensity.coerceIn(0f, 1f)
        fun amp(base: Int): Int = (base * scale).toInt().coerceIn(1, 255)
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                val effect = when (kind) {
                    0 -> VibrationEffect.createOneShot(8, amp(70))
                    1 -> VibrationEffect.createOneShot(14, amp(110))
                    2 -> VibrationEffect.createWaveform(longArrayOf(0, 20, 30, 12), intArrayOf(0, amp(180), 0, amp(90)), -1)
                    3 -> VibrationEffect.createWaveform(longArrayOf(0, 35, 10, 120), intArrayOf(0, amp(255), 0, amp(140)), -1)
                    4 -> VibrationEffect.createWaveform(longArrayOf(0, 18, 60, 26), intArrayOf(0, amp(150), 0, amp(230)), -1)
                    else -> VibrationEffect.createWaveform(longArrayOf(0, 16, 90, 16), intArrayOf(0, amp(160), 0, amp(160)), -1)
                }
                v.vibrate(effect)
            } else {
                @Suppress("DEPRECATION")
                v.vibrate(if (kind >= 3) 60L else 20L)
            }
        } catch (e: SecurityException) {
            // VIBRATE permission missing: haptics silently unavailable.
        }
    }

    /** 0 nominal, 1 fair, 2 serious, 3 critical, -1 unknown (API < 29). */
    @JvmStatic
    fun thermalState(activity: Activity): Int {
        if (Build.VERSION.SDK_INT < 29) return -1
        val pm = activity.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return -1
        return when (pm.currentThermalStatus) {
            PowerManager.THERMAL_STATUS_NONE -> 0
            PowerManager.THERMAL_STATUS_LIGHT -> 1
            PowerManager.THERMAL_STATUS_MODERATE -> 2
            PowerManager.THERMAL_STATUS_SEVERE -> 2
            else -> 3
        }
    }
}
