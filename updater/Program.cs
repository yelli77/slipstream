using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Diagnostics;

namespace StarTruckMPUpdater
{
    class Program
    {
        const string VersionJsonUrl = "https://api.github.com/repos/yelli77/slipstream/contents/version.json";
        const string BootstrapZipUrl = "https://raw.githubusercontent.com/yelli77/slipstream/main/bootstrap/bepinex-bootstrap.zip";
        const string ConfigFileName = "updater-config.txt";
        const string LocalVersionFileName = "installed-build.txt";

        static int Main(string[] args)
        {
            Console.WriteLine("=== StarTruckMP Updater ===");
            Console.WriteLine();

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(exeDir, ConfigFileName);
            string localVersionPath = Path.Combine(exeDir, LocalVersionFileName);

            // 1. Find or ask for the Star Trucker install path (based on the game executable, NOT BepInEx,
            //    since BepInEx might not be installed yet on a fresh setup)
            string? gamePath = LoadSavedGamePath(configPath);
            if (gamePath == null || !IsValidGameFolder(gamePath))
            {
                gamePath = AutoDetectGamePath();
            }
            while (gamePath == null || !IsValidGameFolder(gamePath))
            {
                Console.WriteLine("Star Trucker Installationsordner nicht gefunden.");
                Console.Write("Bitte Pfad zum Star Trucker Ordner eingeben (z.B. C:\\Program Files (x86)\\Steam\\steamapps\\common\\Star Trucker): ");
                gamePath = Console.ReadLine();
                if (gamePath != null)
                {
                    gamePath = gamePath.Trim().Trim('"');
                }
            }
            File.WriteAllText(configPath, gamePath);

            string pluginsDir = Path.Combine(gamePath, "BepInEx", "plugins");
            string dllPath = Path.Combine(pluginsDir, "StarTruckMP.dll");

            Console.WriteLine($"Spielordner: {gamePath}");
            Console.WriteLine();

            // 2. Make sure BepInEx + RiptideNetworking (Abhaengigkeit) installiert sind. Falls nicht: Bootstrap-Paket
            //    herunterladen und ins Spielverzeichnis entpacken.
            bool freshBepInExInstall = false;
            try
            {
                freshBepInExInstall = EnsureBepInExInstalled(gamePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Installieren von BepInEx: {ex.Message}");
                Pause();
                return 1;
            }

            // 3. Read locally installed build number
            string localBuild = File.Exists(localVersionPath) ? File.ReadAllText(localVersionPath).Trim() : "(unbekannt)";
            Console.WriteLine($"Aktuell installiert: {localBuild}");

            // 4. Fetch remote version info
            VersionInfo? remote;
            try
            {
                remote = FetchVersionInfo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Abrufen der Versionsinfo: {ex.Message}");
                Pause();
                return 1;
            }

            if (remote == null)
            {
                Console.WriteLine("Konnte keine Versionsinfo laden.");
                Pause();
                return 1;
            }

            Console.WriteLine($"Neueste Version:      {remote.build}");
            Console.WriteLine();

            if (remote.build == localBuild && File.Exists(dllPath) && !freshBepInExInstall)
            {
                Console.WriteLine("Du hast bereits die neueste Version.");
                LaunchGame(gamePath);
                Pause();
                return 0;
            }

            // 5. Make sure the game isn't running (file lock)
            if (IsGameRunning())
            {
                Console.WriteLine("Star Trucker läuft gerade. Bitte das Spiel schließen und Enter drücken, um fortzufahren...");
                Console.ReadLine();
                while (IsGameRunning())
                {
                    Console.WriteLine("Spiel läuft noch. Bitte schließen und Enter drücken...");
                    Console.ReadLine();
                }
            }

            // 6. Download + install StarTruckMP.dll
            try
            {
                Console.WriteLine($"Lade {remote.build} herunter...");
                byte[] gz = DownloadBytes(remote.url);

                Console.WriteLine("Entpacke...");
                byte[] dll = GunzipBytes(gz);

                Directory.CreateDirectory(pluginsDir);
                File.WriteAllBytes(dllPath, dll);
                File.WriteAllText(localVersionPath, remote.build);

                Console.WriteLine();
                Console.WriteLine($"Erfolgreich installiert: {remote.build}");
                Console.WriteLine($"Datei: {dllPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Installieren: {ex.Message}");
                Pause();
                return 1;
            }

            if (freshBepInExInstall)
            {
                Console.WriteLine();
                Console.WriteLine("BepInEx wurde gerade neu installiert. Star Trucker wird jetzt gestartet,");
                Console.WriteLine("damit BepInEx die noetigen Interop-Dateien generieren kann.");
            }

            LaunchGame(gamePath);
            Pause();
            return 0;
        }

        static void LaunchGame(string gamePath)
        {
            try
            {
                string appIdFile = Path.Combine(gamePath, "steam_appid.txt");
                if (File.Exists(appIdFile) && int.TryParse(File.ReadAllText(appIdFile).Trim(), out int appId))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Starte Star Trucker ueber Steam (App {appId})...");
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
                    Console.WriteLine();
                    Console.WriteLine("Starte Star Trucker...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = gamePath,
                        UseShellExecute = true
                    });
                    return;
                }

                Console.WriteLine("Konnte Star Trucker.exe nicht finden, bitte manuell starten.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Star Trucker konnte nicht automatisch gestartet werden: {ex.Message}");
                Console.WriteLine("Bitte manuell ueber Steam starten.");
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

            Console.WriteLine("BepInEx bzw. eine benoetigte Abhaengigkeit (RiptideNetworking.dll) fehlt.");
            Console.WriteLine("Lade BepInEx-Grundinstallation herunter...");

            byte[] zipBytes = DownloadBytes(BootstrapZipUrl);
            string tmpZip = Path.Combine(Path.GetTempPath(), "starttruckmp-bepinex-bootstrap.zip");
            File.WriteAllBytes(tmpZip, zipBytes);

            Console.WriteLine("Installiere BepInEx...");
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

            Console.WriteLine("BepInEx-Grundinstallation abgeschlossen.");
            return true;
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
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content));
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

        static void Pause()
        {
            Console.WriteLine();
            Console.WriteLine("Enter drücken zum Beenden...");
            Console.ReadLine();
        }
    }

    class VersionInfo
    {
        public string build { get; set; } = "";
        public string url { get; set; } = "";
    }
}
