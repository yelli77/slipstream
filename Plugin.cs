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
    public const string customBuildNumber = "custom-build-246";
    public const int protocolBuildNumber = 152;
    internal static new ManualLogSource Log;

    // Feste Werte, keine Konfigurationsdatei mehr noetig: Server-Adresse ist der einzige
    // oeffentliche dedizierte Server, Movement-Sync-Intervall ist eine Netzwerk-Tuning-Konstante,
    // Hupe laesst sich ohnehin im Spiel selbst binden.
    public const string ServerAddress = "31.97.125.237:7777";
    public const int MovementUpdateMs = 100;
    public static readonly UnityEngine.KeyCode HonkKey = UnityEngine.KeyCode.H;

    // Nur true, wenn das Spiel gerade ueber den Slipstream-Launcher gestartet wurde (per
    // Consume-once-Marker erkannt, siehe StarTruckMP.Common.LaunchMarker). Bei einem Start direkt
    // aus der Steam-Bibliothek (ohne Launcher) bleibt das false - dann wird weder der
    // Online/Offline-Umschalter im Pause-Menue angezeigt noch jemals automatisch verbunden.
    public static bool LaunchedViaSlipstream = false;


    public override void Load()
    {
        Log = base.Log;

        LaunchedViaSlipstream = global::StarTruckMP.Common.LaunchMarker.ConsumeIfFresh();
        Log.LogInfo(LaunchedViaSlipstream
            ? "Ueber Slipstream gestartet - Online-Funktion aktiv."
            : "Nicht ueber Slipstream gestartet (z.B. direkt via Steam) - Online-Funktion bleibt deaktiviert.");

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
            StarTruckClient.DockingBayHUD.UpdatePositions();
            StarTruckClient.WarpGateBillboard.UpdatePositions();
            StarTruckClient.JumpgateOption1.UpdatePositions();
            StarTruckClient.JumpgateOption2.UpdatePositions();
            StarTruckClient.JumpgateOption3.UpdatePositions();
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
            try { StarTruckClient.DockingBayHUD.OnSectorChanged(); } catch (Exception ex) { Log.LogError($"DockingBayHUD.OnSectorChanged error: {ex.Message}"); }
            try { StarTruckClient.WarpGateBillboard.OnSectorChanged(); } catch (Exception ex) { Log.LogError($"WarpGateBillboard.OnSectorChanged error: {ex.Message}"); }
            try { StarTruckClient.JumpgateOption1.CreateBoards(); } catch (Exception ex2) { Log.LogError($"JumpgateOption1.CreateBoards error: {ex2.Message}"); }
            try { StarTruckClient.JumpgateOption2.CreateBoards(); } catch (Exception ex3) { Log.LogError($"JumpgateOption2.CreateBoards error: {ex3.Message}"); }
            try { StarTruckClient.JumpgateOption3.CreateBoards(); } catch (Exception ex4) { Log.LogError($"JumpgateOption3.CreateBoards error: {ex4.Message}"); }
        }

        // Online/Offline-Umschalter sitzt im Pause-Menue (nicht im Hauptmenue) - da ist der
        // Verbindungsstatus tatsaechlich relevant, weil man da schon in einer Spielsession ist.
        [HarmonyPatch(typeof(PauseScreen), nameof(PauseScreen.Awake))]
        [HarmonyPostfix]
        public static void PauseScreen_Awake(PauseScreen __instance)
        {
            try { OnlineModeToggle.CreateToggleButton(__instance); } catch (Exception ex) { Log.LogWarning($"OnlineModeToggle-Button konnte nicht erstellt werden: {ex.Message}"); }
        }

        // Vorheriger Ansatz (ScreenController.Activate() + "__instance is PauseScreen") hat beim
        // echten Test NIE gefeuert - vermutlich funktioniert der C#-"is"-Typcheck auf IL2CPP-
        // Interop-Objekten hier nicht zuverlaessig (aehnliche Interop-Eigenheiten hatten wir
        // schon bei Listen/Nullable). Stattdessen: direkt PauseScreen.OnPauseButton() patchen -
        // das ist die konkrete, eindeutige Methode, die beim Druecken der Pause-Taste laeuft,
        // kein Typcheck noetig. Protected -> ueber String statt nameof() patchen.
        [HarmonyPatch(typeof(PauseScreen), "OnPauseButton")]
        [HarmonyPostfix]
        public static void PauseScreen_OnPauseButton()
        {
            try { OnlineModeToggle.UpdateLabel(); } catch { /* Label-Refresh darf nie den Rest stoppen */ }
            try { OnlineModeToggle.RefreshNavigation(); } catch (Exception ex) { Log.LogWarning($"OnlineModeToggle.RefreshNavigation Fehler: {ex.Message}"); }
        }

    }

}
