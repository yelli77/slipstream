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
    }
}