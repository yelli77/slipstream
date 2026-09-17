using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Riptide;
using StarTruckMP.Utilities;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// PLAN B "Same-Seed lite" (custom-build-333): Kennungs-Broadcast + lokale Filterung.
    ///
    /// Hintergrund: die alten Sync-Wege (v1 Hand-Blob, v2 native FlatSharp ueber
    /// QuestInstanceSaveData-Restore) scheiterten am QuestTaskParameterSaveData-Union
    /// (Discriminator/Value-Dead-End) bzw. an Restore-Seiteneffekten (Doppel-Jobs,
    /// nicht auflösbare Params). Dieser Weg schickt GAR KEINE Job-Objekte mehr:
    ///
    ///  1. Jeder Client generiert seine Jobs lokal wie vom Spiel vorgesehen
    ///     (ProceduralJobGenerator bleibt unangetastet - keine Injektion, kein Restore).
    ///  2. Der Autoritaets-Client (niedrigste Spieler-ID im Sektor, JobBoardSync.
    ///     IsAuthorityForCurrentSector) broadcastet NUR die Kennungen seiner Jobs:
    ///     "(questId|displayName)"-Strings. Chunks direkt via Riptide
    ///     (kein ChunkedBlobTransfer noetig).
    ///  3. Nicht-Autoritaets-Clients FILTERN ihre eigene GetAvailableJobs()-Liste
    ///     genau auf diese Kennungen (Anzeige-/Board-Ebene, keine Spielobjekte
    ///     werden angefasst). Ziel: alle Spieler sehen dieselbe Jobliste pro Sektor,
    ///     fuer Coop-Missionen.
    ///
    /// Kennung (BuildJobKey, custom-build-339): NUR questId (UUID). Die questId eines
    /// prozedural generierten Jobs ist sprachunabhaengig und auf allen Clients identisch.
    /// Der fruehere Key "questId|displayName" enthielt den LOKALISIERTEN Anzeigenamen -
    /// deutsch- und englischsprachige Clients bauten damit unterschiedliche Keys und der
    /// Match war immer 0 (Build-339-Log-Beweis). UUID-only ist eindeutig pro Sektor-Board.
    /// (QuestInstanceSaveData.id existiert als Feld, wird hier aber NICHT benutzt,
    /// weil wir keine SaveData-Objekte mehr austauschen - und die QuestInstance-
    /// Wrapper-Klasse keinen id-Getter exponiert. Der Key ist bewusst NUR ein
    /// Anzeige-Filter-Kriterium, kein Restore-Schluessel.)
    ///
    /// DIAGNOSE ( Build-333, Michael-Test): beim Board-Öffnen loggt JobBoardComputer
    /// 'meine Jobs: [...] + empfangene Kennungen: [...] + match: X/Y'.
    ///
    /// custom-build-335: Riptide.InsufficientCapacityException behoben. Verifiziert
    /// gegen die verbauten Riptide 2.2-Assemblies (ilspycmd): Message.MaxSize ist ein
    /// HARDES Limit von 1231 Bytes (MaxPayloadSize 1225) und Message.Create() hat
    /// KEINEN Groesse-Parameter - 61 Kennungen (~2.2KB) passen schlicht nicht in EINE
    /// Message. Loesung: der Payload wird in Chunks aufgeteilt. Format:
    ///   Chunk 0: [sector][totalChunks][totalIdents] [chunk-idents...]
    ///   Chunk k>0: [chunk-idents...]
    /// Der Empfangler sammelt Chunks bis totalChunks erreicht sind. Reset bei
    /// Sektorwechsel/Timeout (60s wie HasFreshIdents) ist implizit: empfangene
    /// Teilmengen werden erst nach Completion uebernommen.
    /// </summary>
    public static class JobBoardIdSync
    {
        private const float IDENT_BROADCAST_INTERVAL = 2f;

        // Build-341 Diagnose: meldet (gedrosselt), wenn komplette Kennungs-Serien mit
        // FREMDEM Sektor-Tag ankommen. Genau das Muster ('stale, Sektor X' + match 0/N)
        // entsteht, wenn zwei Clients jeweils glauben, Authority "ihres" Sektor-Tags zu
        // sein (Split-Brain: currentSector eines Clients ist stale oder die Sektor-
        // Beliefs der playerList laufen auseinander). Das Log zeigt dann BEIDE Tags.
        private static float nextForeignSeriesWarn = 0f;
        private static string lastForeignTag = null;
        private static int foreignSeriesCount = 0;

        // Empfangene Kennungen je Sektor (vom Autoritaets-Client).
        private static readonly HashSet<string> receivedIdents = new HashSet<string>();
        private static string receivedIdentsSector = null;
        private static float lastReceiveTime = -999f;

        // custom-build-338 (Fix BEFUND 3): Sammel-Puffer KEYED statt globaler Reset.
        // Bei 2s-Broadcast + Pacing ueberlappen sich Serien - ein globaler Reset bei
        // chunkIndex==0 oder totalIdents-Aenderung hat die laufende Serie verworfen.
        // Key: sector|totalChunks|totalIdents. Alte Keys (>60s ohne Chunk) werden
        // verworfen (Cleanup in Update()).
        private class ChunkAssembly
        {
            public string Sector;
            public int TotalChunks;
            public int TotalIdents;
            public readonly HashSet<string> Idents = new HashSet<string>();
            public readonly bool[] HaveChunks;
            public float LastChunkTime;

            public ChunkAssembly(int totalChunks)
            {
                HaveChunks = new bool[Math.Max(totalChunks, 1)];
            }

            public bool Complete
            {
                get
                {
                    for (int i = 0; i < HaveChunks.Length; i++)
                        if (!HaveChunks[i]) return false;
                    return true;
                }
            }
        }

        private static readonly Dictionary<string, ChunkAssembly> chunkAssemblies = new Dictionary<string, ChunkAssembly>();

        private static string AssemblyKey(string sector, int totalChunks, int totalIdents)
            => sector + "|" + totalChunks + "|" + totalIdents;

        private const float ASSEMBLY_TIMEOUT = 60f;

        private static void CleanupStaleAssemblies()
        {
            var staleKeys = new List<string>();
            foreach (var kv in chunkAssemblies)
            {
                if (Time.unscaledTime - kv.Value.LastChunkTime > ASSEMBLY_TIMEOUT)
                    staleKeys.Add(kv.Key);
            }
            foreach (var k in staleKeys)
            {
                chunkAssemblies.Remove(k);
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: veraltete Sammel-Serie verworfen (Key '{k}', Timeout {ASSEMBLY_TIMEOUT}s)");
            }
        }

        // Sende-Seite: Re-Broadcast-Timer (auch wenn keine neue Generierung feuert,
        // damit Spaetankoemmlinge die Kennungen noch bekommen - zusaetzlich zum
        // Re-Broadcast-Trigger bei Spielerankunft in Client.cs).
        private static float nextPeriodicSend = 0f;
        // Build-362: einmaliges Deaktivierungs-Log (statt 2s-Broadcast-Beweis).
        private static bool periodicDeactivatedLogged = false;
        private static bool broadcastActiveLogged = false;

        // custom-build-339: questId-only (sprachunabhaengig). Der displayName ist
        // LOKALISIERT und machte die Kennung sprachabhaengig -> Keys matchten nie
        // ueber verschiedene Client-Sprachen hinweg.
        public static string BuildJobKey(global::QuestInstance job)
        {
            if (job == null) return "";
            string qid;
            try { qid = job.questId ?? ""; } catch { qid = ""; }
            return "(" + qid + ")";
        }

        public static string BuildJobKey(global::StarTruckSaveData.QuestInstanceSaveData job)
        {
            if (job == null) return "";
            string qid;
            try { qid = job.questParametersAsset ?? ""; } catch { qid = ""; }
            // QuestInstanceSaveData hat keinen displayName - Kennung hier nur aus den
            // vorhandenen Feldern; fuer den Ident-Vergleich auf Empfaengerseite wird
            // aber ohnehin der Live-QuestInstance-Key benutzt.
            return "(" + qid + ")";
        }

        // ------------------------------------------------------------------
        // SENDEN (nur Autoritaet)
        // ------------------------------------------------------------------
        public static void OnLocalJobsGenerated()
        {
            try
            {
                SendCurrentIdents("OnLocalJobsGenerated");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.OnLocalJobsGenerated fehlgeschlagen: {ex}");
            }
        }

        // Sendet die Kennungen der aktuellen Live-Jobliste. Gibt die Anzahl gesendeter
        // Kennungen zurueck (-1 = uebersprungen mit Grund im Log). Alle Skip-Gruende
        // werden GELOGGT (Build-334-Diagnose: im 333-Test-Log erschien KEINE einzige
        // Send-Zeile und auch kein Skip-Grund - der Pfad starb still).
        private static int SendCurrentIdents(string trigger)
        {
            var client = StarTruckClient.client;
            if (client == null || !client.IsConnected)
            {
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden uebersprungen ({trigger}): client={client != null} connected={(client != null && client.IsConnected)}");
                return -1;
            }
            if (!JobBoardSync.IsAuthorityForCurrentSector())
            {
                // Einmal pro Sekunde reicht als Diagnose - der periodische Aufruf laeuft alle 2s.
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden uebersprungen ({trigger}): nicht Authority im Sektor '{StarTruckClient.currentSector}' (myId={StarTruckClient.client.Id}, Spieler im Sektor: {AuthorityDiagLine()})");
                return -1;
            }

            var liveJobs = global::ProceduralJobGenerator.GetAvailableJobs();
            if (liveJobs == null)
            {
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden uebersprungen ({trigger}): GetAvailableJobs()=null");
                return -1;
            }

            int count = liveJobs.Count;
            var idents = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                try { idents.Add(BuildJobKey(liveJobs[i])); } catch { }
            }

            SendIdents(trigger, StarTruckClient.currentSector, idents);
            return idents.Count;
        }

        private static string AuthorityDiagLine()
        {
            var parts = new List<string>();
            foreach (var kv in StarTruckClient.playerList)
                parts.Add($"{kv.Key}:{kv.Value.sector ?? "?"}");
            return string.Join(", ", parts);
        }

        // custom-build-338 (Payload-Fix): byte-level Konkatenation statt string.Join
        // (das rief byte[].ToString() auf -> 'System.Byte[]'-Muell im Payload).
        private static byte[] ConcatBytes(List<byte[]> parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var buf = new byte[total];
            int off = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, buf, off, p.Length);
                off += p.Length;
            }
            return buf;
        }

        private static void SendIdents(string trigger, string sector, List<string> idents)
        {
            var client = StarTruckClient.client;
            if (client == null || !client.IsConnected)
            {
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden abgebrochen ({trigger}): client nicht verbunden");
                return;
            }

            // Build-341: KEIN Broadcast mit leerem/'none'-Sektor-Tag. Ein solcher Tag war
            // im l3/l5-Muster die Ursache fuer 'stale, Sektor X' + match 0/N auf den
            // Empfaengern - die Serie war mit dem falschen Sektor markiert und wurde
            // dort (zu Recht) verworfen.
            if (string.IsNullOrEmpty(sector) || sector == "none")
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync: Senden abgebrochen ({trigger}): currentSector='{sector}' - kein Broadcast mit ungueltigem Sektor-Tag");
                return;
            }

            if (idents == null || idents.Count == 0)
            {
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden abgebrochen ({trigger}): 0 Kennungen");
                return;
            }

            // Payload-Groesse messen (UTF8-Bytes) und in Chunks aufteilen. Chunks sind
            // WIRKLICH klein (EINE Message = max ~1200 Bytes Payload): JEDER Chunk ist
            // eine eigene Message mit eigene sector/count/total-Header, damit das Relay
            // und der Empfaenger keine partial-Felder mehr misvertstehen (334-Fehler).
            // Das per-Chunk-Limit liegt konservativ bei 900 Bytes Nutzdaten (Riptide
            // MaxPayloadSize=1225 - Header + VarULong-Overhead bleiben Reserve).
            const int MAX_CHUNK_BYTES = 900;

            var chunkPayloads = new List<byte[]>();
            var cur = new List<byte[]>();
            int curBytes = 0;
            for (int i = 0; i < idents.Count; i++)
            {
                var entry = System.Text.Encoding.UTF8.GetBytes((idents[i] ?? "") + "\n");
                if (curBytes + entry.Length > MAX_CHUNK_BYTES && cur.Count > 0)
                {
                    chunkPayloads.Add(ConcatBytes(cur));
                    cur = new List<byte[]>();
                    curBytes = 0;
                }
                cur.Add(entry);
                curBytes += entry.Length;
            }
            if (cur.Count > 0) chunkPayloads.Add(ConcatBytes(cur));

            // custom-build-338 (Payload-Fix): Sicherheit vor dem Senden - der fertige
            // Chunk-Payload muss als UTF8 decodierbar sein und darf NIEMALS 'System.Byte[]'
            // enthalten (das war der 335er-Bug: string.Join ueber byte[] rief ToString()
            // auf). Validierung 1x pro Broadcast (nicht pro Chunk), LogError bei Fehlschlag.
            bool byteGarbage = false;
            foreach (var p in chunkPayloads)
            {
                string decoded = System.Text.Encoding.UTF8.GetString(p);
                if (decoded.Contains("System.Byte[]")) { byteGarbage = true; break; }
            }
            if (byteGarbage)
                StarTruckMP.Log.LogError($"JobBoardIdSync: Chunk-Payload enthaelt 'System.Byte[]' (Sendefehler) - Broadcast abgebrochen ({trigger}, {idents.Count} Kennungen, Sektor '{sector}')");
            else
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Payload-Validierung OK (kein 'System.Byte[]', {chunkPayloads.Count} Chunks, {trigger})");
            if (byteGarbage) return;

            // custom-build-335-Nachbesserung: KEIN Chunk-Cap mehr. Das alte MAX_CHUNKS=16
            // hat den GESAMTEN Broadcast verworfen, sobald 215-228 Kennungen 17-18 Chunks
            // benoetigten (3.477x 'Senden abgebrochen ... > 16' im Live-Log). Die Empfangs-
            // seite sammelt dynamisch per totalChunks-Feld (HandleIncoming), sie hat nie
            // ein 16er-Limit - mehr Chunks zu senden ist also jederzeit sicher.

            // Chunks senden - EINE Message pro Chunk, alles reliable, mit Pacing
            // (custom-build-338, Fix BEFUND 2): Chunks landen in pendingSends und werden
            // von Update() mit max. MAX_CHUNKS_PER_FRAME pro Frame versendet.
            int chunkCount = chunkPayloads.Count;
            for (int ci = 0; ci < chunkCount; ci++)
            {
                pendingSends.Enqueue(new PendingChunk
                {
                    Payload = chunkPayloads[ci],
                    Sector = sector ?? "",
                    TotalChunks = chunkCount,
                    ChunkIndex = ci,
                    TotalIdents = idents.Count,
                });
            }

            // Verifikations-Gate (335): gesendet-Zeile mit Payload-Groesse.
            int payloadTotal = 0;
            foreach (var p in chunkPayloads) payloadTotal += p.Length;
            StarTruckMP.Log.LogInfo($"JobBoardIdSync: {idents.Count} Kennungen in Send-Queue (payload={payloadTotal} bytes, {chunkCount} Chunks, Sektor '{sector}', {trigger})");
        }

        // custom-build-338 (Fix BEFUND 2): Send-Pacing wie ChunkedBlobTransfer -
        // max. MAX_CHUNKS_PER_FRAME reliable Chunks pro Frame. 17-18 Chunks in einem
        // Frame scheitern am Reliable-Burst (letzter Chunk kam beim Empfaenger nie an).
        private const int MAX_CHUNKS_PER_FRAME = 15;

        private class PendingChunk
        {
            public byte[] Payload;
            public string Sector;
            public int TotalChunks;
            public int ChunkIndex;
            public int TotalIdents;
        }

        private static readonly Queue<PendingChunk> pendingSends = new Queue<PendingChunk>();

        private static void SendPendingChunks()
        {
            var client = StarTruckClient.client;
            int budget = MAX_CHUNKS_PER_FRAME;
            while (pendingSends.Count > 0 && budget-- > 0)
            {
                var pc = pendingSends.Peek();
                if (client == null || !client.IsConnected)
                {
                    // Verbindung weg - Queue verwerfen (nicht mehr benoetigt, naechster
                    // 2s-Broadcast baut neu auf).
                    pendingSends.Clear();
                    return;
                }
                pendingSends.Dequeue();
                var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.jobBoardIdents_deactivated_DO_NOT_USE);
                msg.AddString(pc.Sector);
                msg.AddInt(pc.TotalChunks);
                msg.AddInt(pc.ChunkIndex);
                msg.AddInt(pc.TotalIdents);
                msg.AddBytes(pc.Payload, includeLength: true);
                try
                {
                    client.Send(msg);
                }
                catch (Exception sendEx)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardIdSync: client.Send Chunk {pc.ChunkIndex}/{pc.TotalChunks} WIRF: {sendEx}");
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // EMPFANGEN (alle Nicht-Autoritaeten) - chunked, custom-build-335
        // ------------------------------------------------------------------
        public static void HandleIncoming(MessageReceivedEventArgs e)
        {
            try
            {
                string sector = e.Message.GetString();
                int totalChunks = e.Message.GetInt();
                int chunkIndex = e.Message.GetInt();
                int totalIdents = e.Message.GetInt();
                byte[] payload = e.Message.GetBytes();

                // Build-341 (Kernfix): Sektor-Guard. Empfangene Serien mit einem ANDEREN
                // Sektor-Tag als unserem aktuellen Sektor werden VOR dem Sammeln komplett
                // verworfen. Vorher landeten fremd-getaggte Serien im Sammel-Puffer, wurden
                // dort komplett, ueberschrieben dann receivedIdents (mit receivedIdentsSector
                // = fremder Sektor) und produzierten exakt das l3/l5-Muster 'stale, Sektor X'
                // + match 0/N: jeder Client behielt seine eigene lokale Liste.
                string mySector = StarTruckClient.currentSector;
                if (!string.Equals(sector, mySector, System.StringComparison.Ordinal))
                {
                    foreignSeriesCount++;
                    if (Time.unscaledTime >= nextForeignSeriesWarn)
                    {
                        StarTruckMP.Log.LogWarning($"JobBoardIdSync: Serie mit fremdem Sektor-Tag verworfen (empfangen='{sector}', eigener Sektor='{mySector}', totalIdents={totalIdents}, verworfene Serien seit Start={foreignSeriesCount}) - NICHT angewendet");
                        nextForeignSeriesWarn = Time.unscaledTime + 10f;
                        lastForeignTag = sector;
                    }
                    return;
                }

                string key = AssemblyKey(sector, totalChunks, totalIdents);
                if (!chunkAssemblies.TryGetValue(key, out var asm))
                {
                    asm = new ChunkAssembly(totalChunks)
                    {
                        Sector = sector,
                        TotalChunks = totalChunks,
                        TotalIdents = totalIdents,
                    };
                    chunkAssemblies[key] = asm;
                }

                if (chunkIndex < 0 || chunkIndex >= asm.HaveChunks.Length)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardIdSync: Chunk-Index {chunkIndex} ausserhalb (totalChunks={totalChunks}, Sektor '{sector}') - Chunk verworfen");
                    return;
                }

                var text = System.Text.Encoding.UTF8.GetString(payload);
                var parts = text.Split('\n');
                // custom-build-338 (Payload-Fix): defensiv auf Empfaengerseite - ein Ident
                // der Form 'System.Byte[]' oder ohne '|' bzw. ohne Klammern ist Muell
                // (Sender-Bug) und wird verworfen. LogWarning nur 1x pro Serie.
                bool warnedGarbage = false;
                foreach (var p in parts)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    // custom-build-339: questId-only Keys. ALTES Format "(qid|Name)" von
                    // evtl. noch alten Clients defensiv tolerieren: nur der questId-Teil
                    // vor '|' landet im Set. Neues Format "(qid)" enthaelt kein '|'.
                    bool isGarbage = p.Contains("System.Byte[]") || !p.StartsWith("(") || !p.EndsWith(")");
                    if (isGarbage)
                    {
                        if (!warnedGarbage)
                        {
                            warnedGarbage = true;
                            StarTruckMP.Log.LogWarning($"JobBoardIdSync: ungueltige Kennung empfangen (Sektor '{sector}', Chunk {chunkIndex}/{totalChunks}) - verworfen: '{p.Substring(0, Math.Min(40, p.Length))}'");
                        }
                        continue;
                    }
                    string body = p.Substring(1, p.Length - 2);
                    int pipe = body.IndexOf('|');
                    string qidOnly = pipe >= 0 ? body.Substring(0, pipe) : body;
                    if (string.IsNullOrEmpty(qidOnly)) continue;
                    asm.Idents.Add("(" + qidOnly + ")");
                }
                asm.HaveChunks[chunkIndex] = true;
                asm.LastChunkTime = Time.unscaledTime;

                if (!asm.Complete)
                {
                    // custom-build-338: Log gedrosselt - nur jeder 5. Chunk (plus Chunk 1)
                    // statt jeder Chunk (21439 Zeilen im 337er-Log).
                    int haveCount = 0;
                    for (int i = 0; i < asm.HaveChunks.Length; i++) if (asm.HaveChunks[i]) haveCount++;
                    bool logThis = haveCount <= 1 || haveCount % 5 == 0;
                    if (logThis)
                        StarTruckMP.Log.LogInfo($"JobBoardIdSync: Chunk {haveCount}/{totalChunks} empfangen (Sektor '{sector}', {asm.Idents.Count} Kennungen bisher)");
                    return;
                }

                // Alle Chunks der Serie da - uebernehmen und Serie aus dem Keyed-Store
                // entfernen (damit der naechste identische Broadcast neu sammeln kann).
                receivedIdents.Clear();
                foreach (var id in asm.Idents) receivedIdents.Add(id);
                receivedIdentsSector = sector;
                lastReceiveTime = Time.unscaledTime;
                chunkAssemblies.Remove(key);

                if (receivedIdents.Count != totalIdents)
                {
                    // custom-build-338: Apply nur bei count==totalIdents, sonst Log+discard.
                    // (Bei deduplizierten Kennungen kann der Satz kleiner sein - dann gilt
                    // der empfangene Satz als gueltig, wir loggen die Abweichung nur.)
                    StarTruckMP.Log.LogInfo($"JobBoardIdSync: {receivedIdents.Count} Kennungen empfangen (Sektor '{sector}', {totalChunks} Chunks, erwartet {totalIdents}) - Abweichung geloggt, Satz uebernommen");
                }
                else
                {
                    StarTruckMP.Log.LogInfo($"JobBoardIdSync: {receivedIdents.Count} Kennungen empfangen (Sektor '{sector}', {totalChunks} Chunks, erwartet {totalIdents}).");
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.HandleIncoming fehlgeschlagen: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // ABFRAGE (JobBoardComputer-Anzeige)
        // ------------------------------------------------------------------
        public static bool HasFreshIdents(string sector)
        {
            if (sector == null) return false;
            return receivedIdentsSector == sector
                && (Time.unscaledTime - lastReceiveTime) < 60f;
        }

        public static HashSet<string> GetReceivedIdents()
        {
            return receivedIdents;
        }

        /// <summary>
        /// Filtert die uebergebene Live-Liste auf die empfangenen Kennungen.
        /// Liefert die INDIZES der Treffer in der urspruenglichen Reihenfolge.
        /// Ohne frische Kennungen (oder als Autoritaet) wird null geliefert =
        /// "Liste unveraendert anzeigen".
        /// </summary>
        public static List<int> FilterIndices(string sector, Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            if (!HasFreshIdents(sector)) return null;
            if (JobBoardSync.IsAuthorityForCurrentSector()) return null;
            if (jobs == null) return null;

            var matches = new List<int>();
            for (int i = 0; i < jobs.Count; i++)
            {
                string key;
                try { key = BuildJobKey(jobs[i]); } catch { continue; }
                if (receivedIdents.Contains(key)) matches.Add(i);
            }
            return matches;
        }

        // Diagnose-Zeile fuer das Board (Michael-Test): 'meine Jobs: [...] +
        // empfangene Kennungen: [...] + match: X/Y'.
        public static string BuildDiagLine(string sector, Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            try
            {
                var mine = new List<string>();
                int jc = (jobs != null) ? jobs.Count : 0;
                for (int i = 0; i < jc; i++)
                {
                    try { mine.Add(BuildJobKey(jobs[i])); } catch { }
                }
                bool fresh = HasFreshIdents(sector);
                string recv = fresh
                    ? "[" + string.Join(", ", GetReceivedIdents()) + "]"
                    : (receivedIdentsSector != null ? $"[stale, Sektor '{receivedIdentsSector}']" : "[keine]");

                int match = 0;
                if (fresh)
                {
                    var recvSet = GetReceivedIdents();
                    for (int i = 0; i < jc; i++)
                    {
                        try { if (recvSet.Contains(BuildJobKey(jobs[i]))) match++; } catch { }
                    }
                }
                return $"JobSync-Diag: meine Jobs: [{string.Join(", ", mine)}] + empfangene Kennungen: {recv} + match: {match}/{jc}";
            }
            catch (Exception ex)
            {
                return $"JobSync-Diag: (Fehler: {ex.Message})";
            }
        }

        // Periodisches Re-Send (Autoritaet), damit ein Sektor-Spaetkommer die
        // Kennungen auch ohne frischen Generierungs-Trigger bekommt. Aufruf aus
        // StarTruckClient.Update() (Patch PauseController.Update).
        public static void Update()
        {
            try
            {
                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected) return;
                if (!broadcastActiveLogged)
                {
                    // Einmaliger Beweis, dass der periodische Broadcast-Pfad lebt.
                    broadcastActiveLogged = true;
                    StarTruckMP.Log.LogInfo($"JobBoardIdSync: periodic job idents broadcast active (Interval={IDENT_BROADCAST_INTERVAL}s, myId={client.Id}, Sektor='{StarTruckClient.currentSector}')");
                }
                if (Time.unscaledTime < nextPeriodicSend) return;
                nextPeriodicSend = Time.unscaledTime + IDENT_BROADCAST_INTERVAL;
                // Build-362: periodischer Kennungs-Broadcast DEAKTIVIERT — ersetzt durch den
                // server-authoritativen Pool (JobBoardServerSync, Message 19). Der Ticker lief
                // bisher auch nach der 342er-Deaktivierung weiter (eigener 2s-Ticker) und hat
                // konkurrierende jobBoardIdents-Sets im Umlauf gehalten. Nummer 18 reserviert.
                if (!periodicDeactivatedLogged)
                {
                    periodicDeactivatedLogged = true;
                    StarTruckMP.Log.LogInfo("JobBoardIdSync: periodic idents broadcast deaktiviert (Build-362, server-authoritativer Pool).");
                }
                // SendCurrentIdents("periodic-2s");
            }
            catch (Exception ex)
            {
                // Auch der periodische Broadcast darf nie toeten - aber ERST NACH dem Log
                // (333: leerer catch => jede Exception in diesem Pfad war komplett lautlos,
                // genau deshalb erschien im Test-Log weder Send- noch Skip-Zeile).
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.Update(periodic) Fehler: {ex}");
            }
        }

        // custom-build-338: Frame-getriebener Pfad (ruft Client.FixedUpdate/Update auf) -
        // versendet gepacede Chunks und raeumt veraltete Sammel-Serien auf.
        public static void FixedUpdate()
        {
            try
            {
                if (pendingSends.Count > 0) SendPendingChunks();
                CleanupStaleAssemblies();
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.FixedUpdate Fehler: {ex}");
            }
        }

        public static void OnDisconnect()
        {
            receivedIdents.Clear();
            receivedIdentsSector = null;
            lastReceiveTime = -999f;
            chunkAssemblies.Clear();
            pendingSends.Clear();
        }
    }
}