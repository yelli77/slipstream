using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Riptide;
using StarTruckMP.Common;
using StarTruckMP.Dedicated;

namespace StarTruckMP.Tests;

/// <summary>
/// Headless-Integrationstest fuer die Job-Board-Sync-Kette (custom-build-365).
///
/// Startet die ECHTE Dedicated-Server-Schicht in-process (MessageHandler-Dispatch +
/// JobBoardServer + JobBoardStore + JobBoardCodec) als Riptide-Server und verbindet
/// ZWEI echte Riptide-Clients. Client A laedt 200 Jobs hoch — ueber die gemeinsam
/// genutzte Sende-/Pacing-Logik (common/ChunkedSend, identisch zur Mod-Sende-Strecke).
///
/// Assertions (Exit-Code != 0 bei Fail, Ausgabe 'PASS:'/'FAIL:' pro Assertion):
///  (a) Upload gemerged: Server-Log 'gemerged' und GetJobCount(sector) == 200
///  (b) beide Clients empfangen den Pool vollstaendig (200 Jobs, identische QuestId-Liste)
///  (c) jobTaken entfernt den Job bei BEIDEN Clients aus dem Pool
///  (d) Chunk-Verlust: Testmodus droppt deterministisch 2-3 Chunks — Upload bleibt stuck,
///      Re-Send mit neuem transferId (Re-Send-Netz, Build-364) komplettiert ihn.
///      (Riptide-Reliable retransmitting intern deckt Burst-Verlust nachweislich nicht
///      zuverlaessig ab — Lesson 335; der Test modelliert den Verlust an der Sendestelle.)
///  (e) Burst-Pacing-Verifikation: der echte Sende-Pfad darf KEINEN Burst > SEND_PACING(15)
///      erzeugen — ein Verstoess failt den Test hart (Pacing-Regelverstoess-Detektor).
/// </summary>
public static class Program
{
    private const ushort ServerPort = 7797;
    private const string SectorMain = "TESTSEKTOR_A";
    private const string SectorLoss = "TESTSEKTOR_B";
    private const int JobCount = 200;
    private const int LossJobCount = 30;

    private static int _failures;
    private static readonly List<string> _serverLog = new();
    private static readonly object _logLock = new();

    private static Riptide.Server _server;
    private static readonly Dictionary<ushort, PlayerState> _players = new();
    private static Riptide.Client _clientA;
    private static Riptide.Client _clientB;
    private static JobBoardStore _store;
    private static MessageHandler _handler;
    private static ManualResetEvent _stopFlag;

    private class ClientState
    {
        public string Name;
        public readonly Dictionary<string, List<string>> PoolBySector = new();
        public readonly List<string> TakenEvents = new();
    }

    private static readonly ClientState _stateA = new() { Name = "A" };
    private static readonly ClientState _stateB = new() { Name = "B" };

    // Chunk-Assemblierung pro Sektor (Spiegel von JobBoardServerSync.IncomingPool).
    private static readonly Dictionary<string, Dictionary<int, byte[]>> _incomingA = new();
    private static readonly Dictionary<string, Dictionary<int, byte[]>> _incomingB = new();

    public static int Main()
            {
                Console.WriteLine("=== JobBoardSmokeTest (StarTruckMP) ===");
                var sw = Stopwatch.StartNew();
                try
                {
                    // (f) Build-368 Root-Cause-Beweis: das ALTE Client-Format (BinaryWriter.
                    // Write(string) = 7-Bit-Varint-Laenge) kann vom echten Server-Codec
                    // (JobBoardCodec.DecodeJobs, int32-LE-Laengen) NICHT decodiert werden.
                    // Der Server-Merge wirft -> kein BroadcastPool -> nie ein Pool-Download
                    // (Log 367: 'kein Pool-Download nach Upload' endlos).
                    byte[] oldFmt = SerializeJobs_OldBinaryWriterFormat(3);
                    bool oldFails = false;
                    try { var old = JobBoardCodec.DecodeJobs(oldFmt); oldFails = old.Count != 3; }
                    catch (Exception) { oldFails = true; }
                    Check("f1: ALTES Client-Format (7-bit-Varint-Strings) wird vom Server-Codec NICHT decodiert (Root-Cause 368)", oldFails);
                    byte[] newFmt = SerializeJobs(3, "jobNew", descLen: 10);
                    var parsed = JobBoardCodec.DecodeJobs(newFmt);
                    Check("f2: NEUES Client-Format (int32-LE-Strings) decodiert korrekt (3/3 Jobs)",
                        parsed.Count == 3 && parsed[0].QuestId == "jobNew-0001");
            StartServer();
            StartTicker();
            ConnectClients();

            // ---- (a)+(b)+(e): Upload 200 Jobs mit echtem Sende-Pfad inkl. Pacing ----
            byte[] payload = SerializeJobs(JobCount, "jobA");
            var stats = new ChunkedSend.Stats();
            int chunks = ChunkedSend.SendChunked((ushort)MessageType.jobBoardUpload, SectorMain,
                0, 42, payload, m => _clientA.Send(m), null, stats);
            Console.WriteLine($"[test] Upload gesendet: {JobCount} Jobs, {payload.Length} bytes, {chunks} Chunks, {stats.BurstSizes.Count} Bursts (Profil: {string.Join(",", stats.BurstSizes)})");

            Check($"e1: Burst-Pacing eingehalten (max Burst {stats.MaxBurstSize} <= 15)", stats.PacingOk);
            Check($"e2: Pacing aktiv, mehrere Bursts ({stats.BurstSizes.Count} >= 2)", stats.BurstSizes.Count >= 2);
            Check("e3: Chunk-Zahl konsistent", stats.TotalChunks == chunks);

            WaitUntil(() => _store.GetJobCount(SectorMain) == JobCount && LogContains("gemerged"),
                TimeSpan.FromSeconds(15));
            Check($"a1: Upload gemerged — GetJobCount('{SectorMain}') == {JobCount} (ist {_store.GetJobCount(SectorMain)})",
                LogContains("gemerged") && _store.GetJobCount(SectorMain) == JobCount);
            Check("a2: Upload nicht stuck verworfen (kein 'stuck Upload' im Server-Log)", !LogContains("stuck Upload"));

            WaitUntil(() => PoolCount(_stateA, SectorMain) == JobCount && PoolCount(_stateB, SectorMain) == JobCount,
                TimeSpan.FromSeconds(10));
            Check($"b1: Client A Pool vollstaendig ({PoolCount(_stateA, SectorMain)}/{JobCount})", PoolCount(_stateA, SectorMain) == JobCount);
            Check($"b2: Client B Pool vollstaendig ({PoolCount(_stateB, SectorMain)}/{JobCount})", PoolCount(_stateB, SectorMain) == JobCount);
            var idsA = PoolIds(_stateA, SectorMain);
            var idsB = PoolIds(_stateB, SectorMain);
            Check("b3: Identische QuestId-Liste bei beiden Clients", idsA != null && idsB != null && idsA.SequenceEqual(idsB));

            // ---- (c) jobTaken ----
            string takenId = idsA[0];
            var takenMsg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.jobTaken);
            takenMsg.AddString(SectorMain);
            takenMsg.AddString(takenId);
            _clientB.Send(takenMsg);
            bool takenOk = WaitUntil(() => _store.GetJobCount(SectorMain) == JobCount - 1
                && PoolCount(_stateA, SectorMain) == JobCount - 1
                && PoolCount(_stateB, SectorMain) == JobCount - 1
                && _stateA.TakenEvents.Contains(takenId)
                && _stateB.TakenEvents.Contains(takenId),
                TimeSpan.FromSeconds(10));
            Check($"c1: jobTaken entfernt '{takenId}' bei beiden Clients (Store {JobCount - 1}, A {PoolCount(_stateA, SectorMain)}, B {PoolCount(_stateB, SectorMain)}, Events A={_stateA.TakenEvents.Count}/B={_stateB.TakenEvents.Count})", takenOk);

            // ---- (d) Chunk-Verlust + Re-Send-Netz ----
            byte[] payloadL = SerializeJobs(LossJobCount, "jobB");
            int totalChunksL = Math.Max(1, (payloadL.Length + ChunkedSend.MAX_CHUNK - 1) / ChunkedSend.MAX_CHUNK);
            // deterministische Verluste, verteilt ueber Bursts (2-3 Chunks):
            var drop = new HashSet<int> { 2, 7 };
            if (totalChunksL > 20) drop.Add(20);
            ChunkedSend.SendChunked((ushort)MessageType.jobBoardUpload, SectorLoss,
                0, 77, payloadL, m => _clientA.Send(m), drop, null);
            Thread.Sleep(2500);
            Check($"d1: Upload mit {drop.Count} verlorenen Chunks bleibt stuck (Pool NICHT gemerged, count={_store.GetJobCount(SectorLoss)})", _store.GetJobCount(SectorLoss) == 0);
            // Re-Send-Netz (Build-364): kompletter Re-Send mit NEUEM transferId
            ChunkedSend.SendChunked((ushort)MessageType.jobBoardUpload, SectorLoss,
                0, 78, payloadL, m => _clientA.Send(m), null, null);
            bool resent = WaitUntil(() => _store.GetJobCount(SectorLoss) == LossJobCount, TimeSpan.FromSeconds(10));
            Check($"d2: Re-Send mit neuem transferId komplettiert den Upload ({_store.GetJobCount(SectorLoss)}/{LossJobCount})", resent);

            Console.WriteLine($"\n=== Ergebnis: {((_failures == 0) ? "ALLE ASSERTIONS GRUEN" : $"{_failures} FAIL")}, Dauer {sw.Elapsed.TotalSeconds:F1}s ===");
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: Test-Absturz: {ex}");
            return 1;
        }
        finally
        {
            _stopFlag?.Set();
            try { _clientA?.Disconnect(); _clientB?.Disconnect(); } catch { }
            Thread.Sleep(200);
            try { _server?.Stop(); } catch { }
        }
    }

    // ------------------------------------------------------------------ infra
    private static void StartServer()
    {
        _server = new Riptide.Server();
        _server.ClientConnected += (s, e) => { Console.WriteLine($"[server] Client connected: {e.Client.Id}"); _players[e.Client.Id] = new PlayerState { Id = e.Client.Id, Sector = "none" }; };
        _server.ClientDisconnected += (s, e) => _players.Remove(e.Client.Id);
        _server.MessageReceived += (s, e) => _handler.Handle(e, _server);
        _server.Start(ServerPort, 8);
        _store = new JobBoardStore(LogServer);
        _handler = new MessageHandler(_players, 1, _ => { }, (_, _) => { });
        _handler.JobBoards = new JobBoardServer(_store, LogServer);
        Console.WriteLine($"[server] Riptide-Server gestartet auf Port {ServerPort}");
    }

    private static void LogServer(string m)
    {
        lock (_logLock) _serverLog.Add(m);
        Console.WriteLine("[server] " + m);
    }

    private static void ConnectClients()
    {
        _clientA = new Riptide.Client();
        _clientB = new Riptide.Client();
        _clientA.MessageReceived += (s, e) => OnClientMessage(e, _stateA, _incomingA);
        _clientB.MessageReceived += (s, e) => OnClientMessage(e, _stateB, _incomingB);
        _clientA.Connect($"127.0.0.1:{ServerPort}"); // Riptide 2.x: Port gehoert in die Host-Adresse
        _clientB.Connect($"127.0.0.1:{ServerPort}");
        WaitUntil(() => _clientA.IsConnected && _clientB.IsConnected, TimeSpan.FromSeconds(10));
        if (!_clientA.IsConnected || !_clientB.IsConnected)
            throw new Exception($"Clients nicht verbunden (A={_clientA.IsConnected}, B={_clientB.IsConnected})");
        Console.WriteLine("[test] Beide Clients verbunden");

        _clientA.Send(BuildNameMessage(_stateA.Name));
        _clientB.Send(BuildNameMessage(_stateB.Name));
        _clientA.Send(BuildSectorMessage(SectorMain));
        _clientB.Send(BuildSectorMessage(SectorMain));
        WaitUntil(() => _store.GetPlayerSector(_clientA.Id) == SectorMain && _store.GetPlayerSector(_clientB.Id) == SectorMain,
            TimeSpan.FromSeconds(10));
        if (_store.GetPlayerSector(_clientA.Id) != SectorMain || _store.GetPlayerSector(_clientB.Id) != SectorMain)
            throw new Exception("Clients konnten dem Sektor nicht beitreten");
        Console.WriteLine($"[test] Beide Clients im Sektor '{SectorMain}' (A={_clientA.Id}, B={_clientB.Id})");
    }

    private static void StartTicker()
    {
        _stopFlag = new ManualResetEvent(false);
        new Thread(() =>
        {
            while (!_stopFlag.WaitOne(8))
            {
                try
                {
                    _server?.Update();
                    _clientA?.Update();
                    _clientB?.Update();
                }
                catch { }
            }
        }) { IsBackground = true }.Start();
    }

    // ---- Client-Empfang: Spiegel von JobBoardServerSync.HandlePoolIncoming/ApplyPool,
    // Decodierung ueber den ECHTEN JobBoardCodec (kein zweites Wire-Format).
    private static void OnClientMessage(MessageReceivedEventArgs e, ClientState state, Dictionary<string, Dictionary<int, byte[]>> incoming)
    {
        switch ((MessageType)e.MessageId)
        {
            case MessageType.jobBoardDownload:
            {
                string sector = e.Message.GetString();
                e.Message.GetUShort();
                byte transferId = e.Message.GetByte();
                ushort chunkIndex = e.Message.GetUShort();
                ushort totalChunks = e.Message.GetUShort();
                byte[] data = e.Message.GetBytes();
                if (!incoming.TryGetValue(sector, out var buf) || buf.Count >= totalChunks && chunkIndex == 0)
                    buf = new Dictionary<int, byte[]>();
                incoming[sector] = buf;
                buf[chunkIndex] = data;
                if (buf.Count == totalChunks)
                {
                    incoming.Remove(sector);
                    var payload = new byte[buf.Values.Sum(c => c.Length)];
                    int off = 0;
                    foreach (var kv in buf.OrderBy(kv => kv.Key))
                    { Buffer.BlockCopy(kv.Value, 0, payload, off, kv.Value.Length); off += kv.Value.Length; }
                    var (jobs, version) = JobBoardCodec.DecodePool(payload);
                    state.PoolBySector[sector] = jobs.Select(j => j.QuestId).ToList();
                    Console.WriteLine($"[client {state.Name}] Pool empfangen: {state.PoolBySector[sector].Count} Jobs (v{version}, {totalChunks} Chunks)");
                }
                break;
            }
            case MessageType.jobTaken:
            {
                string sector = e.Message.GetString();
                string questId = e.Message.GetString();
                state.TakenEvents.Add(questId);
                if (state.PoolBySector.TryGetValue(sector, out var ids)) ids.Remove(questId);
                Console.WriteLine($"[client {state.Name}] jobTaken empfangen: {questId}");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ helpers
    private static Message BuildNameMessage(string name)
    {
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.SetPlayerName);
        msg.AddUShort(0); // playerId-Placeholder
        msg.AddString(name);
        return msg;
    }

    private static Message BuildSectorMessage(string sector)
    {
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.UpdateSector);
        msg.AddUShort(0); // playerId-Placeholder
        msg.AddString(sector);
        return msg;
    }

    // Identisches Job-Layout zum Client (questId/displayName/displayDescription,
    // paramCount=0 nach Build-343). Strings wie im JobBoardCodec: [int32 len][utf8]
    // — NICHT BinaryWriter/7-bit-Varint, sonst decodiert der echte Codec nicht.
    private static byte[] SerializeJobs(int count, string prefix, int descLen = 120)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Action<string> ws = s =>
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            bw.Write(b.Length);
            bw.Write(b);
        };
        bw.Write(count);
        for (int i = 1; i <= count; i++)
        {
            ws($"{prefix}-{i:D4}");
            ws($"{prefix} Job {i}");
            ws(new string('x', descLen) + $" #{i}");
            bw.Write(0); // paramCount = 0
        }
        bw.Flush();
        return ms.ToArray();
    }

    // Build-368: Replikat des ALTEN Client-Formats (Client/JobBoardServerSync.cs vor dem
    // Fix): BinaryWriter.Write(string) schreibt die String-Laenge als 7-Bit-Varint —
    // genau das, was der Server-Codec nicht lesen kann (Regression-Beweis, Assertion f1).
    private static byte[] SerializeJobs_OldBinaryWriterFormat(int count)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(count);
        for (int i = 1; i <= count; i++)
        {
            bw.Write($"jobOld-{i:D4}");
            bw.Write($"jobOld Job {i}");
            bw.Write(new string('x', 40) + $" #{i}");
            bw.Write(0);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static int PoolCount(ClientState st, string sector)
        => st.PoolBySector.TryGetValue(sector, out var ids) ? ids.Count : 0;

    private static List<string> PoolIds(ClientState st, string sector)
        => st.PoolBySector.TryGetValue(sector, out var ids) ? ids.ToList() : null;

    private static bool LogContains(string needle)
    {
        lock (_logLock) return _serverLog.Any(l => l != null && l.Contains(needle));
    }

    private static void Check(string label, bool ok)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + label);
        if (!ok) _failures++;
    }

    private static bool WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(25);
        }
        return cond();
    }
}
