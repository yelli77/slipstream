using System;
using System.IO;
using Riptide;
using StarTruckMP.Utilities;
using HarmonyLib;
using StarTruckSaveData;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Synchronisiert die physischen Cargo-Container (CargoTracker.saveData.containers)
    /// zwischen allen Spielern im selben Sektor - Voraussetzung dafuer, dass ein von
    /// JobBoardSync synchronisierter Job (der per TrailerId-Parameter auf eine bestimmte
    /// Cargo-trackingId zeigt) beim Empfaenger ueberhaupt ein existierendes Objekt findet.
    ///
    /// Gleiches Muster wie JobBoardSync: Autoritaets-Client (niedrigste Spieler-ID im Sektor)
    /// liest CargoTracker.GetData(...).CargoSaveData.containers aus (IList&lt;CargoContainerSaveData&gt;,
    /// die auch trackingId enthaelt!) und schickt sie an alle anderen. Die Empfaenger wenden sie
    /// per CargoTracker.SetData(...) an - NICHT per CreateContainer(), weil CreateContainer immer
    /// eine neue trackingId vergibt (GetNextAvailableInstanceId) statt eine vorgegebene zu
    /// verwenden. Nur der SetData/saveData-Restore-Pfad (derselbe, den ein Spielstand-Load
    /// benutzt) erhaelt die trackingId - und die muss auf allen Clients identisch sein, damit
    /// die Job-Parameter (TrailerId) ueberhaupt auf dasselbe Objekt zeigen.
    ///
    /// Reihenfolge: wird VOR JobBoardSync ausgeloest (Hook auf CargoTracker.SpawnCurrentSectorCargo,
    /// das laeuft im Spiel vor der Job-Generierung fuer den Sektor).
    ///
    /// NOCH UNVERIFIZIERT (erster Test im Spiel noetig):
    /// - Ob `new SystemSaveData(cargoSaveData)` + CargoTracker.SetData(...) tatsaechlich
    ///   trackingIds 1:1 uebernimmt statt sie neu zu vergeben (das ist die Kernannahme dieses
    ///   ganzen Ansatzes - falls falsch, muessen Jobs/Cargo anders verknuepft werden).
    /// - Ob doppeltes Spawnen (Empfaenger hat ggf. schon eigene Cargo im Sektor generiert, bevor
    ///   der Sync ankommt) zu Duplikaten fuehrt. Falls ja: muss client-seitig vor dem Anwenden
    ///   geprueft werden, ob eine trackingId schon lokal existiert (CargoTracker.GetCargoRecordByTrackingId).
    /// </summary>
    public static class CargoSync
    {
        public static void OnLocalCargoSpawned()
        {
            try
            {
                var client = StarTruckClient.client;
                if (client == null || !client.IsConnected) return;
                if (!JobBoardSync.IsAuthorityForCurrentSector()) return;

                if (!CargoTracker.ready)
                {
                    StarTruckMP.Log.LogInfo("CargoSync: CargoTracker noch nicht ready, ueberspringe (naechster Trigger holt es nach).");
                    return;
                }

                // CargoTracker.GetData(context)/SystemSaveData-Union ist NICHT fuer den Live-
                // Gebrauch gedacht (wirft InvalidOperationException ausserhalb des eigentlichen
                // Speicher-Vorgangs - siehe Log von custom-build-176, gleiches Problem wie bei
                // JobBoardSync). CargoTracker hat aber eine direkte saveData-Property, kein
                // Kontext-Umweg noetig.
                var tracker = CargoTracker.Get();
                if (tracker == null) return;

                var cargoSave = tracker.saveData;
                if (cargoSave == null || cargoSave.containers == null) return;

                byte[] blob = SerializeContainers(cargoSave.containers);
                int count = Il2CppCount(cargoSave.containers);

                var msg = Message.Create(MessageSendMode.Reliable, (ushort)messageType.cargoSync);
                msg.AddString(StarTruckClient.currentSector);
                msg.AddBytes(blob);
                client.Send(msg);

                StarTruckMP.Log.LogInfo($"CargoSync: {count} Container fuer Sektor '{StarTruckClient.currentSector}' gesendet ({blob.Length} bytes).");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"CargoSync.OnLocalCargoSpawned fehlgeschlagen: {ex}");
            }
        }

        public static void HandleIncoming(MessageReceivedEventArgs e)
        {
            try
            {
                string sector = e.Message.GetString();
                byte[] blob = e.Message.GetBytes();

                if (sector != StarTruckClient.currentSector) return;
                if (JobBoardSync.IsAuthorityForCurrentSector()) return;

                var incoming = DeserializeContainers(blob);

                // MERGE statt Ersetzen: lokale Container, die NICHT in der eingehenden Liste
                // stehen (z.B. der eigene gehitchte Trailer aus einer schon vorher angenommenen
                // Mission), bleiben erhalten. Fuer trackingIds, die in beiden Listen vorkommen,
                // gewinnt die eingehende (Host-)Version. Ein harter Ersatz wuerde sonst Cargo
                // verwaisen, die zu einer eigenen bereits aktiven Mission gehoert.
                var tracker = CargoTracker.Get();
                Il2CppSystem.Collections.Generic.IList<CargoContainerSaveData> localContainers = null;
                if (tracker != null)
                {
                    localContainers = tracker.saveData?.containers;
                }

                var incomingIds = new System.Collections.Generic.HashSet<long>();
                for (int i = 0; i < incoming.Count; i++) incomingIds.Add(incoming[i].trackingId);

                var merged = new Il2CppSystem.Collections.Generic.List<CargoContainerSaveData>();
                int keptLocal = 0;
                if (localContainers != null)
                {
                    int localCount = Il2CppCount(localContainers);
                    for (int i = 0; i < localCount; i++)
                    {
                        var lc = localContainers[i];
                        if (!incomingIds.Contains(lc.trackingId))
                        {
                            merged.Add(lc);
                            keptLocal++;
                        }
                    }
                }
                for (int i = 0; i < incoming.Count; i++) merged.Add(incoming[i]);

                var cargoSave = new CargoSaveData();
                cargoSave.containers = merged.Cast<Il2CppSystem.Collections.Generic.IList<CargoContainerSaveData>>();

                CargoTracker.Get()?.SetData(new Il2CppSystem.Nullable<SystemSaveData>(new SystemSaveData(cargoSave)));
                StarTruckMP.Log.LogInfo($"CargoSync: Container fuer Sektor '{sector}' uebernommen ({incoming.Count} vom Host, {keptLocal} eigene behalten).");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"CargoSync.HandleIncoming fehlgeschlagen: {ex}");
            }
        }

        private static int Il2CppCount<T>(Il2CppSystem.Collections.Generic.IList<T> list)
        {
            if (list == null) return 0;
            return list.Cast<Il2CppSystem.Collections.Generic.ICollection<T>>().Count;
        }

        private static byte[] SerializeContainers(Il2CppSystem.Collections.Generic.IList<CargoContainerSaveData> containers)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            int count = Il2CppCount(containers);
            w.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                var c = containers[i];
                w.Write(c.trackingId);
                w.Write(c.cargoType ?? "");
                w.Write(c.corporation ?? "");
                w.Write(c.sectorName ?? "");
                w.Write(c.cargoBayId ?? "");
                w.Write(c.worldPosition.x); w.Write(c.worldPosition.y); w.Write(c.worldPosition.z);
                w.Write(c.worldRotation.x); w.Write(c.worldRotation.y); w.Write(c.worldRotation.z); w.Write(c.worldRotation.w);
                w.Write(c.linearVelocity.x); w.Write(c.linearVelocity.y); w.Write(c.linearVelocity.z);
                w.Write(c.angularVelocity.x); w.Write(c.angularVelocity.y); w.Write(c.angularVelocity.z);
                w.Write(c.towingPermission);
                w.Write(c.expiryDate);
                w.Write(c.damagePercent);
                w.Write(c.trailerHitpoints);
                w.Write(c.contentHitpoints);
                w.Write(c.multiTrailerHitchedToId);
                w.Write(c.multiTrailerHitchedFromId);
                w.Write(c.canExpire);
            }

            return ms.ToArray();
        }

        private static Il2CppSystem.Collections.Generic.List<CargoContainerSaveData> DeserializeContainers(byte[] blob)
        {
            var result = new Il2CppSystem.Collections.Generic.List<CargoContainerSaveData>();
            using var ms = new MemoryStream(blob);
            using var r = new BinaryReader(ms);

            ushort count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var c = new CargoContainerSaveData();
                c.trackingId = r.ReadInt64();
                c.cargoType = r.ReadString();
                c.corporation = r.ReadString();
                c.sectorName = r.ReadString();
                c.cargoBayId = r.ReadString();

                var pos = new Vector3Data(); pos.x = r.ReadSingle(); pos.y = r.ReadSingle(); pos.z = r.ReadSingle();
                c.worldPosition = pos;

                var rot = new QuaternionData(); rot.x = r.ReadSingle(); rot.y = r.ReadSingle(); rot.z = r.ReadSingle(); rot.w = r.ReadSingle();
                c.worldRotation = rot;

                var linVel = new Vector3Data(); linVel.x = r.ReadSingle(); linVel.y = r.ReadSingle(); linVel.z = r.ReadSingle();
                c.linearVelocity = linVel;

                var angVel = new Vector3Data(); angVel.x = r.ReadSingle(); angVel.y = r.ReadSingle(); angVel.z = r.ReadSingle();
                c.angularVelocity = angVel;

                c.towingPermission = r.ReadBoolean();
                c.expiryDate = r.ReadInt64();
                c.damagePercent = r.ReadSingle();
                c.trailerHitpoints = r.ReadDouble();
                c.contentHitpoints = r.ReadDouble();
                c.multiTrailerHitchedToId = r.ReadInt64();
                c.multiTrailerHitchedFromId = r.ReadInt64();
                c.canExpire = r.ReadBoolean();

                result.Add(c);
            }
            return result;
        }
    }

    [HarmonyPatch]
    public class CargoSyncPatches
    {
        // Laeuft nach dem lokalen Cargo-Spawn fuer den aktuellen Sektor - das passiert im Spiel
        // VOR der Job-Generierung (ProceduralJobGenerator braucht die CargoRecords als Input),
        // daher kommt CargoSync auch vor JobBoardSync beim Empfaenger an.
        [HarmonyPatch(typeof(CargoTracker), nameof(CargoTracker.SpawnCurrentSectorCargo))]
        [HarmonyPostfix]
        public static void SpawnCurrentSectorCargo_Postfix()
        {
            try { CargoSync.OnLocalCargoSpawned(); }
            catch (Exception ex) { StarTruckMP.Log.LogWarning($"SpawnCurrentSectorCargo_Postfix Fehler: {ex.Message}"); }
        }
    }
}
