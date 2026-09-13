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
    ///     "(questId|displayName)"-Strings. Kleine Message, direkt via Riptide
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
    /// </summary>
    public static class JobBoardIdSync
    {
        private const float IDENT_BROADCAST_INTERVAL = 2f;

        // Empfangene Kennungen je Sektor (vom Autoritaets-Client).
        private static readonly HashSet<string> receivedIdents = new HashSet<string>();
        private static string receivedIdentsSector = null;
        private static float lastReceiveTime = -999f;

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

            var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.jobBoardIdents);
            msg.AddString(sector ?? "");
            msg.AddUShort((ushort)idents.Count);
            for (int i = 0; i < idents.Count; i++) msg.AddString(idents[i] ?? "");
            try
            {
                client.Send(msg);
            }
            catch (Exception sendEx)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync: client.Send({trigger}) WIRF: {sendEx}");
                return;
            }

            StarTruckMP.Log.LogInfo($"JobBoardIdSync: {idents.Count} Kennungen fuer Sektor '{sector}' gesendet ({trigger}, MessageType={(ushort)messageType.jobBoardIdents}): [{string.Join(", ", idents)}]");
        }

        // ------------------------------------------------------------------
        // EMPFANGEN (alle Nicht-Autoritaeten)
        // ------------------------------------------------------------------
        public static void HandleIncoming(MessageReceivedEventArgs e)
        {
            try
            {
                string sector = e.Message.GetString();
                ushort n = e.Message.GetUShort();
                var idents = new HashSet<string>();
                for (int i = 0; i < n; i++)
                {
                    string s = e.Message.GetString();
                    if (!string.IsNullOrEmpty(s)) idents.Add(s);
                }

                receivedIdents.Clear();
                foreach (var id in idents) receivedIdents.Add(id);
                receivedIdentsSector = sector;
                lastReceiveTime = Time.unscaledTime;

                StarTruckMP.Log.LogInfo($"JobBoardIdSync: {idents.Count} Kennungen empfangen (Sektor '{sector}').");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardIdSync.HandleIncoming fehlgeschlagen (Sektor empfangen): {ex}");
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
        }
    }
}
