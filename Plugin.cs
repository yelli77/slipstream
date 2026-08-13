using BepInEx.Unity.IL2CPP;
using BepInEx;
using Object = UnityEngine.Object;
using UnityEngine;
using HarmonyLib;
using System.Reflection;
using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.UnityEngine;

namespace StarTruckMP;

[BepInPlugin(pluginGuid, pluginName, pluginVersion)]
public class StarTruckMP : BasePlugin
{
    public const string pluginGuid = "StarTruckMP";
    public const string pluginName = "Star Trucker MP";
    public const string pluginVersion = "0.1";
    public const string customBuildNumber = "custom-build-137";
    internal static new ManualLogSource Log;

    // Feste Werte, keine Konfigurationsdatei mehr noetig: Server-Adresse ist der einzige
    // oeffentliche dedizierte Server, Movement-Sync-Intervall ist eine Netzwerk-Tuning-Konstante,
    // Hupe laesst sich ohnehin im Spiel selbst binden.
    public const string ServerAddress = "31.97.125.237:7777";
    public const int MovementUpdateMs = 100;
    public static readonly UnityEngine.KeyCode HonkKey = UnityEngine.KeyCode.H;


    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"Plugin {pluginGuid} is loaded! [{customBuildNumber}]");
        Harmony.CreateAndPatchAll(typeof(TruckClient));

    }

    [HarmonyPatch]
    public class TruckClient
    {
        [HarmonyPatch(typeof(PauseController), nameof(Update), new Type[] { })]
        [HarmonyPostfix]
        public static void Update()
        {
            StarTruckClient.StarTruckClient.Update();
            StarTruckClient.StarTruckClient.FixedUpdate();
            StarTruckClient.StarTruckClient.CheckHonk();
            StarTruckClient.StarTruckClient.SendMovement();
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
