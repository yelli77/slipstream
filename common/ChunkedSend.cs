using System;
using System.Collections.Generic;
using System.Threading;

namespace StarTruckMP.Common;

/// <summary>
/// Gemeinsame, Unity-freie Chunked-Send-Logik (custom-build-365): die reine
/// Send-/Pacing-Logik des JobBoard-Uploads (Client) — identisch zum Wireformat des
/// Server-BroadcastPool — extrahiert aus Client/JobBoardServerSync.SendChunked,
/// damit sie headless getestet werden kann (tests/JobBoardSmokeTest).
///
/// Lesson 335 / 364: Riptide-Reliable-Bursts verlieren Chunks, wenn zu viele Chunks
/// in einem engen Loop hintereinander verschickt werden. Deshalb Pacing: maximal
/// SEND_PACING Chunks pro Burst, danach PACING_PAUSE_MS Pause. Diese Konstanten sind
/// die EINZIGE Quelle — Mod (JobBoardServerSync.SendChunked) und Test nutzen dieselbe
/// Klasse, kein Duplikat-Drift.
/// </summary>
public static class ChunkedSend
{
    public const int MAX_CHUNK = 900;      // konservativ unter Riptide MaxPayloadSize=1225
    public const int SEND_PACING = 15;     // Chunks pro Burst
    public const int PACING_PAUSE_MS = 2;  // Pause zwischen Bursts

    /// <summary>Instrumentierung fuer Tests/Diagnose: Burst-Profile des letzten Sende-Vorgangs.</summary>
    public sealed class Stats
    {
        public int TotalChunks;
        public int BytesSent;
        /// <summary>Anzahl Chunks je Burst (jeder Eintrag ≤ SEND_PACING bei korrektem Pacing).</summary>
        public readonly List<int> BurstSizes = new();
        public int MaxBurstSize;

        /// <summary>Pacing-Regel: kein Burst groesser als SEND_PACING.</summary>
        public bool PacingOk => MaxBurstSize <= SEND_PACING;
    }

    /// <summary>
    /// Zerlegt <paramref name="payload"/> in MAX_CHUNK-Blöcke und sendet sie als
    /// reliable Einzelnachrichten (Wireformat: [sector][senderId-Placeholder][transferId]
    /// [chunkIndex][totalChunks][bytes]). Pacing: alle SEND_PACING Chunks eine
    /// PACING_PAUSE_MS-Pause, ausser nach dem letzten Chunk.
    /// </summary>
    /// <param name="messageTypeId">Wire-MessageId (upload/download/... je nach Richtung).</param>
    /// <param name="senderIdPlaceholder">0 beim Server, Client-ID-Placeholder beim Client.</param>
    /// <param name="send">Send-Callback (client.Send bzw. server.Send je Richtung).</param>
    /// <param name="dropChunks">NUR TESTMODUS: Chunk-Indizes, die bewusst NICHT gesendet
    /// werden (deterministischer Chunk-Verlust, z.B. Burst-Verlust wie in Lesson 335).</param>
    /// <returns>Anzahl Chunks des Transfers (inkl. gedroppter).</returns>
    public static int SendChunked(ushort messageTypeId, string sector, ushort senderIdPlaceholder,
        byte transferId, byte[] payload, Action<Riptide.Message> send,
        System.Collections.Generic.IReadOnlyCollection<int> dropChunks = null, Stats stats = null)
    {
        int totalChunks = Math.Max(1, (payload.Length + MAX_CHUNK - 1) / MAX_CHUNK);
        int burst = 0;
        for (int ci = 0; ci < totalChunks; ci++)
        {
            if (dropChunks != null && dropChunks.Contains(ci)) continue;

            int len = Math.Min(MAX_CHUNK, payload.Length - ci * MAX_CHUNK);
            var chunk = new byte[len];
            Buffer.BlockCopy(payload, ci * MAX_CHUNK, chunk, 0, len);
            var msg = Riptide.Message.Create(Riptide.MessageSendMode.Reliable, messageTypeId);
            msg.AddString(sector);
            msg.AddUShort(senderIdPlaceholder);
            msg.AddByte(transferId);
            msg.AddUShort((ushort)ci);
            msg.AddUShort((ushort)totalChunks);
            msg.AddBytes(chunk);
            send(msg);

            burst++;
            if (burst >= SEND_PACING)
            {
                if (stats != null) { stats.BurstSizes.Add(burst); stats.MaxBurstSize = Math.Max(stats.MaxBurstSize, burst); }
                burst = 0;
                if (ci + 1 < totalChunks) Thread.Sleep(PACING_PAUSE_MS);
            }
        }
        if (burst > 0 && stats != null)
        {
            stats.BurstSizes.Add(burst);
            stats.MaxBurstSize = Math.Max(stats.MaxBurstSize, burst);
        }
        if (stats != null) stats.TotalChunks = totalChunks;
        return totalChunks;
    }
}
