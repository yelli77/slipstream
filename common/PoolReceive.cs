using System;
using System.Collections.Generic;

namespace StarTruckMP.Common;

/// <summary>
/// custom-build-369 (Punkt A): Unity-freie Empfangs-/Reassembly-Logik fuer
/// JobBoard-Pool-Downloads (jobBoardDownload, Server -> Client, chunked).
/// Extracted aus Client/JobBoardServerSync.HandlePoolIncoming, damit die ECHTE
/// Empfangsstrecke headless im JobBoardSmokeTest laeuft (vor 369 hat der Test die
/// Empfangsseite nur gespiegelt — der echte Fehler blieb unentdeckt).
///
/// ROOT-CAUSE 368 (Beweis: Spielerlogs 368 + VPS-Serverlogs): der Server sendet
/// Pools nachweislich (1130 Jobs / 174077 bytes / 194 Chunks), die Clients empfangen
/// NICHTS — kein recv-Log, keine Fehlermeldung. Ursachen im alten Client-Handler:
///   1. transferId-Kollision: JobBoardServer waehlt transferId = Now()*37 % 251 mit
///      Now() als FLOAT-Sekunden seit Unix-Epoch (~1.79e9) — bei 24-Bit-Mantissa
///      quantisiert Now() auf 128s-Schritte, d.h. ALLE Broadcasts in einem
///      128s-Fenster haben dieselbe transferId. Der Client keyte seinen Puffer NUR
///      nach transferId: Chunks MEHRERER verschiedener Pool-Broadcasts (der Server
///      broadcastet nach JEDEM Upload, "changed || true") landeten im selben Puffer.
///   2. Stilles Werfen ueberall: doppelte Chunks wurden verworfen
///      (if (Chunks[i] == null)), Out-of-Range-Chunks return ohne Log
///      (chunkIndex >= pool.Chunks.Length), ReadString lieferte bei korrupter
///      Laenge still "" (Desync-Schleife), unvollstaendige Transfers wurden in
///      Update() nach 60s STILL verworfen (kein Log). Ergebnis: totale Funkstille.
///   3. Kein recv-Log: selbst wenn Chunks ankamen, war das UNSEHBAR.
///
/// FIX 369 (diese Klasse): jede Einzelentscheidung loggt (injectable Logger),
/// Duplikate werden ueberschrieben + bei Inhaltsabweichung als Transfer-Mixing
/// geloggt, veraltete Assemblierungen werden bei neuem Chunceingang nach
/// StaleRestartSeconds neu gestartet, Timeouts und Decodierfehler werden laut.
/// Wireformat unveraendert: [sector][senderId-Placeholder][transferId]
/// [chunkIndex][totalChunks][bytes].
/// </summary>
public static class PoolReceive
{
    /// <summary>Wird nach der letzten Assemblierung... Nein: Anzahl Chunks, ab der
    /// wir von einem "grossen" Transfer sprechen (Logging-Pragma).</summary>
    public const int MAX_CHUNK = 900; // identisch zu ChunkedSend.MAX_CHUNK (Doku-Referenz)

    /// <summary>
    /// Assembliert chunked Pool-Downloads. Eine Instanz pro Client (der Mod haelt
    /// genau eine; der Test eine pro Client).
    /// </summary>
    public sealed class Assembler
    {
        /// <summary>Injectable Uhr (Client: Time.unscaledTime, Test: Environment.TickCount-ms).</summary>
        private readonly Func<float> _now;
        /// <summary>Info-Logger (recv/completion logs).</summary>
        private readonly Action<string> _logInfo;
        /// <summary>Warn-Logger (ALLE Fehlerpfade — stille Fehlschlaege sind 369 disabled).</summary>
        private readonly Action<string> _logWarn;

        /// <summary>Ein neuer Chunk fuer eine Assemblierung, die laenger als so viele
        /// Sekunden keinen Chunk sah, startet den Puffer neu (Re-Broadcast mit
        /// kollidierender transferId wird sonst mit Alt-Chunks gemischt).</summary>
        public float StaleRestartSeconds = 5f;

        private sealed class Incoming
        {
            public string Sector;
            public int TotalChunks;
            public readonly byte[][] Chunks;
            public readonly bool[] Seen;
            public int Received;
            public float LastChunkTime;
            public int DuplicateCount;

            public Incoming(int totalChunks)
            {
                TotalChunks = Math.Max(totalChunks, 1);
                Chunks = new byte[TotalChunks][];
                Seen = new bool[TotalChunks];
            }
        }

        // Key: transferId (Wireformat hat keine bessere Transfer-Kennung; Kollisions-
        // und Mischfaelle werden unten ERLKANNT geloggt statt still gemischt).
        private readonly Dictionary<byte, Incoming> _incoming = new();

        /// <summary>Zuletzt vollstaendig assemblierte Payloads (FIFO, vom Aufrufer abzuholen).</summary>
        public readonly Queue<(string Sector, byte[] Payload)> Completed = new();

        public Assembler(Func<float> now, Action<string> logInfo, Action<string> logWarn)
        {
            _now = now ?? (() => 0f);
            _logInfo = logInfo ?? (_ => { });
            _logWarn = logWarn ?? (_ => { });
        }

        /// <summary>
        /// Verarbeitet einen empfangenen Pool-Chunk (wird aus dem Riptide-Handler
        /// aufgerufen, NACH dem Header-Parse). Wirft NICHT — alle Fehler werden
        /// geloggt (369: stille Fehlschlaege disabled) und als false gemeldet.
        /// </summary>
        /// <returns>true, wenn dieser Chunk einen Transfer VOLLSTAENDIG gemacht hat
        /// (Payload liegt dann in <see cref="Completed"/>).</returns>
        public bool HandleChunk(string sector, byte transferId, ushort chunkIndex, ushort totalChunks, byte[] data)
        {
            try
            {
                // recv-Log am Handler-Anfang (369): jeder Chunk ist sichtbar.
                _logInfo($"recv: poolDownload sector={sector} chunk={chunkIndex}/{totalChunks} (transfer={transferId}, {data?.Length ?? 0} bytes)");

                if (data == null || data.Length == 0)
                {
                    _logWarn($"poolDownload sector={sector} chunk={chunkIndex}/{totalChunks}: LEERER Chunk verworfen (transfer={transferId})");
                    return false;
                }
                if (totalChunks <= 0 || chunkIndex >= totalChunks)
                {
                    _logWarn($"poolDownload sector={sector}: ungueltige Chunk-Metadaten chunk={chunkIndex}/{totalChunks} (transfer={transferId}) — verworfen");
                    return false;
                }

                float now = _now();

                // Kollisions-/Neustart-Erkennung: existierender Puffer mit abweichenden
                // Metadaten ODER zu alt => neu starten (mit Log statt still).
                if (_incoming.TryGetValue(transferId, out var pool)
                    && (pool.Sector != sector || pool.TotalChunks != totalChunks
                        || (now - pool.LastChunkTime) > StaleRestartSeconds))
                {
                    string why = pool.Sector != sector
                        ? $"Sektorwechsel '{pool.Sector}' -> '{sector}'"
                        : pool.TotalChunks != totalChunks
                            ? $"totalChunks geaendert {pool.TotalChunks} -> {totalChunks}"
                            : $"Stale-Restart ({(now - pool.LastChunkTime):F1}s ohne Chunk)";
                    _logWarn($"poolDownload transfer={transferId}: Assemblierung NEU GESTARTET ({why}; hatte {pool.Received}/{pool.TotalChunks} Chunks) — transferId-Kollision oder Re-Broadcast");
                    _incoming.Remove(transferId);
                    pool = null;
                }

                if (pool == null)
                {
                    pool = new Incoming(totalChunks) { Sector = sector };
                    _incoming[transferId] = pool;
                    _logInfo($"poolDownload transfer={transferId}: Assemblierung gestartet (Sektor '{sector}', {totalChunks} Chunks)");
                }

                if (pool.Seen[chunkIndex])
                {
                    // Duplikat: ueberschreiben statt verwerfen; Abweichung = Transfer-Mixing
                    // (zwei Broadcasts teilen sich die transferId) -> LAUT loggen (369).
                    var old = pool.Chunks[chunkIndex];
                    bool differs = old.Length != data.Length;
                    if (!differs)
                        for (int i = 0; i < old.Length; i++)
                            if (old[i] != data[i]) { differs = true; break; }
                    pool.DuplicateCount++;
                    if (differs)
                        _logWarn($"poolDownload sector={sector} chunk={chunkIndex}/{totalChunks}: DUPLIKAT mit ABWEICHENDEM INHALT (transfer={transferId}) — Transfer-Mixing durch transferId-Kollision, ueberschreibe");
                    else if (pool.DuplicateCount <= 3 || pool.DuplicateCount % 25 == 0)
                        _logInfo($"poolDownload sector={sector} chunk={chunkIndex}/{totalChunks}: identisches Duplikat ({pool.DuplicateCount}. Gesamt, transfer={transferId})");
                    return false; // Duplikat komplettiert nichts Neues (Inhalt schon da)
                }

                pool.Chunks[chunkIndex] = data;
                pool.Seen[chunkIndex] = true;
                pool.Received++;
                pool.LastChunkTime = now;

                if (pool.Received < pool.TotalChunks) return false;

                // komplett: Payload in Chunk-Reihenfolge assemblieren
                _incoming.Remove(transferId);
                int payloadLen = 0;
                for (int i = 0; i < pool.TotalChunks; i++) payloadLen += pool.Chunks[i].Length;
                var payload = new byte[payloadLen];
                int off = 0;
                for (int i = 0; i < pool.TotalChunks; i++)
                {
                    Buffer.BlockCopy(pool.Chunks[i], 0, payload, off, pool.Chunks[i].Length);
                    off += pool.Chunks[i].Length;
                }
                _logInfo($"poolDownload sector={sector} transfer={transferId}: Alle {pool.TotalChunks} Chunks empfangen ({payloadLen} bytes, {pool.DuplicateCount} Duplikate) — Payload uebergeben");
                Completed.Enqueue((sector, payload));
                return true;
            }
            catch (Exception ex)
            {
                // 369: KEIN stiller Fehlschlag — jeder Fehler im Empfangspfad ist sichtbar.
                _logWarn($"poolDownload sector={sector} chunk={chunkIndex}/{totalChunks} (transfer={transferId}): FEHLER im Empfangspfad: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Raeumt unvollstaendige Assemblierungen ab (aus Update() gerufen) — MIT Log
        /// (vor 369: stiller Discard, dadurch war verlorener Chunks nicht sichtbar).
        /// </summary>
        public void Maintenance(float timeoutSeconds)
        {
            List<byte> stale = null;
            foreach (var kv in _incoming)
            {
                if (_now() - kv.Value.LastChunkTime <= timeoutSeconds) continue;
                (stale ??= new List<byte>()).Add(kv.Key);
            }
            if (stale == null) return;
            foreach (var k in stale)
            {
                var p = _incoming[k];
                _incoming.Remove(k);
                int missing = p.TotalChunks - p.Received;
                _logWarn($"poolDownload Sektor '{p.Sector}' (transfer={k}): UNVOLLSTAENDIG nach Timeout verworfen — {p.Received}/{p.TotalChunks} Chunks, {missing} fehlen (Indizes: {MissingIndices(p)}). Server muss neu broadcasten.");
            }
        }

        public void Reset()
        {
            foreach (var kv in _incoming)
                _logWarn($"poolDownload Sektor '{kv.Value.Sector}' (transfer={kv.Key}): Assemblierung verworfen (Reset) — {kv.Value.Received}/{kv.Value.TotalChunks} Chunks");
            _incoming.Clear();
            Completed.Clear();
        }

        private static string MissingIndices(Incoming p)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < p.TotalChunks && shown < 12; i++)
                if (!p.Seen[i]) { if (shown > 0) sb.Append(','); sb.Append(i); shown++; }
            if (shown >= 12) sb.Append(",...");
            return sb.Length == 0 ? "-" : sb.ToString();
        }
    }
}
