using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 311: Shop an JobsBoard-Docking-Bays.
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
    /// Patchpunkt ist der hochmoegliche Einstieg in den Amenity-Ablauf nach dem Docking:
    ///   TruckAmenityTerminal.OnAmenityEnter(sender, eventArgs)
    ///   -> setzt _currentAmenity/_currentShop aus dem AmenityEventArgs und ist DIE
    ///      Zentrale, an der entschieden wird, welcher Amenity-Screen spaeter oeffnet
    ///      (ShowAmenityScreen -> _currentAmenitySetup.openAmenityScreenEvent).
    ///
    /// Strategie: Wenn das Event einen JobsBoard-Amenity traegt (Dock an einer
    /// JobsBoard-Bay) UND der MP-Client verbunden ist, werden die Event-Args vor dem
    /// nativen Handler auf Shop umgeschrieben (amenity=Shop, shopDescription=<Station-
    /// Shop-Gruppe>). Der native Pfad (Docking-Cinematic -> DockedScreen/AmenitySetup-
    /// Kette mit ShopDescription) laeuft danach 1:1 wie an echten Shop-Bays - kein
    /// UI-Klon, keine Coroutines.
    ///
    /// Fallback: Hat die Station KEINE Shop-Gruppe (keine DockingBayGroup mit
    /// amenityType==Shop bzw. keine shopDescription), bleibt das Bay-Verhalten
    /// unveraendert (Jobboard via Dock) - nur eine Log-Warnung, kein Crash.
    /// Singleplayer ohne MP-Client: Patches greifen gar nicht (Gate auf IsConnected).
    /// </summary>
    public static class ShopAtJobBoardBays
    {
        private const int AmenityJobsBoard = 1; // StationAmenity.JobsBoard (verifiziert)
        private const int AmenityShop = 2;      // StationAmenity.Shop (verifiziert)

        private static bool searched = false;
        private static MethodInfo mi_OnAmenityEnter = null;
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
        /// Liest die amenityType-Property eines Il2Cpp-Objekts (DockingBay oder
        /// Station.DockingBayGroup) als int. Basiert auf dem DockingBayHUD-Muster
        /// (direkte Property, Fallback Field).
        /// </summary>
        private static int ReadAmenityType(Il2CppObjectBase obj)
        {
            if (obj == null) return -1;
            try
            {
                var prop = obj.GetType().GetProperty("amenityType", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val != null) return Convert.ToInt32(val);
                }
                var prop2 = obj.GetType().GetProperty("AmenityType", BindingFlags.Public | BindingFlags.Instance);
                if (prop2 != null)
                {
                    var val2 = prop2.GetValue(obj);
                    if (val2 != null) return Convert.ToInt32(val2);
                }
                var field = obj.GetType().GetField("m_amenityType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var val3 = field.GetValue(obj);
                    if (val3 != null) return Convert.ToInt32(val3);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.ReadAmenityType fehlgeschlagen: {ex.Message}");
            }
            return -1;
        }

        /// <summary>
        /// Sucht die shopDescription der ersten DockingBayGroup der Station, deren
        /// amenityType == Shop ist (Station.DockingBayGroups). Das ist derselbe Kontext,
        /// den echte Shop-Bays ueber DockingBay.ShopDescription bekommen - Lager/Preise
        /// entsprechen damit dem Stationsshop.
        /// </summary>
        private static Il2CppObjectBase FindStationShopDescription(Il2CppObjectBase stationObj)
        {
            if (stationObj == null) return null;
            try
            {
                var groupsProp = stationObj.GetType().GetProperty("DockingBayGroups", BindingFlags.Public | BindingFlags.Instance);
                if (groupsProp == null)
                {
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: Station.DockingBayGroups nicht gefunden.");
                    return null;
                }
                var groups = groupsProp.GetValue(stationObj) as System.Collections.IEnumerable;
                if (groups == null)
                {
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: Station.DockingBayGroups ist null/leer.");
                    return null;
                }
                foreach (var g in groups)
                {
                    var gObj = g as Il2CppObjectBase;
                    if (gObj == null) continue;
                    int amenity = ReadAmenityType(gObj);
                    if (amenity != AmenityShop) continue;
                    var sdProp = gObj.GetType().GetProperty("shopDescription", BindingFlags.Public | BindingFlags.Instance);
                    var sd = sdProp?.GetValue(gObj) as Il2CppObjectBase;
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

        /// <summary>
        /// Finds the Station object for a DockingBay (bay.ParentStation). Uses the
        /// DockingBayHUD FindMember pattern because the declaring type of the
        /// ParentStation property can differ from the runtime proxy type.
        /// </summary>
        private static Il2CppObjectBase FindStationOfBay(Il2CppObjectBase bayObj)
        {
            if (bayObj == null) return null;
            try
            {
                foreach (var name in new[] { "ParentStation", "parentStation", "m_parentStation" })
                {
                    var prop = bayObj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var v = prop.GetValue(bayObj) as Il2CppObjectBase;
                        if (v != null) return v;
                    }
                    var f = bayObj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                    {
                        var v2 = f.GetValue(bayObj) as Il2CppObjectBase;
                        if (v2 != null) return v2;
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.FindStationOfBay fehlgeschlagen: {ex.Message}");
            }
            return null;
        }

        // ── Harmony patch registration (called from Plugin.Load) ──

        /// <summary>
        /// Resolve TruckAmenityTerminal.OnAmenityEnter via assembly scan: the type lives
        /// in the game's Assembly-CSharp (global namespace), not this assembly, so a
        /// plain AccessTools.Method("TruckAmenityTerminal:...") string lookup is
        /// unreliable. The loop over AppDomain assemblies is the working path.
        /// </summary>
        private static MethodInfo TryResolveOnAmenityEnter()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t == null || t.Name != "TruckAmenityTerminal") continue;
                        var mi = AccessTools.Method(t, "OnAmenityEnter");
                        if (mi != null) return mi;
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.TryResolveOnAmenityEnter: nicht auflösbar: {ex.Message}");
            }
            return null;
        }

        public static void Apply()
        {
            if (harmonyApplied) return;
            harmonyApplied = true;
            var harmony = new Harmony("StarTruckMP.ShopAtJobBoardBays");

            // highest possible level: the entry into the amenity flow after docking.
            mi_OnAmenityEnter = TryResolveOnAmenityEnter();

            if (mi_OnAmenityEnter == null)
            {
                StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays.Apply: Patchziel nicht gefunden - Feature inaktiv.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(ShopAtJobBoardBays), nameof(RewriteAmenityPrefix));
            harmony.Patch(mi_OnAmenityEnter, prefix: prefix);
            StarTruckMP.Log.LogInfo("311 ShopAtJobBoardBays.Apply: Harmony-Patch auf TruckAmenityTerminal.OnAmenityEnter registriert.");
        }

        // ── Prefix: rewrite JobsBoard amenity events into Shop events ──

        private static readonly Dictionary<int, int> rewriteCounters = new Dictionary<int, int>();

        /// <summary>
        /// Prefix vor TruckAmenityTerminal.OnAmenityEnter(sender, eventArgs).
        /// Wenn das Event einen JobsBoard-Amenity traegt und wir als MP-Client verbunden
        /// sind: amenity -> Shop umschreiben und die shopDescription der Station injizieren.
        /// Wenn die Station keinen Shop hat: keine Aenderung (Bay verhaelt sich wie bisher).
        /// </summary>
        // ReSharper disable once RedundantAssignment
        public static void RewriteAmenityPrefix(object __0, object __1)
        {
            try
            {
                if (!ShouldRewrite()) return;

                var argsObj = __1 as Il2CppObjectBase;
                if (argsObj == null) return;

                // amenity aus den Args lesen (Feld, nicht Property - AmenityEventArgs.expose)
                int amenity = ReadAmenityType(argsObj);
                if (amenity != AmenityJobsBoard)
                {
                    return; // echter Shop/Repairs/etc. oder unlesbar -> unveraendert durchlassen
                }

                // shopDescription der Station beschaffen:
                // AmenityEventArgs.shopDescription kommt aus der Bay; aber die JobsBoard-
                // Bay selbst hat keine ShopDescription. Wir brauchen also die Station.
                // sender (=__0) ist der Trigger/Bay-Context - wir suchen die Station direkt
                // ueber die Szene: Alle DockingBays, JobsBoard-amenityType, deren ParentStation.
                Il2CppObjectBase shopDesc = null;
                try
                {
                    // 1. Aus den Event-Args selbst (falls das Spiel sie setzt)
                    var sdProp = argsObj.GetType().GetProperty("shopDescription", BindingFlags.Public | BindingFlags.Instance);
                    var sdFromArgs = sdProp?.GetValue(argsObj) as Il2CppObjectBase;
                    // 2. Falls vorhanden: sender-Kette (sender ist bei Dock-Events ein DockingBay
                    //    bzw. DockingBaySharedAssets) fuer die ParentStation nutzen
                    if (sdFromArgs == null && __0 is Il2CppObjectBase senderObj)
                    {
                        var stationObj = FindStationOfBay(senderObj);
                        if (stationObj != null)
                            shopDesc = FindStationShopDescription(stationObj);
                        if (shopDesc == null)
                            StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: Station/Shop-Gruppe nicht ermittelbar (sender=" + senderObj.GetType().Name + ").");
                    }
                    else if (sdFromArgs != null)
                    {
                        shopDesc = sdFromArgs;
                    }
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays: ShopDescription-Suche fehlgeschlagen: {ex.Message}");
                }

                if (shopDesc == null)
                {
                    // Sauberer Fallback: Bay verhaelt sich wie bisher (= Jobboard via Dock).
                    StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: keine ShopDescription verfuegbar - Bay bleibt Jobboard (Fallback).");
                    return;
                }

                // amenity -> Shop umschreiben (Feldzugriff, IL2CPP-Enum ist int-backing)
                try
                {
                    var amenityField = argsObj.GetType().GetField("m_amenity",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? argsObj.GetType().GetField("amenity",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (amenityField == null)
                    {
                        StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: AmenityEventArgs.amenity-Feld nicht gefunden.");
                        return;
                    }
                    amenityField.SetValue(argsObj, Enum.ToObject(amenityField.FieldType, AmenityShop));

                    // shopDescription injizieren
                    var sdProp2 = argsObj.GetType().GetProperty("shopDescription", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sdProp2 != null && sdProp2.CanWrite)
                    {
                        sdProp2.SetValue(argsObj, shopDesc);
                    }
                    else
                    {
                        var sdField = argsObj.GetType().GetField("m_shopDescription",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sdField != null)
                        {
                            sdField.SetValue(argsObj, shopDesc);
                        }
                        else
                        {
                            StarTruckMP.Log.LogWarning("311 ShopAtJobBoardBays: shopDescription nicht schreibbar.");
                            return;
                        }
                    }

                    int cnt;
                    rewriteCounters.TryGetValue(AmenityShop, out cnt);
                    rewriteCounters[AmenityShop] = cnt + 1;
                    StarTruckMP.Log.LogInfo($"311 ShopAtJobBoardBays: JobsBoard-Dock-Ereignis zu Shop umgeschrieben (#{cnt + 1}).");
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays: Umschreiben fehlgeschlagen: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // Niemals crashen - im Zweifel laeuft der native Ablauf unveraendert.
                StarTruckMP.Log.LogWarning($"311 ShopAtJobBoardBays.RewriteAmenityPrefix Fehler: {ex}");
            }
        }
    }
}