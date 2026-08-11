using BepInEx.Unity.IL2CPP;
using BepInEx;
using Object = UnityEngine.Object;
using UnityEngine;
using HarmonyLib;
using System.Reflection;
using System;
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
    public const string customBuildNumber = "custom-build-109";
    internal static new ManualLogSource Log;
    public static ConfigEntry<string> IPAddress;
    public static ConfigEntry<int> MoveUpdate;
    public static ConfigEntry<UnityEngine.KeyCode> joinKey;
    public static ConfigEntry<UnityEngine.KeyCode> hostKey;
    public static ConfigEntry<string> PlayerName;
    public static ConfigEntry<UnityEngine.KeyCode> HonkKey;


    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"Plugin {pluginGuid} is loaded! [{customBuildNumber}]");
        IPAddress = Config.Bind("Server Info", "ServerIP", "127.0.0.1:7777", "IP Address to Join");
        MoveUpdate = Config.Bind("Server Info", "MovementUpdate", 100, "Movement update frequencey in ms");
        joinKey = Config.Bind("Keybinds", "JoinKey", UnityEngine.KeyCode.LeftBracket, "Set the Key to press for joining the listed IP");
        hostKey = Config.Bind("Keybinds", "HostKey", UnityEngine.KeyCode.RightBracket, "Set the Key to press for hosting a server");
        PlayerName = Config.Bind("Player Info", "PlayerName", "", "Your display name shown to other players (leave empty for default)");
        HonkKey = Config.Bind("Keybinds", "HonkKey", UnityEngine.KeyCode.H, "Set the Key to press for honking your horn");
        Harmony.CreateAndPatchAll(typeof(TruckClient));
        Harmony.CreateAndPatchAll(typeof(SonityDiagnostic));

    }

    [HarmonyPatch]
    public class TruckClient
    {
        [HarmonyPatch(typeof(PauseController), nameof(Update), new Type[] { })]
        [HarmonyPostfix]
        public static void Update()
        {
            StarTruckServer.StarTruckServer.Update();
            StarTruckClient.StarTruckClient.Update();
            StarTruckServer.StarTruckServer.FixedUpdate();
            StarTruckClient.StarTruckClient.FixedUpdate();
            StarTruckClient.StarTruckClient.SendMovement();
            StarTruckClient.StarTruckClient.CheckHonk();
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

    [HarmonyPatch]
    public class SonityDiagnostic
    {
        private static int playCount = 0;

        [HarmonyPatch(typeof(Sonity.SoundEvent), nameof(Sonity.SoundEvent.Play))]
        [HarmonyPostfix]
        public static void Play_Postfix(Sonity.SoundEvent __instance)
        {
            playCount++;
            if (playCount <= 20)
                StarTruckMP.Log.LogInfo($"[SONITY-DIAG] Play() name='{__instance.name}' count={playCount}");
        }

        [HarmonyPatch(typeof(Sonity.SoundEvent), nameof(Sonity.SoundEvent.PlayAtPosition), new Type[] { typeof(UnityEngine.Vector3) })]
        [HarmonyPostfix]
        public static void PlayAtPosition_V3_Postfix(Sonity.SoundEvent __instance, UnityEngine.Vector3 position)
        {
            playCount++;
            if (playCount <= 20)
                StarTruckMP.Log.LogInfo($"[SONITY-DIAG] PlayAtPosition(V3) name='{__instance.name}' pos=({position.x:F1},{position.y:F1},{position.z:F1}) count={playCount}");
        }

        [HarmonyPatch(typeof(Sonity.SoundEvent), nameof(Sonity.SoundEvent.Play2D))]
        [HarmonyPostfix]
        public static void Play2D_Postfix(Sonity.SoundEvent __instance)
        {
            playCount++;
            if (playCount <= 20)
                StarTruckMP.Log.LogInfo($"[SONITY-DIAG] Play2D() name='{__instance.name}' count={playCount}");
        }
    }
}
