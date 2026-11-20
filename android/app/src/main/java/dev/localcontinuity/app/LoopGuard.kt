package dev.localcontinuity.app

/** Echo/duplicate/stale suppression. Same rules as the hub (PROTOCOL.md §5). */
class LoopGuard(private val selfId: String) {
    private val newestByOrigin = HashMap<String, Long>()
    private val seen = LinkedHashSet<String>()
    private var lastAppliedRemoteHash: String? = null
    private var lastBroadcastHash: String? = null

    @Synchronized
    fun acceptIncoming(id: String, origin: String, ts: Long): Boolean {
        if (origin == selfId || id in seen) return false
        val newest = newestByOrigin[origin]
        if (newest != null && ts < newest) return false
        newestByOrigin[origin] = maxOf(newest ?: ts, ts)
        seen += id
        if (seen.size > 512) seen.remove(seen.first())
        return true
    }

    @Synchronized
    fun markAppliedRemote(hash: String) {
        lastAppliedRemoteHash = hash
        lastBroadcastHash = null
    }

    @Synchronized
    fun shouldBroadcastLocal(hash: String): Boolean {
        if (hash == lastAppliedRemoteHash || hash == lastBroadcastHash) return false
        lastAppliedRemoteHash = null
        lastBroadcastHash = hash
        return true
    }
}
