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
///  (f) Build-368 Wireformat-Beweis (altes 7-Bit-Varint-Format vs. Server-Codec).
///  (g) custom-build-369 Empfangs-Regression: Gross-Pool mit 150+ Chunks, empfangen
///      ueber die ECHTE Empfangs-Assemblierung (common/PoolReceive, dieselbe Klasse wie
///      Client/JobBoardServerSync) bis 'Pool vollstaendig' — das alte 12/12-Muster
///      (Spiegel-Empfaenger, ~40 Chunks) liess den 368er-Empfangsfehler durch.
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

    // custom-build-369: Client-Empfang ueber die ECHTE Assemblierungs-Logik
    // (common/PoolReceive — dieselbe Klasse wie Client/JobBoardServerSync), nicht mehr
    // ueber einen Test-Spiegel. Dadurch deckt der Test jetzt den echten Empfangspfad
    // (transferId-Keying, Reassembly, 'recv:'/'Pool vollstaendig'-Logging) ab.
    private static int _recvChunksA, _recvChunksB;   // 'recv: poolDownload ...'-Logs
    private static int _recvWarnA, _recvWarnB;       // recv-WARN-Logs (Fehler im Empfangspfad)
    private static readonly object _netLock = new object();

    private static readonly PoolReceive.Assembler _recvA = new(
        () => (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
        m => { if (m.StartsWith("recv: poolDownload")) Interlocked.Increment(ref _recvChunksA); Console.WriteLine("[recv A] " + m); },
        m => { Interlocked.Increment(ref _recvWarnA); Console.WriteLine("[recv-WARN A] " + m); });
    private static readonly PoolReceive.Assembler _recvB = new(
        () => (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds,
        m => { if (m.StartsWith("recv: poolDownload")) Interlocked.Increment(ref _recvChunksB); Console.WriteLine("[recv B] " + m); },
        m => { Interlocked.Increment(ref _recvWarnB); Console.WriteLine("[recv-WARN B] " + m); });

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
                0, 42, payload, m => { lock (_netLock) _clientA.Send(m); }, null, stats);
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
            lock (_netLock) _clientB.Send(takenMsg);
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
                0, 77, payloadL, m => { lock (_netLock) _clientA.Send(m); }, drop, null);
            Thread.Sleep(2500);
            Check($"d1: Upload mit {drop.Count} verlorenen Chunks bleibt stuck (Pool NICHT gemerged, count={_store.GetJobCount(SectorLoss)})", _store.GetJobCount(SectorLoss) == 0);
            // Re-Send-Netz (Build-364): kompletter Re-Send mit NEUEM transferId
            ChunkedSend.SendChunked((ushort)MessageType.jobBoardUpload, SectorLoss,
                0, 78, payloadL, m => { lock (_netLock) _clientA.Send(m); }, null, null);
            bool resent = WaitUntil(() => _store.GetJobCount(SectorLoss) == LossJobCount, TimeSpan.FromSeconds(10));
            Check($"d2: Re-Send mit neuem transferId komplettiert den Upload ({_store.GetJobCount(SectorLoss)}/{LossJobCount})", resent);

            // ---- (g) custom-build-369 Regression: ECHTER Empfangspfad mit 150+ Chunks ----
            // Das 12/12-Muster (kleine Pools, ~40 Chunks) liess den 368er-Fehler durch:
            // der Empfang lief im Test als SPIEGEL, nicht als echte Logik, und grosse
            // Transfers (115-226 Chunks in den Spielerlogs) wurden nie gefahren. Hier:
            // 512 Jobs mit 400-Zeichen-Beschreibungen -> ~227KB Payload -> ~250 Chunks
            // Pool-Download, empfangen ueber die ECHTE PoolReceive-Assemblierung (dieselbe
            // Klasse wie Client/JobBoardServerSync) bis 'Pool vollstaendig'.
            const int BigJobCount = 512;   // == MAX_JOBS_PER_SECTOR des Servers
            const int BigDescLen = 400;
            string SectorBig = "TESTSEKTOR_C";
            lock (_netLock) _clientA.Send(BuildSectorMessage(SectorBig));
            lock (_netLock) _clientB.Send(BuildSectorMessage(SectorBig));
            bool bothInBig = WaitUntil(() =>
                _store.GetPlayerSector(_clientA.Id) == SectorBig && _store.GetPlayerSector(_clientB.Id) == SectorBig,
                TimeSpan.FromSeconds(10));
            Check("g0: beide Clients im Gross-Pool-Sektor", bothInBig);
            _recvA.Reset(); _recvB.Reset();
            int recvChunksBeforeA = _recvChunksA, recvChunksBeforeB = _recvChunksB;
            int recvWarnBeforeA = _recvWarnA, recvWarnBeforeB = _recvWarnB;

            byte[] bigPayload = SerializeJobs(BigJobCount, "jobG", descLen: BigDescLen);
            int bigUploadChunks = ChunkedSend.SendChunked((ushort)MessageType.jobBoardUpload, SectorBig,
                0, 91, bigPayload, m => { lock (_netLock) _clientA.Send(m); }, null, null);
            Console.WriteLine($"[test] Gross-Upload gesendet: {BigJobCount} Jobs, {bigPayload.Length} bytes, {bigUploadChunks} Chunks (erwartete Pool-Chunks ~{Math.Ceiling(bigPayload.Length / 900.0)})");
            Check($"g1: Gross-Upload hat 150+ Chunks (tatsaechlich {bigUploadChunks})", bigUploadChunks >= 150);

            bool bigMerged = WaitUntil(() => _store.GetJobCount(SectorBig) == BigJobCount && LogContains(SectorBig), TimeSpan.FromSeconds(20));
            Check($"g2: Gross-Upload gemerged ({_store.GetJobCount(SectorBig)}/{BigJobCount})", bigMerged);

            int bigExpected = Math.Max(1, (bigPayload.Length + ChunkedSend.MAX_CHUNK - 1) / ChunkedSend.MAX_CHUNK);
            bool bigPoolA = WaitUntil(() => PoolCount(_stateA, SectorBig) == BigJobCount, TimeSpan.FromSeconds(20));
            bool bigPoolB = WaitUntil(() => PoolCount(_stateB, SectorBig) == BigJobCount, TimeSpan.FromSeconds(20));
            int recvA = _recvChunksA - recvChunksBeforeA, recvB = _recvChunksB - recvChunksBeforeB;
            Console.WriteLine($"[test] Pool-Download-Empfang: A {recvA}/{bigExpected} recv-Logs, B {recvB}/{bigExpected} recv-Logs");
            Check($"g3: Client A Pool vollstaendig ueber echten Empfangspfad ({PoolCount(_stateA, SectorBig)}/{BigJobCount}, {recvA}/{bigExpected} Chunks empfangen)",
                bigPoolA && recvA >= bigExpected);
            Check($"g4: Client B Pool vollstaendig ueber echten Empfangspfad ({PoolCount(_stateB, SectorBig)}/{BigJobCount}, {recvB}/{bigExpected} Chunks empfangen)",
                bigPoolB && recvB >= bigExpected);
            var bigIdsA = PoolIds(_stateA, SectorBig);
            var bigIdsB = PoolIds(_stateB, SectorBig);
            Check("g5: Gross-Pool inhaltlich identisch bei beiden Clients",
                bigIdsA != null && bigIdsB != null && bigIdsA.Count == BigJobCount && bigIdsA.SequenceEqual(bigIdsB));
            Check("g6: kein Empfangsfehler im echten Pfad (keine recv-WARN-Logs waehrend des Gross-Transfers)",
                _recvWarnA == recvWarnBeforeA && _recvWarnB == recvWarnBeforeB);

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
        _clientA.MessageReceived += (s, e) => OnClientMessage(e, _stateA, _recvA);
        _clientB.MessageReceived += (s, e) => OnClientMessage(e, _stateB, _recvB);
        _clientA.Connect($"127.0.0.1:{ServerPort}"); // Riptide 2.x: Port gehoert in die Host-Adresse
        _clientB.Connect($"127.0.0.1:{ServerPort}");
        WaitUntil(() => _clientA.IsConnected && _clientB.IsConnected, TimeSpan.FromSeconds(10));
        if (!_clientA.IsConnected || !_clientB.IsConnected)
            throw new Exception($"Clients nicht verbunden (A={_clientA.IsConnected}, B={_clientB.IsConnected})");
        Console.WriteLine("[test] Beide Clients verbunden");

        lock (_netLock) _clientA.Send(BuildNameMessage(_stateA.Name));
        lock (_netLock) _clientB.Send(BuildNameMessage(_stateB.Name));
        lock (_netLock) _clientA.Send(BuildSectorMessage(SectorMain));
        lock (_netLock) _clientB.Send(BuildSectorMessage(SectorMain));
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
                    // custom-build-369: Riptide 2.2 Message-Pool ist NICHT thread-safe —
                    // Update-Pump und Main-Thread-Sends muessen sich exclusivieren, sonst
                    // handet die Pool dieselbe Message-Instanz doppelt aus
                    // (InsufficientCapacityException bei Gross-Transfers mit 150+ Chunks).
                    lock (_netLock)
                    {
                        _server?.Update();
                        _clientA?.Update();
                        _clientB?.Update();
                    }
                }
                catch { }
            }
        }) { IsBackground = true }.Start();
    }

    // ---- Client-Empfang: Spiegel von JobBoardServerSync.HandlePoolIncoming/ApplyPool,
    // Decodierung ueber den ECHTEN JobBoardCodec (kein zweites Wire-Format).
    private static void OnClientMessage(MessageReceivedEventArgs e, ClientState state, PoolReceive.Assembler recv)
    {
        switch ((MessageType)e.MessageId)
        {
            case MessageType.jobBoardDownload:
            {
                string sector = e.Message.GetString();
                e.Message.GetUShort(); // senderId-Placeholder
                byte transferId = e.Message.GetByte();
                ushort chunkIndex = e.Message.GetUShort();
                ushort totalChunks = e.Message.GetUShort();
                byte[] data = e.Message.GetBytes();
                // ECHTE Empfangslogik (PoolReceive, identisch zum Mod-Client) —
                // inkl. 'recv: poolDownload ...'-Log und 'Pool vollstaendig'.
                if (recv.HandleChunk(sector, transferId, chunkIndex, totalChunks, data)
                    && recv.Completed.Count > 0)
                {
                    var (doneSector, payload) = recv.Completed.Dequeue();
                    var (jobs, version) = JobBoardCodec.DecodePool(payload);
                    state.PoolBySector[doneSector] = jobs.Select(j => j.QuestId).ToList();
                    Console.WriteLine($"[client {state.Name}] Pool vollstaendig: {jobs.Count} Jobs, v{version} ({totalChunks} Chunks, Sektor '{doneSector}')");
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
