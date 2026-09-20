using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using System.Linq;

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
        // 384: Alle DockingBays teilen sich EIN DockingBaySharedAssets-Objekt (Diagnose-Dump
        // Atlas Prime: alle 18 Bays shared=0x220CE172F00). Eine Aufloesung ueber den
        // SharedAssets-Pointer liefert deshalb IMMER dieselbe (beliebige) Bay -> das Gate
        // suppressete legitime Docks an allen anderen Stationen (Distanz zur falschen Bay).
        // Cache daher nur fuer die Bay-LISTE; die Auswahl erfolgt pro Aufruf (billig).
        private static List<DockingBay> allBaysCache = null;
        private static float allBaysCachedAt = -999f;
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

                // ── 369 (Fix B): der TATSAECHLICH wirksame Restpfad ──
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
                // Choke-Point: TruckAmenityTerminal.OnAmenityEnter — LETZTE Sperre: jeder
                // Amenity-Eintritt, der nicht vom lokal anwesenden Spieler kommt (lokaler
                // Truck weit weg vom Terminal), wird unterdrueckt.
                //
                // Ein zweiter Choke-Point (Harmony-Prefix direkt auf
                // AmenityTriggerZone.OnTriggerStay) existierte hier bis Build 381 und
                // wurde per zwei sauberen A/B-Feldtests (Build 380 + 381) als alleiniger
                // Verursacher eines reproduzierbaren harten PhysX-Absturzes identifiziert
                // und in Build 382 entfernt. Er ist inzwischen auch funktional ueberholt:
                // SweepGhostTriggerImmunity() (siehe unten) haelt Ghost-Truck-Collider
                // bereits auf Physik-Ebene per Physics.IgnoreCollision von der Amenity-
                // Triggerzone fern, wodurch OnTriggerStay fuer Ghosts dort NATIV nie mehr
                // feuert - der urspruengliche Zweck dieses Patches ist damit erfuellt,
                // ohne die instabile native Methode selbst patchen zu muessen.
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

                var myTruck = global::StarTruckMP.StarTruckClient.StarTruckClient.myTruck;
                if (myTruck == null) return true;

                // 384: Bay ueber den lokalen Truck bestimmen (m_truck == myTruck, sonst
                // naechste nicht von einem Ghost belegte Bay) statt ueber das globale
                // SharedAssets-Objekt.
                var bay = ResolveBayForLocalTruck(myTruck);
                if (bay == null) return true; // Bay nicht aufloesbar -> nie eingreifen

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

                LogAllow(bay.gameObject, dist);
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
                // 393: jeder Terminal-Eintritt wird VOR dem nativen Aufruf geloggt (auch ins Watchdog-Log).
                bool boardBusy = JobBoardComputer.BoardActiveOrRecent;
                Watchdog.Mark("AmenityEnter " + DescribeTerminal(__instance) + " dist=" + dist.ToString("F0") + "m boardBusy=" + boardBusy);
                // 393: waehrend Jobboard offen bzw. kurz danach KEIN Amenity-Eintritt (Werkstatt-Freeze-Verdacht).
                if (boardBusy)
                {
                    StarTruckMP.Log.LogWarning("393 AmenityEnter UNTERDRUECKT (Jobboard aktiv/kuerzlich): " + DescribeTerminal(__instance) + " dist=" + dist.ToString("F0") + "m");
                    return false;
                }
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

        /// <summary>393: Kurzbeschreibung eines Terminals (Name + Amenity-Typ), nur Reflection, crash-sicher.</summary>
        public static string DescribeTerminal(object t)
        {
            try
            {
                var go = (t as Component)?.gameObject;
                string name = go != null ? go.name : "?";
                string am = "?";
                try
                {
                    var pi = t.GetType().GetProperty("_currentAmenity");
                    if (pi != null) am = Convert.ToString(pi.GetValue(t, null));
                }
                catch { }
                return "'" + name + "' amenity=" + am;
            }
            catch { return "?"; }
        }

        /// <summary>393: loggt die 4 naechsten Amenity-Terminals (Werkstatt/Lackierer etc.) mit Entfernung zum lokalen Truck.</summary>
        public static void LogNearbyTerminals(string ctx)
        {
            try
            {
                var myTruck = global::StarTruckMP.StarTruckClient.StarTruckClient.myTruck;
                if (myTruck == null) return;
                var myPos = myTruck.transform.position;
                var list = new List<KeyValuePair<float, string>>();
                foreach (var t in UnityEngine.Object.FindObjectsOfType<TruckAmenityTerminal>())
                {
                    if (t == null) continue;
                    list.Add(new KeyValuePair<float, string>(Vector3.Distance(t.transform.position, myPos), DescribeTerminal(t)));
                }
                list.Sort((x, y) => x.Key.CompareTo(y.Key));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 4; i++) sb.Append(" | ").Append(list[i].Key.ToString("F0")).Append("m ").Append(list[i].Value);
                StarTruckMP.Log.LogInfo("393 NaechsteTerminals[" + ctx + "] von " + list.Count + ":" + sb);
            }
            catch (Exception ex) { StarTruckMP.Log.LogWarning("393 LogNearbyTerminals Fehler: " + ex.Message); }
        }

        // 379 Fix (Root Cause statt Nachbearbeitung): Physics.IgnoreCollision zwischen
        // JEDEM Ghost-Truck-Collider und JEDER Amenity-/Werkstatt-Triggerzone im
        // Sektor. Root Cause (Build 377/378 Live-Diagnose bestaetigt): Ghost-Trucks
        // liegen auf demselben Physik-Layer wie der lokale Truck (noetig fuer
        // Truck-vs-Truck-Kollision) und alle 32 Projekt-Layer sind bereits belegt -
        // ein dedizierter Ghost-Layer ist daher NICHT verfuegbar. Physics.IgnoreCollision
        // arbeitet stattdessen pro Collider-INSTANZ, nicht pro Layer: der lokale Truck
        // bleibt von der Werkstatt-Triggerzone komplett unberuehrt, nur Ghost-Truck
        // <-> Triggerzone wird ignoriert. Effekt: OnTriggerStay feuert fuer Ghosts an
        // dieser Zone NATIV nie mehr - der eigentliche Root Cause ist behoben, nicht
        // nur nachtraeglich abgefangen. Die bestehenden Gates (368/369/370, 311/315)
        // bleiben unveraendert als zusaetzliches Sicherheitsnetz aktiv, bis dieser Fix
        // im Feld bestaetigt ist.
        private static readonly HashSet<(int ghostColliderId, int zoneColliderId)> ignoredPairs =
            new HashSet<(int, int)>();

        public static void SweepGhostTriggerImmunity()
        {
            try
            {
                var client = global::StarTruckMP.StarTruckClient.StarTruckClient.client;
                if (client == null || !client.IsConnected) return; // Singleplayer: nichts zu tun

                var ghostColliders = new List<Collider>();
                foreach (var helper in UnityEngine.Object.FindObjectsOfType<global::StarTruckMP.Encoding.RemoteTruckCollisionHelper>())
                {
                    if (helper == null || helper.gameObject == null) continue;
                    try
                    {
                        // 380 Fix: NUR die dedizierte konvexe BoxCollider auf dem Truck-Root
                        // verwenden (siehe RemoteTruckCollisionHelper.Update() / Messages.cs
                        // createPlayer()). NIEMALS GetComponentsInChildren<Collider>() nutzen -
                        // das erfasst auch die absichtlich deaktivierten, nicht-konvexen
                        // Mesh-Collider der Exterior-Geometrie, und Physics.IgnoreCollision
                        // darauf destabilisiert PhysX (bekannter, im Code dokumentierter
                        // Crash: "PhysX crashes on two non-convex mesh colliders touching").
                        var boxCol = helper.gameObject.GetComponent<BoxCollider>();
                        if (boxCol != null) ghostColliders.Add(boxCol);
                    }
                    catch { }
                }
                if (ghostColliders.Count == 0) return;

                var zoneColliders = new List<Collider>();
                foreach (var zone in UnityEngine.Object.FindObjectsOfType<AmenityTriggerZone>())
                {
                    if (zone == null || zone.gameObject == null) continue;
                    try
                    {
                        // 380 Fix: nicht-konvexe MeshCollider ausschliessen (siehe Kommentar
                        // oben bei ghostColliders) - reine Vorsichtsmassnahme, betrifft die
                        // heutigen Box/Sphere/Capsule-Trigger-Collider nicht.
                        foreach (var c in zone.gameObject.GetComponentsInChildren<Collider>())
                        {
                            if (c is MeshCollider mc && !mc.convex) continue;
                            zoneColliders.Add(c);
                        }
                    }
                    catch { }
                }
                if (zoneColliders.Count == 0) return;

                int newlyIgnored = 0;
                foreach (var gc in ghostColliders)
                {
                    if (gc == null) continue;
                    int gcId = gc.GetInstanceID();
                    foreach (var zc in zoneColliders)
                    {
                        if (zc == null) continue;
                        int zcId = zc.GetInstanceID();
                        var key = (gcId, zcId);
                        if (ignoredPairs.Contains(key)) continue;
                        try
                        {
                            Physics.IgnoreCollision(gc, zc, true);
                            ignoredPairs.Add(key);
                            newlyIgnored++;
                        }
                        catch (Exception exPair)
                        {
                            StarTruckMP.Log.LogWarning($"379 SweepGhostTriggerImmunity: IgnoreCollision Fehler ({gc.name} / {zc.name}): {exPair.Message}");
                        }
                    }
                }
                if (newlyIgnored > 0)
                {
                    StarTruckMP.Log.LogInfo($"379 SweepGhostTriggerImmunity: {newlyIgnored} neue Ghost<->Triggerzone-Paare auf IgnoreCollision gesetzt (gesamt getrackt: {ignoredPairs.Count}, ghosts={ghostColliders.Count}, zones={zoneColliders.Count}).");
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"379 SweepGhostTriggerImmunity Fehler: {ex.Message}");
            }
        }

        // ── Helpers ──

        /// <summary>
        /// 384: Liste aller DockingBays im Sektor (FindObjectsOfType ist teuer, CanEnterAmenity
        /// kann pro Frame gepollt werden) - kurzer TTL-Cache nur fuer die Liste.
        /// </summary>
        private static List<DockingBay> GetAllBaysCached()
        {
            var now = Time.realtimeSinceStartup;
            if (allBaysCache != null && now - allBaysCachedAt < BayCacheTtlSeconds)
                return allBaysCache;
            try
            {
                var list = new List<DockingBay>();
                var bays = UnityEngine.Object.FindObjectsOfType<DockingBay>();
                if (bays != null)
                {
                    foreach (var bay in bays)
                        if (bay != null) list.Add(bay);
                }
                allBaysCache = list;
                allBaysCachedAt = now;
                return list;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"{LogTag} GetAllBays fehlgeschlagen: {ex.Message}");
                return null;
            }
        }

        private static bool TryGetPosition(UnityEngine.Object o, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                var c = o as Component;
                if (c != null && c.transform != null) { pos = c.transform.position; return true; }
                var g = o as GameObject;
                if (g != null && g.transform != null) { pos = g.transform.position; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 384: Bestimmt die Bay, an der der LOKALE Truck dockt: 1) Bay, deren m_truck der
        /// lokale Truck ist (eindeutig); 2) sonst die naechste Bay zum lokalen Truck, die
        /// nicht von einem Remote-Ghost belegt ist. Steht der lokale Truck weit von jeder
        /// Bay, liefert (2) eine ferne Bay -> das Distanz-Gate suppressed wie bisher.
        /// </summary>
        private static DockingBay ResolveBayForLocalTruck(UnityEngine.Object myTruck)
        {
            var bays = GetAllBaysCached();
            if (bays == null || bays.Count == 0) return null;
            if (!TryGetPosition(myTruck, out var myPos)) return null;

            DockingBay best = null;
            var bestDist = float.MaxValue;
            foreach (var bay in bays)
            {
                if (bay == null) continue;
                try
                {
                    var docked = TryGetDockedTruckGameObject(bay);
                    if (docked != null)
                    {
                        if (SameNativeObject(docked, myTruck)) return bay;
                        if (IsRemoteGhost(docked)) continue;
                    }
                    if (bay.transform == null) continue;
                    var d = Vector3.Distance(bay.transform.position, myPos);
                    if (d < bestDist) { bestDist = d; best = bay; }
                }
                catch { continue; }
            }
            return best;
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

        private static float lastAllowLog = -999f;

        /// <summary>385: Diagnose - erlaubter (lokaler) Amenity-Eintritt, gedrosselt auf 5 s.</summary>
        private static void LogAllow(UnityEngine.Object anchor, float dist)
        {
            if (Time.realtimeSinceStartup - lastAllowLog < 5f) return;
            lastAllowLog = Time.realtimeSinceStartup;
            var anchorName = "<unknown>";
            try { anchorName = (anchor as GameObject)?.name ?? anchor?.name ?? "<unknown>"; } catch { }
            StarTruckMP.Log.LogInfo($"{LogTag} AmenityLocalGate: Amenity-Eintritt erlaubt (lokal, bay={anchorName}, {dist:F0}m)");
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
