using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using StarTruckMP.Utilities;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 3D World-Space Billboard near each WarpGate showing which players
    /// (and NPC trucks) have marked this gate as their destination jumpgate.
    /// Shows: Gate Name, ranked list (sorted by distance), or "FREE" when empty.
    /// Uses TextGenerator + Mesh (proven CreateNameLabel pattern) — no TMP, no Canvas.
    /// </summary>
    public static class WarpGateBillboard
    {
        private static List<GateBillboard> billboards = new List<GateBillboard>();
        private static string lastSector = "none";
        private static float nextUpdateTime = 0f;
        private static readonly float UpdateInterval = 0.5f;
        private static readonly float BillboardDistance = 50f;
        private static readonly float SideOffset = 125f;
        private static readonly float HeightOffset = 12f;

        // How far / how aligned with a gate an NPC (or player) truck has to be
        // before it counts as "heading to" that gate.
        private static readonly float HeadingCheckRadius = 1500f;
        private static readonly float HeadingMinDot = 0.3f;

        // Colors
        private static readonly Color GateNameColor = Color.white;
        private static readonly Color PlayerColor = new Color(0.2f, 1f, 0.8f, 1f);
        private static readonly Color NpcColor = new Color(1f, 0.85f, 0.3f, 1f);
        private static readonly Color FreeColor = new Color(0f, 1f, 0.5f, 0.95f);
        private static readonly Color SepColor = new Color(0.3f, 0.5f, 0.7f, 0.6f);

        // Cached font (created once, reused across all billboards)
        private static Font cachedFont = null;
        private static bool fontInitialized = false;

        // Reflection cache for gate name resolution
        private static FieldInfo fi_entryGateName = null;
        private static FieldInfo fi_entryGateId = null;
        private static bool gateNameReflectionSearched = false;

        private static bool diagLogged = false;

        private class GateBillboard
        {
            public string gateId;
            public string gateName;
            public WarpTriggerZone gateZone;
            public GameObject rootObj;
            public GameObject backingObj;
            public GameObject textNameObj;
            public GameObject textSepObj;
            public GameObject textContentObj;
            public float lastContentUpdate;
        }

        /// <summary>
        /// Initializes the cached font once. Returns null if font creation fails.
        /// </summary>
        private static Font GetFont()
        {
            if (fontInitialized) return cachedFont;
            fontInitialized = true;
            try
            {
                cachedFont = Font.CreateDynamicFontFromOSFont("Arial", 80);
            }
            catch { }
            if (cachedFont == null)
                StarTruckMP.Log.LogWarning("WarpGateBillboard: Font.CreateDynamicFontFromOSFont failed");
            return cachedFont;
        }

        /// <summary>
        /// Resolves gate ID via reflection on WarpGate component.
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
        /// Builds a Mesh from a text string using TextGenerator.
        /// Returns null if font is missing or no vertices are generated.
        /// </summary>
        private static Mesh BuildTextMesh(string text, Font font, int fontSize)
        {
            if (font == null) return null;
            if (string.IsNullOrEmpty(text)) text = " ";

            TextGenerator textGen = new TextGenerator();
            var settings = new TextGenerationSettings();
            settings.font = font;
            settings.fontSize = fontSize;
            settings.fontStyle = FontStyle.Bold;
            settings.textAnchor = TextAnchor.MiddleCenter;
            settings.color = Color.white;
            settings.scaleFactor = 1f;
            settings.lineSpacing = 1.2f;
            settings.richText = false;
            settings.resizeTextForBestFit = false;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            settings.generationExtents = new Vector2(2400, 600);
            settings.pivot = new Vector2(0.5f, 0.5f);
            settings.updateBounds = true;
            settings.generateOutOfBounds = true;
            settings.alignByGeometry = false;

            textGen.Populate(text, settings);
            var vertList = new Il2CppSystem.Collections.Generic.List<UIVertex>();
            textGen.GetVertices(vertList);
            UIVertex[] uiVerts = vertList.ToArray();

            if (uiVerts == null || uiVerts.Length == 0) return null;

            Mesh mesh = new Mesh();
            Vector3[] verts = new Vector3[uiVerts.Length];
            Vector2[] uvs = new Vector2[uiVerts.Length];
            Color32[] colors = new Color32[uiVerts.Length];

            for (int i = 0; i < uiVerts.Length; i += 4)
            {
                // TextGenerator outputs: i=BL, i+1=TL, i+2=BR, i+3=TR
                // Standard quad winding: i=BL, i+1=TL, i+2=TR, i+3=BR
                verts[i] = uiVerts[i].position;
                verts[i + 1] = uiVerts[i + 1].position;
                verts[i + 2] = uiVerts[i + 3].position; // TR
                verts[i + 3] = uiVerts[i + 2].position; // BR

                float uvY0 = Mathf.Clamp(uiVerts[i].uv0.y, 0.01f, 0.99f);
                float uvY1 = Mathf.Clamp(uiVerts[i + 1].uv0.y, 0.01f, 0.99f);
                float uvY2 = Mathf.Clamp(uiVerts[i + 3].uv0.y, 0.01f, 0.99f);
                float uvY3 = Mathf.Clamp(uiVerts[i + 2].uv0.y, 0.01f, 0.99f);

                uvs[i] = new Vector2(uiVerts[i].uv0.x, uvY0);
                uvs[i + 1] = new Vector2(uiVerts[i + 1].uv0.x, uvY1);
                uvs[i + 2] = new Vector2(uiVerts[i + 3].uv0.x, uvY2);
                uvs[i + 3] = new Vector2(uiVerts[i + 2].uv0.x, uvY3);

                colors[i] = uiVerts[i].color;
                colors[i + 1] = uiVerts[i + 1].color;
                colors[i + 2] = uiVerts[i + 3].color;
                colors[i + 3] = uiVerts[i + 2].color;
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors32 = colors;

            int quadCount = uiVerts.Length / 4;
            int[] tris = new int[quadCount * 6];
            int ti = 0;
            for (int i = 0; i < uiVerts.Length; i += 4)
            {
                tris[ti++] = i; tris[ti++] = i + 1; tris[ti++] = i + 2;
                tris[ti++] = i; tris[ti++] = i + 2; tris[ti++] = i + 3;
            }
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Creates a text line GameObject with MeshFilter + MeshRenderer using TextGenerator.
        /// The text is rendered as a 3D mesh, scaled to targetWidth.
        /// </summary>
        private static GameObject CreateTextLine(string text, float fontSize, Color color,
            Transform parent, Vector3 localPos, float targetWidth)
        {
            Font font = GetFont();
            if (font == null) return null;

            Mesh mesh = BuildTextMesh(text, font, (int)fontSize);
            if (mesh == null) return null;

            Bounds bounds = mesh.bounds;
            float textScale = (bounds.size.x > 0) ? (targetWidth / bounds.size.x) : 0.01f;

            GameObject textObj = new GameObject("TextLine");
            textObj.transform.SetParent(parent, false);
            textObj.transform.localPosition = localPos;

            MeshFilter mf = textObj.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = textObj.AddComponent<MeshRenderer>();
            // Use font material but ensure URP compatibility
            var textMat = new Material(font.material);
            // If font shader is hidden/broken, try URP unlit
            if (textMat.shader == null || textMat.shader.name.Contains("Hidden"))
            {
                Shader fallback = Shader.Find("Universal Render Pipeline/Unlit");
                if (fallback != null) textMat.shader = fallback;
            }
            textMat.SetColor("_BaseColor", color); // URP unlit color
            textMat.SetColor("_Color", color);     // Built-in fallback
            textMat.SetInt("_Cull", 0); // No backface culling
            mr.material = textMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            textObj.transform.localScale = new Vector3(textScale, textScale, textScale);
            return textObj;
        }

        /// <summary>
        /// Creates the 3D backing slab behind the billboard text.
        /// </summary>
        private static GameObject CreateBacking(string gateName, Transform parent)
        {
            GameObject backing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            backing.name = "Backing_" + gateName;
            backing.transform.SetParent(parent, false);
            backing.transform.localPosition = new Vector3(0f, 0f, 0.15f);
            backing.transform.localScale = new Vector3(8f, 12f, 0.1f);

            var bRenderer = backing.GetComponent<MeshRenderer>();
            if (bRenderer != null)
            {
                // Try URP shaders first, fallback to Standard
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Standard");

                var mat = new Material(shader);
                mat.color = new Color(0.08f, 0.08f, 0.15f, 1f); // Visible dark blue, fully opaque
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.02f, 0.03f, 0.06f));
                bRenderer.material = mat;
                bRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                bRenderer.receiveShadows = false;
                bRenderer.enabled = true;
            }

            var autoCol = backing.GetComponent<Collider>();
            if (autoCol != null) UnityEngine.Object.Destroy(autoCol);

            return backing;
        }

        /// <summary>
        /// Creates a world-space billboard for a single gate using TextGenerator+Mesh.
        /// </summary>
        private static void CreateBillboard(WarpTriggerZone zone)
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

            // Static rotation: face outward from gate (toward approaching traffic)
            root.transform.rotation = Quaternion.LookRotation(gateForward, Vector3.up);

            // 3D backing slab
            GameObject backing = CreateBacking(gateName, root.transform);

            // EXIT GATE line — yellow, large
            string exitText = "EXIT GATE:\n" + gateName;
            GameObject nameObj = CreateTextLine(exitText, 80f, GateNameColor,
                root.transform, new Vector3(0f, 4.5f, -0.1f), 12f);
            if (nameObj != null) nameObj.name = "Name_" + gateName;

            // Separator line
            GameObject sepObj = CreateTextLine("————————————————", 60f, SepColor,
                root.transform, new Vector3(0f, 1.0f, -0.1f), 12f);
            if (sepObj != null) sepObj.name = "Sep_" + gateName;

            // Player content — starts as "FREE"
            GameObject contentObj = CreateTextLine("FREE", 70f, FreeColor,
                root.transform, new Vector3(0f, -2.5f, -0.1f), 12f);
            if (contentObj != null) contentObj.name = "Content_" + gateName;

            if (!diagLogged)
            {
                var cam = Camera.main;
                float camDist = cam != null ? Vector3.Distance(root.transform.position, cam.transform.position) : -1f;
                StarTruckMP.Log.LogInfo($"WarpGateBillboard: placed '{gateName}' at {root.transform.position}, camDist={camDist:F0}m (TextGenerator+Mesh)");
            }

            billboards.Add(new GateBillboard
            {
                gateId = gateId,
                gateName = gateName,
                gateZone = zone,
                rootObj = root,
                backingObj = backing,
                textNameObj = nameObj,
                textSepObj = sepObj,
                textContentObj = contentObj,
                lastContentUpdate = 0f,
            });
        }

        /// <summary>
        /// Collects all players (remote + local) and NPC trucks heading to a specific
        /// gate. Returns list of (name, distance) tuples, sorted by distance.
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
        /// Rebuilds the content text mesh when the player list changes.
        /// </summary>
        private static void UpdateBillboardContent(GateBillboard bb)
        {
            if (bb.textContentObj == null) return;

            Vector3 gateWorldPos = bb.gateZone.transform.position;
            var players = GetPlayersForGate(bb.gateId, gateWorldPos);

            string contentText;
            Color contentColor;
            float fontSize;

            if (players.Count == 0)
            {
                contentText = "FREE";
                contentColor = FreeColor;
                fontSize = 70f;
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
                contentText = sb.ToString().TrimEnd();
                contentColor = PlayerColor;
                fontSize = 60f;
            }

            Font font = GetFont();
            if (font == null) return;

            Mesh newMesh = BuildTextMesh(contentText, font, (int)fontSize);
            if (newMesh == null) return;

            // Scale to fit the billboard width
            Bounds bounds = newMesh.bounds;
            float targetWidth = 12f;
            float textScale = (bounds.size.x > 0) ? (targetWidth / bounds.size.x) : 0.01f;

            // Update or recreate mesh on the content object
            MeshFilter mf = bb.textContentObj.GetComponent<MeshFilter>();
            if (mf != null)
            {
                if (mf.mesh != null) UnityEngine.Object.Destroy(mf.mesh);
                mf.mesh = newMesh;
            }
            bb.textContentObj.transform.localScale = new Vector3(textScale, textScale, textScale);

            // Update material color
            MeshRenderer mr = bb.textContentObj.GetComponent<MeshRenderer>();
            if (mr != null && mr.material != null)
            {
                mr.material.SetColor("_Color", contentColor);
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

                // Ensure font is available
                if (GetFont() == null)
                {
                    StarTruckMP.Log.LogWarning("WarpGateBillboard: no font available, skipping.");
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
                        CreateBillboard(zone);
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

            float now = Time.realtimeSinceStartup;
            bool doTextUpdate = now >= nextUpdateTime;
            if (doTextUpdate) nextUpdateTime = now + UpdateInterval;

            foreach (var bb in billboards)
            {
                if (bb.gateZone == null || bb.gateZone.gameObject == null || bb.rootObj == null) continue;

                try
                {
                    // Text meshes always face camera for readability
                    // (Cube backing stays static)
                    Vector3 toCam = cam.transform.position - bb.rootObj.transform.position;
                    if (toCam.sqrMagnitude > 0.01f)
                    {
                        Quaternion faceCamera = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                        if (bb.textNameObj != null) bb.textNameObj.transform.rotation = faceCamera;
                        if (bb.textSepObj != null) bb.textSepObj.transform.rotation = faceCamera;
                        if (bb.textContentObj != null) bb.textContentObj.transform.rotation = faceCamera;
                    }

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
