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
    /// Kennung (BuildJobKey): questId + displayName. Die questId eines prozedural
    /// generierten Jobs enthaelt typischerweise bereits die Template-Id; zusammen mit
    /// dem Anzeigenamen ist die Kombination in der Praxis eindeutig pro Sektor-Board.
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

        // Chunks-Puffer-Budget: Riptide MaxSize=1231 => pro Reliable-Message bleiben
        // nach Header+ID ~1200 Bytes. Sicherer Chunk-Grenzwert fuer die ident-Strings.
        private const int MAX_CHUNKS = 16;

        // Empfangene Kennungen je Sektor (vom Autoritaets-Client).
        private static readonly HashSet<string> receivedIdents = new HashSet<string>();
        private static string receivedIdentsSector = null;
        private static float lastReceiveTime = -999f;

        // Chunk-Sammel-Zustand (Empfangsseite, custom-build-335).
        private static string chunkSector = null;
        private static int chunkTotal = 0;
        private static int chunkTotalIdents = 0;
        private static int chunkNextIndex = 0;
        private static readonly HashSet<string> chunkIdents = new HashSet<string>();

        // Sende-Seite: Re-Broadcast-Timer (auch wenn keine neue Generierung feuert,
        // damit Spaetankoemmlinge die Kennungen noch bekommen - zusaetzlich zum
        // Re-Broadcast-Trigger bei Spielerankunft in Client.cs).
        private static float nextPeriodicSend = 0f;
        private static bool broadcastActiveLogged = false;

        public static string BuildJobKey(global::QuestInstance job)
        {
            if (job == null) return "";
            string qid, dn;
            try { qid = job.questId ?? ""; } catch { qid = ""; }
            try { dn = job.displayName ?? ""; } catch { dn = ""; }
            return "(" + qid + "|" + dn + ")";
        }

        public static string BuildJobKey(global::StarTruckSaveData.QuestInstanceSaveData job)
        {
            if (job == null) return "";
            string qid;
            try { qid = job.questParametersAsset ?? ""; } catch { qid = ""; }
            // QuestInstanceSaveData hat keinen displayName - Kennung hier nur aus den
            // vorhandenen Feldern; fuer den Ident-Vergleich auf Empfaengerseite wird
            // aber ohnehin der Live-QuestInstance-Key benutzt.
            return "(" + qid + "|?)";
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

        private static void SendIdents(string trigger, string sector, List<string> idents)
        {
            var client = StarTruckClient.client;
            if (client == null || !client.IsConnected)
            {
                StarTruckMP.Log.LogInfo($"JobBoardIdSync: Senden abgebrochen ({trigger}): client nicht verbunden");
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
                var entry = Encoding.UTF8.GetBytes((idents[i] ?? "") + "\n");
                if (curBytes + entry.Length > MAX_CHUNK_BYTES && cur.Count > 0)
                {
                    chunkPayloads.Add(Encoding.UTF8.GetBytes(string.Join("", cur)));
                    cur = new List<byte[]>();
                    curBytes = 0;
                }
                cur.Add(entry);
                curBytes += entry.Length;
            }
            if (cur.Count > 0) chunkPayloads.Add(Encoding.UTF8.GetBytes(string.Join("", cur)));

            if (chunkPayloads.Count > MAX_CHUNKS)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync: Senden abgebrochen ({trigger}): {idents.Count} Kennungen benoetigen {chunkPayloads.Count} Chunks (> {MAX_CHUNKS}) - Payload zu gross fuer einen Broadcast");
                return;
            }

            // Chunks senden - EINE Message pro Chunk, alles reliable.
            int sentChunks = 0;
            for (int ci = 0; ci < chunkPayloads.Count; ci++)
            {
                var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.jobBoardIdents);
                msg.AddString(sector ?? "");
                msg.AddInt(chunkPayloads.Count);  // totalChunks
                msg.AddInt(ci);                   // chunkIndex
                msg.AddInt(idents.Count);         // totalIdents (fuer Empfangs-Log)
                msg.AddBytes(chunkPayloads[ci], includeLength: false);
                try
                {
                    client.Send(msg);
                    sentChunks++;
                }
                catch (Exception sendEx)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardIdSync: client.Send({trigger}) Chunk {ci}/{chunkPayloads.Count} WIRF: {sendEx}");
                    return;
                }
            }

            // Verifikations-Gate (335): gesendet-Zeile mit Payload-Groesse.
            int payloadTotal = 0;
            foreach (var p in chunkPayloads) payloadTotal += p.Length;
            StarTruckMP.Log.LogInfo($"JobBoardIdSync: {idents.Count} Kennungen gesendet (payload={payloadTotal} bytes, {sentChunks}/{chunkPayloads.Count} Chunks, Sektor '{sector}', {trigger})");
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

                // Ein neuer Broadcast (Sektorwechsel / neue totalIdents / Chunk 0) startet
                // einen neuen Sammelvorgang.
                if (chunkSector != sector || chunkTotal != totalChunks || chunkTotalIdents != totalIdents || chunkIndex == 0)
                {
                    chunkSector = sector;
                    chunkTotal = totalChunks;
                    chunkTotalIdents = totalIdents;
                    chunkIdents.Clear();
                    chunkNextIndex = 0;
                }

                var text = Encoding.UTF8.GetString(payload);
                var parts = text.Split('\n');
                foreach (var p in parts)
                {
                    if (!string.IsNullOrEmpty(p)) chunkIdents.Add(p);
                }
                chunkNextIndex = chunkIndex + 1;

                if (chunkIndex + 1 < totalChunks)
                {
                    StarTruckMP.Log.LogInfo($"JobBoardIdSync: Chunk {chunkIndex + 1}/{totalChunks} empfangen (Sektor '{sector}', {chunkIdents.Count} Kennungen bisher)");
                    return;
                }

                // Alle Chunks da - uebernehmen.
                receivedIdents.Clear();
                foreach (var id in chunkIdents) receivedIdents.Add(id);
                receivedIdentsSector = sector;
                lastReceiveTime = Time.unscaledTime;

                StarTruckMP.Log.LogInfo($"JobBoardIdSync: {receivedIdents.Count} Kennungen empfangen (Sektor '{sector}', {totalChunks} Chunks, erwartet {totalIdents}).");
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
                SendCurrentIdents("periodic-2s");
            }
            catch (Exception ex)
            {
                // Auch der periodische Broadcast darf nie toeten - aber ERST NACH dem Log
                // (333: leerer catch => jede Exception in diesem Pfad war komplett lautlos,
                // genau deshalb erschien im Test-Log weder Send- noch Skip-Zeile).
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.Update(periodic) Fehler: {ex}");
            }
        }

        public static void OnDisconnect()
        {
            receivedIdents.Clear();
            receivedIdentsSector = null;
            lastReceiveTime = -999f;
            chunkSector = null;
            chunkTotal = 0;
            chunkTotalIdents = 0;
            chunkNextIndex = 0;
            chunkIdents.Clear();
        }
    }
}