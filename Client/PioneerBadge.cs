using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using StarTruckMP.Utilities;

namespace StarTruckMP.Encoding
{
    /// <summary>
    /// custom-build-348: Pioneer-Badge — rundes 'Pioneers Club'-Icon direkt neben dem
    /// Namenslabel ueber dem Truck eines Pioniers.
    ///
    /// Implementierung: kleines Textured-Quad als CHILD des Namenslabels (erbt dessen
    /// Billboard-Rotation + Position aus BillboardNameLabels). TMP hat keine Inline-Image-
    /// Faehigkeit ohne PackageManager — daher separates Quad. Groesse ~1.05x Label-Hoehe,
    /// mittig ueber dem Text. Sichtbar nur wenn pioneer==true. Saubere Zerstoerung mit dem
    /// Label (Kind-Objekt wird mit dem Parent zerstoert; gespeicherte Referenz wird trotzdem
    /// beim Label-Abbau mit aufgeraeumt).
    ///
    /// Nur am Namenslabel ueber dem Truck — bewusst NICHT im Chat, NICHT auf Map-Indikatoren.
    /// </summary>
    public static class PioneerBadge
    {
        private const string ResourceName = "StarTruckMP.pioneer_badge_256.png";
        private const string ShaderName = "UI/Default"; // URP-kompatibel, UniGLTF-frei

        private static Texture2D _texture;
        private static bool _textureAttempted;

        /// <summary>steamId -> letzte vom Server gemeldete Pioneer-Flag (SetPlayerSteamId-keyed via playerId).</summary>
        private static readonly Dictionary<ulong, bool> PioneerBySteamId = new();

        /// <summary>connectionId -> steamId (aus setPlayerSteamId-Broadcast am Client-Seitigen Handler).</summary>
        private static readonly Dictionary<ushort, ulong> SteamIdByPlayer = new();

        /// <summary>NameLabel-GameObject -> zugehoeriges Badge-GameObject (fuer Aufraeumen/Umbau).</summary>
        private static readonly Dictionary<int, GameObject> BadgeByLabel = new();

        /// <summary>
        /// Server-Meldung verarbeiten: (steamId, pioneer). Ruft der Client-Handler aus
        /// Client_MessageReceived (messageType.pioneerFlag) auf.
        /// </summary>
        public static void HandlePioneerFlag(ulong steamId, bool pioneer)
        {
            PioneerBySteamId[steamId] = pioneer;
            StarTruckMP.Log.LogInfo($"PioneerBadge: steamId {steamId} pioneer={pioneer}");
            // Bestehende Labels sofort aktualisieren (auch ohne Neuspawn).
            foreach (var kv in StarTruckClient.StarTruckClient.playerList)
            {
                var p = kv.Value;
                if (p.NameLabel != null && IsPioneer(p, kv.Key)) UpdateLabel(p, kv.Key);
            }
        }

        /// <summary>
        /// Ist der Spieler mit dieser playerInfo ein Pionier?
        /// custom-build-356: Fallback ueber die SteamIdByPlayer-Map, falls p.steamId noch
        /// nicht gefuellt ist (Label-Entstehung kommt oft VOR dem setPlayerSteamId-Broadcast).
        /// </summary>
        private static bool IsPioneer(playerInfo p, ushort playerId)
        {
            if (p.steamId != 0) return PioneerBySteamId.TryGetValue(p.steamId, out bool f) && f;
            return SteamIdByPlayer.TryGetValue(playerId, out ulong sid)
                   && PioneerBySteamId.TryGetValue(sid, out bool flag) && flag;
        }

        /// <summary>SteamID einer playerInfo zuordnen (vom setPlayerSteamId-Handler gerufen).</summary>
        public static void AssociateSteamId(ushort playerId, ulong steamId)
        {
            SteamIdByPlayer[playerId] = steamId;
            // playerInfo struct: steamId via playerList zurueckschreiben.
            if (StarTruckClient.StarTruckClient.playerList.TryGetValue(playerId, out var p))
            {
                p.steamId = steamId;
                StarTruckClient.StarTruckClient.playerList[playerId] = p;
                // custom-build-356 (FIX A): Zuordnung kann NACH dem Spawn eintreffen —
                // falls das Label bereits existiert, Badge sofort nachtraeglich anhaengen/aktualisieren.
                if (p.NameLabel != null) AttachOrUpdate(p.NameLabel, playerId);
            }
        }

        /// <summary>
        /// Nach jedem Label-(Neu)Bau aufrufen: haengt das Badge als Kind an / entfernt es.
        /// Gibt das Label-GameObject zurueck (durchreichend, damit die Aufrufer kompakt bleiben).
        /// </summary>
        public static GameObject AttachOrUpdate(GameObject nameLabel, ushort playerId)
        {
            try
            {
                if (nameLabel == null) { DetachFor(nameLabel); return null; }
                if (!StarTruckClient.StarTruckClient.playerList.TryGetValue(playerId, out var p)) return nameLabel;
                if (!IsPioneer(p, playerId)) { DetachFor(nameLabel); return nameLabel; }

                // Vorhandenes Badge nicht doppelt anlegen.
                int key = nameLabel.GetInstanceID();
                if (BadgeByLabel.TryGetValue(key, out var existing) && existing != null) return nameLabel;

                var badge = CreateBadge(nameLabel);
                if (badge != null) BadgeByLabel[key] = badge;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"PioneerBadge.AttachOrUpdate failed: {ex.Message}");
            }
            return nameLabel;
        }

        private static void UpdateLabel(playerInfo p, ushort playerId)
        {
            if (p.NameLabel == null) return;
            AttachOrUpdate(p.NameLabel, playerId);
        }

        /// <summary>Label wird zerstoert/neu gebaut: Badge-Referenz aufräumen.</summary>
        public static void DetachFor(GameObject nameLabel)
        {
            if (nameLabel == null) return;
            int key = nameLabel.GetInstanceID();
            BadgeByLabel.Remove(key); // Kind-Objekt stirbt mit dem Label automatisch.
        }

        private static GameObject CreateBadge(GameObject nameLabel)
        {
            var tex = GetTexture();
            if (tex == null)
            {
                if (!_textureAttempted)
                    StarTruckMP.Log.LogWarning("PioneerBadge: Textur nicht ladbar — Badge uebersprungen.");
                return null;
            }

            // Groesse: Badge ≈ Label-Hoehe. size/labelH sind WORLD units
            // (bounds.size.y). Das Label ist ein TMP-Klon mit localScale (5,5,5),
            // das Quad haengt aber als Kind daran — seine localScale/localPosition
            // muessen daher world→local umgerechnet werden (Division durch
            // parent-lossyScale), sonst wirken Werte 5x zu gross/falsch platziert.
            var bounds = ComputeWorldBounds(nameLabel, out string labelHsrc);
            float labelH = bounds.HasValue ? bounds.Value.size.y : 5f;
            StarTruckMP.Log.LogInfo($"PioneerBadge: labelH={labelH:F3}w (src={labelHsrc})");
            float py = Mathf.Max(0.0001f, nameLabel.transform.lossyScale.y);
            float px = Mathf.Max(0.0001f, nameLabel.transform.lossyScale.x);
            float worldSize = Mathf.Max(0.3f, labelH * 2.1f); // 2x von 1.05 — verdoppelt
            float worldY = labelH * 2.25f; // 3x von 0.75 — Badge haengt 3x hoeher ueber der Text-Mitte

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PioneerBadge_" + nameLabel.name;
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.layer = nameLabel.layer;

            var mr = quad.GetComponent<MeshRenderer>();
            // custom-build-355 (Bug D): Shader-Fallback-Kette — in IL2CPP koennen
            // UI/Default und Sprites/Default gestript sein; wir loggen, was gefunden
            // wurde, und fallen bis zum Namenslabel-Shader zurueck (der existiert
            // garantiert). Wenn ALLES null ist: Badge gar nicht anlegen (statt
            // unsichtbarem Quad).
            var shader = Shader.Find("UI/Default");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("TextMeshPro/Mobile/Forward");
            if (shader == null)
            {
                try
                {
                    var labelMr = nameLabel.GetComponentInChildren<MeshRenderer>();
                    if (labelMr != null && labelMr.sharedMaterial != null)
                        shader = labelMr.sharedMaterial.shader;
                }
                catch { }
            }
            StarTruckMP.Log.LogInfo($"PioneerBadge: shader found = '{(shader != null ? shader.name : "NULL")}'");
            if (shader == null)
            {
                StarTruckMP.Log.LogWarning("PioneerBadge: kein Shader verfuegbar (alle Fallbacks null) — Badge wird NICHT angelegt.");
                UnityEngine.Object.Destroy(quad);
                return null;
            }
            var mat = new Material(shader); // Material instanziieren — niemals sharedMaterial manipulieren
            mat.mainTexture = tex;
            mat.color = Color.white;
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", 0); // beidseitig — Billboard dreht sich eh
            mr.material = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            quad.transform.SetParent(nameLabel.transform, false);
            // WORLD-Offsets/-Groesse → LOCAL umrechnen (parent lossyScale, TMP-Klon = 5x):
            // y = halbe Badge-Hoehe ueber Label-Top + Luft, z leicht nach vorn gegen Z-Fighting.
            quad.transform.localPosition = new Vector3(0f, worldY / py, 0.05f);
            quad.transform.localScale = new Vector3(worldSize / px, worldSize / py, 1f);
            // Das Label selbst ist ein Billboard (LookRotation zur Kamera) — als Kind erbt das
            // Quad Rotation+Position. Quad-Mesh schaut +Z; LookRotation richtet +Z zur Kamera,
            // passt also direkt.
            quad.SetActive(true);
            StarTruckMP.Log.LogInfo($"PioneerBadge: Badge an '{nameLabel.name}' gehaengt (size={worldSize:F2}w off={worldY:F2}w scale={py:F1}).");
            return quad;
        }

        private static Bounds? ComputeWorldBounds(GameObject go, out string src)
        {
            src = "fallback";
            try
            {
                // custom-build-360: MeshRenderer.bounds ist beim frisch geklonten TMP-Label
                // noch 0 (Mesh nicht aktualisiert) — bevorzugt TMP preferredHeight messen
                // (lokal → world via lossyScale), renderer.bounds nur wenn plausibel (>0.5).
                var tmp = go.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (tmp != null)
                {
                    float worldH = tmp.preferredHeight * tmp.transform.lossyScale.y;
                    if (worldH > 0.05f)
                    {
                        var tb = tmp.textBounds; // lokal
                        if (tb.size.y > 0.01f)
                        {
                            float sx = Mathf.Max(0.0001f, tmp.transform.lossyScale.x);
                            src = "tmp";
                            return new Bounds(tmp.transform.position,
                                new Vector3(tb.size.x * sx, worldH, 0.1f));
                        }
                        src = "tmp";
                        return new Bounds(tmp.transform.position, new Vector3(1f, worldH, 0.1f));
                    }
                }
                var r = go.GetComponentInChildren<MeshRenderer>();
                if (r != null && r.bounds.size.y > 0.5f)
                {
                    src = "bounds";
                    return r.bounds;
                }
                // TMP-Pfad lieferte nichts Messbares und renderer.bounds ist ~0:
                // height über preferredHeight dennoch versuchen (ohne textBounds).
                if (tmp != null)
                {
                    float wh = tmp.preferredHeight * tmp.transform.lossyScale.y;
                    if (wh > 0.05f)
                    {
                        src = "tmp";
                        return new Bounds(tmp.transform.position, new Vector3(1f, wh, 0.1f));
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static Texture2D GetTexture()
        {
            if (_texture != null) return _texture;
            if (_textureAttempted) return null;
            _textureAttempted = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    StarTruckMP.Log.LogWarning($"PioneerBadge: EmbeddedResource '{ResourceName}' nicht gefunden.");
                    return null;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] png = ms.ToArray();
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!UnityEngine.ImageConversion.LoadImage(tex, png, false))
                {
                    StarTruckMP.Log.LogWarning("PioneerBadge: ImageConversion.LoadImage fehlgeschlagen.");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                _texture = tex;
                StarTruckMP.Log.LogInfo($"PioneerBadge: Textur geladen ({tex.width}x{tex.height}, {png.Length} Bytes).");
                return _texture;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"PioneerBadge: Textur-Laden fehlgeschlagen: {ex.Message}");
                return null;
            }
        }
    }
}