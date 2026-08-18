using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using StarTruckMP.Utilities;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 3D World-Space Billboard near each WarpGate showing which players
    /// (and NPC trucks) have marked this gate as their destination jumpgate.
    /// Shows: Gate Name, ranked list (sorted by distance), or "FREE" when empty.
    /// Uses WorldSpace Canvas + TextMeshProUGUI (proven DockingBayHUD pattern).
    /// </summary>
    public static class WarpGateBillboard
    {
        private static List<GateBillboard> billboards = new List<GateBillboard>();
        private static string lastSector = "none";
        private static float nextUpdateTime = 0f;
        private static readonly float UpdateInterval = 0.5f;
        private static readonly float BillboardDistance = 50f;
        private static readonly float SideOffset = 125f;   // pushes the board off the direct flight line
        private static readonly float HeightOffset = 12f; // and up, like a roadside/gantry sign

        // How far / how aligned with a gate an NPC (or player) truck has to be
        // before it counts as "heading to" that gate. Mirrors Client.DetectDestinationGates().
        private static readonly float HeadingCheckRadius = 1500f;
        private static readonly float HeadingMinDot = 0.3f;

        // Colors
        private static readonly Color GateNameColor = Color.white;
        private static readonly Color PlayerColor = new Color(0.2f, 1f, 0.8f, 1f);
        private static readonly Color NpcColor = new Color(1f, 0.85f, 0.3f, 1f);
        private static readonly Color FreeColor = new Color(0f, 1f, 0.5f, 0.95f);
        private static readonly Color BgColor = new Color(0.05f, 0.08f, 0.15f, 0.85f);
        private static readonly Color SepColor = new Color(0.3f, 0.5f, 0.7f, 0.6f);

        // Reflection cache for gate name resolution
        private static FieldInfo fi_entryGateName = null;
        private static FieldInfo fi_entryGateId = null;
        private static bool gateNameReflectionSearched = false;

        // Reflection cache for AIVehicleDriver (NPC) name/id resolution
        private static PropertyInfo pi_driverId = null;
        private static bool driverReflectionSearched = false;

        private static bool diagLogged = false;

        /// <summary>
        /// MonoBehaviour that makes the billboard face the camera each frame.
        /// Must be a concrete class for IL2CPP injection.
        /// </summary>

        private class GateBillboard
        {
            public string gateId;
            public string gateName;
            public WarpTriggerZone gateZone;
            public GameObject rootObj;
            public TMPro.TextMeshPro nameTMP;
            public TMPro.TextMeshPro sepTMP;
            public TMPro.TextMeshPro contentTMP;
            public float lastContentUpdate;
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
        /// Finds an existing TextMeshProUGUI object in scene to clone as template.
        /// Same pattern as DockingBayHUD.FindSourceTMP().
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
        /// Configures a TMP label cloned from sourceTMP so it actually renders.
        /// Cloning an arbitrary scene TMP object via Instantiate() can silently carry
        /// over state that makes the clone invisible even though the GameObject exists
        /// (e.g. enableAutoSizing recalculating to a near-zero size in the new, differently
        /// proportioned RectTransform, a stale/shared font material reference, or a
        /// CanvasRenderer alpha baked in from a faded/hidden source panel). This resets
        /// every one of those explicitly instead of hoping the clone "just works".
        /// </summary>

        /// <summary>
        /// Creates a world-space billboard for a single gate using Canvas + TMPUGUI.
        /// </summary>
        private static void CreateBillboard(WarpTriggerZone zone, TMPro.TextMeshPro sourceTMP)
        {
            string gateId = GetGateId(zone);
            string gateName = GetGateName(zone);

            var sectorGO = GameObject.Find("[Sector]");
            if (sectorGO == null) return;

            // Root container at gate position
            GameObject root = new GameObject($"Billboard_{gateName}");
            SceneManager.MoveGameObjectToScene(root, sectorGO.scene);
            root.transform.SetParent(null);

            // Position beside gate
            Vector3 gateForward = -zone.transform.forward;
            Vector3 sideAxis = Vector3.Cross(Vector3.up, gateForward);
            if (sideAxis.sqrMagnitude < 0.01f) sideAxis = zone.transform.right;
            sideAxis.Normalize();
            root.transform.position = zone.transform.position
                + gateForward * BillboardDistance
                + sideAxis * SideOffset
                + Vector3.up * HeightOffset;

            // Static rotation: same direction as gate
            root.transform.rotation = Quaternion.LookRotation(gateForward, Vector3.up);

            // 3D backing slab
            GameObject backing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            backing.name = "Backing_" + gateName;
            backing.transform.SetParent(root.transform, false);
            backing.transform.localPosition = new Vector3(0f, 0f, 0.15f);
            backing.transform.localScale = new Vector3(8f, 12f, 0.1f);
            var bRenderer = backing.GetComponent<MeshRenderer>();
            if (bRenderer != null)
            {
                var mat = new Material(Shader.Find("Standard"));
                if (mat.shader != null)
                {
                    mat.color = new Color(0.05f, 0.05f, 0.1f, 0.92f);
                    mat.SetFloat("_Mode", 3f);
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = 3000;
                }
                bRenderer.material = mat;
                bRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                bRenderer.receiveShadows = false;
            }
            var autoCol = backing.GetComponent<Collider>();
            if (autoCol != null) UnityEngine.Object.Destroy(autoCol);

            // --- 3D TextMeshPro lines ---
            float y = 4.5f;

            // EXIT GATE line
            GameObject nameObj = new GameObject("Name_" + gateName);
            nameObj.transform.SetParent(root.transform, false);
            nameObj.transform.localPosition = new Vector3(0f, y, -0.1f);
            nameObj.transform.localRotation = Quaternion.identity;
            var nameTMP = nameObj.AddComponent<TMPro.TextMeshPro>();
            CopyTMPSettings(sourceTMP, nameTMP);
            nameTMP.text = "EXIT GATE:\n" + gateName;
            nameTMP.fontSize = 4f;
            nameTMP.color = GateNameColor;
            nameTMP.alignment = TMPro.TextAlignmentOptions.Center;
            nameTMP.overflowMode = TMPro.TextOverflowModes.Overflow;
            nameTMP.enableWordWrapping = false;
            nameTMP.enableAutoSizing = false;
            nameTMP.rectTransform.sizeDelta = new Vector2(12f, 4f);

            y -= 3.2f;

            // Separator
            GameObject sepObj = new GameObject("Sep_" + gateName);
            sepObj.transform.SetParent(root.transform, false);
            sepObj.transform.localPosition = new Vector3(0f, y, -0.1f);
            sepObj.transform.localRotation = Quaternion.identity;
            var sepTMP = sepObj.AddComponent<TMPro.TextMeshPro>();
            CopyTMPSettings(sourceTMP, sepTMP);
            sepTMP.text = "————————————————";
            sepTMP.fontSize = 2f;
            sepTMP.color = SepColor;
            sepTMP.alignment = TMPro.TextAlignmentOptions.Center;
            sepTMP.overflowMode = TMPro.TextOverflowModes.Overflow;
            sepTMP.enableWordWrapping = false;
            sepTMP.enableAutoSizing = false;
            sepTMP.rectTransform.sizeDelta = new Vector2(12f, 1f);

            y -= 1.5f;

            // Player content
            GameObject contentObj = new GameObject("Content_" + gateName);
            contentObj.transform.SetParent(root.transform, false);
            contentObj.transform.localPosition = new Vector3(0f, y, -0.1f);
            contentObj.transform.localRotation = Quaternion.identity;
            var contentTMP = contentObj.AddComponent<TMPro.TextMeshPro>();
            CopyTMPSettings(sourceTMP, contentTMP);
            contentTMP.text = "FREE";
            contentTMP.fontSize = 3.5f;
            contentTMP.color = FreeColor;
            contentTMP.alignment = TMPro.TextAlignmentOptions.Center;
            contentTMP.overflowMode = TMPro.TextOverflowModes.Overflow;
            contentTMP.enableWordWrapping = false;
            contentTMP.enableAutoSizing = false;
            contentTMP.rectTransform.sizeDelta = new Vector2(12f, 8f);

            if (!diagLogged)
            {
                var cam = Camera.main;
                float camDist = cam != null ? Vector3.Distance(root.transform.position, cam.transform.position) : -1f;
                StarTruckMP.Log.LogInfo($"WarpGateBillboard: placed '{gateName}' at {root.transform.position}, camDist={camDist:F0}m (3D TMP)");
            }

            billboards.Add(new GateBillboard
            {
                gateId = gateId,
                gateName = gateName,
                gateZone = zone,
                rootObj = root,
                nameTMP = nameTMP,
                sepTMP = sepTMP,
                contentTMP = contentTMP,
                lastContentUpdate = 0f,
            });
        }

        private static void CopyTMPSettings(TMPro.TextMeshPro source, TMPro.TextMeshPro target)
        {
            if (source == null || target == null) return;
            if (source.font != null) target.font = source.font;
            if (source.fontSharedMaterial != null) target.fontSharedMaterial = source.fontSharedMaterial;
            target.raycastTarget = false;
            target.alpha = 1f;
        }

        /// <summary>
        /// Collects all players (remote + local) and NPC trucks heading to a specific
        /// gate. Returns list of (name, distance) tuples, sorted by distance — the
        /// sort order is what determines each entry's "POS N" slot on the board.
        /// </summary>
        private static List<(string name, float distance)> GetPlayersForGate(string gateId, Vector3 gateWorldPos)
        {
            var result = new List<(string, float)>();

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
                    if (StarTruckClient.floatingOrigin != null)
                        playerPos += StarTruckClient.floatingOrigin.m_currentOrigin;

                    float dist = Vector3.Distance(gateWorldPos, playerPos);
                    result.Add((kv.Value.Name, dist));
                }
            }

            if (!string.IsNullOrEmpty(StarTruckClient.currentDestinationGateId)
                && StarTruckClient.currentDestinationGateId == gateId
                && StarTruckClient.myTruck != null)
            {
                Vector3 localPos = StarTruckClient.myTruck.transform.position;
                float dist = Vector3.Distance(gateWorldPos, localPos);
                result.Add(("(Du)", dist));
            }

            result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return result;
        }

        /// <summary>
        /// Updates the content text of a billboard (player list or "FREE").
        /// </summary>
        private static void UpdateBillboardContent(GateBillboard bb)
        {
            if (bb.contentTMP == null) return;
            Vector3 gateWorldPos = bb.gateZone.transform.position;
            var players = GetPlayersForGate(bb.gateId, gateWorldPos);
            if (players.Count == 0)
            {
                bb.contentTMP.text = "FREE";
                bb.contentTMP.fontSize = 3.5f;
                bb.contentTMP.color = FreeColor;
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < players.Count; i++)
                {
                    var (name, dist) = players[i];
                    string distText = dist >= 1000f ? $"{dist / 1000f:F1}km" : $"{dist:F0}m";
                    sb.AppendLine($"POS {i + 1}: {name} - {distText}");
                }
                if (players.Count > 10)
                    sb.AppendLine($"... +{players.Count - 10} more");
                bb.contentTMP.text = sb.ToString().TrimEnd();
                bb.contentTMP.fontSize = 3f;
                bb.contentTMP.color = PlayerColor;
            }
        }


        public static void RefreshBillboards()
        {
            try
            {
                if (!StarTruckClient.client.IsConnected) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                if (sector == lastSector) return;
                lastSector = sector;

                ClearBillboards();

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null)
                {
                    StarTruckMP.Log.LogWarning("WarpGateBillboard: no 3D TextMeshPro source found, skipping.");
                    return;
                }

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

                    // Billboard stays visible at all distances — WorldSpace Canvas
                    // handles its own visibility via camera clipping.

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
