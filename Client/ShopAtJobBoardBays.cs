using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 311c (Dock-Rewrite): Shop an JobsBoard-Docking-Bays.
    ///
    /// Hintergrund: Das Jobboard ist seit custom-build-288/307 jederzeit per J-Taste
    /// verfuegbar (Client/JobBoardComputer.cs) - die JobsBoard-Docking-Bays am Station-
    /// Dock sind dafuer redundant. Diese Patches leiten den Dock-Ablauf an diesen Bays
    /// auf den SHOP um, damit mehr freie Bays fuer Spieler existieren, die in den Shop
    /// wollen. Echte Shop-Bays verhalten sich unverändert.
    ///
    /// Mechanismus (statisch gegen die interop Assembly-CSharp.dll verifiziert):
    ///   StationAmenity-Enum: None=0, JobsBoard=1, Shop=2, Repairs=3, PaintShop=4,
    ///   BodyShop=5, UpgradeShop=6, ItemDelivery=7, FuelPump=8, ParkingBay=9
    ///   (ilspycmd -t StationAmenity /bepinex/interop/Assembly-CSharp.dll).
    ///
    /// 311c: ECHTER Dock-Pfad (verifiziert via ilspycmd-Dekompilierung): Die native
    /// DockingCoroutine (DockingBay.&lt;DockingCoroutine&gt;d__82, liest m_amenityType
    /// der Bay) ruft nach dem Dock-Cinematic
    ///   DockingBaySharedAssets.EnterAmenity(StationAmenity amenityType,
    ///   string nameStringId, ShopDescription shopDesc, ItemDeliveryDescription
    ///   deliveryDesc, bool showScreenImmediately)
    /// auf; diese Methode baut das AmenityEventArgs und feuert m_enterAmenityEvent.
    /// Der vorherige Prefix auf TruckAmenityTerminal.OnAmenityEnter feuerte beim Dock
    /// NICHT (das ist der AmenityTriggerZone-Pfad im Stationsinneren) und wurde entfernt.
    /// Der neue Prefix schreibt amenityType (ref) JobsBoard -> Shop um und injiziert
    /// per ref die shopDescription der ParentStation (Shop-DockingBayGroup), BEVOR der
    /// native Handler die EventArgs baut. Bay-Aufloesung: __instance (ScriptableObject)
    /// -> DockingBay mit m_sharedAssets == __instance -> ParentStation.
    ///
    /// 311b (POI-Marker): Die POI-Marker der JobsBoard-Bays zeigten weiterhin
    /// 'Auftragsborse' (Klemmbrett-Icon). Statisch verifizierte natives Design:
    ///   - DockingBay.ConfigurePOI(bool) liest m_setPOISettingFromAmenity und ruft
    ///     dann m_sharedAssets.GetPOISettings(m_amenityType) und haengt das Ergebnis
    ///     an m_dockingBayPOI.settings (der PointsOfInterest-Manager rendert daraus
    ///     Icon + Label). CallerCount von ConfigurePOI ist 2.
    ///   - RegisterPointOfInterest.SetSettings(PointOfInterestSettings) ist public
    ///     und aktualisiert die zugehoerigen Marker (der POI-Manager liest pro Frame
    ///     entry.settings).
    ///   - Der angezeigte Label-Text kommt aus RegisterPointOfInterest.displayNameId
    ///     (String-ID); die POI-Settings liefern nur Icon/Prefab/Farben.
    /// Native Pfade, die wir nutzen (KEIN eigenes Overlay):
    ///   1. PointOfInterestSettings austauschen: DockingBay.m_sharedAssets
    ///      .m_poiSettingsJobsBoard = m_poiSettingsShop (fuer zukuenftige
    ///      ConfigurePOI-Aufrufe), plus bay.ConfigurePOI(bay.gameObject.activeSelf)
    ///      als Re-Apply-Versuch.
    ///   2. RegisterPointOfInterest.SetSettings(shopSettings) direkt an der live
    ///      registrierten POI-Instanz (Wirksamkeit unabhaengig vom CallerCount-Design
    ///      von ConfigurePOI).
    ///   3. displayNameId auf shopDescription.shopDisplayName setzen, damit das
    ///      Label den Shop-Namen zeigt (nicht die String-ID 'Auftragsboerse').
    /// Fallback/diag: Wenn m_dockingBayPOI oder Shop-Settings null sind, Log-Warnung
    /// (Praefix 311b) und Bay-Anzeige unveraendert.
    ///
    /// DockingBayHUD-Konsistenz: DockingBayHUD.IsJobsBoard fragt
    /// ShouldRewriteForAmenityDisplay() ab und behandelt die Bays als Shop-Bays,
    /// damit kein 'Modified - Job Board (Jobs)'-HUD-Marker mehr erzeugt wird.
    /// Singleplayer ohne MP-Client: Patches greifen gar nicht (Gate auf IsConnected).
    /// </summary>
    public static class ShopAtJobBoardBays
    {
        private static bool harmonyApplied = false;

        /// <summary>
        /// True, wenn dieser Dock-Event-Ablauf umgeschrieben werden soll:
        /// nur fuer verbundene MP-Clients (Singleplayer unveraendert lassen).
        /// Konsistent mit DockingBayHUD.RefreshDockingBays / JobBoardComputer.CheckToggle,
        /// die ebenfalls auf StarTruckClient.client.IsConnected gaten.
        /// </summary>
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

        /// <summary>
        /// 311b: Oeffentliches Gate fuer die Anzeige-Umschreibung (POI/HUD).
        /// Derselbe Gate wie ShouldRewrite() - verbundener MP-Client.
        /// </summary>
        public static bool ShouldRewriteForAmenityDisplay()
        {
            return ShouldRewrite();
        }

        /// <summary>
        /// Liest die shopDescription der ersten DockingBayGroup der Station, deren
        /// amenityType == Shop ist (Station.DockingBayGroups).
        /// </summary>
        private static ShopDescription FindStationShopDescription(Station stationObj)
        {
            if (stationObj == null) return null;
            try
            {
                var groups = stationObj.DockingBayGroups;
                if (groups == null)
                {
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: Station.DockingBayGroups ist null/leer.");
                    return null;
                }
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    if (g.amenityType != StationAmenity.Shop) continue;
                    var sd = g.shopDescription;
                    if (sd != null) return sd;
                    // Gruppe vorhanden, aber Description null -> continue (nicht abbrechen)
                }
                StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: Station hat keine Shop-DockingBayGroup mit shopDescription.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.FindStationShopDescription fehlgeschlagen: {ex.Message}");
            }
            return null;
        }

        public static void Apply()
        {
            if (harmonyApplied) return;
            harmonyApplied = true;
            var harmony = new Harmony("StarTruckMP.ShopAtJobBoardBays");

            // 313: Ziel-Suche gelockt. Root Cause des 312-Fehlschlags (BepInEx-Log
            // 'Patchziel ... nicht gefunden'): Die interop-Proxy-Signatur ist
            //   public unsafe bool EnterAmenity(StationAmenity amenityType,
            //       string nameStringId, ShopDescription shopDesc,
            //       ItemDeliveryDescription deliveryDesc, bool showScreenImmediately = true)
            // ABER: DockingBaySharedAssets.cs im Proxy beginnt mit `using Il2CppSystem;`,
            // d.h. das 'string' der Signatur ist Il2CppSystem.String (via ilspycmd -t
            // DockingBaySharedAssets /bepinex/interop/Assembly-CSharp.dll verifiziert,
            // NativeMethodInfoPtr_..._StationAmenity_String_ShopDescription_..., Token
            // 100671329). Der alte Match 'ps[1].ParameterType == typeof(string)' verglich
            // System.String mit Il2CppSystem.String und scheiterte dadurch IMMER.
            //
            // Neue Strategie: nur Methodenname + Parameteranzahl matchen; alle
            // Kandidaten mit Parametertypen ins Log; wenn mehr als einer, den mit
            // StationAmenity an Position 0 (und ShopDescription an Position 2)
            // bevorzugen. IL2CPP.GetIl2CppMethodByToken ist hier nicht noetig: Der
            // Proxy-Methodenkoerper ruft selbst via NativeMethodInfoPtr in den nativen
            // Code - Harmony patched den Proxy, das genuegt (gleicher Pfad wie bei
            // OnAmenityEnter, der funktioniert hat).
            MethodInfo mi = null;
            string targetDesc = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t == null || t.Name != "DockingBaySharedAssets") continue;
                        MethodInfo fallbackCandidate = null;
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (m.Name != "EnterAmenity") continue;
                            var ps = m.GetParameters();
                            var sig = string.Join(", ", Array.ConvertAll(ps, p => p.ParameterType.FullName));
                            StarTruckMP.Log.LogInfo($"313 EnterAmenity-Kandidat: {t.FullName}.{m.Name}({sig})");
                            if (ps.Length != 5) continue;
                            var isPrimary = ps[0].ParameterType.Name == "StationAmenity"
                                         && ps[2].ParameterType.Name == "ShopDescription";
                            if (isPrimary)
                            {
                                mi = m;
                                targetDesc = $"{t.Name}.{m.Name}(StationAmenity, {ps[1].ParameterType}, ShopDescription, {ps[3].ParameterType}, bool)";
                                break;
                            }
                            fallbackCandidate ??= m;
                        }
                        if (mi == null && fallbackCandidate != null)
                        {
                            mi = fallbackCandidate;
                            targetDesc = $"{t.Name}.{fallbackCandidate.Name}(FALLBACK: {fallbackCandidate})";
                        }
                        if (mi != null) break;
                    }
                    if (mi != null) break;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.Apply: Ziel-Suche fehlgeschlagen: {ex.Message}");
            }

            if (mi == null)
            {
                StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays.Apply: Patchziel DockingBaySharedAssets.EnterAmenity nicht gefunden - Feature inaktiv.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(ShopAtJobBoardBays), nameof(EnterAmenityPrefix));
            harmony.Patch(mi, prefix: prefix);
            StarTruckMP.Log.LogInfo($"311 ShopAtJobBoardBays.Apply: Harmony-Patch auf {targetDesc} registriert (Docking-Pfad 311c).");
        }

        // ── Prefix: rewrite the JobsBoard dock amenity into a Shop amenity ──

        private static int rewriteCount = 0;

        /// <summary>
        /// Prefix vor DockingBaySharedAssets.EnterAmenity - DER Punkt, den die native
        /// DockingCoroutine nach dem Dock-Cinematic aufruft. amenityType und shopDesc
        /// werden per ref umgeschrieben, BEVOR der native Handler das AmenityEventArgs
        /// baut und m_enterAmenityEvent feuert.
        /// </summary>
        // ReSharper disable once RedundantAssignment
        public static void EnterAmenityPrefix(
            DockingBaySharedAssets __instance,
            ref StationAmenity amenityType,
            ref ShopDescription shopDesc)
        {
            try
            {
                if (!ShouldRewrite()) return;
                if (amenityType != StationAmenity.JobsBoard) return; // echte Bays unveraendert

                var bay = FindBayForSharedAssets(__instance);
                if (bay == null)
                {
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: DockingBay fuer SharedAssets nicht gefunden - Bay bleibt Jobboard (Fallback).");
                    return;
                }

                var station = bay.ParentStation;
                if (station == null)
                {
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: DockingBay.ParentStation null - Bay bleibt Jobboard (Fallback).");
                    return;
                }

                var stationShop = FindStationShopDescription(station);
                if (stationShop == null)
                {
                    // Sauberer Fallback: Bay verhaelt sich wie bisher (= Jobboard via Dock).
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: keine ShopDescription an der Station verfuegbar - Bay bleibt Jobboard (Fallback).");
                    return;
                }

                amenityType = StationAmenity.Shop;
                shopDesc = stationShop;

                rewriteCount++;
                StarTruckMP.Log.LogInfo($"311 ShopAtJobBoardBays: JobsBoard-Dock zu Shop umgeschrieben (#{rewriteCount}, bay={bay.gameObject?.name}).");
            }
            catch (Exception ex)
            {
                // Niemals crashen - im Zweifel laeuft der native Ablauf unveraendert.
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.EnterAmenityPrefix Fehler: {ex}");
            }
        }

        /// <summary>
        /// Findet die DockingBay, deren m_sharedAssets == sharedAssets ist (311c).
        /// DockingBay.m_sharedAssets ist eine public Property auf dem interop Proxy.
        /// Wrapper-Identitaet kann pro Zugriff wechseln -> Pointer-Vergleich als
        /// verlaesslicheres Kriterium zusaetzlich zu ReferenceEquals.
        /// </summary>
        private static DockingBay FindBayForSharedAssets(DockingBaySharedAssets shared)
        {
            if (shared == null) return null;
            try
            {
                var bays = UnityEngine.Object.FindObjectsOfType<DockingBay>();
                foreach (var bay in bays)
                {
                    if (bay == null) continue;
                    try
                    {
                        var sa = bay.m_sharedAssets;
                        if (sa == null) continue;
                        if (ReferenceEquals(sa, shared) || sa.Pointer == shared.Pointer) return bay;
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.FindBayForSharedAssets fehlgeschlagen: {ex.Message}");
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // 311b: POI-Marker der JobsBoard-Bays auf Shop-Anzeige umschreiben
        // (Einkaufskorb-Icon + Shopname) via native POI-Settings, kein Overlay.
        // ════════════════════════════════════════════════════════════════════

        private static bool poiApplied = false;

        // 313: Sektor-Gate (ein Log/Run pro Sektorwechsel statt alle 5 s).
        private static string lastPoiSector = "none";

        /// <summary>
        /// Re writes the POI settings of all JobsBoard bays in the scene so the native
        /// POI marker renderer shows the Shop icon + shop display name instead of the
        /// job board clipboard + "Auftragsbörse".
        ///
        /// Called periodically from Plugin.Update (throttled by DockingBayHUD's refresh
        /// cycle) and re-applies when a new sector loads.
        /// </summary>
        public static void ApplyShopPoiToJobsBoardBays()
        {
            if (!ShouldRewriteForAmenityDisplay()) return;
            try
            {
                // 313: Sektor-Gate - der Rewrite muss nur laufen, wenn ein neuer Sektor
                // geladen wurde (Bays werden pro Sektor neu erzeugt). Vorher lief der
                // Pfad alle 5 s (Log-Spam) und setzte zudem Text-Updates regelmaessig neu.
                var sector = global::StarTruckMP.StarTruckClient.StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;
                if (sector == lastPoiSector) return;
                lastPoiSector = sector;

                var allBays = UnityEngine.Object.FindObjectsOfType<DockingBay>();
                if (allBays == null || allBays.Length == 0) return;

                int rewritten = 0, noPoi = 0, noShared = 0, noShopSettings = 0;
                foreach (var bay in allBays)
                {
                    if (bay == null || bay.gameObject == null) continue;
                    try
                    {
                        // JobsBoard-Bay-Klassifikation (shared with DockingBayHUD).
                        if (!DockingBayAmenityUtil.IsJobsBoardBay(bay)) continue;

                        // m_sharedAssets (DockingBaySharedAssets ScriptableObject) with
                        // per-amenity POI settings (m_poiSettingsShop etc.).
                        var shared = ReadSharedAssets(bay);
                        if (shared == null)
                        {
                            noShared++;
                            continue;
                        }

                        // 1) Native path: redirect the JobsBoard POI setting of this
                        //    bay's shared assets to the Shop POI setting (basket icon),
                        //    so any ConfigurePOI(…) call picks up Shop automatically.
                        var shopSettings = shared.m_poiSettingsShop;
                        if (shopSettings == null)
                        {
                            noShopSettings++;
                            continue;
                        }
                        if (shared.m_poiSettingsJobsBoard != shopSettings)
                        {
                            shared.m_poiSettingsJobsBoard = shopSettings;
                        }

                        // 2) Live POI instance: m_dockingBayPOI (RegisterPointOfInterest)
                        //    gets SetSettings(shopSettings) so the already-registered
                        //    POI entry switches immediately (PointsOfInterest manager
                        //    reads entry.settings each frame and drives the marker).
                        var poi = bay.m_dockingBayPOI;
                        if (poi == null)
                        {
                            noPoi++;
                            continue;
                        }
                        poi.SetSettings(shopSettings);

                        // 313 POI-Text Plan-B: displayNameId (String-ID) wird vom nativen
                        // Renderer nur aufgeloest, wenn die ID in der String-Tabelle
                        // existiert - ein Shopname tut das nicht, deshalb zeigt das Label
                        // weiterhin 'Auftragsborse'. Bewiesener Pfad (statisch verifiziert
                        // via ilspycmd -t PointOfInterestMarker): PointsOfInterest.entries
                        // -> PointOfInterestEntry.marker (PointOfInterestMarker) ->
                        // marker._name/_label (TMPro.TextMeshProUGUI) + setter displayName
                        // (Token 100665771). Den Live-Marker-Text direkt setzen.
                        var shopDesc = bay.ShopDescription ?? FindShopDescriptionViaGroup(bay);
                        var dispName = shopDesc?.shopDisplayName;
                        var textSet = SetLiveMarkerText(poi, bay, dispName);
                        if (textSet) rewritten++;
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"311b POI-Rewrite an '{bay.gameObject.name}' fehlgeschlagen: {ex.Message}");
                    }
                }

                if (rewritten > 0 || noShared > 0 || noPoi > 0 || noShopSettings > 0)
                {
                    StarTruckMP.Log.LogInfo(
                        $"311b POI-Rewrite: {rewritten} JobsBoard-Bays auf Shop-POI umgeschrieben " +
                        $"(kein POI: {noPoi}, keine SharedAssets: {noShared}, keine Shop-Settings: {noShopSettings}).");
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311b ApplyShopPoiToJobsBoardBays Fehler: {ex}");
            }
        }

        /// <summary>
        /// 313 POI-Text Plan-B: setzt den sichtbaren Label-Text des Live-Markers
        /// direkt auf den Shop-Namen. Statisch verifizierte Struktur (ilspycmd):
        ///   PointsOfInterest (MonoBehaviour, Feld 'entries' = List&lt;PointOfInterestEntry&gt;)
        ///     -> PointOfInterestEntry.marker : PointOfInterestMarker
        ///        -> PointOfInterestMarker._name/_label : TMPro.TextMeshProUGUI
        ///        -> PointOfInterestMarker.set_displayName (public setter, Token 100665771).
        /// Diag-Log '313b text:' zeigt Marker + Text vorher/nachher (BepInEx-Log).
        /// </summary>
        private static bool SetLiveMarkerText(RegisterPointOfInterest poi, DockingBay bay, string newText)
        {
            try
            {
                if (poi == null || poi.gameObject == null) return false;
                if (string.IsNullOrEmpty(newText)) newText = "Shop";

                PointOfInterestMarker marker = null;
                // Einstiegspunkt 1: PointsOfInterest.entries -> Entry per POI-Id matchen
                // -> entry.marker (Pointer-Vergleich, Wrapper-Identitaet ist instabil).
                var manager = UnityEngine.Object.FindObjectOfType<PointsOfInterest>();
                if (manager != null)
                {
                    try
                    {
                        var entries = manager.entries;
                        if (entries != null)
                        {
                            foreach (var e in entries)
                            {
                                if (e == null || e.marker == null) continue;
                                try
                                {
                                    if (e.id != null && poi.id != null
                                        && e.id.Pointer == poi.id.Pointer)
                                    {
                                        marker = e.marker;
                                        break;
                                    }
                                }
                                catch { continue; }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"313b SetLiveMarkerText: entries-Scan fehlgeschlagen: {ex.Message}");
                    }
                }
                if (marker == null)
                {
                    // Fallback: Marker als Component am/kinder des POI-GameObjects.
                    try
                    {
                        var ms = poi.GetComponentsInChildren<PointOfInterestMarker>(true);
                        if (ms != null && ms.Length > 0) marker = ms[0];
                    }
                    catch { }
                }
                if (marker == null)
                {
                    StarTruckMP.Log.LogWarning($"313b text: kein Live-Marker gefunden (poi={poi.gameObject?.name}) - Label bleibt 'Auftragsboerse'.");
                    return false;
                }

                string before = null;
                TMPro.TextMeshProUGUI label = null;
                try
                {
                    label = marker._name;
                    if (label == null) label = marker._label;
                    if (label != null) before = label.text;
                }
                catch { }
                if (label == null)
                {
                    // Fallback: erster TMP am Marker-GameObject.
                    try
                    {
                        var tmps = marker.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                        if (tmps != null && tmps.Length > 0) label = tmps[0];
                        if (label != null) before = label.text;
                    }
                    catch { }
                }
                if (label == null)
                {
                    StarTruckMP.Log.LogWarning($"313b text: Marker '{marker.gameObject?.name}' gefunden, aber kein TMP-Label - Label bleibt unveraendert.");
                    return false;
                }

                label.text = newText;
                // displayName-Setter ebenfalls rufen (native Text-Aktualisierung,
                // Token 100665771) - falls Init den Text spaeter neu aufbaut.
                try { marker.displayName = newText; } catch { }
                StarTruckMP.Log.LogInfo($"313b text: Marker '{marker.gameObject?.name}' '{before}' -> '{newText}' (bay={bay.gameObject?.name}).");
                return true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"313b SetLiveMarkerText fehlgeschlagen: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads DockingBay.m_sharedAssets via the shared reflection helper
        /// (native-first read). Returns null when unavailable.
        /// </summary>
        private static DockingBaySharedAssets ReadSharedAssets(DockingBay bay)
        {
            try
            {
                var m = DockingBayAmenityUtil.FindMember(bay.GetType(), "m_sharedAssets");
                if (m == null) return null;
                return DockingBayAmenityUtil.ReadIl2CppField(m, bay) as DockingBaySharedAssets;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311b ReadSharedAssets fehlgeschlagen: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fallback ShopDescription lookup via the Station's Shop DockingBayGroup
        /// when bay.ShopDescription is null (JobsBoard bays normally have none).
        /// </summary>
        private static ShopDescription FindShopDescriptionViaGroup(DockingBay bay)
        {
            try
            {
                var stationObj = bay.ParentStation;
                if (stationObj == null) return null;
                var m = DockingBayAmenityUtil.FindMember(stationObj.GetType(), "DockingBayGroups")
                      ?? DockingBayAmenityUtil.FindMember(stationObj.GetType(), "m_dockingBayGroups");
                if (m == null) return null;
                var groups = DockingBayAmenityUtil.ReadIl2CppField(m, stationObj) as System.Collections.IEnumerable;
                if (groups == null) return null;
                foreach (var g in groups)
                {
                    var gObj = g as Il2CppObjectBase;
                    if (gObj == null) continue;
                    if (DockingBayAmenityUtil.ReadAmenityType(gObj) != AmenityTypes.Shop) continue;
                    var gm = DockingBayAmenityUtil.FindMember(gObj.GetType(), "shopDescription");
                    if (gm == null) continue;
                    return DockingBayAmenityUtil.ReadIl2CppField(gm, gObj) as Il2CppObjectBase as ShopDescription;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311b FindShopDescriptionViaGroup fehlgeschlagen: {ex.Message}");
            }
            return null;
        }
    }
}