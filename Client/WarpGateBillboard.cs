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
        private static readonly float MaxVisibleDistance = 5000f;

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

                Vector3 dir = transform.position - mainCam.transform.position;
                if (dir.sqrMagnitude > 0.01f)
                {
                    transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                }
            }
        }

        private class GateBillboard
        {
            public WarpTriggerZone gateZone;
            public string gateId;
            public string displayName;
            public GameObject rootObj;
            public TMPro.TextMeshProUGUI nameLabel;
            public TMPro.TextMeshProUGUI separator;
            public TMPro.TextMeshProUGUI contentLabel;
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
        private static TMPro.TextMeshProUGUI FindSourceTMP()
        {
            var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
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
        private static void HardenClonedLabel(GameObject obj, TMPro.TextMeshProUGUI label, TMPro.TextMeshProUGUI sourceTMP)
        {
            if (obj == null || label == null) return;

            // Make sure the clone itself (and everything up to our root) is active —
            // Instantiate() preserves the source's active state, so if sourceTMP was
            // ever found on a currently-inactive object this clone would silently stay off.
            if (!obj.activeSelf) obj.SetActive(true);

            // Autosizing recalculates fontSize on its own; if it survived the clone it
            // will silently override the explicit fontSize set right after this call and
            // can collapse the text to ~0pt in our much larger canvas. Force it off.
            label.enableAutoSizing = false;
            label.overflowMode = TMPro.TextOverflowModes.Overflow;
            label.enabled = true;

            // Re-bind font + material explicitly rather than trusting the cloned
            // reference — IL2CPP interop clones have been observed losing/blanking the
            // shared material reference on TMP components.
            if (sourceTMP != null)
            {
                if (sourceTMP.font != null) label.font = sourceTMP.font;
                if (sourceTMP.fontSharedMaterial != null) label.fontSharedMaterial = sourceTMP.fontSharedMaterial;
            }

            label.alpha = 1f;
            var cr = obj.GetComponent<CanvasRenderer>();
            if (cr != null) cr.SetAlpha(1f);
            var cg = obj.GetComponent<CanvasGroup>();
            if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; cg.interactable = true; }
        }

        /// <summary>
        /// Creates a world-space billboard for a single gate using Canvas + TMPUGUI.
        /// </summary>
        private static void CreateBillboard(WarpTriggerZone zone, TMPro.TextMeshProUGUI sourceTMP)
        {
            string gateId = GetGateId(zone);
            string gateName = GetGateName(zone);

            // Root object with WorldSpace Canvas
            GameObject root = new GameObject($"Billboard_{gateName}");
            root.transform.SetParent(null);

            // Position: beside the approach corridor (like a real airport/highway board),
            // not directly on the gate-to-player line. A billboard sitting exactly on that
            // line gets driven straight through - the camera clips inside the paper-thin
            // WorldSpace canvas plane at close range and it visually "disappears". Offsetting
            // it sideways + up keeps it out of the flight path while staying readable as the
            // truck passes.
            Vector3 towardPlayer = (StarTruckClient.myTruck != null)
                ? (StarTruckClient.myTruck.transform.position - zone.transform.position)
                : zone.transform.forward * -1f;
            if (towardPlayer.sqrMagnitude < 0.01f) towardPlayer = zone.transform.forward * -1f;
            towardPlayer.Normalize();

            Vector3 sideAxis = Vector3.Cross(Vector3.up, towardPlayer);
            if (sideAxis.sqrMagnitude < 0.01f) sideAxis = zone.transform.right;
            sideAxis.Normalize();

            Vector3 corridorPoint = zone.transform.position + towardPlayer * BillboardDistance;
            root.transform.position = corridorPoint + sideAxis * SideOffset + Vector3.up * HeightOffset;
            var cam = Camera.main;
            float camDist = cam != null ? Vector3.Distance(root.transform.position, cam.transform.position) : -1f;
            StarTruckMP.Log.LogInfo($"WarpGateBillboard: placed '{gateName}' at {root.transform.position}, camDist={camDist:F0}m, gatePos={zone.transform.position}");

            // WorldSpace Canvas (must be BEFORE BillboardBehavior so GetComponent works in Awake)
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 500;
            root.AddComponent<UnityEngine.UI.CanvasScaler>();
            root.AddComponent<BillboardBehavior>();

            // Canvas RectTransform sizing: 4500x7500 canvas units, scaled to 0.01 = 45m x 75m world
            RectTransform canvasRT = root.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(3500f, 6000f);
            root.transform.localScale = new Vector3(0.01f, 0.01f, 0.01f);

            // Background Image (not Quad)
            GameObject bg = new GameObject("BG");
            bg.transform.SetParent(root.transform, false);
            var bgRT = bg.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero;
            bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            bgRT.sizeDelta = new Vector2(3500f, 6000f);
            var bgImg = bg.AddComponent<UnityEngine.UI.Image>();
            bgImg.color = BgColor;
            bgImg.raycastTarget = false;

            // --- Name Label (clone from sourceTMP) ---
            GameObject nameObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            nameObj.name = "NameLabel";
            var nameRT = nameObj.GetComponent<RectTransform>();
            if (nameRT != null)
            {
                nameRT.anchorMin = new Vector2(0.5f, 0.5f);
                nameRT.anchorMax = new Vector2(0.5f, 0.5f);
                nameRT.anchoredPosition = new Vector2(0f, 1800f);
                nameRT.sizeDelta = new Vector2(3400f, 1000f);
                nameRT.localScale = Vector3.one;
            }
            var nameLabel = nameObj.GetComponent<TMPro.TextMeshProUGUI>();
            HardenClonedLabel(nameObj, nameLabel, sourceTMP);
            if (nameLabel != null)
            {
                nameLabel.text = $"EXIT GATE: {gateName}";
                nameLabel.fontSize = 520f;
                nameLabel.color = GateNameColor;
                nameLabel.alignment = TMPro.TextAlignmentOptions.Left;
                nameLabel.raycastTarget = false;
                if (nameLabel.font == null && sourceTMP.font != null)
                    nameLabel.font = sourceTMP.font;
                nameLabel.ForceMeshUpdate();
            }

            // --- Separator ---
            GameObject sepObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            sepObj.name = "Separator";
            var sepRT = sepObj.GetComponent<RectTransform>();
            if (sepRT != null)
            {
                sepRT.anchorMin = new Vector2(0.5f, 0.5f);
                sepRT.anchorMax = new Vector2(0.5f, 0.5f);
                sepRT.anchoredPosition = new Vector2(0f, 1100f);
                sepRT.sizeDelta = new Vector2(3400f, 400f);
                sepRT.localScale = Vector3.one;
            }
            var sepTMP = sepObj.GetComponent<TMPro.TextMeshProUGUI>();
            HardenClonedLabel(sepObj, sepTMP, sourceTMP);
            if (sepTMP != null)
            {
                sepTMP.text = "————————————";
                sepTMP.fontSize = 280f;
                sepTMP.color = SepColor;
                sepTMP.alignment = TMPro.TextAlignmentOptions.Left;
                sepTMP.raycastTarget = false;
                if (sepTMP.font == null && sourceTMP.font != null)
                    sepTMP.font = sourceTMP.font;
                sepTMP.ForceMeshUpdate();
            }

            // --- Content Label (player list or "FREE") ---
            GameObject contentObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            contentObj.name = "ContentLabel";
            var contentRT = contentObj.GetComponent<RectTransform>();
            if (contentRT != null)
            {
                contentRT.anchorMin = new Vector2(0.5f, 0.5f);
                contentRT.anchorMax = new Vector2(0.5f, 0.5f);
                contentRT.anchoredPosition = new Vector2(0f, -400f);
                contentRT.sizeDelta = new Vector2(3400f, 3600f);
                contentRT.localScale = Vector3.one;
            }
            var contentTMP = contentObj.GetComponent<TMPro.TextMeshProUGUI>();
            HardenClonedLabel(contentObj, contentTMP, sourceTMP);
            if (contentTMP != null)
            {
                contentTMP.text = "FREE";
                contentTMP.fontSize = 520f;
                contentTMP.color = FreeColor;
                contentTMP.alignment = TMPro.TextAlignmentOptions.Left;
                contentTMP.raycastTarget = false;
                if (contentTMP.font == null && sourceTMP.font != null)
                    contentTMP.font = sourceTMP.font;
                contentTMP.ForceMeshUpdate();
            }

            // Force an immediate layout/geometry rebuild instead of waiting an
            // indeterminate number of frames for IL2CPP's Canvas update loop.
            Canvas.ForceUpdateCanvases();

            if (!diagLogged)
            {
                diagLogged = true;
                try
                {
                    var crN = nameObj.GetComponent<CanvasRenderer>();
                    StarTruckMP.Log.LogInfo($"WarpGateBillboard DIAG: nameLabel active={nameLabel?.isActiveAndEnabled} alpha={nameLabel?.alpha} font={(nameLabel?.font != null)} mat={(nameLabel?.fontSharedMaterial != null)} canvasRendererAlpha={crN?.GetAlpha()} autoSize={nameLabel?.enableAutoSizing} fontSize={nameLabel?.fontSize}");
                }
                catch (Exception dex)
                {
                    StarTruckMP.Log.LogWarning($"WarpGateBillboard DIAG failed: {dex.Message}");
                }
            }

            billboards.Add(new GateBillboard
            {
                gateZone = zone,
                gateId = gateId,
                displayName = gateName,
                rootObj = root,
                nameLabel = nameLabel,
                separator = sepTMP,
                contentLabel = contentTMP
            });
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
            if (bb.contentLabel == null || bb.gateZone == null) return;

            Vector3 gateWorldPos = bb.gateZone.transform.position;
            var players = GetPlayersForGate(bb.gateId, gateWorldPos);

            if (players.Count == 0)
            {
                bb.contentLabel.text = "FREE";
                bb.contentLabel.fontSize = 260f;
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
                bb.contentLabel.fontSize = 600f;
                bb.contentLabel.color = PlayerColor;
            }

            bb.contentLabel.ForceMeshUpdate();
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
                    StarTruckMP.Log.LogWarning("WarpGateBillboard: no source TMPUGUI found, skipping.");
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
