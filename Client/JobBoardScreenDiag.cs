using System;
using System.Text;
using HarmonyLib;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// custom-build-390: reine Diagnose (kein Verhalten geaendert). Das Spiel verarbeitet die im
    /// Jobboard angenommenen Jobs vermutlich erst beim Verlassen des Screens
    /// (JobBoardScreen.ApplyUsersJobChoices). Wir loggen VOR und NACH dieser (nativen) Methode
    /// sowie vor/nach OnScreenBack, damit bei einem Freeze/Crash die letzte Log-Zeile zeigt,
    /// in welchem Schritt es passiert ist und welche Jobs betroffen waren.
    /// </summary>
    [HarmonyPatch]
    public class JobBoardScreenDiagPatches
    {
        private static string Describe(Il2CppSystem.Collections.Generic.List<global::QuestInstance> list, int max = 8)
        {
            if (list == null) return "null";
            try
            {
                int n = list.Count;
                var sb = new StringBuilder();
                sb.Append(n).Append(" [");
                for (int i = 0; i < n && i < max; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    try
                    {
                        var j = list[i];
                        string id = "?", name = "?", drop = "?";
                        try { id = j?.questId ?? "null"; } catch { }
                        try { name = j?.displayName ?? "null"; } catch { }
                        try { drop = j?.DropOffBay() ?? "null"; } catch { drop = "ERR"; }
                        sb.Append(id).Append(" '").Append(name).Append("' drop=").Append(drop);
                    }
                    catch (Exception ex) { sb.Append("ERR:" + ex.GetType().Name); }
                }
                if (n > max) sb.Append(" ...");
                sb.Append(']');
                return sb.ToString();
            }
            catch (Exception ex) { return "ERR:" + ex.Message; }
        }

        private static void Dump(string phase, global::JobBoardScreen s)
        {
            try
            {
                string sector = StarTruckClient.currentSector;
                bool online = StarTruckClient.client != null && StarTruckClient.client.IsConnected;
                string acc = "?", term = "?", active = "?";
                try { acc = Describe(s.userAcceptedJobs); } catch (Exception e) { acc = "ERR:" + e.Message; }
                try { term = Describe(s.userTerminatedJobs); } catch (Exception e) { term = "ERR:" + e.Message; }
                try { active = s.currentActiveJobs != null ? s.currentActiveJobs.Count.ToString() : "null"; } catch (Exception e) { active = "ERR:" + e.Message; }
                StarTruckMP.Log.LogInfo($"390 {phase}: sector='{sector}' online={online} accepted={acc} terminated={term} activeJobs={active}");
            }
            catch (Exception ex) { StarTruckMP.Log.LogWarning("390 JobBoardScreenDiag Fehler: " + ex.Message); }
        }

        [HarmonyPatch(typeof(global::JobBoardScreen), "ApplyUsersJobChoices")]
        [HarmonyPrefix]
        public static void Apply_Prefix(global::JobBoardScreen __instance) { Watchdog.Mark("ApplyUsersJobChoices BEGIN"); Dump("ApplyUsersJobChoices BEGIN", __instance); }

        [HarmonyPatch(typeof(global::JobBoardScreen), "ApplyUsersJobChoices")]
        [HarmonyPostfix]
        public static void Apply_Postfix(global::JobBoardScreen __instance) { Watchdog.Mark("ApplyUsersJobChoices END"); Dump("ApplyUsersJobChoices END (ohne Crash durchgelaufen)", __instance); }

        [HarmonyPatch(typeof(global::JobBoardScreen), "OnScreenBack")]
        [HarmonyPrefix]
        public static void Back_Prefix(global::JobBoardScreen __instance, string backToScreen) { Watchdog.Mark("OnScreenBack BEGIN"); Dump("OnScreenBack BEGIN (back='" + backToScreen + "')", __instance); }

        [HarmonyPatch(typeof(global::JobBoardScreen), "OnScreenBack")]
        [HarmonyPostfix]
        public static void Back_Postfix(global::JobBoardScreen __instance) { Watchdog.Mark("OnScreenBack END"); StarTruckMP.Log.LogInfo("390 OnScreenBack END (ohne Crash durchgelaufen)"); }
    }
}
