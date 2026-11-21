package com.camstudio.pro.data

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

private val Context.dataStore by preferencesDataStore("camstudio")

/** Small JSON-blob persistence on top of Preferences DataStore. */
class Store(private val ctx: Context) {
    private val kSettings = stringPreferencesKey("settings")
    private val kFx = stringPreferencesKey("fx")
    private val kPro = stringPreferencesKey("pro")
    private val kPcs = stringPreferencesKey("pcs")
    private val kPresets = stringPreferencesKey("presets")
    private val kDeviceId = stringPreferencesKey("deviceId")

    class Snapshot(
        val settings: AppSettings,
        val fx: FxState,
        val pro: ProState,
        val pcs: List<SavedPc>,
        val presets: List<Preset>,
        val deviceId: String,
    )

    fun loadBlocking(): Snapshot = runBlocking {
        val p = ctx.dataStore.data.first()
        fun obj(s: String?) = s?.let { runCatching { JSONObject(it) }.getOrNull() }
        fun arr(s: String?) = s?.let { runCatching { JSONArray(it) }.getOrNull() }
        var id = p[kDeviceId]
        if (id == null) {
            id = UUID.randomUUID().toString()
            ctx.dataStore.edit { it[kDeviceId] = id }
        }
        Snapshot(
            obj(p[kSettings])?.let { AppSettings.fromJson(it) } ?: AppSettings(),
            obj(p[kFx])?.let { FxState.fromJson(it) } ?: FxState(),
            obj(p[kPro])?.let { ProState.fromJson(it) } ?: ProState(),
            arr(p[kPcs])?.let { a -> (0 until a.length()).map { SavedPc.fromJson(a.getJSONObject(it)) } } ?: emptyList(),
            arr(p[kPresets])?.let { a -> (0 until a.length()).map { Preset.fromJson(a.getJSONObject(it)) } } ?: emptyList(),
            id,
        )
    }

    suspend fun saveSettings(s: AppSettings) = ctx.dataStore.edit { it[kSettings] = s.toJson().toString() }
    suspend fun saveFx(s: FxState) = ctx.dataStore.edit { it[kFx] = s.toJson().toString() }
    suspend fun savePro(s: ProState) = ctx.dataStore.edit { it[kPro] = s.toJson().toString() }

    suspend fun savePcs(list: List<SavedPc>) = ctx.dataStore.edit { p ->
        p[kPcs] = JSONArray().apply { list.forEach { put(it.toJson()) } }.toString()
    }

    suspend fun savePresets(list: List<Preset>) = ctx.dataStore.edit { p ->
        p[kPresets] = JSONArray().apply { list.forEach { put(it.toJson()) } }.toString()
    }
}
