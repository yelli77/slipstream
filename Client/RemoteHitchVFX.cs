using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace StarTruckMP
{
    /// <summary>
    /// Build 346: Reines Client-VFX für Remote-Hitch-Zustände. Wenn ein Remote-Spieler
    /// einen Trailer hitched/unhitched, erscheint bei den anderen Clients die native
    /// Hitch-Energie-Animation (MaglockHitchVFX) zwischen Remote-Truck-Heck und
    /// Trailer-Front — rein visuell: KEINE Physik, KEIN Joint, KEIN MaglockHitchPoint.
    ///
    /// Quelle des VFX-Prefabs: MaglockConnector.hitchVFX der LOKALEN Szene (der lokale
    /// Spieler muss mind. einmal gescannt/gehitcht haben, damit die Instanz existiert).
    /// Fallback: Wenn kein Prefab auffindbar -> still überspringen (kein Fehler,
    /// kein Placeholder), damit Empfänger ohne lokale Hitch-Erfahrung nichts kaputt-
    /// konfiguriertes sehen.
    ///
    /// Das VFX-Objekt wird per LateUpdate an (Truck-Heck <-> Trailer-Front) gesetzt,
    /// folgt also der Trailer-Interpolation, ohne die Physik/Interpolation des Trailers
    /// selbst zu stören (kein Parenting am interpolierten Transform nötig).
    /// </summary>
    public static class RemoteHitchVFX
    {
        private class VfxState
        {
            public GameObject Instance;
            public MaglockHitchVFX Behaviour;   // natives MaglockHitchVFX (interop, typed)
            public Transform SrcTruck;
            public Transform SrcTrailer;
            public bool Activated;
        }

        // playerId -> VFX-State
        private static readonly Dictionary<ushort, VfxState> active = new Dictionary<ushort, VfxState>();

        // Cached template objects (wiederverwendbar, müssen nicht per Call neu gesucht werden)
        private static GameObject cachedVfxPrefab = null;
        private static bool templateLookupDone = false;

        /// <summary>
        /// Zeigt (oder aktualisiert) das Remote-Hitch-VFX für einen Remote-Spieler.
        /// Wird bei Trailer-Spawn und bei jedem Hitch-Statuswechsel aufgerufen.
        /// No-op, wenn kein lokales hitchVFX-Prefab existiert (Fallback: still überspringen).
        /// </summary>
        public static void Show(ushort playerId, GameObject remoteTruck, GameObject remoteTrailer)
        {
            try
            {
                if (remoteTruck == null || remoteTrailer == null)
                {
                    Hide(playerId);
                    return;
                }

                var vfx = GetOrCreate(playerId, remoteTruck, remoteTrailer);
                if (vfx == null) return; // still skipped (kein Prefab gefunden)

                vfx.SrcTruck = remoteTruck.transform;
                vfx.SrcTrailer = remoteTrailer.transform;
                Activate(vfx, playerId);
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"RemoteHitchVFX.Show[{playerId}] failed: {ex.Message}");
                Hide(playerId);
            }
        }

        /// <summary>
        /// Entfernt das Remote-Hitch-VFX für einen Remote-Spieler (Unhitch / Trailer-Zerstörung).
        /// </summary>
        public static void Hide(ushort playerId)
        {
            if (!active.TryGetValue(playerId, out var vfx)) return;
            try
            {
                Deactivate(vfx);
                if (vfx.Instance != null) GameObject.Destroy(vfx.Instance);
                StarTruckMP.Log.LogDebug($"RemoteHitchVFX.Hide[{playerId}]: VFX despawned");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"RemoteHitchVFX.Hide[{playerId}] failed: {ex.Message}");
            }
            active.Remove(playerId);
        }

        /// <summary>
        /// Aufräumen, wenn ein Remote-Spieler komplett entfernt wird.
        /// </summary>
        public static void HideAll()
        {
            var keys = new List<ushort>(active.Keys);
            foreach (var k in keys) Hide(k);
        }

        // ---- Internals -------------------------------------------------------

        private static VfxState GetOrCreate(ushort playerId, GameObject truck, GameObject trailer)
        {
            if (active.TryGetValue(playerId, out var existing) && existing.Instance != null)
                return existing;

            // Vorhandenen State aufräumen (z.B. Trailer neu gespawnt)
            if (existing != null)
            {
                try { if (existing.Instance != null) GameObject.Destroy(existing.Instance); } catch { }
                active.Remove(playerId);
            }

            var prefab = FindVfxPrefab();
            if (prefab == null)
            {
                // Kein VFX-Prefab auffindbar (Empfänger hat noch nie lokal gehitcht):
                // still überspringen — kein Fehler, kein Placeholder (Absicht, siehe Klassendoc).
                StarTruckMP.Log.LogDebug($"RemoteHitchVFX[{playerId}]: no local hitchVFX prefab found, skipping VFX (silent fallback)");
                return null;
            }

            var instance = GameObject.Instantiate(prefab);
            instance.name = $"RemoteHitchVFX_{playerId}";
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, trailer.scene);
            instance.transform.SetParent(null);

            // Rein visuell: alle Collider, Joints und Gameplay-Behaviours auf dem VFX-Clone entfernen.
            StripNonVisual(instance);

            var state = new VfxState
            {
                Instance = instance,
                Behaviour = GetMaglockHitchVfxBehaviour(instance),
                SrcTruck = truck.transform,
                SrcTrailer = trailer.transform,
            };
            active[playerId] = state;
            StarTruckMP.Log.LogInfo($"RemoteHitchVFX[{playerId}]: VFX instance spawned (visual only)");
            return state;
        }

        private static void Activate(VfxState vfx, ushort playerId)
        {
            if (vfx.Instance == null) return;
            try
            {
                // Native Signatur (per Dekompilat bestätigt):
                //   Activate(Transform srcTransform, Vector3 srcWorldPos, Transform dstTransform, Vector3 dstWorldPos)
                // src = Remote-Truck(-Heck), dst = Trailer-Front. MaglockHitchVFX spannt in
                // LateUpdate selbst das Energieband zwischen den beiden Transforms.
                var srcT = FindTruckRearTransform(vfx.SrcTruck);
                var dstT = FindTrailerFrontTransform(vfx.SrcTrailer);
                if (srcT == null || dstT == null)
                {
                    srcT = vfx.SrcTruck.transform;
                    dstT = vfx.SrcTrailer.transform;
                }

                // Init() initialisiert Materialien/Animator; danach Aktivierung mit den
                // src/dst-Transforms + Weltpositionen (die Positionen sind die Startpunkte,
                // LateUpdate hält sie aktuell solange m_srcTransform/m_dstTransform gesetzt sind).
                vfx.Behaviour.Init();
                vfx.Behaviour.Activate(srcT, srcT.position, dstT, dstT.position);

                if (!vfx.Activated)
                {
                    vfx.Activated = true;
                    StarTruckMP.Log.LogInfo($"RemoteHitchVFX[{playerId}]: hitch VFX activated (src={srcT?.name}, dst={dstT?.name})");
                }
                vfx.Instance.SetActive(true);
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"RemoteHitchVFX.Activate[{playerId}] failed: {ex.Message}");
            }
        }

        private static void Deactivate(VfxState vfx)
        {
            if (vfx?.Instance == null) return;
            try
            {
                // Deactivate(bool instant = false) — sanftes Ausblenden
                vfx.Behaviour?.Deactivate(false);
                vfx.Activated = false;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"RemoteHitchVFX.Deactivate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pro Frame das VFX an die aktuelle interpolierte Position setzen
        /// (Truck-Heck <-> Trailer-Front), ohne die Trailer-Interpolation zu stören.
        /// Wird aus Client.Update() nach SmoothTrailerMovement() aufgerufen.
        /// </summary>
        public static void LateUpdate()
        {
            if (active.Count == 0) return;
            foreach (var kv in active)
            {
                var vfx = kv.Value;
                if (vfx?.Instance == null || !vfx.Instance.activeSelf) continue;
                try
                {
                    if (vfx.SrcTruck == null || vfx.SrcTrailer == null) { Hide(kv.Key); continue; }
                    var srcT = FindTruckRearTransform(vfx.SrcTruck);
                    var dstT = FindTrailerFrontTransform(vfx.SrcTrailer);
                    if (srcT == null || dstT == null) continue;

                    // VFX-Root zwischen src und dst setzen (Orientation egal — das native
                    // MaglockHitchVFX spannt in LateUpdate selbst das Band zwischen den Transforms).
                    vfx.Instance.transform.position = (srcT.position + dstT.position) * 0.5f;
                    vfx.Instance.transform.rotation = Quaternion.LookRotation(
                        dstT.position - srcT.position, Vector3.up);
                }
                catch { }
            }
        }

        // ---- Template lookup -------------------------------------------------

        /// <summary>
        /// Findet das native hitchVFX-Prefab: 1) MaglockConnector.hitchVFX der LOKALEN Szene,
        /// 2) Fallback: MaglockHitchVFX-Component irgendeines Objekts in der Szene.
        /// Null, wenn beides nicht existiert (Empfänger hat noch nie lokal gescannt/gehitcht).
        /// </summary>
        private static GameObject FindVfxPrefab()
        {
            if (cachedVfxPrefab != null) return cachedVfxPrefab;

            if (!templateLookupDone)
            {
                templateLookupDone = true;
                try
                {
                    // Local truck -> MaglockConnector -> hitchVFX
                    GameObject localTruck = Client.myTruck;
                    if (localTruck == null) localTruck = GameObject.Find("StarTruck(Clone)");
                    if (localTruck != null)
                    {
                        MaglockConnector connector = null;
                        foreach (var mb in localTruck.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null) continue;
                            if (mb.GetIl2CppType().Name == "MaglockConnector")
                            {
                                connector = mb.Cast<MaglockConnector>();
                                break;
                            }
                        }
                        if (connector != null)
                        {
                            // hitchVFX ist eine MaglockHitchVFX-Component (kein GameObject)
                            var vfxComp = connector.hitchVFX;
                            if (vfxComp != null && vfxComp.gameObject != null)
                            {
                                cachedVfxPrefab = vfxComp.gameObject;
                                StarTruckMP.Log.LogInfo($"RemoteHitchVFX: hitchVFX template found on local MaglockConnector ('{vfxComp.gameObject.name}')");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogDebug($"RemoteHitchVFX: connector.hitchVFX lookup failed: {ex.Message}");
                }

                if (cachedVfxPrefab == null)
                {
                    // Fallback: irgendein MaglockHitchVFX in der Szene (z.B. NPC-Truck)
                    try
                    {
                        foreach (var mb in GameObject.FindObjectsOfType<MonoBehaviour>(true))
                        {
                            if (mb == null) continue;
                            if (mb.GetIl2CppType().Name == "MaglockHitchVFX")
                            {
                                cachedVfxPrefab = mb.gameObject;
                                StarTruckMP.Log.LogInfo($"RemoteHitchVFX: hitchVFX template found via scene MaglockHitchVFX ('{mb.gameObject.name}')");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogDebug($"RemoteHitchVFX: scene MaglockHitchVFX lookup failed: {ex.Message}");
                    }
                }
            }
            return cachedVfxPrefab;
        }

        // ---- Transform helpers -----------------------------------------------

        private static Transform FindTruckRearTransform(Transform truck)
        {
            // Truck-Heck: bevorzugt Native-Heck-Anchor (MaglockConnector.truckRearmostTransformBinding /
            // trailerRearAnchor). Einfacher, stabiler Pfad: tiefster Punkt entlang -forward des Trucks.
            // Wir nehmen schlicht den Truck-Root — MaglockHitchVFX spannt in LateUpdate zwischen
            // src und dst, exakte Heck-Anchor-Position ist nicht kritisch fürs rein visuelle Band.
            return truck;
        }

        private static Transform FindTrailerFrontTransform(Transform trailer)
        {
            // Trailer-Front: Kind-Anchor "HitchPoint"/"hitch" bevorzugt, sonst Trailer-Root.
            try
            {
                foreach (Transform child in trailer)
                {
                    var n = child.name.ToLowerInvariant();
                    if (n.Contains("hitch") || n.Contains("anchor") || n.Contains("front"))
                        return child;
                }
            }
            catch { }
            return trailer;
        }

        // ---- Cleanup helpers ---------------------------------------------------

        /// <summary>
        /// Entfernt ALLE nicht-visuellen Komponenten vom VFX-Clone: Collider, Rigidbodies,
        /// Joints, MaglockHitchPoint & Co. — das VFX darf KEINE Physik/Kein Gameplay-Verhalten haben.
        /// </summary>
        private static void StripNonVisual(GameObject go)
        {
            try
            {
                foreach (var col in go.GetComponentsInChildren<Collider>(true))
                    try { GameObject.Destroy(col); } catch { }

                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                    try { GameObject.Destroy(rb); } catch { }

                foreach (var j in go.GetComponentsInChildren<ConfigurableJoint>(true))
                    try { GameObject.Destroy(j); } catch { }
                foreach (var j in go.GetComponentsInChildren<FixedJoint>(true))
                    try { GameObject.Destroy(j); } catch { }
                foreach (var j in go.GetComponentsInChildren<CharacterJoint>(true))
                    try { GameObject.Destroy(j); } catch { }
                foreach (var j in go.GetComponentsInChildren<HingeJoint>(true))
                    try { GameObject.Destroy(j); } catch { }

                // Gameplay-Behaviours auf dem VFX-Clone deaktivieren (MaglockHitchVFX selbst und
                // MaglockGlowVFX bleiben aktiv — sie sind die eigentliche Animation).
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var tname = mb.GetIl2CppType().Name;
                    if (tname == "MaglockHitchVFX" || tname == "MaglockGlowVFX" || tname == "MaglockGlowTrailerVFX")
                        continue;
                    // Alles andere (MaglockHitchPoint, MaglockConnector, EvOverlapNotifier, ...)
                    // gehört nicht auf ein rein-visuelles Objekt.
                    try { GameObject.Destroy(mb); } catch { }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogDebug($"RemoteHitchVFX.StripNonVisual: {ex.Message}");
            }
        }

        private static MaglockHitchVFX GetMaglockHitchVfxBehaviour(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null && mb.GetIl2CppType().Name == "MaglockHitchVFX")
                    return mb.Cast<MaglockHitchVFX>();
            }
            return null;
        }
    }
}
