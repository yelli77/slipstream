using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing;

namespace StarTruckMPUpdater
{
    class Program
    {
        const string VersionJsonUrl = "https://raw.githubusercontent.com/yelli77/slipstream/main/version.json";
        const string VersionJsonApiFallback = "https://api.github.com/repos/yelli77/slipstream/contents/version.json";
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
                    "Star Trucker install folder not found.\n\nPlease enter the path to the Star Trucker folder:",
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
                ShowError($"Error installing BepInEx:\n{ex.Message}");
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

            // 3. Read locally installed build number. Fail-open: wenn die Datei fehlt oder
            // leer ist, wird IMMER neu heruntergeladen (kein "Bereits aktuell").
            string localBuild = "";
            if (File.Exists(localVersionPath))
            {
                try
                {
                    // Trim + BOM-Strip + Normalisierung, damit U+FEFF am Dateianfang
                    // den Versionsvergleich nicht unsinnig faelscht.
                    localBuild = File.ReadAllText(localVersionPath).Trim().TrimStart('\uFEFF').Trim();
                }
                catch (Exception ex)
                {
                    Log($"Konnte {localVersionPath} nicht lesen: {ex.Message}");
                    localBuild = "";
                }
            }
            if (localBuild.Length == 0)
            {
                Log("installed-build.txt fehlt oder ist leer - UPDATE wird erzwungen.");
            }
            else
            {
                Log($"Aktuell installiert: {localBuild}");
            }

            // 4. Fetch remote version info
            VersionInfo? remote;
            try
            {
                remote = FetchVersionInfo();
            }
            catch (Exception ex)
            {
                Log($"Fehler beim Abrufen der Versionsinfo: {ex.Message}");
                ShowError($"Error fetching version info:\n{ex.Message}");
                return 1;
            }

            if (remote == null)
            {
                Log("Konnte keine Versionsinfo laden.");
                ShowError("Could not load version info.");
                return 1;
            }

            Log($"Neueste Version: {remote.build}");

            bool buildMatch = localBuild.Length > 0 &&
                string.Equals(remote.build.Trim(), localBuild, StringComparison.OrdinalIgnoreCase);
            // 5. Make sure the game isn't running (file lock) - passiert VOR dem Statusfenster,
            //    damit der "Bitte Spiel schliessen"-Dialog nicht hinter dem Fenster landet.
            bool updateNeeded = !(buildMatch && File.Exists(dllPath) && !freshBepInExInstall);
            while (updateNeeded && IsGameRunning())
            {
                var result = MessageBox.Show(
                    "Star Trucker is currently running and needs to be closed for the update.\n\nPlease close the game, then click OK.",
                    "Slipstream",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Cancel)
                {
                    Log("Abgebrochen, weil Spiel noch laeuft.");
                    return 1;
                }
            }

            // 6. Statusfenster mit Versionsvergleich zeigen und den Rest (Launch bzw.
            //    Download+Install) im Hintergrund-Thread abhandeln.
            return RunUpdateFlow(updateNeeded, freshBepInExInstall, localBuild, remote, gamePath, dllPath, localVersionPath);
        }

        /// <summary>
        /// Zeigt das ProgressForm mit installierter/neuester Version und erledigt den Rest des
        /// Ablaufs in einem Hintergrund-Thread, waehrend das Fenster auf dem UI-Thread lebt
        /// (gleiches Muster wie der fruehere Fresh-Install-Flow):
        ///  - Kein Update noetig: kurzer "Bereits aktuell"-Text, Spiel startet, Fenster schliesst
        ///    sich nach ~2 Sekunden von selbst.
        ///  - Update noetig: Spinner laeuft, Download+Install im Hintergrund; danach Status
        ///    "Update installiert...", das Fenster schliesst, sobald das Spielfenster sichtbar
        ///    ist (oder nach Timeout).
        ///  - Fehler: Fenster schliessen und den ueblichen ShowError-Dialog zeigen.
        /// </summary>
        static int RunUpdateFlow(bool updateNeeded, bool freshBepInExInstall, string localBuild,
                                 VersionInfo remote, string gamePath, string dllPath, string localVersionPath)
        {
            var form = new ProgressForm(localBuild, remote.build);
            int exitCode = 0;

            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    if (!updateNeeded)
                    {
                        // Nichts zu tun: kurz anzeigen, dass alles aktuell ist, Spiel starten
                        // und das Fenster nach ~2 Sekunden von selbst schliessen.
                        form.UpdateStatus("Bereits aktuell");
                        Log("Bereits aktuell.");
                        LaunchGame(gamePath);
                        System.Threading.Thread.Sleep(2000);
                    }
                    else
                    {
                        form.UpdateStatus($"Update wird heruntergeladen... {remote.build}");
                        Log($"Lade {remote.build} herunter...");

                        if (string.IsNullOrWhiteSpace(remote.url))
                        {
                            throw new InvalidOperationException(
                                "Update fehlgeschlagen: Die Download-URL in version.json ist leer.\nBitte Slipstream manuell neu installieren.");
                        }

                        byte[] gz = DownloadBytes(remote.url);

                        Log("Entpacke...");
                        byte[] dll = GunzipBytes(gz);

                        string? pluginsDir = Path.GetDirectoryName(dllPath);
                        if (pluginsDir != null) Directory.CreateDirectory(pluginsDir);
                        File.WriteAllBytes(dllPath, dll);
                        File.WriteAllText(localVersionPath, remote.build);

                        Log($"Erfolgreich installiert: {remote.build} ({dllPath})");

                        if (freshBepInExInstall)
                        {
                            // Bei einer Frisch-Installation generiert BepInEx/IL2CppInterop beim
                            // ersten Start erst die Interop-Assemblies - das kann mehrere Minuten
                            // dauern, deshalb hier der laengere Hinweistext.
                            form.UpdateStatus("Das Spiel wird vorbereitet, dies kann bis zu zwei Minuten dauern...");
                        }
                        else
                        {
                            form.UpdateStatus("Update installiert, Spiel wird gestartet...");
                        }

                        LaunchGame(gamePath);

                        // Fenster offen lassen, bis das Spielfenster wirklich sichtbar ist -
                        // so sieht der Nutzer, dass der Start geklappt hat (Timeout als Fallback).
                        WaitForGameWindowVisible(freshBepInExInstall ? 180 : 60);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Fehler im Update-Ablauf: {ex.Message}");
                    exitCode = 1;
                    // Fenster zuerst schliessen, dann den ueblichen Fehler-Dialog zeigen.
                    form.CloseSafely();
                    ShowError(ex.Message);
                    return;
                }
                form.CloseSafely();
            });
            worker.IsBackground = true;
            worker.Start();

            Application.Run(form);
            return exitCode;
        }

        /// <summary>
        /// Wartet, bis der Star-Trucker-Prozess den gewuenschten Laufzustand erreicht
        /// (running=true: warten bis er startet; running=false: warten bis er beendet wird).
        /// Gibt false zurueck, wenn der Zustand innerhalb des Timeouts nicht erreicht wurde.
        /// </summary>
        // Feste Steam App-ID von Star Trucker (store.steampowered.com/app/2380050).
        // Fertige Steam-Builds liefern normalerweise KEINE steam_appid.txt mit (die ist nur fuer
        // Dev-Testing ohne Steam-Client gedacht) - sich darauf zu verlassen fuehrte dazu, dass der
        // direkte .exe-Start ohne Steam-Initialisierung sofort abstuerzte (kurzes schwarzes Fenster,
        // dann nichts mehr, weil SteamAPI_Init() fehlschlaegt).
        const int SteamAppId = 2380050;

        static void LaunchGame(string gamePath)
        {
            // Marker VOR dem eigentlichen Start hinterlegen (nicht erst nach Erfolg) - so ist er
            // in jedem Fall vorhanden, bevor der Spielprozess (und damit unser Mod) ueberhaupt
            // hochfaehrt, egal ob per Steam oder als Direkt-Fallback gestartet wird. Ohne Marker
            // (z.B. Start direkt aus der Steam-Bibliothek, ohne Slipstream) bleibt die
            // Online-Funktion im Mod deaktiviert.
            StarTruckMP.Common.LaunchMarker.Write();

            try
            {
                // Primaer: immer ueber Steam starten (steam:// startet noetigenfalls auch den
                // Steam-Client selbst und initialisiert die Steam-Session sauber).
                Log($"Starte Star Trucker ueber Steam (App {SteamAppId})...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://run/{SteamAppId}",
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex)
            {
                Log($"Start ueber Steam fehlgeschlagen: {ex.Message}. Versuche direkten Start als Fallback.");
            }

            // Fallback: falls steam:// aus irgendeinem Grund nicht funktioniert (z.B. Steam nicht
            // installiert) - Hinweis, dass ein Direktstart ohne laufenden Steam-Client meist scheitert.
            try
            {
                string exePath = Path.Combine(gamePath, "Star Trucker.exe");
                if (File.Exists(exePath))
                {
                    Log("Starte Star Trucker direkt (Fallback, ohne Steam)...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = gamePath,
                        UseShellExecute = true
                    });
                    ShowError("Star Trucker was started without Steam. If the game closes immediately, please start Steam manually first and launch Star Trucker from there.");
                    return;
                }

                Log("Konnte Star Trucker.exe nicht finden.");
                ShowError("Could not find Star Trucker.exe. Please start it manually via Steam.");
            }
            catch (Exception ex2)
            {
                Log($"Star Trucker konnte nicht automatisch gestartet werden: {ex2.Message}");
                ShowError($"Star Trucker could not be started automatically:\n{ex2.Message}\n\nPlease start it manually via Steam.");
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

            // WICHTIG: Hier absichtlich KEINE eigene Datei mehr anlegen, wenn sie fehlt.
            // Eine von uns von Hand gebaute Minimal-Config (statt der von BepInEx selbst
            // generierten, vollstaendigen Datei) hat BepInEx komplett am Laden gehindert -
            // vermutlich, weil BepInEx beim Parsen einer unvollstaendigen/untypischen Datei
            // intern abbricht. Deshalb: nur patchen, wenn BepInEx die Datei schon SELBST
            // erzeugt hat (also mindestens einmal erfolgreich durchgelaufen ist).
            if (!File.Exists(cfgPath))
            {
                Log("BepInEx.cfg existiert noch nicht (erster Lauf) - ueberspringe Konsolen-Patch, BepInEx generiert sie selbst.");
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

        /// <summary>
        /// Prueft ob das Spielfenster tatsaechlich sichtbar ist (nicht nur der Prozess existiert).
        /// Der Prozess startet fast sofort, aber bei Star Trucker vergeht bis zu einer Minute
        /// (Cpp2IL/Interop-Generierung, Unity-Init), bevor ueberhaupt ein Fenster gerendert wird -
        /// reines Process.Exists waere also viel zu frueh fuer "das Spiel ist jetzt sichtbar".
        /// </summary>
        static bool IsGameWindowVisible()
        {
            try
            {
                foreach (var name in new[] { "Star Trucker", "StarTrucker" })
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero) return true;
                        }
                        catch { /* Prozess evtl. gerade beendet, egal */ }
                    }
                }
            }
            catch { }
            return false;
        }

        static bool WaitForGameWindowVisible(int timeoutSeconds)
        {
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < deadline)
            {
                if (IsGameWindowVisible()) return true;
                System.Threading.Thread.Sleep(1000);
            }
            return IsGameWindowVisible();
        }

        static VersionInfo? FetchVersionInfo()
        {
            // 1. Try raw (nur 5-Min CDN-Cache, kein API-Cache)
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "StarTruckMPUpdater");
                var raw = http.GetStringAsync(VersionJsonUrl).GetAwaiter().GetResult();
                var info = JsonSerializer.Deserialize<VersionInfo>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (info != null && !string.IsNullOrEmpty(info.build))
                {
                    Log($"VersionInfo von raw: {info.build}");
                    return info;
                }
            }
            catch (Exception ex)
            {
                Log($"raw-version.json fehlgeschlagen: {ex.Message}");
            }

            // 2. Fallback: GitHub API (base64-kodiert)
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "StarTruckMPUpdater");
                var apiJson = http.GetStringAsync(VersionJsonApiFallback).GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(apiJson);
                var content = doc.RootElement.GetProperty("content").GetString();
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content ?? ""));
                var info = JsonSerializer.Deserialize<VersionInfo>(decoded, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (info != null && !string.IsNullOrEmpty(info.build))
                {
                    Log($"VersionInfo von API-Fallback: {info.build}");
                    return info;
                }
            }
            catch (Exception ex)
            {
                Log($"api-version.json fehlgeschlagen: {ex.Message}");
            }

            return null;
        }

        const string GitHubRawBase = "https://raw.githubusercontent.com/yelli77/slipstream/main/";

        static byte[] DownloadBytes(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Download-URL ist leer. Bitte Slipstream-Update erneut ausloesen.");

            // Relative URL aus version.json robust aufloesen
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                url = GitHubRawBase + url.TrimStart('/');
                Log($"Relative URL aufgelost zu: {url}");
            }

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
            MessageBox.Show(message, "Slipstream - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
            /// Zeigt ein schlichtes Fortschrittsfenster mit Spinner und Statustext. Kein Schliessen-Button
            /// (der Prozess soll nicht abgebrochen werden koennen), Statustext per Invoke von einem
            /// Hintergrund-Thread aus aktualisierbar, schliesst sich selbst per CloseSafely().
            /// Im Kopf zeigt es immer installierte und neueste Version (wenn bekannt).
            /// </summary>
    /// <summary>
    /// Selbst gezeichneter, rotierender Punkt-Spinner (Windows-Forms hat keinen eingebauten
    /// Spinner-Control) - 8 Punkte im Kreis mit abnehmender Deckkraft, per Timer alle 60ms
    /// um einen Schritt weitergedreht.
    /// </summary>
    class SpinnerControl : Control
    {
        private int angle = 0;
        private readonly System.Windows.Forms.Timer timer;

        public SpinnerControl()
        {
            DoubleBuffered = true;
            Size = new Size(40, 40);
            timer = new System.Windows.Forms.Timer { Interval = 60 };
            timer.Tick += (s, e) => { angle = (angle + 30) % 360; Invalidate(); };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            const int numDots = 8;
            float radius = Math.Min(Width, Height) / 2f - 5f;
            var center = new PointF(Width / 2f, Height / 2f);

            for (int i = 0; i < numDots; i++)
            {
                double theta = (angle + i * (360.0 / numDots)) * Math.PI / 180.0;
                float x = center.X + (float)(radius * Math.Cos(theta));
                float y = center.Y + (float)(radius * Math.Sin(theta));
                int alpha = 60 + (255 - 60) * (numDots - i) / numDots;
                using var brush = new SolidBrush(Color.FromArgb(alpha, Color.DodgerBlue));
                const float dotSize = 7f;
                g.FillEllipse(brush, x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    class ProgressForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const int SW_RESTORE = 9;

        private readonly Label statusLabel;
        private readonly SpinnerControl spinner;
        private readonly Label versionLabel;

        /// <summary>
        /// optionalLocal/optionalRemote: werden im Kopf als "Installierte Version: X" /
        /// "Neueste Version: Y" angezeigt (leer = Zeile weglassen).
        /// </summary>
        public ProgressForm(string optionalLocal = "", string optionalRemote = "")
        {
            Text = "Slipstream";
            Width = 480;
            Height = 240;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            TopMost = true;
            ShowInTaskbar = true;

            Shown += (s, e) => ForceForeground();

            spinner = new SpinnerControl
            {
                Anchor = AnchorStyles.None
            };

            // Kopf: installierte + neueste Version anzeigen (auch bei "Bereits aktuell" sichtbar).
            versionLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(optionalLocal) && string.IsNullOrWhiteSpace(optionalRemote)
                    ? ""
                    : (string.IsNullOrWhiteSpace(optionalLocal)
                        ? $"Neueste Version: {optionalRemote}"
                        : string.IsNullOrWhiteSpace(optionalRemote)
                            ? $"Installierte Version: {optionalLocal}"
                            : $"Installierte Version: {optionalLocal}\nNeueste Version: {optionalRemote}"),
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(20, 8, 20, 0)
            };

            statusLabel = new Label
            {
                Text = "Preparing...",
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Bottom,
                Height = 70,
                Padding = new Padding(20, 0, 20, 10)
            };

            Controls.Add(statusLabel);
            Controls.Add(spinner);
            Controls.Add(versionLabel);

            // Spinner in der verbleibenden Flaeche zwischen Kopf- und Status-Label zentrieren.
            Layout += (s, e) =>
            {
                spinner.Location = new Point(
                    (ClientSize.Width - spinner.Width) / 2,
                    versionLabel.Height + (ClientSize.Height - versionLabel.Height - statusLabel.Height - spinner.Height) / 2);
            };
        }

        /// <summary>
        /// TopMost allein reicht oft nicht, um ein Fenster wirklich in den Vordergrund zu holen -
        /// Windows blockiert das fuer Prozesse ohne unmittelbar vorausgegangene Nutzerinteraktion
        /// (Fokus-Diebstahl-Schutz). AttachThreadInput mit dem aktuell fokussierten Fenster ist der
        /// Standard-Workaround dafuer.
        /// </summary>
        private void ForceForeground()
        {
            try
            {
                ShowWindow(Handle, SW_RESTORE);

                IntPtr foreground = GetForegroundWindow();
                uint foregroundThreadId = GetWindowThreadProcessId(foreground, IntPtr.Zero);
                uint currentThreadId = GetCurrentThreadId();

                if (foregroundThreadId != currentThreadId)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    SetForegroundWindow(Handle);
                    Activate();
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
                else
                {
                    SetForegroundWindow(Handle);
                    Activate();
                }
            }
            catch { /* wenn's nicht klappt, bleibt's halt in der Taskleiste - kein Absturzgrund */ }
        }

        public void UpdateStatus(string text)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired)
                    Invoke(new Action(() => { if (!IsDisposed) statusLabel.Text = text; }));
                else
                    statusLabel.Text = text;
            }
            catch (ObjectDisposedException) { /* Fenster wurde inzwischen geschlossen, egal */ }
            catch (InvalidOperationException) { /* Handle nicht (mehr) vorhanden, egal */ }
        }

        public void CloseSafely()
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired)
                    Invoke(new Action(Close));
                else
                    Close();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    class VersionInfo
    {
        public string build { get; set; } = "";
        public string url { get; set; } = "";
    }
}
