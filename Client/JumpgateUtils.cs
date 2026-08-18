using System;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    public static class JumpgateUtils
    {
        private static PropertyInfo pi_entryGateId = null;
        private static bool reflectionCached = false;

        public static void CacheReflection()
        {
            if (reflectionCached) return;
            reflectionCached = true;
            try
            {
                pi_entryGateId = typeof(SectorEntryPoint).GetProperty("entryGateId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { }
            StarTruckMP.Log.LogInfo("JumpgateUtils: pi_entryGateId=" + (pi_entryGateId != null ? "found" : "null"));
        }

        public static string GetEntryGateId(SectorEntryPoint ep)
        {
            if (ep == null) return "";
            try
            {
                if (pi_entryGateId != null)
                    return pi_entryGateId.GetValue(ep) as string ?? "";
            }
            catch { }
            return "";
        }

        public static SectorEntryPoint FindEntryPoint(GameObject go)
        {
            if (go == null) return null;
            SectorEntryPoint ep = null;
            try { ep = go.GetComponent<SectorEntryPoint>(); } catch { }
            if (ep == null) try { ep = go.GetComponentInParent<SectorEntryPoint>(); } catch { }
            if (ep == null) try { ep = go.GetComponentInChildren<SectorEntryPoint>(); } catch { }
            return ep;
        }

        public static string GetEntryGateIdForZone(WarpTriggerZone zone)
        {
            if (zone == null) return "";
            try
            {
                SectorEntryPoint ep = FindEntryPoint(zone.gameObject);
                string id = GetEntryGateId(ep);
                if (!string.IsNullOrEmpty(id)) return id;
                if (zone.transform.parent != null)
                {
                    ep = FindEntryPoint(zone.transform.parent.gameObject);
                    id = GetEntryGateId(ep);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
                // IL2CPP: foreach on Transform enumerator yields Il2CppSystem.Object boxes,
                // causing InvalidCastException. Use index-based GetChild() instead.
                int childCount = zone.transform.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    Transform child = zone.transform.GetChild(i);
                    ep = FindEntryPoint(child.gameObject);
                    id = GetEntryGateId(ep);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning("JumpgateUtils.GetEntryGateIdForZone: " + ex.Message);
            }
            string fallback = zone.gameObject.name;
            int ci = fallback.IndexOf("(Clone)");
            if (ci > 0) fallback = fallback.Substring(0, ci).Trim();
            return fallback;
        }

        /// <summary>
        /// Find the WarpGate component associated with a WarpTriggerZone. WarpGate is NOT
        /// necessarily on the same GameObject as the trigger collider (GetComponent/
        /// GetComponentInParent both came back empty in testing) — search the same
        /// self/parent/children pattern used for SectorEntryPoint above, plus
        /// GetComponentInChildren as an extra fallback.
        /// </summary>
        public static WarpGate FindWarpGateForZone(WarpTriggerZone zone)
        {
            if (zone == null) return null;
            try
            {
                WarpGate wg = null;
                try { wg = zone.GetComponent<WarpGate>(); } catch { }
                if (wg != null) return wg;
                try { wg = zone.GetComponentInParent<WarpGate>(); } catch { }
                if (wg != null) return wg;
                try { wg = zone.GetComponentInChildren<WarpGate>(); } catch { }
                if (wg != null) return wg;

                if (zone.transform.parent != null)
                {
                    try { wg = zone.transform.parent.GetComponentInChildren<WarpGate>(); } catch { }
                    if (wg != null) return wg;
                }

                // IL2CPP: index-based GetChild() to avoid foreach enumerator boxing issues.
                int childCount = zone.transform.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    Transform child = zone.transform.GetChild(i);
                    try { wg = child.GetComponent<WarpGate>(); } catch { }
                    if (wg != null) return wg;
                    try { wg = child.GetComponentInChildren<WarpGate>(); } catch { }
                    if (wg != null) return wg;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning("JumpgateUtils.FindWarpGateForZone: " + ex.Message);
            }
            return null;
        }
    }
}