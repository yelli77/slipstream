using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 384 (Shop-Klon-Rewrite): Shops an JobsBoard-Docking-Bays.
    ///
    /// Ersetzt die Laufzeit-Umbiegung aus 311-375 (Prefix auf EnterAmenity, Postfix mit
    /// LoadAndShow("ShopScreen"), OnAmenityEnter-Schlucker, POI-/Label-Sweeps).
    /// Diagnose aus den Atlas-Prime-Logs: Diese Kette war zustandsabhaengig (Bay-Suche ueber
    /// das geteilte SharedAssets-Objekt lieferte je nach Enumerationsreihenfolge die falsche
    /// Bay, m_amenityType wurde dauerhaft an evtl. falscher Bay veraendert, das native
    /// Job-Board-Open wurde ohne Absicherung geschluckt) und lieferte ueberall dort
    /// "Andocken, nichts passiert", wo die Reihenfolge ungluecklich fiel.
    ///
    /// Neues Prinzip: Die JobsBoard-Bay wird beim Sektorladen zu einer ECHTEN Shop-Bay
    /// gemacht, indem die Felder der Shop-Bay DERSELBEN Station geklont werden
    /// (m_amenityType, m_shopDescription, m_amenityName, m_setPOISettingFromAmenity,
    /// POI-Settings + displayNameId). Damit laeuft Docking, Amenity-Event und Screen-Open
    /// exakt wie an einer nativen Shop-Bay - ohne Harmony-Patches.
    ///
    /// Regeln:
    ///  - Nur Stationen, in denen das Spiel selbst einen Shop anbietet (Shop-Bay mit
    ///    ShopDescription bzw. Shop-DockingBayGroup mit ShopDescription). Kein Fallback auf
    ///    Shops anderer Stationen/Sektoren: Station ohne Shop => Bay bleibt Job Board.
    ///  - Nur fuer verbundene MP-Clients; bei Disconnect werden die Originalwerte
    ///    wiederhergestellt.
    ///  - SharedAssets der Bay werden NICHT getauscht/veraendert (enthalten die
    ///    Docking-Cinematic-Timelines der Bay).
    ///  - Quest-Bays (DockingBay.IsQuestBay) werden nicht angefasst.
    /// </summary>
    public static class ShopAtJobBoardBays
    {
        private const string Tag = "384 ShopClone:";

        private sealed class CloneRecord
        {
            public DockingBay bay;
            public int bayId;
            public StationAmenity amenity;
            public ShopDescription shop;
            public string amenityName;
            public bool setPoiFromAmenity;
            public PointOfInterestSettings poiSettings;
            public string poiDisplayNameId;
        }

        private static readonly List<CloneRecord> clones = new List<CloneRecord>();
        private static readonly HashSet<int> clonedBayIds = new HashSet<int>();
        private static readonly HashSet<string> dumpedStations = new HashSet<string>();
        private static string lastSector = "none";
        private static int passesLeft = 0;
        private static bool active = false;

        /// <summary>Nur fuer verbundene MP-Clients (Singleplayer unveraendert).</summary>
        private static bool ShouldRewrite()
        {
            try
            {
                var client = global::StarTruckMP.StarTruckClient.StarTruckClient.client;
                return client != null && client.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Gate fuer die HUD-Klassifikation (DockingBayHUD.IsJobsBoard), unveraendert.</summary>
        public static bool ShouldRewriteForAmenityDisplay()
        {
            return ShouldRewrite();
        }

        /// <summary>
        /// Wird aus Plugin.Update im 5-s-Takt aufgerufen. Bei Sektorwechsel (und einige Ticks
        /// danach, weil Bays verzoegert erscheinen koennen) werden alle JobsBoard-Bays mit
        /// Shop-Station geklont. Idempotent.
        /// </summary>
        public static void ApplyShopClones()
        {
            try
            {
                if (!ShouldRewrite())
                {
                    if (active)
                    {
                        active = false;
                        RestoreAll();
                        lastSector = "none";
                    }
                    return;
                }
                if (!active)
                {
                    active = true;
                    lastSector = "none";
                }

                var sector = global::StarTruckMP.StarTruckClient.StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                if (sector != lastSector)
                {
                    // Neue Szene: alte Bay-Referenzen sind zerstoert.
                    lastSector = sector;
                    clones.Clear();
                    clonedBayIds.Clear();
                    dumpedStations.Clear();
                    passesLeft = 4;
                }
                if (passesLeft <= 0) return;
                passesLeft--;

                RunPass(sector);
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{Tag} ApplyShopClones Fehler: {ex}");
            }
        }

        private static void RunPass(string sector)
        {
            var allBays = UnityEngine.Object.FindObjectsOfType<DockingBay>();
            if (allBays == null || allBays.Length == 0) return;

            // Bays je Station gruppieren (Schluessel = nativer Station-Pointer).
            var byStation = new Dictionary<long, List<DockingBay>>();
            var stationObjs = new Dictionary<long, Station>();
            foreach (var bay in allBays)
            {
                if (bay == null) continue;
                Station st = null;
                try { st = bay.ParentStation; } catch { }
                if (st == null) continue;
                long key = (long)st.Pointer;
                if (!byStation.TryGetValue(key, out var list))
                {
                    list = new List<DockingBay>();
                    byStation[key] = list;
                    stationObjs[key] = st;
                }
                list.Add(bay);
            }

            int cloned = 0, noShop = 0, skippedQuest = 0, failed = 0;
            foreach (var kv in byStation)
            {
                var st = stationObjs[kv.Key];
                var bays = kv.Value;
                string stationName = SafeName(st);

                // Referenz: eine echte Shop-Bay dieser Station (+ deren ShopDescription).
                DockingBay refBay = null;
                ShopDescription shop = null;
                foreach (var b in bays)
                {
                    try
                    {
                        if (b.m_amenityType != StationAmenity.Shop) continue;
                        var sd = b.m_shopDescription;
                        if (sd == null) continue;
                        refBay = b;
                        shop = sd;
                        break;
                    }
                    catch { }
                }
                if (shop == null)
                {
                    // Fallback INNERHALB derselben Station: Shop-DockingBayGroup.
                    shop = FindStationGroupShop(st);
                    if (shop != null)
                    {
                        foreach (var b in bays)
                        {
                            try { if (b.m_amenityType == StationAmenity.Shop) { refBay = b; break; } } catch { }
                        }
                    }
                }

                DumpStationOnce(sector, stationName, bays, refBay, shop);

                foreach (var bay in bays)
                {
                    try
                    {
                        if (!DockingBayAmenityUtil.IsJobsBoardBay(bay)) continue;
                        int id = bay.GetInstanceID();
                        if (clonedBayIds.Contains(id)) continue;

                        if (shop == null)
                        {
                            // Spiel bietet an dieser Station keinen Shop an => Bay bleibt Job Board.
                            noShop++;
                            continue;
                        }
                        bool isQuestBay = false;
                        try { isQuestBay = bay.IsQuestBay; } catch { }
                        if (isQuestBay)
                        {
                            skippedQuest++;
                            StarTruckMP.Log.LogInfo($"{Tag} Quest-Bay uebersprungen: station='{stationName}' bay='{SafeName(bay)}'");
                            continue;
                        }

                        if (CloneOne(bay, refBay, shop, stationName)) cloned++;
                        else failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        StarTruckMP.Log.LogWarning($"{Tag} Klon an '{SafeName(bay)}' fehlgeschlagen: {ex.Message}");
                    }
                }
            }

            if (cloned > 0 || failed > 0 || skippedQuest > 0)
            {
                StarTruckMP.Log.LogInfo(
                    $"{Tag} Pass sector='{sector}': {cloned} JobsBoard-Bays zu Shop-Bays geklont, " +
                    $"{noShop} ohne Shop in Station (bleiben Job Board), {skippedQuest} Quest-Bays uebersprungen, {failed} Fehler " +
                    $"(gesamt geklont: {clones.Count}).");
            }
        }

        private static bool CloneOne(DockingBay bay, DockingBay refBay, ShopDescription shop, string stationName)
        {
            var poi = bay.m_dockingBayPOI;
            var refPoi = refBay != null ? refBay.m_dockingBayPOI : null;

            // Originalzustand sichern (fuer Restore bei Disconnect).
            var rec = new CloneRecord
            {
                bay = bay,
                bayId = bay.GetInstanceID(),
                amenity = bay.m_amenityType,
                shop = bay.m_shopDescription,
                amenityName = bay.m_amenityName,
                setPoiFromAmenity = bay.m_setPOISettingFromAmenity,
                poiSettings = poi != null ? poi.settings : null,
                poiDisplayNameId = poi != null ? poi.displayNameId : null,
            };

            // Bay-Konfiguration der Shop-Bay klonen: das liest die native DockingCoroutine
            // beim Andocken (m_amenityType/m_shopDescription) -> natives Shop-Open.
            bay.m_amenityType = StationAmenity.Shop;
            bay.m_shopDescription = shop;
            if (refBay != null)
            {
                try
                {
                    var nm = refBay.m_amenityName;
                    if (!string.IsNullOrEmpty(nm)) bay.m_amenityName = nm;
                    bay.m_setPOISettingFromAmenity = refBay.m_setPOISettingFromAmenity;
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"{Tag} amenityName/setPOI aus Referenz nicht uebernommen: {ex.Message}");
                }
            }

            // POI (Icon + Label) ueber die nativen Wege: Settings der echten Shop-Bay bzw.
            // m_poiSettingsShop der eigenen SharedAssets, displayNameId der Referenz.
            string poiInfo = "kein POI";
            if (poi != null)
            {
                try
                {
                    PointOfInterestSettings settings = refPoi != null ? refPoi.settings : null;
                    if (settings == null)
                    {
                        var shared = bay.m_sharedAssets;
                        if (shared != null) settings = shared.m_poiSettingsShop;
                    }
                    if (settings != null) poi.SetSettings(settings);
                    if (refPoi != null)
                    {
                        var dn = refPoi.displayNameId;
                        if (!string.IsNullOrEmpty(dn)) poi.displayNameId = dn;
                    }
                    poiInfo = $"POI umgestellt (settings={(settings != null)}, displayNameId='{poi.displayNameId}')";
                }
                catch (Exception ex)
                {
                    poiInfo = "POI-Fehler: " + ex.Message;
                }
            }

            clones.Add(rec);
            clonedBayIds.Add(rec.bayId);
            string shopName = null;
            try { shopName = shop.shopDisplayName; } catch { }
            StarTruckMP.Log.LogInfo(
                $"{Tag} geklont: station='{stationName}' bay='{SafeName(bay)}' -> Shop '{shopName}' " +
                $"(Referenz-Bay='{(refBay != null ? SafeName(refBay) : "keine (Gruppen-Shop)")}'; {poiInfo})");
            return true;
        }

        /// <summary>Shop-ShopDescription aus den DockingBayGroups DIESER Station (kein Fremd-Fallback).</summary>
        private static ShopDescription FindStationGroupShop(Station st)
        {
            try
            {
                var groups = st.DockingBayGroups;
                if (groups == null) return null;
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    if (g.amenityType != StationAmenity.Shop) continue;
                    var sd = g.shopDescription;
                    if (sd != null) return sd;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{Tag} DockingBayGroups nicht lesbar: {ex.Message}");
            }
            return null;
        }

        /// <summary>Stellt alle geaenderten Bays auf den Originalzustand zurueck (Disconnect).</summary>
        private static void RestoreAll()
        {
            int restored = 0;
            foreach (var rec in clones)
            {
                try
                {
                    var bay = rec.bay;
                    if (bay == null) continue;
                    bay.m_amenityType = rec.amenity;
                    bay.m_shopDescription = rec.shop;
                    bay.m_amenityName = rec.amenityName;
                    bay.m_setPOISettingFromAmenity = rec.setPoiFromAmenity;
                    var poi = bay.m_dockingBayPOI;
                    if (poi != null)
                    {
                        if (rec.poiSettings != null) poi.SetSettings(rec.poiSettings);
                        poi.displayNameId = rec.poiDisplayNameId;
                    }
                    restored++;
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"{Tag} Restore fehlgeschlagen: {ex.Message}");
                }
            }
            clones.Clear();
            clonedBayIds.Clear();
            if (restored > 0)
                StarTruckMP.Log.LogInfo($"{Tag} Disconnect: {restored} Bays auf Originalzustand zurueckgesetzt.");
        }

        /// <summary>
        /// Diagnose (einmal je Station und Sektor): Bay-Liste mit Amenity, Shop, SharedAssets-
        /// Pointer, IsQuestBay und POI-displayNameId - beantwortet, ob Bays sich SharedAssets
        /// teilen und welche Bays das Spiel als Quest-Bay fuehrt.
        /// </summary>
        private static void DumpStationOnce(string sector, string stationName, List<DockingBay> bays, DockingBay refBay, ShopDescription shop)
        {
            if (!dumpedStations.Add(sector + "|" + stationName)) return;
            try
            {
                string shopName = null;
                try { shopName = shop != null ? shop.shopDisplayName : null; } catch { }
                StarTruckMP.Log.LogInfo(
                    $"{Tag} Station '{stationName}' sector='{sector}': bays={bays.Count}, spielEigenerShop='{shopName ?? "KEIN"}', " +
                    $"referenzBay='{(refBay != null ? SafeName(refBay) : "-")}'");
                foreach (var b in bays)
                {
                    try
                    {
                        string sdName = null;
                        try { var sd = b.m_shopDescription; sdName = sd != null ? sd.shopDisplayName : null; } catch { }
                        string shared = "-";
                        try { var sa = b.m_sharedAssets; if (sa != null) shared = sa.Pointer.ToString("X"); } catch { }
                        bool quest = false;
                        try { quest = b.IsQuestBay; } catch { }
                        string dn = "-";
                        try { var p = b.m_dockingBayPOI; if (p != null) dn = p.displayNameId; } catch { }
                        StarTruckMP.Log.LogInfo(
                            $"{Tag}   bay='{SafeName(b)}' amenity={b.m_amenityType} shop='{sdName ?? "-"}' " +
                            $"shared=0x{shared} quest={quest} poiNameId='{dn}'");
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"{Tag}   Bay-Dump fehlgeschlagen: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{Tag} Station-Dump fehlgeschlagen: {ex.Message}");
            }
        }

        private static string SafeName(UnityEngine.Component c)
        {
            try { return c != null && c.gameObject != null ? c.gameObject.name : "null"; }
            catch { return "?"; }
        }
    }
}
