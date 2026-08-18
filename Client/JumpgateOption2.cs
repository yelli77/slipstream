using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Option 2: Reuse the game's native SpeedTrap or CargoBay.bayIdSign sign objects
    /// for warp gate departure boards. Falls back to Canvas+TMP clone if native signs
    /// are not found or have unexpected types.
    /// </summary>
    public static class JumpgateOption2
    {
        // ─── State ───
        private static readonly Dictionary<string, GateBoard> boards = new();
        private static float lastUpdate = 0f;
        private static readonly float UpdateInterval = 1.5f;
        private static bool initialized = false;
        private static bool investigatedNative = false;
        private static bool nativeSignAvailable = false;

        // ─── Constants ───
        private const float SideOffset = 25f;     // meters to the side (offset from Option 1)
        private const float HeightOffset = 8f;
        private const float SignWidth = 2000f;
        private const float SignHeight = 3000f;
        private const float FontSizeValue = 180f;

        // ─── Internal types ───
        private struct PlayerEntry
        {
            public string playerName;
            public float distanceFromGate;
            public bool isLocal;
        }

        private class GateBoard
        {
            public string gateEntryId;
            public GameObject rootObject;
            public TMPro.TextMeshProUGUI tmpLabel;
            public float lastTextUpdate;
            public List<PlayerEntry> currentPlayerEntries;
            // For native sign fallback: store the text component reference
        }

        // ─── Reflection caches ───



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
        /// Investigate native sign objects (SpeedTrap.m_signText, CargoBay.bayIdSign).
        /// Logs what we find and sets nativeSignAvailable flag.
        /// </summary>
        private static void InvestigateNativeSigns()
        {
            if (investigatedNative) return;
            investigatedNative = true;

            try
            {
                // Try SpeedTrap first
                var speedTraps = UnityEngine.Object.FindObjectsOfType<SpeedTrap>();
                if (speedTraps != null && speedTraps.Length > 0)
                {
                    StarTruckMP.Log.LogInfo($"JumpgateOption2: Found {speedTraps.Length} SpeedTrap(s) in scene.");
                    var st = speedTraps[0];

                    // Read m_signText
                    var signTextField = st.GetType().GetField("m_signText",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (signTextField != null)
                    {
                        var signText = signTextField.GetValue(st);
                        StarTruckMP.Log.LogInfo($"JumpgateOption2: SpeedTrap.m_signText type={signText?.GetType()?.FullName ?? "null"}");
                        if (signText != null)
                        {
                            // Check if it's a TMP or TextMesh
                            var tmpComp = signText as TMPro.TextMeshProUGUI;
                            var tmComp = signText as TextMesh;
                            if (tmpComp != null)
                            {
                                StarTruckMP.Log.LogInfo($"JumpgateOption2: m_signText is TextMeshProUGUI — native sign viable.");
                                nativeSignAvailable = true;
                            }
                            else if (tmComp != null)
                            {
                                StarTruckMP.Log.LogInfo($"JumpgateOption2: m_signText is TextMesh — native sign viable.");
                                nativeSignAvailable = true;
                            }
                            else
                            {
                                StarTruckMP.Log.LogInfo($"JumpgateOption2: m_signText is {signText.GetType().Name} — not a text component, falling back.");
                            }
                        }
                    }
                    else
                    {
                        // Try as property
                        var signTextProp = st.GetType().GetProperty("m_signText",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (signTextProp != null)
                        {
                            var signText = signTextProp.GetValue(st);
                            StarTruckMP.Log.LogInfo($"JumpgateOption2: SpeedTrap.m_signText (prop) type={signText?.GetType()?.FullName ?? "null"}");
                            if (signText is TMPro.TextMeshProUGUI || signText is TextMesh)
                                nativeSignAvailable = true;
                        }
                    }
                }

                // Try CargoBay.bayIdSign
                if (!nativeSignAvailable)
                {
                    var cargoBays = UnityEngine.Object.FindObjectsOfType<CargoBay>();
                    if (cargoBays != null && cargoBays.Length > 0)
                    {
                        StarTruckMP.Log.LogInfo($"JumpgateOption2: Found {cargoBays.Length} CargoBay(s) in scene.");
                        var cb = cargoBays[0];

                        var bayIdSignField = cb.GetType().GetField("bayIdSign",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (bayIdSignField == null)
                            bayIdSignField = cb.GetType().GetField("m_bayIdSign",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        if (bayIdSignField != null)
                        {
                            var bayIdSign = bayIdSignField.GetValue(cb);
                            StarTruckMP.Log.LogInfo($"JumpgateOption2: CargoBay.bayIdSign type={bayIdSign?.GetType()?.FullName ?? "null"}");
                            if (bayIdSign is TMPro.TextMeshProUGUI || bayIdSign is TextMesh)
                            {
                                nativeSignAvailable = true;
                                StarTruckMP.Log.LogInfo("JumpgateOption2: bayIdSign is text component — native sign viable.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption2: InvestigateNativeSigns error: {ex.Message}");
            }

            if (!nativeSignAvailable)
            {
                StarTruckMP.Log.LogInfo("JumpgateOption2: No native sign text components found — will use Canvas+TMP fallback.");
            }
        }

        // ─── Gate/player logic (same as Option 1) ───

        private static List<PlayerEntry> CollectPlayersForGate(string entryGateId, Vector3 gateWorldPos)
        {
            var entries = new List<PlayerEntry>();

            try
            {
                foreach (var kv in StarTruckClient.playerList)
                {
                    var p = kv.Value;
                    if (p.destinationGateId != entryGateId) continue;
                    float dist = Vector3.Distance(gateWorldPos, p.truckTrans.Pos);
                    entries.Add(new PlayerEntry
                    {
                        playerName = p.Name ?? $"Player_{kv.Key}",
                        distanceFromGate = dist,
                        isLocal = false
                    });
                }
            }
            catch { }

            // Local player (via currentDestinationGateId OR proximity detection)
            try
            {
                string localApproaching = JumpgateUtils.DetectLocalPlayerApproachingGate();
                bool localGateMatch = (!string.IsNullOrEmpty(StarTruckClient.currentDestinationGateId)
                    && StarTruckClient.currentDestinationGateId == entryGateId)
                    || (!string.IsNullOrEmpty(localApproaching) && localApproaching == entryGateId);
                if (localGateMatch && StarTruckClient.myTruck != null)
                {
                    Vector3 myPos = StarTruckClient.floatingOrigin != null
                        ? StarTruckClient.floatingOrigin.m_currentOrigin + StarTruckClient.myTruck.transform.position
                        : StarTruckClient.myTruck.transform.position;
                    float dist = Vector3.Distance(gateWorldPos, myPos);
                    entries.Add(new PlayerEntry
                    {
                        playerName = StarTruckClient.myPlayerName ?? "Du",
                        distanceFromGate = dist,
                        isLocal = true
                    });
                }
            }
            catch { }

            entries.Sort((a, b) => a.distanceFromGate.CompareTo(b.distanceFromGate));
            return entries;
        }

        private static string BuildDepartureText(List<PlayerEntry> entries)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== DEPARTURE GATE ===");

            int pos = 1;
            foreach (var entry in entries)
            {
                string distText;
                if (entry.distanceFromGate < 1000f)
                    distText = $"{entry.distanceFromGate:F0} m";
                else
                    distText = $"{entry.distanceFromGate / 1000f:F1} km";

                if (entry.isLocal)
                    sb.AppendLine($"(Du) --- {distText}");
                else
                    sb.AppendLine($"POS {pos}. {entry.playerName} --- {distText}");

                pos++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Create a departure board next to a warp gate.
        /// Uses native SpeedTrap/CargoBay sign if available, otherwise Canvas+TMP fallback.
        /// </summary>
        private static GateBoard CreateBoardForGate(
            string entryGateId,
            Transform gateTransform,
            List<PlayerEntry> entries,
            TMPro.TextMeshProUGUI sourceTMP)
        {
            Vector3 gatePos = gateTransform.position;
            Vector3 approachDir = gateTransform.forward;
            if (approachDir.sqrMagnitude < 0.01f)
                approachDir = Vector3.forward;
            approachDir = approachDir.normalized;

            Vector3 sideDir = Vector3.Cross(Vector3.up, approachDir).normalized;
            if (sideDir.sqrMagnitude < 0.01f)
                sideDir = Vector3.Cross(Vector3.right, approachDir).normalized;

            Vector3 signPos = gatePos + sideDir * SideOffset + Vector3.up * HeightOffset;
            Quaternion fixedRot = Quaternion.LookRotation(-approachDir, Vector3.up);

            // Try native sign first
            if (nativeSignAvailable)
            {
                var nativeBoard = TryCreateNativeSign(entryGateId, signPos, fixedRot, entries);
                if (nativeBoard != null) return nativeBoard;
            }

            // Fallback: Canvas+TMP (same as Option 1)
            return CreateFallbackBoard(entryGateId, signPos, fixedRot, entries, sourceTMP);
        }

        private static GateBoard TryCreateNativeSign(
            string entryGateId, Vector3 signPos, Quaternion fixedRot, List<PlayerEntry> entries)
        {
            try
            {
                // Find a SpeedTrap or CargoBay to clone
                SpeedTrap templateST = null;
                var speedTraps = UnityEngine.Object.FindObjectsOfType<SpeedTrap>();
                if (speedTraps != null && speedTraps.Length > 0)
                    templateST = speedTraps[0];

                if (templateST != null)
                {
                    // Clone the SpeedTrap's sign parent (look for a parent with renderers)
                    GameObject signRoot = FindSignParent(templateST.gameObject);
                    if (signRoot == null) signRoot = templateST.gameObject;

                    GameObject clone = UnityEngine.Object.Instantiate(signRoot);
                    clone.name = $"DepartureBoard_Native_{entryGateId}";

                    // Strip SpeedTrap component
                    var stComp = clone.GetComponent<SpeedTrap>();
                    if (stComp != null) UnityEngine.Object.Destroy(stComp);

                    // Strip detection colliders
                    foreach (var col in clone.GetComponentsInChildren<Collider>())
                        UnityEngine.Object.Destroy(col);

                    // Position
                    clone.transform.position = signPos;
                    clone.transform.rotation = fixedRot;

                    // Find text component on clone
                    TMPro.TextMeshProUGUI tmpLabel = null;
                    var allTMP = clone.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                    if (allTMP != null && allTMP.Length > 0)
                        tmpLabel = allTMP[0];

                    if (tmpLabel == null)
                    {
                        UnityEngine.Object.Destroy(clone);
                        return null;
                    }

                    // Fix IL2CPP clone state
                    try { tmpLabel.enableAutoSizing = false; } catch { }
                    tmpLabel.fontSize = FontSizeValue;
                    tmpLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
                    tmpLabel.color = new Color(0.2f, 1f, 0.4f, 1f);
                    tmpLabel.raycastTarget = false;
                    tmpLabel.text = BuildDepartureText(entries);
                    clone.SetActive(true);
                    try { tmpLabel.ForceMeshUpdate(); } catch { }

                    StarTruckMP.Log.LogInfo($"JumpgateOption2: native SpeedTrap sign cloned for gate '{entryGateId}'.");

                    return new GateBoard
                    {
                        gateEntryId = entryGateId,
                        rootObject = clone,
                        tmpLabel = tmpLabel,
                        lastTextUpdate = Time.realtimeSinceStartup,
                        currentPlayerEntries = new List<PlayerEntry>(entries)
                    };
                }

                // Try CargoBay
                CargoBay templateCB = null;
                var cargoBays = UnityEngine.Object.FindObjectsOfType<CargoBay>();
                if (cargoBays != null && cargoBays.Length > 0)
                    templateCB = cargoBays[0];

                if (templateCB != null)
                {
                    var bayIdSignField = templateCB.GetType().GetField("bayIdSign",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (bayIdSignField == null)
                        bayIdSignField = templateCB.GetType().GetField("m_bayIdSign",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (bayIdSignField != null)
                    {
                        var signTextObj = bayIdSignField.GetValue(templateCB) as UnityEngine.Object;
                        if (signTextObj != null)
                        {
                            // Find the root of the sign text
                            var signGO = signTextObj as GameObject;
                            if (signGO == null)
                            {
                                var comp = signTextObj as Component;
                                if (comp != null) signGO = comp.gameObject;
                            }
                            if (signGO != null)
                            {
                                GameObject signParent = FindSignParent(signGO);
                                if (signParent == null) signParent = signGO;
                                GameObject clone = UnityEngine.Object.Instantiate(signParent);
                                clone.name = $"DepartureBoard_CargoBay_{entryGateId}";
                                clone.transform.position = signPos;
                                clone.transform.rotation = fixedRot;

                                TMPro.TextMeshProUGUI tmpLabel = null;
                                var allTMP = clone.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                                if (allTMP != null && allTMP.Length > 0)
                                    tmpLabel = allTMP[0];

                                if (tmpLabel != null)
                                {
                                    try { tmpLabel.enableAutoSizing = false; } catch { }
                                    tmpLabel.fontSize = FontSizeValue;
                                    tmpLabel.alignment = TMPro.TextAlignmentOptions.TopLeft;
                                    tmpLabel.color = new Color(0.2f, 1f, 0.4f, 1f);
                                    tmpLabel.raycastTarget = false;
                                    tmpLabel.text = BuildDepartureText(entries);
                                    clone.SetActive(true);
                                    try { tmpLabel.ForceMeshUpdate(); } catch { }

                                    StarTruckMP.Log.LogInfo($"JumpgateOption2: native CargoBay sign cloned for gate '{entryGateId}'.");

                                    return new GateBoard
                                    {
                                        gateEntryId = entryGateId,
                                        rootObject = clone,
                                        tmpLabel = tmpLabel,
                                        lastTextUpdate = Time.realtimeSinceStartup,
                                        currentPlayerEntries = new List<PlayerEntry>(entries)
                                    };
                                }
                                else
                                {
                                    UnityEngine.Object.Destroy(clone);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption2: TryCreateNativeSign error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Find a suitable parent GameObject for cloning a sign.
        /// Walk up the hierarchy looking for renderers (the visual part).
        /// </summary>
        private static GameObject FindSignParent(GameObject go)
        {
            var current = go.transform;
            while (current != null)
            {
                if (current.GetComponentsInChildren<Renderer>() != null
                    && current.GetComponentsInChildren<Renderer>().Length > 0)
                {
                    // Check if this has renderers and is a reasonable size to clone
                    return current.gameObject;
                }
                current = current.parent;
            }
            return null;
        }

        private static GateBoard CreateFallbackBoard(
            string entryGateId, Vector3 signPos, Quaternion fixedRot,
            List<PlayerEntry> entries, TMPro.TextMeshProUGUI sourceTMP)
        {
            if (sourceTMP == null)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption2: No source TMP for fallback board at gate '{entryGateId}'.");
                return null;
            }

            GameObject canvasObj = new GameObject($"DepartureBoard_FB_{entryGateId}");
            canvasObj.transform.position = signPos;
            canvasObj.transform.rotation = fixedRot;
            canvasObj.transform.localScale = Vector3.one * 0.01f;

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 500;

            var canvasRT = canvasObj.GetComponent<RectTransform>();
            if (canvasRT == null) canvasRT = canvasObj.AddComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(SignWidth, SignHeight);

            // Clone TMP
            GameObject tmpObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, canvasObj.transform);
            tmpObj.name = "DepartureText";

            var tmpRT = tmpObj.GetComponent<RectTransform>();
            if (tmpRT != null)
            {
                tmpRT.anchorMin = Vector2.zero;
                tmpRT.anchorMax = Vector2.one;
                tmpRT.offsetMin = Vector2.zero;
                tmpRT.offsetMax = Vector2.zero;
                tmpRT.localScale = Vector3.one;
            }

            var tmp = tmpObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp == null)
            {
                UnityEngine.Object.Destroy(canvasObj);
                return null;
            }

            // IL2CPP clone fixups
            try { tmp.enableAutoSizing = false; } catch { }
            try
            {
                if (tmp.font == null && sourceTMP.font != null) tmp.font = sourceTMP.font;
                if (tmp.fontSharedMaterial == null && sourceTMP.fontSharedMaterial != null)
                    tmp.fontSharedMaterial = sourceTMP.fontSharedMaterial;
            }
            catch { }

            tmp.fontSize = FontSizeValue;
            tmp.fontSizeMin = FontSizeValue;
            tmp.fontSizeMax = FontSizeValue;
            tmp.alignment = TMPro.TextAlignmentOptions.TopLeft;
            tmp.color = new Color(0.2f, 1f, 0.4f, 1f);
            tmp.raycastTarget = false;
            tmpObj.SetActive(true);
            try { var cr = tmpObj.GetComponent<CanvasRenderer>(); if (cr != null) cr.SetAlpha(1f); } catch { }
            try { var cg = tmpObj.GetComponent<CanvasGroup>(); if (cg != null) cg.alpha = 1f; } catch { }

            tmp.text = BuildDepartureText(entries);
            try { tmp.ForceMeshUpdate(); } catch { }
            try { Canvas.ForceUpdateCanvases(); } catch { }

            StarTruckMP.Log.LogInfo($"JumpgateOption2: fallback Canvas+TMP board for gate '{entryGateId}'.");

            return new GateBoard
            {
                gateEntryId = entryGateId,
                rootObject = canvasObj,
                tmpLabel = tmp,
                lastTextUpdate = Time.realtimeSinceStartup,
                currentPlayerEntries = new List<PlayerEntry>(entries)
            };
        }

        // ─── Public API ───

        public static void CreateBoards()
        {
            try
            {
                JumpgateUtils.CacheReflection();
                InvestigateNativeSigns();
                Cleanup();

                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return; }
                if (allGates == null || allGates.Length == 0) return;

                var sourceTMP = FindSourceTMP();
                int boardsCreated = 0;

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                    WarpGate gateComp = null;
                    try { gateComp = zone.GetComponent<WarpGate>(); } catch { }
                    if (gateComp == null)
                        try { gateComp = zone.GetComponentInParent<WarpGate>(); } catch { }

                    string entryId = JumpgateUtils.GetEntryGateId(gateComp);
                    if (string.IsNullOrEmpty(entryId))
                    {
                        entryId = zone.gameObject.name;
                        int ci = entryId.IndexOf("(Clone)");
                        if (ci > 0) entryId = entryId.Substring(0, ci).Trim();
                    }

                    var entries = CollectPlayersForGate(entryId, zone.transform.position);
                    var board = CreateBoardForGate(entryId, zone.transform, entries, sourceTMP);
                    if (board != null)
                    {
                        boards[entryId] = board;
                        boardsCreated++;
                    }
                }

                initialized = true;
                StarTruckMP.Log.LogInfo($"JumpgateOption2: {boardsCreated} board(s) created in '{sector}'.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption2.CreateBoards error: {ex}");
            }
        }

        public static void UpdatePositions()
        {
            if (!initialized || boards.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            if (now - lastUpdate < UpdateInterval) return;
            lastUpdate = now;

            try
            {
                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return; }
                if (allGates == null || allGates.Length == 0) return;

                var seenGateIds = new HashSet<string>();

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                    WarpGate gateComp = null;
                    try { gateComp = zone.GetComponent<WarpGate>(); } catch { }
                    if (gateComp == null)
                        try { gateComp = zone.GetComponentInParent<WarpGate>(); } catch { }

                    string entryId = JumpgateUtils.GetEntryGateId(gateComp);
                    if (string.IsNullOrEmpty(entryId))
                    {
                        entryId = zone.gameObject.name;
                        int ci = entryId.IndexOf("(Clone)");
                        if (ci > 0) entryId = entryId.Substring(0, ci).Trim();
                    }

                    seenGateIds.Add(entryId);

                    var currentEntries = CollectPlayersForGate(entryId, zone.transform.position);

                    // Always keep board alive — update text to FREE when empty

                    if (boards.TryGetValue(entryId, out var board))
                    {
                        bool changed = board.currentPlayerEntries.Count != currentEntries.Count;
                        if (!changed)
                        {
                            for (int i = 0; i < currentEntries.Count; i++)
                            {
                                if (board.currentPlayerEntries[i].playerName != currentEntries[i].playerName
                                    || Mathf.Abs(board.currentPlayerEntries[i].distanceFromGate - currentEntries[i].distanceFromGate) > 10f)
                                { changed = true; break; }
                            }
                        }
                        if (changed && board.tmpLabel != null)
                        {
                            board.tmpLabel.text = BuildDepartureText(currentEntries);
                            board.currentPlayerEntries = new List<PlayerEntry>(currentEntries);
                            try { board.tmpLabel.ForceMeshUpdate(); } catch { }
                        }
                    }
                    else
                    {
                        var sourceTMP = FindSourceTMP();
                        if (sourceTMP != null)
                        {
                            var newBoard = CreateBoardForGate(entryId, zone.transform, currentEntries, sourceTMP);
                            if (newBoard != null) boards[entryId] = newBoard;
                        }
                    }
                }

                var toRemove = new List<string>();
                foreach (var kv in boards)
                    if (!seenGateIds.Contains(kv.Key)) toRemove.Add(kv.Key);
                foreach (var key in toRemove)
                {
                    if (boards.TryGetValue(key, out var stale) && stale.rootObject != null)
                        UnityEngine.Object.Destroy(stale.rootObject);
                    boards.Remove(key);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption2.UpdatePositions error: {ex}");
            }
        }

        public static void Cleanup()
        {
            try
            {
                foreach (var kv in boards)
                    if (kv.Value?.rootObject != null) UnityEngine.Object.Destroy(kv.Value.rootObject);
                boards.Clear();
                initialized = false;
                lastUpdate = 0f;
            }
            catch { }
        }
    }
}
