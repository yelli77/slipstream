using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Fix 368: Werkstatt-/Amenity-Eintritt darf NUR den Spieler betreffen, der dockt.
    ///
    /// Bug: Wenn Spieler A in eine Repair-Station/Werkstatt dockt, "landeten" ALLE anderen
    /// Spieler desselben Sektors (bisweilen scheinbar auch Spieler anderer Sektoren)
    /// ebenfalls in der Werkstatt.
    ///
    /// Root Cause (Beweiskette ueber Mod-Code + reference/api-dump/Assembly-CSharp.txt):
    ///   1. Encoding/Messages.cs createPlayer() baut fuer jeden Remote-Spieler einen
    ///      Ghost-Truck ("RemoteTruck{id}"), der bewusst auf die PHYSIK-LAYER des
    ///      lokalen Trucks gelegt wird (Messages.cs ~Zeile 46: newTruck.layer =
    ///      myTruck.layer) und nach der 120s-Grace einen aktiven Rigidbody +
    ///      BoxCollider bekommt (RemoteTruckCollisionHelper, Messages.cs ~Zeile 884-915,
    ///      "collisions ENABLED"). Das ist noetig fuer Truck-vs-Truck-Kollision.
    ///   2. Solange A in der Werkstatt ist, steht As Truck (per Position-Sync) nativ
    ///      IM Docking-/Recovery-Bay der Station. Auf jedem anderen Client B desselben
    ///      Sektors steht also der Ghost mit aktivem Rigidbody auf der Player-Truck-Layer
    ///      IM BAY.
    ///   3. Die native Bay-Logik (DockingBay.Update-Proximity-Check mit
    ///      k_proximityCheckTimer/k_triggerColliderLayer, DockingCoroutine
    ///      DockingBay.<DockingCoroutine>d__82 mit Feld _canEnterAmenity_5__2, dann
    ///      DockingBaySharedAssets.CanEnterAmenity -> Dock-Cinematic ->
    ///      DockingBaySharedAssets.EnterAmenity -> m_enterAmenityEvent) kann den Ghost
    ///      nicht vom echten Truck unterscheiden: gleicher Layer, echte Rigidbody-
    ///      Physik. Der Dock/Amenity-Ablauf laeuft deshalb AUF B lokalen Client und
    ///      zieht B in den Werkstatt-/Garage-Zustand (Amenity-Screen/Dock-Cinematic),
    ///      obwohl B ganz woanders ist. Da jeder Client den Zustand SELBST laedt,
    ///      wirkt der Bug wie ein "globaler" Trigger - der Sektorsync traegt den
    ///      Zustand aber nicht selbst ueber (deshalb primar Same-Sector; andere
    ///      Sektoren nur falls Ghosts dort temporaer noch sichtbar sind).
    ///
    /// Fix (nur MP-Clients, Singleplayer unveraendert): Gate an den beiden nativen
    /// Choke-Points DockingBaySharedAssets.CanEnterAmenity und .EnterAmenity:
    ///   - Wenn die Bay ihren Dock-Truck kennt (m_truck) und das ein Remote-Ghost ist,
    ///     wird der Amenity-Ablauf komplett unterdrueckt (exakter Discriminator).
    ///   - Fallback: wenn der LOCALE Truck weiter als AmenityGateMaxMeters von der Bay
    ///     entfernt ist, ebenfalls unterdruecken. Der echte Spieler, der dockt, steht
    ///     natuerlich an der Bay; ein Spieler, der nach dem Dock im Stationsinneren
    ///     ein Amenity-Terminal benutzt, ist ebenfalls nahe seines Trucks.
    /// Kann die Bay nicht aufgeloest werden, greift das Gate NICHT (kein Break des
    /// nativen Flows - Muster wie ShopAtJobBoardBays: never break native).
    /// </summary>
    public static class AmenityLocalGate
    {
        private const string LogTag = "368";

        /// <summary>
        /// Maximaler Abstand (Meter, Szenen-Koordinaten, Floating-Origin-symmetrisch)
        /// zwischen lokalem Truck und Bay, bei dem der lokale Spieler als "der Dockende"
        /// gilt. Stationsinnenraeume sind deutlich kleiner, Sektoren sind km-gross.
        /// Per Env STRUCKMP_AMENITY_GATE_METERS ueberschreibbar (Diagnose).
        /// </summary>
        public static float AmenityGateMaxMeters = 750f;

        /// <summary>
        /// 369 (Fix B): Die Prioritaet dieses Gates. MUSS HOEHER sein als die des
        /// 311 ShopAtJobBoardBays-Prefixes (explizit 400, siehe dort): Harmony sortiert
        /// Prefixes absteigend nach Priority — laeuft das Gate zuerst und liefert false
        /// (Suppress), werden NIEDRIGER-priorisierte Prefixes (inkl. 311-Rewrite) und der
        /// Native-Body uebersprungen; liefert es true, laeuft die 311-Kette unangetastet.
        /// </summary>
        public const int GatePriority = 800;


        private static Harmony harmonyInstance;
        private static bool applied = false;

        // Bay-Aufloesung (FindObjectsOfType) ist teuer; CanEnterAmenity kann pro Frame
        // aus der DockingCoroutine gepollt werden. Cache pro SharedAssets-Pointer mit
        // kurzem TTL.
        private static readonly Dictionary<long, (DockingBay bay, float resolvedAt)> bayCache =
            new Dictionary<long, (DockingBay, float)>();
        private const float BayCacheTtlSeconds = 2f;

        private static float lastSuppressedLog = -999f;
        private static int suppressCount = 0;

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            var envMeters = Environment.GetEnvironmentVariable("STRUCKMP_AMENITY_GATE_METERS");
            if (!string.IsNullOrEmpty(envMeters) &&
                float.TryParse(envMeters, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0f)
            {
                AmenityGateMaxMeters = parsed;
            }

            try
            {
                harmonyInstance = new Harmony("StarTruckMP.AmenityLocalGate");

                var gatePrefix = new HarmonyMethod(typeof(AmenityLocalGate), nameof(AmenityGatePrefix));
                gatePrefix.priority = GatePriority; // HarmonyMethod-Feld (Property-Schreibweise kompiliert hier nicht)

                var canEnterType = AccessTools.TypeByName("DockingBaySharedAssets");
                if (canEnterType != null)
                {
                    var canEnter = AccessTools.Method(canEnterType, "CanEnterAmenity");
                    if (canEnter != null)
                    {
                        harmonyInstance.Patch(canEnter, prefix: gatePrefix);
                        StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Prefix auf DockingBaySharedAssets.CanEnterAmenity registriert.");
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: CanEnterAmenity nicht gefunden - Gate dort inaktiv.");
                    }

                    var enter = AccessTools.Method(canEnterType, "EnterAmenity");
                    if (enter != null)
                    {
                        harmonyInstance.Patch(enter, prefix: gatePrefix);
                        StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Prefix auf DockingBaySharedAssets.EnterAmenity registriert.");
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: EnterAmenity nicht gefunden - Gate dort inaktiv.");
                    }
                }
                else
                {
                    StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: Typ DockingBaySharedAssets nicht gefunden - Feature inaktiv.");
                }

                // ── 369 (Fix B): die TATSAECHLICH wirksamen Restpfade ──
                //
                // Beweis 368: Das DockingBaySharedAssets-Gate feuert (Suppress-Log auf
                // beiden Clients), aber ALLE Spieler landen trotzdem in der Werkstatt —
                // der Amenity-Eintritt laeuft auf einem Pfad, der CanEnterAmenity/
                // EnterAmenity NIE beruehrt: AmenityTriggerZone (api-dump: OnTriggerStay,
                // m_truckInTrigger/m_truckFullyInTrigger, onAmenityEnter) ist eine eigene
                // Physik-Triggerzone im Stationsinneren. Der Ghost-Truck (RemoteTruck,
                // aktiv auf Player-Truck-Layern, steht physisch IM Dock/Bay) loest
                // OnTriggerStay aus -> onAmenityEnter -> TruckAmenityTerminal.OnAmenityEnter
                // -> Werkstatt-/Amenity-Zustand. Das DockingBay-Gate sieht davon nichts.
                //
                // Zwei weitere Choke-Points:
                //  1. AmenityTriggerZone.OnTriggerStay: Ghost-Trigger an der Quelle
                //     abschneiden (der lokale Truck selbst laeuft nativ weiter).
                //  2. TruckAmenityTerminal.OnAmenityEnter: LETZTE Sperre — jeder
                //     Amenity-Eintritt, der nicht vom lokal anwesenden Spieler kommt
                //     (lokaler Truck weit weg vom Terminal), wird unterdrueckt.
                try
                {
                    var triggerZoneType = AccessTools.TypeByName("AmenityTriggerZone");
                    var stay = AccessTools.Method(triggerZoneType, "OnTriggerStay");
                    if (stay != null)
                    {
                        var stayPrefix = new HarmonyMethod(typeof(AmenityLocalGate), nameof(TriggerZoneStayPrefix));
                        stayPrefix.priority = GatePriority;
                        harmonyInstance.Patch(stay, prefix: stayPrefix);
                        StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Prefix auf AmenityTriggerZone.OnTriggerStay registriert (Fix B: Ghost-Trigger-Pfad).");
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: AmenityTriggerZone.OnTriggerStay nicht gefunden - Restpfad-Gate dort inaktiv.");
                    }
                }
                catch (Exception exTZ)
                {
                    StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: OnTriggerStay-Patch fehlgeschlagen: {exTZ.Message}");
                }

                try
                {
                    var terminalType = AccessTools.TypeByName("TruckAmenityTerminal");
                    var onEnter = AccessTools.Method(terminalType, "OnAmenityEnter");
                    if (onEnter != null)
                    {
                        var terminalPrefix = new HarmonyMethod(typeof(AmenityLocalGate), nameof(TerminalEnterPrefix));
                        terminalPrefix.priority = GatePriority; // VOR dem 315c-Prefix (400, ShopAtJobBoardBays)
                        harmonyInstance.Patch(onEnter, prefix: terminalPrefix);
                        StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Prefix auf TruckAmenityTerminal.OnAmenityEnter registriert (Fix B: letzte Sperre).");
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: TruckAmenityTerminal.OnAmenityEnter nicht gefunden - Restpfad-Gate dort inaktiv.");
                    }
                }
                catch (Exception exTE)
                {
                    StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate: OnAmenityEnter-Patch fehlgeschlagen: {exTE.Message}");
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{LogTag} AmenityLocalGate.Apply fehlgeschlagen: {ex.Message}");
            }
        }

        /// <summary>
        /// Gemeinsamer Gate-Prefix fuer CanEnterAmenity und EnterAmenity. Rueckgabe false
        /// = native Methode komplett skippen (kein Dock-Cinematic, kein Amenity-Screen,
        /// kein m_enterAmenityEvent) — genau dann, wenn NICHT der lokale Spieler dockt,
        /// sondern ein Remote-Ghost die Bay getriggert hat.
        /// </summary>
        // ReSharper disable once RedundantAssignment
        public static bool AmenityGatePrefix(DockingBaySharedAssets __instance)
        {
            try
            {
                // Singleplayer: natives Verhalten unveraendert.
                var client = global::StarTruckMP.StarTruckClient.StarTruckClient.client;
                if (client == null || !client.IsConnected) return true;

                if (__instance == null) return true;

                var bay = ResolveBayCached(__instance);
                if (bay == null) return true; // Bay nicht aufloesbar -> nie eingreifen

                var myTruck = global::StarTruckMP.StarTruckClient.StarTruckClient.myTruck;
                if (myTruck == null) return true;

                // Discriminator (a): kennt die Bay bereits "ihren" Dock-Truck und ist das
                // ein Remote-Ghost, ist der ganze Ablauf ein Ghost-Dock -> skippen.
                var dockedTruck = TryGetDockedTruckGameObject(bay);
                if (dockedTruck != null)
                {
                    if (IsRemoteGhost(dockedTruck))
                    {
                        LogSuppress(bay.gameObject, "DockingBaySharedAssets.CanEnter/EnterAmenity",
                            $"docked truck ist Remote-Ghost '{dockedTruck.name}' (local '{myTruck.name}')");
                        return false;
                    }
                    // Dock-Truck ist der lokale Truck -> legitimer eigener Dock.
                    if (SameNativeObject(dockedTruck, myTruck)) return true;
                }

                // Discriminator (b): Distanz-Gate. Der echte Dockende steht an der Bay;
                // ein Client, dessen Truck woanders im Sektor steht, ist nicht der Dockende.
                var bayPos = (bay.transform != null) ? bay.transform.position : Vector3.zero;
                var dist = Vector3.Distance(bayPos, myTruck.transform.position);
                if (dist > AmenityGateMaxMeters)
                {
                    LogSuppress(bay.gameObject, "DockingBaySharedAssets.CanEnter/EnterAmenity",
                        $"local truck {dist:F0}m von Bay entfernt (Limit {AmenityGateMaxMeters:F0}m)");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Niemals crashen - im Zweifel laeuft der native Ablauf unveraendert.
                StarTruckMP.Log.LogWarning($"{LogTag} AmenityGatePrefix Fehler: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 369 (Fix B): Prefix vor AmenityTriggerZone.OnTriggerStay(Collider other).
        ///
        /// Root Cause des 368-Restbugs (Beweis: Suppress-Log des DockingBay-Gates AUF
        /// BEIDEN Clients, trotzdem Werkstatt bei allen): Der Ghost-Truck steht
        /// physisch in der Dock-/Amenity-Triggerzone der Station und loest dort
        /// OnTriggerStay aus. Diese Zone feuert onAmenityEnter unabhAengig von
        /// DockingBaySharedAssets.CanEnterAmenity/EnterAmenity — das 368-Gate sah
        /// diesen Pfad nicht.
        ///
        /// Entscheidung:
        ///  - Trigger-Quelle ist der LOKALE Truck -> native (Spieler ist selbst da).
        ///  - Trigger-Quelle ist ein RemoteTruck-Ghost und der lokale Truck ist WEIT
        ///    weg von der Zone -> skippen (return false): der Ghost repraesentiert nur
        ///    einen fernen Spieler; sein Amenity-Eintritt darf hier nichts ausloesen.
        ///  - Ghost, aber lokaler Truck NAHE -> native lassen (nicht mehr unterscheidbar,
        ///    never-break-native).
        ///  - OnTriggerExit bleibt unangetastet (natives Cleanup intakt).
        /// </summary>
        // ReSharper disable once RedundantAssignment
        public static bool TriggerZoneStayPrefix(AmenityTriggerZone __instance, Collider other)
        {
            try
            {
                var client = global::StarTruckMP.StarTruckClient.StarTruckClient.client;
                if (client == null || !client.IsConnected) return true; // Singleplayer: nativ
                if (__instance == null || other == null) return true;

                var myTruck = global::StarTruckMP.StarTruckClient.StarTruckClient.myTruck;
                if (myTruck == null) return true;

                var triggerRoot = ResolveTriggerSource(other);
                if (triggerRoot == null) return true;

                // Lokaler Truck selbst: nie anfassen.
                if (SameNativeObject(triggerRoot, myTruck)) return true;
                if (!IsRemoteGhost(triggerRoot)) return true; // fremde native Objekte: nativ

                // Ghost-Trigger: nur unterdruecken, wenn der lokale Spieler definitiv
                // nicht selbst in der Naehe der Zone ist.
                var myPos = (myTruck.transform != null) ? myTruck.transform.position : Vector3.zero;
                var zonePos = (__instance.transform != null) ? __instance.transform.position : Vector3.zero;
                var dist = Vector3.Distance(zonePos, myPos);
                if (dist > AmenityGateMaxMeters)
                {
                    LogSuppress(__instance.gameObject, "TriggerZone.OnTriggerStay",
                        $"Ghost-Trigger '{triggerRoot.name}', local truck {dist:F0}m von Zone entfernt (Limit {AmenityGateMaxMeters:F0}m)");
                    return false; // kein m_truckInTrigger, kein onAmenityEnter vom Ghost
                }

                return true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{LogTag} TriggerZoneStayPrefix Fehler: {ex.Message}");
                return true; // niemals native Pfade per Exception blockieren
            }
        }

        /// <summary>
        /// 369 (Fix B): Prefix vor TruckAmenityTerminal.OnAmenityEnter — die LETZTE
        /// Sperre vor dem Werkstatt-/Amenity-Zustand. Egal ueber welchen Pfad das
        /// m_enterAmenityEvent hier ankommt (DockingCoroutine, AmenityTriggerZone,
        /// direktes Event): wenn der lokale Truck weit vom Terminal entfernt ist, kann
        /// der Eintritt nicht vom lokalen Spieler stammen -> skippen. Der Spieler, der
        /// selbst dockt/steht, ist natuerlich neben seinem Terminal (Docked-Truck und
        /// Terminal teilen die Station).
        /// Prioritaet 800: laeuft VOR dem 315c-Shop-Prefix (400) von ShopAtJobBoardBays —
        /// ein unterdrueckter fremder Eintritt kann dort niemals ein Shop-Open triggern.
        /// </summary>
        // ReSharper disable once RedundantAssignment
        public static bool TerminalEnterPrefix(TruckAmenityTerminal __instance)
        {
            try
            {
                var client = global::StarTruckMP.StarTruckClient.StarTruckClient.client;
                if (client == null || !client.IsConnected) return true; // Singleplayer: nativ
                if (__instance == null) return true;

                var myTruck = global::StarTruckMP.StarTruckClient.StarTruckClient.myTruck;
                if (myTruck == null) return true;

                var termPos = (__instance.transform != null) ? __instance.transform.position : Vector3.zero;
                var myPos = (myTruck.transform != null) ? myTruck.transform.position : Vector3.zero;
                var dist = Vector3.Distance(termPos, myPos);
                if (dist > AmenityGateMaxMeters)
                {
                    LogSuppress(__instance.gameObject, "TruckAmenityTerminal.OnAmenityEnter",
                        $"local truck {dist:F0}m vom Terminal entfernt (Limit {AmenityGateMaxMeters:F0}m) — Eintritt nicht vom lokalen Spieler");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{LogTag} TerminalEnterPrefix Fehler: {ex.Message}");
                return true;
            }
        }

        // ── Helpers ──

        /// <summary>
        /// Liefert das Root-GameObject der OnTriggerStay-Quelle: attachedRigidbody
        /// zuerst (Rigidbody-Trucks), sonst das Collider-GO selbst, sonst der oberste
        /// Parent. Niemals werfen.
        /// </summary>
        private static GameObject ResolveTriggerSource(Collider other)
        {
            try
            {
                var rb = other.attachedRigidbody;
                var go = rb != null ? rb.gameObject : other.gameObject;
                if (go == null) return null;
                // Topmost Parent (Trigger-Zonen-Subcollider zeigen auf den Truck-Root).
                var t = go.transform;
                while (t != null && t.parent != null) t = t.parent;
                return (t != null) ? t.gameObject : go;
            }
            catch { return null; }
        }


        private static DockingBay ResolveBayCached(DockingBaySharedAssets shared)
        {
            long key;
            try { key = shared.Pointer.ToInt64(); } catch { return null; }

            var now = Time.realtimeSinceStartup;
            if (bayCache.TryGetValue(key, out var cached))
            {
                // Unity-null-Check: Bay kann zerstoert worden sein (Sektorwechsel).
                if (cached.bay != null && now - cached.resolvedAt < BayCacheTtlSeconds)
                    return cached.bay;
                bayCache.Remove(key);
            }

            DockingBay resolved = null;
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
                        if (ReferenceEquals(sa, shared) || sa.Pointer == shared.Pointer)
                        {
                            resolved = bay;
                            break;
                        }
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{LogTag} ResolveBay fehlgeschlagen: {ex.Message}");
                return null;
            }

            if (resolved != null)
            {
                bayCache[key] = (resolved, now);
            }
            return resolved;
        }

        /// <summary>
        /// DockingBay.m_truck lesen (Property zuerst, Field als Fallback — Muster wie
        /// 315b in ShopAtJobBoardBays: Il2Cpp-Felder sind auf dem Proxy Properties).
        /// Die Reflexion ist absicht typunspezifisch, weil der Elementtyp (GameObject
        /// vs. Truck-Komponente) nicht aus dem api-dump hervorgeht.
        /// </summary>
        private static GameObject TryGetDockedTruckGameObject(DockingBay bay)
        {
            object value = ReadMember(bay, "m_truck");
            if (value == null) return null;

            var asGo = value as GameObject;
            if (asGo != null) return asGo;

            // Il2Cpp-Proxy einer Komponente: .gameObject via Reflexion holen.
            try
            {
                var goProp = value.GetType().GetProperty("gameObject");
                return goProp?.GetValue(value) as GameObject;
            }
            catch { return null; }
        }

        private static object ReadMember(UnityEngine.Object target, string memberName)
        {
            if (target == null) return null;
            try
            {
                var prop = target.GetType().GetProperty(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                {
                    return prop.GetValue(target);
                }
                var field = target.GetType().GetField(memberName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(target);
            }
            catch { return null; }
        }

        private static bool IsRemoteGhost(GameObject go)
        {
            return go != null && go.name != null &&
                   go.name.StartsWith("RemoteTruck", StringComparison.Ordinal);
        }

        private static bool SameNativeObject(object proxy, UnityEngine.Object native)
        {
            if (proxy == null || native == null) return false;
            try
            {
                var ptrProp = proxy.GetType().GetProperty("Pointer");
                if (ptrProp == null) return false;
                return Convert.ToInt64(ptrProp.GetValue(proxy)) == native.Pointer.ToInt64();
            }
            catch { return false; }
        }

        private static void LogSuppress(UnityEngine.Object anchor, string patchMethod, string reason)
        {
            suppressCount++;
            if (Time.realtimeSinceStartup - lastSuppressedLog < 5f) return;
            lastSuppressedLog = Time.realtimeSinceStartup;
            var anchorName = "<unknown>";
            try { anchorName = (anchor as MonoBehaviour)?.gameObject?.name
                    ?? (anchor as GameObject)?.name ?? anchor?.name ?? "<unknown>"; } catch { }
            StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Amenity-Eintritt unterdrueckt (#{suppressCount}, {patchMethod}, anchor={anchorName}): {reason}");
        }
    }
}
