using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Riptide;
using StarTruckMP.Utilities;
using HarmonyLib;
using StarTruckSaveData;
using FlatSharp;
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
    /// Build-328 (PLAN C): Native FlatSharp-Serialisierung.
    /// Der v1-Hand-Blob (BinaryWriter-Spiegel der Save-Datenstrukturen) konnte das FlatSharp-
    /// Union-Problem (QuestTaskParameterSaveData._Discriminator_k__BackingField + value) nie
    /// zuverlaessig loesen: sowohl der Union-CTor als auch der RawWrite mit il2cpp_field_get_offset
    /// endeten beim Empfaenger auf Kind=NONE + value=null (326er/327er-Logs).
    ///
    /// v2 umgeht das Problem voellig: Wir serialisieren ein ECHTES QuestSaveData-Objekt mit dem
    /// SPIEL-EIGENEN FlatSharp-Serializer (SaveSlotContainer.Serializer - statisches Property,
    /// ISerializer&lt;SaveSlotContainer&gt; aus FlatSharp.Runtime) und parsen auf der Gegenseite
    /// mit demselben Serializer zurueck. Discriminators und value-Pointer schreibt/liest dann
    /// exakt der nativen Serializer-Codepfad, der auch echte Spielstaende schreibt/liest.
    ///
    /// Container-Layout (Dekompilierung SaveSlotContainer.cs):
    ///   SaveSlotContainer { IList&lt;SaveContainer&gt; containers; SaveSlotMetadata metadata; }
    ///   SaveContainer { string key; Nullable&lt;SystemSaveData&gt; content; }
    ///   SystemSaveData = FlatSharp-Union, Variante 12 = QuestSaveData (SystemSaveData.cs: QuestSaveData = 12).
    ///   SaveSlotContainer.Serializer (statisch) liefert ISerializer&lt;SaveSlotContainer&gt;.
    ///
    /// Wire-Format v2: [1 Byte Format-Tag=2][4 Bytes Little-Endian Laenge][FlatSharp-Bytes eines
    /// SaveSlotContainer mit genau einem Container: key="mp_jobs", content=SystemSaveData(12, QuestSaveData)].
    /// v1-Blobs (Format-Tag ungleich 2 bzw. Startbyte != 2 an dieser Position) werden verworfen.
    /// Beide Spieler MUESSEN 328 haben (Updater zieht beide hoch).
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

                var questSave = new QuestSaveData();
                questSave.availableJobs = saveList.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();

                // custom-build-335: defekte Jobs VOR der Serialisierung entfernen -
                // ein Discriminator-0-Parameter crasht sonst den nativen FlatSharp-
                // Serializer ('Exception determining type of union. Discriminator = 0').
                int sanitizedOut = SanitizeQuestSaveData(questSave, "Senden");
                if (sanitizedOut > 0)
                    StarTruckMP.Log.LogWarning($"JobBoardSync.OnLocalJobsGenerated: {sanitizedOut} defekte Jobs aus dem Sync-Blob entfernt (Rest wird gesendet).");

                byte[] blob;
                int jobCount;
                try
                {
                    blob = SerializeQuestSaveDataNative(questSave, out jobCount);
                }
                catch (Exception serEx)
                {
                    // custom-build-335: NICHT mehr komplett abbrechen - bis auf Weiteres
                    // nur die Kennungen (JobBoardIdSync) senden, damit Clients zumindest
                    // die Board-Filterung behalten statt ein leeres Board zu bekommen.
                    StarTruckMP.Log.LogWarning($"JobBoardSync.OnLocalJobsGenerated: native Serialisierung fehlgeschlagen - Blob-Sync uebersprungen (Kennungs-Sync laeuft unabhaengig weiter): {serEx.Message}");
                    return;
                }
                if (sanitizedOut > 0)
                    StarTruckMP.Log.LogInfo($"JobBoardSync: {sanitizedOut} defekte Jobs entfernt, {jobCount} gesunde Jobs fuer Sektor '{StarTruckClient.currentSector}' im Blob.");

                ChunkedBlobTransfer.Send("job", (ushort)messageType.jobBoardSync, StarTruckClient.currentSector, blob);

                StarTruckMP.Log.LogInfo($"JobBoardSync: {jobCount} Jobs fuer Sektor '{StarTruckClient.currentSector}' gesendet ({blob.Length} bytes, native FlatSharp v2).");
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

                QuestSaveData questSave;
                try
                {
                    questSave = DeserializeQuestSaveDataNative(blob);
                }
                catch (Exception desEx)
                {
                    // v1-Blobs (alter Hand-Format) und korrupte Blobs landen hier - verwerfen.
                    StarTruckMP.Log.LogWarning($"JobBoardSync.HandleIncoming: Deserialize fehlgeschlagen (Sektor '{sector}', {blob?.Length ?? 0} bytes) - Blob verworfen, warte auf naechsten Sync: {desEx.Message}");
                    return;
                }

                // QuestTracker kann beim Empfaenger im Sync-Moment noch nicht ready sein
                // (Ready-Flag hinkt der lokalen Generierung hinterher). ApplyRestore-NREs
                // landen in der Retry-Queue (TryApplyPending wartet auf QuestTracker.ready).
                try
                {
                    ApplyRestore(sector, questSave);
                }
                catch (Exception applyEx)
                {
                    // ApplyRestore toucht QuestTracker (Il2Cpp) - im Sektorwechsel-Fenster
                    // kann das NREn. NICHT endgueltig verwerfen, sondern in die Retry-Queue.
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
        // value), killt damit ALLE Jobs. Deshalb: job-weise ueber QuestTracker.
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

        // Diagnose fuer EINEN Job - ALLE Parameter-Namen mit Kind und value loggen.
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
                        // Generisch - Kind + name + value JEDES Parameters loggen.
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

        // ItemKind -> Name der typed Value-Property an QuestTaskParameterSaveData.
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
                // Struktureller Fehler (Teardown-Fenster o.ae.), der sich durch Wiederholen
                // nicht heilt. Sofort verwerfen.
                StarTruckMP.Log.LogWarning($"JobBoardSync: Retry verworfen (Sektor '{pending.sector}', Versuch {pending.attempts}) - struktureller Fehler, Wiederholung sinnlos: {retryEx.Message}");
                pendingRestore = null;
            }
        }

        // =====================================================================
        // Build-328 PLAN C: Native FlatSharp-Serialisierung (jobFormatVersion=2)
        // =====================================================================
        //
        // v1 (Hand-Format via BinaryWriter) konnte das QuestTaskParameterSaveData-Union
        // (Discriminator+value) nie korrekt uebertragen: Der Discriminator las sich beim
        // Empfaenger immer als NONE (0), value immer als null - egal ob Union-CTor oder
        // RawWrite mit il2cpp_field_get_offset. Der Kind-Getter liest den Discriminator
        // offenbar ueber einen anderen Pfad als den reinen Feld-Offset.
        //
        // v2 laesst das Spiel selbst serialisieren:
        //   - QUESTSAVE-DATEN landen als echtes QuestSaveData-Objekt in einem
        //     SaveSlotContainer (Spiel-Klasse mit statischem Serializer-Property).
        //   - SaveSlotContainer.Serializer.Write(container) produziert byte[] mit sauber
        //     geschriebenen Discriminators (der native Serializer kennt den Union-Layout).
        //   - SaveSlotContainer.Serializer.Parse(bytes) erzeugt beim Empfaenger ein NEUES
        //     QuestSaveData-Objekt ueber den nativen FlatSharp-Deserialisierungs-Pfad -
        //     derselbe Pfad wie beim echten Spielstand-Load. Discriminators + value-Pointer
        //     stimmen garantiert (das Spiel liest seine eigenen Spielstaende damit).
        //
        // Dekompiliert (Assembly-CSharp dekompiliert am 13.09.):
        //   SaveSlotContainer.Serializer : ISerializer<SaveSlotContainer> (statisch, sauber)
        //   SaveSlotContainer { IList<SaveContainer> containers; SaveSlotMetadata metadata; }
        //   SaveContainer { string key; Nullable<SystemSaveData> content; }
        //   SystemSaveData( QuestSaveData ) - Union-CTor, Item-Index 12.
        //   SaveContainer.SetContent/GetContent ueber Nullable<SystemSaveData>.

        private const byte WIRE_FORMAT_V2 = 2;

        private static FlatSharp.ISerializer<SaveSlotContainer> _ssSerializer = null;
        private static bool _ssSerializerResolved = false;

        private static FlatSharp.ISerializer<SaveSlotContainer> GetSaveSlotContainerSerializer()
        {
            if (!_ssSerializerResolved)
            {
                _ssSerializer = SaveSlotContainer.Serializer;
                _ssSerializerResolved = true;
                if (_ssSerializer == null)
                    StarTruckMP.Log.LogWarning("JobBoardSync: SaveSlotContainer.Serializer ist NULL - native Serialisierung unmoeglich.");
                else
                    StarTruckMP.Log.LogInfo($"JobBoardSync: SaveSlotContainer.Serializer aufgeloest ({_ssSerializer.GetType().Name}).");
            }
            return _ssSerializer;
        }

        // Build-331: Managed Union-CTor 'new SystemSaveData(questSave)' liess den Discriminator
        // auf 0 (NONE) -> GetMaxSize crashte mit 'Exception determining type of union.
        // Discriminator = 0'. Fix: Union manuell aufbauen ueber die nativen Feld-Wrapper:
        //   _Discriminator_k__BackingField (byte, direkter Offset-Write im Interop-Code)
        //   value (Il2CppSystem.Object, il2cpp_gc_wbarrier_set_field im Interop-Code)
        // Beide Setter sind reine Feldschreibungen (keine runtime_invoke), der native
        // GetMaxSizeOf(SystemSaveData) liest genau diese beiden Felder.
        private const byte UNION_ITEM_KIND_QUESTSAVEDATA = 12;

        private static SystemSaveData CreateQuestSaveDataUnion(QuestSaveData questSave)
        {
            var ssd = new SystemSaveData(); // Default-CTor, keine Union-Zuweisung
            ssd._Discriminator_k__BackingField = UNION_ITEM_KIND_QUESTSAVEDATA;
            ssd.value = questSave; // native Feld-Wrapper (wbarrier), setzt den Union-Value direkt

            byte disc = ssd._Discriminator_k__BackingField;
            var valObj = ssd.value;
            StarTruckMP.Log.LogInfo($"JobBoardSync: SystemSaveData-Union manuell: disc={disc} (erwartet {UNION_ITEM_KIND_QUESTSAVEDATA}), value={(valObj != null ? "gesetzt" : "NULL")}");
            if (disc != UNION_ITEM_KIND_QUESTSAVEDATA)
                throw new InvalidOperationException($"SystemSaveData-Discriminator-Write fehlgeschlagen (disc={disc})");
            return ssd;
        }

        // =====================================================================
        // custom-build-335: Zentrales Sanitizing der QuestSave-Daten.
        //
        // Zwei Live-Probleme aus den Logs von custom-build-335:
        //  a) 'InvalidOperationException: Exception determining type of union.
        //     Discriminator = 0' beim nativen FlatSharp-Serialisieren: ein
        //     QuestTaskParameterSaveData mit Kind=0 (NONE, ungesetzter Variant)
        //     oder einem NULL-Value laesst den Serializer crashen - und der alte
        //     catch→return-Pfad ließClients OHNE Jobs fuer den Sektor zurueck.
        //  b) NRE beim nativen Restore (QuestTaskParameter.GenerateSector →
        //     QuestTracker.RestoreAvailableJobs): ein Parameter ohne auflösbare
        //     Property-Referenz killt RestoreAvailableJobs strukturell (10/10
        //     Retries failed, Blob verworfen).
        //
        // Beide Probleme haben dieselbe Wurzel (defekte Parameter-Union) und
        // werden deshalb an EINER Stelle behandelt: SanitizeQuestSaveData laeuft
        // VOR der Serialisierung (Seite Sender) und wird via Deserialize-Layout
        // auch vor ApplyRestore wirksam, weil nur noch gesäuberte Jobs im Blob
        // landen. Defekte Jobs werden GANZ entfernt (LogWarning mit Job-Id +
        // Parameter-Index), gesunde Jobs des Sektors bleiben erhalten.
        // =====================================================================

        // Entfernt alle Jobs, deren Parameter-Union nicht serialisierbar/auflösbar
        // ist (Kind = NONE/0 oder value null bzw. typed value-Property null).
        // Liefert die Anzahl entfernter Jobs zurueck.
        private static int SanitizeQuestSaveData(QuestSaveData questSave, string context)
        {
            var jobs = questSave?.availableJobs;
            if (jobs == null) return 0;

            int removed = 0;
            var keep = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>();
            int count = Il2CppCount(jobs);
            for (int i = 0; i < count; i++)
            {
                var job = jobs[i];
                int badParam = FindBadParameterIndex(job);
                if (badParam >= 0)
                {
                    removed++;
                    StarTruckMP.Log.LogWarning($"JobBoardSync[{context}]: Job '{JobIdOf(job)}' (Index {i}) entfernt - Parameter {badParam} hat ungueltige Union (Kind=0/NONE oder value=null).");
                    continue;
                }
                keep.Add(job);
            }

            if (removed > 0)
                questSave.availableJobs = keep.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();
            return removed;
        }

        // Liefert den Index des ERSTEN defekten Parameters oder -1, wenn alle ok sind.
        // Defekt = Kind ist NONE (Discriminator 0, kein gesetzter Variant - FlatSharp
        // wirft sonst 'Exception determining type of union. Discriminator = 0') ODER
        // die typed Value-Property des Variants ist null (NRE-Quelle im nativen
        // GenerateSector/RestoreAvailableJobs-Pfad).
        private static int FindBadParameterIndex(QuestInstanceSaveData job)
        {
            try
            {
                var pars = job?.generatedParameters;
                if (pars == null) return -1;
                int pc = Il2CppCount(pars);
                for (int p = 0; p < pc; p++)
                {
                    var par = pars[p];
                    try
                    {
                        var kind = par.Kind;
                        if (kind == default(QuestTaskParameterSaveData.ItemKind))
                            return p; // Discriminator 0 = ungesetzter Variant
                        string prop = KindToProp(kind);
                        if (prop == null) return p; // unbekannter Variant nicht sicher syncbar
                        var pi = typeof(QuestTaskParameterSaveData).GetProperty(prop);
                        if (pi == null) return p;
                        if (pi.GetValue(par) as Il2CppSystem.Object == null) return p;
                    }
                    catch
                    {
                        return p; // Parameter nicht lesbar -> im Zweifel rausschmeissen
                    }
                }
            }
            catch
            {
                return 0; // Parameterliste selbst nicht lesbar -> Job defekt
            }
            return -1;
        }

        private static string JobIdOf(QuestInstanceSaveData job)
        {
            try { return job?.id ?? "<null-id>"; } catch { return "<unreadable>"; }
        }

        // SERIALISIEREN (Sender): QuestSaveData -> SaveSlotContainer -> native FlatSharp-Bytes.
        private static byte[] SerializeQuestSaveDataNative(QuestSaveData questSave, out int jobCount)
        {
            jobCount = 0;
            var serializer = GetSaveSlotContainerSerializer();
            if (serializer == null)
                throw new InvalidOperationException("SaveSlotContainer.Serializer nicht aufgeloest");

            // Container mit key="mp_jobs" + content=SystemSaveData(Union idx 12 = QuestSaveData)
            var container = new SaveContainer();
            container.key = "mp_jobs";
            var ssd = CreateQuestSaveDataUnion(questSave);
            container.content = new Il2CppSystem.Nullable<SystemSaveData>(ssd);

            var ssc = new SaveSlotContainer();
            var containerList = new Il2CppSystem.Collections.Generic.List<SaveContainer>();
            ssc.containers = containerList.Cast<Il2CppSystem.Collections.Generic.IList<SaveContainer>>();
            containerList.Add(container);
            ssc.metadata = new SaveSlotMetadata();

            // GetMaxSize -> Puffer -> Write (nativ, via ISerializer<T>-Extension Write<T>)
            int maxSize = serializer.GetMaxSize(ssc);
            if (maxSize <= 0)
                throw new InvalidOperationException($"GetMaxSize lieferte {maxSize}");
            var buf = new Il2CppStructArray<byte>(maxSize + 16);
            int written = serializer.Write(buf, ssc);
            if (written <= 0)
                throw new InvalidOperationException($"Write lieferte {written}");

            jobCount = Il2CppCount(questSave.availableJobs);

            // Wire: [tag=2][4 len LE][payload]
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(WIRE_FORMAT_V2);
            w.Write(written);
            var outBytes = new byte[written];
            for (int i = 0; i < written; i++) outBytes[i] = buf[i];
            w.Write(outBytes, 0, written);
            return ms.ToArray();
        }

        // DESERIALISIEREN (Empfaenger): FlatSharp-Bytes -> SaveSlotContainer -> QuestSaveData.
        private static QuestSaveData DeserializeQuestSaveDataNative(byte[] blob)
        {
            if (blob == null || blob.Length < 5)
                throw new InvalidOperationException($"Blob zu kurz ({blob?.Length ?? 0} bytes)");
            if (blob[0] != WIRE_FORMAT_V2)
                throw new InvalidOperationException($"Blob ist nicht v2 (Format-Tag {blob[0]}) - vermutlich v1 vom alten Client (<=327), wird verworfen");

            int len = blob[1] | (blob[2] << 8) | (blob[3] << 16) | (blob[4] << 24);
            if (len <= 0 || len > blob.Length - 5)
                throw new InvalidOperationException($"Blob-Laenge {len} passt nicht zu {blob.Length} bytes");

            var payload = new Il2CppStructArray<byte>(len);
            System.Buffer.BlockCopy(blob, 5, payload, 0, len); // managed array copy

            var serializer = GetSaveSlotContainerSerializer();
            if (serializer == null)
                throw new InvalidOperationException("SaveSlotContainer.Serializer nicht aufgeloest");

            // Nativ PARSEN - derselbe Pfad wie beim echten Spielstand-Load.
            var parsed = FlatSharp.ISerializerExtensions.Parse(serializer, payload);
            if (parsed == null)
                throw new InvalidOperationException("Parse lieferte null");

            var containers = parsed.containers;
            if (containers == null) throw new InvalidOperationException("parsed.containers == null");
            int cc = Il2CppCount(containers);
            for (int i = 0; i < cc; i++)
            {
                var c = containers[i];
                if (c?.key != "mp_jobs") continue;
                if (c.content == null || !c.content.HasValue) continue;
                // SystemSaveData-Union: Item 12 = QuestSaveData
                if (c.content.Value.QuestSaveData is QuestSaveData qsd && qsd != null)
                    return qsd;
            }
            throw new InvalidOperationException("kein mp_jobs-Container mit QuestSaveData gefunden");
        }

        // Build-328 Roundtrip-Verifikation (nur bei Debug-Flag): eigenes QuestSaveData nativ
        // serialisieren + deserialisieren und Kinds vergleichen. Log: 'roundtrip kind match: X/Y'.
        public static void RunRoundtripVerification()
        {
            try
            {
                var liveJobs = ProceduralJobGenerator.GetAvailableJobs();
                int total = 0, matched = 0;
                var questSave = new QuestSaveData();
                if (liveJobs != null && liveJobs.Count > 0)
                {
                    var saveList = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>();
                    int liveCount = liveJobs.Count;
                    for (int i = 0; i < liveCount; i++)
                        saveList.Add(liveJobs[i].GetData());
                    questSave.availableJobs = saveList.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();
                }
                else
                {
                    // Kein Live-Board: leeres QuestSaveData - Roundtrip ueber Struktur-only.
                    questSave.availableJobs = new Il2CppSystem.Collections.Generic.List<QuestInstanceSaveData>().Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();
                }

                byte[] blob = SerializeQuestSaveDataNative(questSave, out _);
                var rt = DeserializeQuestSaveDataNative(blob);

                // Kinds aller Parameter vergleichen (326/327-Kern-Problemfeld: Discriminator).
                var jobsBefore = questSave.availableJobs;
                var jobsAfter = rt.availableJobs;
                int jbCount = Il2CppCount(jobsBefore);
                int jaCount = Il2CppCount(jobsAfter);
                StarTruckMP.Log.LogInfo($"JobBoardSync.Roundtrip: Jobs vor={jbCount} nach={jaCount}.");
                int n = Math.Min(jbCount, jaCount);
                for (int j = 0; j < n; j++)
                {
                    CompareJobKinds(jobsBefore[j], jobsAfter[j], ref total, ref matched, j);
                }
                StarTruckMP.Log.LogInfo($"JobBoardSync: roundtrip kind match: {matched}/{total}");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.Roundtrip-Verifikation fehlgeschlagen: {ex}");
            }
        }

        private static void CompareJobKinds(QuestInstanceSaveData before, QuestInstanceSaveData after, ref int total, ref int matched, int jobIdx)
        {
            int pcB = Il2CppCount(before?.generatedParameters);
            int pcA = Il2CppCount(after?.generatedParameters);
            if (pcB != pcA)
            {
                StarTruckMP.Log.LogWarning($"JobBoardSync.Roundtrip: Job {jobIdx} '{before?.id}' Parameter-Anzahl {pcA} != {pcB}");
                total += pcB; // die Soll-Werte zaehlen
                return;
            }
            for (int p = 0; p < pcB; p++)
            {
                var kb = before.generatedParameters[p].Kind;
                var ka = after.generatedParameters[p].Kind;
                total++;
                if (kb == ka) matched++;
                else StarTruckMP.Log.LogWarning($"JobBoardSync.Roundtrip: Job {jobIdx} '{before?.id}' Param {p} Kind {ka} != {kb}");
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
            // Build-333 (PLAN B Same-Seed lite): Kennungs-Broadcast - unabhaengig vom Blob-Sync.
            try { JobBoardIdSync.OnLocalJobsGenerated(); }
            catch (Exception ex) { StarTruckMP.Log.LogWarning($"GenerateJobsForAllSectors_Postfix (IdSync) Fehler: {ex.Message}"); }
        }
    }
}