using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// custom-build-390: BepInEx ueberschreibt LogOutput.log bei jedem Spielstart, ein Log nach
    /// Absturz/Freeze ist nach dem Neustart weg. Deshalb: waehrend des Spiels alle 10 s ein Snapshot
    /// nach BepInEx/log_backups/LogOutput_current.log; beim naechsten Start wird der alte Snapshot
    /// zu LogOutput_yyyyMMdd_HHmmss.log umbenannt (max. 10 Stueck werden behalten).
    /// </summary>
    public static class LogBackup
    {
        private const int KEEP = 10;
        private const float INTERVAL = 10f;
        private static float next = 0f;
        private static string dir, cur, src;
        private static bool broken = false;

        public static void RotateOnStart()
        {
            try
            {
                dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "log_backups");
                cur = Path.Combine(dir, "LogOutput_current.log");
                src = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                Directory.CreateDirectory(dir);
                if (File.Exists(cur))
                {
                    string stamp = File.GetLastWriteTime(cur).ToString("yyyyMMdd_HHmmss");
                    string dest = Path.Combine(dir, "LogOutput_" + stamp + ".log");
                    if (File.Exists(dest)) dest = Path.Combine(dir, "LogOutput_" + stamp + "_" + Environment.TickCount + ".log");
                    File.Move(cur, dest);
                }
                var old = new DirectoryInfo(dir).GetFiles("LogOutput_2*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KEEP).ToList();
                foreach (var f in old) { try { f.Delete(); } catch { } }
            }
            catch (Exception ex) { broken = true; StarTruckMP.Log.LogWarning("390 LogBackup.RotateOnStart fehlgeschlagen: " + ex.Message); }
        }

        public static void Tick()
        {
            if (broken || src == null) return;
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + INTERVAL;
            try
            {
                using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var d = new FileStream(cur, FileMode.Create, FileAccess.Write, FileShare.Read);
                s.CopyTo(d);
            }
            catch { /* Snapshot ist best-effort */ }
        }
    }
}
