using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
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
                // Build-323: QuestTracker nicht ready -> kein Vorab-Puffer mehr noetig,
                // ApplyRestore-NREs landen in der Retry-Queue (TryApplyPending wartet
                // ohnehin auf QuestTracker.ready).
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

        // Build-325: Retry-Queue fuer ApplyRestore-Fehler - obsolet geworden durch das
        // JOB-WEISE Apply mit try/catch pro Job (ApplyJobsOneByOne): Ein unauflösbarer
        // Parameter/JOB bricht nicht mehr alles ab, es gibt keinen All-or-Nothing-Fehler
        // mehr, der einen Retry erfordert. Struktur bleibt (EnqueueRetry ist ab jetzt
        // ein No-Op, falls HandleIncoming doch mal eine Exception wirft - z.B. Il2Cpp-
        // Teardown-Fenster), TryApplyPending raumt leerstaende Entries sofort ab.
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
            // Build-325: only kept as a safety net for HandleIncoming-level exceptions
            // (Il2Cpp-Teardown windows). The per-job apply path cannot throw for one bad
            // job anymore, so a retry is almost always pointless - TryApplyPending
            // discards pending entries after a single failed attempt instead of
            // re-trying 10x with an error that can never heal.
            pendingRestore = new PendingRestore
            {
                sector = sector,
                jobs = questSave,
                attempts = 0,
                nextTryTime = Time.realtimeSinceStartup + RETRY_INTERVAL,
                lastError = error,
            };
            StarTruckMP.Log.LogInfo($"JobBoardSync: Retry-Entry fuer Sektor '{sector}' angelegt (1 Veruchs-Window von {RETRY_INTERVAL:0}s).");
        }

        private static void ApplyRestore(string sector, QuestSaveData questSave)
        {
            if (sector != StarTruckClient.currentSector) return;
            if (IsAuthorityForCurrentSector()) return;
            int applied = ApplyJobsOneByOne(questSave);
            // Post-Check: lehnt RestoreAvailableJobs mit 1-Job-Blob nur AN statt ANZUHAENGEN,
            // landet hier nur der letzte Job auf dem Board - das Test-Log zeigt es sofort.
            try
            {
                var board = ProceduralJobGenerator.GetAvailableJobs();
                StarTruckMP.Log.LogInfo($"JobBoardSync: Board-Count nach ApplyRestore: {(board != null ? board.Count : -1)} (applied={applied}).");
            }
            catch { /* Diagnose darf nicht toeten */ }
            StarTruckMP.Log.LogInfo($"JobBoardSync: Jobs fuer Sektor '{sector}' uebernommen ({applied}).");
        }

        // Build-325: RestoreAvailableJobs ist All-or-Nothing - ein einzelner Job, dessen
        // Parameter im Spiel-Code nicht aufgeloest werden koennen (QuestTaskParameter.
        // GenerateSector NREt z.B. auf fehlender Sektor-Registry bei Sektor-Params ohne
        // value), killt damit ALLE 62 Jobs. Deshalb: job-weise ueber QuestTracker.
        // ConvertQuestSaveDataToInstances anwenden - genau derselbe Pfad wie im Spiel -
        // aber mit try/catch PRO JOB. Ein unauflösbarer Job wird uebersprungen (Log mit
        // Diagnose), alle restlichen Jobs werden synchronisiert.
        private static int ApplyJobsOneByOne(QuestSaveData questSave)
        {
            var jobs = questSave?.availableJobs;
            if (jobs == null) return 0;

            int applied = 0;
            int skipped = 0;
            int count = Il2CppCount(jobs);
            for (int i = 0; i < count; i++)
            {
                var job = jobs[i];
                var single = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>();
                single.Add(job);
                var singleSave = new QuestSaveData();
                singleSave.availableJobs = single.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();
                try
                {
                    QuestTracker.Get()?.RestoreAvailableJobs(singleSave);
                    applied++;
                }
                catch (Exception jobEx)
                {
                    skipped++;
                    string diag = DescribeSingleJob(job);
                    StarTruckMP.Log.LogWarning($"JobBoardSync: Job {i + 1}/{count} '{job.id}' uebersprungen (Param nicht auflösbar): {jobEx.Message} | {diag}");
                }
            }
            if (skipped > 0)
                StarTruckMP.Log.LogWarning($"JobBoardSync: {skipped}/{count} Jobs konnten nicht restoriert und wurden uebersprungen ({applied} uebernommen).");
            return applied;
        }

        // Build-325: Diagnose fuer EINEN Job - ALLE Parameter-Namen mit Kind und value
        // loggen. Vorgaenger DescribeJobs zeigte fuer jeden Job '[]', weil nur
        // sectorId-Parameter mit name+value aufgeschluesselt wurden; aus einem leeren
        // Output konnte man nicht ablesen, ob die Parameter-Liste wirklich leer war oder
        // nur keine sectorId-Parameter enthielt (oder sectorId ohne sectorId.value).
        private static string DescribeSingleJob(QuestInstanceSaveData job)
        {
            try
            {
                var pars = job?.generatedParameters;
                if (pars == null) return "generatedParameters=null";
                int pc = Il2CppCount(pars);
                var parts = new List<string>();
                for (int p = 0; p < pc && p < 16; p++)
                {
                    var par = pars[p];
                    string detail;
                    try
                    {
                        // Build-326: generisch - Kind + name + value JEDES Parameters loggen.
                        // Die Discriminator-Variante entscheidet, welche typed Property den
                        // Wrapper haelt - dort liegt name:string (alle 19 Varianten haben es).
                        string nm = "?";
                        string val = "?";
                        var k = par.Kind;
                        try
                        {
                            Il2CppSystem.Object vObj = null;
                            string prop = KindToProp(k);
                            if (prop != null)
                            {
                                var pi = typeof(QuestTaskParameterSaveData).GetProperty(prop);
                                if (pi != null) vObj = pi.GetValue(par) as Il2CppSystem.Object;
                            }
                            if (vObj != null)
                            {
                                val = vObj.ToString();
                                var nameProp = vObj.GetIl2CppType().GetProperty("name");
                                if (nameProp != null)
                                    nm = nameProp.GetValue(vObj)?.ToString() ?? "null";
                            }
                            else val = "null";
                        }
                        catch { }
                        detail = $"{k} name='{nm}' value='{val}'";
                    }
                    catch (Exception pe)
                    {
                        detail = $"{par.Kind} (nicht lesbar: {pe.Message})";
                    }
                    parts.Add(detail);
                }
                return $"params={pc}[{string.Join(", ", parts)}]";
            }
            catch (Exception ex)
            {
                return $"(DescribeSingleJob fehlgeschlagen: {ex.Message})";
            }
        }

        // Build-326: ItemKind -> Name der typed Value-Property an QuestTaskParameterSaveData.
        private static string KindToProp(QuestTaskParameterSaveData.ItemKind k)
        {
            switch (k)
            {
                case QuestTaskParameterSaveData.ItemKind.intValue: return "intValue";
                case QuestTaskParameterSaveData.ItemKind.stringValue: return "stringValue";
                case QuestTaskParameterSaveData.ItemKind.trailerId: return "trailerId";
                case QuestTaskParameterSaveData.ItemKind.cargoProperties: return "cargoProperties";
                case QuestTaskParameterSaveData.ItemKind.cargoType: return "cargoType";
                case QuestTaskParameterSaveData.ItemKind.sectorId: return "sectorId";
                case QuestTaskParameterSaveData.ItemKind.cargoBayId: return "cargoBayId";
                case QuestTaskParameterSaveData.ItemKind.galacticTime: return "galacticTime";
                case QuestTaskParameterSaveData.ItemKind.corporationId: return "corporationId";
                case QuestTaskParameterSaveData.ItemKind.conversation: return "conversation";
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags: return "inventoryItemTags";
                case QuestTaskParameterSaveData.ItemKind.floatValue: return "floatValue";
                case QuestTaskParameterSaveData.ItemKind.vector3: return "vector3";
                case QuestTaskParameterSaveData.ItemKind.quest: return "quest";
                case QuestTaskParameterSaveData.ItemKind.identifier: return "identifier";
                case QuestTaskParameterSaveData.ItemKind.questFlag: return "questFlag";
                case QuestTaskParameterSaveData.ItemKind.ventureLocation: return "ventureLocation";
                case QuestTaskParameterSaveData.ItemKind.ventureType: return "ventureType";
                case QuestTaskParameterSaveData.ItemKind.ventureJobType: return "ventureJobType";
                default: return null;
            }
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
                // Build-325: Der per-Job-Apply kann fuer einen bad Job nicht mehr werfen -
                // kommt es hier an, ist es ein struktureller Fehler (Teardown-Fenster o.ae.),
                // der sich durch 10x Wiederholen nicht heilt. Sofort verwerfen.
                StarTruckMP.Log.LogWarning($"JobBoardSync: Retry verworfen (Sektor '{pending.sector}', Versuch {pending.attempts}) - struktureller Fehler, Wiederholung sinnlos: {retryEx.Message}");
                pendingRestore = null;
            }
        }

        // Build-325: das alte All-jobs-DescribeJobs (das fuer jeden Job '[]' zeigte, weil
        // es nur sectorId-Parameter mit Werten aufschluesselte) ist ersetzt durch
        // DescribeSingleJob - pro Job ALLE Parameter mit Kind+Werten, nur noch im
        // Fehlerfall eines einzelnen Jobs geloggt.

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

<<<<<<< HEAD
        // Build-326: QuestTaskParameterSaveData ist ein FlatSharp-Union-Struct. Die Parameter-
        // Konstruktoren des Interop-Assemblys setzen intern Discriminator+value, aber beim
        // Konstruieren aus gemanagtem Code endet der Discriminator empirisch auf 0 (= NONE),
        // egal welcher Konstruktor benutzt wurde -> beim Empfaenger dekodiert ALLE Parameter
        // als Kind=NONE -> QuestTaskParameter.GenerateSector NREt (custom-build-325-Befund:
        // 247 Jobs, alle 'params=16[NONE x12]', applied=0).
        //
        // Fix: Discriminator-Backingfield und value-Feld DIREKT per Feld-Offset setzen
        // (beide sind als unsafe-Properties mit direktem Feldzugriff im Interop verfuegbar),
        // statt dem IL2CPP-Konstruktor zu trauen. Der Direktzugriff umgeht den Union-Switch
        // voellig und ist exakt das, was FlatSharp selbst schreibt.
        private static QuestTaskParameterSaveData MakeParam(QuestTaskParameterSaveData.ItemKind kind, Il2CppSystem.Object value)
        {
            var p = new QuestTaskParameterSaveData();
            p._Discriminator_k__BackingField = (byte)kind;
            p.value = value;
            return p;
=======
        // Build-327: QuestTaskParameterSaveData ist ein FlatSharp-Union-Struct
        // (_Discriminator_k__BackingField + value). Build-326 versuchte, den Discriminator
        // ueber die generierte unsafe-Property (Managed Field-Offset-Write) zu setzen - das
        // griff nicht (Empfaenger-Log: alle Parameter Kind=NONE, value='null').
        //
        // Mehrstufige MakeParam mit ZWINGENDER Verifikation (Discriminator-Readback):
        // 1) Typisierter Union-CTor: new QuestTaskParameterSaveData(value). Der interop-
        //    generierte CTor ruft den nativen CTor per il2cpp_runtime_invoke (instanzgebunden).
        // 2) Fallback: il2cpp_object_new + Direktzugriff auf das Discriminator-Backingfield
        //    mit dem ECHTEN unmanaged Offset (il2cpp_field_get_offset) und value via
        //    il2cpp_gc_wbarrier_set_field - exakt die Mechanik der generierten unsafe-Setter.
        // 3) Readback nach jedem Versuch ueber denselben Offset. Erst wenn der Discriminator
        //    wirklich auf kind steht, verlaesst das Objekt MakeParam - sonst Exception + Log.
        private static QuestTaskParameterSaveData MakeParam(QuestTaskParameterSaveData.ItemKind kind, Il2CppSystem.Object value)
        {
            byte expected = (byte)kind;

            try
            {
                QuestTaskParameterSaveData p = MakeParamViaTypedCtor(kind, value);
                byte readback = ReadDiscriminatorSafe(p, "ctor");
                if (readback == expected) return p;
                StarTruckMP.Log.LogWarning($"JobBoardSync.MakeParam: ctor-Variante lieferte Discriminator {readback} != {expected} - naechster Versuch: RawWrite.");
            }
            catch (Exception ex1)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.MakeParam: Union-CTor-Variante fehlgeschlagen: {ex1.Message}");
            }

            try
            {
                QuestTaskParameterSaveData p = MakeParamViaRawWrite(kind, value);
                byte readback = ReadDiscriminatorSafe(p, "rawWrite");
                if (readback == expected) return p;
                StarTruckMP.Log.LogWarning($"JobBoardSync.MakeParam: rawWrite-Variante lieferte Discriminator {readback} != {expected}.");
            }
            catch (Exception ex2)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.MakeParam: RawWrite-Variante fehlgeschlagen: {ex2.Message}");
            }

            throw new InvalidOperationException(
                $"JobBoardSync.MakeParam: Discriminator konnte NICHT auf {expected} gesetzt werden - Job-Parameter nicht synchronisierbar (Plan-B-Hexdump im Log, siehe DescribeParamRaw).");
        }

        // Typisierter Union-CTor je Variante.
        private static QuestTaskParameterSaveData MakeParamViaTypedCtor(QuestTaskParameterSaveData.ItemKind kind, Il2CppSystem.Object value)
        {
            switch (kind)
            {
                case QuestTaskParameterSaveData.ItemKind.intValue: return new QuestTaskParameterSaveData((IntValue)value);
                case QuestTaskParameterSaveData.ItemKind.stringValue: return new QuestTaskParameterSaveData((StringValue)value);
                case QuestTaskParameterSaveData.ItemKind.trailerId: return new QuestTaskParameterSaveData((TrailerId)value);
                case QuestTaskParameterSaveData.ItemKind.cargoProperties: return new QuestTaskParameterSaveData((CargoProperties)value);
                case QuestTaskParameterSaveData.ItemKind.cargoType: return new QuestTaskParameterSaveData((StarTruckSaveData.CargoType)value);
                case QuestTaskParameterSaveData.ItemKind.sectorId: return new QuestTaskParameterSaveData((SectorId)value);
                case QuestTaskParameterSaveData.ItemKind.cargoBayId: return new QuestTaskParameterSaveData((CargoBayId)value);
                case QuestTaskParameterSaveData.ItemKind.galacticTime: return new QuestTaskParameterSaveData((StarTruckSaveData.GalacticTime)value);
                case QuestTaskParameterSaveData.ItemKind.corporationId: return new QuestTaskParameterSaveData((CorporationId)value);
                case QuestTaskParameterSaveData.ItemKind.conversation: return new QuestTaskParameterSaveData((Conversation)value);
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags: return new QuestTaskParameterSaveData((StarTruckSaveData.InventoryItemTags)value);
                case QuestTaskParameterSaveData.ItemKind.floatValue: return new QuestTaskParameterSaveData((FloatValue)value);
                case QuestTaskParameterSaveData.ItemKind.vector3: return new QuestTaskParameterSaveData((Vector3Value)value);
                case QuestTaskParameterSaveData.ItemKind.quest: return new QuestTaskParameterSaveData((Quest)value);
                case QuestTaskParameterSaveData.ItemKind.identifier: return new QuestTaskParameterSaveData((StarTruckSaveData.Identifier)value);
                case QuestTaskParameterSaveData.ItemKind.questFlag: return new QuestTaskParameterSaveData((StarTruckSaveData.QuestFlag)value);
                case QuestTaskParameterSaveData.ItemKind.ventureLocation: return new QuestTaskParameterSaveData((StarTruckSaveData.VentureLocation)value);
                case QuestTaskParameterSaveData.ItemKind.ventureType: return new QuestTaskParameterSaveData((VentureTypeData)value);
                case QuestTaskParameterSaveData.ItemKind.ventureJobType: return new QuestTaskParameterSaveData((VentureJobTypeData)value);
                default:
                    throw new InvalidOperationException($"MakeParamViaTypedCtor: unbekannter ItemKind {kind}");
            }
        }

        // ---- IL2CPP-Rohzugriff (Fallback) ----
        // fieldInfo-Handles einmalig auflösen; Offset via il2cpp_field_get_offset; Schreiben
        // exakt wie die generierten unsafe-Setter (Pointer-Arithmetik bzw. wbarrier-Write).
        private static System.IntPtr _qtpsdFieldDiscriminator = System.IntPtr.Zero;
        private static System.IntPtr _qtpsdFieldValue = System.IntPtr.Zero;
        private static int _qtpsdOffsetDiscriminator = -1;
        private static bool _qtpsdFieldsResolved = false;
        private static readonly object _qtpsdFieldLock = new object();

        private static void ResolveQtpsdFields()
        {
            lock (_qtpsdFieldLock)
            {
                if (_qtpsdFieldsResolved) return;
                System.IntPtr classPtr = Il2CppClassPointerStore<QuestTaskParameterSaveData>.NativeClassPtr;
                if (classPtr == System.IntPtr.Zero)
                    throw new InvalidOperationException("QuestTaskParameterSaveData.NativeClassPtr == 0");
                _qtpsdFieldDiscriminator = IL2CPP.GetIl2CppField(classPtr, "<Discriminator>k__BackingField");
                _qtpsdFieldValue = IL2CPP.GetIl2CppField(classPtr, "value");
                if (_qtpsdFieldDiscriminator == System.IntPtr.Zero)
                    throw new InvalidOperationException("Discriminator-Feld nicht gefunden");
                _qtpsdOffsetDiscriminator = (int)IL2CPP.il2cpp_field_get_offset(_qtpsdFieldDiscriminator);
                _qtpsdFieldsResolved = true;
                StarTruckMP.Log.LogInfo($"JobBoardSync: Discriminator-Feld-Offset = {_qtpsdOffsetDiscriminator}");
            }
        }

        private static unsafe byte ReadDiscriminatorSafe(QuestTaskParameterSaveData p, string variant)
        {
            try
            {
                ResolveQtpsdFields();
                System.IntPtr objPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(p);
                return *((byte*)objPtr + _qtpsdOffsetDiscriminator);
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.MakeParam: Discriminator-Readback ({variant}) fehlgeschlagen: {ex.Message}");
                return 255;
            }
        }

        private static QuestTaskParameterSaveData MakeParamViaRawWrite(QuestTaskParameterSaveData.ItemKind kind, Il2CppSystem.Object value)
        {
            ResolveQtpsdFields();
            System.IntPtr classPtr = Il2CppClassPointerStore<QuestTaskParameterSaveData>.NativeClassPtr;
            System.IntPtr objPtr = IL2CPP.il2cpp_object_new(classPtr);
            if (objPtr == System.IntPtr.Zero)
                throw new InvalidOperationException("il2cpp_object_new lieferte 0");
            unsafe
            {
                *((byte*)objPtr + _qtpsdOffsetDiscriminator) = (byte)kind;
                if (value != null)
                {
                    System.IntPtr valuePtr = IL2CPP.Il2CppObjectBaseToPtr(value);
                    IL2CPP.il2cpp_gc_wbarrier_set_field(objPtr, (System.IntPtr)((byte*)objPtr + IL2CPP.il2cpp_field_get_offset(_qtpsdFieldValue)), valuePtr);
                }
            }
            return new QuestTaskParameterSaveData(objPtr);
>>>>>>> feature/jobboard-unionctor-327
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
<<<<<<< HEAD
                    { var v = new IntValue(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.stringValue:
                    { var v = new StringValue(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.trailerId:
                    { var v = new TrailerId(); v.name = r.ReadString(); v.value = r.ReadInt64(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoProperties:
                    { var v = new CargoProperties(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoType:
                    { var v = new StarTruckSaveData.CargoType(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.sectorId:
                    { var v = new SectorId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoBayId:
                    { var v = new CargoBayId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.galacticTime:
                    { var v = new StarTruckSaveData.GalacticTime(); v.name = r.ReadString(); v.value = r.ReadInt64(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.corporationId:
                    { var v = new CorporationId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.floatValue:
                    { var v = new FloatValue(); v.name = r.ReadString(); v.value = r.ReadSingle(); return MakeParam(kind, v); }
=======
{ var v = new IntValue(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.stringValue:
{ var v = new StringValue(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.trailerId:
{ var v = new TrailerId(); v.name = r.ReadString(); v.value = r.ReadInt64(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoProperties:
{ var v = new CargoProperties(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoType:
{ var v = new StarTruckSaveData.CargoType(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.sectorId:
{ var v = new SectorId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.cargoBayId:
{ var v = new CargoBayId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.galacticTime:
{ var v = new StarTruckSaveData.GalacticTime(); v.name = r.ReadString(); v.value = r.ReadInt64(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.corporationId:
{ var v = new CorporationId(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.floatValue:
{ var v = new FloatValue(); v.name = r.ReadString(); v.value = r.ReadSingle(); return MakeParam(kind, v); }
>>>>>>> feature/jobboard-unionctor-327
                case QuestTaskParameterSaveData.ItemKind.vector3:
                    {
                        var v = new Vector3Value(); v.name = r.ReadString();
                        var vec = new Vector3Data();
                        vec.x = r.ReadSingle(); vec.y = r.ReadSingle(); vec.z = r.ReadSingle();
                        v.value = vec;
                        return MakeParam(kind, v);
                    }
                case QuestTaskParameterSaveData.ItemKind.conversation:
<<<<<<< HEAD
                    { var v = new Conversation(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags:
                    { var v = new StarTruckSaveData.InventoryItemTags(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.identifier:
                    { var v = new StarTruckSaveData.Identifier(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.quest:
                    { var v = new Quest(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.questFlag:
                    { var v = new StarTruckSaveData.QuestFlag(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.ventureLocation:
                    { var v = new StarTruckSaveData.VentureLocation(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.ventureType:
                    { var v = new VentureTypeData(); v.name = r.ReadString(); v.value = (VentureType)r.ReadInt32(); return MakeParam(kind, v); }
=======
{ var v = new Conversation(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.inventoryItemTags:
{ var v = new StarTruckSaveData.InventoryItemTags(); v.name = r.ReadString(); v.value = r.ReadInt32(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.identifier:
{ var v = new StarTruckSaveData.Identifier(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.quest:
{ var v = new Quest(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.questFlag:
{ var v = new StarTruckSaveData.QuestFlag(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.ventureLocation:
{ var v = new StarTruckSaveData.VentureLocation(); v.name = r.ReadString(); v.value = r.ReadString(); return MakeParam(kind, v); }
                case QuestTaskParameterSaveData.ItemKind.ventureType:
{ var v = new VentureTypeData(); v.name = r.ReadString(); v.value = (VentureType)r.ReadInt32(); return MakeParam(kind, v); }
>>>>>>> feature/jobboard-unionctor-327
                case QuestTaskParameterSaveData.ItemKind.ventureJobType:
                    { var v = new VentureJobTypeData(); v.name = r.ReadString(); v.value = (VentureJobType)r.ReadInt32(); return MakeParam(kind, v); }
                default:
                    throw new InvalidOperationException($"JobBoardSync: unbekannter/nicht unterstuetzter ItemKind beim Lesen: {kind}");
            }
        }

        // Build-327 Plan B (nur Diagnose): rohe Bytes eines Param-Objekts hexdumpen, damit im
        // Test die wire bytes gegen die Dekompilierung validiert werden koennen. Liest die
        // ersten 64 Bytes ab Objekt-Header (klassptr + Felder) - der Discriminator liegt
        // irgendwo in den ersten Feldern, der Dump zeigt die echten Werte.
        private static unsafe string DescribeParamRaw(QuestTaskParameterSaveData p)
        {
            try
            {
                System.IntPtr objPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(p);
                int dumpLen = Math.Min(64, 64);
                var sb = new System.Text.StringBuilder("raw[");
                for (int i = 0; i < dumpLen; i++)
                {
                    sb.Append((*((byte*)objPtr + i)).ToString("X2"));
                    if (i < dumpLen - 1) sb.Append(' ');
                }
                sb.Append($"] kindReadback={p.Kind}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"(raw dump fehlgeschlagen: {ex.Message})";
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
