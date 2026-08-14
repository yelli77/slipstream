using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using SC = StarTruckMP.StarTruckClient.StarTruckClient;

namespace StarTruckMP.MainMenu
{
    /// <summary>
    /// Fuegt dem Hauptmenue einen Online/Offline-Umschalter hinzu. Wird bewusst als komplett
    /// NEUES, eigenes UI-Element gebaut (nicht als Klon des Optionen-Buttons mit anschliessendem
    /// Ausschlachten) - ein Klon des MenuButton-Skripts bringt eine im Editor fest verdrahtete
    /// ("persistente") Navigation zum Optionen-Screen mit, die sich zur Laufzeit nicht sauber
    /// entfernen laesst, und das nachtraegliche Entfernen/Ersetzen der Komponenten hat auch die
    /// Klickbarkeit (Raycast-Ziel) kaputtgemacht. Optik (Hintergrund-Sprite, Schriftart etc.) wird
    /// vom Optionen-Button uebernommen, aber alle Komponenten sind frisch und ohne Altlasten.
    ///
    /// Der Zustand wird in einer eigenen, kleinen Textdatei neben der Mod-DLL gespeichert -
    /// bewusst NICHT ueber BepInEx.Configuration, damit es keine automatisch generierte/
    /// erweiterbare Config-Datei mit allen moeglichen Eintraegen gibt.
    /// </summary>
    public static class OnlineModeToggle
    {
        private static readonly string StateFilePath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
            "slipstream-mode.txt");

        public static bool OnlineModeEnabled { get; private set; } = LoadMode();

        private static Button toggleButtonInstance;
        private static TMP_Text toggleLabel;

        private static bool LoadMode()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var content = File.ReadAllText(StateFilePath).Trim();
                    return !content.Equals("offline", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* Default (online) bei jeglichem Lesefehler */ }
            return true;
        }

        private static void SaveMode()
        {
            try
            {
                File.WriteAllText(StateFilePath, OnlineModeEnabled ? "online" : "offline");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"Konnte Online/Offline-Modus nicht speichern: {ex.Message}");
            }
        }

        /// <summary>
        /// Sucht unter dem gegebenen Screen (Hauptmenue, Pause-Menue, ...) per Text "Optionen"
        /// nach einem MenuButton, der als optischer Templatespender dient. Generisch gehalten, weil
        /// nicht jeder Screen (z.B. PauseScreen) eine oeffentliche optionsButton-Objektreferenz hat
        /// wie MainMenuScreen - hier wird stattdessen zur Laufzeit die komplette Button-Liste des
        /// Screens durchsucht.
        /// </summary>
        private static MenuButton FindOptionsButtonTemplate(Component screen)
        {
            var allButtons = screen.GetComponentsInChildren<MenuButton>(true);
            foreach (var b in allButtons)
            {
                var t = b.GetComponentInChildren<TMP_Text>(true);
                if (t != null && !string.IsNullOrEmpty(t.text) && t.text.Trim().Equals("Optionen", StringComparison.OrdinalIgnoreCase))
                {
                    return b;
                }
            }
            // Notloesung: irgendeinen Button als Templatespender nehmen, falls "Optionen" mal
            // anders heisst oder nicht gefunden wird - besser ein optisch nicht 100% passender
            // Button als gar keiner.
            return allButtons.Length > 0 ? allButtons[0] : null;
        }

        public static void CreateToggleButton(Component screen)
        {
            // Unity's == ueberladen erkennt auch "wurde inzwischen zerstoert" korrekt (nicht nur
            // C#-null) - falls der Screen neu erstellt wurde, wird hier neu angelegt.
            if (toggleButtonInstance != null)
            {
                UpdateLabel();
                return;
            }

            var template = FindOptionsButtonTemplate(screen);
            if (template == null)
            {
                StarTruckMP.Log.LogWarning("OnlineModeToggle: kein passender Template-Button gefunden, Button wird nicht erstellt.");
                return;
            }

            var templateGO = template.gameObject;
            var templateRect = templateGO.GetComponent<RectTransform>();
            var templateImage = templateGO.GetComponent<Image>();
            var templateText = templateGO.GetComponentInChildren<TMP_Text>();
            var templateLayoutElement = templateGO.GetComponent<LayoutElement>();

            // Root-Objekt: RectTransform + Image + Button in einem Rutsch anlegen.
            var go = new GameObject("SlipstreamModeButton");
            go.AddComponent<RectTransform>();
            go.AddComponent<Image>();
            go.AddComponent<Button>();
            go.transform.SetParent(template.transform.parent, false);
            go.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            var rect = go.GetComponent<RectTransform>();
            if (templateRect != null)
            {
                rect.anchorMin = templateRect.anchorMin;
                rect.anchorMax = templateRect.anchorMax;
                rect.pivot = templateRect.pivot;
                rect.sizeDelta = templateRect.sizeDelta;
                rect.localScale = templateRect.localScale;
            }

            var image = go.GetComponent<Image>();
            if (templateImage != null)
            {
                image.sprite = templateImage.sprite;
                image.color = templateImage.color;
                image.type = templateImage.type;
                image.material = templateImage.material;
                image.raycastTarget = true;
            }

            if (templateLayoutElement != null)
            {
                var le = go.AddComponent<LayoutElement>();
                le.preferredWidth = templateLayoutElement.preferredWidth;
                le.preferredHeight = templateLayoutElement.preferredHeight;
                le.minWidth = templateLayoutElement.minWidth;
                le.minHeight = templateLayoutElement.minHeight;
            }

            // Text-Kindobjekt, ueber das ganze Root gespannt.
            var textGO = new GameObject("Label");
            textGO.AddComponent<RectTransform>();
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGO.AddComponent<TextMeshProUGUI>();
            if (templateText != null)
            {
                text.font = templateText.font;
                text.fontSize = templateText.fontSize;
                text.color = templateText.color;
                text.alignment = templateText.alignment;
                text.fontStyle = templateText.fontStyle;
            }

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            if (template.TryGetComponent<Selectable>(out var templateSelectable))
            {
                button.colors = templateSelectable.colors;
            }
            button.onClick.AddListener((UnityAction)OnToggleClicked);

            toggleLabel = text;
            toggleButtonInstance = button;
            UpdateLabel();

            StarTruckMP.Log.LogInfo("OnlineModeToggle: Button im Hauptmenue erstellt.");
        }

        private static void OnToggleClicked()
        {
            OnlineModeEnabled = !OnlineModeEnabled;
            SaveMode();
            UpdateLabel();
            StarTruckMP.Log.LogInfo($"Modus umgeschaltet: {(OnlineModeEnabled ? "Online" : "Offline")}");
        }

        public static void UpdateLabel()
        {
            if (toggleLabel == null) return;

            if (!OnlineModeEnabled)
            {
                toggleLabel.text = "Offline";
                return;
            }

            // Solange online, aber noch nicht (oder nicht mehr) tatsaechlich verbunden, nur "Online"
            // zeigen. Sobald eine echte Verbindung besteht, den Spielernamen mit anzeigen.
            bool isConnected = false;
            string playerName = "";
            try
            {
                isConnected = SC.client != null && SC.client.IsConnected;
                playerName = SC.myPlayerName;
            }
            catch { /* Client evtl. noch nicht initialisiert, einfach "Online" zeigen */ }

            if (isConnected && !string.IsNullOrEmpty(playerName))
            {
                toggleLabel.text = $"Online - connected as {playerName} at Slipstream";
            }
            else
            {
                toggleLabel.text = "Online";
            }
        }
    }
}
