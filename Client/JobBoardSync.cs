using System;
using System.IO;
using System.Collections.Generic;
using Riptide;
using StarTruckMP.Utilities;
using HarmonyLib;
using StarTruckSaveData;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Synchronisiert das Job-Board (ProceduralJobGenerator.availableJobs) zwischen allen
    /// Spielern im selben Sektor. Ansatz "Host-autoritativ + Broadcast":
    ///
    /// - Der dedizierte Server fuehrt selbst keine Unity/Job-Logik aus (siehe dedicated/*.cs,
    ///   reiner .NET-Konsolen-Relay) und kann daher nicht selbst Jobs generieren. Autoritaet
    ///   ist deshalb ueber Konvention geregelt: wer die niedrigste Spieler-ID unter allen
    ///   Spielern im selben Sektor hat (inkl. sich selbst), gilt als "Host" fuer dieses Sektor-
    ///   Jobboard.
    /// - Wenn ProceduralJobGenerator lokal neue Jobs generiert hat (Postfix auf
    ///   GenerateJobsForAllSectors) und wir die Autoritaet fuer den aktuellen Sektor sind,
    ///   lesen wir QuestTracker.GetData(...).QuestSaveData.availableJobs aus - exakt dieselbe
    ///   Struktur (QuestInstanceSaveData/QuestTaskParameterSaveData), die das Spiel auch fuers
    ///   eigene Speichersystem verwendet - und schicken sie ueber jobBoardSync an den Server,
    ///   der sie an alle anderen Spieler im selben Sektor weiterleitet (reiner Relay).
    /// - Empfangende Clients (die NICHT selbst Autoritaet sind) rufen
    ///   QuestTracker.RestoreAvailableJobs(...) auf - denselben Pfad, den auch ein normaler
    ///   Spielstand-Load benutzt, statt den ProceduralJobGenerator-State von Hand zu basteln.
    ///
    /// WICHTIG: die Save-Datentypen (QuestInstanceSaveData, QuestTaskParameterSaveData, ...)
    /// verwenden IL2CPP-Collections (Il2CppSystem.Collections.Generic.List/IList), keine
    /// normalen .NET-Listen - deshalb ueberall explizite Il2Cpp-Listen bauen und beim Lesen
    /// indexbasiert statt mit foreach/LINQ zugreifen (IL2CPP-IList-Interfaces liefern keinen
    /// GetEnumerator).
    ///
    /// NOCH UNVERIFIZIERT (erster Test im Spiel noetig):
    /// - Ob Nullable&lt;SystemSaveData&gt; im IL2CPP-Interop sich wie ein normales C#
    ///   Nullable&lt;T&gt; verhaelt (HasValue/Value). Falls nicht: Fehler landet im Log,
    ///   OnLocalJobsGenerated bricht sauber ab, nichts crasht.
    /// - Ob `new QuestSaveData()` / `new QuestInstanceSaveData()` / `new IntValue()` etc. aus
    ///   gemanagtem Code funktionieren (uebliches Il2CppInterop-Pattern fuer Save-Datentypen,
    ///   aber im Mod bisher nicht fuer diese Art Typ verwendet).
    /// - 8 "komplexe" Parameter-Varianten (cargoProperties, cargoType, conversation,
    ///   inventoryItemTags, quest, ventureLocation, ventureType, ventureJobType) sind NICHT
    ///   implementiert - Jobs, die diese nutzen, werden unvollstaendig synchronisiert (Log
    ///   durchsuchen nach "nicht unterstuetzte Parameter").
    /// </summary>
    public static class JobBoardSync
    {
        // IL2CPP-Interop: IList<T>-Referenzen aus Save-Datentypen haben kein direktes .Count -
        // das liegt nur auf ICollection<T> (und auf der konkreten List<T>). Kleiner Helper statt
        // ueberall Casts zu wiederholen.
        private static int Il2CppCount<T>(Il2CppSystem.Collections.Generic.IList<T> list)
        {
            if (list == null) return 0;
            return list.Cast<Il2CppSystem.Collections.Generic.ICollection<T>>().Count;
        }

        public static void OnLocalJobsGenerated()
        {
            try
            {
                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected) return;
                if (!IsAuthorityForCurrentSector()) return;

                if (!QuestTracker.ready)
                {
                    StarTruckMP.Log.LogInfo("JobBoardSync: QuestTracker noch nicht ready, ueberspringe (naechster Trigger holt es nach).");
                    return;
                }

                // QuestTracker.GetData(context)/SystemSaveData-Union ist NICHT fuer den Live-
                // Gebrauch gedacht (wirft InvalidOperationException ausserhalb des eigentlichen
                // Speicher-Vorgangs - siehe Log von custom-build-176). Direkter, sauberer Weg
                // ohne Umweg ueber den Save-Provider-Mechanismus: ProceduralJobGenerator haelt
                // die Live-Jobliste selbst, und jede QuestInstance kann sich per GetData() (ganz
                // ohne Kontext/Parameter) direkt in ihre QuestInstanceSaveData umwandeln.
                var generator = ProceduralJobGenerator.Get();
                if (generator == null) return;

                var liveJobs = ProceduralJobGenerator.GetAvailableJobs();
                if (liveJobs == null) return;

                var saveList = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>();
                int liveCount = liveJobs.Count;
                for (int i = 0; i < liveCount; i++)
                {
                    saveList.Add(liveJobs[i].GetData());
                }

                byte[] blob = SerializeJobs(saveList.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>());
                int jobCount = saveList.Count;

                ChunkedBlobTransfer.Send("job", (ushort)messageType.jobBoardSync, StarTruckClient.currentSector, blob);

                StarTruckMP.Log.LogInfo($"JobBoardSync: {jobCount} Jobs fuer Sektor '{StarTruckClient.currentSector}' gesendet ({blob.Length} bytes).");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.OnLocalJobsGenerated fehlgeschlagen: {ex}");
            }
        }

        public static void HandleIncoming(MessageReceivedEventArgs e)
        {
            try
            {
                if (!ChunkedBlobTransfer.TryReceiveChunk("job", e.Message, out string sector, out byte[] blob))
                {
                    return; // noch nicht alle Chunks da, warten auf den Rest
                }

                // Nur uebernehmen wenn wir selbst gerade in diesem Sektor sind - Jobboards
                // anderer Sektoren wuerden sonst den lokalen ProceduralJobGenerator-State fuer
                // den falschen Sektor ueberschreiben.
                if (sector != StarTruckClient.currentSector) return;

                // Sind wir selbst die Autoritaet fuer diesen Sektor, ignorieren wir eingehende
                // Syncs - unser eigener Stand ist die Quelle der Wahrheit.
                if (IsAuthorityForCurrentSector()) return;

                // Build-321: NullReferenceException-Quelle eingegrenzt - im Sektorwechsel-
                // Fenster koennen DeserializeJobs oder der questSave-Cast NREs werfen
                // (IL2CPP-Objekte, deren Managed-Wrapper waehrend des Sektor-Teardown
                // bereits invalidiert sind).
                // Build-323: Ein defekter Blob ist NICHT mehr endgueltig verworfen.
                // Hintergrund (Testbefehl 322): ApplyRestore kann in Spiel-Code NREn
                // (QuestTaskParameter.GenerateSector), wenn ein Sektor-Parameter auf einen
                // Sektor verweist, den der EMPFAENGER-Client noch nicht geladen/registriert
                // hat. Solche Races loesen sich binnen Sekunden (Sektor-Load, QuestTracker-
                // Sektor-Ready). Deshalb: DeserializeJobs/Cast-Fehler -> Blob verwerfen
                // (unlesbar, kein Retry sinnvoll); ApplyRestore-Fehler -> PUFFERN und
                // mit Retry-Queue (max. 10 Versuche, alle 2s) nachziehen. Siehe
                // EnqueueRetry/ProcessRetryQueue.
                Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData> jobs = null;
                try
                {
                    jobs = DeserializeJobs(blob);
                }
                catch (Exception desEx)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardSync.HandleIncoming: DeserializeJobs fehlgeschlagen (Sektor '{sector}', {blob?.Length ?? 0} bytes) - Blob verworfen, warte auf naechsten Sync: {desEx.Message}");
                    return;
                }

                QuestSaveData questSave;
                try
                {
                    questSave = new QuestSaveData();
                    questSave.availableJobs = jobs.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();
                }
                catch (Exception castEx)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardSync.HandleIncoming: QuestSaveData-Cast fehlgeschlagen (Sektor '{sector}') - Sync verworfen: {castEx.Message}");
                    return;
                }

                // QuestTracker kann beim Empfaenger im Sync-Moment noch nicht ready sein
                // (Ready-Flag hinkt der lokalen Generierung hinterher). Frueher wurde der
                // Blob hier hart verworfen -> Sync ging still verloren, jeder sah sein
                // eigenes Board. Stattdessen puffern und Frame-fuer-Frame uebernehmen,
                // sobald QuestTracker.ready ist.
                if (!QuestTracker.ready)
                {
                    pendingRestoreSector = sector;
                    pendingRestoreJobs = questSave;
                    StarTruckMP.Log.LogInfo($"JobBoardSync: QuestTracker noch nicht ready, Sync fuer Sektor '{sector}' gepuffert (Retry ueber FixedUpdate).");
                    return;
                }

                try
                {
                    ApplyRestore(sector, questSave);
                }
                catch (Exception applyEx)
                {
                    // ApplyRestore toucht QuestTracker (Il2Cpp) - im Sektorwechsel-Fenster
                    // kann das NREn. Build-323: NICHT mehr endgueltig verwerfen, sondern
                    // in die Retry-Queue (Sektor-Load-Races loesen sich binnen Sekunden).
                    // Nur wenn ALLE Versuche scheitern, wird der Blob endgueltig verworfen
                    // (dann Log mit Diagnose: job-Ids + betroffene Parameter-Namen).
                    StarTruckMP.Log.LogWarning($"JobBoardSync.HandleIncoming: ApplyRestore fehlgeschlagen (Sektor '{sector}') - Retry-Queue: {applyEx.Message}");
                    EnqueueRetry(sector, questSave, applyEx.Message);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.HandleIncoming fehlgeschlagen: {ex}");
            }
        }

        public static bool IsAuthorityForCurrentSector()
        {
            ushort myId = StarTruckClient.client.Id;
            ushort lowest = myId;
            foreach (var kv in StarTruckClient.playerList)
            {
                if (kv.Value.sector == StarTruckClient.currentSector && kv.Key < lowest)
                    lowest = kv.Key;
            }
            return lowest == myId;
        }

        // Build-323: Retry-Queue fuer ApplyRestore-Fehler (QuestTaskParameter.GenerateSector
        // NREt, wenn ein Sektor-Parameter auf einen (noch) nicht geladenen Sektor verweist).
        // - Generisch fuer ALLE Sektoren (keine Whitelist/Blacklist): Es wird nur geprueft,
        //   ob die Sektor-Metadaten des aktuellen Sektors abrufbar sind; ein noch nicht
        //   geladener Sektor loest sich auf, sobald das Spiel ihn laedt.
        // - Max. MAX_RETRY_ATTEMPTS Versuche im Abstand RETRY_INTERVAL Sekunden; danach wird
        //   der Blob endgueltig verworfen (Diagnose-Log mit Job-Ids).
        // - Ein neuer Sync-Broadcast fuer denselben Sektor ersetzt den gepufferten (immer
        //   der aktuellste Stand gewinnt).
        // Wird ueber Client.FixedUpdate() -> TryApplyPending() nachgezogen.
        private const int MAX_RETRY_ATTEMPTS = 10;
        private const float RETRY_INTERVAL = 2f;

        private class PendingRestore
        {
            public string sector;
            public QuestSaveData jobs;
            public int attempts;
            public float nextTryTime;
            public string lastError;
        }

        private static PendingRestore pendingRestore = null;

        private static void EnqueueRetry(string sector, QuestSaveData questSave, string error)
        {
            // Neuer Broadcast fuer denselben Sektor ersetzt den alten gepufferten Stand.
            if (pendingRestore != null && pendingRestore.sector == sector)
            {
                pendingRestore.jobs = questSave;
                pendingRestore.attempts = 0;
                pendingRestore.lastError = error;
                pendingRestore.nextTryTime = Time.realtimeSinceStartup + RETRY_INTERVAL;
                return;
            }
            pendingRestore = new PendingRestore
            {
                sector = sector,
                jobs = questSave,
                attempts = 0,
                nextTryTime = Time.realtimeSinceStartup + RETRY_INTERVAL,
                lastError = error,
            };
            StarTruckMP.Log.LogInfo($"JobBoardSync: Retry-Queue fuer Sektor '{sector}' eingerichtet (max {MAX_RETRY_ATTEMPTS} Versuche alle {RETRY_INTERVAL:0}s).");
        }

        private static void ApplyRestore(string sector, QuestSaveData questSave)
        {
            if (sector != StarTruckClient.currentSector) return;
            if (IsAuthorityForCurrentSector()) return;
            QuestTracker.Get()?.RestoreAvailableJobs(questSave);
            StarTruckMP.Log.LogInfo($"JobBoardSync: Jobs fuer Sektor '{sector}' uebernommen ({(questSave.availableJobs != null ? Il2CppCount(questSave.availableJobs) : 0)}).");
        }

        public static void TryApplyPending()
        {
            var pending = pendingRestore;
            if (pending == null) return;

            // Sektor verlassen? Retry ist dann sinnlos (ApplyRestore wuerde eh abbrechen).
            if (pending.sector != StarTruckClient.currentSector)
            {
                StarTruckMP.Log.LogInfo($"JobBoardSync: Retry fuer Sektor '{pending.sector}' verworfen (wir sind jetzt in '{StarTruckClient.currentSector}').");
                pendingRestore = null;
                return;
            }
            if (!QuestTracker.ready) return;
            if (IsAuthorityForCurrentSector())
            {
                // Autoritaet wechselt zu uns: unser eigener Stand ist Quelle der Wahrheit,
                // der gepufferte Remote-Stand ist ueberfluessig.
                pendingRestore = null;
                return;
            }
            if (Time.realtimeSinceStartup < pending.nextTryTime) return;

            pending.attempts++;
            try
            {
                ApplyRestore(pending.sector, pending.jobs);
                pendingRestore = null;
                StarTruckMP.Log.LogInfo($"JobBoardSync: Retry erfolgreich (Sektor '{pending.sector}', Versuch {pending.attempts}).");
                return;
            }
            catch (Exception retryEx)
            {
                pending.lastError = retryEx.Message;
                if (pending.attempts >= MAX_RETRY_ATTEMPTS)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardSync: Retry ENDGUELTIG fehlgeschlagen (Sektor '{pending.sector}' nach {pending.attempts} Versuchen) - Blob verworfen. Letzter Fehler: {retryEx.Message}. Diagnose: {DescribeJobs(pending.jobs)}");
                    pendingRestore = null;
                    return;
                }
                pending.nextTryTime = Time.realtimeSinceStartup + RETRY_INTERVAL;
                StarTruckMP.Log.LogInfo($"JobBoardSync: Retry {pending.attempts}/{MAX_RETRY_ATTEMPTS} fuer Sektor '{pending.sector}' fehlgeschlagen ({retryEx.Message}) - naechster Versuch in {RETRY_INTERVAL:0}s.");
            }
        }

        // Diagnose: Job-Ids + Parameter-Namen des gepufferten Standes loggen, damit der
        // problematische Job/Parameter (QuestTaskParameter.GenerateSector) identifiziert
        // werden kann, auch wenn das Spiel nur All-or-Nothing-Restore anbietet.
        private static string DescribeJobs(QuestSaveData questSave)
        {
            try
            {
                var jobs = questSave?.availableJobs;
                if (jobs == null) return "availableJobs=null";
                var ids = new List<string>();
                int count = Il2CppCount(jobs);
                for (int i = 0; i < count && i < 20; i++)
                {
                    var job = jobs[i];
                    var paramNames = new List<string>();
                    var pars = job.generatedParameters;
                    if (pars != null)
                    {
                        int pc = Il2CppCount(pars);
                        for (int p = 0; p < pc && p < 10; p++)
                        {
                            var par = pars[p];
                            // Nur Sektor-/Route-Parameter nennen - das sind die Kandidaten
                            // fuer GenerateSector-NREs (Sektoren, die der Empfaenger noch
                            // nie geladen hat).
                            if (par.Kind == QuestTaskParameterSaveData.ItemKind.sectorId && par.sectorId != null)
                                paramNames.Add($"sectorId:{par.sectorId.name}={par.sectorId.value}");
                        }
                    }
                    ids.Add($"{job.id}[{string.Join(",", paramNames)}]");
                }
                return $"jobs={count}: {string.Join("; ", ids)}";
            }
            catch (Exception ex)
            {
                return $"(DescribeJobs fehlgeschlagen: {ex.Message})";
            }
        }

        // ---- Serialisierung (eigenes Blob-Format, unabhaengig von Riptide-Feld-API) ----
        // Spiegelt QuestInstanceSaveData / QuestTaskParameterSaveData - dasselbe Format, das
        // das Spiel selbst fuers Speichern nutzt (FlatSharp-generierte Save-Datentypen).

        // Alle 19 ItemKind-Varianten sind FlatSharp-generierte name+value Wrapper (siehe
        // WriteParam/ReadParam) - es gibt keine unterstuetzten/nicht unterstuetzten Varianten
        // mehr. Bleibt als Stelle stehen, falls das Spiel per Update neue Varianten einfuehrt,
        // die ReadParam/WriteParam noch nicht kennen (dann: NONE-Fall bzw. default-Fall).
        private static bool IsSupportedKind(QuestTaskParameterSaveData.ItemKind k)
        {
            return k != QuestTaskParameterSaveData.ItemKind.NONE;
        }

        private static byte[] SerializeJobs(Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData> jobs)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            int jobsCount = Il2CppCount(jobs);
            w.Write((ushort)jobsCount);
            for (int ji = 0; ji < jobsCount; ji++)
            {
                var job = jobs[ji];
                w.Write(job.id ?? "");
                w.Write(job.questParametersAsset ?? "");

                var taskStates = job.taskStates;
                int stateCount = Il2CppCount(taskStates);
                w.Write((ushort)stateCount);
                for (int i = 0; i < stateCount; i++) w.Write(taskStates[i] ?? "");

                var taskCounts = job.taskCompleteCounts;
                int cCount = Il2CppCount(taskCounts);
                w.Write((ushort)cCount);
                for (int i = 0; i < cCount; i++) w.Write(taskCounts[i]);

                var pars = job.generatedParameters;
                int totalParams = Il2CppCount(pars);
                int supportedCount = 0;
                if (pars != null)
                {
                    for (int i = 0; i < totalParams; i++)
                        if (IsSupportedKind(pars[i].Kind)) supportedCount++;
                }
                w.Write((ushort)supportedCount);
                if (pars != null)
                {
                    for (int i = 0; i < totalParams; i++)
                        if (IsSupportedKind(pars[i].Kind)) WriteParam(w, pars[i]);
                }

                if (totalParams > supportedCount)
                {
                    StarTruckMP.Log.LogWarning($"JobBoardSync: Job '{job.id}' hat {totalParams - supportedCount} nicht unterstuetzte Parameter (cargoType/cargoProperties/conversation/etc.) - diese werden NICHT synchronisiert.");
                }
            }

            return ms.ToArray();
        }

        private static Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData> DeserializeJobs(byte[] blob)
        {
            var result = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>();
            using var ms = new MemoryStream(blob);
            using var r = new BinaryReader(ms);

            ushort count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var job = new QuestInstanceSaveData();
                job.id = r.ReadString();
                job.questParametersAsset = r.ReadString();

                ushort stateCount = r.ReadUInt16();
                var states = new Il2CppSystem.Collections.Generic.List<string>();
                for (int s = 0; s < stateCount; s++) states.Add(r.ReadString());
                job.taskStates = states.Cast<Il2CppSystem.Collections.Generic.IList<string>>();

                ushort countCount = r.ReadUInt16();
                var counts = new Il2CppSystem.Collections.Generic.List<int>();
                for (int c = 0; c < countCount; c++) counts.Add(r.ReadInt32());
                job.taskCompleteCounts = counts.Cast<Il2CppSystem.Collections.Generic.IList<int>>();

                ushort paramCount = r.ReadUInt16();
                var pars = new Il2CppSystem.Collections.Generic.List<QuestTaskParameterSaveData>();
                for (int p = 0; p < paramCount; p++) pars.Add(ReadParam(r));
                job.generatedParameters = pars.Cast<Il2CppSystem.Collections.Generic.IList<QuestTaskParameterSaveData>>();

                result.Add(job);
            }
            return result;
        }

        private static void WriteParam(BinaryWriter w, QuestTaskParameterSaveData p)
        {
            w.Write((byte)p.Kind);
            switch (p.Kind)
            {
                case QuestTaskParameterSaveData.ItemKind.intValue:
                    w.Write(p.intValue.name ?? ""); w.Write(p.intValue.value); break;
                case QuestTaskParameterSaveData.ItemKind.stringValue:
                    w.Write(p.stringValue.name ?? ""); w.Write(p.stringValue.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.trailerId:
                    w.Write(p.trailerId.name ?? ""); w.Write(p.trailerId.value); break;
                case QuestTaskParameterSaveData.ItemKind.cargoProperties:
                    w.Write(p.cargoProperties.name ?? ""); w.Write(p.cargoProperties.value); break;
                case QuestTaskParameterSaveData.ItemKind.cargoType:
                    w.Write(p.cargoType.name ?? ""); w.Write(p.cargoType.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.sectorId:
                    w.Write(p.sectorId.name ?? ""); w.Write(p.sectorId.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.cargoBayId:
                    w.Write(p.cargoBayId.name ?? ""); w.Write(p.cargoBayId.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.galacticTime:
                    w.Write(p.galacticTime.name ?? ""); w.Write(p.galacticTime.value); break;
                case QuestTaskParameterSaveData.ItemKind.corporationId:
                    w.Write(p.corporationId.name ?? ""); w.Write(p.corporationId.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.conversation:
                    w.Write(p.conversation.name ?? ""); w.Write(p.conversation.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags:
                    w.Write(p.inventoryItemTags.name ?? ""); w.Write(p.inventoryItemTags.value); break;
                case QuestTaskParameterSaveData.ItemKind.floatValue:
                    w.Write(p.floatValue.name ?? ""); w.Write(p.floatValue.value); break;
                case QuestTaskParameterSaveData.ItemKind.vector3:
                    w.Write(p.vector3.name ?? "");
                    w.Write(p.vector3.value.x); w.Write(p.vector3.value.y); w.Write(p.vector3.value.z);
                    break;
                case QuestTaskParameterSaveData.ItemKind.quest:
                    w.Write(p.quest.name ?? ""); w.Write(p.quest.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.identifier:
                    w.Write(p.identifier.name ?? ""); w.Write(p.identifier.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.questFlag:
                    w.Write(p.questFlag.name ?? ""); w.Write(p.questFlag.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.ventureLocation:
                    w.Write(p.ventureLocation.name ?? ""); w.Write(p.ventureLocation.value ?? ""); break;
                case QuestTaskParameterSaveData.ItemKind.ventureType:
                    w.Write(p.ventureType.name ?? ""); w.Write((int)p.ventureType.value); break;
                case QuestTaskParameterSaveData.ItemKind.ventureJobType:
                    w.Write(p.ventureJobType.name ?? ""); w.Write((int)p.ventureJobType.value); break;
                default:
                    StarTruckMP.Log.LogWarning($"JobBoardSync.WriteParam: unbekannter ItemKind {p.Kind}, wird uebersprungen.");
                    break;
            }
        }

        private static QuestTaskParameterSaveData ReadParam(BinaryReader r)
        {
            var kind = (QuestTaskParameterSaveData.ItemKind)r.ReadByte();
            switch (kind)
            {
                case QuestTaskParameterSaveData.ItemKind.intValue:
                    { var v = new IntValue(); v.name = r.ReadString(); v.value = r.ReadInt32(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.stringValue:
                    { var v = new StringValue(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.trailerId:
                    { var v = new TrailerId(); v.name = r.ReadString(); v.value = r.ReadInt64(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.cargoProperties:
                    { var v = new CargoProperties(); v.name = r.ReadString(); v.value = r.ReadInt32(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.cargoType:
                    { var v = new StarTruckSaveData.CargoType(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.sectorId:
                    { var v = new SectorId(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.cargoBayId:
                    { var v = new CargoBayId(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.galacticTime:
                    { var v = new StarTruckSaveData.GalacticTime(); v.name = r.ReadString(); v.value = r.ReadInt64(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.corporationId:
                    { var v = new CorporationId(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.floatValue:
                    { var v = new FloatValue(); v.name = r.ReadString(); v.value = r.ReadSingle(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.vector3:
                    {
                        var v = new Vector3Value(); v.name = r.ReadString();
                        var vec = new Vector3Data();
                        vec.x = r.ReadSingle(); vec.y = r.ReadSingle(); vec.z = r.ReadSingle();
                        v.value = vec;
                        return new QuestTaskParameterSaveData(v);
                    }
                case QuestTaskParameterSaveData.ItemKind.conversation:
                    { var v = new Conversation(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags:
                    { var v = new StarTruckSaveData.InventoryItemTags(); v.name = r.ReadString(); v.value = r.ReadInt32(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.identifier:
                    { var v = new StarTruckSaveData.Identifier(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.quest:
                    { var v = new Quest(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.questFlag:
                    { var v = new StarTruckSaveData.QuestFlag(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.ventureLocation:
                    { var v = new StarTruckSaveData.VentureLocation(); v.name = r.ReadString(); v.value = r.ReadString(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.ventureType:
                    { var v = new VentureTypeData(); v.name = r.ReadString(); v.value = (VentureType)r.ReadInt32(); return new QuestTaskParameterSaveData(v); }
                case QuestTaskParameterSaveData.ItemKind.ventureJobType:
                    { var v = new VentureJobTypeData(); v.name = r.ReadString(); v.value = (VentureJobType)r.ReadInt32(); return new QuestTaskParameterSaveData(v); }
                default:
                    throw new InvalidOperationException($"JobBoardSync: unbekannter/nicht unterstuetzter ItemKind beim Lesen: {kind}");
            }
        }
    }

    [HarmonyPatch]
    public class JobBoardSyncPatches
    {
        // Laeuft nach jeder lokalen Job-Generierung (Sektorwechsel, Cargo-Aenderung etc.) -
        // JobBoardSync.OnLocalJobsGenerated() entscheidet selbst per Autoritaets-Check, ob
        // tatsaechlich etwas gesendet wird.
        [HarmonyPatch(typeof(ProceduralJobGenerator), nameof(ProceduralJobGenerator.GenerateJobsForAllSectors))]
        [HarmonyPostfix]
        public static void GenerateJobsForAllSectors_Postfix()
        {
            try { JobBoardSync.OnLocalJobsGenerated(); }
            catch (Exception ex) { StarTruckMP.Log.LogWarning($"GenerateJobsForAllSectors_Postfix Fehler: {ex.Message}"); }
        }
    }
}
