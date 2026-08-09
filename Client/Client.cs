using Riptide;
using System;
using UnityEngine;
using StarTruckMP.Utilities;
using StarTruckMP.Encoding;
using System.Collections.Generic;
using System.Linq;

namespace StarTruckMP.StarTruckClient
{
    public class StarTruckClient
    {
        public static Client client = new Client();
        public static Dictionary<ushort, playerInfo> playerList = new Dictionary<ushort, playerInfo>();
        public static string currentSector = "none";
        public static movementTrans playerTrans = new movementTrans();
        public static movementTrans truckTrans = new movementTrans();
        public static movementTrans trailerTrans = new movementTrans();
        public static bool trailerHitchedLastSent = false;
        public static bool sentFirstUpdate = false;
        public static bool inTruck = true;
        public static GameObject myPlayer = null;
        public static Rigidbody myPlayerRigid = null;
        public static GameObject myTruck = null;
        public static Rigidbody myTruckRigid = null;
        public static GameObject playerCam = null;
        public static FloatingOriginManager floatingOrigin = null;
        public static PlayerLocation playerLocation = null;
        public static Vector3 lookRot = Vector3.zero;
        public static GameObject spaceSuitObj = null;
        public static Material[] spaceSuitMats = null;
        private static float nextPositionLogTime = 0f;
        private const float PositionLogIntervalSeconds = 60f;

        public static void FixedUpdate()
        {
            client.Update();
            ReanchorRemotePlayersToFloatingOrigin();

            if (client.IsConnected && Time.realtimeSinceStartup >= nextPositionLogTime)
            {
                nextPositionLogTime = Time.realtimeSinceStartup + PositionLogIntervalSeconds;
                LogRemotePlayersPeriodically();
            }
        }

        public static void Update()
        {
            if (UnityEngine.Input.GetKeyDown(StarTruckMP.joinKey.Value) && !StarTruckServer.StarTruckServer.server.IsRunning)
            {
                if (!client.IsConnected)
                {
                    StarTruckMP.Log.LogInfo($"Client Connecting");
                    ConnectToServer(StarTruckMP.IPAddress.Value);
                }
                else
                {
                    StarTruckMP.Log.LogInfo($"Client Disconnecting");
                    client.Disconnect();
                }
            }
        }

        public static void ConnectToServer(string IPAddress)
        {
            sentFirstUpdate = false;
            try
            {
                var connection = client.Connect(IPAddress, 5);
                client.Connected += Client_Connected;
                client.ConnectionFailed += Client_ConnectionFailed;
                client.MessageReceived += Client_MessageReceived;
                client.ClientConnected += Client_ClientConnected;
                client.ClientDisconnected += Client_ClientDisconnected;
                client.Disconnected += Client_Disconnected;

                myPlayer = GameObject.FindGameObjectWithTag("Player");
                if (myPlayer == null) { StarTruckMP.Log.LogError("ConnectToServer: GameObject with tag 'Player' not found."); return; }

                playerCam = GameObject.Find("Main Camera");
                if (playerCam == null) { StarTruckMP.Log.LogError("ConnectToServer: 'Main Camera' not found."); }

                myTruck = GameObject.Find("StarTruck(Clone)");
                if (myTruck == null) { StarTruckMP.Log.LogError("ConnectToServer: 'StarTruck(Clone)' not found."); return; }

                var fomGO = GameObject.Find("[FloatingOriginManager]");
                if (fomGO == null) { StarTruckMP.Log.LogError("ConnectToServer: '[FloatingOriginManager]' not found."); return; }
                floatingOrigin = fomGO.GetComponent<FloatingOriginManager>();

                myPlayerRigid = myPlayer.GetComponent<Rigidbody>();
                myTruckRigid = myTruck.GetComponent<Rigidbody>();
                playerLocation = myPlayer.GetComponent<PlayerLocation>();

                var interior = myTruck.transform.Find("Interior");
                if (interior == null) { StarTruckMP.Log.LogError("ConnectToServer: 'Interior' not found under truck."); return; }
                var suitRoot = interior.transform.Find("SpaceSuit_Root");
                if (suitRoot == null) { StarTruckMP.Log.LogError("ConnectToServer: 'SpaceSuit_Root' not found under Interior."); return; }
                var suitParent = suitRoot.transform.Find("SpaceSuit");
                if (suitParent == null || suitParent.childCount == 0)
                {
                    StarTruckMP.Log.LogError("ConnectToServer: 'SpaceSuit' not found or has no child under SpaceSuit_Root.");
                    return;
                }
                spaceSuitObj = suitParent.GetChild(0).gameObject;
                StarTruckMP.Log.LogInfo($"ConnectToServer: spaceSuitObj resolved to '{spaceSuitObj.name}' (children={spaceSuitObj.transform.childCount})");

                var suitRenderer = spaceSuitObj.GetComponent<MeshRenderer>();
                if (suitRenderer == null)
                {
                    StarTruckMP.Log.LogWarning("ConnectToServer: no MeshRenderer directly on SpaceSuit child, searching children instead.");
                    suitRenderer = spaceSuitObj.GetComponentInChildren<MeshRenderer>();
                }
                if (suitRenderer == null)
                {
                    StarTruckMP.Log.LogError("ConnectToServer: could not find a MeshRenderer anywhere on/under the SpaceSuit object - spaceSuitMats will stay unset.");
                }
                else
                {
                    spaceSuitMats = suitRenderer.materials;
                }

                StarTruckMP.Log.LogInfo($"ConnectToServer setup complete: myPlayer={myPlayer != null}, playerCam={playerCam != null}, myTruck={myTruck != null}, floatingOrigin={floatingOrigin != null}, spaceSuitObj={spaceSuitObj != null}");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogError($"ConnectToServer setup failed: {ex}");
            }
        }

        private static void Client_Disconnected(object sender, DisconnectedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Disconnected from Server: {e.Reason.ToString()}");

            foreach (var player in playerList.Values)
            {
                GameObject.Destroy(player.Player);
                GameObject.Destroy(player.Truck);
                if (player.Trailer != null) GameObject.Destroy(player.Trailer);
            }
            ushort[] keys = playerList.Keys.ToArray<ushort>();
            foreach (var pId in keys) { playerList.Remove(pId); }
        }

        private static void Client_ClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Client disconnected from Server: {e.Id}");
        }

        public static void Client_Connected(object sender, EventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Connected to Server");
            OnArrivedAtSector();
        }

        private static void Client_ClientConnected(object sender, ClientConnectedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Client Connected: {e.Id}");
        }

        public static void Client_ConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Connection Failed");
        }

        public static void Client_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
        {
            if (e.MessageId == (ushort)messageType.clientJoin)
            {
                foreach (ushort id in e.Message.GetUShorts())
                {
                    Vector3 pPos = new Vector3(e.Message.GetFloat(), e.Message.GetFloat(), e.Message.GetFloat());
                    Vector3 pRot = new Vector3(e.Message.GetFloat(), e.Message.GetFloat(), e.Message.GetFloat());
                    string sector = e.Message.GetString();
                    if (!playerList.ContainsKey(id))
                    {
                        playerInfo newPlayer = new playerInfo();
                        newPlayer.sector = sector;
                        newPlayer.truckTrans.Pos = pPos;
                        newPlayer.truckTrans.Rot = pRot;
                        newPlayer.playerTrans.Pos = pPos;
                        newPlayer.playerTrans.Rot = pRot;
                        playerList.Add(id, newPlayer);
                        RemoveFromSector(id, playerList[id]);
                    }
                }
                SendMovement();
            }

            if (e.MessageId == (ushort)messageType.playerConnected)
            {
                ushort id = e.Message.GetUShort();
                if (!playerList.ContainsKey(id))
                {
                    playerInfo newPlayer = new playerInfo();
                    newPlayer.sector = "none";
                    playerList.Add(id, newPlayer);
                }
            }

            if (e.MessageId == (ushort)messageType.movementUpdate)
            {
                ushort playerId = e.Message.GetUShort();

                if (playerId != client.Id)
                {
                    float[] pt = e.Message.GetFloats();

                    Vector3 playerPos;
                    playerPos.x = pt[0];
                    playerPos.y = pt[1];
                    playerPos.z = pt[2];

                    Vector3 playerRot;
                    playerRot.x = pt[3];
                    playerRot.y = pt[4];
                    playerRot.z = pt[5];

                    Vector3 playerVel;
                    playerVel.x = pt[6];
                    playerVel.y = pt[7];
                    playerVel.z = pt[8];

                    Vector3 playerAngVel;
                    playerAngVel.x = pt[9];
                    playerAngVel.y = pt[10];
                    playerAngVel.z = pt[11];

                    bool isTruck = e.Message.GetBool();
                    bool inSeat = e.Message.GetBool();

                    playerInfo currentPlayer;
                    bool foundPlayer = playerList.TryGetValue(playerId, out currentPlayer);

                    if (foundPlayer)
                    {
                        if (isTruck)
                        {
                            Messages.updateMovement(currentPlayer.Truck, playerPos, playerRot, playerVel, playerAngVel);
                            currentPlayer.truckTrans.Pos = playerPos;
                            currentPlayer.truckTrans.Rot = playerRot;
                            currentPlayer.truckTrans.Vel = playerVel;
                            currentPlayer.truckTrans.AngVel = playerAngVel;

                            if (inSeat)
                            {
                                Messages.updateMovement(currentPlayer.Player, playerPos, playerRot, playerVel, playerAngVel);
                                currentPlayer.playerTrans.Pos = playerPos;
                                currentPlayer.playerTrans.Rot = playerRot;
                                currentPlayer.playerTrans.Vel = playerVel;
                                currentPlayer.playerTrans.AngVel = playerAngVel;
                            }
                        }
                        else
                        {
                            Messages.updateMovement(currentPlayer.Player, playerPos, playerRot, playerVel, playerAngVel);
                            currentPlayer.playerTrans.Pos = playerPos;
                            currentPlayer.playerTrans.Rot = playerRot;
                            currentPlayer.playerTrans.Vel = playerVel;
                            currentPlayer.playerTrans.AngVel = playerAngVel;
                        }
                        playerList[playerId] = currentPlayer;
                    }
                }
            }

            if (e.MessageId == (ushort)messageType.trailerMovementUpdate)
            {
                ushort playerId = e.Message.GetUShort();
                if (playerId != client.Id)
                {
                    bool hitched = e.Message.GetBool();
                    float[] tt = e.Message.GetFloats();

                    Vector3 trailerPos;
                    trailerPos.x = tt[0];
                    trailerPos.y = tt[1];
                    trailerPos.z = tt[2];

                    Vector3 trailerRot;
                    trailerRot.x = tt[3];
                    trailerRot.y = tt[4];
                    trailerRot.z = tt[5];

                    playerInfo currentPlayer;
                    bool foundPlayer = playerList.TryGetValue(playerId, out currentPlayer);
                    if (foundPlayer)
                    {
                        currentPlayer.trailerHitched = hitched;
                        currentPlayer.trailerTrans.Pos = trailerPos;
                        currentPlayer.trailerTrans.Rot = trailerRot;

                        if (hitched && currentPlayer.Trailer == null)
                        {
                            currentPlayer.Trailer = Messages.createTrailerMesh(playerId);
                        }
                        else if (!hitched && currentPlayer.Trailer != null)
                        {
                            GameObject.Destroy(currentPlayer.Trailer);
                            currentPlayer.Trailer = null;
                        }

                        if (currentPlayer.Trailer != null)
                        {
                            Messages.updateMovement(currentPlayer.Trailer, trailerPos, trailerRot, Vector3.zero, Vector3.zero);
                        }

                        playerList[playerId] = currentPlayer;
                    }
                }
            }

            if (e.MessageId == (ushort)messageType.clientDisconnect)
            {
                ushort clientId = e.Message.GetUShort();
                playerInfo clientInfo;
                playerList.TryGetValue(clientId, out clientInfo);

                GameObject.Destroy(clientInfo.Truck);
                GameObject.Destroy(clientInfo.Player);
                if (clientInfo.Trailer != null) GameObject.Destroy(clientInfo.Trailer);
                playerList.Remove(clientId);
            }

            if (e.MessageId == (ushort)messageType.updateSector)
            {
                ushort clientId = e.Message.GetUShort();
                if (clientId != client.Id)
                {
                    playerInfo clientInfo;
                    playerList.TryGetValue(clientId, out clientInfo);
                    clientInfo.sector = e.Message.GetString();
                    playerList[clientId] = clientInfo;

                    RemoveFromSector(clientId, clientInfo);
                }
            }

            if (e.MessageId == (ushort)messageType.updateLivery)
            {
                ushort clientId = e.Message.GetUShort();
                if (clientId != client.Id)
                {
                    var livery = e.Message.GetString();
                    playerInfo clientInfo;
                    playerList.TryGetValue(clientId, out clientInfo);
                    clientInfo.livery = livery;
                    playerList[clientId] = clientInfo;
                    if (clientInfo.Truck != null)
                        clientInfo.Truck.transform.GetChild(0).GetComponent<LiveryAndDamageApplierTruckExterior>().LoadAndApplyLiveryById(livery);
                }
            }
        }

            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"Client_MessageReceived error: {ex.Message}");
            }
        }

        public static async void SendMovement()
        {
            while (client.IsConnected)
            {
              try
              {
                if (myTruck != null && playerLocation)
                {
                    if (!sentFirstUpdate || (floatingOrigin.m_currentOrigin + myTruck.transform.position) != truckTrans.Pos || myTruck.transform.eulerAngles != truckTrans.Rot || myTruckRigid.velocity != truckTrans.Vel || myTruckRigid.angularVelocity != truckTrans.AngVel)
                    {
                        client.Send(Messages.createMovementMessage(client.Id, floatingOrigin.m_currentOrigin + myTruck.transform.position, myTruck.transform.eulerAngles, myTruckRigid.velocity, myTruckRigid.angularVelocity, true, false));
                        truckTrans.Pos = floatingOrigin.m_currentOrigin + myTruck.transform.position;
                        truckTrans.Rot = myTruck.transform.eulerAngles;
                        truckTrans.Vel = myTruckRigid.velocity;
                        truckTrans.AngVel = myTruckRigid.angularVelocity;
                    }
                }
                if (myPlayer != null && playerLocation != null)
                {
                    if (!sentFirstUpdate || PlayerLocation.worldPosition != playerTrans.Pos || playerCam.transform.eulerAngles != playerTrans.Rot || myPlayerRigid.velocity != playerTrans.Vel || myPlayerRigid.angularVelocity != playerTrans.AngVel)
                    {
                        client.Send(Messages.createMovementMessage(client.Id, PlayerLocation.worldPosition + new Vector3(0, -1, 0), playerCam.transform.eulerAngles, myPlayerRigid.velocity, myPlayerRigid.angularVelocity, false, false));
                        playerTrans.Pos = PlayerLocation.worldPosition;
                        playerTrans.Rot = playerCam.transform.eulerAngles;
                        playerTrans.Vel = myPlayerRigid.velocity;
                        playerTrans.AngVel = myPlayerRigid.angularVelocity;
                    }
                }

                if (!sentFirstUpdate)
                {
                    sentFirstUpdate = true;
                    StarTruckMP.Log.LogInfo($"SendMovement: forced initial position sync sent (truckPos=({truckTrans.Pos.x:F2}, {truckTrans.Pos.y:F2}, {truckTrans.Pos.z:F2}))");
                }

                SendTrailerMovement();
              }
              catch (System.Exception ex)
              {
                StarTruckMP.Log.LogError($"SendMovement error: {ex.Message}");
              }

                await System.Threading.Tasks.Task.Delay(StarTruckMP.MoveUpdate.Value);
            }

        }

        private const float HitchDistanceThreshold = 50f;

        public static void SendTrailerMovement()
        {
            if (myTruck == null || floatingOrigin == null) return;

            CargoContainer hitchedCargo = null;
            try
            {
                // Find the closest CargoContainer to our truck — distance-based hitch detection
                var allCargo = GameObject.FindObjectsOfType<CargoContainer>();
                float bestDist = HitchDistanceThreshold;
                foreach (var cargo in allCargo)
                {
                    if (cargo == null) continue;
                    float dist = Vector3.Distance(myTruck.transform.position, cargo.transform.position);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        hitchedCargo = cargo;
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"SendTrailerMovement: cargo lookup failed: {ex.Message}");
            }

            bool hitched = hitchedCargo != null;

            if (hitched)
            {
                Vector3 pos = (hitchedCargo.rb != null ? hitchedCargo.rb.position : hitchedCargo.transform.position)
                              + floatingOrigin.m_currentOrigin;
                Vector3 rot = hitchedCargo.transform.eulerAngles;

                client.Send(Messages.createTrailerMovementMessage(client.Id, true, pos, rot));
                trailerHitchedLastSent = true;
            }
            else if (trailerHitchedLastSent)
            {
                client.Send(Messages.createTrailerMovementMessage(client.Id, false, Vector3.zero, Vector3.zero));
                trailerHitchedLastSent = false;
            }
        }

        public static void equipLivery(string livery)
        {
            if (client.IsConnected)
            {
                client.Send(Messages.updateLivery(client.Id, livery));
            }
        }

        public static void OnArrivedAtSector()
        {
            if (client.IsConnected)
            {
                currentSector = GameObject.Find("[Sector]").scene.name;
                client.Send(Messages.updateSector(client.Id, currentSector));
                StarTruckMP.Log.LogInfo($"Entered Sector: {currentSector}");

                foreach (var client in playerList)
                {
                    var cId = client.Key;
                    var c = client.Value;

                    RemoveFromSector(cId, c);
                }
            }
        }

        public static void RemoveFromSector(ushort clientId, playerInfo clientInfo)
        {
            StarTruckMP.Log.LogInfo($"RemoveFromSector check: player {clientId}, theirSector='{clientInfo.sector}', mySector='{currentSector}', hasTruck={clientInfo.Truck != null}");

            if (clientInfo.sector != currentSector)
            {
                if (clientInfo.Truck != null)
                {
                    StarTruckMP.Log.LogInfo($"Despawning player {clientId} (different sector)");
                    GameObject.Destroy(clientInfo.Truck);
                    GameObject.Destroy(clientInfo.Player);
                    if (clientInfo.Trailer != null) GameObject.Destroy(clientInfo.Trailer);
                    clientInfo.Truck = null;
                    clientInfo.Player = null;
                    clientInfo.Trailer = null;
                    playerList[clientId] = clientInfo;
                }
            }
            else if (clientInfo.sector == currentSector && clientInfo.Truck == null)
            {
                StarTruckMP.Log.LogInfo($"Spawning player {clientId} in sector '{currentSector}' at pos {playerList[clientId].truckTrans.Pos}");
                playerInfo player = Messages.createPlayer(clientId, playerList[clientId].truckTrans.Pos, playerList[clientId].truckTrans.Rot, currentSector);
                clientInfo.Truck = player.Truck;
                clientInfo.Player = player.Player;
                playerList[clientId] = clientInfo;
                StarTruckMP.Log.LogInfo($"Spawn result for player {clientId}: truck={(clientInfo.Truck != null ? "OK" : "NULL")}, player={(clientInfo.Player != null ? "OK" : "NULL")}");
            }
        }

        private static Vector3 lastAnchoredOrigin = Vector3.zero;

        public static void ReanchorRemotePlayersToFloatingOrigin()
        {
            if (floatingOrigin == null) return;
            if (floatingOrigin.m_currentOrigin == lastAnchoredOrigin) return;
            lastAnchoredOrigin = floatingOrigin.m_currentOrigin;

            foreach (var kv in playerList)
            {
                var p = kv.Value;
                if (p.Truck != null)
                {
                    p.Truck.transform.position = p.truckTrans.Pos - floatingOrigin.m_currentOrigin;
                }
                if (p.Player != null)
                {
                    p.Player.transform.position = p.playerTrans.Pos - floatingOrigin.m_currentOrigin;
                }
                if (p.Trailer != null)
                {
                    p.Trailer.transform.position = p.trailerTrans.Pos - floatingOrigin.m_currentOrigin;
                }
            }
        }

        public static void LogRemotePlayersPeriodically()
        {
            Vector3 myOrigin = floatingOrigin != null ? floatingOrigin.m_currentOrigin : Vector3.zero;
            Vector3 myTruckAbsPos = (myTruck != null && floatingOrigin != null) ? (floatingOrigin.m_currentOrigin + myTruck.transform.position) : Vector3.zero;
            StarTruckMP.Log.LogInfo($"Local view: myFloatingOrigin=({myOrigin.x:F2}, {myOrigin.y:F2}, {myOrigin.z:F2}), myTruckAbsPos=({myTruckAbsPos.x:F2}, {myTruckAbsPos.y:F2}, {myTruckAbsPos.z:F2}), {playerList.Count} other player(s):");
            if (playerList.Count == 0)
            {
                StarTruckMP.Log.LogInfo("Local view: no other players tracked.");
                return;
            }
            foreach (var kv in playerList)
            {
                var p = kv.Value;
                bool hasTruck = p.Truck != null;
                Vector3 localPos = hasTruck ? p.Truck.transform.position : Vector3.zero;
                bool active = hasTruck && p.Truck.activeInHierarchy;
                StarTruckMP.Log.LogInfo($"  Player {kv.Key}: sector='{p.sector}', hasTruck={hasTruck}, truckLocalScenePos=({localPos.x:F2}, {localPos.y:F2}, {localPos.z:F2}), truckActive={active}, lastKnownAbsPos=({p.truckTrans.Pos.x:F2}, {p.truckTrans.Pos.y:F2}, {p.truckTrans.Pos.z:F2})");
            }
        }
    }
}
