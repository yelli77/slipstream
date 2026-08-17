using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using StarTruckMP.Utilities;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 3D World-Space Billboard near each WarpGate showing which players
    /// have marked this gate as their destination jumpgate.
    /// Shows: Gate Name, player list (sorted by distance), or "FREE" when empty.
    /// Replaces the old screen-space WarpGateHUD overlay.
    /// </summary>
    public static class WarpGateBillboard
    {
        private static List<GateBillboard> billboards = new List<GateBillboard>();
        private static string lastSector = "none";
        private static float nextUpdateTime = 0f;
        private static readonly float UpdateInterval = 0.5f; // 2 Hz text updates
        private static readonly float BillboardDistance = 500f; // meters before gate
        private static readonly float MaxVisibleDistance = 5000f; // hide if camera too far

        // Colors
        private static readonly Color GateNameColor = Color.white;
        private static readonly Color PlayerColor = new Color(0.2f, 1f, 0.8f, 1f);    // cyan-green
        private static readonly Color DistColor = new Color(0.7f, 0.9f, 1f, 0.8f);     // light blue
        private static readonly Color FreeColor = new Color(0f, 1f, 0.5f, 0.95f);      // bright green
        private static readonly Color BgColor = new Color(0.05f, 0.08f, 0.15f, 0.85f); // dark blue-black
        private static readonly Color SepColor = new Color(0.3f, 0.5f, 0.7f, 0.6f);    // separator

        // Reflection cache for gate name resolution
        private static FieldInfo fi_entryGateName = null;
        private static FieldInfo fi_entryGateId = null;
        private static bool gateNameReflectionSearched = false;

        /// <summary>
        /// MonoBehaviour that makes the billboard face the camera each frame.
        /// Must be a concrete class for IL2CPP injection.
        /// </summary>
        public class BillboardBehavior : MonoBehaviour
        {
            private Camera mainCam = null;

            public void Awake()
            {
                mainCam = Camera.main;
            }

            public void Update()
            {
                if (mainCam == null)
                    mainCam = Camera.main;
                if (mainCam == null) return;

                // Billboard: face camera
                Vector3 dir = mainCam.transform.position - transform.position;
                if (dir.sqrMagnitude > 0.01f)
                {
                    // Face the camera, but keep upright (lock Y rotation if needed)
                    transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
                }
            }
        }

        private class GateBillboard
        {
            public WarpTriggerZone gateZone;
            public string gateId;
            public string displayName;
            public GameObject rootObj;
            public TMPro.TextMeshPro nameLabel;
            public TMPro.TextMeshPro separator;
            public TMPro.TextMeshPro contentLabel; // player list or "FREE"
            public GameObject bgQuad;
        }

        /// <summary>
        /// Resolves gate ID via reflection on WarpGate component.
        /// Tries entryGateId, then entryGateName, then gameObject.name.
        /// </summary>
        private static string GetGateId(WarpTriggerZone zone)
        {
            if (zone == null) return "unknown";

            WarpGate gate = null;
            try { gate = zone.GetComponent<WarpGate>(); } catch { }
            if (gate == null)
            {
                try { gate = zone.GetComponentInParent<WarpGate>(); } catch { }
            }

            if (gate != null)
            {
                try
                {
                    if (!gateNameReflectionSearched)
                    {
                        gateNameReflectionSearched = true;
                        var gateType = gate.GetType();
                        fi_entryGateId = gateType.GetField("entryGateId",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        fi_entryGateName = gateType.GetField("entryGateName",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    if (fi_entryGateId != null)
                    {
                        var val = fi_entryGateId.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }

                    // Try property too
                    var propId = gate.GetType().GetProperty("entryGateId",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (propId != null)
                    {
                        var val = propId.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }
                }
                catch { }
            }

            // Fallback: cleaned gameObject.name
            string raw = zone.gameObject?.name ?? "Gate";
            int cloneIdx = raw.IndexOf("(Clone)");
            if (cloneIdx > 0) raw = raw.Substring(0, cloneIdx).Trim();
            return raw;
        }

        /// <summary>
        /// Resolves display name for gate (human-readable).
        /// </summary>
        private static string GetGateName(WarpTriggerZone zone)
        {
            if (zone == null) return "Gate ???";

            WarpGate gate = null;
            try { gate = zone.GetComponent<WarpGate>(); } catch { }
            if (gate == null)
            {
                try { gate = zone.GetComponentInParent<WarpGate>(); } catch { }
            }

            if (gate != null)
            {
                try
                {
                    if (fi_entryGateName != null)
                    {
                        var val = fi_entryGateName.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }

                    var prop = gate.GetType().GetProperty("entryGateName",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var val = prop.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }
                }
                catch { }
            }

            string raw = zone.gameObject?.name ?? "Gate";
            int cloneIdx = raw.IndexOf("(Clone)");
            if (cloneIdx > 0) raw = raw.Substring(0, cloneIdx).Trim();
            return raw;
        }

        /// <summary>
        /// Finds an existing TextMeshPro (3D) object in scene to clone as template.
        /// </summary>
        private static TMPro.TextMeshPro FindSourceTMP()
        {
            var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshPro>();
            if (allTMP == null) return null;
            foreach (var tmp in allTMP)
            {
                if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                    return tmp;
            }
            return null;
        }

        /// <summary>
        /// Creates a world-space billboard for a single gate.
        /// </summary>
        private static void CreateBillboard(WarpTriggerZone zone, TMPro.TextMeshPro sourceTMP)
        {
            string gateId = GetGateId(zone);
            string gateName = GetGateName(zone);

            // Root object
            GameObject root = new GameObject($"Billboard_{gateName}");
            root.transform.SetParent(null); // world-space, not under any parent
            root.transform.position = zone.transform.position + zone.transform.forward * BillboardDistance;

            // Billboard behavior (faces camera)
            root.AddComponent<BillboardBehavior>();

            // Background quad (dark panel behind text)
            GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "BG";
            bg.transform.SetParent(root.transform, false);
            bg.transform.localPosition = Vector3.zero;
            bg.transform.localScale = new Vector3(15f, 25f, 1f);
            var bgRenderer = bg.GetComponent<MeshRenderer>();
            if (bgRenderer != null)
            {
                bgRenderer.material = new Material(Shader.Find("Standard"));
                bgRenderer.material.color = BgColor;
                bgRenderer.material.SetInt("_Cull", 0);
                bgRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                bgRenderer.receiveShadows = false;
                bgRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                bgRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
            // Remove collider from quad
            var bgCollider = bg.GetComponent<Collider>();
            if (bgCollider != null) UnityEngine.Object.Destroy(bgCollider);

            // Gate name label (top area)
            GameObject nameObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            nameObj.name = "GateName";
            nameObj.transform.localPosition = new Vector3(0f, 9f, -0.1f);
            nameObj.transform.localScale = Vector3.one;
            var nameTMP = nameObj.GetComponent<TMPro.TextMeshPro>();
            if (nameTMP != null)
            {
                nameTMP.text = $"EXIT GATE: {gateName}";
                nameTMP.fontSize = 6f;
                nameTMP.color = GateNameColor;
                nameTMP.alignment = TMPro.TextAlignmentOptions.Center;
                nameTMP.enableAutoSizing = false;
                nameTMP.overflowMode = TMPro.TextOverflowModes.Overflow;
                nameTMP.enableWordWrapping = false;
            }

            // Separator line
            GameObject sepObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            sepObj.name = "Separator";
            sepObj.transform.localPosition = new Vector3(0f, 7.5f, -0.1f);
            sepObj.transform.localScale = Vector3.one;
            var sepTMP = sepObj.GetComponent<TMPro.TextMeshPro>();
            if (sepTMP != null)
            {
                sepTMP.text = "————————————————";
                sepTMP.fontSize = 3f;
                sepTMP.color = SepColor;
                sepTMP.alignment = TMPro.TextAlignmentOptions.Center;
                sepTMP.enableAutoSizing = false;
            }

            // Content label (player list or "FREE")
            GameObject contentObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            contentObj.name = "Content";
            contentObj.transform.localPosition = new Vector3(0f, 2f, -0.1f);
            contentObj.transform.localScale = Vector3.one;
            var contentTMP = contentObj.GetComponent<TMPro.TextMeshPro>();
            if (contentTMP != null)
            {
                contentTMP.text = "FREE";
                contentTMP.fontSize = 5f;
                contentTMP.color = FreeColor;
                contentTMP.alignment = TMPro.TextAlignmentOptions.Center;
                contentTMP.enableAutoSizing = false;
                contentTMP.overflowMode = TMPro.TextOverflowModes.Overflow;
                contentTMP.enableWordWrapping = false;
            }

            billboards.Add(new GateBillboard
            {
                gateZone = zone,
                gateId = gateId,
                displayName = gateName,
                rootObj = root,
                nameLabel = nameTMP,
                separator = sepTMP,
                contentLabel = contentTMP,
                bgQuad = bg
            });
        }

        /// <summary>
        /// Collects all players (remote + local) heading to a specific gate.
        /// Returns list of (name, distance) tuples, sorted by distance.
        /// </summary>
        private static List<(string name, float distance)> GetPlayersForGate(string gateId, Vector3 gateWorldPos)
        {
            var result = new List<(string, float)>();

            // Remote players
            if (StarTruckClient.playerList != null)
            {
                foreach (var kv in StarTruckClient.playerList)
                {
                    if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                    if (string.IsNullOrEmpty(kv.Value.sector) || kv.Value.sector == "none") continue;
                    if (string.IsNullOrEmpty(kv.Value.destinationGateId)) continue;
                    if (kv.Value.destinationGateId != gateId) continue;

                    Vector3 playerPos = new Vector3(
                        kv.Value.truckTrans.Pos.x,
                        kv.Value.truckTrans.Pos.y,
                        kv.Value.truckTrans.Pos.z
                    );
                    // Floating origin correction
                    if (StarTruckClient.floatingOrigin != null)
                        playerPos += StarTruckClient.floatingOrigin.m_currentOrigin;

                    float dist = Vector3.Distance(gateWorldPos, playerPos);
                    result.Add((kv.Value.Name, dist));
                }
            }

            // Local player
            if (!string.IsNullOrEmpty(StarTruckClient.currentDestinationGateId)
                && StarTruckClient.currentDestinationGateId == gateId
                && StarTruckClient.myTruck != null)
            {
                Vector3 localPos = StarTruckClient.myTruck.transform.position;
                float dist = Vector3.Distance(gateWorldPos, localPos);
                result.Add(("(Du)", dist));
            }

            // Sort by distance (closest first)
            result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return result;
        }

        /// <summary>
        /// Updates the content text of a billboard (player list or "FREE").
        /// </summary>
        private static void UpdateBillboardContent(GateBillboard bb)
        {
            if (bb.contentLabel == null || bb.gateZone == null) return;

            Vector3 gateWorldPos = bb.gateZone.transform.position;
            var players = GetPlayersForGate(bb.gateId, gateWorldPos);

            if (players.Count == 0)
            {
                bb.contentLabel.text = "FREE";
                bb.contentLabel.fontSize = 8f;
                bb.contentLabel.color = FreeColor;
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                int maxShow = Mathf.Min(players.Count, 10);
                for (int i = 0; i < maxShow; i++)
                {
                    var (name, dist) = players[i];
                    string distText;
                    if (dist >= 1000f)
                        distText = $"{dist / 1000f:F1}km";
                    else
                        distText = $"{dist:F0}m";

                    sb.AppendLine($"POS {i + 1}. {name} --- {distText}");
                }
                if (players.Count > 10)
                    sb.AppendLine($"... +{players.Count - 10} more");

                bb.contentLabel.text = sb.ToString().TrimEnd();
                bb.contentLabel.fontSize = 4f;
                bb.contentLabel.color = PlayerColor;
            }
        }

        public static void RefreshBillboards()
        {
            try
            {
                if (!StarTruckClient.client.IsConnected) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                // Only refresh on sector change
                if (sector == lastSector) return;
                lastSector = sector;

                ClearBillboards();

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null)
                {
                    StarTruckMP.Log.LogWarning("WarpGateBillboard: no source TMP found, skipping.");
                    return;
                }

                // Find all warp gates in scene
                WarpTriggerZone[] allGates;
                try
                {
                    allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>();
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"WarpGateBillboard: FindObjectsOfType failed: {ex.Message}");
                    return;
                }

                if (allGates == null || allGates.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("WarpGateBillboard: no WarpTriggerZone objects found.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"WarpGateBillboard: found {allGates.Length} gate(s) in '{sector}'");

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;
                    try
                    {
                        CreateBillboard(zone, sourceTMP);
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"WarpGateBillboard: creation failed for '{zone.gameObject.name}': {ex.Message}");
                    }
                }

                StarTruckMP.Log.LogInfo($"WarpGateBillboard: {billboards.Count} billboard(s) created.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"WarpGateBillboard.RefreshBillboards error: {ex}");
            }
        }

        public static void UpdatePositions()
        {
            if (billboards.Count == 0) return;

            Camera cam = Camera.main;
            if (cam == null) cam = StarTruckClient.playerCam?.GetComponent<Camera>();
            if (cam == null) return;

            Vector3 camPos = cam.transform.position;
            float now = Time.realtimeSinceStartup;
            bool doTextUpdate = now >= nextUpdateTime;
            if (doTextUpdate) nextUpdateTime = now + UpdateInterval;

            foreach (var bb in billboards)
            {
                if (bb.gateZone == null || bb.gateZone.gameObject == null || bb.rootObj == null) continue;

                try
                {
                    Vector3 gateWorldPos = bb.gateZone.transform.position;
                    float distToCamera = Vector3.Distance(camPos, gateWorldPos);

                    // Hide if camera too far
                    if (distToCamera > MaxVisibleDistance)
                    {
                        if (bb.rootObj.activeSelf) bb.rootObj.SetActive(false);
                        continue;
                    }

                    if (!bb.rootObj.activeSelf) bb.rootObj.SetActive(true);

                    // Update text (throttled to 2 Hz)
                    if (doTextUpdate)
                    {
                        UpdateBillboardContent(bb);
                    }
                }
                catch { }
            }
        }

        public static void OnSectorChanged()
        {
            lastSector = "none";
            ClearBillboards();
            RefreshBillboards();
        }

        public static void Cleanup()
        {
            ClearBillboards();
            lastSector = "none";
        }

        private static void ClearBillboards()
        {
            foreach (var bb in billboards)
            {
                if (bb.rootObj != null)
                    UnityEngine.Object.Destroy(bb.rootObj);
            }
            billboards.Clear();
        }
    }
}
