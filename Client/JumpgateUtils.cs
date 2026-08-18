using System;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Shared utilities for all three jumpgate departure board options.
    /// Handles local player gate detection via proximity + velocity (independent of
    /// currentDestinationGateId, which may be stale or empty at board creation time).
    /// </summary>
    public static class JumpgateUtils
    {
        private static FieldInfo fi_entryGateId = null;
        private static bool reflectionCached = false;

        public static void CacheReflection()
        {
            if (reflectionCached) return;
            reflectionCached = true;
            try
            {
                fi_entryGateId = typeof(WarpGate).GetField("entryGateId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { }
            StarTruckMP.Log.LogInfo($"JumpgateUtils: fi_entryGateId={(fi_entryGateId != null ? "found" : "null")}");
        }

        public static string GetEntryGateId(WarpGate gate)
        {
            if (gate == null) return "";
            try
            {
                if (fi_entryGateId != null)
                    return fi_entryGateId.GetValue(gate) as string ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Detect which gate the local player is approaching.
        /// Uses proximity + velocity vector (same as DetectDestinationGates).
        /// Returns the gate ID (entryGateId or fallback name) or empty string.
        /// </summary>
        public static string DetectLocalPlayerApproachingGate()
        {
            try
            {
                if (StarTruckClient.myTruck == null)
                {
                    StarTruckMP.Log.LogInfo("JumpgateUtils.DetectLocalPlayerApproachingGate: myTruck=null");
                    return "";
                }

                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return ""; }
                if (allGates == null || allGates.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("JumpgateUtils.DetectLocalPlayerApproachingGate: no gates in scene");
                    return "";
                }

                Vector3 myPos = StarTruckClient.floatingOrigin != null
                    ? StarTruckClient.floatingOrigin.m_currentOrigin + StarTruckClient.myTruck.transform.position
                    : StarTruckClient.myTruck.transform.position;
                Vector3 myVel = StarTruckClient.myTruckRigid != null
                    ? StarTruckClient.myTruckRigid.velocity : Vector3.zero;
                float mySpeed = myVel.magnitude;

                StarTruckMP.Log.LogInfo($"JumpgateUtils: player at ({myPos.x:F0},{myPos.y:F0},{myPos.z:F0}) vel=({myVel.x:F0},{myVel.y:F0},{myVel.z:F0}) speed={mySpeed:F0} gates={allGates.Length}");

                float bestScore = -1f;
                string bestGateId = "";

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;
                    Vector3 gatePos = zone.transform.position;
                    float dist = Vector3.Distance(myPos, gatePos);
                    if (dist > 1500f)
                    {
                        StarTruckMP.Log.LogInfo($"JumpgateUtils: gate '{zone.gameObject.name}' too far ({dist:F0}m > 1500)");
                        continue;
                    }

                    Vector3 toGate = (gatePos - myPos).normalized;
                    float dot = Vector3.Dot(myVel.normalized, toGate);

                    StarTruckMP.Log.LogInfo($"JumpgateUtils: gate '{zone.gameObject.name}' dist={dist:F0}m dot={dot:F2} speed={mySpeed:F0}");

                    if (dot < 0.3f)
                    {
                        StarTruckMP.Log.LogInfo($"JumpgateUtils: gate '{zone.gameObject.name}' dot={dot:F2} < 0.3 — REJECTED (not approaching)");
                        continue;
                    }

                    float score = (1f - dist / 1500f) * 0.5f + dot * 0.5f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        WarpGate gateComp = null;
                        try { gateComp = zone.GetComponent<WarpGate>(); } catch { }
                        if (gateComp == null)
                            try { gateComp = zone.GetComponentInParent<WarpGate>(); } catch { }

                        string gateId = GetEntryGateId(gateComp);
                        if (string.IsNullOrEmpty(gateId))
                        {
                            gateId = zone.gameObject.name;
                            int ci = gateId.IndexOf("(Clone)");
                            if (ci > 0) gateId = gateId.Substring(0, ci).Trim();
                        }
                        bestGateId = gateId;
                        StarTruckMP.Log.LogInfo($"JumpgateUtils: gate '{gateId}' score={score:F3} — new best");
                    }
                }

                StarTruckMP.Log.LogInfo($"JumpgateUtils: result = '{bestGateId}' (bestScore={bestScore:F3})");
                return bestGateId;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateUtils.DetectLocalPlayerApproachingGate error: {ex}");
                return "";
            }
        }
    }
}
