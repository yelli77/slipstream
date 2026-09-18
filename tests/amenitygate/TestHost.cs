// Stub harness: compile-check + logic test for Client/AmenityLocalGate.cs
// Mirrors the minimal API surface used by the gate (HarmonyLib, UnityEngine,
// game DockingBay/DockingBaySharedAssets proxies, StarTruckClient statics).
// lives under tests/ (excluded from the mod build via csproj Compile Remove).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// ── HarmonyLib stubs ──
namespace HarmonyLib
{
    public static class Priority { public const int First = 800; }
    public class HarmonyMethod
    {
        public int priority; // HarmonyMethod-Feld (Il2Cpp-Realitaet, Property-Schreibweise kompiliert nicht)
        public int Priority { get => priority; set => priority = value; }
        public HarmonyMethod(Type t, string m) { }
    }
    public class Harmony
    {
        public Harmony(string id) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix = null) { }
    }
    public static class AccessTools
    {
        public static Type TypeByName(string n) => null;
        public static MethodInfo Method(Type t, string n) => null;
    }
}

// ── UnityEngine stubs ──
namespace UnityEngine
{
    public class Object
    {
        public IntPtr Pointer = IntPtr.Zero;
        public string name = "";
        public static List<Object> Registry = new List<Object>();
        public static T[] FindObjectsOfType<T>() where T : Object
            => Registry.OfType<T>().ToArray();
        private static int nextInstanceId = 1;
        private readonly int instanceId = nextInstanceId++;
        public int GetInstanceID() => instanceId; // 379-Diag-Stub
    }
    public class GameObject : Object
    {
        public GameObject(string n) { name = n; }
        public Transform transform;
        public int layer = 0; // 377-Diag-Stub: reines Test-Double, keine echte Physik-Layer-Logik
        public List<Collider> Colliders = new List<Collider>(); // 379-Diag-Stub
        public T[] GetComponentsInChildren<T>() where T : class
            => Colliders.OfType<T>().ToArray();
    }
    public static class LayerMask
    {
        // 377-Diag-Stub: reicht fuer die Testsuite (nur Logging, keine Assertions darauf).
        public static string LayerToName(int layer) => $"Layer{layer}";
    }
    public static class Physics
    {
        // 378-Diag-Stub: reicht fuer die Testsuite (nur Logging, keine Assertions darauf).
        public static bool GetIgnoreLayerCollision(int layerA, int layerB) => false;
        // 379-Diag-Stub: no-op, nur damit AmenityLocalGate.cs kompiliert.
        public static void IgnoreCollision(Collider a, Collider b, bool ignore) { }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
    }
    public class MonoBehaviour : Component { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static float Distance(Vector3 a, Vector3 b)
            => (float)Math.Sqrt((a.x-b.x)*(a.x-b.x) + (a.y-b.y)*(a.y-b.y) + (a.z-b.z)*(a.z-b.z));
    }
    public class Transform
    {
        public Vector3 position;
        public Transform parent;
        public GameObject gameObject;
    }
    public class Rigidbody
    {
        public GameObject gameObject;
    }
    public class Collider : Object
    {
        public Rigidbody attachedRigidbody;
        public GameObject gameObject;
    }
    public static class Time
    {
        public static float realtimeSinceStartup = 0f;
    }
}

// ── Game proxy stubs (Restpfade, Fix B) ──
public class DockingBaySharedAssets : UnityEngine.Object { }
public class DockingBay : UnityEngine.MonoBehaviour
{
    public DockingBaySharedAssets m_sharedAssets;   // proxy property surface
    public UnityEngine.GameObject m_truck;          // docked truck (proxy property)
    public static int k_triggerColliderLayer = 0;   // 377-Diag-Stub: native Konstante, hier nur Platzhalter
}
public class AmenityTriggerZone : UnityEngine.MonoBehaviour { }
namespace StarTruckMP.Encoding
{
    public class RemoteTruckCollisionHelper : UnityEngine.MonoBehaviour { } // 379-Diag-Stub
}
public class TruckAmenityTerminal : UnityEngine.MonoBehaviour { }

// ── Mod statics stubs ──
namespace StarTruckMP { public static class Log { public static List<string> Lines = new List<string>();
    public static void LogInfo(string m) => Lines.Add("INFO: " + m);
    public static void LogWarning(string m) => Lines.Add("WARN: " + m); } }
namespace Riptide { public class Client { public bool IsConnected = true; } }

namespace StarTruckMP.StarTruckClient
{
    public static class StarTruckClient
    {
        public static Riptide.Client client = new Riptide.Client();
        public static UnityEngine.GameObject myTruck;
    }
}

// ── Test driver ──
public static class TestGate
{
    static int failed = 0;
    static void Check(string name, bool cond)
    {
        Console.WriteLine((cond ? "PASS" : "FAIL") + ": " + name);
        if (!cond) { Environment.ExitCode = 1; failed++; }
    }

    static DockingBay MakeBay(long sharedPtr, UnityEngine.GameObject dockedTruck, UnityEngine.Vector3 bayPos)
    {
        var shared = new DockingBaySharedAssets { Pointer = (IntPtr)sharedPtr };
        var bay = new DockingBay { m_sharedAssets = shared, m_truck = dockedTruck };
        bay.gameObject = new UnityEngine.GameObject("Docking_Bay_XX");
        bay.transform = new UnityEngine.Transform { position = bayPos };
        UnityEngine.Object.Registry.Add(shared);
        UnityEngine.Object.Registry.Add(bay);
        return bay;
    }
    static long sharedPtr = 5000;
    static DockingBaySharedAssets Shared(long p) =>
        (DockingBaySharedAssets)UnityEngine.Object.Registry.First(o => o is DockingBaySharedAssets s && s.Pointer == (IntPtr)p);

    public static int Main()
    {
        var dockedMyTruck = new UnityEngine.GameObject("StarTruck(Clone)") { Pointer = (IntPtr)111 };
        dockedMyTruck.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
        var ghost = new UnityEngine.GameObject("RemoteTruck7") { Pointer = (IntPtr)222 };
        ghost.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
        var farMyTruck = new UnityEngine.GameObject("StarTruck(Clone)") { Pointer = (IntPtr)333 };
        farMyTruck.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(9000, 0, 0) };

        // Case 1: legit dock — bay's m_truck == local truck → allow
        {
            sharedPtr = 5000;
            var bay = MakeBay(5000, dockedMyTruck, dockedMyTruck.transform.position);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck;
            Check("legit dock (m_truck == myTruck) allowed", StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(Shared(5000)));
        }
        // Case 2: ghost docked in bay → suppress (the bug scenario)
        {
            sharedPtr = 5001;
            var bay = MakeBay(5001, ghost, ghost.transform.position);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = farMyTruck;
            var shared = Shared(sharedPtr);
            Check("ghost dock suppressed (m_truck == RemoteTruck7)", !StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
        }
        // Case 3: no m_truck yet, local truck far away → suppress (distance gate)
        {
            sharedPtr = 5002;
            var bay = MakeBay(5002, null, new UnityEngine.Vector3(500, 0, 0));
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = farMyTruck; // 9000m away
            var shared = Shared(sharedPtr);
            Check("far truck + no docked truck -> distance gate suppress", !StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
        }
        // Case 4: near truck, no m_truck → allow
        {
            sharedPtr = 5003;
            var bay = MakeBay(5003, null, dockedMyTruck.transform.position);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck;
            var shared = Shared(sharedPtr);
            Check("near truck, empty bay -> allow", StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
        }
        // Case 5: singleplayer (not connected) -> never interfere
        {
            StarTruckMP.StarTruckClient.StarTruckClient.client = null;
            sharedPtr = 5004;
            var bay = MakeBay(5004, ghost, ghost.transform.position);
            var shared = Shared(sharedPtr);
            Check("singleplayer (client null) -> allow native", StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
            StarTruckMP.StarTruckClient.StarTruckClient.client = new Riptide.Client();
        }
        // Case 6: unresolvable bay -> allow (never break native)
        {
            var shared = new DockingBaySharedAssets { Pointer = (IntPtr)9999 };
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck;
            Check("unresolvable bay -> allow", StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
        }
        // Case 7: cache TTL hit -> same verdict without re-scan (no new bay registered)
        {
            sharedPtr = 7777;
            var bay = MakeBay(7777, ghost, ghost.transform.position);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = farMyTruck;
            var shared = Shared(sharedPtr);
            Check("first verdict suppress (ghost)", !StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
            UnityEngine.Time.realtimeSinceStartup = 1f; // within 2s TTL
            Check("cached verdict within TTL suppress (ghost)", !StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
            UnityEngine.Time.realtimeSinceStartup = 10f; // TTL expired -> registry rescan finds same bay
            Check("cache expired -> rescan, still suppressed (ghost)", !StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(shared));
        }
        // ── 369 Fix B: AmenityTriggerZone.OnTriggerStay (der 368-Restpfad) ──
        // Case 8: Ghost loest OnTriggerStay aus, lokaler Truck weit weg -> suppress
        {
            var zone = new AmenityTriggerZone();
            zone.gameObject = new UnityEngine.GameObject("AmenityTrigger_Workshop");
            zone.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(500, 0, 0) };
            UnityEngine.Object.Registry.Add(zone);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = farMyTruck; // 9000m
            var col = new UnityEngine.Collider { gameObject = ghost, attachedRigidbody = new UnityEngine.Rigidbody { gameObject = ghost } };
            Check("B1: Ghost-Trigger in Zone + lokaler Truck weit -> suppress",
                !StarTruckMP.StarTruckClient.AmenityLocalGate.TriggerZoneStayPrefix(zone, col));
        }
        // Case 9: lokaler Truck selbst in der Zone -> native (allow)
        {
            var zone = new AmenityTriggerZone();
            zone.gameObject = new UnityEngine.GameObject("AmenityTrigger_Workshop2");
            zone.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
            UnityEngine.Object.Registry.Add(zone);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck; // 10m
            var col = new UnityEngine.Collider { gameObject = dockedMyTruck, attachedRigidbody = new UnityEngine.Rigidbody { gameObject = dockedMyTruck } };
            Check("B2: lokaler Truck in Zone -> allow (nativ)",
                StarTruckMP.StarTruckClient.AmenityLocalGate.TriggerZoneStayPrefix(zone, col));
        }
        // Case 10 (370-Fix): Ghost-Trigger, lokaler Truck NAHE -> jetzt IMMER suppress.
        // Vorher "allow (ambiguous)" - genau das war der 369-Feldbug: an gemeinsamen
        // Shop/Werkstatt/JobBoard-Bays ist "lokaler Truck nah" der Normalfall, nicht
        // die Ausnahme, die Distanz-Ausnahme liess den Ghost-Trigger fast immer durch.
        {
            var zone = new AmenityTriggerZone();
            zone.gameObject = new UnityEngine.GameObject("AmenityTrigger_Workshop3");
            zone.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
            UnityEngine.Object.Registry.Add(zone);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck;
            var col = new UnityEngine.Collider { gameObject = ghost, attachedRigidbody = null };
            Check("B3: Ghost-Trigger + lokaler Truck nahe -> suppress (370: kein Ambiguous-Fall mehr)",
                !StarTruckMP.StarTruckClient.AmenityLocalGate.TriggerZoneStayPrefix(zone, col));
        }
        // Case 11: TruckAmenityTerminal.OnAmenityEnter — letzte Sperre
        {
            var termFar = new TruckAmenityTerminal();
            termFar.gameObject = new UnityEngine.GameObject("Terminal_Far");
            termFar.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(500, 0, 0) };
            UnityEngine.Object.Registry.Add(termFar);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = farMyTruck; // 9000m
            Check("B4: Terminal weit vom lokalen Truck -> suppress (letzter Choke-Point)",
                !StarTruckMP.StarTruckClient.AmenityLocalGate.TerminalEnterPrefix(termFar));

            var termNear = new TruckAmenityTerminal();
            termNear.gameObject = new UnityEngine.GameObject("Terminal_Near");
            termNear.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(12, 0, 0) };
            UnityEngine.Object.Registry.Add(termNear);
            StarTruckMP.StarTruckClient.StarTruckClient.myTruck = dockedMyTruck;
            Check("B5: Terminal am eigenen Dock -> allow (eigener Spieler)",
                StarTruckMP.StarTruckClient.AmenityLocalGate.TerminalEnterPrefix(termNear));
        }

        // ── 369 Fix C: Gate(800) vs. 311 Shop-Rewrite(400) — Harmony-Chain-Simulation ──
        // Harmony sortiert Prefixes absteigend nach Priority; ein Prefix mit return false
        // skippt Native + alle niedriger priorisierten Prefixes. ShopPrefixPriority
        // (=400) ist hier gespiegelt, weil ShopAtJobBoardBays.cs (Il2Cpp-Abhaengigkeiten)
        // nicht im Stub-Projekt kompiliert.
        {
            const int shopPrefixPriorityMirrored = 400;
            Check("C0: Gate-Priority (800) > 311-ShopPrefix-Priority (400) — Gate entscheidet zuerst",
                StarTruckMP.StarTruckClient.AmenityLocalGate.GatePriority > shopPrefixPriorityMirrored);
        }
        RunChainSim(dockedMyTruck, ghost, farMyTruck);

        /// <summary>
        /// 369 Fix C: Simuliert die Harmony-Prefix-Kette auf
        /// DockingBaySharedAssets.EnterAmenity — Gate (Prio 800, ECHTER Code) zuerst,
        /// dann der 311-Shop-Rewrite (Prio 400, gespiegelt), dann der Postfix
        /// (Screen-Open, gespiegelt). Beweist: Ghost-Dock => kein Rewrite, kein
        /// Screen-Open; legitimes Shop-Dock => Rewrite + Screen-Open laufen.
        /// </summary>
        void RunChainSim(UnityEngine.GameObject localTruck, UnityEngine.GameObject ghostTruck, UnityEngine.GameObject farTruck)
        {
            int shopRewrites, screenOpens;
            void Chain(UnityEngine.GameObject docked, UnityEngine.GameObject myTruckState, long ptr, string label)
            {
                shopRewrites = 0; screenOpens = 0;
                StarTruckMP.StarTruckClient.StarTruckClient.myTruck = myTruckState;
                // Priorities absteigend: Gate(800) zuerst, dann 311-Prefix(400), dann Postfix.
                bool gateAllows = StarTruckMP.StarTruckClient.AmenityLocalGate.AmenityGatePrefix(Shared(ptr));
                // 311-EnterAmenityPrefix (gespiegelt): void-Prefix, schreibt __args um
                // (JobsBoard->Shop) und feuert den bewiesenen ShopScreen-Open-Pfad.
                if (gateAllows) // nur bei gateAllows erreicht Harmony den 311-Prefix + Native + Postfix
                {
                    // nur JobsBoard-Bays werden umgeschrieben (311-Guard gespiegelt)
                    bool isJobsBoardBay = true; // Test-Bays sind JobsBoard-Bays
                    if (isJobsBoardBay) { shopRewrites++; screenOpens++; }
                }
                Check($"C1[{label}]: Ghost/Fern-Dock -> KEIN 311-Rewrite, KEIN Screen-Open (rewrites={shopRewrites}, opens={screenOpens})",
                    docked == ghostTruck || myTruckState == farTruck
                        ? (shopRewrites == 0 && screenOpens == 0)
                        : (shopRewrites == 1 && screenOpens == 1));
            }

            // Ghost dockt (m_truck == RemoteTruck), lokaler Truck weit weg:
            sharedPtr = 8100;
            MakeBay(8100, ghostTruck, ghostTruck.transform.position);
            Chain(ghostTruck, farTruck, 8100, "Ghost-Dock");

            // Legitimes Shop-Dock: m_truck == lokaler Truck, truck an der Bay:
            sharedPtr = 8101;
            MakeBay(8101, localTruck, localTruck.transform.position);
            Chain(localTruck, localTruck, 8101, "Legit-Shop-Dock");
        }

        Console.WriteLine("Log lines: " + StarTruckMP.Log.Lines.Count);
        foreach (var l in StarTruckMP.Log.Lines) Console.WriteLine("  " + l);
        Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failed}");
        return Environment.ExitCode;
    }
}