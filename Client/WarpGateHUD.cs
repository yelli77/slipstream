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
    /// HUD-Overlay das an jedem WarpGate im Sektor anzeigt, welcher Spieler
    /// als naechster springen darf (naechster Spieler im 2km Umkreis).
    /// Zeigt: Gate-Name + Spielername + Entfernung.
    /// </summary>
    public static class WarpGateHUD
    {
        private static GameObject hudCanvas = null;
        private static Camera gameCam = null;
        private static List<WarpGateMarker> markers = new List<WarpGateMarker>();
        private static string lastSector = "none";
        private static float nextRefreshTime = 0f;
        private static float nextUpdateTime = 0f;
        private static readonly float RefreshInterval = 3f;
        private static readonly float UpdateInterval = 0.2f; // 5 Hz text updates

        // 2km range for player eligibility
        private static readonly float MaxPlayerDistance = 2000f;
        // Hide gate marker when camera >5km from gate
        private static readonly float MaxCameraDistance = 5000f;

        // Colors
        private static readonly Color GateReadyColor = new Color(0f, 1f, 0.5f, 0.95f);    // green
        private static readonly Color GateNoPlayerColor = new Color(1f, 1f, 0.3f, 0.7f);    // yellow faded
        private static readonly Color PlayerTextColor = new Color(0.2f, 1f, 0.8f, 1f);      // cyan-green
        private static readonly Color OffscreenColor = new Color(1f, 0.6f, 0f, 0.9f);       // orange

        private class WarpGateMarker
        {
            public WarpTriggerZone gateZone;
            public GameObject rootObj;
            public UnityEngine.UI.Image dotImg;
            public TMPro.TextMeshProUGUI gateNameLabel;
            public TMPro.TextMeshProUGUI playerLabel;
            public TMPro.TextMeshProUGUI distLabel;
            public string displayName;
            public float lastDistToCamera;
        }

        // Reflection cache for gate name resolution
        private static FieldInfo fi_entryGateName = null;
        private static FieldInfo fi_jumpgateName = null;
        private static bool gateNameReflectionSearched = false;

        private static Sprite cachedCircleSprite = null;
        private static Sprite GetCircleSprite()
        {
            if (cachedCircleSprite != null) return cachedCircleSprite;
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size / 2f;
            float radius = size / 2f - 1f;
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    tex.SetPixel(x, y, dist <= radius ? Color.white : new Color(0, 0, 0, 0));
                }
            }
            tex.Apply();
            cachedCircleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return cachedCircleSprite;
        }

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

        private static void EnsureHUDCanvas()
        {
            if (hudCanvas != null && hudCanvas.activeInHierarchy) return;

            var sourceTMP = FindSourceTMP();
            if (sourceTMP == null)
            {
                StarTruckMP.Log.LogWarning("WarpGateHUD: no source TMP found, skipping.");
                return;
            }

            hudCanvas = new GameObject("StarTruckMP_WarpGateHUD");
            UnityEngine.Object.DontDestroyOnLoad(hudCanvas);
            var canvas = hudCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1001; // above DockingBayHUD (1000)
            hudCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();

            StarTruckMP.Log.LogInfo("WarpGateHUD: Canvas created.");
        }

        private static void ClearMarkers()
        {
            foreach (var m in markers)
            {
                if (m.rootObj != null)
                    UnityEngine.Object.Destroy(m.rootObj);
            }
            markers.Clear();
        }

        /// <summary>
        /// Resolves a display name for a WarpTriggerZone gate.
        /// Tries WarpGate component fields, then jumpgateName, then gameObject.name.
        /// </summary>
        private static string GetGateName(WarpTriggerZone zone)
        {
            if (zone == null) return "Gate ???";

            // Try to find WarpGate component on same object or parent
            WarpGate gate = null;
            try { gate = zone.GetComponent<WarpGate>(); } catch { }
            if (gate == null)
            {
                try { gate = zone.GetComponentInParent<WarpGate>(); } catch { }
            }

            // Try reflection to read entryGateName / jumpgateName
            if (gate != null)
            {
                try
                {
                    if (!gateNameReflectionSearched)
                    {
                        gateNameReflectionSearched = true;
                        var gateType = gate.GetType();
                        fi_entryGateName = gateType.GetField("entryGateName",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        fi_jumpgateName = gateType.GetField("jumpgateName",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    if (fi_entryGateName != null)
                    {
                        var val = fi_entryGateName.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }
                    if (fi_jumpgateName != null)
                    {
                        var val = fi_jumpgateName.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }

                    // Try properties too (IL2CPP may expose fields as properties)
                    var prop = gate.GetType().GetProperty("entryGateName",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var val = prop.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }
                    var prop2 = gate.GetType().GetProperty("jumpgateName",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (prop2 != null)
                    {
                        var val = prop2.GetValue(gate) as string;
                        if (!string.IsNullOrEmpty(val)) return val.Trim();
                    }
                }
                catch { }
            }

            // Fallback: use gameObject name, clean it up
            string raw = zone.gameObject?.name ?? "Gate";
            int cloneIdx = raw.IndexOf("(Clone)");
            if (cloneIdx > 0) raw = raw.Substring(0, cloneIdx).Trim();
            return raw;
        }

        /// <summary>
        /// Finds the nearest eligible player (remote + local) to a gate position.
        /// Returns null if no player within MaxPlayerDistance.
        /// </summary>
        private static (string name, float distance)? FindNearestPlayer(Vector3 gateWorldPos)
        {
            float minDist = float.MaxValue;
            string nearestName = null;

            // Check remote players
            if (StarTruckClient.playerList != null)
            {
                foreach (var kv in StarTruckClient.playerList)
                {
                    if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                    if (string.IsNullOrEmpty(kv.Value.sector) || kv.Value.sector == "none") continue;

                    Vector3 playerPos = new Vector3(
                        kv.Value.truckTrans.Pos.x,
                        kv.Value.truckTrans.Pos.y,
                        kv.Value.truckTrans.Pos.z
                    );
                    // Floating origin correction
                    if (StarTruckClient.floatingOrigin != null)
                        playerPos += StarTruckClient.floatingOrigin.m_currentOrigin;

                    float dist = Vector3.Distance(gateWorldPos, playerPos);
                    if (dist < minDist && dist <= MaxPlayerDistance)
                    {
                        minDist = dist;
                        nearestName = kv.Value.Name;
                    }
                }
            }

            // Check local player
            if (StarTruckClient.myTruck != null)
            {
                Vector3 localPos = StarTruckClient.myTruck.transform.position;
                float dist = Vector3.Distance(gateWorldPos, localPos);
                if (dist < minDist && dist <= MaxPlayerDistance)
                {
                    minDist = dist;
                    nearestName = "(Du)";
                }
            }

            if (nearestName == null) return null;
            return (nearestName, minDist);
        }

        public static void RefreshGates()
        {
            try
            {
                if (!StarTruckClient.client.IsConnected) { StarTruckMP.Log.LogInfo("WarpGateHUD: not connected, skipping."); return; }

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") { StarTruckMP.Log.LogInfo($"WarpGateHUD: sector is null/empty/none (val={sector}), skipping."); return; }

                // Only refresh if sector changed or timer expired
                if (sector == lastSector && Time.realtimeSinceStartup < nextRefreshTime) return;
                lastSector = sector;
                nextRefreshTime = Time.realtimeSinceStartup + RefreshInterval;

                EnsureHUDCanvas();
                if (hudCanvas == null) { StarTruckMP.Log.LogWarning("WarpGateHUD: hudCanvas still null after EnsureHUDCanvas, skipping."); return; }

                if (gameCam == null)
                    gameCam = StarTruckClient.playerCam?.GetComponent<Camera>();
                if (gameCam == null) { StarTruckMP.Log.LogWarning("WarpGateHUD: gameCam is null (playerCam not available), skipping."); return; }

                ClearMarkers();

                // Find all WarpTriggerZone objects in scene
                WarpTriggerZone[] allGates;
                try
                {
                    allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>();
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"WarpGateHUD: FindObjectsOfType<WarpTriggerZone> failed: {ex.Message}");
                    return;
                }

                if (allGates == null || allGates.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("WarpGateHUD: no WarpTriggerZone objects found in scene.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"WarpGateHUD: found {allGates.Length} WarpTriggerZone(s) in '{sector}'");

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null) return;

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;
                    try
                    {
                        CreateMarker(zone, sourceTMP);
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"WarpGateHUD: marker creation failed for '{zone.gameObject.name}': {ex.Message}");
                    }
                }

                StarTruckMP.Log.LogInfo($"WarpGateHUD: {markers.Count} gate marker(s) created.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"WarpGateHUD.RefreshGates error: {ex}");
            }
        }

        private static void CreateMarker(WarpTriggerZone zone, TMPro.TextMeshProUGUI sourceTMP)
        {
            string gateName = GetGateName(zone);

            // Root object
            GameObject root = new GameObject($"GateMarker_{gateName}");
            root.transform.SetParent(hudCanvas.transform, false);
            var rootRT = root.AddComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(200f, 70f);

            // Dot (small circle indicator)
            GameObject dot = new GameObject("Dot");
            dot.transform.SetParent(root.transform, false);
            var dotRT = dot.AddComponent<RectTransform>();
            dotRT.anchorMin = new Vector2(0.5f, 0.5f);
            dotRT.anchorMax = new Vector2(0.5f, 0.5f);
            dotRT.anchoredPosition = new Vector2(0f, 0f);
            dotRT.sizeDelta = new Vector2(18f, 18f);

            var dotImg = dot.AddComponent<UnityEngine.UI.Image>();
            dotImg.sprite = GetCircleSprite();
            dotImg.color = GateReadyColor;
            dotImg.type = UnityEngine.UI.Image.Type.Simple;
            dotImg.preserveAspect = true;
            dotImg.raycastTarget = false;

            // Gate name label (above dot)
            GameObject nameObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            nameObj.name = "GateNameLabel";
            var nameRT = nameObj.GetComponent<RectTransform>();
            if (nameRT != null)
            {
                nameRT.anchorMin = new Vector2(0.5f, 0.5f);
                nameRT.anchorMax = new Vector2(0.5f, 0.5f);
                nameRT.anchoredPosition = new Vector2(0f, 22f);
                nameRT.sizeDelta = new Vector2(220f, 20f);
                nameRT.localScale = Vector3.one;
            }
            var nameLabel = nameObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (nameLabel != null)
            {
                nameLabel.text = $"Gate: {gateName}";
                nameLabel.fontSize = 11;
                nameLabel.color = Color.white;
                nameLabel.alignment = TMPro.TextAlignmentOptions.Center;
                nameLabel.raycastTarget = false;
            }

            // Player label (below dot)
            GameObject playerObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            playerObj.name = "PlayerLabel";
            var playerRT = playerObj.GetComponent<RectTransform>();
            if (playerRT != null)
            {
                playerRT.anchorMin = new Vector2(0.5f, 0.5f);
                playerRT.anchorMax = new Vector2(0.5f, 0.5f);
                playerRT.anchoredPosition = new Vector2(0f, -8f);
                playerRT.sizeDelta = new Vector2(220f, 20f);
                playerRT.localScale = Vector3.one;
            }
            var playerLabel = playerObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (playerLabel != null)
            {
                playerLabel.text = "— Kein Spieler in Reichweite —";
                playerLabel.fontSize = 10;
                playerLabel.color = GateNoPlayerColor;
                playerLabel.alignment = TMPro.TextAlignmentOptions.Center;
                playerLabel.raycastTarget = false;
            }

            // Distance label (below player label)
            GameObject distObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            distObj.name = "DistLabel";
            var distRT = distObj.GetComponent<RectTransform>();
            if (distRT != null)
            {
                distRT.anchorMin = new Vector2(0.5f, 0.5f);
                distRT.anchorMax = new Vector2(0.5f, 0.5f);
                distRT.anchoredPosition = new Vector2(0f, -24f);
                distRT.sizeDelta = new Vector2(220f, 18f);
                distRT.localScale = Vector3.one;
            }
            var distLabel = distObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (distLabel != null)
            {
                distLabel.text = "";
                distLabel.fontSize = 9;
                distLabel.color = new Color(0.7f, 0.9f, 1f, 0.8f);
                distLabel.alignment = TMPro.TextAlignmentOptions.Center;
                distLabel.raycastTarget = false;
            }

            markers.Add(new WarpGateMarker
            {
                gateZone = zone,
                rootObj = root,
                dotImg = dotImg,
                gateNameLabel = nameLabel,
                playerLabel = playerLabel,
                distLabel = distLabel,
                displayName = gateName,
                lastDistToCamera = 0f
            });
        }

        public static void UpdatePositions()
        {
            if (markers.Count == 0) return;
            if (gameCam == null || gameCam == null)
                gameCam = StarTruckClient.playerCam?.GetComponent<Camera>();
            if (gameCam == null) return;

            Vector3 camPos = gameCam.transform.position;
            float screenW = Screen.width;
            float screenH = Screen.height;

            // Text updates throttled to 5 Hz
            bool doTextUpdate = Time.realtimeSinceStartup >= nextUpdateTime;
            if (doTextUpdate) nextUpdateTime = Time.realtimeSinceStartup + UpdateInterval;

            foreach (var m in markers)
            {
                if (m.gateZone == null || m.gateZone.gameObject == null || m.rootObj == null) continue;

                try
                {
                    // Gate world position
                    Vector3 gateWorldPos = m.gateZone.transform.position;
                    float distToCamera = Vector3.Distance(camPos, gateWorldPos);
                    m.lastDistToCamera = distToCamera;

                    // Hide if camera too far
                    if (distToCamera > MaxCameraDistance)
                    {
                        if (m.rootObj.activeSelf) m.rootObj.SetActive(false);
                        continue;
                    }

                    // Text update (throttled)
                    if (doTextUpdate)
                    {
                        var nearest = FindNearestPlayer(gateWorldPos);

                        if (nearest.HasValue)
                        {
                            string distText;
                            if (nearest.Value.distance >= 1000f)
                                distText = $"{nearest.Value.distance / 1000f:F1} km";
                            else
                                distText = $"{nearest.Value.distance:F0} m";

                            if (m.playerLabel != null)
                            {
                                m.playerLabel.text = $"► {nearest.Value.name}";
                                m.playerLabel.color = PlayerTextColor;
                            }
                            if (m.distLabel != null)
                                m.distLabel.text = distText;
                            if (m.dotImg != null)
                                m.dotImg.color = GateReadyColor;
                        }
                        else
                        {
                            if (m.playerLabel != null)
                            {
                                m.playerLabel.text = "— Kein Spieler in Reichweite —";
                                m.playerLabel.color = GateNoPlayerColor;
                            }
                            if (m.distLabel != null)
                                m.distLabel.text = "";
                            if (m.dotImg != null)
                                m.dotImg.color = GateNoPlayerColor;
                        }
                    }

                    // Project to screen
                    Vector3 screenPos3 = gameCam.WorldToScreenPoint(gateWorldPos);
                    if (screenPos3.z < 0f || screenPos3.z < 500f)
                    {
                        if (m.rootObj.activeSelf) m.rootObj.SetActive(false);
                        continue;
                    }

                    if (!m.rootObj.activeSelf) m.rootObj.SetActive(true);

                    // Clamp to screen edges
                    float margin = 60f;
                    float clampedX = Mathf.Clamp(screenPos3.x, margin, screenW - margin);
                    float clampedY = Mathf.Clamp(screenPos3.y, margin, screenH - margin);

                    bool isOnScreen = screenPos3.x >= 0 && screenPos3.x <= screenW &&
                                      screenPos3.y >= 0 && screenPos3.y <= screenH;

                    var rt = m.rootObj.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.anchoredPosition = new Vector2(clampedX - screenW / 2f, clampedY - screenH / 2f);
                    }

                    // Dim off-screen markers
                    if (!isOnScreen)
                    {
                        if (m.dotImg != null) m.dotImg.color = OffscreenColor;
                        if (m.gateNameLabel != null) m.gateNameLabel.color = new Color(1f, 0.8f, 0.5f, 0.7f);
                    }
                    else
                    {
                        if (m.gateNameLabel != null) m.gateNameLabel.color = Color.white;
                    }
                }
                catch { }
            }
        }

        public static void OnSectorChanged()
        {
            lastSector = "none"; // Force refresh
            ClearMarkers();
            RefreshGates();
        }

        public static void Cleanup()
        {
            ClearMarkers();
            lastSector = "none";
            if (hudCanvas != null)
            {
                UnityEngine.Object.Destroy(hudCanvas);
                hudCanvas = null;
            }
            gameCam = null;
        }
    }
}
