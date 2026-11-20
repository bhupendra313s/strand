package dev.localcontinuity.app

import android.net.Uri
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import java.util.concurrent.atomic.AtomicLong

/** Connection phase shown in the UI. */
enum class Phase { NOT_PAIRED, STOPPED, CONNECTING, CONNECTED, ERROR }

data class SyncStatus(
    val phase: Phase = Phase.STOPPED,
    val detail: String = "Not running",
    val hubName: String? = null,
    val hubFeatures: Set<String> = emptySet(),
) {
    val connected get() = phase == Phase.CONNECTED
}

/** App-wide observable state. Written by SyncService, read by Compose and the tile. */
object SyncState {
    private val _status = MutableStateFlow(SyncStatus())
    val status: StateFlow<SyncStatus> = _status.asStateFlow()
    val connected: Boolean get() = _status.value.connected

    fun update(transform: (SyncStatus) -> SyncStatus) = _status.update(transform)
}

enum class Direction { IN, OUT }

data class ActivityItem(
    val id: Long,
    val featureId: String,
    val direction: Direction,
    val title: String,
    val detail: String,
    val time: Long = System.currentTimeMillis(),
    val image: Uri? = null,
    /** Correlates later updates, e.g. a screenshot id to mark it "Pasted on PC". */
    val key: String? = null,
)

/** In-memory recent activity (never persisted: rows may contain clipboard previews). */
object ActivityLog {
    private val ids = AtomicLong()
    private val _items = MutableStateFlow<List<ActivityItem>>(emptyList())
    val items: StateFlow<List<ActivityItem>> = _items.asStateFlow()

    /** Last time anything was added or progressed, and which way it went: drives the dot on the Home link. */
    data class Pulse(val at: Long, val direction: Direction)
    private val _pulse = MutableStateFlow(Pulse(0L, Direction.OUT))
    val pulse: StateFlow<Pulse> = _pulse.asStateFlow()

    fun add(featureId: String, direction: Direction, title: String, detail: String, image: Uri? = null, key: String? = null) {
        val item = ActivityItem(ids.incrementAndGet(), featureId, direction, title, detail, image = image, key = key)
        _items.update { (listOf(item) + it).take(50) }
        _pulse.value = Pulse(System.currentTimeMillis(), direction)
    }

    fun updateByKey(key: String, transform: (ActivityItem) -> ActivityItem) {
        var dir: Direction? = null
        _items.update { list -> list.map { if (it.key == key) transform(it).also { n -> dir = n.direction } else it } }
        dir?.let { _pulse.value = Pulse(System.currentTimeMillis(), it) }
    }

    fun preview(text: String, max: Int = 120): String {
        val one = text.trim().replace(Regex("\\s+"), " ")
        return if (one.length <= max) one else one.take(max) + "…"
    }
}
