using System;
using System.IO;
using System.Threading;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// custom-build-392: Diagnose-Watchdog. Hintergrund-Thread prueft, ob der Unity-Hauptthread noch
    /// tickt (Beat() aus PauseController.Update). Bei Stillstand wird der letzte Schritt (Mark)
    /// in LogOutput.log UND BepInEx/log_backups/watchdog.log geschrieben. Keine Unity-APIs im Thread.
    /// </summary>
    public static class Watchdog
    {
        private static long lastBeat = 0;
        private static long beats = 0;
        private static volatile string lastStep = "(none)";
        private static long lastStepAt = 0;
        private static Thread thread;
        private static string path;

        public static void Start()
        {
            if (thread != null) return;
            try
            {
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "log_backups");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "watchdog.log");
            }
            catch { path = null; }
            thread = new Thread(Loop) { IsBackground = true, Name = "StarTruckMP-Watchdog" };
            thread.Start();
        }

        public static void Beat()
        {
            Interlocked.Exchange(ref lastBeat, Environment.TickCount64);
            Interlocked.Increment(ref beats);
        }

        public static void Mark(string step, bool log = true)
        {
            lastStep = step;
            Interlocked.Exchange(ref lastStepAt, Environment.TickCount64);
            if (log)
            {
                try { StarTruckMP.Log.LogInfo("392 step: " + step); } catch { }
                Write("step: " + step);
            }
        }

        private static void Write(string msg)
        {
            if (path == null) return;
            try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine); } catch { }
        }

        private static void Loop()
        {
            bool hung = false;
            long hangStart = 0, lastReport = 0;
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (Interlocked.Read(ref beats) == 0) continue;
                    long now = Environment.TickCount64;
                    long gap = now - Interlocked.Read(ref lastBeat);
                    if (gap > 4000)
                    {
                        if (!hung) { hung = true; hangStart = now - gap; lastReport = 0; }
                        if (now - lastReport >= 10000)
                        {
                            lastReport = now;
                            string msg = "392 WATCHDOG: moeglicher Freeze - Hauptthread tickt seit " + (gap / 1000) + "s nicht, letzter Schritt='"
                                + lastStep + "' (vor " + ((now - Interlocked.Read(ref lastStepAt)) / 1000) + "s), sector=" + StarTruckClient.currentSector
                                + ", WorkingSet=" + (Environment.WorkingSet / 1048576) + "MB";
                            try { StarTruckMP.Log.LogWarning(msg); } catch { }
                            Write(msg);
                        }
                    }
                    else if (hung)
                    {
                        hung = false;
                        string msg = "392 WATCHDOG: Hauptthread wieder aktiv nach " + ((now - hangStart) / 1000) + "s, letzter Schritt='" + lastStep + "'";
                        try { StarTruckMP.Log.LogWarning(msg); } catch { }
                        Write(msg);
                    }
                }
                catch { }
            }
        }
    }
}
