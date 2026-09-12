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

        private static void SetVisible(bool v)
        {
            if (v == visible && !(v && canvasObj == null)) return;
            visible = v;
            if (visible)
            {
                bool cockpitOk = EnsureCockpitPanel();
                if (cockpitOk) { cockpitObj.SetActive(true); nextTextRefresh = 0f; StarTruckMP.Log.LogInfo("JobBoardComputer: Cockpit-Display an."); }
                else { EnsureUI(); if (canvasObj != null) canvasObj.SetActive(true); nextTextRefresh = 0f; StarTruckMP.Log.LogInfo("JobBoardComputer: Overlay an (Fallback)."); }
            }
            else
            {
                if (cockpitObj != null) { cockpitObj.SetActive(false); StarTruckMP.Log.LogInfo("JobBoardComputer: Cockpit-Display aus."); }
                if (canvasObj != null) { canvasObj.SetActive(false); StarTruckMP.Log.LogInfo("JobBoardComputer: Overlay aus."); }
            }
        }

        // Wird aus TruckClient.Update() (Plugin.cs, PauseController-Postfix) aufgerufen.
        public static void Update()
        {
            CheckToggle();
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
            cockpitObj.transform.localPosition = new Vector3(0f, 0f, -0.05f);
            cockpitObj.transform.localRotation = Quaternion.identity;
            cockpitObj.transform.localScale = Vector3.one;
            var go = new GameObject("Text");
            go.transform.SetParent(cockpitObj.transform, false);
            cockpitText = go.AddComponent<TMPro.TextMeshPro>();
            cockpitText.fontSize = 0.02f;
            cockpitText.color = new Color(0.45f, 1f, 0.55f);
            cockpitText.alignment = TMPro.TextAlignmentOptions.TopLeft;
            cockpitText.text = "";
            cockpitText.ForceMeshUpdate();
            var rt = go.GetComponent<RectTransform>();
            if (rt != null) { rt.sizeDelta = new Vector2(1.6f, 1.2f); rt.anchoredPosition = new Vector2(-0.8f, 0.6f); }
            cockpitObj.SetActive(false);
            StarTruckMP.Log.LogInfo("JobBoardComputer: Cockpit-Panel erstellt (popupsRoot gefunden).");
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

            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                if (shown >= 10)
                {
                    sb.AppendLine("... und " + (count - shown) + " weitere (am Dock andocken)");
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

                    sb.Append((i + 1) + ". " + name);
                    if (credits > 0) sb.Append("  |  " + credits + " cr");
                    if (!string.IsNullOrEmpty(bay)) sb.Append("\n    Ziel-Bay: " + bay);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine((i + 1) + ". (Anzeige-Fehler: " + ex.Message + ")");
                }
            }

            sb.AppendLine();
            sb.AppendLine("[J] Schliessen");
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
    }
}
