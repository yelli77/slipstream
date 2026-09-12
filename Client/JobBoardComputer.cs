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

        public static void CheckToggle()
        {
            if (Time.unscaledTime < nextToggleCheck) return;
            nextToggleCheck = Time.unscaledTime + 0.2f;

            if (StarTruckClient.client == null || !StarTruckClient.client.IsConnected) { SetVisible(false); return; }
            if (!Input.GetKeyDown(ToggleKey)) return;

            SetVisible(!visible);
        }

        private static void SetVisible(bool v)
        {
            if (v == visible && !(v && canvasObj == null)) return;
            visible = v;
            if (visible)
            {
                EnsureUI();
                if (canvasObj != null) canvasObj.SetActive(true);
                nextTextRefresh = 0f;
                StarTruckMP.Log.LogInfo("JobBoardComputer: Overlay an.");
            }
            else if (canvasObj != null)
            {
                canvasObj.SetActive(false);
                StarTruckMP.Log.LogInfo("JobBoardComputer: Overlay aus.");
            }
        }

        // Wird aus TruckClient.Update() (Plugin.cs, PauseController-Postfix) aufgerufen.
        public static void Update()
        {
            CheckToggle();
            if (!visible || canvasObj == null || !canvasObj.activeInHierarchy) return;
            if (Time.unscaledTime < nextTextRefresh) return;
            nextTextRefresh = Time.unscaledTime + 1.0f;
            RefreshText();
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

        private static TMPro.TextMeshProUGUI FindSourceTMP()
        {
            var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
            if (allTMP == null) return null;
            foreach (var tmp in allTMP)
                if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                    return tmp;
            return null;
        }

        private static void RefreshText()
        {
            if (text == null) return;

            string sector = StarTruckClient.currentSector;

            if (string.IsNullOrEmpty(sector) || sector == "none")
            {
                text.text = "BOARD-COMPUTER\n\nKein Sektor.";
                text.ForceMeshUpdate();
                return;
            }

            var jobs = ProceduralJobGenerator.GetAvailableJobs();
            int count = (jobs != null) ? jobs.Count : 0;

            if (count == 0)
            {
                text.text = "BOARD-COMPUTER\nSektor: " + sector + "\n\nKeine Auftraege verfuegbar.\n\n[J] Schliessen";
                text.ForceMeshUpdate();
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("== BOARD-COMPUTER ==");
            sb.AppendLine("Sektor: " + sector + " - " + count + " Auftraege");
            sb.AppendLine();

            for (int i = 0; i < count; i++)
            {
                var job = jobs[i];
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
            text.text = sb.ToString();
            text.ForceMeshUpdate();
        }

        public static void Cleanup()
        {
            if (canvasObj != null)
            {
                UnityEngine.Object.Destroy(canvasObj);
                canvasObj = null;
                text = null;
            }
            visible = false;
        }
    }
}
