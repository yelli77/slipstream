using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Forms;

namespace StarTruckMPUpdater
{
    class Program
    {
        const string VersionJsonUrl = "https://api.github.com/repos/yelli77/slipstream/contents/version.json";
        const string BootstrapZipUrl = "https://raw.githubusercontent.com/yelli77/slipstream/main/bootstrap/bepinex-bootstrap.zip";
        const string ConfigFileName = "updater-config.txt";
        const string LocalVersionFileName = "installed-build.txt";
        const string LogFileName = "slipstream-log.txt";

        static string logPath = "";

        [STAThread]
        static int Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(exeDir, LogFileName);
            string configPath = Path.Combine(exeDir, ConfigFileName);
            string localVersionPath = Path.Combine(exeDir, LocalVersionFileName);

            Log("=== Slipstream Start ===");

            // 1. Find or ask for the Star Trucker install path (based on the game executable, NOT BepInEx,
            //    since BepInEx might not be installed yet on a fresh setup)
            string? gamePath = LoadSavedGamePath(configPath);
            if (gamePath == null || !IsValidGameFolder(gamePath))
            {
                gamePath = AutoDetectGamePath();
            }
            while (gamePath == null || !IsValidGameFolder(gamePath))
            {
                gamePath = Microsoft.VisualBasic.Interaction.InputBox(
                    "Star Trucker Installationsordner nicht gefunden.\n\nBitte Pfad zum Star Trucker Ordner eingeben:",
                    "Slipstream",
                    @"C:\Program Files (x86)\Steam\steamapps\common\Star Trucker");

                if (string.IsNullOrWhiteSpace(gamePath))
                {
                    Log("Kein Spielordner angegeben, Abbruch.");
                    return 1;
                }
                gamePath = gamePath.Trim().Trim('"');
            }
            File.WriteAllText(configPath, gamePath);

            string pluginsDir = Path.Combine(gamePath, "BepInEx", "plugins");
            string dllPath = Path.Combine(pluginsDir, "StarTruckMP.dll");

            Log($"Spielordner: {gamePath}");

            // 2. Make sure BepInEx + RiptideNetworking (Abhaengigkeit) installiert sind. Falls nicht: Bootstrap-Paket
            //    herunterladen und ins Spielverzeichnis entpacken.
            bool freshBepInExInstall = false;
            try
            {
                freshBepInExInstall = EnsureBepInExInstalled(gamePath);
            }
            catch (Exception ex)
            {
                Log($"Fehler beim Installieren von BepInEx: {ex.Message}");
                ShowError($"Fehler beim Installieren von BepInEx:\n{ex.Message}");
                return 1;
            }

            // BepInEx-Konsolenfenster fuer alle Installationen (auch bereits bestehende) deaktivieren.
            try
            {
                EnsureBepInExConsoleDisabled(gamePath);
            }
            catch (Exception ex)
            {
                Log($"Konnte BepInEx-Konsole nicht deaktivieren: {ex.Message}");
            }

            // Alte StarTruckMP.cfg aufraeumen: der Mod hat keine konfigurierbaren Werte mehr
            // (Server-Adresse, Sync-Intervall und Hupe sind jetzt fest im Code), die Datei ist
            // also nur noch veralteter Ballast aus frueheren Versionen.
            try
            {
                RemoveObsoleteConfig(gamePath);
            }
            catch (Exception ex)
            {
                Log($"Konnte alte StarTruckMP.cfg nicht entfernen: {ex.Message}");
            }

            // 3. Read locally installed build number
            string localBuild = File.Exists(localVersionPath) ? File.ReadAllText(localVersionPath).Trim() : "(unbekannt)";
            Log($"Aktuell installiert: {localBuild}");

            // 4. Fetch remote version info
            VersionInfo? remote;
            try
            {
                remote = FetchVersionInfo();
            }
            catch (Exception ex)
            {
                Log($"Fehler beim Abrufen der Versionsinfo: {ex.Message}");
                ShowError($"Fehler beim Abrufen der Versionsinfo:\n{ex.Message}");
                return 1;
            }

            if (remote == null)
            {
                Log("Konnte keine Versionsinfo laden.");
                ShowError("Konnte keine Versionsinfo laden.");
                return 1;
            }

            Log($"Neueste Version: {remote.build}");

            if (remote.build == localBuild && File.Exists(dllPath) && !freshBepInExInstall)
            {
                Log("Bereits aktuell.");
                LaunchGame(gamePath);
                return 0;
            }

            // 5. Make sure the game isn't running (file lock)
            while (IsGameRunning())
            {
                var result = MessageBox.Show(
                    "Star Trucker läuft gerade und muss für das Update geschlossen werden.\n\nBitte das Spiel schließen und dann OK klicken.",
                    "Slipstream",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Cancel)
                {
                    Log("Abgebrochen, weil Spiel noch laeuft.");
                    return 1;
                }
            }

            // 6. Download + install StarTruckMP.dll
            try
            {
                Log($"Lade {remote.build} herunter...");
                byte[] gz = DownloadBytes(remote.url);

                Log("Entpacke...");
                byte[] dll = GunzipBytes(gz);

                Directory.CreateDirectory(pluginsDir);
                File.WriteAllBytes(dllPath, dll);
                File.WriteAllText(localVersionPath, remote.build);

                Log($"Erfolgreich installiert: {remote.build} ({dllPath})");
            }
            catch (Exception ex)
            {
                Log($"Fehler beim Installieren: {ex.Message}");
                ShowError($"Fehler beim Installieren:\n{ex.Message}");
                return 1;
            }

            if (freshBepInExInstall)
            {
                Log("BepInEx wurde gerade neu installiert. Star Trucker wird jetzt gestartet, damit BepInEx die Interop-Dateien generieren kann.");
            }

            LaunchGame(gamePath);
            return 0;
        }

        static void LaunchGame(string gamePath)
        {
            try
            {
                string appIdFile = Path.Combine(gamePath, "steam_appid.txt");
                if (File.Exists(appIdFile) && int.TryParse(File.ReadAllText(appIdFile).Trim(), out int appId))
                {
                    Log($"Starte Star Trucker ueber Steam (App {appId})...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://run/{appId}",
                        UseShellExecute = true
                    });
                    return;
                }

                string exePath = Path.Combine(gamePath, "Star Trucker.exe");
                if (File.Exists(exePath))
                {
                    Log("Starte Star Trucker...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = gamePath,
                        UseShellExecute = true
                    });
                    return;
                }

                Log("Konnte Star Trucker.exe nicht finden.");
                ShowError("Konnte Star Trucker.exe nicht finden. Bitte manuell starten.");
            }
            catch (Exception ex)
            {
                Log($"Star Trucker konnte nicht automatisch gestartet werden: {ex.Message}");
                ShowError($"Star Trucker konnte nicht automatisch gestartet werden:\n{ex.Message}\n\nBitte manuell ueber Steam starten.");
            }
        }

        static bool IsValidGameFolder(string path)
        {
            // Erkennung ueber das Spiel selbst, nicht ueber BepInEx - damit auch Frisch-Installationen
            // (noch ohne BepInEx) erkannt werden.
            return File.Exists(Path.Combine(path, "Star Trucker.exe")) ||
                   Directory.Exists(Path.Combine(path, "Star Trucker_Data"));
        }

        static string? LoadSavedGamePath(string configPath)
        {
            if (File.Exists(configPath))
            {
                var p = File.ReadAllText(configPath).Trim();
                if (p.Length > 0) return p;
            }
            return null;
        }

        static string? AutoDetectGamePath()
        {
            string[] candidates = new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Star Trucker",
                @"C:\Program Files\Steam\steamapps\common\Star Trucker",
                @"D:\Steam\steamapps\common\Star Trucker",
                @"D:\SteamLibrary\steamapps\common\Star Trucker",
            };
            foreach (var c in candidates)
            {
                if (IsValidGameFolder(c))
                    return c;
            }
            return null;
        }

        /// <summary>
        /// Prueft ob BepInEx (inkl. der RiptideNetworking-Abhaengigkeit) im Spielordner vorhanden ist.
        /// Falls nicht, wird das Bootstrap-Paket heruntergeladen und entpackt.
        /// Gibt true zurueck, wenn gerade eine Frisch-Installation durchgefuehrt wurde.
        /// </summary>
        static bool EnsureBepInExInstalled(string gamePath)
        {
            string corePath = Path.Combine(gamePath, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
            string doorstopConfig = Path.Combine(gamePath, "doorstop_config.ini");
            string winhttp = Path.Combine(gamePath, "winhttp.dll");
            string dotnetDir = Path.Combine(gamePath, "dotnet");
            string riptideDll = Path.Combine(gamePath, "BepInEx", "plugins", "RiptideNetworking.dll");

            bool bepInExMissing = !File.Exists(corePath) || !File.Exists(doorstopConfig) ||
                                   !File.Exists(winhttp) || !Directory.Exists(dotnetDir);
            bool riptideMissing = !File.Exists(riptideDll);

            if (!bepInExMissing && !riptideMissing)
            {
                return false;
            }

            Log("BepInEx bzw. eine benoetigte Abhaengigkeit (RiptideNetworking.dll) fehlt. Lade BepInEx-Grundinstallation herunter...");

            byte[] zipBytes = DownloadBytes(BootstrapZipUrl);
            string tmpZip = Path.Combine(Path.GetTempPath(), "starttruckmp-bepinex-bootstrap.zip");
            File.WriteAllBytes(tmpZip, zipBytes);

            Log("Installiere BepInEx...");
            using (var archive = ZipFile.OpenRead(tmpZip))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Verzeichnis-Eintrag
                        string dirPath = Path.Combine(gamePath, entry.FullName);
                        Directory.CreateDirectory(dirPath);
                        continue;
                    }

                    string destPath = Path.Combine(gamePath, entry.FullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null) Directory.CreateDirectory(destDir);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            try { File.Delete(tmpZip); } catch { /* egal */ }

            Log("BepInEx-Grundinstallation abgeschlossen.");
            return true;
        }

        /// <summary>
        /// Stellt sicher, dass BepInEx/config/BepInEx.cfg unter [Logging.Console] Enabled = false hat,
        /// damit kein Konsolenfenster beim Spielstart aufpoppt. Patcht auch bereits bestehende Installationen,
        /// nicht nur Frisch-Installationen ueber das Bootstrap-Paket.
        /// </summary>
        static void RemoveObsoleteConfig(string gamePath)
        {
            string cfgPath = Path.Combine(gamePath, "BepInEx", "config", "StarTruckMP.cfg");
            if (File.Exists(cfgPath))
            {
                File.Delete(cfgPath);
                Log("Alte StarTruckMP.cfg entfernt (keine konfigurierbaren Werte mehr vorhanden).");
            }
        }

                static void EnsureBepInExConsoleDisabled(string gamePath)
        {
            string cfgPath = Path.Combine(gamePath, "BepInEx", "config", "BepInEx.cfg");

            if (!File.Exists(cfgPath))
            {
                var dir = Path.GetDirectoryName(cfgPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(cfgPath, "[Logging.Console]\n\nEnabled = false\n");
                Log("BepInEx.cfg neu angelegt mit deaktivierter Konsole.");
                return;
            }

            var lines = File.ReadAllLines(cfgPath);
            bool inConsoleSection = false;
            bool foundEnabledLine = false;
            bool changed = false;
            int consoleSectionIndex = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inConsoleSection = trimmed.Equals("[Logging.Console]", StringComparison.OrdinalIgnoreCase);
                    if (inConsoleSection) consoleSectionIndex = i;
                    continue;
                }

                if (inConsoleSection && trimmed.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("="))
                {
                    foundEnabledLine = true;
                    if (!trimmed.Equals("Enabled = false", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "Enabled = false";
                        changed = true;
                    }
                }
            }

            var linesList = new System.Collections.Generic.List<string>(lines);

            if (consoleSectionIndex == -1)
            {
                // Section existiert noch gar nicht -> anhaengen
                linesList.Add("");
                linesList.Add("[Logging.Console]");
                linesList.Add("");
                linesList.Add("Enabled = false");
                changed = true;
            }
            else if (!foundEnabledLine)
            {
                // Section existiert, aber kein Enabled-Key -> direkt danach einfuegen
                linesList.Insert(consoleSectionIndex + 1, "Enabled = false");
                changed = true;
            }

            if (changed)
            {
                File.WriteAllLines(cfgPath, linesList);
                Log("BepInEx.cfg gepatcht: Konsole deaktiviert.");
            }
        }

        static bool IsGameRunning()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Star Trucker"))
                {
                    return true;
                }
                foreach (var p in Process.GetProcessesByName("StarTrucker"))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        static VersionInfo? FetchVersionInfo()
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "StarTruckMPUpdater");
            var apiJson = http.GetStringAsync(VersionJsonUrl).GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(apiJson);
            var content = doc.RootElement.GetProperty("content").GetString();
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content ?? ""));
            return JsonSerializer.Deserialize<VersionInfo>(decoded, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        static byte[] DownloadBytes(string url)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.Add("User-Agent", "StarTruckMPUpdater");
            return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }

        static byte[] GunzipBytes(byte[] gz)
        {
            using var input = new MemoryStream(gz);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        static void Log(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch { /* egal, Logging darf nie den Ablauf stoppen */ }
        }

        static void ShowError(string message)
        {
            MessageBox.Show(message, "Slipstream - Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    class VersionInfo
    {
        public string build { get; set; } = "";
        public string url { get; set; } = "";
    }
}
