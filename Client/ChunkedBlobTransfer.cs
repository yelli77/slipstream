using System;
using System.Collections.Generic;
using System.Threading;
using Riptide;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Riptide-Nachrichten haben eine harte Groessenobergrenze (MTU-sicher, in der Praxis ca.
    /// 1200 Bytes nutzbare Kapazitaet). Job-/Cargo-Blobs sind aber schnell 10-100KB gross (siehe
    /// custom-build-178 Log: 40272 bzw. 58801 Bytes - "Cannot add an array... with 9660 bits of
    /// remaining capacity"). Deshalb manuelles Chunking auf Anwendungsebene: der Blob wird in
    /// kleine Stuecke zerlegt und als mehrere Nachrichten verschickt, der Empfaenger setzt sie
    /// wieder zusammen. Der dedizierte Server muss davon nichts wissen - jeder einzelne Chunk
    /// ist fuer sich klein genug und wird ganz normal ueber den bestehenden opaken Byte-Relay
    /// durchgereicht wie jede andere Nachricht (dedicated/MessageHandler.cs wurde auf das neue,
    /// 5-Felder-Wireformat sector+transferId+chunkIndex+totalChunks+bytes angepasst).
    ///
    /// "channel" trennt die Zwischenspeicher fuer parallele Nutzung durch JobBoardSync ("job")
    /// und CargoSync ("cargo") - beide haben unabhaengige Transfer-IDs/Puffer.
    ///
    /// custom-build-324 (Chunk-Pacing): 105KB/106 Chunks kamen beim Empfaenger mit 0/106 an,
    /// waehrend ein kleinerer Cargo-Transfer (87 Chunks) frueher funktionierte. Verdacht:
    /// der komplette Burst wird in EINEM Frame verschickt (alle client.Send() hintereinander),
    /// wodurch 106 Reliable-Pendings + 106 UDP-Datagrams + deren Resend-Timer gleichzeitig
    /// anfallen und das Relay bzw. der Empfaenger-Stack Teile verwirft. Fix: Chunks landen in
    /// einer Send-Queue und werden von ChunkedBlobTransfer.Update() (aufgerufen aus
    /// Client.FixedUpdate, 50Hz) mit max. ChunksPerFrame Chunks pro Frame versendet. Die Queue
    /// ist global FIFO (ueber alle Channels), damit die Reihenfolge Cargo-vor-Job erhalten
    /// bleibt - die Empfaenger erwarten die CargoSync-Objekte vor den referenzierenden Jobs.
    ///
    /// Ausserdem (Root-Cause-Haerte):
    /// - transferId startet nicht mehr bei 0, sondern bei einem Zufallswert pro Client-Start.
    ///   Sonst koennten nach einem Neustart Chunks einer ALTEN Session (gleiche transferId=0)
    ///   fälschlich als Teil des neuen Transfers interpretiert werden.
    /// - Empfaengerseitig gibt es jetzt Diagnose-Logs (erster Chunk, Fortschritt, Abschluss)
    ///   und einen Timeout: kommt fuer einen unvollstaendigen Transfer fuer
    ///   ReceiveTimeoutSeconds kein neuer Chunk, wird der Puffer verworfen und geloggt -
    ///   halbe Transfers blockieren nicht mehr dauerhaft die Channel-Puffer.
    /// </summary>
    public static class ChunkedBlobTransfer
    {
        private const int ChunkPayloadSize = 1000;

        // Pacing: max. so viele Chunks pro FixedUpdate-Frame versenden. 15 Chunks * ~1050B
        // = ~16KB pro Frame (50Hz) = ~800KB/s - schnell genug fuer einen 105KB-Blob (<0,2s),
        // aber klein genug, dass kein Burst mehr in einem einzelnen UDP-Moment ankommt.
        private const int ChunksPerFrame = 15;

        // Empfangsseitiger Timeout: ein unvollstaendiger Transfer, der laenger als so viele
        // Sekunden keinen neuen Chunk bekommen hat, wird verworfen (Log) und der Puffer
        // freigeben. Muss deutlich groesser sein als die Sendezeit eines gepaceten Transfers
        // (106 Chunks / 15 pro Frame / 50Hz = ~0,15s) plus Netz-Resends.
        private const float ReceiveTimeoutSeconds = 15f;

        private class SendState
        {
            public uint nextTransferId;
            // Wartende Chunks des laufenden Transfers dieses Channels (FIFO).
            public readonly Queue<PendingChunk> queue = new Queue<PendingChunk>();
        }

        private class PendingChunk
        {
            public ushort messageTypeId;
            public string sector;
            public byte transferId;
            public ushort chunkIndex;
            public ushort totalChunks;
            public byte[] data;
        }

        private static readonly Dictionary<string, SendState> sendStates = new Dictionary<string, SendState>();
        // Globaler FIFO ueber alle Channels: erhaelt die Cross-Channel-Reihenfolge
        // (CargoSync-Vor-JobBoardSync), auch wenn beide Channels quasi gleichzeitig senden.
        private static readonly Queue<string> sendOrder = new Queue<string>();

        static ChunkedBlobTransfer()
        {
            // Nicht bei 0 starten (siehe Klassendoku): nach einem Client-Neustart ist
            // transferId=0 sonst mit Resten einer alten Session kollidierbar.
            uint seed = (uint)Environment.TickCount;
            // Environment.TickCount kann negativ sein - maskieren. Zusätzlich mischen wir
            // die Prozess-Id hinein, damit zwei Clients nicht denselben Startwert haben.
            nextRandomStart = (uint)(seed ^ (Environment.ProcessId << 16)) & 0xFF;
        }

        private static uint nextRandomStart;

        private class ReceiveState
        {
            public byte transferId;
            public ushort totalChunks;
            public byte[][] chunks;
            public int received;
            public float lastChunkTime; // Time.realtimeSinceStartup des letzten empfangenen Chunks
        }
        // Schluessel ist "channel:senderId" statt nur "channel" - sonst wuerden parallele
        // Transfers zweier verschiedener Spieler (z.B. beide senden beim Sektorwechsel fast
        // gleichzeitig einen Job-/Cargo-Sync) sich denselben Puffer teilen und sich gegenseitig
        // die Chunks ueberschreiben/zerhacken, sobald eine neue transferId reinkommt bevor der
        // andere Transfer fertig ist. Das fuehrte zu korrupten, falsch zusammengesetzten Blobs
        // (kaputte Strings, teils Index-out-of-range beim Deserialisieren).
        private static readonly Dictionary<string, ReceiveState> receiveStates = new Dictionary<string, ReceiveState>();

        public static void Send(string channel, ushort messageTypeId, string sector, byte[] payload)
        {
            var client = StarTruckClient.client;
            if (client == null || !client.IsConnected) return;

            if (!sendStates.TryGetValue(channel, out var state))
            {
                state = new SendState { nextTransferId = nextRandomStart };
                sendStates[channel] = state;
            }
            byte transferId = (byte)(state.nextTransferId++ & 0xFF);
            if (state.nextTransferId > 0xFF) state.nextTransferId = nextRandomStart;

            int totalChunks = (payload.Length + ChunkPayloadSize - 1) / ChunkPayloadSize;
            if (totalChunks == 0) totalChunks = 1; // auch leere Payload als 1 (leerer) Chunk senden

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkPayloadSize;
                int len = Math.Min(ChunkPayloadSize, payload.Length - offset);
                byte[] chunkData = new byte[len];
                Array.Copy(payload, offset, chunkData, 0, len);

                state.queue.Enqueue(new PendingChunk
                {
                    messageTypeId = messageTypeId,
                    sector = sector,
                    transferId = transferId,
                    chunkIndex = (ushort)i,
                    totalChunks = (ushort)totalChunks,
                    data = chunkData,
                });
                sendOrder.Enqueue(channel);
            }

            StarTruckMP.Log.LogInfo($"ChunkedBlobTransfer[{channel}]: {payload.Length} bytes in {totalChunks} Chunk(s) in Send-Queue (transferId={transferId}, Pacing {ChunksPerFrame}/Frame).");
        }

        /// <summary>
        /// Versendet bis zu ChunksPerFrame wartende Chunks. Muss regelmaessig gerufen werden
        /// (Client.FixedUpdate, 50Hz). Wird NICHT gerufen, solange die Verbindung down ist -
        /// dann bleibt die Queue erhalten und wird beim Reconnect weiter abgearbeitet.
        /// </summary>
        public static void Update()
        {
            var client = StarTruckClient.client;
            if (client == null || !client.IsConnected) return;
            if (sendOrder.Count == 0) return;

            int budget = ChunksPerFrame;
            while (budget > 0 && sendOrder.Count > 0)
            {
                string channel = sendOrder.Dequeue();
                if (!sendStates.TryGetValue(channel, out var state) || state.queue.Count == 0)
                    continue; // Stale-Eintrag (Queue zwischenzeitlich geleert) - ueberspringen

                PendingChunk chunk = state.queue.Dequeue();
                var msg = Message.Create(MessageSendMode.Reliable, chunk.messageTypeId);
                msg.AddString(chunk.sector);
                msg.AddByte(chunk.transferId);
                msg.AddUShort(chunk.chunkIndex);
                msg.AddUShort(chunk.totalChunks);
                msg.AddBytes(chunk.data);
                try
                {
                    client.Send(msg);
                }
                catch (Exception ex)
                {
                    // Send-Fehler (z.B. Verbindung gerade weg): Rest des Transfers verwerfen,
                    // sonst bleiben Halb-Transfers in der Queue haengen.
                    StarTruckMP.Log.LogWarning($"ChunkedBlobTransfer[{channel}]: Send von Chunk {chunk.chunkIndex}/{chunk.totalChunks} fehlgeschlagen, Transfer verworfen: {ex.Message}");
                    state.queue.Clear();
                    // Restliche Queue-Eintraege dieses Transfers aus der Global-Queue entfernen:
                    while (sendOrder.Count > 0 && sendOrder.Peek() == channel)
                        sendOrder.Dequeue();
                    continue;
                }
                budget--;
            }
        }

        /// <summary>
        /// Empfaengerseitige Wartung: verwirft unvollstaendige Transfers nach ReceiveTimeoutSeconds
        /// ohne neuen Chunk (Log). Muss regelmaessig gerufen werden (Client.FixedUpdate).
        /// </summary>
        public static void ReceiveMaintenance()
        {
            if (receiveStates.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;

            List<string> expired = null;
            foreach (var kv in receiveStates)
            {
                var state = kv.Value;
                if (state.received < state.totalChunks && now - state.lastChunkTime > ReceiveTimeoutSeconds)
                {
                    (expired ??= new List<string>()).Add(kv.Key);
                }
            }
            if (expired == null) return;

            foreach (var key in expired)
            {
                var state = receiveStates[key];
                StarTruckMP.Log.LogWarning($"ChunkedBlobTransfer: Transfer {key} (transferId={state.transferId}) UNVOLLSTAENDIG verworfen - {state.received}/{state.totalChunks} Chunks nach {ReceiveTimeoutSeconds:0}s ohne neuen Chunk (Timeout).");
                receiveStates.Remove(key);
            }
        }

        /// <summary>
        /// Liest einen Chunk aus der Nachricht und puffert ihn. Gibt true zurueck (mit
        /// completePayload gefuellt), sobald ALLE Chunks eines Transfers eingetroffen sind.
        /// Ein neuer Transfer (andere transferId) verwirft einen evtl. noch unvollstaendigen
        /// alten Puffer fuer diesen Channel automatisch (z.B. nach Verbindungsproblemen).
        /// </summary>
        public static bool TryReceiveChunk(string channel, Message msg, out string sector, out byte[] completePayload)
        {
            sector = msg.GetString();
            ushort senderId = msg.GetUShort();
            byte transferId = msg.GetByte();
            ushort chunkIndex = msg.GetUShort();
            ushort totalChunks = msg.GetUShort();
            byte[] chunkData = msg.GetBytes();

            completePayload = null;

            string key = channel + ":" + senderId;

            bool isNewTransfer = false;
            if (!receiveStates.TryGetValue(key, out var state) || state.transferId != transferId)
            {
                state = new ReceiveState
                {
                    transferId = transferId,
                    totalChunks = totalChunks,
                    chunks = new byte[totalChunks][],
                    received = 0,
                    lastChunkTime = UnityEngine.Time.realtimeSinceStartup,
                };
                receiveStates[key] = state;
                isNewTransfer = true;
            }

            if (chunkIndex < state.chunks.Length && state.chunks[chunkIndex] == null)
            {
                state.chunks[chunkIndex] = chunkData;
                state.received++;
                state.lastChunkTime = UnityEngine.Time.realtimeSinceStartup;
            }

            // Diagnose (custom-build-324): vorher war der Empfaengerkomplett stumm - man konnte
            // aus den Logs nicht unterscheiden, ob Chunks gar nicht ankamen oder nur der
            // Abschluss fehlte. Jetzt: 1x pro neuem Transfer + Fortschritt + Abschluss.
            if (isNewTransfer)
            {
                StarTruckMP.Log.LogInfo($"ChunkedBlobTransfer[{channel}]: Transfer-Start von Sender {senderId} (transferId={transferId}, {totalChunks} Chunks erwartet).");
            }
            else if (state.received < state.totalChunks &&
                     (state.received == 1 || state.received % 25 == 0 || chunkIndex + 1 == totalChunks))
            {
                StarTruckMP.Log.LogInfo($"ChunkedBlobTransfer[{channel}]: {state.received}/{state.totalChunks} Chunks empfangen (transferId={transferId}, zuletzt Chunk {chunkIndex}).");
            }

            if (state.received < state.totalChunks) return false;

            int totalLen = 0;
            foreach (var c in state.chunks) totalLen += c?.Length ?? 0;
            var result = new byte[totalLen];
            int pos = 0;
            foreach (var c in state.chunks)
            {
                if (c == null) continue;
                Array.Copy(c, 0, result, pos, c.Length);
                pos += c.Length;
            }

            StarTruckMP.Log.LogInfo($"ChunkedBlobTransfer[{channel}]: Transfer komplett von Sender {senderId} (transferId={transferId}, {totalChunks}/{totalChunks} Chunks, {totalLen} bytes).");
            completePayload = result;
            receiveStates.Remove(key);
            return true;
        }
    }
}