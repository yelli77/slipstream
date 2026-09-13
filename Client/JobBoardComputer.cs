using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Text;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Boardcomputer-Jobboard (custom-build-288): J-Toggle zeigt ein Screen-Space-Overlay
    /// mit den verfuegbaren Jobs des AKTUELLEN Sektors. Datenquelle ist der Live-State
    /// (ProceduralJobGenerator.GetAvailableJobs()) - derselbe, den auch das Dock-Jobboard
    /// zeigt und den JobBoardSync (build-287) zwischen allen Spielern synchronisiert.
    /// Kein Andocken noetig. Anzeige pro Job: displayName + Ziel-Bay + Credits.
    /// </summary>
    public static class JobBoardComputer
    {
        public static readonly KeyCode ToggleKey = KeyCode.J;

        private static GameObject canvasObj;
        private static TMPro.TextMeshProUGUI text;
        private static bool visible = false;
        private static float nextToggleCheck = 0f;
        private static float nextTextRefresh = 0f;
        // Cockpit-Panel (build-292): World-Space-TMP als Kind unter MonitorOverlaySwitcher.popupsRootTransform
                private static GameObject cockpitObj;
                private static TMPro.TextMeshPro cockpitText;

        public static void CheckToggle()
        {
            // FIX (build-290): GetKeyDown ist nur EINEN Frame true. Der fruehere 0,2s-Debounce
            // VOR dem Check hat ~95% aller Keydowns verschluckt (nur ein Treffer alle 12 Frames).
            // Toggle-Check jetzt JEDE FRAME; der Throttle bleibt nur fuer Heartbeat.
            bool jDown = Input.GetKeyDown(ToggleKey);

            if (StarTruckClient.client == null || !StarTruckClient.client.IsConnected) { if (visible) SetVisible(false); return; }
            if (!jDown) return;

            SetVisible(!visible);
        }

        // 307: interne Coroutine-Runner-Klasse (nur StartCoroutine durchreichen).
        // Muss injected werden (Plugin.cs: ClassInjector.RegisterTypeInIl2Cpp<CoroutineRunnerHelper>).
        public class CoroutineRunnerHelper : MonoBehaviour
        {
        }

        private static bool TryOpenGameJobBoard()
        {
            // (A) PRIMAERPFAAD: echtes Jobboard via MenuState.LoadAndShow
            try
            {
                var ms = com.monsterandmonster.Menu.MenuState.Get();
                StarTruckMP.Log.LogInfo("307 MenuState.Get() = " + (ms == null ? "null" : "nicht null"));
                if (ms != null)
                {
                    try
                    {
                        var runnerGO = new GameObject("StarTruckMP_CoroutineRunner307");
                        UnityEngine.Object.DontDestroyOnLoad(runnerGO);
                        var runner = runnerGO.AddComponent<CoroutineRunnerHelper>();
                        runner.StartCoroutine(ms.LoadAndShow("JobBoardScreen", null, null));
                        ScheduleJobBoardVerify();
                        return true;
                    }
                    catch (System.Exception ex2)
                    {
                        StarTruckMP.Log.LogWarning("307 LoadAndShow-Start fehlgeschlagen: " + ex2.Message);
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("307 MenuState-Pfad fehlgeschlagen: " + ex.Message);
            }
            // (B) FALLBACK: DevPanel_Cheats.ShowJobBoard()
            try
            {
                var devGO = new GameObject("StarTruckMP_DevPanel307");
                UnityEngine.Object.DontDestroyOnLoad(devGO);
                var dp = devGO.AddComponent<global::DevPanel_Cheats>();
                dp.ShowJobBoard();
                StarTruckMP.Log.LogInfo("307 (b) DevPanel-ShowJobBoard aufgerufen");
                return true;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("307 (b) DevPanel-Fallback fehlgeschlagen: " + ex.Message);
            }
            return false;
        }

        private static float jobBoardVerifyAt = -1f;

        private static void ScheduleJobBoardVerify()
        {
            jobBoardVerifyAt = UnityEngine.Time.time + 1f;
        }

        // Wird aus Update() aufgerufen (kein Coroutine-Runner noetig).
        private static void TickJobBoardVerify()
        {
            if (jobBoardVerifyAt < 0f || UnityEngine.Time.time < jobBoardVerifyAt) return;
            jobBoardVerifyAt = -1f;
            bool found = false;
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types = null;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t == null || t.Name != "JobBoardScreen" || !typeof(UnityEngine.Component).IsAssignableFrom(t)) continue;
                        try
                        {
                            var insts = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(t));
                            if (insts != null && insts.Length > 0) { found = true; break; }
                        }
                        catch { }
                    }
                    if (found) break;
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("307 JobBoardScreen-Suche fehlgeschlagen: " + ex.Message);
            }
            StarTruckMP.Log.LogInfo("307 JobBoardScreen-Instanz vorhanden: " + found);
        }

private static void SetVisible(bool v)
        {
            if (v == visible && !(v && canvasObj == null)) return;
            visible = v;
            if (visible)
            {
                // DIAGNOSE (Build-333): Board-Oeffnen -> Kennungs-Listen + Matchquote ins Log.
                try
                {
                    string sector = StarTruckClient.currentSector;
                    var jobsNow = global::ProceduralJobGenerator.GetAvailableJobs();
                    StarTruckMP.Log.LogInfo($"JobBoardComputer: {JobBoardIdSync.BuildDiagLine(sector, jobsNow)}");
                }
                catch (Exception diagEx) { StarTruckMP.Log.LogWarning($"JobBoardComputer: Diag-Zeile fehlgeschlagen: {diagEx.Message}"); }
                // 307: PRIMAER erst das echte Jobboard via MenuState (A), dann DevPanel-Fallback (B).
                if (TryOpenGameJobBoard())
                {
                    StarTruckMP.Log.LogInfo("JobBoardComputer: 307 Original-Jobboard geoeffnet.");
                }
                else
                {
                    EnsureUI(); if (canvasObj != null) canvasObj.SetActive(true); nextTextRefresh = 0f;
                    StarTruckMP.Log.LogInfo("JobBoardComputer: 307 Overlay-Fallback aktiv");
                }
            }
            else
            {
                TryInvokeGameJobBoard(false);
                if (canvasObj != null) { canvasObj.SetActive(false); StarTruckMP.Log.LogInfo("JobBoardComputer: Overlay aus."); }
            }
        }

        // Wird aus TruckClient.Update() (Plugin.cs, PauseController-Postfix) aufgerufen.
        public static void Update()
        {
            CheckToggle();
            TickJobBoardVerify();
            if (!visible) return;
            if (canvasObj == null && cockpitObj == null) return;
            if (canvasObj == null && cockpitObj != null && !cockpitObj.activeInHierarchy) return;
            if (Time.unscaledTime < nextTextRefresh) return;
            nextTextRefresh = Time.unscaledTime + 1.0f;
            var jobs = ProceduralJobGenerator.GetAvailableJobs();
            string sector = StarTruckClient.currentSector;
            string body = BuildBody(sector, jobs);
            if (text != null) { text.text = body; text.ForceMeshUpdate(); }
            if (cockpitText != null && cockpitObj != null && cockpitObj.activeInHierarchy) { cockpitText.text = body; cockpitText.ForceMeshUpdate(); }
        }

        private static void EnsureUI()
        {
            if (canvasObj != null) return;

            canvasObj = new GameObject("StarTruckMP_JobBoardComputer");
            UnityEngine.Object.DontDestroyOnLoad(canvasObj);
            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;
            canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();

            var bgGo = new GameObject("BG");
            bgGo.transform.SetParent(canvasObj.transform, false);
            var bgRT = bgGo.AddComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0.5f, 0.5f);
            bgRT.anchorMax = new Vector2(0.5f, 0.5f);
            bgRT.sizeDelta = new Vector2(620f, 700f);
            var bgImg = bgGo.AddComponent<UnityEngine.UI.Image>();
            bgImg.color = new Color(0.03f, 0.06f, 0.09f, 0.92f);
            bgImg.raycastTarget = false;

            TMPro.TextMeshProUGUI sourceTMP = FindSourceTMP();
            GameObject txtGo;
            if (sourceTMP != null)
            {
                txtGo = UnityEngine.Object.Instantiate(sourceTMP.gameObject, canvasObj.transform);
                txtGo.name = "Text";
            }
            else
            {
                txtGo = new GameObject("Text");
                txtGo.transform.SetParent(canvasObj.transform, false);
                txtGo.AddComponent<TMPro.TextMeshProUGUI>();
            }
            var rt = txtGo.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.SetParent(canvasObj.transform, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(580f, 660f);
                rt.anchoredPosition = Vector2.zero;
                rt.localScale = Vector3.one;
            }
            text = txtGo.GetComponent<TMPro.TextMeshProUGUI>();
            if (text != null)
            {
                text.text = "";
                text.fontSize = 20;
                text.color = Color.white;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.raycastTarget = false;
                text.enableWordWrapping = true;
                if (text.font == null && sourceTMP != null && sourceTMP.font != null)
                    text.font = sourceTMP.font;
                text.ForceMeshUpdate();
            }
            StarTruckMP.Log.LogInfo("JobBoardComputer: UI erstellt.");
        }

        private static bool EnsureCockpitPanel()
        {
            if (cockpitObj != null) return cockpitText != null;
            var switcher = UnityEngine.Object.FindObjectOfType<MonitorOverlaySwitcher>();
            if (switcher == null || switcher.popupsRootTransform == null) return false;
            var root = switcher.popupsRootTransform;
            cockpitObj = new GameObject("StarTruckMP_JobCockpitPanel");
            cockpitObj.transform.SetParent(root, false);
            cockpitObj.transform.localRotation = Quaternion.identity;
            cockpitObj.transform.localScale = Vector3.one;

            // 297: Anchoring - die Monitor-RenderTexture-Kamera (Camera_RenderToTexture_*)
            // suchen; das Panel wird deren CHILD, sodass die Monitor-Kamera es mit rendert
            // (Render-Textur-Pipeline des LLAMA-Displays). Floating Origin egal.
            UnityEngine.Transform anchorT = null;
            try
            {
                var cams = UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>();
                UnityEngine.Camera best = null;
                foreach (var c2 in cams)
                {
                    if (c2 == null) continue;
                    string cn = c2.gameObject.name;
                    if (cn.EndsWith("_Left")) { best = c2; break; }
                }
                if (best == null)
                {
                    foreach (var c2 in cams)
                    {
                        if (c2 == null) continue;
                        if (c2.gameObject.name.StartsWith("Camera_RenderToTexture")) { best = c2; break; }
                    }
                }
                if (best != null)
                {
                    anchorT = best.transform;
                    StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel anchored to monitor cam '" + best.gameObject.name + "'.");
                }
                else
                {
                    StarTruckMP.Log.LogWarning("JobBoardComputer: CockpitPanel keine Camera_RenderToTexture* gefunden - Fallback popupsRoot.");
                    anchorT = root;
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("CockpitPanel: Anchor-Suche fehlgeschlagen: " + ex.Message);
                anchorT = root;
            }
            cockpitObj.transform.SetParent(anchorT, false);
            // Panel ein Stueck VOR der Monitor-Kamera platzieren (in deren Sichtfeld, rendern von deren Perspektive)
            cockpitObj.transform.localPosition = new Vector3(0f, 0f, 1.2f);
            cockpitObj.transform.localRotation = Quaternion.identity;
            cockpitObj.transform.localScale = Vector3.one * 0.5f;
            // 298: Panel auf einen Layer setzen, den die Monitor-Kamera rendert (Culling Mask),
            // sonst rendert sie es nicht (Diagnose 297b: cam cullingMask=0x1000 vs panelLayer=0).
            try
            {
                var layerCam = anchorT.GetComponent<UnityEngine.Camera>();
                if (layerCam != null)
                {
                    for (int L = 0; L < 32; L++)
                    {
                        if ((layerCam.cullingMask & (1 << L)) != 0)
                        {
                            cockpitObj.layer = L;
                            var renders = cockpitObj.GetComponentsInChildren<UnityEngine.Renderer>(true);
                            foreach (var r in renders) r.gameObject.layer = L;
                            StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel Layer=" + L + " (" + UnityEngine.LayerMask.LayerToName(L) + "), children=" + renders.Length + ".");
                            break;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("CockpitPanel: Layer-Fix fehlgeschlagen: " + ex.Message);
            }
            // 297a/297b: Textgroesse an die Render-Textur der Monitor-Kamera anpassen + Diagnose
            try
            {
                var anchorCam = anchorT.GetComponent<UnityEngine.Camera>();
                if (anchorCam != null && anchorCam.targetTexture != null)
                {
                    float s = anchorCam.targetTexture.height / 1080f;
                    cockpitObj.transform.localScale = Vector3.one * s;
                    StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel scale=" + s.ToString("F4") + " (targetTexture " + anchorCam.targetTexture.width + "x" + anchorCam.targetTexture.height + ").");
                }
                if (anchorCam != null)
                {
                    StarTruckMP.Log.LogInfo("JobBoardComputer: MonitorCam targetTexture=" + (anchorCam.targetTexture != null ? anchorCam.targetTexture.width + "x" + anchorCam.targetTexture.height : "null")
                        + " cullingMask=0x" + anchorCam.cullingMask.ToString("X8")
                        + " camLayer=" + anchorCam.gameObject.layer
                        + " panelLayer=" + cockpitObj.layer);
                }
                else
                {
                    StarTruckMP.Log.LogWarning("JobBoardComputer: Anchor ist keine Kamera (297a-Diagnose unmoeglich).");
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("CockpitPanel: 297a-Diagnose fehlgeschlagen: " + ex.Message);
            }

            // Font sourcing: clone an existing TextMeshPro under popupsRoot (game font asset),
            // otherwise fresh AddComponent<TextMeshPro> renders NOTHING (no font in IL2CPP).
            TMPro.TextMeshPro cloned = null;
            try
            {
                int n = root.childCount;
                for (int i = 0; i < n && cloned == null; i++)
                {
                    Transform child = root.GetChild(i);
                    if (child == null) continue;
                    var tmps = child.GetComponentsInChildren<TMPro.TextMeshPro>(true);
                    if (tmps != null && tmps.Length > 0) cloned = tmps[0];
                }
            }
            catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: TMP-Suche fehlgeschlagen: " + ex.Message); }

            // Fallback 294: rekursiv ganze Szene nach IRGENDEINEM TMP suchen (3D oder UGUI) -
            // dessen font-Asset laesst sich auch auf ein frisches 3D-TMP uebertragen.
            TMPro.TextMeshProUGUI uguiFallback = null;
            if (cloned == null)
            {
                try
                {
                    var allT = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshPro>(true);
                    if (allT != null && allT.Length > 0 && allT[0] != null) cloned = allT[0];
                }
                catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: 3D-Szenen-Suche fehlgeschlagen: " + ex.Message); }
                if (cloned == null)
                {
                    try
                    {
                        var allU = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>(true);
                        if (allU != null && allU.Length > 0 && allU[0] != null) uguiFallback = allU[0];
                    }
                    catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: UGUI-Szenen-Suche fehlgeschlagen: " + ex.Message); }
                }
            }

            TMPro.TextMeshPro sourceTemplate = null;
            GameObject textGO;
            if (cloned == null && uguiFallback != null)
            {
                // Kein klonbares 3D-TMP, aber UGUI-TMP gefunden: frisches 3D-TMP + dessen Font uebernehmen.
                textGO = new GameObject("Text");
                textGO.transform.SetParent(cockpitObj.transform, false);
                cockpitText = textGO.AddComponent<TMPro.TextMeshPro>();
                if (uguiFallback.font != null) cockpitText.font = uguiFallback.font;
                StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel fontFallback=UGUI-TMP (font=" + (uguiFallback.font != null ? uguiFallback.font.name : "null") + ").");
            }
            else if (cloned != null)
            {
                // 295: NICHT mehr das ganze GameObject klonen (Klon blieb unsichtbar - Ursache unbekannt),
                // sondern frisches 3D-TMP + font/fontSharedMaterial der Vorlage uebernehmen.
                sourceTemplate = cloned;
                textGO = new GameObject("Text");
                textGO.transform.SetParent(cockpitObj.transform, false);
                cockpitText = textGO.AddComponent<TMPro.TextMeshPro>();
                if (cloned.font != null) cockpitText.font = cloned.font;
                // 302: channels-Eintraege lesen + Panel exakt wie einen echten Kanaleintrag anlegen
            try
            {
                Transform swL = null;
                {
                    var monCams = anchorT != null ? anchorT.parent : null;
                    if (monCams != null)
                    {
                        for (int si = 0; si < monCams.childCount; si++)
                        {
                            var sk = monCams.GetChild(si);
                            if (sk.name == "MonitorChannelSwitcher_Left") { swL = sk; break; }
                        }
                    }
                }
                if (swL != null)
                {
                    cockpitObj.transform.SetParent(swL, false);
                    // Position wie die der anderen Kinder (alle worldPos ~(46.8,-7.8,305.4), also localPos=0)
                    cockpitObj.transform.localPosition = Vector3.zero;
                    cockpitObj.transform.localRotation = Quaternion.identity;
                    cockpitObj.transform.localScale = Vector3.one;
                    cockpitObj.layer = swL.gameObject.layer;
                    StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel301 umgehängt unter '" + swL.name + "' (layer=" + swL.gameObject.layer + ", childCount=" + swL.childCount + ").");
                    }
                // Panel-Transform an channels[0]-Werte anpassen wird erst nach dem Log moeglich sein;
                // aber mindestens: Layer endgueltig auf den Kamera-Layer (11) erzwingen inkl. aller Kinder.
                try {
                    var monCamRef = anchorT != null ? anchorT.GetComponent<UnityEngine.Camera>() : null;
                    if (monCamRef != null) {
                        for (int LF = 0; LF < 32; LF++) {
                            if ((monCamRef.cullingMask & (1 << LF)) != 0) {
                                cockpitObj.transform.gameObject.layer = LF;
                                var allR = cockpitObj.transform.GetComponentsInChildren<UnityEngine.Renderer>(true);
                                foreach (var r in allR) r.gameObject.layer = LF;
                                var allT = cockpitObj.transform.GetComponentsInChildren<UnityEngine.Transform>(true);
                                foreach (var t in allT) t.gameObject.layer = LF;
                                StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel302 Layer-Final=" + LF + " (" + UnityEngine.LayerMask.LayerToName(LF) + "), children=" + allT.Length + ".");
                                break;
                            }
                        }
                    }
                } catch (System.Exception lfe) { StarTruckMP.Log.LogWarning("CockpitPanel: 302-Layer-Final fehlgeschlagen: " + lfe.Message); }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("CockpitPanel: 302 fehlgeschlagen: " + ex.Message);
            }
            // 299: Layer-Set NACH Text-Erstellung wiederholen (Text-GO + Renderer), da der
            // Layer-Fix oben vor dem Text-AddComponent lief (children=0, Text blieb Layer 0).
            try
            {
                var layerCam2 = anchorT != null ? anchorT.GetComponent<UnityEngine.Camera>() : null;
                if (layerCam2 != null)
                {
                    for (int L2 = 0; L2 < 32; L2++)
                    {
                        if ((layerCam2.cullingMask & (1 << L2)) != 0)
                        {
                            cockpitObj.layer = L2;
                            var renders2 = cockpitObj.GetComponentsInChildren<UnityEngine.Renderer>(true);
                            foreach (var r in renders2) r.gameObject.layer = L2;
                            StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel Layer-Reapply=" + L2 + " (" + UnityEngine.LayerMask.LayerToName(L2) + "), children=" + renders2.Length + ".");
                            break;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("CockpitPanel: Layer-Reapply fehlgeschlagen: " + ex.Message);
            }
            StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel fontClone=False, fontFromTemplate=" + (cloned.font != null ? cloned.font.name : "null") + " (Template='" + cloned.gameObject.name + "').");
            }
            else
            {
                textGO = new GameObject("Text");
                textGO.transform.SetParent(cockpitObj.transform, false);
                cockpitText = textGO.AddComponent<TMPro.TextMeshPro>();
            }
            if (cockpitText == null) { UnityEngine.Object.Destroy(cockpitObj); cockpitObj = null; return false; }
            // NOTES_WORLDSPACE_UI: clone silently carries over state - re-assert everything explicitly.
            cockpitText.enableAutoSizing = false;   // must be BEFORE fontSize, else fontSize is silently overridden
            cockpitText.fontSize = 0.02f;
            cockpitText.color = new Color(0.45f, 1f, 0.55f);
            cockpitText.alignment = TMPro.TextAlignmentOptions.TopLeft;
            cockpitText.enableWordWrapping = false;
            cockpitText.overflowMode = TMPro.TextOverflowModes.Overflow;
            if (sourceTemplate != null)
            {
                if (cockpitText.font == null && sourceTemplate.font != null)
                    cockpitText.font = sourceTemplate.font;
                try { if (sourceTemplate.fontSharedMaterial != null) cockpitText.fontSharedMaterial = sourceTemplate.fontSharedMaterial; }
                catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: fontSharedMaterial-Rebind fehlgeschlagen: " + ex.Message); }
            }
            // Letzter Fallback 294: irgendein geladenes TMP_FontAsset aus den Resources nehmen.
            if (cockpitText.font == null)
            {
                try
                {
                    var fonts = UnityEngine.Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0 && fonts[0] != null)
                    {
                        cockpitText.font = fonts[0];
                        StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel fontFallback=FontAsset (" + fonts[0].name + ").");
                    }
                }
                catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: FontAsset-Suche fehlgeschlagen: " + ex.Message); }
            }

            // Active/alpha state: Instantiate() preserves whatever the source had.
            textGO.SetActive(true);
            try
            {
                var cr = textGO.GetComponent<UnityEngine.CanvasRenderer>();
                if (cr != null) cr.SetAlpha(1f);
                else StarTruckMP.Log.LogInfo("JobBoardComputer: CockpitPanel kein CanvasRenderer am Text (OK fuer 3D-TMP).");
            }
            catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: SetAlpha fehlgeschlagen: " + ex.Message); }
            var cg = textGO.GetComponent<UnityEngine.CanvasGroup>();
            if (cg != null) cg.alpha = 1f;
            cockpitText.text = "";
            cockpitText.ForceMeshUpdate();
            var rt = textGO.GetComponent<RectTransform>();
            if (rt != null) { rt.sizeDelta = new Vector2(1.6f, 1.2f); rt.anchoredPosition = new Vector2(-0.8f, 0.6f); }
            cockpitObj.SetActive(false);
            try { UnityEngine.Canvas.ForceUpdateCanvases(); } catch (System.Exception ex) { StarTruckMP.Log.LogWarning("CockpitPanel: ForceUpdateCanvases fehlgeschlagen: " + ex.Message); }
            StarTruckMP.Log.LogInfo("JobBoardComputer: Cockpit-Panel erstellt (fontClone=" + (cloned != null) + ").");
            return true;
        }

        private static TMPro.TextMeshProUGUI FindSourceTMP()
        {
            var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
            if (allTMP == null) return null;
            foreach (var tmp in allTMP)
                if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                    return tmp;
            return null;
        }

        private static string BuildBody(string sector, Il2CppSystem.Collections.Generic.List<global::QuestInstance> jobs)
        {
            if (string.IsNullOrEmpty(sector) || sector == "none")
                return "BOARD-COMPUTER\n\nKein Sektor.";

            int count = (jobs != null) ? jobs.Count : 0;
            if (count == 0)
                return "BOARD-COMPUTER\nSektor: " + sector + "\n\nKeine Auftraege verfuegbar.\n\n[J] Schliessen";

            var sb = new StringBuilder();
            sb.AppendLine("== BOARD-COMPUTER ==");
            sb.AppendLine("Sektor: " + sector + " - " + count + " Auftraege");
            sb.AppendLine();

            // Build-333 (PLAN B Same-Seed lite): Kennungs-Filter - wenn frische Kennungen
            // vom Autoritaets-Client vorliegen, zeigen wir nur die Treffer an.
            var filterIdx = JobBoardIdSync.FilterIndices(sector, jobs);
            int filterCount = (filterIdx != null) ? filterIdx.Count : count;

            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                if (filterIdx != null && !filterIdx.Contains(i)) continue;
                if (shown >= 10)
                {
                    sb.AppendLine("... und " + Math.Max(0, filterCount - shown) + " weitere (am Dock andocken)");
                    break;
                }
                var job = jobs[i];
                shown++;
                if (job == null) continue;
                try
                {
                    string name = "Auftrag";
                    try { if (!string.IsNullOrEmpty(job.displayName)) name = job.displayName; } catch { }
                    int credits = 0;
                    try { credits = job.Credits(); } catch { }
                    string bay = "";
                    try { bay = job.DropOffBay(); } catch { }

                    sb.Append((shown + 1) + ". " + name);
                    if (credits > 0) sb.Append("  |  " + credits + " cr");
                    if (!string.IsNullOrEmpty(bay)) sb.Append("\n    Ziel-Bay: " + bay);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine((shown + 1) + ". (Anzeige-Fehler: " + ex.Message + ")");
                }
            }

            sb.AppendLine();
            sb.AppendLine("... und " + Math.Max(0, filterCount - shown) + " weitere (am Dock andocken)");
            sb.AppendLine();
            sb.AppendLine("[J] Schliessen");
            // DIAGNOSE (Build-333, Michael-Test): Kennungs-Listen + Matchquote direkt im Board.
            sb.AppendLine(JobBoardIdSync.BuildDiagLine(sector, jobs));
            return sb.ToString();
        }

        public static void Cleanup()
        {
            if (canvasObj != null)
            {
                UnityEngine.Object.Destroy(canvasObj);
                canvasObj = null;
                text = null;
            }
            if (cockpitObj != null)
            {
                UnityEngine.Object.Destroy(cockpitObj);
                cockpitObj = null;
                cockpitText = null;
            }
            visible = false;
        }
        // 306: Original-Jobboard. (a) DevPanel_Cheats-Instanz suchen (statisch verifizierter Host von m_openJobBoardScreenEvent)
        // und dessen GameEvent invoken. (b) Fallback: existierende JobBoardScreen-Instanz im UI suchen und deren GameObject aktivieren.
        // Ein GameEvent wird NICHT selbst instanziiert (onTriggered-Listener-Struktur unbekannt - Crash-Risiko).
        private static UnityEngine.Component _devPanelComp;
        private static UnityEngine.Object _jobBoardScreenObj;
        private static bool TryInvokeGameJobBoard(bool opening)
        {
            try
            {
                if (opening)
                {
                    // (a) DevPanel_Cheats ueber IL2CPP-Typnamen suchen (auch inactive, FindObjectsOfTypeAll)
                    if (_devPanelComp == null)
                    {
                        UnityEngine.Object[] insts = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            System.Type[] types = null;
                            try { types = asm.GetTypes(); } catch { continue; }
                            if (types == null) continue;
                            foreach (var t in types)
                            {
                                if (t == null || t.Name != "DevPanel_Cheats" || !typeof(UnityEngine.Component).IsAssignableFrom(t)) continue;
                                try { insts = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.From(t)); } catch { }
                                if (insts != null && insts.Length > 0)
                                {
                                    _devPanelComp = insts[0] as UnityEngine.Component;
                                    StarTruckMP.Log.LogInfo("JobBoardComputer: 306 (a) DevPanel_Cheats gefunden auf '" + (_devPanelComp != null ? _devPanelComp.gameObject.name : "?") + "' (insts=" + insts.Length + ").");
                                    break;
                                }
                            }
                            if (_devPanelComp != null) break;
                        }
                        if (_devPanelComp == null)
                            StarTruckMP.Log.LogWarning("JobBoardComputer: 306 (a) DevPanel_Cheats-Instanz nicht gefunden.");
                    }
                    if (_devPanelComp != null)
                    {
                        var hostT = _devPanelComp.GetIl2CppType();
                        var evF = hostT.GetField("m_openJobBoardScreenEvent");
                        if (evF != null)
                        {
                            var evObj = evF.GetValue(_devPanelComp);
                            if (evObj != null)
                            {
                                var evT = evObj.GetIl2CppType();
                                Il2CppSystem.Reflection.MethodInfo invM = null;
                                foreach (var m in evT.GetMethods())
                                    if (m.Name == "Invoke" && m.GetParameters().Length == 0) { invM = m; break; }
                                if (invM != null)
                                {
                                    invM.Invoke(evObj, null);
                                    StarTruckMP.Log.LogInfo("JobBoardComputer: 306 (a) DevPanel gefunden/invoked.");
                                    return true;
                                }
                            }
                        }
                        StarTruckMP.Log.LogWarning("JobBoardComputer: 306 (a) Event/Invoke nicht erreichbar am DevPanel.");
                    }
                    // (b) JobBoardScreen-Instanz direkt suchen (auch inactive) und GameObject aktivieren
                    UnityEngine.Object[] jbs = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        System.Type[] types = null;
                        try { types = asm.GetTypes(); } catch { continue; }
                        if (types == null) continue;
                        foreach (var t in types)
                        {
                            if (t == null || t.Name != "JobBoardScreen" || !typeof(UnityEngine.Component).IsAssignableFrom(t)) continue;
                            try { jbs = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.From(t)); } catch { }
                            if (jbs != null && jbs.Length > 0)
                            {
                                _jobBoardScreenObj = jbs[0];
                                break;
                            }
                        }
                        if (_jobBoardScreenObj != null) break;
                    }
                    if (_jobBoardScreenObj != null)
                    {
                        var comp = _jobBoardScreenObj as UnityEngine.Component;
                        if (comp != null)
                        {
                            var go = comp.gameObject;
                            if (!go.activeSelf) go.SetActive(true);
                            StarTruckMP.Log.LogInfo("JobBoardComputer: 306 (b) JobBoardScreen-GO aktiviert: '" + go.name + "'.");
                            return true;
                        }
                    }
                    StarTruckMP.Log.LogWarning("JobBoardComputer: 306 (b) JobBoardScreen-Instanz nicht gefunden.");
                    return false;
                }
                return false;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning("JobBoardComputer: 306 Original-Jobboard-Fehler: " + ex.Message);
                return false;
            }
        }

    }
}
