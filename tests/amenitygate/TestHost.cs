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
        public int Priority { get; set; }
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
        public long Pointer = 0;
        public string name = "";
        public static List<Object> Registry = new List<Object>();
        public static T[] FindObjectsOfType<T>() where T : Object
            => Registry.OfType<T>().ToArray();
    }
    public class GameObject : Object
    {
        public GameObject(string n) { name = n; }
        public Transform transform;
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
    }
    public static class Time
    {
        public static float realtimeSinceStartup = 0f;
    }
}

// ── Game proxy stubs ──
public class DockingBaySharedAssets : UnityEngine.Object { }
public class DockingBay : UnityEngine.MonoBehaviour
{
    public DockingBaySharedAssets m_sharedAssets;   // proxy property surface
    public UnityEngine.GameObject m_truck;          // docked truck (proxy property)
}

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
        var shared = new DockingBaySharedAssets { Pointer = sharedPtr };
        var bay = new DockingBay { m_sharedAssets = shared, m_truck = dockedTruck };
        bay.gameObject = new UnityEngine.GameObject("Docking_Bay_XX");
        bay.transform = new UnityEngine.Transform { position = bayPos };
        UnityEngine.Object.Registry.Add(shared);
        UnityEngine.Object.Registry.Add(bay);
        return bay;
    }
    static long sharedPtr = 5000;
    static DockingBaySharedAssets Shared(long p) =>
        (DockingBaySharedAssets)UnityEngine.Object.Registry.First(o => o is DockingBaySharedAssets s && s.Pointer == p);

    public static int Main()
    {
        var dockedMyTruck = new UnityEngine.GameObject("StarTruck(Clone)") { Pointer = 111 };
        dockedMyTruck.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
        var ghost = new UnityEngine.GameObject("RemoteTruck7") { Pointer = 222 };
        ghost.transform = new UnityEngine.Transform { position = new UnityEngine.Vector3(10, 0, 0) };
        var farMyTruck = new UnityEngine.GameObject("StarTruck(Clone)") { Pointer = 333 };
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
            var shared = new DockingBaySharedAssets { Pointer = 9999 };
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
        Console.WriteLine("Log lines: " + StarTruckMP.Log.Lines.Count);
        foreach (var l in StarTruckMP.Log.Lines) Console.WriteLine("  " + l);
        Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failed}");
        return Environment.ExitCode;
    }
}