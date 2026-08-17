using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

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

        // Reflection cache for DockingBay.m_dockingBayGroups (Il2Cpp FIELD, type List<DockingBayGroup>)
        // and Station.DockingBayGroup.amenityType (Il2Cpp FIELD, type StationAmenity enum).
        private static MemberInfo fi_dockingBayGroups = null;
        private static MemberInfo fi_groupAmenityType = null;
        private static bool groupReflectionSearched = false;

        // StationAmenity.JobsBoard == 1 (None=0). Only this bay type is the Auftragsboerse.
        private static readonly int AmenityJobsBoard = 1;

        /// <summary>
        /// Returns true if the DockingBay belongs to at least one DockingBayGroup whose
        /// amenityType == StationAmenity.JobsBoard (the job board / Auftragsboerse).
        /// Uses reflection because DockingBayGroup is a nested Il2Cpp type we can't
        /// reference at compile time.
        /// </summary>
        // Finds a member (field OR property getter) by name across the type and all base types.
        // Il2CppInterop exposes some Il2Cpp fields as .NET properties (e.g. DockingBayGroups,
        // amenityType), so we must check both.
        private static MemberInfo FindMember(System.Type t, string name)
        {
            var cur = t;
            while (cur != null && cur != typeof(object))
            {
                var f = cur.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead) return p;
                cur = cur.BaseType;
            }
            // Last resort: case-insensitive scan
            cur = t;
            while (cur != null && cur != typeof(object))
            {
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) return f;
                foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (p.CanRead && p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) return p;
                cur = cur.BaseType;
            }
            return null;
        }

        private static object GetMemberValue(MemberInfo m, object target)
        {
            if (m is FieldInfo fi) return fi.GetValue(target);
            if (m is PropertyInfo pi) return pi.GetGetMethod(true).Invoke(target, null);
            return null;
        }
        // Reads an Il2Cpp value robustly. Tries, in order:
        //   1. Native field read via il2cpp_field_get_value + NativeFieldInfoPtr_<name>
        //      (works at C++ level regardless of .NET proxy type hierarchy).
        //   2. Property getter — use Il2CppObjectBase ptr to avoid target-type mismatch
        //      when property is declared on a base class (e.g. Station.get_DockingBayGroups
        //      called on a DockingBay proxy).
        //   3. Reflection GetValue (last resort — may fail if declaring type != runtime type).
        private static unsafe object ReadIl2CppField(MemberInfo member, object target)
        {
            string memberName = member?.Name ?? "?";
            var il2cppObj = target as Il2CppObjectBase;

            // ── Path 1: Native field read via il2cpp_field_get_value ──
            var fi = member as FieldInfo;
            if (fi != null && il2cppObj != null)
            {
                var declType = fi.DeclaringType;
                var nativeField = declType?.GetField("NativeFieldInfoPtr_" + fi.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (nativeField != null)
                {
                    try
                    {
                        var nativeFieldPtr = (System.IntPtr)nativeField.GetValue(null);
                        var objPtr = IL2CPP.Il2CppObjectBaseToPtr(il2cppObj);
                        StarTruckMP.Log.LogInfo($"DockingBayHUD.ReadIl2CppField '{memberName}': native-read objPtr={objPtr!=System.IntPtr.Zero} nativePtr={nativeFieldPtr!=System.IntPtr.Zero}");
                        if (objPtr != System.IntPtr.Zero && nativeFieldPtr != System.IntPtr.Zero)
                        {
                            int ptrSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(System.IntPtr));
                            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(ptrSize);
                            try
                            {
                                unsafe
                                {
                                    IL2CPP.il2cpp_field_get_value(objPtr, nativeFieldPtr, (void*)buf);
                                }
                                var fieldValPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                                StarTruckMP.Log.LogInfo($"DockingBayHUD.ReadIl2CppField '{memberName}': fieldValPtr={fieldValPtr!=System.IntPtr.Zero}");
                                if (fieldValPtr != System.IntPtr.Zero)
                                {
                                    return System.Activator.CreateInstance(fi.FieldType, new object[] { fieldValPtr });
                                }
                            }
                            finally
                            {
                                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                            }
                        }
                    }
                    catch (Exception nex)
                    {
                        StarTruckMP.Log.LogWarning($"DockingBayHUD.ReadIl2CppField '{memberName}': native-read failed: {nex.Message}");
                    }
                }
                else
                {
                    StarTruckMP.Log.LogInfo($"DockingBayHUD.ReadIl2CppField '{memberName}': no NativeFieldInfoPtr_ on {declType?.FullName}");
                }
            }

            // ── Path 2: Property getter via Il2CppObjectBase ptr ──
            // When the property is declared on a base class (e.g. Station) but the target
            // is a derived proxy (e.g. DockingBay), getter.Invoke(target) fails with
            // "Object does not match target type." Workaround: pass the Il2CppObjectBase
            // itself — Il2CppInterop's generated getter accepts it because it only needs
            // the native ptr, not the .NET wrapper type.
            if (member is PropertyInfo pi && pi.CanRead)
            {
                try
                {
                    var getter = pi.GetGetMethod(true);
                    if (getter != null)
                    {
                        // Prefer Il2CppObjectBase as target to avoid type mismatch
                        object invokeTarget = il2cppObj != null ? il2cppObj : target;
                        var result = getter.Invoke(invokeTarget, null);
                        StarTruckMP.Log.LogInfo($"DockingBayHUD.ReadIl2CppField '{memberName}': Property-Getter OK, result={result!=null} type={result?.GetType()?.FullName}");
                        return result;
                    }
                }
                catch (Exception pex)
                {
                    StarTruckMP.Log.LogWarning($"DockingBayHUD.ReadIl2CppField '{memberName}': Property-Getter failed: {pex.InnerException?.Message ?? pex.Message}");
                }
            }

            // ── Path 3: Reflection GetValue (last resort) ──
            try
            {
                var fallback = GetMemberValue(member, target);
                StarTruckMP.Log.LogInfo($"DockingBayHUD.ReadIl2CppField '{memberName}': reflection fallback result={fallback!=null} type={fallback?.GetType()?.FullName}");
                return fallback;
            }
            catch (Exception fex)
            {
                StarTruckMP.Log.LogWarning($"DockingBayHUD.ReadIl2CppField '{memberName}': ALL paths failed: {fex.Message}");
                return null;
            }
        }


        private static bool IsJobsBoard(DockingBay bay)
        {
            try
            {
                if (!groupReflectionSearched)
                {
                    groupReflectionSearched = true;
                    var bayType = bay.GetType();
                    // m_dockingBayGroups is declared on Station (the base class of DockingBay),
                    // so search the Station type for it. Fall back to bayType if Station not found.
                    var stationType = TryFindType("Station");
                    fi_dockingBayGroups = stationType != null
                        ? FindMember(stationType, "m_dockingBayGroups")
                        : FindMember(bayType, "m_dockingBayGroups");
                    // DockingBayGroup is nested in Station. Find Station, then its nested type.
                    var groupType = stationType != null
                        ? stationType.GetNestedType("DockingBayGroup", BindingFlags.Public | BindingFlags.NonPublic)
                        : null;
                    if (groupType != null)
                    {
                        fi_groupAmenityType = FindMember(groupType, "amenityType");
                    }
                    StarTruckMP.Log.LogInfo($"DockingBayHUD.IsJobsBoard reflection: groupsField={(fi_dockingBayGroups!=null)}, amenityField={(fi_groupAmenityType!=null)}, stationType={(stationType!=null)}, groupType={(groupType!=null)}");
                }
                if (fi_dockingBayGroups == null || fi_groupAmenityType == null) return false;

                var groupsObj = ReadIl2CppField(fi_dockingBayGroups, bay);
                StarTruckMP.Log.LogInfo($"DockingBayHUD.DBGRP groupsObj={groupsObj!=null} type={(groupsObj?.GetType()?.FullName)}");
                if (groupsObj == null) return false;

                // Il2Cpp List: use Count + indexer via reflection
                var listType = groupsObj.GetType();
                var countProp = listType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                StarTruckMP.Log.LogInfo($"DockingBayHUD.DBG countProp={countProp!=null} listType={listType?.FullName}");
                if (countProp == null) return false;
                int count = (int)countProp.GetValue(groupsObj);
                StarTruckMP.Log.LogInfo($"DockingBayHUD.DBG groups count={count}");
                var itemMethod = listType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                if (itemMethod == null) return false;

                for (int i = 0; i < count; i++)
                {
                    var grp = itemMethod.Invoke(groupsObj, new object[] { i });
                    StarTruckMP.Log.LogInfo($"DockingBayHUD.DBG group[{i}] grp={(grp!=null)} type={(grp?.GetType()?.FullName)}");
                    if (grp == null) continue;
                    var amenityVal = ReadIl2CppField(fi_groupAmenityType, grp);
                    StarTruckMP.Log.LogInfo($"DockingBayHUD.DBG group[{i}] amenityVal={(amenityVal!=null)} aType={(amenityVal?.GetType()?.FullName)}");
                    if (amenityVal == null) continue;
                    // amenityVal is an Il2CppSystem.Enum — get numeric value robustly
                    int numeric = 0;
                    try { numeric = Convert.ToInt32(amenityVal); }
                    catch
                    {
                        // Il2Cpp enums may expose value__ or need .value
                        var vfield = amenityVal.GetType().GetField("value__", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (vfield != null) numeric = Convert.ToInt32(vfield.GetValue(amenityVal));
                    }
                    StarTruckMP.Log.LogInfo($"DockingBayHUD.DBG group[{i}] numeric={numeric} (want {AmenityJobsBoard})");
                    if (numeric == AmenityJobsBoard) return true;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"DockingBayHUD.IsJobsBoard error: {ex.Message}");
            }
            return false;
        }

        private static System.Type TryGetNestedType(System.Type parent, string name)
        {
            try
            {
                var nested = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var n in nested)
                    if (n.Name == name) return n;
            }
            catch { }
            return null;
        }

        private static System.Type TryFindType(string typeName)
        {
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(typeName, false, false);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

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

                int jobsBoardCount = 0;
                foreach (var bay in allBays)
                {
                    if (bay == null || bay.gameObject == null) continue;
                    try
                    {
                        // TODO: re-enable JobsBoard filter once IL2CPP reflection is sorted
                        // if (!IsJobsBoard(bay)) continue;
                        CreateMarker(bay, sourceTMP);
                        jobsBoardCount++;
                    }
                    catch (Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"DockingBayHUD: marker creation failed for '{bay.gameObject.name}': {ex.Message}");
                    }
                }
                StarTruckMP.Log.LogInfo($"DockingBayHUD: {jobsBoardCount} JobsBoard marker(s) created.");

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
            

            Vector3 camPos = gameCam.transform.position;
            Vector3 camForward = gameCam.transform.forward;
            float screenW = Screen.width;
            float screenH = Screen.height;

            // Distance text + color fade are throttled (~20 Hz) to avoid string allocations
            // every frame. The actual marker POSITION is updated every frame so it glides
            // smoothly instead of stuttering at 20 Hz (the "huepfen" effect).
            bool doTextUpdate = Time.realtimeSinceStartup >= nextUpdateTime;
            if (doTextUpdate) nextUpdateTime = Time.realtimeSinceStartup + UpdateInterval;

            foreach (var m in markers)
            {
                if (m.bay == null || m.bay.gameObject == null || m.rootObj == null) continue;

                try
                {
                    // World position accounting for floating origin
                    Vector3 bayWorldPos = m.bay.transform.position;
                    float distance = Vector3.Distance(camPos, bayWorldPos);

                    // Format distance — throttled to avoid per-frame string allocation
                    if (doTextUpdate)
                    {
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
                    }

                    // Project to screen
                    Vector3 screenPos3 = gameCam.WorldToScreenPoint(bayWorldPos);
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
