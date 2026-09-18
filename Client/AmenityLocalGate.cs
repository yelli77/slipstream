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
                gatePrefix.Priority = Priority.First;

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
                        LogSuppress(bay, $"docked truck ist Remote-Ghost '{dockedTruck.name}' (local '{myTruck.name}')");
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
                    LogSuppress(bay, $"local truck {dist:F0}m von Bay entfernt (Limit {AmenityGateMaxMeters:F0}m)");
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

        // ── Helpers ──

        private static DockingBay ResolveBayCached(DockingBaySharedAssets shared)
        {
            long key;
            try { key = shared.Pointer; } catch { return null; }

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
                return Convert.ToInt64(ptrProp.GetValue(proxy)) == native.Pointer;
            }
            catch { return false; }
        }

        private static void LogSuppress(DockingBay bay, string reason)
        {
            suppressCount++;
            if (Time.realtimeSinceStartup - lastSuppressedLog < 5f) return;
            lastSuppressedLog = Time.realtimeSinceStartup;
            var bayName = "<unknown>";
            try { bayName = bay.gameObject?.name ?? "<unknown>"; } catch { }
            StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Amenity-Eintritt unterdrueckt (#{suppressCount}, bay={bayName}): {reason}");
        }
    }
}
