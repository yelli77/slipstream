using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace StarTruckMP.Dedicated;

/// <summary>
/// Pioneer-Badge (custom-build-348): Spielzeit-Tracking + Pioneer-Liste.
///
/// - Trackt Spielzeit pro SteamID: Session-Start beim Connect, Delta-Akkumulation
///   bei Disconnect (bzw. periodisch). Persistenz in playtime.json (atomisch, tmp+rename).
/// - Pioneer-Liste in pioneers.json (atomisch, tmp+rename): Seed-Pioniere zuerst,
///   danach die ersten 100 Steam-IDs, die totalMinutes >= 600 (10h) erreichen —
///   Reihenfolge = Zeitpunkt des Erreichens.
/// - Pfade konfigurierbar via STARTTRUCKMP_PLAYTIME_PATH / STARTTRUCKMP_PIONEERS_PATH.
///   Default: playtime.json im Arbeitsverzeichnis des Servers, pioneers.json daneben.
/// </summary>
public class PlaytimeTracker
{
    public const int MaxPioneers = 100;       // erste 100 Spieler mit 10h+
    public const int ThresholdMinutes = 600;  // 10 Stunden

    private sealed class PlaytimeEntry
    {
        public long totalMinutes { get; set; }
        public string firstSeen { get; set; } = "";
        public string name { get; set; } = "";
    }

    private sealed class PioneerEntry
    {
        public string steamId { get; set; } = "";
        public string name { get; set; } = "";
        public string addedAt { get; set; } = "";
        public string source { get; set; } = ""; // "seed" | "10h"
    }

    private sealed class PioneersFile
    {
        public List<PioneerEntry> pioneers { get; set; } = new();
    }

    // Seed-Pioniere (hardcodiert). Steam-IDs verifiziert am 2026-09-16 via
    // VPS /opt/data/discord-bridge-bot/data/links.json (steamId -> discordId) und
    // Discord-Username-Lookup der Discord-IDs:
    //   76561198089098228 -> 390813687193010177 -> "yelli_"   (Repo-Owner yelli77)
    //   76561199230438614 -> 432562804721975298 -> "malik2487"
    private static readonly (string steamId, string name)[] SeedPioneers =
    {
        ("76561198089098228", "Yelli"),
        ("76561199230438614", "Malik"),
    };

    private static readonly string PlaytimePath =
        Environment.GetEnvironmentVariable("STARTTRUCKMP_PLAYTIME_PATH") ?? "playtime.json";

    private static readonly string PioneersPath =
        Environment.GetEnvironmentVariable("STARTTRUCKMP_PIONEERS_PATH")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(PlaytimePath)) ?? ".", "pioneers.json");

    private readonly object _lock = new();
    private readonly Dictionary<string, PlaytimeEntry> _playtime = new(); // steamId -> entry
    private readonly List<PioneerEntry> _pioneers = new();                // Reihenfolge = Erreichen
    private readonly HashSet<string> _pioneerSet = new(StringComparer.Ordinal);

    // Laufende Sessions: connectionId -> (steamId, sessionStart, name)
    private sealed class Session
    {
        public ulong SteamId;
        public DateTime Start = DateTime.UtcNow;
        public string Name = "";
    }
    private readonly Dictionary<ushort, Session> _sessions = new();
    private readonly Dictionary<ulong, string> _sessionNames = new(); // steamId -> letzter Name

    public PlaytimeTracker()
    {
        Load();
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private static string SerializePlaytime(Dictionary<string, PlaytimeEntry> data)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        bool first = true;
        foreach (var kv in data)
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append(" \"").Append(Escape(kv.Key)).Append("\": { \"totalMinutes\": ")
              .Append(kv.Value.totalMinutes).Append(", \"firstSeen\": \"").Append(Escape(kv.Value.firstSeen))
              .Append("\", \"name\": \"").Append(Escape(kv.Value.name)).Append("\" }");
        }
        sb.Append("\n}");
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    /// <summary>Atomisches Schreiben (tmp + File.Replace/Rename), crash-safe.</summary>
    private static void SaveAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (PlatformNotSupportedException)
        {
            // Einige FS (OverlayFS) unterstuetzen File.Replace nicht — Fallback Rename.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(PlaytimePath))
                {
                    var raw = File.ReadAllText(PlaytimePath);
                    using var doc = JsonDocument.Parse(raw);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var e = prop.Value;
                        _playtime[prop.Name] = new PlaytimeEntry
                        {
                            totalMinutes = e.TryGetProperty("totalMinutes", out var m) ? m.GetInt64() : 0,
                            firstSeen = e.TryGetProperty("firstSeen", out var f) ? f.GetString() ?? "" : "",
                            name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        };
                    }
                    Console.WriteLine($"[INFO] PlaytimeTracker: {_playtime.Count} Eintraege geladen ({PlaytimePath})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] PlaytimeTracker: playtime.json unlesbar ({ex.Message}) — starte leer.");
            }

            try
            {
                if (File.Exists(PioneersPath))
                {
                    var raw = File.ReadAllText(PioneersPath);
                    var parsed = JsonSerializer.Deserialize<PioneersFile>(raw);
                    if (parsed?.pioneers != null)
                    {
                        foreach (var p in parsed.pioneers)
                        {
                            if (string.IsNullOrEmpty(p.steamId)) continue;
                            _pioneers.Add(p);
                            _pioneerSet.Add(p.steamId);
                        }
                    }
                    Console.WriteLine($"[INFO] PlaytimeTracker: {_pioneers.Count} Pioniere geladen ({PioneersPath})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] PlaytimeTracker: pioneers.json unlesbar ({ex.Message}) — starte leer.");
            }

            // Seeds immer (re)anfügen — an den LISTENANFANG, falls sie fehlen.
            foreach (var (steamId, name) in SeedPioneers)
            {
                if (string.IsNullOrEmpty(steamId)) continue; // TODO-Fallback: nicht raten
                if (!_pioneerSet.Contains(steamId))
                {
                    _pioneers.Insert(0, new PioneerEntry
                    {
                        steamId = steamId,
                        name = name,
                        addedAt = DateTime.UtcNow.ToString("o"),
                        source = "seed",
                    });
                    _pioneerSet.Add(steamId);
                }
            }
            if (_pioneers.Count > 0) SavePioneers();
            if (_playtime.Count > 0) SavePlaytime();
        }
    }

    private void SavePlaytime()
    {
        // Caller muss _lock halten.
        try { SaveAtomic(PlaytimePath, SerializePlaytime(_playtime)); }
        catch (Exception ex) { Console.WriteLine($"[WARN] PlaytimeTracker: playtime.json schreiben fehlgeschlagen: {ex.Message}"); }
    }

    private void SavePioneers()
    {
        // Caller muss _lock halten.
        try
        {
            var file = new PioneersFile { pioneers = _pioneers };
            SaveAtomic(PioneersPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.WriteLine($"[WARN] PlaytimeTracker: pioneers.json schreiben fehlgeschlagen: {ex.Message}"); }
    }

    // ------------------------------------------------------------------
    // Session-Lifecycle
    // ------------------------------------------------------------------

    public void OnConnect(ushort connectionId, string name)
    {
        lock (_lock)
        {
            _sessions[connectionId] = new Session { Start = DateTime.UtcNow, Name = name ?? "" };
        }
    }

    /// <summary>SteamID einer laufenden Session nachtraeglich setzen (SetPlayerSteamId-Handler).</summary>
    public void OnSteamIdKnown(ushort connectionId, ulong steamId, string name)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(connectionId, out var s))
            {
                s.SteamId = steamId;
                if (!string.IsNullOrEmpty(name)) s.Name = name;
            }
            if (steamId != 0 && !string.IsNullOrEmpty(name)) _sessionNames[steamId] = name;
        }
    }

    public void UpdateName(ulong steamId, string name)
    {
        if (steamId == 0 || string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            _sessionNames[steamId] = name;
            if (_playtime.TryGetValue(steamId.ToString(), out var e)) e.name = name;
        }
    }

    /// <summary>
    /// Disconnect: verbleibende Session-Zeit der Playtime zuordnen.
    /// Rückgabe: true, wenn dadurch ein NICHT-Pionier die 10h geknackt hat (Client weg —
    /// Broadcast entfällt, aber der Caller kann loggen).
    /// </summary>
    public bool OnDisconnect(ushort connectionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(connectionId, out var s)) return false;
            _sessions.Remove(connectionId);
            return Accumulate(s);
        }
    }

    private bool Accumulate(Session s)
    {
        // Caller muss _lock halten.
        if (s.SteamId == 0) return false;
        var delta = DateTime.UtcNow - s.Start;
        string id = s.SteamId.ToString();
        if (!_playtime.TryGetValue(id, out var e))
        {
            e = new PlaytimeEntry { firstSeen = DateTime.UtcNow.ToString("o"), name = s.Name ?? "" };
            _playtime[id] = e;
        }
        if (string.IsNullOrEmpty(e.name) && !string.IsNullOrEmpty(s.Name)) e.name = s.Name;
        e.totalMinutes += (long)Math.Floor(delta.TotalMinutes);
        // Teilminuten unter 1 Minute nicht verlieren: Rest als vorgezogenen Session-Start
        // beruecksichtigen, indem wir den Start um den Rest zuruecksetzen (nur in Memory;
        // der naechste Flush schreibt die aufgerundete Minute).
        var remainderSec = delta.TotalSeconds - Math.Floor(delta.TotalMinutes) * 60;
        s.Start = DateTime.UtcNow.AddSeconds(-remainderSec);
        SavePlaytime();
        bool wasPioneer = _pioneerSet.Contains(id);
        return !wasPioneer && e.totalMinutes >= ThresholdMinutes && EarnedSlotAvailable();
    }

    /// <summary>
    /// Periodische Wartung (alle 5 Min vom Server-Loop gerufen):
    /// - Flushet laufende Session-Deltas in die Playtime (crash-safe) und speichert.
    /// - Prueft 10h-Schwelle fuer alle Steam-IDs (inkl. laufender Sessions).
    /// Rückgabe: neu promoviert (steamId, name) in Erreichen-Reihenfolge.
    /// </summary>
    public List<(ulong steamId, string name)> PeriodicMaintenance()
    {
        var promoted = new List<(ulong, string)>();
        lock (_lock)
        {
            // Laufende Sessions flushen (Session-Start auf "jetzt" minus Restsekunden).
            foreach (var s in _sessions.Values)
            {
                if (s.SteamId == 0) continue;
                var delta = DateTime.UtcNow - s.Start;
                if (delta.TotalMinutes < 1) continue;
                string id = s.SteamId.ToString();
                if (!_playtime.TryGetValue(id, out var e))
                {
                    e = new PlaytimeEntry { firstSeen = DateTime.UtcNow.ToString("o"), name = s.Name ?? "" };
                    _playtime[id] = e;
                }
                if (string.IsNullOrEmpty(e.name) && !string.IsNullOrEmpty(s.Name)) e.name = s.Name;
                e.totalMinutes += (long)Math.Floor(delta.TotalMinutes);
                var remainderSec = delta.TotalSeconds - Math.Floor(delta.TotalMinutes) * 60;
                s.Start = DateTime.UtcNow.AddSeconds(-remainderSec);
            }
            SavePlaytime();

            // Schwellen-Check in Erreichen-Reihenfolge (aufsteigend nach Minuten).
            var candidates = new List<(string id, long minutes)>();
            foreach (var kv in _playtime)
                if (kv.Value.totalMinutes >= ThresholdMinutes && !_pioneerSet.Contains(kv.Key))
                    candidates.Add((kv.Key, kv.Value.totalMinutes));
            candidates.Sort((a, b) => a.minutes.CompareTo(b.minutes));

            foreach (var (id, _) in candidates)
            {
                if (_pioneers.Count(p => p.source == "10h") >= MaxPioneers) break;
                var entry = _playtime[id];
                var pe = new PioneerEntry
                {
                    steamId = id,
                    name = _sessionNames.TryGetValue(ulong.Parse(id), out var n) && !string.IsNullOrEmpty(n) ? n : entry.name,
                    addedAt = DateTime.UtcNow.ToString("o"),
                    source = "10h",
                };
                _pioneers.Add(pe);
                _pioneerSet.Add(id);
                SavePioneers();
                Console.WriteLine($"[INFO] PlaytimeTracker: PIONIER #{_pioneers.Count} — {pe.name} (steamId {id}) hat 10h erreicht.");
                promoted.Add((ulong.Parse(id), pe.name));
            }
        }
        return promoted;
    }

    private int CountEarned()
    {
        int n = 0;
        foreach (var p in _pioneers) if (p.source == "10h") n++;
        return n;
    }

    private bool EarnedSlotAvailable() => CountEarned() < MaxPioneers;

    public bool IsPioneer(ulong steamId)
    {
        if (steamId == 0) return false;
        lock (_lock) { return _pioneerSet.Contains(steamId.ToString()); }
    }
}