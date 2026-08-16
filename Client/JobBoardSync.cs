using System;
using System.IO;
using System.Collections.Generic;
using Riptide;
using StarTruckMP.Utilities;
using HarmonyLib;
using StarTruckSaveData;

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

                var tracker = QuestTracker.Get();
                if (tracker == null) return;

                var emptyCurrent = new Il2CppSystem.Nullable<SystemSaveData>();
                var saveDataOpt = tracker.GetData(emptyCurrent, SaveState.GetDataContext.SaveGame);
                if (saveDataOpt == null || !saveDataOpt.HasValue)
                {
                    StarTruckMP.Log.LogWarning("JobBoardSync: QuestTracker.GetData() lieferte keinen Wert.");
                    return;
                }

                var questSave = saveDataOpt.Value.QuestSaveData;
                if (questSave == null || questSave.availableJobs == null)
                {
                    return;
                }

                byte[] blob = SerializeJobs(questSave.availableJobs);
                int jobCount = Il2CppCount(questSave.availableJobs);

                var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.jobBoardSync);
                msg.AddString(StarTruckClient.currentSector);
                msg.AddBytes(blob);
                client.Send(msg);

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
                string sector = e.Message.GetString();
                byte[] blob = e.Message.GetBytes();

                // Nur uebernehmen wenn wir selbst gerade in diesem Sektor sind - Jobboards
                // anderer Sektoren wuerden sonst den lokalen ProceduralJobGenerator-State fuer
                // den falschen Sektor ueberschreiben.
                if (sector != StarTruckClient.currentSector) return;

                // Sind wir selbst die Autoritaet fuer diesen Sektor, ignorieren wir eingehende
                // Syncs - unser eigener Stand ist die Quelle der Wahrheit.
                if (IsAuthorityForCurrentSector()) return;

                var jobs = DeserializeJobs(blob);

                var questSave = new QuestSaveData();
                questSave.availableJobs = jobs.Cast<Il2CppSystem.Collections.Generic.IList<QuestInstanceSaveData>>();

                QuestTracker.Get()?.RestoreAvailableJobs(questSave);
                StarTruckMP.Log.LogInfo($"JobBoardSync: Jobs fuer Sektor '{sector}' uebernommen ({jobs.Count}).");
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
