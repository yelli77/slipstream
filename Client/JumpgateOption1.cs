using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Static 'airport departure board' style world-space Canvas+TMP sign for warp gates.
    /// Mounted next to each warp gate showing which players have that gate as their destination.
    /// Format: 'POS 1. PlayerName --- 3.2km', ranked by distance.
    /// The sign is STATIC — fixed orientation set once at creation, no camera-facing rotation.
    /// Like a real airport departure board you read as you fly past.
    /// </summary>
    public static class JumpgateOption1
    {
        // ─── State ───
        private static readonly Dictionary<string, GateBoard> boards = new();
        private static float lastUpdate = 0f;
        private static readonly float UpdateInterval = 1.5f; // seconds between text refreshes
        private static bool initialized = false;

        // ─── Constants ───
        private const float SideOffset = 12f;     // meters to the side of gate
        private const float HeightOffset = 8f;     // meters above gate
        private const float SignWidth = 2000f;     // canvas units
        private const float SignHeight = 3000f;    // canvas units
        private const float FontSizeValue = 180f;  // low hundreds for legibility

        // ─── GateBoard: holds everything for one gate's departure board ───
        private class GateBoard
        {
            public string gateEntryId;
            public GameObject rootObject;
            public TMPro.TextMeshProUGUI tmpLabel;
            public float lastTextUpdate;
            public List<PlayerEntry> currentPlayerEntries = new();
        }

        // ─── PlayerEntry: snapshot of one player heading to a gate ───
        private class PlayerEntry
        {
            public string playerName;
            public float distanceFromGate;
            public bool isLocal;
        }

        // ─── Reflection cache for WarpGate.entryGateId ───
        private static FieldInfo fi_entryGateId;
        private static bool reflectionCached = false;

        private static void CacheReflection()
        {
            if (reflectionCached) return;
            reflectionCached = true;
            try
            {
                var type = typeof(WarpGate);
                fi_entryGateId = type.GetField("entryGateId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi_entryGateId == null)
                {
                    // Try property
                    var pi = type.GetProperty("entryGateId",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                        StarTruckMP.Log.LogInfo("JumpgateOption1: entryGateId found as property");
                }
                StarTruckMP.Log.LogInfo($"JumpgateOption1: reflection cached, fi_entryGateId={fi_entryGateId != null}");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: reflection cache failed: {ex.Message}");
            }
        }

        private static string GetEntryGateId(WarpGate gate)
        {
            if (gate == null) return null;
            try
            {
                if (fi_entryGateId != null)
                    return fi_entryGateId.GetValue(gate) as string;

                // Fallback: property
                var pi = gate.GetType().GetProperty("entryGateId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null)
                    return pi.GetValue(gate) as string;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Find a suitable source TextMeshProUGUI to clone from the scene.
        /// </summary>
        private static TMPro.TextMeshProUGUI FindSourceTMP()
        {
            try
            {
                var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                if (allTMP == null) return null;
                foreach (var tmp in allTMP)
                {
                    if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                        return tmp;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Called once when sector loads. Creates departure boards next to each warp gate
        /// that has at least one player heading to it.
        /// </summary>
        public static void CreateBoards()
        {
            try
            {
                CacheReflection();
                Cleanup();

                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none")
                {
                    StarTruckMP.Log.LogInfo("JumpgateOption1: no sector, skipping CreateBoards.");
                    return;
                }

                // Find all warp gates in scene
                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"JumpgateOption1: FindObjectsOfType<WarpTriggerZone> failed: {ex.Message}");
                    return;
                }

                if (allGates == null || allGates.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("JumpgateOption1: no WarpTriggerZone found in scene.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"JumpgateOption1: found {allGates.Length} gates in sector '{sector}'");

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null)
                {
                    StarTruckMP.Log.LogWarning("JumpgateOption1: no source TMP found, cannot create boards.");
                    return;
                }

                int boardsCreated = 0;
                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                    // Get entryGateId via reflection on WarpGate component
                    WarpGate gateComp = null;
                    try { gateComp = zone.GetComponent<WarpGate>(); } catch { }
                    if (gateComp == null)
                        try { gateComp = zone.GetComponentInParent<WarpGate>(); } catch { }

                    string entryId = GetEntryGateId(gateComp);
                    if (string.IsNullOrEmpty(entryId))
                    {
                        // Fallback: use game object name
                        entryId = zone.gameObject.name;
                        int ci = entryId.IndexOf("(Clone)");
                        if (ci > 0) entryId = entryId.Substring(0, ci).Trim();
                        StarTruckMP.Log.LogInfo($"JumpgateOption1: using fallback entryId '{entryId}' for gate '{zone.gameObject.name}'");
                    }

                    // Find all players heading to this gate
                    var playerEntries = CollectPlayersForGate(entryId, zone.transform.position);
                    if (playerEntries.Count == 0)
                    {
                        StarTruckMP.Log.LogInfo($"JumpgateOption1: no players heading to gate '{entryId}', skipping board.");
                        continue;
                    }

                    // Create the board
                    var board = CreateBoardForGate(entryId, zone.transform, playerEntries, sourceTMP);
                    if (board != null)
                    {
                        boards[entryId] = board;
                        boardsCreated++;
                    }
                }

                StarTruckMP.Log.LogInfo($"JumpgateOption1: {boardsCreated} departure board(s) created in '{sector}'.");
                initialized = true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.CreateBoards error: {ex}");
            }
        }

        /// <summary>
        /// Collect all players heading to a specific gate, ranked by distance.
        /// </summary>
        private static List<PlayerEntry> CollectPlayersForGate(string entryGateId, Vector3 gateWorldPos)
        {
            var entries = new List<PlayerEntry>();

            // Remote players
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
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: playerList iteration error: {ex.Message}");
            }

            // Local player
            try
            {
                if (!string.IsNullOrEmpty(StarTruckClient.currentDestinationGateId)
                    && StarTruckClient.currentDestinationGateId == entryGateId
                    && StarTruckClient.myTruck != null)
                {
                    Vector3 myPos = StarTruckClient.floatingOrigin != null
                        ? StarTruckClient.floatingOrigin.m_currentOrigin + StarTruckClient.myTruck.transform.position
                        : StarTruckClient.myTruck.transform.position;
                    float dist = Vector3.Distance(gateWorldPos, myPos);
                    string myName = StarTruckClient.myPlayerName ?? "Du";
                    entries.Add(new PlayerEntry
                    {
                        playerName = myName,
                        distanceFromGate = dist,
                        isLocal = true
                    });
                }
            }
            catch { }

            // Sort by distance (closest first)
            entries.Sort((a, b) => a.distanceFromGate.CompareTo(b.distanceFromGate));
            return entries;
        }

        /// <summary>
        /// Build the multi-line departure text for a gate.
        /// </summary>
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
        /// Create a single departure board next to a warp gate.
        /// Uses the from-scratch Canvas+TMP clone recipe from NOTES_WORLDSPACE_UI.md.
        /// </summary>
        private static GateBoard CreateBoardForGate(
            string entryGateId,
            Transform gateTransform,
            List<PlayerEntry> entries,
            TMPro.TextMeshProUGUI sourceTMP)
        {
            Vector3 gatePos = gateTransform.position;

            // Compute approach direction (use gate's forward, or fallback to world forward)
            Vector3 approachDir = gateTransform.forward;
            if (approachDir.sqrMagnitude < 0.01f)
                approachDir = Vector3.forward;
            approachDir = approachDir.normalized;

            // Position sign to the side and above the gate
            Vector3 sideDir = Vector3.Cross(Vector3.up, approachDir).normalized;
            // Ensure side direction is not degenerate (if approach is straight up)
            if (sideDir.sqrMagnitude < 0.01f)
                sideDir = Vector3.Cross(Vector3.right, approachDir).normalized;

            Vector3 signPos = gatePos + sideDir * SideOffset + Vector3.up * HeightOffset;

            // Orientation: face back along the approach direction (like a roadside sign)
            Quaternion fixedRot = Quaternion.LookRotation(-approachDir, Vector3.up);

            // ── Create Canvas root ──
            GameObject canvasObj = new GameObject($"DepartureBoard_{entryGateId}");
            canvasObj.transform.position = signPos;
            canvasObj.transform.rotation = fixedRot;
            canvasObj.transform.localScale = Vector3.one * 0.01f; // Scale canvas down to world size

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 500;

            var canvasRT = canvasObj.GetComponent<RectTransform>();
            if (canvasRT == null) canvasRT = canvasObj.AddComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(SignWidth, SignHeight);

            // ── Clone TMP from source ──
            GameObject tmpObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, canvasObj.transform);
            tmpObj.name = "DepartureText";

            // Reset transform to fill canvas
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
                StarTruckMP.Log.LogWarning($"JumpgateOption1: TMP component missing on clone for gate '{entryGateId}'");
                UnityEngine.Object.Destroy(canvasObj);
                return null;
            }

            // ── IL2CPP clone gotchas: fix them in the right order ──

            // 1. Disable auto-sizing BEFORE setting fontSize (IL2CPP quirk)
            try { tmp.enableAutoSizing = false; } catch { }

            // 2. Re-bind font and material from source
            try
            {
                if (tmp.font == null && sourceTMP.font != null)
                    tmp.font = sourceTMP.font;
                if (tmp.fontSharedMaterial == null && sourceTMP.fontSharedMaterial != null)
                    tmp.fontSharedMaterial = sourceTMP.fontSharedMaterial;
            }
            catch { }

            // 3. Set fontSize (must be AFTER disabling auto-sizing)
            tmp.fontSize = FontSizeValue;
            tmp.fontSizeMin = FontSizeValue;
            tmp.fontSizeMax = FontSizeValue;

            // 4. Alignment: top-left
            tmp.alignment = TMPro.TextAlignmentOptions.TopLeft;

            // 5. Text color: bright yellow/green for visibility in space
            tmp.color = new Color(0.2f, 1f, 0.4f, 1f); // bright green

            // 6. Re-assert active state and alpha
            tmpObj.SetActive(true);
            try
            {
                var renderer = tmpObj.GetComponent<CanvasRenderer>();
                if (renderer != null) renderer.SetAlpha(1f);
            }
            catch { }
            try
            {
                var cg = tmpObj.GetComponent<CanvasGroup>();
                if (cg != null) cg.alpha = 1f;
            }
            catch { }

            // 7. Raycast target off
            tmp.raycastTarget = false;

            // 8. Set initial text
            tmp.text = BuildDepartureText(entries);

            // 9. Force mesh update
            try { tmp.ForceMeshUpdate(); } catch { }

            // 10. Force canvas update
            try { Canvas.ForceUpdateCanvases(); } catch { }

            StarTruckMP.Log.LogInfo($"JumpgateOption1: board created for gate '{entryGateId}' at ({signPos.x:F0},{signPos.y:F0},{signPos.z:F0}) with {entries.Count} player(s).");

            return new GateBoard
            {
                gateEntryId = entryGateId,
                rootObject = canvasObj,
                tmpLabel = tmp,
                lastTextUpdate = Time.realtimeSinceStartup,
                currentPlayerEntries = new List<PlayerEntry>(entries)
            };
        }

        /// <summary>
        /// Called every frame from Harmony patch. Updates text content periodically
        /// (not every frame — string allocation is throttled).
        /// Handles players appearing/disappearing from gates.
        /// </summary>
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

                // Find all gates again to refresh player lists
                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return; }

                if (allGates == null || allGates.Length == 0) return;

                // Track which gate IDs we've seen this update cycle
                var seenGateIds = new HashSet<string>();

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                    WarpGate gateComp = null;
                    try { gateComp = zone.GetComponent<WarpGate>(); } catch { }
                    if (gateComp == null)
                        try { gateComp = zone.GetComponentInParent<WarpGate>(); } catch { }

                    string entryId = GetEntryGateId(gateComp);
                    if (string.IsNullOrEmpty(entryId))
                    {
                        entryId = zone.gameObject.name;
                        int ci = entryId.IndexOf("(Clone)");
                        if (ci > 0) entryId = entryId.Substring(0, ci).Trim();
                    }

                    seenGateIds.Add(entryId);

                    // Collect current players for this gate
                    var currentEntries = CollectPlayersForGate(entryId, zone.transform.position);

                    if (currentEntries.Count == 0)
                    {
                        // No players heading here — destroy board if it exists
                        if (boards.TryGetValue(entryId, out var existingBoard))
                        {
                            if (existingBoard.rootObject != null)
                                UnityEngine.Object.Destroy(existingBoard.rootObject);
                            boards.Remove(entryId);
                            StarTruckMP.Log.LogInfo($"JumpgateOption1: removed board for gate '{entryId}' (no players).");
                        }
                        continue;
                    }

                    // Check if player list changed
                    bool changed = false;
                    if (boards.TryGetValue(entryId, out var board))
                    {
                        if (board.currentPlayerEntries.Count != currentEntries.Count)
                        {
                            changed = true;
                        }
                        else
                        {
                            for (int i = 0; i < currentEntries.Count; i++)
                            {
                                if (board.currentPlayerEntries[i].playerName != currentEntries[i].playerName
                                    || Mathf.Abs(board.currentPlayerEntries[i].distanceFromGate - currentEntries[i].distanceFromGate) > 10f)
                                {
                                    changed = true;
                                    break;
                                }
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
                        // New gate with players — create board
                        var sourceTMP = FindSourceTMP();
                        if (sourceTMP != null)
                        {
                            var newBoard = CreateBoardForGate(entryId, zone.transform, currentEntries, sourceTMP);
                            if (newBoard != null)
                                boards[entryId] = newBoard;
                        }
                    }
                }

                // Destroy boards for gates that no longer exist in scene
                var toRemove = new List<string>();
                foreach (var kv in boards)
                {
                    if (!seenGateIds.Contains(kv.Key))
                        toRemove.Add(kv.Key);
                }
                foreach (var key in toRemove)
                {
                    if (boards.TryGetValue(key, out var staleBoard))
                    {
                        if (staleBoard.rootObject != null)
                            UnityEngine.Object.Destroy(staleBoard.rootObject);
                        StarTruckMP.Log.LogInfo($"JumpgateOption1: removed stale board for gate '{key}' (gate not in scene).");
                    }
                    boards.Remove(key);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.UpdatePositions error: {ex}");
            }
        }

        /// <summary>
        /// Destroy all created boards and clear internal state.
        /// Safe to call multiple times.
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                foreach (var kv in boards)
                {
                    if (kv.Value?.rootObject != null)
                        UnityEngine.Object.Destroy(kv.Value.rootObject);
                }
                boards.Clear();
                initialized = false;
                lastUpdate = 0f;
                StarTruckMP.Log.LogInfo("JumpgateOption1: cleaned up all departure boards.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.Cleanup error: {ex}");
            }
        }
    }
}
