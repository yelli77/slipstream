using System;
using System.Globalization;
using System.IO;

namespace StarTruckMP.Common
{
    /// <summary>
    /// "Consume-once" Startmarker: der Slipstream-Launcher hinterlegt ihn unmittelbar bevor er
    /// das Spiel startet, der Mod liest ihn beim eigenen Start einmalig aus und loescht ihn sofort
    /// wieder. So laesst sich zuverlaessig erkennen, ob das Spiel gerade ueber Slipstream oder
    /// direkt (z.B. per Doppelklick in der Steam-Bibliothek) gestartet wurde - OHNE dass der
    /// Launcher beim Beenden irgendetwas aufraeumen muesste (das waere bei Abstuerzen/hartem
    /// Beenden unzuverlaessig).
    ///
    /// Liegt bewusst im OS-Temp-Verzeichnis statt im Spielordner: der Launcher startet das Spiel
    /// primaer ueber "steam://run/..." (siehe LaunchGame) - dort ist Steam selbst der unmittelbare
    /// Elternprozess des Spiels, eine Umgebungsvariable des Launchers wuerde dort also gar nicht
    /// erst ankommen. Ein Temp-Datei-Marker ist von der Prozess-Elternschaft unabhaengig.
    /// </summary>
    public static class LaunchMarker
    {
        private const string FileName = "slipstream-launch.marker";

        // Grosszuegig bemessen: ein steam://-Aufruf kann - falls der Steam-Client dabei selbst
        // erst noch hochfahren muss (kalter Start) - durchaus ein bis zwei Minuten brauchen, bis
        // der eigentliche Spielprozess (und damit unser Mod) ueberhaupt laeuft.
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);

        private static string MarkerPath => Path.Combine(Path.GetTempPath(), FileName);

        /// <summary>Vom Launcher aufzurufen, unmittelbar bevor der Spielprozess gestartet wird.</summary>
        public static void Write()
        {
            try
            {
                File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch
            {
                // Falls das Schreiben scheitert (z.B. Temp-Verzeichnis nicht beschreibbar), startet
                // das Spiel trotzdem - dann eben ohne Online-Funktion, das ist der sichere Default.
            }
        }

        /// <summary>
        /// Vom Mod beim eigenen Start aufzurufen. Liefert true genau dann, wenn ein frischer
        /// Marker vorliegt - und verbraucht ihn dabei (loescht ihn), damit ein spaeterer
        /// Direktstart ueber Steam (ohne den Launcher erneut zu durchlaufen) ihn nicht
        /// versehentlich wiederverwendet.
        /// </summary>
        public static bool ConsumeIfFresh()
        {
            try
            {
                if (!File.Exists(MarkerPath))
                    return false;

                bool fresh = false;
                var text = File.ReadAllText(MarkerPath);
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var writtenAt))
                {
                    fresh = (DateTime.UtcNow - writtenAt) <= MaxAge;
                }

                // Immer loeschen, unabhaengig davon ob "frisch" - ein einmal gelesener Marker ist
                // verbraucht, egal ob er noch gueltig war oder schon zu alt.
                try { File.Delete(MarkerPath); } catch { /* nicht kritisch */ }

                return fresh;
            }
            catch
            {
                // Im Zweifel (z.B. Zugriffsfehler) lieber ohne Online-Funktion starten als mit -
                // das entspricht dem sichereren Default (Direktstart-Verhalten).
                return false;
            }
        }
    }
}
