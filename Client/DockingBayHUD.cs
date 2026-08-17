using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// HUD-Overlay das beim Betreten eines Sektors alle Docking Bays im System
    /// als Markierungen auf dem Bildschirm anzeigt — unabhaengig davon, ob der
    /// Scanner aktiv ist oder wie weit entfernt die Docks sind.
    ///
    /// Markierungen: farbiger Punkt + Name + Distanz in km.
    /// Off-Screen-Marker werden am Viewport-Rand geklemmt mit Richtungspfeil.
    /// </summary>
    public static class DockingBayHUD
    {
        private static GameObject hudCanvas = null;
        private static Camera gameCam = null;
        private static List<DockingBayMarker> markers = new List<DockingBayMarker>();
        private static string lastSector = "none";
        private static float nextRefreshTime = 0f;
        private static float nextUpdateTime = 0f;
        private static readonly float RefreshInterval = 3f;   // DockingBays neu suchen
        private static readonly float UpdateInterval = 0.05f; // 20 Hz Position-Update

        // Marker colors
        private static readonly Color DockingColor = new Color(0.2f, 0.8f, 1f, 0.95f); // cyan
        private static readonly Color DockingColorFar = new Color(0.2f, 0.8f, 1f, 0.5f); // faded cyan
        private static readonly Color OffscreenEdgeColor = new Color(1f, 0.6f, 0f, 0.9f); // orange

        private class DockingBayMarker
        {
            public DockingBay bay;
            public GameObject rootObj;
            public UnityEngine.UI.Image dotImg;
            public TMPro.TextMeshProUGUI nameLabel;
            public TMPro.TextMeshProUGUI distLabel;
            public string bayName;
        }

        // Reflection cache for DockingBay.m_dockingBayId
        private static FieldInfo fi_dockingBayId = null;
        private static bool reflectionSearched = false;

        private static string GetBayName(DockingBay bay)
        {
            try
            {
                // Try m_dockingBayId via reflection
                if (!reflectionSearched)
                {
                    reflectionSearched = true;
                    fi_dockingBayId = bay.GetType().GetField("m_dockingBayId",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (fi_dockingBayId != null)
                {
                    var val = fi_dockingBayId.GetValue(bay) as string;
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            catch { }

            // Fallback: child TextMeshPro or object name
            try
            {
                var tmps = bay.GetComponentsInChildren<TMPro.TextMeshPro>();
                if (tmps != null)
                {
                    foreach (var tmp in tmps)
                    {
                        if (tmp != null && !string.IsNullOrEmpty(tmp.text))
                            return tmp.text;
                    }
                }
            }
            catch { }

            return bay.gameObject.name;
        }

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
                StarTruckMP.Log.LogWarning("DockingBayHUD: no source TMP found, skipping.");
                return;
            }

            hudCanvas = new GameObject("StarTruckMP_DockingBayHUD");
            UnityEngine.Object.DontDestroyOnLoad(hudCanvas);
            var canvas = hudCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            hudCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();

            StarTruckMP.Log.LogInfo("DockingBayHUD: Canvas created.");
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

        public static void RefreshDockingBays()
        {
            try
            {
                if (!StarTruckClient.client.IsConnected) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                // Only refresh if sector changed or timer expired
                if (sector == lastSector && Time.realtimeSinceStartup < nextRefreshTime) return;
                lastSector = sector;
                nextRefreshTime = Time.realtimeSinceStartup + RefreshInterval;

                EnsureHUDCanvas();
                if (hudCanvas == null) return;

                // Find camera
                if (gameCam == null || gameCam == null)
                    gameCam = StarTruckClient.playerCam?.GetComponent<Camera>();
                if (gameCam == null) return;

                ClearMarkers();

                // Find all DockingBay objects in scene
                var allBays = UnityEngine.Object.FindObjectsOfType<DockingBay>();
                if (allBays == null || allBays.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("DockingBayHUD: no DockingBay objects found in scene.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"DockingBayHUD: found {allBays.Length} DockingBay objects in '{sector}'");

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null) return;

                foreach (var bay in allBays)
                {
                    if (bay == null || bay.gameObject == null) continue;
                    try
                    {
                        CreateMarker(bay, sourceTMP);
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"DockingBayHUD: marker creation failed for '{bay.gameObject.name}': {ex.Message}");
                    }
                }

                StarTruckMP.Log.LogInfo($"DockingBayHUD: {markers.Count} markers created.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"DockingBayHUD.RefreshDockingBays error: {ex}");
            }
        }

        private static void CreateMarker(DockingBay bay, TMPro.TextMeshProUGUI sourceTMP)
        {
            string bayName = GetBayName(bay);

            // Root object
            GameObject root = new GameObject($"DockMarker_{bayName}");
            root.transform.SetParent(hudCanvas.transform, false);
            var rootRT = root.AddComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(200f, 80f);

            // Dot
            GameObject dot = new GameObject("Dot");
            dot.transform.SetParent(root.transform, false);
            var dotRT = dot.AddComponent<RectTransform>();
            dotRT.anchorMin = new Vector2(0.5f, 0.5f);
            dotRT.anchorMax = new Vector2(0.5f, 0.5f);
            dotRT.anchoredPosition = new Vector2(0f, 0f);
            dotRT.sizeDelta = new Vector2(30f, 30f);

            var dotImg = dot.AddComponent<UnityEngine.UI.Image>();
            dotImg.sprite = GetCircleSprite();
            dotImg.color = DockingColor;
            dotImg.type = UnityEngine.UI.Image.Type.Simple;
            dotImg.preserveAspect = true;
            dotImg.raycastTarget = false;

            // Name label
            GameObject nameObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            nameObj.name = "NameLabel";
            var nameRT = nameObj.GetComponent<RectTransform>();
            if (nameRT != null)
            {
                nameRT.anchorMin = new Vector2(0.5f, 0.5f);
                nameRT.anchorMax = new Vector2(0.5f, 0.5f);
                nameRT.anchoredPosition = new Vector2(0f, 25f);
                nameRT.sizeDelta = new Vector2(250f, 30f);
                nameRT.localScale = Vector3.one;
            }
            var nameLabel = nameObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (nameLabel != null)
            {
                nameLabel.text = bayName;
                nameLabel.fontSize = 16;
                nameLabel.color = Color.white;
                nameLabel.alignment = TMPro.TextAlignmentOptions.Center;
                nameLabel.raycastTarget = false;
            }

            // Distance label
            GameObject distObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
            distObj.name = "DistLabel";
            var distRT = distObj.GetComponent<RectTransform>();
            if (distRT != null)
            {
                distRT.anchorMin = new Vector2(0.5f, 0.5f);
                distRT.anchorMax = new Vector2(0.5f, 0.5f);
                distRT.anchoredPosition = new Vector2(0f, -25f);
                distRT.sizeDelta = new Vector2(250f, 30f);
                distRT.localScale = Vector3.one;
            }
            var distLabel = distObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (distLabel != null)
            {
                distLabel.text = "...";
                distLabel.fontSize = 14;
                distLabel.color = new Color(0.7f, 0.9f, 1f, 0.9f);
                distLabel.alignment = TMPro.TextAlignmentOptions.Center;
                distLabel.raycastTarget = false;
            }

            markers.Add(new DockingBayMarker
            {
                bay = bay,
                rootObj = root,
                dotImg = dotImg,
                nameLabel = nameLabel,
                distLabel = distLabel,
                bayName = bayName
            });
        }

        public static void UpdatePositions()
        {
            if (markers.Count == 0) return;
            if (gameCam == null || gameCam == null)
                gameCam = StarTruckClient.playerCam?.GetComponent<Camera>();
            if (gameCam == null) return;
            

            if (Time.realtimeSinceStartup < nextUpdateTime) return;
            nextUpdateTime = Time.realtimeSinceStartup + UpdateInterval;

                        Vector3 camPos = gameCam.transform.position;
            Vector3 camForward = gameCam.transform.forward;
            float screenW = Screen.width;
            float screenH = Screen.height;

            foreach (var m in markers)
            {
                if (m.bay == null || m.bay.gameObject == null || m.rootObj == null) continue;

                try
                {
                    // World position accounting for floating origin
                    Vector3 bayWorldPos = m.bay.transform.position;
                    float distance = Vector3.Distance(camPos, bayWorldPos);

                    // Format distance
                    string distText;
                    if (distance >= 1000f)
                        distText = $"{distance / 1000f:F1} km";
                    else
                        distText = $"{distance:F0} m";

                    if (m.distLabel != null)
                        m.distLabel.text = distText;

                    // Fade based on distance
                    if (m.dotImg != null)
                    {
                        float t = Mathf.Clamp01((distance - 2000f) / 18000f); // 2-20km fade
                        m.dotImg.color = Color.Lerp(DockingColor, DockingColorFar, t);
                    }

                    // Project to screen
                    Vector3 screenPos3 = gameCam.WorldToScreenPoint(bayWorldPos);
                    StarTruckMP.Log.LogInfo($"DockBayHUD DEBUG: camPos={camPos} bayPos={m.bay.transform.position} screenPos={screenPos3}");
                    if (screenPos3.z < 0)
                    {
                        // Behind camera — hide
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
                        if (m.dotImg != null) m.dotImg.color = OffscreenEdgeColor;
                        if (m.nameLabel != null) m.nameLabel.color = new Color(1f, 0.8f, 0.5f, 0.7f);
                    }
                    else
                    {
                        if (m.nameLabel != null) m.nameLabel.color = Color.white;
                    }
                }
                catch { }
            }
        }

        public static void OnSectorChanged()
        {
            lastSector = "none"; // Force refresh on next frame
            ClearMarkers();
            RefreshDockingBays();
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
