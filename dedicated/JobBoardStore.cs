using System;
using System.Collections.Generic;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

/// <summary>
/// Server-authoritative Job-Sync (custom-build-342): In-Memory-Store pro Sektor.
///
/// Jobboards sind Weltzustand, nicht spielergebunden:
/// - Upload von jedem Client im Sektor (sek-ID + vollstaendige Job-Liste, chunked).
/// - Der Store merged in einen Gesamtpool (dedupe nach questId) und merkt sich eine
///   Pool-Version. Clients bekommen auf Upload/Join/Sektorwechsel den VOLLSTAENDIGEN
///   Pool (kein Delta — idempotent, selbstheilend, ~10-20 KB je Sektor).
/// - JobTaken entfernt einen Job (questId) aus dem Pool.
/// - Restart leert den Store implizit (erste Uploads fuellen ihn wieder).
/// Kein Persistieren bewusst — der Pool regeneriert sich aus lokalen Client-Boards.
/// </summary>
public class JobBoardStore
{
    private class SectorPool
    {
        public readonly Dictionary<string, JobBoardJob> Jobs = new();
        public readonly HashSet<ushort> Members = new();
        public int Version;
    }

    private readonly Dictionary<string, SectorPool> _pools = new();
    private readonly Dictionary<ushort, string> _playerSectors = new();
    private readonly Action<string> _log;

    public JobBoardStore(Action<string> log) { _log = log ?? (_ => { }); }

    public void SetPlayerSector(ushort playerId, string sector)
    {
        if (string.IsNullOrEmpty(sector) || sector == "none") return;
        if (_playerSectors.TryGetValue(playerId, out var old) && old == sector) return;
        if (old != null && _pools.TryGetValue(old, out var oldPool))
            oldPool.Members.Remove(playerId);
        _playerSectors[playerId] = sector;
        if (!_pools.TryGetValue(sector, out var pool))
        {
            pool = new SectorPool();
            _pools[sector] = pool;
        }
        pool.Members.Add(playerId);
        _log($"JobBoardStore: player {playerId} now in sector '{sector}' ({pool.Members.Count} members, {pool.Jobs.Count} pooled jobs)");
    }

    public void RemovePlayer(ushort playerId)
    {
        if (_playerSectors.TryGetValue(playerId, out var sector))
        {
            if (_pools.TryGetValue(sector, out var pool)) pool.Members.Remove(playerId);
            _playerSectors.Remove(playerId);
        }
    }

    public string GetPlayerSector(ushort playerId)
        => _playerSectors.TryGetValue(playerId, out var s) ? s : null;

    /// <summary>Merged eine Client-Liste in den Sektor-Pool. Liefert true, wenn sich der Pool geaendert hat.</summary>
    public bool MergeJobs(string sector, List<JobBoardJob> jobs)
    {
        if (string.IsNullOrEmpty(sector) || jobs == null || jobs.Count == 0) return false;
        if (!_pools.TryGetValue(sector, out var pool))
        {
            pool = new SectorPool();
            _pools[sector] = pool;
        }
        bool changed = false;
        foreach (var job in jobs)
        {
            if (string.IsNullOrEmpty(job.QuestId)) continue;
            if (pool.Jobs.TryGetValue(job.QuestId, out var existing))
            {
                // gleiche questId: Anzeigefelder auffrischen (erster Uploader gewinnt bei Params)
                existing.DisplayName = JobBoardJob.BestOf(existing.DisplayName, job.DisplayName);
                existing.DisplayDescription = JobBoardJob.BestOf(existing.DisplayDescription, job.DisplayDescription);
            }
            else
            {
                pool.Jobs[job.QuestId] = job;
                changed = true;
            }
        }
        if (changed) pool.Version++;
        return changed;
    }

    /// <summary>Entfernt einen angenommenen Job. Liefert true, wenn der Pool sich geaendert hat.</summary>
    public bool RemoveJob(string sector, string questId)
    {
        if (string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(questId)) return false;
        if (!_pools.TryGetValue(sector, out var pool)) return false;
        if (pool.Jobs.Remove(questId))
        {
            pool.Version++;
            return true;
        }
        return false;
    }

    public int GetJobCount(string sector)
        => _pools.TryGetValue(sector, out var pool) ? pool.Jobs.Count : 0;

    public int GetVersion(string sector)
        => _pools.TryGetValue(sector, out var pool) ? pool.Version : 0;

    /// <summary>Liefert die Members eines Sektors (Snapshot).</summary>
    public List<ushort> GetMembers(string sector)
        => _pools.TryGetValue(sector, out var pool) ? new List<ushort>(pool.Members) : new List<ushort>();

    public bool HasPool(string sector)
        => _pools.TryGetValue(sector, out var pool) && pool.Jobs.Count > 0;

    public JobBoardJob[] GetJobs(string sector)
    {
        if (!_pools.TryGetValue(sector, out var pool)) return Array.Empty<JobBoardJob>();
        var arr = new JobBoardJob[pool.Jobs.Count];
        pool.Jobs.Values.CopyTo(arr, 0);
        return arr;
    }
}

/// <summary>
/// Ein Job im Pool: NUR die Felder, die das Jobboard zum Anzeigen/Accept braucht
/// (QuestInstance.questId/displayName/displayDescription + flache Parameter-Primitive).
/// Keine FlatSharp-Union, keine Spielobjekte ueber die Leitung.
/// </summary>
public class JobBoardJob
{
    public string QuestId = "";
    public string DisplayName = "";
    public string DisplayDescription = "";
    // Flache Parameter-Tripel (name, kind, primitiveValue) — Accept matcht lokal per questId.
    public List<JobBoardParam> Params = new();

    public static string BestOf(string a, string b)
        => string.IsNullOrEmpty(a) ? (b ?? "") : a;

    public int WireSize()
    {
        int size = 4 + System.Text.Encoding.UTF8.GetByteCount(QuestId ?? "")
                    + 4 + System.Text.Encoding.UTF8.GetByteCount(DisplayName ?? "")
                    + 4 + System.Text.Encoding.UTF8.GetByteCount(DisplayDescription ?? "")
                    + 4;
        if (Params != null)
            foreach (var p in Params) size += p.WireSize();
        return size;
    }
}

public class JobBoardParam
{
    public string Name = "";
    public byte Kind = 0;
    public string StrValue = ""; // Primitive als String-Kanonik (int/float/string/time)

    public int WireSize()
        => 4 + System.Text.Encoding.UTF8.GetByteCount(Name ?? "")
            + 1 + 4 + System.Text.Encoding.UTF8.GetByteCount(StrValue ?? "");
}
