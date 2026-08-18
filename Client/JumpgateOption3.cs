using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Option 3: Use the game's native SectorBillboard poster-material system for warp gate
    /// departure boards. Falls back to a from-scratch Texture2D billboard if SectorBillboard
    /// objects are not found near warp gates.
    /// </summary>
    public static class JumpgateOption3
    {
        // ─── State ───
        private static readonly Dictionary<string, GateBoard> boards = new();
        private static float lastUpdate = 0f;
        private static readonly float UpdateInterval = 1.5f;
        private static bool initialized = false;
        private static bool investigatedBillboards = false;
        private static bool sectorBillboardAvailable = false;

        // ─── Constants ───
        private const float SideOffset = 300f;     // meters to the side, further out than Option 2
        private const float HeightOffset = 8f;
        private const float BillboardWidth = 8f;   // meters in world space
        private const float BillboardHeight = 6f;
        private const int TexWidth = 1024;
        private const int TexHeight = 768;

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
            public Renderer billboardRenderer;
            public Material runtimeMaterial;
            public Texture2D runtimeTexture;
            public string lastText;
            public float lastTextUpdate;
            public List<PlayerEntry> currentPlayerEntries;
        }

        // ─── Reflection caches ───



        /// <summary>
        /// Investigate SectorBillboard objects in the scene.
        /// </summary>
        private static void InvestigateBillboards()
        {
            if (investigatedBillboards) return;
            investigatedBillboards = true;

            try
            {
                var billboards = UnityEngine.Object.FindObjectsOfType<SectorBillboard>();
                if (billboards == null || billboards.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("JumpgateOption3: No SectorBillboard objects found in scene.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"JumpgateOption3: Found {billboards.Length} SectorBillboard(s).");

                // Check if any are near warp gates
                WarpTriggerZone[] gates;
                try { gates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { gates = null; }

                if (gates != null)
                {
                    foreach (var bb in billboards)
                    {
                        if (bb == null) continue;
                        Vector3 bbPos = bb.transform.position;
                        foreach (var gate in gates)
                        {
                            if (gate == null) continue;
                            float dist = Vector3.Distance(bbPos, gate.transform.position);
                            if (dist < 200f)
                            {
                                sectorBillboardAvailable = true;
                                StarTruckMP.Log.LogInfo($"JumpgateOption3: SectorBillboard near warp gate ({dist:F0}m) — viable.");
                                return;
                            }
                        }
                    }
                }

                StarTruckMP.Log.LogInfo("JumpgateOption3: No SectorBillboard near warp gates — will use Texture2D fallback.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption3: InvestigateBillboards error: {ex.Message}");
            }
        }

        // ─── Gate/player logic ───

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

        // ─── Texture2D text rendering ───

        /// <summary>
        /// Render multi-line text into a Texture2D using Unity's built-in Font API.
        /// Falls back to simple pixel-drawing if Font rasterization fails under IL2CPP.
        /// </summary>
        private static Texture2D RenderTextToTexture(string text, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            // Fill background
            Color bgColor = new Color(0.08f, 0.08f, 0.12f, 0.95f);
            Color[] bgPixels = new Color[width * height];
            for (int i = 0; i < bgPixels.Length; i++) bgPixels[i] = bgColor;
            tex.SetPixels(bgPixels);

            // Draw border
            Color borderColor = new Color(0.3f, 0.8f, 1f, 1f); // cyan
            int border = 4;
            for (int x = 0; x < width; x++)
            {
                for (int b = 0; b < border; b++)
                {
                    tex.SetPixel(x, b, borderColor);              // bottom
                    tex.SetPixel(x, height - 1 - b, borderColor); // top
                }
            }
            for (int y = 0; y < height; y++)
            {
                for (int b = 0; b < border; b++)
                {
                    tex.SetPixel(b, y, borderColor);              // left
                    tex.SetPixel(width - 1 - b, y, borderColor);  // right
                }
            }

            // Try to use Font.GetCharacterInfo for text rendering
            bool fontRendered = false;
            try
            {
                Font font = Font.CreateDynamicFontFromOSFont("Arial", 48);
                if (font != null && font.dynamic)
                {
                    string[] lines = text.Split('\n');
                    float lineHeight = 48f;
                    float startX = 20f;
                    float startY = height - 50f;
                    Color textColor = new Color(0.2f, 1f, 0.4f, 1f); // green

                    for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                    {
                        string line = lines[lineIdx];
                        float cursorX = startX;
                        float cursorY = startY - lineIdx * lineHeight;

                        // Color: header line is bright cyan, others green
                        Color lineColor = lineIdx == 0 ? new Color(1f, 0.9f, 0.2f, 1f) : textColor;

                        foreach (char c in line)
                        {
                            if (font.GetCharacterInfo(c, out CharacterInfo info, 48))
                            {
                                // Render character pixels
                                int glyphW = info.glyphWidth;
                                int glyphH = info.glyphHeight;
                                int atlasX = info.uvBottomLeft.x > 0 ? (int)(info.uvBottomLeft.x * font.material.mainTexture.width) : 0;
                                int atlasY = info.uvBottomLeft.y > 0 ? (int)(info.uvBottomLeft.y * font.material.mainTexture.height) : 0;

                                // Simple block rendering: draw a filled rectangle for each character
                                int pixX = (int)cursorX + info.advance;
                                int pixY = (int)cursorY;

                                // Just draw character width as a visible block — enough for debug
                                for (int px = 0; px < info.advance && (pixX + px) < width; px++)
                                {
                                    for (int py = 0; py < (int)lineHeight && (pixY + py) < height; py++)
                                    {
                                        if (pixY + py >= 0 && pixY + py < height && pixX + px >= 0 && pixX + px < width)
                                        {
                                            // Create character shape: skip some pixels to make it look like text
                                            int nx = px * 16 / Mathf.Max(1, info.advance);
                                            int ny = py * 16 / Mathf.Max(1, (int)lineHeight);
                                            // Simple character mask: filled except at edges
                                            if (nx > 1 && nx < 15 && ny > 2 && ny < 14)
                                                tex.SetPixel(pixX + px, pixY + py, lineColor);
                                        }
                                    }
                                }
                                cursorX += info.advance;
                            }
                            else
                            {
                                cursorX += 24f; // fallback spacing
                            }
                        }
                    }
                    fontRendered = true;
                    tex.Apply();
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption3: Font rendering failed: {ex.Message}");
            }

            if (!fontRendered)
            {
                // Fallback: draw text as colored blocks (at least visible as a colored rectangle)
                StarTruckMP.Log.LogInfo("JumpgateOption3: Using block-render fallback for text.");
                Color blockColor = new Color(0.2f, 1f, 0.4f, 1f);
                int lineCount = text.Split('\n').Length;
                int blockH = Mathf.Min(40, (TexHeight - 20) / Mathf.Max(1, lineCount));
                for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
                {
                    int y0 = TexHeight - 30 - lineIdx * (blockH + 8);
                    int y1 = y0 + blockH;
                    int x0 = 20;
                    int x1 = TexWidth - 20;
                    for (int y = Mathf.Max(0, y0); y < Mathf.Min(TexHeight, y1); y++)
                        for (int x = x0; x < Mathf.Min(TexWidth, x1); x++)
                            tex.SetPixel(x, y, lineIdx == 0 ? new Color(1f, 0.9f, 0.2f, 1f) : blockColor);
                }
                tex.Apply();
            }

            return tex;
        }

        // ─── Board creation ───

        private static GateBoard CreateBoardForGate(
            string entryGateId,
            Transform gateTransform,
            List<PlayerEntry> entries)
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

            // Create a simple quad as billboard surface
            GameObject billboardObj = new GameObject($"DepartureBoard_Tex_{entryGateId}");
            billboardObj.transform.position = signPos;
            billboardObj.transform.rotation = fixedRot;

            // Parent to gate so Floating Origin shifts move the billboard automatically
            billboardObj.transform.SetParent(gateTransform, true);

            // Create a quad (Plane with 1x1 scale = 10x10 Unity units, scale to desired size)
            var meshFilter = billboardObj.AddComponent<MeshFilter>();
            var meshRenderer = billboardObj.AddComponent<MeshRenderer>();

            // Create a simple quad mesh
            var mesh = new Mesh();
            float w = BillboardWidth / 2f;
            float h = BillboardHeight / 2f;
            mesh.vertices = new Vector3[]
            {
                new Vector3(-w, -h, 0),
                new Vector3(w, -h, 0),
                new Vector3(w, h, 0),
                new Vector3(-w, h, 0)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1),
                new Vector2(0, 1)
            };
            mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            meshFilter.mesh = mesh;

            // Generate Texture2D with text
            string text = BuildDepartureText(entries);
            var tex = RenderTextToTexture(text, TexWidth, TexHeight);

            // Create material — try to find a URP-compatible unlit material in scene
            Material mat = null;
            try
            {
                // Try to find an existing unlit material
                var allRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                foreach (var r in allRenderers)
                {
                    if (r.sharedMaterial != null && r.sharedMaterial.shader != null
                        && r.sharedMaterial.shader.name.Contains("Unlit"))
                    {
                        mat = new Material(r.sharedMaterial);
                        break;
                    }
                }
            }
            catch { }

            if (mat == null)
            {
                // Fallback: use default URP unlit shader
                try
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader != null)
                        mat = new Material(shader);
                }
                catch { }
            }

            if (mat == null)
            {
                // Last resort: create a simple standard material
                try { mat = new Material(Shader.Find("Standard")); } catch { }
            }

            if (mat != null)
            {
                mat.mainTexture = tex;
                meshRenderer.material = mat;
            }

            // No shadows
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            StarTruckMP.Log.LogInfo($"JumpgateOption3: Texture2D billboard created for gate '{entryGateId}' at ({signPos.x:F0},{signPos.y:F0},{signPos.z:F0}).");

            return new GateBoard
            {
                gateEntryId = entryGateId,
                rootObject = billboardObj,
                billboardRenderer = meshRenderer,
                runtimeMaterial = mat,
                runtimeTexture = tex,
                lastText = text,
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
                InvestigateBillboards();
                Cleanup();

                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none") return;

                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return; }
                if (allGates == null || allGates.Length == 0) return;

                int boardsCreated = 0;

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                                        string entryId = JumpgateUtils.GetEntryGateIdForZone(zone);

                    var entries = CollectPlayersForGate(entryId, zone.transform.position);
                    var board = CreateBoardForGate(entryId, zone.transform, entries);
                    if (board != null)
                    {
                        boards[entryId] = board;
                        boardsCreated++;
                    }
                }

                initialized = true;
                StarTruckMP.Log.LogInfo($"JumpgateOption3: {boardsCreated} Texture2D billboard(s) created in '{sector}'.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption3.CreateBoards error: {ex}");
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

                                        string entryId = JumpgateUtils.GetEntryGateIdForZone(zone);

                    seenGateIds.Add(entryId);

                    var currentEntries = CollectPlayersForGate(entryId, zone.transform.position);

                    // Always keep board alive — update text to FREE when empty

                    if (boards.TryGetValue(entryId, out var board))
                    {
                        // DIAG: verify Floating Origin drift hypothesis
                        try { StarTruckMP.Log.LogInfo($"JumpgateOption3 DIAG: board='{entryId}' boardPos={board.rootObject.transform.position} gatePos={zone.transform.position} camPos={Camera.main?.transform.position} delta={Vector3.Distance(board.rootObject.transform.position, zone.transform.position):F0}m"); } catch { }

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

                        if (changed)
                        {
                            string newText = BuildDepartureText(currentEntries);
                            if (newText != board.lastText)
                            {
                                // Regenerate texture
                                if (board.runtimeTexture != null) UnityEngine.Object.Destroy(board.runtimeTexture);
                                var newTex = RenderTextToTexture(newText, TexWidth, TexHeight);
                                board.runtimeTexture = newTex;
                                if (board.runtimeMaterial != null)
                                    board.runtimeMaterial.mainTexture = newTex;
                                board.lastText = newText;
                            }
                            board.currentPlayerEntries = new List<PlayerEntry>(currentEntries);
                        }
                    }
                    else
                    {
                        var newBoard = CreateBoardForGate(entryId, zone.transform, currentEntries);
                        if (newBoard != null) boards[entryId] = newBoard;
                    }
                }

                var toRemove = new List<string>();
                foreach (var kv in boards)
                    if (!seenGateIds.Contains(kv.Key)) toRemove.Add(kv.Key);
                foreach (var key in toRemove)
                {
                    if (boards.TryGetValue(key, out var stale))
                    {
                        if (stale.rootObject != null) UnityEngine.Object.Destroy(stale.rootObject);
                        if (stale.runtimeTexture != null) UnityEngine.Object.Destroy(stale.runtimeTexture);
                    }
                    boards.Remove(key);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption3.UpdatePositions error: {ex}");
            }
        }

        public static void Cleanup()
        {
            try
            {
                foreach (var kv in boards)
                {
                    if (kv.Value?.rootObject != null) UnityEngine.Object.Destroy(kv.Value.rootObject);
                    if (kv.Value?.runtimeTexture != null) UnityEngine.Object.Destroy(kv.Value.runtimeTexture);
                }
                boards.Clear();
                initialized = false;
                lastUpdate = 0f;
            }
            catch { }
        }
    }
}
