using System;
using System.Collections.Generic;
using Riptide;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

/// <summary>
/// Server-authoritative Job-Sync-Handler (custom-build-342).
///
/// Empfaengt JobBoardUpload-Nachrichten (chunked, Format siehe Client/JobBoardSyncServer.cs),
/// merged sie per JobBoardStore in den Gesamtpool des Sektors und broadcastet den VOLL-
/// STAENDIGEN Pool (chunked) an alle Clients im Sektor — vollstaendige Job-Daten, nicht
/// nur Kennungen. Bei Sektorwechsel/Join eines Clients wird ihm der aktuelle Pool des
/// Sektors direkt zugestellt.
///
/// Alle Pools sind In-Memory; Server-Restart leert sie (erste Uploads fuellen wieder).
/// </summary>
public class JobBoardServer
{
    private const int MAX_JOBS_PER_SECTOR = 512;
    private const int MAX_CHUNK_PAYLOAD = 900; // Riptide MaxPayloadSize=1225, konservativ bleiben

    private readonly JobBoardStore _store;
    private readonly Action<string> _log;

    // Chunk-Assemblierung fuer Uploads (key = playerId)
    private class IncomingUpload
    {
        public string Sector;
        public int TotalChunks;
        public readonly byte[][] Chunks;
        public int Received;
        public float LastChunkTime;

        public IncomingUpload(int totalChunks)
        {
            Chunks = new byte[Math.Max(totalChunks, 1)][];
            TotalChunks = totalChunks; // Fix: nie zugewiesen (CS0649) => Upload galt bei JEDEM Chunk als neu, Merge nie komplett
        }
    }

    private readonly Dictionary<ushort, IncomingUpload> _uploads = new();

    // Ausgehende Downloads: pro (poolVersion) einfach neu encodieren; Send-Pacing ueber
    // Frame-Queue im DedicatedServer-Loop ist nicht noetig — Chunks sind klein und reliable.
    private float _lastCleanupTime;

    public JobBoardServer(JobBoardStore store, Action<string> log)
    {
        _store = store;
        _log = log;
        _lastCleanupTime = Now();
    }

    private static float Now() => (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    // custom-build-369-Nachtrag: transferId ueber die Leitung ist ein byte. Der alte
    // (byte)(Now()*37%251)-Wert quantisierte bei float32-Epochenzeit (~1.8e9 s) in
    // ~128s-Fenstern -> ALLE Broadcasts im Fenster teilten dieselbe transferId
    // (klientseitiges Transfer-Mixing). Jetzt: globaler monotoner Counter,
    // Interlocked fuer Thread-Sicherheit (Broadcasts laufen auf verschiedenen Threads).
    private static int _nextTransferId;

    /// <summary>Eindeutige, monoton steigende transferId (byte-Wire-Format). Zwei
    /// aufeinanderfolgende Broadcasts (auch an verschiedene Clients/Sektoren im selben
    /// Tick) bekommen niemals denselben Wert (Counter-Inkrement, nicht Zeit-basiert).</summary>
    private static byte NextTransferId() => (byte)(System.Threading.Interlocked.Increment(ref _nextTransferId) & 0xFF);

    public void HandleUpload(MessageReceivedEventArgs e, Riptide.Server server)
    {
        // Wireformat (identisch zu ChunkedBlobTransfer-Relay-Muster):
        // [string sector][ushort senderId(placeholder)][byte transferId][ushort chunkIndex]
        // [ushort totalChunks][bytes chunkData]
        string sector = e.Message.GetString();
        e.Message.GetUShort(); // senderId-Placeholder (Client kennt seine Server-ID nicht)
        byte transferId = e.Message.GetByte();
        ushort chunkIndex = e.Message.GetUShort();
        ushort totalChunks = e.Message.GetUShort();
        byte[] chunkData = e.Message.GetBytes();

        ushort senderId = e.FromConnection.Id;
        if (string.IsNullOrEmpty(sector) || sector == "none")
        {
            _log($"[WARN] JobBoardServer: Upload von {senderId} mit ungueltigem Sektor '{sector}' verworfen");
            return;
        }
        // 343: 512 Chunks à 900 B = ~460 KB — deckt auch 500-Job-Sektoren.
        const int MAX_UPLOAD_CHUNKS = 512;
        if (totalChunks > MAX_UPLOAD_CHUNKS)
        {
            _log($"[WARN] JobBoardServer: Upload von {senderId} mit {totalChunks} Chunks zu gross (Limit {MAX_UPLOAD_CHUNKS} = ~{MAX_UPLOAD_CHUNKS * MAX_CHUNK_PAYLOAD / 1024} KB) - verworfen");
            return;
        }

        if (!_uploads.TryGetValue(senderId, out var up) || up.Sector != sector || up.TotalChunks != totalChunks)
        {
            up = new IncomingUpload(totalChunks) { Sector = sector };
            _uploads[senderId] = up;
        }

        if (chunkIndex >= up.Chunks.Length)
        {
            _log($"[WARN] JobBoardServer: Chunk-Index {chunkIndex}/{totalChunks} von {senderId} ausserhalb - verworfen");
            return;
        }
        if (up.Chunks[chunkIndex] == null)
        {
            up.Chunks[chunkIndex] = chunkData;
            up.Received++;
        }
        up.LastChunkTime = Now();

        if (up.Received < totalChunks) return;

        // komplett: Payload assemblieren, decodieren, mergen, Pool broadcasten
        _uploads.Remove(senderId);
        int payloadLen = 0;
        foreach (var c in up.Chunks) payloadLen += c?.Length ?? 0;
        var payload = new byte[payloadLen];
        int off = 0;
        foreach (var c in up.Chunks)
        {
            if (c == null) continue;
            Buffer.BlockCopy(c, 0, payload, off, c.Length);
            off += c.Length;
        }

        try
        {
            var jobs = JobBoardCodec.DecodeJobs(payload);
            if (jobs.Count > MAX_JOBS_PER_SECTOR) jobs.RemoveRange(MAX_JOBS_PER_SECTOR, jobs.Count - MAX_JOBS_PER_SECTOR);
            bool changed = _store.MergeJobs(sector, jobs);
            _log($"[INFO] JobBoardServer: Upload von {senderId} fuer Sektor '{sector}': {jobs.Count} Jobs gemerged (Pool jetzt {_store.GetJobCount(sector)} Jobs, v{_store.GetVersion(sector)}, changed={changed})");
            if (changed || true) // immer senden: Spaetankoemmlinge brauchen auch bei changed=false den Pool
                BroadcastPool(server, sector);
        }
        catch (Exception ex)
        {
            _log($"[WARN] JobBoardServer: Upload-Payload von {senderId} (Sektor '{sector}', {payloadLen} bytes) ungueltig: {ex.Message}");
        }
    }

    private static Riptide.Connection GetConn(Riptide.Server server, ushort playerId)
    {
        // Riptide 2.2: kein GetConnection(id) — Clients-Array linear scannen reicht bei
        // (<10) Spielern locker.
        var clients = server.Clients;
        foreach (var c in clients)
            if (c != null && c.Id == playerId) return c;
        return null;
    }

    /// <summary>Broadcastet den vollen Pool eines Sektors an alle Mitglieder (chunked, reliable).</summary>
    public void BroadcastPool(Riptide.Server server, string sector)
    {
        var jobs = _store.GetJobs(sector);
        if (jobs.Length == 0) return;
        int version = _store.GetVersion(sector);
        byte[] payload;
        try { payload = JobBoardCodec.EncodeJobs(jobs, version); }
        catch (Exception ex)
        {
            _log($"[WARN] JobBoardServer: Pool-Encode fehlgeschlagen (Sektor '{sector}'): {ex.Message}");
            return;
        }

        var members = _store.GetMembers(sector);
        if (members.Count == 0) return;

        int totalChunks = Math.Max(1, (payload.Length + MAX_CHUNK_PAYLOAD - 1) / MAX_CHUNK_PAYLOAD);
        byte transferId = NextTransferId();
        // 343: Chunks nur in kleinen Bursts senden — Riptide-Reliable-Bursts verlieren
        // sonst den letzten Chunk (Lesson 335). 15 Chunks pro Burst, 2 ms Pause dazwischen.
        const int SEND_PACING = 15;
        for (int ci = 0; ci < totalChunks; ci++)
        {
            int len = Math.Min(MAX_CHUNK_PAYLOAD, payload.Length - ci * MAX_CHUNK_PAYLOAD);
            var chunk = new byte[len];
            Buffer.BlockCopy(payload, ci * MAX_CHUNK_PAYLOAD, chunk, 0, len);
            var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.jobBoardDownload);
            msg.AddString(sector);
            msg.AddUShort(0); // senderId-Platzhalter (Server-Broadcast)
            msg.AddByte(transferId);
            msg.AddUShort((ushort)ci);
            msg.AddUShort((ushort)totalChunks);
            msg.AddBytes(chunk);
            foreach (var memberId in members)
            {
                var conn = GetConn(server, memberId);
                if (conn != null) server.Send(msg, conn);
            }
            if ((ci + 1) % SEND_PACING == 0 && ci + 1 < totalChunks)
                System.Threading.Thread.Sleep(2);
        }
        _log($"[INFO] JobBoardServer: Pool-Download '{sector}' an {members.Count} Clients: {jobs.Length} Jobs, {payload.Length} bytes, {totalChunks} Chunks (v{version})");
    }

    /// <summary>Client hat einen Job angenommen: aus dem Pool entfernen + Event broadcasten.</summary>
    public void HandleJobTaken(MessageReceivedEventArgs e, Riptide.Server server)
    {
        string sector = e.Message.GetString();
        string questId = e.Message.GetString();
        ushort playerId = e.FromConnection.Id;

        if (string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(questId)) return;
        bool removed = _store.RemoveJob(sector, questId);
        _log($"[INFO] JobBoardServer: JobTaken '{questId}' von {playerId} (Sektor '{sector}', removed={removed}, Pool jetzt {_store.GetJobCount(sector)})");

        // Event an alle im Sektor (inkl. Absender — Absender nutzt es als Bestaetigung,
        // doppeltes Entfernen ist idempotent).
        var members = _store.GetMembers(sector);
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.jobTaken);
        msg.AddString(sector);
        msg.AddString(questId);
        foreach (var memberId in members)
        {
            var conn = GetConn(server, memberId);
            if (conn != null) server.Send(msg, conn);
        }
    }

    /// <summary>Sektorwechsel/Join: Store-Tabelle pflegen + aktuellen Pool an den einen Client senden.</summary>
    public void OnPlayerSector(ushort playerId, string sector, Riptide.Server server)
    {
        _store.SetPlayerSector(playerId, sector);
        if (!_store.HasPool(sector)) return;
        var conn = GetConn(server, playerId);
        if (conn == null) return;
        var jobs = _store.GetJobs(sector);
        int version = _store.GetVersion(sector);
        byte[] payload;
        try { payload = JobBoardCodec.EncodeJobs(jobs, version); }
        catch (Exception ex)
        {
            _log($"[WARN] JobBoardServer: Pool-Encode fuer {playerId} fehlgeschlagen: {ex.Message}");
            return;
        }
        int totalChunks = Math.Max(1, (payload.Length + MAX_CHUNK_PAYLOAD - 1) / MAX_CHUNK_PAYLOAD);
        byte transferId = NextTransferId();
        for (int ci = 0; ci < totalChunks; ci++)
        {
            int len = Math.Min(MAX_CHUNK_PAYLOAD, payload.Length - ci * MAX_CHUNK_PAYLOAD);
            var chunk = new byte[len];
            Buffer.BlockCopy(payload, ci * MAX_CHUNK_PAYLOAD, chunk, 0, len);
            var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.jobBoardDownload);
            msg.AddString(sector);
            msg.AddUShort(0);
            msg.AddByte(transferId);
            msg.AddUShort((ushort)ci);
            msg.AddUShort((ushort)totalChunks);
            msg.AddBytes(chunk);
            server.Send(msg, conn);
        }
        _log($"[INFO] JobBoardServer: Pool-Download '{sector}' an {playerId} (Join/Sektorwechsel): {jobs.Length} Jobs, {payload.Length} bytes, {totalChunks} Chunks");
    }

    public void OnPlayerDisconnected(ushort playerId)
    {
        _uploads.Remove(playerId);
        _store.RemovePlayer(playerId);
    }

    /// <summary>Veraltete Upload-Assemblierungen abr&auml;umen (stuck transfers).</summary>
    public void Maintenance()
    {
        float now = Now();
        if (now - _lastCleanupTime < 10f) return;
        _lastCleanupTime = now;
        var stale = new List<ushort>();
        foreach (var kv in _uploads)
            if (now - kv.Value.LastChunkTime > 60f) stale.Add(kv.Key);
        foreach (var id in stale)
        {
            _uploads.Remove(id);
            _log($"[INFO] JobBoardServer: stuck Upload von {id} verworfen (Timeout 60s)");
        }
    }
}
