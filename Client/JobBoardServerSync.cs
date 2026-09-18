using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Riptide;
using StarTruckMP.Utilities;
using StarTruckSaveData;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// custom-build-342: Client-Seite des SERVER-AUTORITATIVEN Job-Syncs (Neuerarbeitung,
    /// mit Michael abgestimmt). Ersetzt JobBoardSync (FlatSharp-Blob) und JobBoardIdSync
    /// (Kennungs-Filter) — kein Doppel-Broadcast, keine FlatSharp-Union mehr.
    ///
    /// ARCHITEKTUR (docs/DESIGN-server-authoritative-jobboard.md):
    /// - ALLE Clients im Sektor laden ihre lokal generierte Job-Liste hoch (JobBoardUpload),
    ///   bei Sektor-Betreten und bei jeder lokalen Neu-Generierung. Server bildet den
    ///   GESAMTPOOL pro Sektor (Vereinigung, dedupe nach questId).
    /// - Server verteilt den VOLLSTAENDIGEN Pool (JobBoardDownload, chunked) an alle
    ///   Clients im Sektor.
    /// - Clients rendern/akzeptieren nur noch Jobs, die im Pool sind (questId-Set).
    ///   Fallback: Solo/bevor Pool ankommt gilt die lokale Liste unangetastet.
    /// - AcceptJob: Client schickt jobTaken(questId) an den Server; Server broadcastet
    ///   das Event, alle entfernen den Job aus dem Board.
    ///
    /// SERIALISIERUNG: explizite Binaer-Codierung ueber BinaryWriter (Vorbild CargoSync) —
    /// NUR die Felder, die Anzeige/Accept brauchen (questId, displayName, displayDescription,
    /// flache Parameter-Primitiva). KEINE FlatSharp-Union, KEINE QuestInstanceSaveData-
    /// Objekte ueber die Leitung (IL2CPP-'Discriminator=0'-Lesson 326/327/335).
    ///
    /// MAIN-THREAD: Empfangen/assemblieren passiert in den Riptide-Callbacks; das Anwenden
    /// (Board-Filter + JobTaken-Entfernen) ist ein reiner Set-Vergleich auf questId-Ebene —
    /// budgetiert aufgebraucht ueber Update(), es werden KEINE Spielobjekte in einem Frame
    /// massenhaft angefasst (Freeze-Lektion 336/340).
    /// </summary>
    public static class JobBoardServerSync
    {
        // Sende-Seite: Upload einmalig pro (Sektor, Job-Hash) — Neu-Generierung mit
        // identischem Inhalt wird nicht erneut hochgeladen.
        private static string lastUploadedSector = null;
        private static string lastUploadedHash = null;
        private static float nextUploadAllowedTime = 0f;
        private const float MIN_UPLOAD_INTERVAL = 1.5f;

        // Build-368 (Re-Send-Backoff): war der letzte Upload zwar verschickt, ist aber seitdem
        // KEIN Pool-Download vom Server angekommen (letzter Pool-Empfang liegt VOR dem
        // Upload und > NO_POOL_RESEND_AFTER Sekunden zurueck), gehen wir von verlorenen
        // Chunks aus (Riptide-Reliable-Burst, Lesson 335) und erlauben Re-Sends mit
        // EXponentiellem Backoff (15s, 30s, 60s, 120s) — nach MAX_CONSECUTIVE_RESENDS
        // ohne einen einzigen Pool-Empfang stoppen wir ganz (kein Endlos-Re-Send mehr,
        // Log-Fund custom-build-367: 22+ identische Re-Sends im 15s-Takt).
        private const float NO_POOL_RESEND_AFTER = 15f;
        private const float RESEND_MIN_INTERVAL = 15f;
        private const int MAX_CONSECUTIVE_RESENDS = 4;
        private static float lastUploadTime = -999f;
        private static float nextResendTime = 0f;
        private static int consecutiveResends = 0;

        // Build-362 (Upload-Retry): OnLocalJobsGenerated() lief EINMALIG pro Generierung
        // (Harmony-Postfix auf GenerateJobsForAllSectors). War der Client zum Generierungs-
        // Zeitpunkt noch nicht verbunden (Jobs entstehen beim World-Load VOR dem Connect),
        // early-returnte alles still und es gab KEINEN zweiten Versuch — der Server-Pool
        // blieb leer. Update() pollt daher alle RETRY_INTERVAL-Sekunden erneut; die Guards
        // unten (Hash-Gate, Pace) machen den Poll idempotent und billig.
        private const float RETRY_INTERVAL = 3f;
        private static float nextRetryPollTime = 0f;
        // Skip-Grund nur EINMALIG pro (Grund) loggen — Aenderung des Grundes loggt erneut,
        // erfolgreicher Upload setzt das Log-Fenster zurueck.
        private static string lastLoggedSkip = null;

        // Empfangs-Seite: Chunk-Assemblierung pro Absender-Transfer.
        private class IncomingPool
        {
            public string Sector;
            public int TotalChunks;
            public readonly byte[][] Chunks = null;
            public int Received;
            public float LastChunkTime;

            public IncomingPool(int totalChunks)
                => Chunks = new byte[Math.Max(totalChunks, 1)][];
        }

        private static readonly Dictionary<byte, IncomingPool> incoming = new();

        // Der aktuelle Pool (questId -> Anzeige-Text) + Meta.
        private static readonly Dictionary<string, string> pooledQuests = new();
        private static string pooledSector = null;
        private static int pooledVersion = -1;
        private static float lastPoolReceiveTime = -999f;

        // JobTaken-Queue (Frame-getrieben abgearbeitet).
        private static readonly Queue<string> pendingTaken = new();

        // ------------------------------------------------------------------
        // SENDEN (Upload)
        // ------------------------------------------------------------------
        public static void OnLocalJobsGenerated()
        {
            try
            {
                string skip = null;

                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected) skip = "client null / nicht verbunden";
                else
                {
                    string sector = StarTruckClient.currentSector;
                    if (string.IsNullOrEmpty(sector) || sector == "none") skip = "Sektor 'none'";
                    else
                    {
                        var liveJobs = global::ProceduralJobGenerator.GetAvailableJobs();
                        if (liveJobs == null || liveJobs.Count == 0) skip = $"Sektor '{sector}': keine Jobs";
                        else
                        {
                            // Nur senden, wenn sich der Inhalt seit dem letzten Upload geaendert hat
                            // (Post-Success-Dauerzustand des 3s-Polls — bewusst OHNE Log).
                            string hash = ComputeJobHash(liveJobs);
                            if (sector == lastUploadedSector && hash == lastUploadedHash)
                            {
                                // Build-364 (Re-Send-Netz): Hash-Gate normal skippen — AUSSER der
                                // Upload davor ist >15s her und seitdem kam kein Pool-Download
                                // an (letzter Pool-Empfang liegt vor dem Upload). Dann gehen wir
                                // von verlorenen Chunks aus und erlauben EIN Re-Send mit neuem
                                // transferId (max. 1x pro 15 s, kein Endlos-Spammen).
                                if (lastPoolReceiveTime < lastUploadTime
                                    && (Time.unscaledTime - lastUploadTime) > NO_POOL_RESEND_AFTER
                                    && Time.unscaledTime >= nextResendTime)
                                {
                                    // Build-368: Backoff 15/30/60/120s; nach 4 erfolglosen Re-Sends ganz
                                    // aufhoeren — ein Server, der nach 4+Uploads nie einen Pool schickt,
                                    // ist kaputt (Root-Cause 368: Wireformat-Mismatch, jetzt behoben),
                                    // und Endlos-Re-Sends spammen nur (Log 367).
                                    consecutiveResends++;
                                    if (consecutiveResends > MAX_CONSECUTIVE_RESENDS)
                                    {
                                        if (lastLoggedSkip == null || lastLoggedSkip != "resend-giveup")
                                        {
                                            lastLoggedSkip = "resend-giveup";
                                            StarTruckMP.Log.LogWarning($"JobBoardServerSync: {MAX_CONSECUTIVE_RESENDS} Re-Sends ohne Pool-Download fuer Sektor '{sector}' — Re-Send deaktiviert bis ein Pool ankommt.");
                                        }
                                        return;
                                    }
                                    nextResendTime = Time.unscaledTime + RESEND_MIN_INTERVAL * (1 << Math.Min(consecutiveResends - 1, 3));
                                    nextUploadAllowedTime = Time.unscaledTime + MIN_UPLOAD_INTERVAL;

                                    byte[] payload = SerializeJobs(liveJobs);
                                    int totalChunks = SendChunked((ushort)messageType.jobBoardUpload, sector, payload);
                                    lastUploadedSector = sector;
                                    lastUploadedHash = hash;
                                    lastUploadTime = Time.unscaledTime;
                                    lastLoggedSkip = null;
                                    StarTruckMP.Log.LogInfo($"JobBoardServerSync: Upload-Retry (Re-Send {consecutiveResends}/{MAX_CONSECUTIVE_RESENDS}, kein Pool-Download nach Upload): {liveJobs.Count} Jobs fuer Sektor '{sector}' ({payload.Length} bytes, {totalChunks} Chunks).");
                                    return;
                                }
                                return;
                            }
                            if (Time.unscaledTime < nextUploadAllowedTime) skip = $"Sektor '{sector}': Pace-Gate (1 Upload/{MIN_UPLOAD_INTERVAL}s)"; // Pace: 1 Upload/1.5s
                            else
                            {
                                nextUploadAllowedTime = Time.unscaledTime + MIN_UPLOAD_INTERVAL;

                                byte[] payload = SerializeJobs(liveJobs);
                                int chunksSent = SendChunked((ushort)messageType.jobBoardUpload, sector, payload);
                                lastUploadedSector = sector;
                                lastUploadedHash = hash;
                                lastUploadTime = Time.unscaledTime;
                                nextResendTime = Time.unscaledTime + RESEND_MIN_INTERVAL; // erst 15s nach dem Upload darf ein Re-Send pruefen
                                consecutiveResends = 0; // neuer Inhalt = neue Chance
                                lastLoggedSkip = null;
                                StarTruckMP.Log.LogInfo($"JobBoardServerSync: {liveJobs.Count} Jobs fuer Sektor '{sector}' hochgeladen ({payload.Length} bytes, {chunksSent}/{chunksSent} Chunks).");
                                return;
                            }
                        }
                    }
                }

                if (skip != null && skip != lastLoggedSkip)
                {
                    lastLoggedSkip = skip;
                    StarTruckMP.Log.LogInfo($"JobBoardServerSync: Upload-Retry skip: {skip}");
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardServerSync.OnLocalJobsGenerated fehlgeschlagen: {ex}");
            }
        }

        // Stabiler Inhalts-Hash: questIds in Board-Reihenfolge.
        private static string ComputeJobHash(Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            var sb = new StringBuilder();
            int n = jobs.Count;
            for (int i = 0; i < n; i++)
            {
                try { sb.Append(jobs[i]?.questId ?? "-"); sb.Append(';'); } catch { sb.Append("?;"); }
            }
            return sb.ToString();
        }

        // Explizite Binaer-Serialisierung NUR der Board-relevanten Felder.
        // QuestTaskParameterSaveData-Union wird NICHT uebertragen.
        // 343: Parameter-Tripel NICHT mehr mitschicken (paramCount=0) — BuildBody/Pool-Filter
        // lesen nur questId/displayName/displayDescription, Accept matcht lokal per questId
        // gegen die eigene QuestInstance. Params waren 85% der Upload-Groesse (169 Jobs:
        // 114 KB -> ~20 KB). Format bleibt kompatibel (paramCount je Job dynamisch gelesen,
        // alte 342-Clients mit Params decodieren weiterhin korrekt).
        //
        // BUILD-368 ROOT-CAUSE-FIX: BINARYWRITER.WRITE(STRING) schreibt die Laenge als
        // 7-BIT-VARINT (System.IO.BinarWriter-Kodierung), der Server (JobBoardCodec.
        // DecodeJobs, dedicated/JobBoardCodec.cs:119-124) liest die Laenge aber als
        // INT32-LE. Bei questId-Laengen < 128 ist der Varint 1 Byte vs. 4 Bytes gelesen
        // -> der Server-Parse explodiert/garbage, Merge wirft (JobBoardServer.HandleUpload
        // catch, dedicated/JobBoardServer.cs:126-129), BroadcastPool wird NIE erreicht ->
        // nie ein Pool-Download (Log 367: 'kein Pool-Download nach Upload' endlos).
        // Jetzt: String-Laenge explizit als int32-LE (BinWriter.Write(int) ist LE-4-Bytes,
        // identisch zu JobBoardCodec.WriteString) — Wireformat entspricht exakt dem Codec.
        private static byte[] SerializeJobs(Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int count = jobs.Count;
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                var job = jobs[i];
                string questId = "", displayName = "", displayDescription = "";
                try { questId = job?.questId ?? ""; } catch { }
                try { displayName = job?.displayName ?? ""; } catch { }
                try { displayDescription = job?.displayDescription ?? ""; } catch { }
                WriteNetString(w, questId);
                WriteNetString(w, displayName);
                WriteNetString(w, displayDescription);
                w.Write(0); // paramCount=0: Parameter werden vom Board/Accept nicht gelesen
            }
            return ms.ToArray();
        }

        // String im JobBoardCodec-Format: [int32-LE laenge][utf8-bytes].
        private static void WriteNetString(BinaryWriter w, string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            w.Write(bytes.Length);
            if (bytes.Length > 0) w.Write(bytes);
        }

        // Gemeinsamer Chunk-Sender (Upload). Format identisch zum ChunkedBlobTransfer-Relay:
        // [sector][senderId-Placeholder][transferId][chunkIndex][totalChunks][bytes]
        private static int SendChunked(ushort messageTypeId, string sector, byte[] payload)
        {
            var client = StarTruckClient.client;
            // Build-365: Sende-/Pacing-Logik in common/ChunkedSend extrahiert (headless
            // testbar, tests/JobBoardSmokeTest); Konstanten + Wireformat + Pacing sind
            // dort EINZIGE Quelle — Verhalten identisch zum Stand 364 (gleiche Chunk-
            // Groesse, gleiches Pacing, gleiche Message-Reihenfolge => gleiche Wire-Bytes).
            var stats = new global::StarTruckMP.Common.ChunkedSend.Stats();
            byte transferId = (byte)(UnityEngine.Random.Range(1, 250));
            return global::StarTruckMP.Common.ChunkedSend.SendChunked(
                messageTypeId, sector, client.Id, transferId, payload, m => client.Send(m), null, stats);
        }

        // ------------------------------------------------------------------
        // EMPFANGEN (Pool-Download)
        // ------------------------------------------------------------------
        public static void HandlePoolIncoming(MessageReceivedEventArgs e)
        {
            try
            {
                string sector = e.Message.GetString();
                e.Message.GetUShort(); // senderId-Placeholder (Server-Broadcast)
                byte transferId = e.Message.GetByte();
                ushort chunkIndex = e.Message.GetUShort();
                ushort totalChunks = e.Message.GetUShort();
                byte[] chunkData = e.Message.GetBytes();

                if (!incoming.TryGetValue(transferId, out var pool) || pool.Sector != sector || pool.TotalChunks != totalChunks)
                {
                    pool = new IncomingPool(totalChunks) { Sector = sector };
                    incoming[transferId] = pool;
                }
                if (chunkIndex >= pool.Chunks.Length) return;
                if (pool.Chunks[chunkIndex] == null)
                {
                    pool.Chunks[chunkIndex] = chunkData;
                    pool.Received++;
                }
                pool.LastChunkTime = Time.unscaledTime;
                if (pool.Received < totalChunks) return;

                // komplett: Payload assemblieren
                incoming.Remove(transferId);
                int payloadLen = 0;
                foreach (var c in pool.Chunks) payloadLen += c?.Length ?? 0;
                var payload = new byte[payloadLen];
                int off = 0;
                foreach (var c in pool.Chunks)
                {
                    if (c == null) continue;
                    Buffer.BlockCopy(c, 0, payload, off, c.Length);
                    off += c.Length;
                }

                ApplyPool(sector, payload);
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardServerSync.HandlePoolIncoming fehlgeschlagen: {ex}");
            }
        }

        private static void ApplyPool(string sector, byte[] payload)
        {
            var newPooled = new Dictionary<string, string>();
            int off = 0;
            int jobCount = BitConverter.ToInt32(payload, off); off += 4;
            int version = BitConverter.ToInt32(payload, off); off += 4;
            for (int j = 0; j < jobCount && off < payload.Length; j++)
            {
                string questId = ReadString(payload, ref off);
                string displayName = ReadString(payload, ref off);
                string displayDescription = ReadString(payload, ref off);
                int paramCount = BitConverter.ToInt32(payload, off); off += 4;
                off += paramCount * 0; // Parameter werden beim Anwenden ignoriert (Anzeige via lokale QuestInstance)
                // Params ueberspringen:
                for (int p = 0; p < paramCount; p++)
                {
                    ReadString(payload, ref off);
                    off += 1;
                    ReadString(payload, ref off);
                }
                if (!string.IsNullOrEmpty(questId))
                    newPooled[questId] = displayName;
            }

            bool versionRegress = sector == pooledSector && version < pooledVersion;
            pooledQuests.Clear();
            foreach (var kv in newPooled) pooledQuests[kv.Key] = kv.Value;
            pooledSector = sector;
            pooledVersion = version;
            lastPoolReceiveTime = Time.unscaledTime;
            consecutiveResends = 0; // Build-368: Pool funktioniert hat einmal -> Backoff-Zaehler reset
            StarTruckMP.Log.LogInfo($"JobBoardServerSync: Pool empfangen fuer Sektor '{sector}': {newPooled.Count} Jobs (v{version}, regress={versionRegress}).");
        }

        private static string ReadString(byte[] b, ref int off)
        {
            int len = BitConverter.ToInt32(b, off); off += 4;
            if (len <= 0 || off + len > b.Length) return "";
            var s = System.Text.Encoding.UTF8.GetString(b, off, len);
            off += len;
            return s;
        }

        // ------------------------------------------------------------------
        // JOB TAKEN
        // ------------------------------------------------------------------
        /// <summary>Ruft der Accept-Pfad auf, WENN wir verbunden sind (sonst nichts).</summary>
        public static void NotifyLocalJobAccepted(global::QuestInstance job)
        {
            try
            {
                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected || job == null) return;
                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;
                string questId;
                try { questId = job.questId ?? ""; } catch { return; }
                if (string.IsNullOrEmpty(questId)) return;

                var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.jobTaken);
                msg.AddString(sector);
                msg.AddString(questId);
                client.Send(msg);
                // Lokal sofort entfernen (kein Warten auf den Broadcast-Roundtrip).
                pooledQuests.Remove(questId);
                StarTruckMP.Log.LogInfo($"JobBoardServerSync: jobTaken gesendet ({questId}, Sektor '{sector}').");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardServerSync.NotifyLocalJobAccepted fehlgeschlagen: {ex}");
            }
        }

        public static void HandleJobTaken(MessageReceivedEventArgs e)
        {
            try
            {
                string sector = e.Message.GetString();
                string questId = e.Message.GetString();
                if (sector != StarTruckClient.currentSector) return;
                if (string.IsNullOrEmpty(questId)) return;
                pooledQuests.Remove(questId); // idempotent
                lastPoolReceiveTime = Time.unscaledTime;
                StarTruckMP.Log.LogInfo($"JobBoardServerSync: JobTaken empfangen ({questId}) — aus dem Board entfernt.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardServerSync.HandleJobTaken fehlgeschlagen: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // ABFRAGE (Board-Anzeige)
        // ------------------------------------------------------------------
        /// <summary>
        /// Liefert true, wenn ein frischer Server-Pool fuer diesen Sektor vorliegt —
        /// dann soll das Board NUR noch die gepoolten Jobs zeigen (Anzeige-Filter auf
        /// questId-Ebene; keine Spielobjekte werden angefasst).
        /// Ohne Pool (Solo/erstes Moment) null = lokale Liste unveraendert.
        /// </summary>
        public static bool HasFreshPool(string sector)
        {
            if (string.IsNullOrEmpty(sector)) return false;
            if (pooledSector != sector) return false;
            return (Time.unscaledTime - lastPoolReceiveTime) < 60f;
        }

        public static bool IsQuestPooled(string questId)
            => questId != null && pooledQuests.ContainsKey(questId);

        public static int PooledCount => pooledQuests.Count;

        /// <summary>Liefert die INDIZES der gepoolten Jobs in der Live-Liste (Board-Anzeige).</summary>
        public static List<int> FilterIndices(string sector, Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            if (!HasFreshPool(sector) || jobs == null) return null;
            var matches = new List<int>();
            int n = jobs.Count;
            for (int i = 0; i < n; i++)
            {
                string qid;
                try { qid = jobs[i]?.questId ?? ""; } catch { continue; }
                if (pooledQuests.ContainsKey(qid)) matches.Add(i);
            }
            return matches;
        }

        public static string BuildDiagLine(string sector, Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            try
            {
                bool fresh = HasFreshPool(sector);
                int mine = (jobs != null) ? jobs.Count : 0;
                int match = 0;
                if (fresh && jobs != null)
                {
                    int n = jobs.Count;
                    for (int i = 0; i < n; i++)
                    {
                        try { if (pooledQuests.ContainsKey(jobs[i]?.questId ?? "")) match++; } catch { }
                    }
                }
                string poolInfo = fresh
                    ? $"Pool v{pooledVersion}: {PooledCount} Jobs"
                    : (pooledSector != null ? $"Pool STALE (Sektor '{pooledSector}')" : "kein Pool");
                return $"ServerSync-Diag: {poolInfo} + lokal: {mine} Jobs + match: {match}/{mine}";
            }
            catch (Exception ex)
            {
                return $"ServerSync-Diag: (Fehler: {ex.Message})";
            }
        }

        // ------------------------------------------------------------------
        // WARTUNG (frame-budgetiert)
        // ------------------------------------------------------------------
        private const float INCOMING_TIMEOUT = 60f;

        public static void Update()
        {
            try
            {
                // Veraltete Incoming-Assemblierungen abraeumen (budgetiert, 1x/s reicht).
                if (incoming.Count > 0)
                {
                    var stale = new List<byte>();
                    foreach (var kv in incoming)
                        if (Time.unscaledTime - kv.Value.LastChunkTime > INCOMING_TIMEOUT) stale.Add(kv.Key);
                    foreach (var k in stale) incoming.Remove(k);
                }
                // Sektorwechsel: Pool des alten Sektors ist irrelevant.
                if (pooledSector != null && pooledSector != StarTruckClient.currentSector)
                {
                    pooledQuests.Clear();
                    pooledSector = null;
                    pooledVersion = -1;
                }
                // Build-362: Sektorwechsel setzt auch die Upload-Gates zurueck — ohne Reset
                // wuerde ein frisch betretener Sektor die letzte (Sektor, Hash)-Kombination
                // der Sende-Seite nicht tangieren, lastUploadedSector bleibt aber alt und
                // blockiert den ersten Upload im neuen Sektor nur zufaellig nie. Explizit:
                string curSector = StarTruckClient.currentSector;
                if (lastUploadedSector != null && lastUploadedSector != curSector)
                {
                    lastUploadedSector = null;
                    lastUploadedHash = null;
                    lastLoggedSkip = null;
                }
                // Build-362: Upload-Retry-Poll — der Einmal-Postfix auf
                // GenerateJobsForAllSectors verpasst das connected-Fenster, wenn die Jobs
                // bereits beim World-Load generiert wurden (VOR dem Client-Connect). Alle
                // RETRY_INTERVAL-Sekunden erneut versuchen; Guards machen es idempotent.
                if (Time.unscaledTime >= nextRetryPollTime)
                {
                    nextRetryPollTime = Time.unscaledTime + RETRY_INTERVAL;
                    OnLocalJobsGenerated();
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardServerSync.Update fehlgeschlagen: {ex}");
            }
        }

        public static void OnDisconnect()
        {
            incoming.Clear();
            pooledQuests.Clear();
            pooledSector = null;
            pooledVersion = -1;
            lastPoolReceiveTime = -999f;
            lastUploadedSector = null;
            lastUploadedHash = null;
            lastUploadTime = -999f;
            nextResendTime = 0f;
            consecutiveResends = 0;
            lastLoggedSkip = null;
        }

        // ------------------------------------------------------------------
        // ACCEPT-GUARD (Build-368, Prio 2)
        // ------------------------------------------------------------------
        /// <summary>
        /// True, wenn der Server-Pool fuer den Sektor NIE angekommen ist, obwohl wir
        /// mehrfach hochgeladen haben (>20s her, letzter Pool-Empfang liegt vor dem
        /// letzten Upload). In diesem defekten Zustand war die native Job-Annahme der
        /// Freeze-Ausloeser (Log 367: Malik nimmt Job an -> Spiel friert -> disconnect 3,
        /// vermutlich blocking Wait auf Pool-/Job-Daten, die nie ankommen). Der
        /// Accept-Prefix bricht die Annahme dann SAUBER ab (UI-Hinweis statt Freeze).
        /// Nach dem Wireformat-Fix (368) kommt der Pool an und der Guard bleibt passiv.
        /// </summary>
        public static bool PoolMissingAfterUpload(out string reason)
        {
            reason = null;
            try
            {
                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected) return false; // Solo: nie blockieren
                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return false;
                if (lastUploadedSector != sector || lastUploadTime <= -900f) return false;
                if (lastPoolReceiveTime >= lastUploadTime) return false; // Pool da
                if (Time.unscaledTime - lastUploadTime <= 20f) return false; // noch im Grace-Fenster
                reason = $"kein Server-Pool nach Upload ({(int)(Time.unscaledTime - lastUploadTime)}s, {consecutiveResends} Re-Sends) — Pool-Sync defekt";
                return true;
            }
            catch { return false; }
        }
    }
}
