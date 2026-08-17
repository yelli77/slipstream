using System;
using System.Collections.Generic;
using Riptide;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Riptide-Nachrichten haben eine harte Groessenobergrenze (MTU-sicher, in der Praxis ca.
    /// 1200 Bytes nutzbare Kapazitaet). Job-/Cargo-Blobs sind aber schnell 10-60KB gross (siehe
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
    /// </summary>
    public static class ChunkedBlobTransfer
    {
        private const int ChunkPayloadSize = 1000;

        private class SendState { public byte nextTransferId = 0; }
        private static readonly Dictionary<string, SendState> sendStates = new Dictionary<string, SendState>();

        private class ReceiveState
        {
            public byte transferId;
            public ushort totalChunks;
            public byte[][] chunks;
            public int received;
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
                state = new SendState();
                sendStates[channel] = state;
            }
            byte transferId = state.nextTransferId++;

            int totalChunks = (payload.Length + ChunkPayloadSize - 1) / ChunkPayloadSize;
            if (totalChunks == 0) totalChunks = 1; // auch leere Payload als 1 (leerer) Chunk senden

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkPayloadSize;
                int len = Math.Min(ChunkPayloadSize, payload.Length - offset);
                byte[] chunkData = new byte[len];
                Array.Copy(payload, offset, chunkData, 0, len);

                var msg = Message.Create(MessageSendMode.Reliable, messageTypeId);
                msg.AddString(sector);
                msg.AddByte(transferId);
                msg.AddUShort((ushort)i);
                msg.AddUShort((ushort)totalChunks);
                msg.AddBytes(chunkData);
                client.Send(msg);
            }

            StarTruckMP.Log.LogInfo($"ChunkedBlobTransfer[{channel}]: {payload.Length} bytes in {totalChunks} Chunk(s) gesendet (transferId={transferId}).");
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

            if (!receiveStates.TryGetValue(key, out var state) || state.transferId != transferId)
            {
                state = new ReceiveState
                {
                    transferId = transferId,
                    totalChunks = totalChunks,
                    chunks = new byte[totalChunks][],
                    received = 0
                };
                receiveStates[key] = state;
            }

            if (chunkIndex < state.chunks.Length && state.chunks[chunkIndex] == null)
            {
                state.chunks[chunkIndex] = chunkData;
                state.received++;
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

            completePayload = result;
            receiveStates.Remove(key);
            return true;
        }
    }
}
