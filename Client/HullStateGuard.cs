using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// custom-build-391: Schutz vor dem HullState-Fehlersturm.
    ///
    /// Befund (Player-prev.log, Sitzung mit Freeze): 104.811x NullReferenceException in
    /// HullState.FixedUpdate -> FinaliseCollision -> CalculateOtherCollisionEnergy(GameObject collidee)
    /// -> GameObject.GetComponent. HullState.smallCollisions ist ein HashSet&lt;GameObject&gt;; enthaelt es ein
    /// inzwischen zerstoertes Objekt, wirft die Summenbildung in JEDEM FixedUpdate und die Menge wird nie
    /// geleert (Dauer-Exception, Log wachst auf ~100 MB, Spiel wird immer langsamer bis Freeze).
    ///
    /// Fix: (1) Prefix auf CalculateOtherCollisionEnergy: null/zerstoerter collidee => 0 statt Exception.
    ///      (2) Finalizer auf FinaliseCollision: entweicht trotzdem eine Exception, wird smallCollisions
    ///          geleert und die Exception verschluckt (Sturm bricht nach dem ersten Fehler ab).
    /// Diagnose: Ring der zuletzt kollidierten Objektnamen (HullState.OnCollisionEnter) + Kontext-Log beim
    /// ersten Auftreten, damit klar wird, WELCHES Objekt es war (Remote-Truck? Trailer? Station?).
    /// </summary>
    [HarmonyPatch]
    public class HullStateGuard
    {
        private const int RING = 8;
        private static readonly string[] ringNames = new string[RING];
        private static readonly float[] ringTimes = new float[RING];
        private static int ringPos = 0;
        private static string lastRecorded = null;

        private static int guardHits = 0;
        private static int finalizerHits = 0;

        private static string PlayerContext()
        {
            try
            {
                var sb = new StringBuilder();
                var list = global::StarTruckMP.StarTruckClient.StarTruckClient.playerList;
                sb.Append("remotePlayers=").Append(list != null ? list.Count : -1);
                if (list != null)
                {
                    sb.Append(" [");
                    bool first = true;
                    foreach (var kv in list)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        bool truckOk = false;
                        try { truckOk = kv.Value.Truck != null; } catch { }
                        sb.Append(kv.Key).Append(truckOk ? ":truck" : ":no-truck");
                    }
                    sb.Append(']');
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "ctx-ERR:" + ex.Message; }
        }

        private static string RingDump()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < RING; i++)
            {
                int idx = (ringPos + i) % RING; // aelteste zuerst
                if (ringNames[idx] == null) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(ringNames[idx]).Append(" @").Append(ringTimes[idx].ToString("F1")).Append('s');
            }
            return sb.Length == 0 ? "(leer)" : sb.ToString();
        }

        private static void LogFirstAndSometimes(string kind, int hits)
        {
            if (hits == 1 || hits % 5000 == 0)
            {
                string sector = global::StarTruckMP.StarTruckClient.StarTruckClient.currentSector;
                StarTruckMP.Log.LogWarning($"391 HullStateGuard[{kind}] Treffer #{hits}: zerstoerter/leerer Kollisionspartner abgefangen. sector='{sector}' t={Time.time:F1}s {PlayerContext()} letzte Kollisionen: {RingDump()}");
            }
        }

        // Ring fuellen: HullState.OnCollisionEnter (private void OnCollisionEnter(Collision))
        [HarmonyPatch(typeof(global::HullState), "OnCollisionEnter")]
        [HarmonyPrefix]
        public static void OnCollisionEnter_Prefix(Collision collision)
        {
            try
            {
                var go = collision?.gameObject;
                if (go == null) return;
                string n = go.name;
                if (n == lastRecorded) return; // Dauerkontakt nicht mehrfach eintragen
                lastRecorded = n;
                ringNames[ringPos] = n;
                ringTimes[ringPos] = Time.time;
                ringPos = (ringPos + 1) % RING;
            }
            catch { }
        }

        [HarmonyPatch(typeof(global::HullState), "CalculateOtherCollisionEnergy")]
        [HarmonyPrefix]
        public static bool CalcEnergy_Prefix(GameObject collidee, ref float __result)
        {
            try
            {
                if ((UnityEngine.Object)collidee == null)
                {
                    __result = 0f;
                    guardHits++;
                    LogFirstAndSometimes("Prefix", guardHits);
                    return false; // Original nicht ausfuehren (wuerde NRE werfen)
                }
            }
            catch { }
            return true;
        }

        [HarmonyPatch(typeof(global::HullState), "FinaliseCollision")]
        [HarmonyFinalizer]
        public static Exception Finalise_Finalizer(Exception __exception, global::HullState __instance)
        {
            if (__exception == null) return null;
            try
            {
                finalizerHits++;
                // smallCollisions ist ein Il2CppSystem HashSet (Assembly Il2CppSystem.Core nicht referenziert) -> per Reflection leeren.
                try
                {
                    var prop = typeof(global::HullState).GetProperty("smallCollisions");
                    var set = prop?.GetValue(__instance);
                    set?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(set, null);
                }
                catch { }
                LogFirstAndSometimes("Finalizer(" + __exception.GetType().Name + ")", finalizerHits);
                return null; // Exception verschlucken
            }
            catch { return __exception; }
        }
    }
}
