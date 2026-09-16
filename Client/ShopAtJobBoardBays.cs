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
                            if (ps.Length < 5) continue;
                            // 314: Accept 5+ params. Runtime has 6 (QuestInstance appended).
                            var isPrimary = ps[0].ParameterType.Name == "StationAmenity"
                                         && ps[2].ParameterType.Name == "ShopDescription";
                            if (isPrimary)
                            {
                                mi = m;
                                targetDesc = $"{t.Name}.{m.Name}(StationAmenity, {ps[1].ParameterType}, ShopDescription, {ps[3].ParameterType}, bool, ...)";
                                StarTruckMP.Log.LogInfo($"314 Patch-Ziel gewaehlt: {t.FullName}.{m.Name} ({ps.Length} Parameter)");
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
            var postfix = new HarmonyMethod(typeof(ShopAtJobBoardBays), nameof(EnterAmenityPostfix));
            harmony.Patch(mi, prefix: prefix, postfix: postfix);
            StarTruckMP.Log.LogInfo($"311 ShopAtJobBoardBays.Apply: Harmony-Patch auf {targetDesc} registriert (Docking-Pfad 311c + 315 Postfix).");
        }

        // ── Prefix: rewrite the JobsBoard dock amenity into a Shop amenity ──

        private static int rewriteCount = 0;

        /// <summary>
        /// 315: Cached Shop-Displayname fuer den Namen-Fix (Aufgabe 2): wird im
        /// EnterAmenityPrefix gesetzt und im Postfix gelesen (guiShow-Callback).
        /// </summary>
        private static string lastShopDisplayName = null;

        /// <summary>
        /// Prefix vor DockingBaySharedAssets.EnterAmenity - DER Punkt, den die native
        /// DockingCoroutine nach dem Dock-Cinematic aufruft. amenityType und shopDesc
        /// werden per ref umgeschrieben, BEVOR der native Handler das AmenityEventArgs
        /// baut und m_enterAmenityEvent feuert.
        /// </summary>
        // ReSharper disable once RedundantAssignment
        // 314: __args approach to handle the runtime 6-param signature
        // (EnterAmenity(StationAmenity, String, ShopDescription, ItemDeliveryDescription,
        //  Boolean, QuestInstance)) without needing QuestInstance type at compile time.
        public static void EnterAmenityPrefix(
            DockingBaySharedAssets __instance,
            object[] __args)
        {
            try
            {
                if (!ShouldRewrite()) return;
                if (__args == null || __args.Length < 5) return;

                // __args[0]: StationAmenity amenityType (ref)
                // __args[1]: string nameStringId
                // __args[2]: ShopDescription shopDesc (ref)
                // __args[3]: ItemDeliveryDescription deliveryDesc
                // __args[4]: bool showScreenImmediately
                // __args[5]: QuestInstance (runtime-only, may be absent)

                // Check JobsBoard via Convert.ToInt32 (works with Il2Cpp enums in __args)
                int amenityInt;
                try { amenityInt = Convert.ToInt32(__args[0]); }
                catch { return; }
                if (amenityInt != (int)AmenityTypes.JobsBoard) return;

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

                // Write back ref parameters through __args
                __args[0] = (StationAmenity)AmenityTypes.Shop;
                __args[2] = stationShop;

                // 315 Fix (Aufgabe 2): nameStringId. Dekompilat-Beweis:
                //   TruckAmenityTerminal.AmenitySetup hat das Feld 'promptStringId'
                //   (GetIl2CppField(AmenitySetup, "promptStringId")) und
                //   TruckAmenityTerminal.get_shopDisplayName (Token 100671943)
                //   liefert den Text, den der Dock-Prompt anzeigt. Die Screens
                //   steuern NUR via bay.m_amenityType (siehe Patch unten) - aber
                //   der Prompt/Screen-Titel baut auf der String-ID auf. Der
                //   Jobboard-Wert (__args[1], native ID 'Auftragsboerse') muss
                //   deshalb auf den Shop-Displaynamen umgeschrieben werden.
                //shopDesc.shopDisplayName ist der native Anzeigename der Shop-Gruppe
                //(ShopDescription.shopDisplayName, ilspycmd -t ShopDescription).
                if (__args.Length >= 2)
                {
                    var displayName = stationShop.shopDisplayName;
                    if (string.IsNullOrEmpty(displayName)) displayName = "Shop";
                    __args[1] = displayName;
                    lastShopDisplayName = displayName;
                }

                // 315 Fix (Aufgabe 1, Kandidat a): m_amenityType der BAY vorschalten.
                //
                // Dekompilat-Beweis, warum die umgeschriebenen EnterAmenity-EventArgs
                // allein NICHT genuegen (User-Log: Jobboerse-UI oeffnet sich weiter):
                //   DockingBay.<DockingCoroutine>d__82.MoveNext (Token 100671310)
                //   liest am Anfang `this.<>4__this.m_amenityType` (DockingBay.
                //   m_amenityType, Feld-Token via GetIl2CppField("m_amenityType")) und
                //   steuert DAMIT den Screen-Open. Das Shop-UI haengt an
                //   bay.m_amenityType == StationAmenity.Shop - nicht an den EventArgs.
                //   Ein Subscriber-Filter (Kandidat c) ist ausgeschlossen:
                //   TruckAmenityTerminal.OnAmenityEnter (Token 100671950) nimmt
                //   sender+EventArgs generisch entgegen und filtert nicht nach Bay.
                // Rewrite muss VOR dem Coroutine-Read greifen -> direkt hier im
                // EnterAmenityPrefix (der feuert, bevor die Coroutine die EventArgs
                // weiterverarbeitet und bevor der Screen-Open-Zweig liest).
                // Enum.ToObject-Regel (game-and-server-ops.md, Interop-Falle): das
                // Feld hat den nativen Enum-Typ StationAmenity.
                try
                {
                    var bayType = bay.GetType();
                    var amenityField = bayType.GetField("m_amenityType",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (amenityField != null)
                    {
                        var enumObj = Enum.ToObject(amenityField.FieldType, AmenityTypes.Shop);
                        amenityField.SetValue(bay, enumObj);
                        StarTruckMP.Log.LogInfo($"315 ShopAtJobBoardBays: bay.m_amenityType -> Shop gesetzt (bay={bay.gameObject?.name}, field={amenityField.FieldType.Name}).");
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning("315 ShopAtJobBoardBays: DockingBay.m_amenityType-Feld nicht gefunden (Screen-Open bleibt evtl. Jobboard).");
                    }
                }
                catch (Exception exAmenity)
                {
                    StarTruckMP.Log.LogWarning($"315 ShopAtJobBoardBays: m_amenityType-Rewrite fehlgeschlagen: {exAmenity.Message}");
                }

                rewriteCount++;
                StarTruckMP.Log.LogInfo($"311 ShopAtJobBoardBays: JobsBoard-Dock zu Shop umgeschrieben (#{rewriteCount}, bay={bay.gameObject?.name}).");
            }
            catch (Exception ex)
            {
                // Niemals crashen - im Zweifel laeuft der native Ablauf unveraendert.
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.EnterAmenityPrefix Fehler: {ex}");
            }
        }

        // ── Postfix: screen-open fallback (315 Fix, Kandidat a) ──

        /// <summary>
        /// 315: Postfix nach DockingBaySharedAssets.EnterAmenity. Beweis im
        /// Dekompilat, warum das notwendig ist: Die Screens (JobBoardScreen /
        /// ShopScreen : ScreenController_Pauser) werden nativ ueber
        /// TruckAmenityTerminal.AmenitySetup.openAmenityScreenEvent /
        /// openDockedScreenEventIfNeeded (GameEvent-Felder,
        /// GetIl2CppField(AmenitySetup, "openAmenityScreenEvent") /
        /// "openDockedScreenEventIfNeeded") geoeffnet. Das ist ein Asset-Bindung
        /// an der BAY - die EventArgs-Parameter (amenityType/shopDesc) steuern
        /// dieses Open NICHT. Falls das m_amenityType-Vorschalten im Prefix nicht
        /// greift, oeffnet der native Pfad weiterhin die Jobboerse.
        ///
        /// Fallback (nur wenn der Prefix umgeschrieben hat): nach dem nativen
        /// EnterAmenity den Shop-Screen via MenuState.LoadAndShow explizit
        /// oeffnen (gleicher Pfad wie JobBoardComputer.TryOpenGameJobBoard,
        /// nur mit "ShopScreen").
        /// </summary>
        public static void EnterAmenityPostfix(object[] __args)
        {
            try
            {
                if (!ShouldRewrite()) return;
                if (__args == null || __args.Length < 5) return;
                if (string.IsNullOrEmpty(lastShopDisplayName)) return; // Prefix hat nicht umgeschrieben

                int amenityInt;
                try { amenityInt = Convert.ToInt32(__args[0]); }
                catch { return; }
                if (amenityInt != (int)AmenityTypes.Shop) return; // nur umgeschriebene Docks

                var ms = com.monsterandmonster.Menu.MenuState.Get();
                if (ms == null)
                {
                    StarTruckMP.Log.LogWarning("315 ShopAtJobBoardBays: MenuState.Get() null - Shop-Screen-Fallback nicht moeglich.");
                    return;
                }
                var runnerGO = new GameObject("StarTruckMP_ShopScreenRunner315");
                UnityEngine.Object.DontDestroyOnLoad(runnerGO);
                var runner = runnerGO.AddComponent<JobBoardComputer.CoroutineRunnerHelper>();
                runner.StartCoroutine(ms.LoadAndShow("ShopScreen", null, null));
                StarTruckMP.Log.LogInfo($"315 ShopAtJobBoardBays: ShopScreen-Open-Fallback ausgefuehrt (shopName='{lastShopDisplayName}').");
            }
            catch (Exception ex)
            {
                // Niemals crashen - im Zweifel bleibt der native Screen-Stand.
                StarTruckMP.Log.LogWarning($"315 ShopAtJobBoardBays.EnterAmenityPostfix Fehler: {ex.Message}");
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
                        var textSet = SetLiveMarkerText(poi, bay, dispName, shopSettings);
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
        /// 314: Setzt den sichtbaren Label-Text des Live-Markers direkt auf den
        /// Shop-Namen. Sucht PointOfInterestMarker und TextMeshProUGUI ueber das
        /// poi.gameObject ('Docking_BayPOI').
        /// Statisch verifizierte Struktur (ilspycmd):
        ///   PointOfInterestMarker._name/_label : TMPro.TextMeshProUGUI
        ///   PointOfInterestMarker.set_displayName (public setter, Token 100665771).
        /// Diag-Log '314b text:' zeigt Marker + TMP + Text vorher/nachher.
        /// </summary>
        private static bool SetLiveMarkerText(RegisterPointOfInterest poi, DockingBay bay, string newText, PointOfInterestSettings shopSettings)
        {
            try
            {
                if (poi == null || poi.gameObject == null) return false;
                if (string.IsNullOrEmpty(newText)) newText = "Shop";

                // 315: 3-stufige Suche, weil 314b bewies, dass unter poi.gameObject
                // ('Docking_BayPOI') weder PointOfInterestMarker noch TMP liegt
                // (User-Log: '314b text: kein TMP/Marker an poi.GameObject').
                //
                // Statisch verifiziert (ilspycmd -t PointOfInterestMarker /
                // -t PointsOfInterest): Der native Renderer haengt den sichtbaren
                // Marker NICHT an das POI-GameObject - er spawnt ihn aus
                // PointOfInterestSettings.markerPrefab unter PointsOfInterest.
                // onScreenPOIParent. Der echte Handle ist daher
                // PointsOfInterest.entries -> PointOfInterestEntry.marker.
                //
                // Stufe 1: Kinder+Parents von poi.gameObject (inkl. world-space
                //          TMPro.TextMeshPro - NOTES_WORLDSPACE_UI.md).
                // Stufe 2: PointsOfInterest.entries-Match ueber entry.settings
                //          (Referenzvergleich mit der gerade gesetzten
                //          shopSettings-Instanz - robust, weil wir sie in Schritt
                //          2 von ApplyShopPoiToJobsBoardBays selbst via
                //          poi.SetSettings(shopSettings) gesetzt haben).
                // Stufe 3: Positions-Naehe zur Bay als Fallback-Match.

                PointOfInterestMarker marker = null;
                TMPro.TMP_Text label = null;

                // Stufe 1a: Kinder von poi.gameObject
                var markers = poi.gameObject.GetComponentsInChildren<PointOfInterestMarker>(true);
                if (markers != null && markers.Length > 0) marker = markers[0];
                var tmps = poi.gameObject.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                if (tmps != null && tmps.Length > 0) label = tmps[0];
                // Stufe 1b: world-space TextMeshPro (NICHT nur UGUI) in Kindern
                if (label == null)
                {
                    var wsTmps = poi.gameObject.GetComponentsInChildren<TMPro.TextMeshPro>(true);
                    if (wsTmps != null && wsTmps.Length > 0) label = wsTmps[0];
                }
                // Stufe 1c: Parents (Marker kann am Parent-Prefab haengen)
                if (marker == null)
                {
                    var parentMarkers = poi.gameObject.GetComponentsInParent<PointOfInterestMarker>(true);
                    if (parentMarkers != null && parentMarkers.Length > 0) marker = parentMarkers[0];
                }
                if (label == null)
                {
                    var parentTmps = poi.gameObject.GetComponentsInParent<TMPro.TextMeshProUGUI>(true);
                    if (parentTmps != null && parentTmps.Length > 0) label = parentTmps[0];
                }

                string matchMode = "poi-go";

                // Stufe 2+3: PointsOfInterest.entries (statistisch verifizierter
                // nativer Handle: entry.marker ist der gespawnte Marker,
                // entry.settings die aktive Settings-Instanz).
                if (marker == null)
                {
                    try
                    {
                        var poiManager = PointsOfInterest.Get();
                        var entries = poiManager?.entries;
                        if (entries != null)
                        {
                            float bestDist = float.MaxValue;
                            Vector3 bayPos = bay.transform.position;
                            foreach (var entry in entries)
                            {
                                if (entry == null) continue;
                                // Stufe 2: Referenz-Match ueber die Settings, die wir
                                // gerade via poi.SetSettings(shopSettings) gesetzt
                                // haben (robust gegen interlaced entry-Listen, wo der
                                // reine Pointer-Vergleich am GO scheiterte).
                                bool settingsMatch = false;
                                if (shopSettings != null)
                                {
                                    try { settingsMatch = entry.settings != null && entry.settings.Pointer == shopSettings.Pointer; }
                                    catch { settingsMatch = false; }
                                }
                                // Stufe 3: Positions-Naehe zur Bay (< 30 m) als
                                // Fallback-Match (POI-GO selbst ist das Docking_BayPOI).
                                bool posMatch = false;
                                try
                                {
                                    var entryGo = entry.gameObject;
                                    if (entryGo != null)
                                    {
                                        float d = Vector3.Distance(entryGo.transform.position, bayPos);
                                        if (d < 30f) posMatch = true;
                                    }
                                }
                                catch { }

                                if (!settingsMatch && !posMatch) continue;

                                var m = entry.marker;
                                if (m == null) continue;
                                float score = settingsMatch ? 0f : 10f;
                                try
                                {
                                    var mGo = m.gameObject;
                                    if (mGo != null) score += Vector3.Distance(mGo.transform.position, bayPos);
                                }
                                catch { }
                                if (score < bestDist)
                                {
                                    bestDist = score;
                                    marker = m;
                                    matchMode = settingsMatch ? "poi-entries(settings)" : "poi-entries(pos)";
                                }
                            }
                        }
                    }
                    catch (Exception exEntries)
                    {
                        StarTruckMP.Log.LogWarning($"315 text: PointsOfInterest.entries-Suche fehlgeschlagen: {exEntries.Message}");
                    }
                }

                // TMP am gefundenen Marker nachziehen (Stufe 2/3 => label neu holen)
                if (marker != null && label == null)
                {
                    var mTmps = marker.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                    if (mTmps != null && mTmps.Length > 0) label = mTmps[0];
                    if (label == null)
                    {
                        var mWs = marker.GetComponentsInChildren<TMPro.TextMeshPro>(true);
                        if (mWs != null && mWs.Length > 0) label = mWs[0];
                    }
                }

                if (marker == null && label == null)
                {
                    StarTruckMP.Log.LogWarning($"315 text: kein TMP/Marker gefunden (poi={poi.gameObject.name}, mode={matchMode}, entries-Match fehlgeschlagen).");
                    return false;
                }

                string before = label != null ? label.text : null;

                if (label != null)
                {
                    label.text = newText;
                }
                // displayName-Setter ebenfalls rufen (native Text-Aktualisierung,
                // Token 100665771) - falls Init den Text spaeter neu aufbaut.
                if (marker != null)
                {
                    try { marker.displayName = newText; } catch { }
                }

                StarTruckMP.Log.LogInfo(
                    $"315 text: mode={matchMode}, marker={marker?.gameObject?.name ?? "null"}, " +
                    $"tmp={(label != null ? label.gameObject.name : "null")}, " +
                    $"goPath={(marker != null && marker.gameObject != null ? GetGoPath(marker.gameObject) : "?")}, " +
                    $"text='{before}'->'{newText}'");
                return true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"314b SetLiveMarkerText fehlgeschlagen: {ex.Message}");
                return false;
            }
        }

        /// <summary>315: GameObject-Pfad fuer Diag-Logs (beweist den Marker-Pfad).</summary>
        private static string GetGoPath(GameObject go)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var t = go.transform;
                while (t != null)
                {
                    if (sb.Length > 0) sb.Insert(0, "/");
                    sb.Insert(0, t.name);
                    t = t.parent;
                }
                return sb.ToString();
            }
            catch { return go?.name ?? "?"; }
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