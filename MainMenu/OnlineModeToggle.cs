using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

namespace StarTruckMP.MainMenu
{
    /// <summary>
    /// Fuegt dem Hauptmenue einen Online/Offline-Umschalter hinzu (visueller Klon des Optionen-
    /// Buttons, direkt danach eingefuegt). Der Zustand wird in einer eigenen, kleinen Textdatei
    /// neben der Mod-DLL gespeichert - bewusst NICHT ueber BepInEx.Configuration, damit es keine
    /// automatisch generierte/erweiterbare Config-Datei mit allen moeglichen Eintraegen gibt.
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
        /// Klont den Optionen-Button im Hauptmenue (nur fuer die Optik - Hintergrund, Text-Stil,
        /// Layout-Position) und haengt ihn direkt danach ein. Das geklonte MenuButton-Skript wird
        /// entfernt und durch einen ganz frischen UnityEngine.UI.Button ersetzt: MenuButton traegt
        /// eine im Editor fest verdrahtete ("persistente") Navigation zum Optionen-Screen, die sich
        /// zur Laufzeit NICHT per onClick.RemoveAllListeners() entfernen laesst (das entfernt nur
        /// zur Laufzeit hinzugefuegte Listener, keine im Editor konfigurierten). Ein frischer Button
        /// hat gar keine alte Verdrahtung im Gepaeck.
        /// </summary>
        public static void CreateToggleButton(MainMenuScreen menu)
        {
            // Unity's == ueberladen erkennt auch "wurde inzwischen zerstoert" korrekt (nicht nur
            // C#-null) - falls das Hauptmenue-Objekt neu erstellt wurde, wird hier neu angelegt.
            if (toggleButtonInstance != null)
            {
                UpdateLabel();
                return;
            }

            var template = menu.optionsButton;
            if (template == null)
            {
                StarTruckMP.Log.LogWarning("OnlineModeToggle: optionsButton nicht gefunden, Button wird nicht erstellt.");
                return;
            }

            // Farben/Uebergangsverhalten vom Original uebernehmen, bevor das Original-Skript weg ist.
            var colors = template.colors;
            var transition = template.transition;

            var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            clone.name = "SlipstreamModeButton";
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            var oldMenuButton = clone.GetComponent<MenuButton>();
            if (oldMenuButton != null)
            {
                UnityEngine.Object.Destroy(oldMenuButton);
            }

            var newButton = clone.AddComponent<Button>();
            newButton.colors = colors;
            newButton.transition = transition;
            newButton.onClick.AddListener((UnityAction)OnToggleClicked);

            toggleLabel = clone.GetComponentInChildren<TMP_Text>();
            toggleButtonInstance = newButton;
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

        private static void UpdateLabel()
        {
            if (toggleLabel != null)
            {
                toggleLabel.text = OnlineModeEnabled ? "Online" : "Offline";
            }
        }
    }
}
