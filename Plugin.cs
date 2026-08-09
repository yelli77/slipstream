using BepInEx.Unity.IL2CPP;
using BepInEx;
using Object = UnityEngine.Object;
using HarmonyLib;
using System;
using System.Threading;
using BepInEx.Logging;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.UnityEngine;

namespace StarTruckMP;

[BepInPlugin(pluginGuid, pluginName, pluginVersion)]
public class StarTruckMP : BasePlugin
{
    public const string pluginGuid = "StarTruckMP";
    public const string pluginName = "Star Trucker MP";
    public const string pluginVersion = "0.1";
    public const string customBuildNumber = "custom-build-24";
    internal static new ManualLogSource Log;
    public static ConfigEntry<string> IPAddress;
    public static ConfigEntry<int> MoveUpdate;
    public static ConfigEntry<UnityEngine.KeyCode> joinKey;
    public static ConfigEntry<UnityEngine.KeyCode> hostKey;


    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"Plugin {pluginGuid} is loaded! [{customBuildNumber}]");
        IPAddress = Config.Bind("Server Info", "ServerIP", "127.0.0.1:7777", "IP Address to Join");
        MoveUpdate = Config.Bind("Server Info", "MovementUpdate", 100, "Movement update frequencey in ms");
        joinKey = Config.Bind("Keybinds", "JoinKey", UnityEngine.KeyCode.LeftBracket, "Set the Key to press for joining the listed IP");
        hostKey = Config.Bind("Keybinds", "HostKey", UnityEngine.KeyCode.RightBracket, "Set the Key to press for hosting a server");
        Harmony.CreateAndPatchAll(typeof(TruckClient));
        StartNetworkThread();
    }

    private static Thread _networkThread;
    private static volatile bool _networkRunning;

    private static void StartNetworkThread()
    {
        _networkRunning = true;
        _networkThread = new Thread(() =>
        {
            Log.LogInfo("Network thread started");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double acc = 0;
            const double dt = 1.0 / 60.0;
            while (_networkRunning)
            {
                double elapsed = sw.Elapsed.TotalSeconds;
                sw.Restart();
                acc += elapsed;
                while (acc >= dt)
                {
                    try { StarTruckServer.StarTruckServer.server.Update(); } catch { }
                    try { StarTruckClient.StarTruckClient.client.Update(); } catch { }
                    acc -= dt;
                }
                Thread.Sleep(1);
            }
            Log.LogInfo("Network thread stopped");
        });
        _networkThread.IsBackground = true;
        _networkThread.Name = "StarTruckMP-Network";
        _networkThread.Start();
    }

    [HarmonyPatch]
    public class TruckClient
    {
        [HarmonyPatch(typeof(PauseController), nameof(Update), new Type[] { })]
        [HarmonyPostfix]
        public static void Update()
        {
            try { StarTruckServer.StarTruckServer.Update(); } catch (Exception ex) { Log.LogError($"Server.Update error: {ex.Message}"); }
            try { StarTruckClient.StarTruckClient.Update(); } catch (Exception ex) { Log.LogError($"Client.Update error: {ex.Message}"); }
            try { StarTruckClient.StarTruckClient.ReanchorRemotePlayersToFloatingOrigin(); } catch (Exception ex) { Log.LogError($"Reanchor error: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(CustomizationState), nameof(CustomizationState.EquipLivery))]
        [HarmonyPostfix]
        public static void EquipLivery(string itemId)
        {
            try { StarTruckClient.StarTruckClient.equipLivery(itemId); } catch (Exception ex) { Log.LogError($"EquipLivery error: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(SectorPersistence), nameof(SectorPersistence.OnArrivedAtSector))]
        [HarmonyPostfix]
        public static void OnArrivedAtSector(Object sender, EventArgs eventArgs)
        {
            try { StarTruckClient.StarTruckClient.OnArrivedAtSector(); } catch (Exception ex) { Log.LogError($"OnArrivedAtSector error: {ex.Message}"); }
        }
    }
}
