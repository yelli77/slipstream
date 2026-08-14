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
                    return content.Equals("online", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* Default (offline) bei jeglichem Lesefehler */ }
            return false;
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

            // "Optionen" kommt offenbar mehrfach vor (z.B. auch als kleiner Icon-Button in einer
            // unteren Leiste). RectTransform.rect.width waere zum Zeitpunkt von Awake() evtl. noch
            // nicht vom Unity-Layout-System berechnet (unzuverlaessiges Timing) - deshalb stattdessen
            // ueber die Geschwisteranzahl im selben Elternobjekt entscheiden: die echte, vertikale
            // Menueliste hat mehrere Eintraege (Optionen, Spiel laden, Spiel speichern, ...), eine
            // kleine Symbolleiste wie unten (Fortsetzen/Fotomodus) hat nur 1-2. Das ist unabhaengig
            // vom Layout-Timing sofort verlaesslich verfuegbar.
            MenuButton best = null;
            int bestSiblingButtonCount = -1;

            foreach (var b in allButtons)
            {
                var t = b.GetComponentInChildren<TMP_Text>(true);
                if (t == null || string.IsNullOrEmpty(t.text) || !t.text.Trim().Equals("Optionen", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parent = b.transform.parent;
                int siblingButtonCount = parent != null ? parent.GetComponentsInChildren<MenuButton>(true).Length : 1;
                if (siblingButtonCount > bestSiblingButtonCount)
                {
                    bestSiblingButtonCount = siblingButtonCount;
                    best = b;
                }
            }

            if (best != null)
            {
                StarTruckMP.Log.LogInfo($"OnlineModeToggle: Template-Button 'Optionen' gefunden (Geschwister-Buttons im Elternobjekt={bestSiblingButtonCount}).");
                return best;
            }

            // Notloesung: den Button mit den meisten Geschwister-Buttons insgesamt nehmen (= mit
            // hoher Wahrscheinlichkeit die echte Liste, nicht eine kleine Symbolleiste).
            MenuButton fallback = null;
            int fallbackSiblingCount = -1;
            foreach (var b in allButtons)
            {
                var parent = b.transform.parent;
                int siblingButtonCount = parent != null ? parent.GetComponentsInChildren<MenuButton>(true).Length : 1;
                if (siblingButtonCount > fallbackSiblingCount)
                {
                    fallbackSiblingCount = siblingButtonCount;
                    fallback = b;
                }
            }
            return fallback;
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

            // Text-Kindobjekt: Anker/Offsets 1:1 vom Original uebernehmen (nicht einfach voll
            // ausspannen) - das Original hat einen definierten linken Einzug fuer den Text, den wir
            // sonst verlieren und alles faelschlich buendig am linken Rand landet.
            var templateTextRect = templateText != null ? templateText.GetComponent<RectTransform>() : null;

            var textGO = new GameObject("Label");
            textGO.AddComponent<RectTransform>();
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            if (templateTextRect != null)
            {
                textRect.anchorMin = templateTextRect.anchorMin;
                textRect.anchorMax = templateTextRect.anchorMax;
                textRect.offsetMin = templateTextRect.offsetMin;
                textRect.offsetMax = templateTextRect.offsetMax;
                textRect.pivot = templateTextRect.pivot;
            }
            else
            {
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
            }

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

            // Farben bewusst fest verdrahtet statt vom Original kopiert: MenuButton steuert seinen
            // Hover-/Auswahl-Balken vermutlich per Animator (isHoverHighlighted/isSelected Parameter,
            // siehe UpdateAnimatorParams), nicht per einfachem Selectable-Farbwechsel - ein Kopieren
            // von templateSelectable.colors haette also vermutlich nur unkonfigurierte
            // Standard-Unity-Farben ergeben, kein sichtbarer Balken. Diese Werte sind an das
            // sichtbare Gold/Amber-Design der Auswahlleiste angelehnt.
            var colors = button.colors;
            colors.normalColor = new Color(0f, 0f, 0f, 0f);
            colors.highlightedColor = new Color32(0xE6, 0xA9, 0x2D, 0xFF);
            colors.pressedColor = new Color32(0xC9, 0x8F, 0x20, 0xFF);
            colors.selectedColor = new Color32(0xE6, 0xA9, 0x2D, 0xFF);
            colors.disabledColor = new Color(0f, 0f, 0f, 0f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            button.colors = colors;

            button.onClick.AddListener((UnityAction)OnToggleClicked);

            toggleLabel = text;
            toggleButtonInstance = button;
            UpdateLabel();

            StarTruckMP.Log.LogInfo("OnlineModeToggle: Button im Hauptmenue erstellt.");
        }

        // Verhindert Connect/Disconnect-Spam durch schnelles Mehrfachklicken - Klicks innerhalb
        // der Cooldown-Zeit nach dem letzten tatsaechlichen Umschalten werden ignoriert.
        private const float ToggleCooldownSeconds = 3f;
        private static float lastToggleTime = -999f;

        private static void OnToggleClicked()
        {
            if (SC.versionRejected)
            {
                StarTruckMP.Log.LogInfo("OnlineModeToggle: Klick ignoriert (Version vom Server abgelehnt, Slipstream muss aktualisiert werden).");
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - lastToggleTime < ToggleCooldownSeconds)
            {
                StarTruckMP.Log.LogInfo("OnlineModeToggle: Klick ignoriert (Cooldown aktiv).");
                return;
            }
            lastToggleTime = now;

            OnlineModeEnabled = !OnlineModeEnabled;
            SaveMode();

            // Beim Umschalten auf Offline eine bestehende Verbindung auch aktiv trennen - vorher
            // wurde nur das Starten NEUER Verbindungen unterbunden, eine bereits laufende blieb
            // munter bestehen.
            if (!OnlineModeEnabled)
            {
                try
                {
                    if (SC.client != null && SC.client.IsConnected)
                    {
                        SC.client.Disconnect();
                    }
                }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"Konnte beim Umschalten auf Offline nicht trennen: {ex.Message}");
                }
            }

            UpdateLabel();
            StarTruckMP.Log.LogInfo($"Modus umgeschaltet: {(OnlineModeEnabled ? "Online" : "Offline")}");
        }

        public static void UpdateLabel()
        {
            if (toggleLabel == null) return;

            if (SC.versionRejected)
            {
                toggleLabel.text = "<color=#E53935>Update required</color>";
                return;
            }

            if (!OnlineModeEnabled)
            {
                toggleLabel.text = "Click to join Slipstream";
                return;
            }

            bool isConnected = false;
            try
            {
                isConnected = SC.client != null && SC.client.IsConnected;
            }
            catch { /* Client evtl. noch nicht initialisiert, Punkt bleibt grau */ }

            // Gruener Punkt = tatsaechlich verbunden, grauer Punkt = online-Modus an, aber (noch)
            // keine aktive Verbindung. Kein Spielername mehr im Text, damit es in eine Zeile passt.
            string dotColor = isConnected ? "#4CAF50" : "#888888";
            toggleLabel.text = $"Online @ Slipstream <color={dotColor}>●</color>";
        }
    }
}
