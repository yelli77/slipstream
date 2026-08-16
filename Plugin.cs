using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using BepInEx;
using Object = UnityEngine.Object;
using UnityEngine;
using HarmonyLib;
using System.Reflection;
using System;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.UnityEngine;
using StarTruckMP.MainMenu;

namespace StarTruckMP;

[BepInPlugin(pluginGuid, pluginName, pluginVersion)]
public class StarTruckMP : BasePlugin
{
    public const string pluginGuid = "StarTruckMP";
    public const string pluginName = "Star Trucker MP";
    public const string pluginVersion = "0.1";
    // WICHTIG: bei jedem Release-Build hochzaehlen (siehe version.json) - customBuildNumber ist
    // nur ein Anzeige-String, protocolBuildNumber ist die tatsaechlich fuer den Versionscheck
    // gegen den Server verwendete Zahl.
    public const string customBuildNumber = "custom-build-176";
    public const int protocolBuildNumber = 151;
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
        ClassInjector.RegisterTypeInIl2Cpp<global::StarTruckMP.Encoding.RemoteTruckCollisionHelper>();

        Harmony.CreateAndPatchAll(typeof(TruckClient));
        Harmony.CreateAndPatchAll(typeof(global::StarTruckMP.StarTruckClient.JobBoardSyncPatches));
        Harmony.CreateAndPatchAll(typeof(global::StarTruckMP.StarTruckClient.CargoSyncPatches));

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

        // Online/Offline-Umschalter sitzt im Pause-Menue (nicht im Hauptmenue) - da ist der
        // Verbindungsstatus tatsaechlich relevant, weil man da schon in einer Spielsession ist.
        [HarmonyPatch(typeof(PauseScreen), nameof(PauseScreen.Awake))]
        [HarmonyPostfix]
        public static void PauseScreen_Awake(PauseScreen __instance)
        {
            try { OnlineModeToggle.CreateToggleButton(__instance); } catch (Exception ex) { Log.LogWarning($"OnlineModeToggle-Button konnte nicht erstellt werden: {ex.Message}"); }
        }

        // ScreenController.Activate() wird von JEDEM Screen beim Anzeigen aufgerufen - deshalb
        // hier auf PauseScreen filtern, sonst wuerde das Label bei jedem beliebigen Screen-Wechsel
        // (unnoetig, aber harmlos) mit aktualisiert.
        [HarmonyPatch(typeof(com.monsterandmonster.Menu.ScreenController), nameof(com.monsterandmonster.Menu.ScreenController.Activate))]
        [HarmonyPostfix]
        public static void ScreenController_Activate(com.monsterandmonster.Menu.ScreenController __instance)
        {
            if (__instance is PauseScreen)
            {
                try { OnlineModeToggle.UpdateLabel(); } catch { /* Label-Refresh darf nie den Rest stoppen */ }
            }
        }

    }

}
