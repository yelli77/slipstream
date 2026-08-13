using Riptide;
using System.Reflection;
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
        private static string lastTrailerModel = "";
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
        private static float nextSendTime = 0f;
        private const float PositionLogIntervalSeconds = 60f;
        private static bool isHonking = false;
        private static bool wasHonking = false;
        private static float honkMaxDistance = 400f;
        
        // Sonity horn SoundEvent cache
        private static Sonity.SoundEvent cachedHornEvent = null;
        private static bool hornEventSearched = false;
        private static System.Reflection.MethodInfo cachedPlayMethod = null;
        private static System.Reflection.MethodInfo cachedStopMethod = null;
        private static System.Reflection.MethodInfo cachedPlayWithParamsMethod = null;
        private static object cachedVolumeParam = null;
        private static bool loggedPlayOverload = false;
        private static System.Collections.Generic.Dictionary<ushort, bool> lastRemoteHonking
            = new System.Collections.Generic.Dictionary<ushort, bool>();
        private static float hornMaxLength = 0f;
        private static bool hornMaxLengthFetched = false;
        private static System.Collections.Generic.Dictionary<ushort, float> honkPlayingUntil
            = new System.Collections.Generic.Dictionary<ushort, float>();

        private static bool isLinked = false;
        private static float nextLinkStatusPollTime = 0f;
        private const float LinkStatusPollIntervalSeconds = 8f;

        public static void FixedUpdate()
        {
            client.Update();
            ReanchorRemotePlayersToFloatingOrigin();
            SmoothTrailerMovement();
            SmoothTruckMovement();
            BillboardNameLabels();
            UpdateMapIndicators();

            if (client.IsConnected && Time.realtimeSinceStartup >= nextPositionLogTime)
            {
                nextPositionLogTime = Time.realtimeSinceStartup + PositionLogIntervalSeconds;
                LogRemotePlayersPeriodically();
            }

            if (client.IsConnected && !isLinked && Time.realtimeSinceStartup >= nextLinkStatusPollTime)
            {
                nextLinkStatusPollTime = Time.realtimeSinceStartup + LinkStatusPollIntervalSeconds;
                try { client.Send(Messages.createRequestLinkStatusMessage(client.Id)); }
                catch (System.Exception ex) { StarTruckMP.Log.LogWarning($"RequestLinkStatus send failed: {ex.Message}"); }
            }
        }

        private static bool isConnecting = false;
        private static float nextConnectAttemptTime = 0f;
        private const float ConnectRetryDelaySeconds = 5f;

        public static void Update()
        {
            // Direkter Online-Modus: kein manueller Verbindungsaufbau mehr noetig. Sobald die
            // fuer den Verbindungsaufbau benoetigten Spielobjekte existieren (Spieler ist im Truck
            // geladen), wird automatisch verbunden. Bei Verbindungsabbruch/-fehler wird nach kurzer
            // Verzoegerung automatisch erneut versucht.
            if (!client.IsConnected && !isConnecting && Time.realtimeSinceStartup >= nextConnectAttemptTime)
            {
                if (GameObject.FindGameObjectWithTag("Player") != null && GameObject.Find("StarTruck(Clone)") != null)
                {
                    StarTruckMP.Log.LogInfo("Auto-Connect: Client Connecting");
                    isConnecting = true;
                    ConnectToServer(StarTruckMP.ServerAddress);
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
            isConnecting = false;
            nextConnectAttemptTime = Time.realtimeSinceStartup + ConnectRetryDelaySeconds;

            foreach (var player in playerList.Values)
            {
                GameObject.Destroy(player.Player);
                GameObject.Destroy(player.Truck);
                if (player.Trailer != null) GameObject.Destroy(player.Trailer);
                if (player.NameLabel != null) GameObject.Destroy(player.NameLabel);
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
            try
            {
                isConnecting = false;
                string myName = GetSteamPersonaName();
                myPlayerName = myName;
                isLinked = false;
                client.Send(Messages.createPlayerNameMessage(client.Id, myName));
                StarTruckMP.Log.LogInfo($"Sent player name: '{myName}'");
                UpdateStatusOverlay();

                // Send SteamID to server (fire-and-forget, non-critical)
                try
                {
                    ulong mySteamId = 0;
                    try
                    {
                        var steamUserType = System.Type.GetType("Steamworks.SteamUser, com.rlabrecque.steamworks.net");
                        if (steamUserType != null)
                        {
                            var getSteamIdMethod = steamUserType.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static);
                            if (getSteamIdMethod != null)
                            {
                                object result = getSteamIdMethod.Invoke(null, null);
                                if (result != null)
                                {
                                    // CSteamID has m_SteamID ulong field
                                    var steamIdField = result.GetType().GetField("m_SteamID");
                                    if (steamIdField != null)
                                        mySteamId = (ulong)steamIdField.GetValue(result);
                                    else
                                    {
                                        // Try implicit conversion or ToString
                                        mySteamId = Convert.ToUInt64(result);
                                    }
                                }
                            }
                        }
                    }
                    catch (System.Exception steamEx)
                    {
                        StarTruckMP.Log.LogWarning($"Steamworks not available: {steamEx.Message}");
                    }

                    client.Send(Messages.createPlayerSteamIdMessage(client.Id, mySteamId));
                    StarTruckMP.Log.LogInfo($"Sent SteamID: {mySteamId}");

                    if (mySteamId != 0)
                    {
                        string steamIdStr = mySteamId.ToString();
                        myLinkCode = steamIdStr.Length >= 6 ? steamIdStr.Substring(steamIdStr.Length - 6) : steamIdStr;
                        UpdateStatusOverlay();
                    }
                }
                catch (System.Exception ex2)
                {
                    StarTruckMP.Log.LogWarning($"Failed to send SteamID: {ex2.Message}");
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"Failed to send player name: {ex.Message}");
            }
            OnArrivedAtSector();
        }

        private static void Client_ClientConnected(object sender, ClientConnectedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Client Connected: {e.Id}");
        }

        public static void Client_ConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Connection Failed");
            isConnecting = false;
            nextConnectAttemptTime = Time.realtimeSinceStartup + ConnectRetryDelaySeconds;
        }

        /// <summary>
        /// Liest den Steam-Anzeigenamen per Reflection aus (gleiches Muster wie die SteamID weiter unten),
        /// damit keine harte Kompilierzeit-Abhaengigkeit auf Steamworks.NET noetig ist. Faellt auf "Player"
        /// zurueck, falls Steamworks nicht verfuegbar ist.
        /// </summary>
        private static string GetSteamPersonaName()
        {
            try
            {
                var steamFriendsType = System.Type.GetType("Steamworks.SteamFriends, com.rlabrecque.steamworks.net");
                if (steamFriendsType != null)
                {
                    var getPersonaNameMethod = steamFriendsType.GetMethod("GetPersonaName", BindingFlags.Public | BindingFlags.Static);
                    if (getPersonaNameMethod != null)
                    {
                        var result = getPersonaNameMethod.Invoke(null, null) as string;
                        if (!string.IsNullOrWhiteSpace(result))
                            return result;
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"Steam-Name konnte nicht gelesen werden: {ex.Message}");
            }
            return "Player";
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
                    string remoteName = e.Message.GetString();
                    if (!playerList.ContainsKey(id))
                    {
                        playerInfo newPlayer = new playerInfo();
                        newPlayer.sector = sector;
                        newPlayer.Name = remoteName;
                        newPlayer.truckTrans.Pos = pPos;
                        newPlayer.truckTrans.Rot = pRot;
                        newPlayer.playerTrans.Pos = pPos;
                        newPlayer.playerTrans.Rot = pRot;
                        playerList.Add(id, newPlayer);
                        RemoveFromSector(id, playerList[id]);
                    }
                }
            }

            if (e.MessageId == (ushort)messageType.playerConnected)
            {
                ushort id = e.Message.GetUShort();
                string remoteName = e.Message.GetString();
                if (!playerList.ContainsKey(id))
                {
                    playerInfo newPlayer = new playerInfo();
                    newPlayer.sector = "none";
                    newPlayer.Name = remoteName;
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
                    bool remoteIsHonking = e.Message.GetBool();

                    playerInfo currentPlayer;
                    bool foundPlayer = playerList.TryGetValue(playerId, out currentPlayer);

                    if (foundPlayer)
                    {
                        if (isTruck)
                        {
                            // Store target for smooth truck interpolation (no hard snap)
                            currentPlayer.truckTargetPos = playerPos;
                            currentPlayer.truckTargetRot = playerRot;
                            // Set velocity on truck Rigidbody for physics extrapolation
                            if (currentPlayer.Truck != null)
                            {
                                var truckRb = currentPlayer.Truck.GetComponent<Rigidbody>();
                                if (truckRb != null)
                                {
                                    truckRb.velocity = playerVel;
                                    truckRb.angularVelocity = playerAngVel;
                                }
                            }
                            currentPlayer.truckTrans.Pos = playerPos;
                            currentPlayer.truckTrans.Rot = playerRot;
                            currentPlayer.truckTrans.Vel = playerVel;
                            currentPlayer.truckTrans.AngVel = playerAngVel;

                            // Player (hidden behind truck) — hard snap is fine since invisible
                            Messages.updateMovement(currentPlayer.Player, playerPos, playerRot, playerVel, playerAngVel);
                            currentPlayer.playerTrans.Pos = playerPos;
                            currentPlayer.playerTrans.Rot = playerRot;
                            currentPlayer.playerTrans.Vel = playerVel;
                            currentPlayer.playerTrans.AngVel = playerAngVel;
                            // Player is in truck — hide suit
                            if (currentPlayer.Player != null)
                            {
                                var suitR = currentPlayer.Player.GetComponentInChildren<MeshRenderer>();
                                if (suitR != null && suitR.enabled) suitR.enabled = false;
                            }
                        }
                        else
                        {
                            Messages.updateMovement(currentPlayer.Player, playerPos, playerRot, playerVel, playerAngVel);
                            currentPlayer.playerTrans.Pos = playerPos;
                            currentPlayer.playerTrans.Rot = playerRot;
                            currentPlayer.playerTrans.Vel = playerVel;
                            currentPlayer.playerTrans.AngVel = playerAngVel;
                            // Player is outside truck (EVA) — show suit
                            if (currentPlayer.Player != null)
                            {
                                var suitR = currentPlayer.Player.GetComponentInChildren<MeshRenderer>();
                                if (suitR != null && !suitR.enabled) suitR.enabled = true;
                            }
                        }
                        playerList[playerId] = currentPlayer;
                        // Receiver-side edge detection: only play on false→true, stop on true→false
                        bool wasRemoteHonking = false;
                        lastRemoteHonking.TryGetValue(playerId, out wasRemoteHonking);
                        if (remoteIsHonking && !wasRemoteHonking && currentPlayer.Truck != null)
                            HandleRemoteHonk(playerId);
                        else if (!remoteIsHonking && wasRemoteHonking && currentPlayer.Truck != null)
                            HandleRemoteHonkStop(playerId);
                        lastRemoteHonking[playerId] = remoteIsHonking;
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
                            currentPlayer.Trailer = Messages.createTrailerMesh(playerId, currentPlayer.trailerModel);
                            // Set initial position immediately so smoothing starts from the right place
                            if (currentPlayer.Trailer != null)
                            {
                                currentPlayer.Trailer.transform.position = trailerPos - floatingOrigin.m_currentOrigin;
                                currentPlayer.Trailer.transform.eulerAngles = trailerRot;
                                currentPlayer.trailerSmoothVel = Vector3.zero;
                                currentPlayer.trailerTargetPos = trailerPos;
                                currentPlayer.trailerTargetRot = trailerRot;
                            }
                        }
                        else if (!hitched && currentPlayer.Trailer != null)
                        {
                            GameObject.Destroy(currentPlayer.Trailer);
                            currentPlayer.Trailer = null;
                        }

                        if (currentPlayer.Trailer != null)
                        {
                            // Set target for per-frame smoothing (no hard-set here)
                            currentPlayer.trailerTargetPos = trailerPos;
                            currentPlayer.trailerTargetRot = trailerRot;
                        }

                        playerList[playerId] = currentPlayer;
                    }
                }
            }

            if (e.MessageId == (ushort)messageType.setPlayerName)
            {
                ushort namePlayerId = e.Message.GetUShort();
                string newName = e.Message.GetString();
                if (namePlayerId != client.Id)
                {
                    playerInfo currentPlayer;
                    if (playerList.TryGetValue(namePlayerId, out currentPlayer))
                    {
                        currentPlayer.Name = newName;
                        if (currentPlayer.NameLabel != null)
                        {
                            GameObject.Destroy(currentPlayer.NameLabel);
                            currentPlayer.NameLabel = null;
                        }
                        if (currentPlayer.Truck != null && !string.IsNullOrEmpty(newName))
                        {
                            currentPlayer.NameLabel = Encoding.Messages.CreateNameLabel(newName, namePlayerId);
                        }
                        playerList[namePlayerId] = currentPlayer;
                        StarTruckMP.Log.LogInfo($"Player {namePlayerId} name set to '{newName}'");
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
                if (clientInfo.NameLabel != null) GameObject.Destroy(clientInfo.NameLabel);
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

            if (e.MessageId == (ushort)messageType.updateTrailerModel)
            {
                ushort trailerPlayerId = e.Message.GetUShort();
                if (trailerPlayerId != client.Id)
                {
                    string containerType = e.Message.GetString();
                    playerInfo clientInfo;
                    playerList.TryGetValue(trailerPlayerId, out clientInfo);
                    string oldModel = clientInfo.trailerModel ?? "";
                    clientInfo.trailerModel = containerType;
                    StarTruckMP.Log.LogInfo($"updateTrailerModel: player {trailerPlayerId} received model='{containerType}' (old='{oldModel}')");
                    // If trailer already spawned with wrong model, respawn it
                    if (!string.IsNullOrEmpty(containerType) && containerType != oldModel && clientInfo.Trailer != null)
                    {
                        StarTruckMP.Log.LogInfo($"updateTrailerModel: player {trailerPlayerId} model changed '{oldModel}' -> '{containerType}', respawning trailer");
                        try { GameObject.Destroy(clientInfo.Trailer); } catch { }
                        clientInfo.Trailer = Messages.createTrailerMesh(trailerPlayerId, containerType);
                        if (clientInfo.Trailer != null)
                        {
                            clientInfo.Trailer.transform.position = clientInfo.trailerTrans.Pos - floatingOrigin.m_currentOrigin;
                            clientInfo.Trailer.transform.eulerAngles = clientInfo.trailerTrans.Rot;
                            clientInfo.trailerSmoothVel = Vector3.zero;
                            clientInfo.trailerTargetPos = clientInfo.trailerTrans.Pos;
                            clientInfo.trailerTargetRot = clientInfo.trailerTrans.Rot;
                            StarTruckMP.Log.LogInfo($"updateTrailerModel: player {trailerPlayerId} trailer respawned OK");
                        }
                        else
                        {
                            StarTruckMP.Log.LogWarning($"updateTrailerModel: player {trailerPlayerId} trailer respawn FAILED");
                        }
                    }
                    playerList[trailerPlayerId] = clientInfo;
                }
            }

            if (e.MessageId == (ushort)messageType.setPlayerSteamId)
            {
                try
                {
                    ushort playerId = e.Message.GetUShort();
                    ulong steamId = e.Message.GetULong();
                    if (playerList.TryGetValue(playerId, out var currentPlayer))
                    {
                        StarTruckMP.Log.LogInfo($"Player {playerId} SteamID: {steamId}");
                    }
                }
                catch (System.Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"setPlayerSteamId error: {ex.Message}");
                }
            }

            if (e.MessageId == (ushort)messageType.linkStatus)
            {
                try
                {
                    bool linked = e.Message.GetBool();
                    if (linked && !isLinked)
                    {
                        isLinked = true;
                        myLinkCode = "";
                        UpdateStatusOverlay();
                        StarTruckMP.Log.LogInfo("Discord link confirmed, hiding link code.");
                    }
                }
                catch (System.Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"linkStatus error: {ex.Message}");
                }
            }
        }

            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"Client_MessageReceived error: {ex.Message}");
            }
        }

        public static void CheckHonk()
        {
            if (!client.IsConnected) return;
            // Use GetKey (held) so honk lasts as long as the key is pressed
            isHonking = UnityEngine.Input.GetKey(StarTruckMP.HonkKey);
        }

        public static void SendMovement()
        {
            if (!client.IsConnected) return;
            if (Time.realtimeSinceStartup < nextSendTime) return;
            nextSendTime = Time.realtimeSinceStartup + StarTruckMP.MovementUpdateMs / 1000f;

            try
            {
                if (myTruck != null && playerLocation)
                {
                    bool honkJustStarted = isHonking && !wasHonking;
                    bool honkJustEnded = !isHonking && wasHonking;
                    bool sendHonk = honkJustStarted || honkJustEnded;
                    if (honkJustStarted || honkJustEnded) wasHonking = isHonking;
                    if (!sentFirstUpdate || sendHonk || (floatingOrigin.m_currentOrigin + myTruck.transform.position) != truckTrans.Pos || myTruck.transform.eulerAngles != truckTrans.Rot || myTruckRigid.velocity != truckTrans.Vel || myTruckRigid.angularVelocity != truckTrans.AngVel)
                    {
                        client.Send(Messages.createMovementMessage(client.Id, floatingOrigin.m_currentOrigin + myTruck.transform.position, myTruck.transform.eulerAngles, myTruckRigid.velocity, myTruckRigid.angularVelocity, true, false, sendHonk));
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

            // Send container model update when hitched container changes
            string currentTrailerModel = "";
            if (hitched && hitchedCargo != null)
            {
                // Use stable type identifier instead of gameObject.name (per-instance ID)
                string typeId = Messages.GetContainerTypeIdentifier(hitchedCargo);
                if (!string.IsNullOrEmpty(typeId))
                {
                    currentTrailerModel = typeId;
                }
                else
                {
                    // Fallback: warn and use gameObject.name as last resort
                    currentTrailerModel = hitchedCargo.gameObject?.name?.Replace("(Clone)", "").Trim() ?? "";
                    StarTruckMP.Log.LogWarning($"SendTrailerMovement: cargo.record.cargoType.containerType is null! Falling back to GO.name='{currentTrailerModel}' — trailer model sync may not match correctly.");
                }
            }
            if (hitched && currentTrailerModel != lastTrailerModel)
            {
                StarTruckMP.Log.LogInfo($"SendTrailerModelUpdate: sending model='{currentTrailerModel}' (old='{lastTrailerModel}')");
                client.Send(Messages.createTrailerModelMessage(client.Id, currentTrailerModel));
                lastTrailerModel = currentTrailerModel;
            }
            else if (!hitched && lastTrailerModel != "")
            {
                StarTruckMP.Log.LogInfo($"SendTrailerModelUpdate: unhitched, clearing model (was '{lastTrailerModel}')");
                lastTrailerModel = "";
            }

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
                UpdateStatusOverlay();

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
                    if (clientInfo.NameLabel != null) { GameObject.Destroy(clientInfo.NameLabel); clientInfo.NameLabel = null; }
                    clientInfo.Truck = null;
                    clientInfo.Player = null;
                    clientInfo.Trailer = null;
                    playerList[clientId] = clientInfo;
                }
            }
            else if (clientInfo.sector == currentSector && clientInfo.Truck == null)
            {
                StarTruckMP.Log.LogInfo($"Spawning player {clientId} in sector '{currentSector}' at pos {playerList[clientId].truckTrans.Pos}");
                playerInfo player = Messages.createPlayer(clientId, playerList[clientId].truckTrans.Pos, playerList[clientId].truckTrans.Rot, currentSector, playerList[clientId].Name);
                clientInfo.Truck = player.Truck;
                clientInfo.Player = player.Player;
                clientInfo.NameLabel = player.NameLabel;
                // Hide spacesuit by default — only show when player is outside truck (EVA)
                if (clientInfo.Player != null)
                {
                    var suitRenderer = clientInfo.Player.GetComponentInChildren<MeshRenderer>();
                    if (suitRenderer != null) suitRenderer.enabled = false;
                }
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

        // Smooth interpolation for remote trailers (no Rigidbody — can't rely on physics extrapolation)
        // Uses Vector3.SmoothDamp for position and Quaternion.Slerp for rotation.
        // smoothTime ~0.1s matches the ~100ms network update interval: the trailer stays close
        // to the target with only a tiny, constant lag — no visible stuttering at any speed.
        private static readonly float TrailerSmoothTime = 0.1f;
        private static readonly float TrailerRotationSpeed = 12f;

        public static void SmoothTrailerMovement()
        {
            foreach (var kv in playerList)
            {
                playerInfo rp = kv.Value;
                if (rp.Trailer == null || !rp.trailerHitched) continue;

                Vector3 targetLocal = rp.trailerTargetPos - floatingOrigin.m_currentOrigin;
                rp.Trailer.transform.position = Vector3.SmoothDamp(
                    rp.Trailer.transform.position,
                    targetLocal,
                    ref rp.trailerSmoothVel,
                    TrailerSmoothTime
                );

                Quaternion targetQuat = Quaternion.Euler(rp.trailerTargetRot);
                rp.Trailer.transform.rotation = Quaternion.Slerp(
                    rp.Trailer.transform.rotation,
                    targetQuat,
                    Time.deltaTime * TrailerRotationSpeed
                );

                playerList[kv.Key] = rp;
            }
        }

        // Smooth velocity-correction for remote trucks.
        // Instead of hard-snapping transform.position (which fights rb.velocity extrapolation),
        // we apply a gentle velocity correction to close the gap between extrapolated and
        // target position. The spring-like correction works WITH the physics, not against it.
        private static readonly float TruckCorrectionK = 5.0f;      // spring constant for position
        private static readonly float TruckRotCorrectionK = 8.0f;   // spring constant for rotation
        private static readonly float TruckMaxCorrection = 10f;      // max correction distance (meters)

        public static void SmoothTruckMovement()
        {
            foreach (var kv in playerList)
            {
                playerInfo rp = kv.Value;
                if (rp.Truck == null) continue;

                Rigidbody rb = rp.Truck.GetComponent<Rigidbody>();
                if (rb == null) continue;

                // Position: velocity-based correction (spring approach)
                Vector3 targetPos = rp.truckTargetPos - floatingOrigin.m_currentOrigin;
                Vector3 error = targetPos - rb.position;
                float errorDist = error.magnitude;
                if (errorDist > TruckMaxCorrection)
                    error = error.normalized * TruckMaxCorrection;

                // Apply correction as velocity addition — works WITH physics, not against it
                rb.velocity = rp.truckTrans.Vel + error * TruckCorrectionK;

                // Rotation: angular velocity correction
                Quaternion targetQuat = Quaternion.Euler(rp.truckTargetRot);
                Quaternion rotError = targetQuat * Quaternion.Inverse(rb.rotation);
                rotError.ToAngleAxis(out float angle, out Vector3 axis);
                if (Mathf.Abs(angle) > 0.01f)
                {
                    if (angle > 180f) angle -= 360f;
                    rb.angularVelocity = rp.truckTrans.AngVel + axis * (angle * Mathf.Deg2Rad) * TruckRotCorrectionK;
                }
                else
                {
                    rb.angularVelocity = rp.truckTrans.AngVel;
                }

                playerList[kv.Key] = rp;
            }
        }

        public static void BillboardNameLabels()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            foreach (var kv in playerList)
            {
                var p = kv.Value;
                if (p.NameLabel != null && p.NameLabel.activeInHierarchy && p.Truck != null)
                {
                    p.NameLabel.transform.position = p.Truck.transform.position + new Vector3(0, 35f, 0);
                    Vector3 dir = p.NameLabel.transform.position - cam.transform.position;
                    if (dir.sqrMagnitude > 0.001f)
                        p.NameLabel.transform.rotation = Quaternion.LookRotation(dir);
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

                                                    
        private static void HandleRemoteHonk(ushort playerId)
        {
            try
            {
                playerInfo rp;
                if (!playerList.TryGetValue(playerId, out rp) || rp.Truck == null) return;

                // --- Don't interrupt a still-playing sequence: let every honk play to completion ---
                float lockUntil;
                if (honkPlayingUntil.TryGetValue(playerId, out lockUntil) && UnityEngine.Time.realtimeSinceStartup < lockUntil)
                    return;

                // --- Lazy-find the horn SoundEvent asset ---
                if (!hornEventSearched)
                {
                    hornEventSearched = true;
                    var allEvents = UnityEngine.Resources.FindObjectsOfTypeAll<Sonity.SoundEvent>();
                    int totalCount = allEvents != null ? allEvents.Length : 0;
                    // Log ALL horn-related SoundEvents for reference
                    string hornNames = "";
                    if (allEvents != null)
                    {
                        foreach (var evt in allEvents)
                        {
                            if (evt != null && !string.IsNullOrEmpty(evt.name) &&
                                evt.name.ToLower().Contains("horn"))
                            {
                                hornNames += evt.name + ", ";
                            }
                        }
                    }
                    StarTruckMP.Log.LogInfo($"HandleRemoteHonk: {totalCount} SoundEvents total, horn-related: [{hornNames}]");

                    // Find truck horn SoundEvent — prefer the EXTERNAL horn (audible at range)
                    // over the interior cabin sound, which is intentionally very short-range.
                    string[] preferredOrder = new string[] {
                        "NPC_Truck_Ext_Horn_Sequence_Neutral_02",
                        "NPC_Truck_Ext_Horn_Sequence_Neutral_01",
                        "NPC_Truck_Ext_Horn_Sequence_Neutral_03",
                        "Truck_Horn_Int_SE"
                    };
                    if (allEvents != null)
                    {
                        foreach (var preferredName in preferredOrder)
                        {
                            foreach (var evt in allEvents)
                            {
                                if (evt != null && evt.name == preferredName)
                                { cachedHornEvent = evt; break; }
                            }
                            if (cachedHornEvent != null) break;
                        }
                    }
                    if (cachedHornEvent != null)
                        StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Using horn SoundEvent '{cachedHornEvent.name}'");
                    else
                        StarTruckMP.Log.LogWarning($"HandleRemoteHonk: No horn SoundEvent found");
                }

                if (cachedHornEvent == null) return;

                if (!hornMaxLengthFetched)
                {
                    hornMaxLengthFetched = true;
                    try
                    {
                        var getMaxLen = cachedHornEvent.GetType().GetMethod("GetMaxLength",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (getMaxLen != null)
                        {
                            hornMaxLength = (float)getMaxLen.Invoke(cachedHornEvent, null);
                            StarTruckMP.Log.LogInfo($"HandleRemoteHonk: GetMaxLength() = {hornMaxLength:F2}s");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"HandleRemoteHonk: GetMaxLength() failed: {ex.Message}");
                    }
                    if (hornMaxLength <= 0f) hornMaxLength = 2.5f; // sane fallback if the API didn't give us a value
                }

                // --- Find Play(Transform) via reflection once ---
                if (cachedPlayMethod == null)
                {
                    var methods = cachedHornEvent.GetType().GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name == "Play")
                        {
                            var p = m.GetParameters();
                            if (p.Length == 1 && p[0].ParameterType == typeof(Transform))
                            {
                                cachedPlayMethod = m;
                                break;
                            }
                        }
                    }
                    if (cachedPlayMethod != null)
                        StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Found Play(Transform) via reflection");
                    else
                        StarTruckMP.Log.LogWarning($"HandleRemoteHonk: Play(Transform) not found on {cachedHornEvent.GetType().FullName}");
                }

                if (cachedPlayMethod == null) return;

                // --- Distance filter: skip if too far ---
                float dist = Vector3.Distance(myTruck.transform.position, rp.Truck.transform.position);
                if (dist > honkMaxDistance) return;

                // --- Build volume boost param once: +12 dB ---
                if (cachedVolumeParam == null)
                {
                    try
                    {
                        // Search ALL loaded assemblies, not just cachedHornEvent's own —
                        // SoundParameterVolumeDecibel lives in Sonity.Runtime, while
                        // SoundEvent lives in Sonity.Public.Runtime (different assembly).
                        Type volType = null;
                        Type updateModeType = null;
                        foreach (var candidateAsm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (volType != null && updateModeType != null) break;
                            Type[] asmTypes;
                            try { asmTypes = candidateAsm.GetTypes(); }
                            catch { continue; }
                            foreach (var t in asmTypes)
                            {
                                if (volType == null && t.Name == "SoundParameterVolumeDecibel") volType = t;
                                if (updateModeType == null && t.Name == "UpdateMode" && t.Namespace != null && t.Namespace.StartsWith("Sonity")) updateModeType = t;
                                if (volType != null && updateModeType != null) break;
                            }
                        }
                        if (volType != null && updateModeType != null)
                        {
                            var ctor = volType.GetConstructor(new Type[] { typeof(float), updateModeType });
                            if (ctor != null)
                            {
                                object updateModeOnce = Enum.GetValues(updateModeType).GetValue(0);
                                foreach (var val in Enum.GetValues(updateModeType))
                                    if (val.ToString() == "Once") { updateModeOnce = val; break; }
                                cachedVolumeParam = ctor.Invoke(new object[] { 24f, updateModeOnce });
                                StarTruckMP.Log.LogInfo($"HandleRemoteHonk: SoundParameterVolumeDecibel(+24dB, {updateModeOnce}) in {volType.Assembly.GetName().Name}");
                            }
                            else
                            {
                                StarTruckMP.Log.LogWarning($"HandleRemoteHonk: SoundParameterVolumeDecibel found in {volType.Assembly.GetName().Name} but no matching ctor(float, UpdateMode)");
                            }
                        }
                        else
                        {
                            StarTruckMP.Log.LogWarning($"HandleRemoteHonk: SoundParameterVolumeDecibel type not found (volType={(volType!=null)}, updateModeType={(updateModeType!=null)})");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"HandleRemoteHonk: volume param error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // --- Find Play(Transform, SoundParameterInternals[]) once ---
                if (cachedPlayWithParamsMethod == null)
                {
                    var methods = cachedHornEvent.GetType().GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name == "Play")
                        {
                            var p = m.GetParameters();
                            if (p.Length == 2 && p[0].ParameterType == typeof(Transform) &&
                                p[1].ParameterType.Name.Contains("SoundParameterInternals"))
                            {
                                cachedPlayWithParamsMethod = m;
                                if (!loggedPlayOverload)
                                {
                                    loggedPlayOverload = true;
                                    var pp = m.GetParameters();
                                    StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Play overload signature: ({pp[0].ParameterType.FullName}, {pp[1].ParameterType.FullName})");
                                }
                                break;
                            }
                        }
                    }
                }

                // --- Play horn at remote truck ---
                if (cachedPlayWithParamsMethod != null && cachedVolumeParam != null)
                {
                    // Plain CLR array via covariance
                    var arr = System.Array.CreateInstance(cachedVolumeParam.GetType(), 1);
                    arr.SetValue(cachedVolumeParam, 0);
                    try
                    {
                        cachedPlayWithParamsMethod.Invoke(cachedHornEvent, new object[] { rp.Truck.transform, arr });
                        honkPlayingUntil[playerId] = UnityEngine.Time.realtimeSinceStartup + hornMaxLength;
                        StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Play(params) OK for player {playerId} dist={dist:F0} vol=+24dB, locked for {hornMaxLength:F2}s");
                    }
                    catch (System.Exception ex)
                    {
                        StarTruckMP.Log.LogWarning($"HandleRemoteHonk: Play(params) failed: {ex.InnerException?.Message ?? ex.Message}");
                        cachedPlayMethod.Invoke(cachedHornEvent, new object[] { rp.Truck.transform });
                        honkPlayingUntil[playerId] = UnityEngine.Time.realtimeSinceStartup + hornMaxLength;
                        StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Play(Transform) fallback for player {playerId} dist={dist:F0}, locked for {hornMaxLength:F2}s");
                    }
                }
                else
                {
                    cachedPlayMethod.Invoke(cachedHornEvent, new object[] { rp.Truck.transform });
                    honkPlayingUntil[playerId] = UnityEngine.Time.realtimeSinceStartup + hornMaxLength;
                    StarTruckMP.Log.LogInfo($"HandleRemoteHonk: Play(Transform) for player {playerId} dist={dist:F0}, locked for {hornMaxLength:F2}s");
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"HandleRemoteHonk error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void HandleRemoteHonkStop(ushort playerId)
        {
            // Intentionally a no-op: the horn sequence (NPC_Truck_Ext_Horn_Sequence_*)
            // should always play to completion once triggered, regardless of when the
            // remote player releases the honk key. Cutting it off early caused stuttering
            // when honking repeatedly. HandleRemoteHonk's honkPlayingUntil lockout already
            // prevents re-triggering while a sequence is still in progress.
        }

        // === MAP PLAYER INDICATORS ===
        // === MAP PLAYER INDICATORS ===
        private static List<GameObject> mapIndicators = new List<GameObject>();

        private static float nextMapRefreshTime = 0f;
        private static bool lastMapOpen = false;

        public static void UpdateMapIndicators()
        {
            try
            {
                bool mapOpen = false;
                int buttonCount = 0;

                try
                {
                    var allBtns = UnityEngine.Object.FindObjectsOfType<MapSectorButton>();
                    buttonCount = allBtns != null ? allBtns.Length : 0;
                    mapOpen = buttonCount > 0;
                }
                catch { }

                // Map just opened — spawn indicators
                if (mapOpen && !lastMapOpen)
                {
                    ClearMapIndicators();
                    SpawnMapIndicators();
                    nextMapRefreshTime = Time.realtimeSinceStartup + 2f;
                }
                // Map just closed — destroy indicators
                else if (!mapOpen && lastMapOpen)
                {
                    ClearMapIndicators();
                }
                // Map still open — update counts in place every 2 seconds
                else if (mapOpen && Time.realtimeSinceStartup >= nextMapRefreshTime)
                {
                    UpdateIndicatorCounts();
                    nextMapRefreshTime = Time.realtimeSinceStartup + 2f;
                }

                lastMapOpen = mapOpen;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"UpdateMapIndicators error: {ex.Message}");
            }
        }

        private static void SpawnMapIndicators()
        {
            try
            {
                var allButtons = UnityEngine.Object.FindObjectsOfType<MapSectorButton>();
                if (allButtons == null || allButtons.Length == 0)
                {
                    StarTruckMP.Log.LogWarning("SpawnMapIndicators: no MapSectorButton objects found");
                    return;
                }

                StarTruckMP.Log.LogInfo($"SpawnMapIndicators: {allButtons.Length} sector buttons, {playerList.Count} players");

                for (int i = 0; i < allButtons.Length; i++)
                {
                    var btn = allButtons[i];
                    if (btn == null) continue;

                    // DIAGNOSTIC: dump ALL sprites on ALL buttons (only first 3 to avoid log flood)
                    if (i < 3)
                    {
                        var allImgs = btn.GetComponentsInChildren<UnityEngine.UI.Image>();
                        int imgCount = allImgs != null ? allImgs.Length : 0;
                        StarTruckMP.Log.LogInfo($"  Button[{i}] '{btn.name}': {imgCount} images");
                        if (allImgs != null)
                        {
                            for (int ii = 0; ii < allImgs.Length; ii++)
                            {
                                var img = allImgs[ii];
                                string spriteName = img.sprite != null ? img.sprite.name : "NULL";
                                StarTruckMP.Log.LogInfo($"    Image[{ii}] '{img.gameObject.name}': sprite='{spriteName}' color=({img.color.r:F2},{img.color.g:F2},{img.color.b:F2},{img.color.a:F2})");
                            }
                        }
                    }

                    string btnSectorName = "";
                    try
                    {
                        var tmps = btn.GetComponentsInChildren<TMPro.TextMeshProUGUI>();
                        if (tmps != null)
                        {
                            foreach (var tmp in tmps)
                            {
                                if (!string.IsNullOrEmpty(tmp.text) && tmp.text.Trim().Length > 1)
                                {
                                    btnSectorName = tmp.text.Trim();
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(btnSectorName))
                    {
                        try
                        {
                            var texts = btn.GetComponentsInChildren<UnityEngine.UI.Text>();
                            if (texts != null)
                            {
                                foreach (var t in texts)
                                {
                                    if (!string.IsNullOrEmpty(t.text) && t.text.Trim().Length > 1)
                                    {
                                        btnSectorName = t.text.Trim();
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    StarTruckMP.Log.LogInfo($"  Button[{i}] '{btn.name}': sectorText='{btnSectorName}'");

                    // Count players in this sector (remote + local)
                    int playerCount = 0;
                    foreach (var kv in playerList)
                    {
                        if (string.IsNullOrEmpty(kv.Value.sector) || kv.Value.sector == "none") continue;
                        string playerDisplay = SectorToDisplayName(kv.Value.sector);
                        if (SectorNamesMatch(playerDisplay, btnSectorName))
                        {
                            playerCount++;
                        }
                    }
                    // Also count local player if in this sector
                    if (!string.IsNullOrEmpty(currentSector) && currentSector != "none")
                    {
                        string localDisplay = SectorToDisplayName(currentSector);
                        if (SectorNamesMatch(localDisplay, btnSectorName))
                        {
                            playerCount++;
                        }
                    }
                    if (playerCount > 0)
                    {
                        CreateMapIndicator(btn, playerCount);
                    }
                }

                if (mapIndicators.Count == 0)
                {
                    StarTruckMP.Log.LogInfo("SpawnMapIndicators: no player-sector matches. Player sectors:");
                    foreach (var kv in playerList)
                    {
                        StarTruckMP.Log.LogInfo($"    Player {kv.Key}: sector='{kv.Value.sector}' -> display='{SectorToDisplayName(kv.Value.sector)}'");
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"SpawnMapIndicators error: {ex.Message}");
            }
        }

        private static string SectorToDisplayName(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || sceneName == "none") return "";
            int lastUnderscore = sceneName.LastIndexOf('_');
            if (lastUnderscore < 0) return sceneName;
            string raw = sceneName.Substring(lastUnderscore + 1);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && char.IsUpper(raw[i]) && !char.IsUpper(raw[i - 1]))
                    sb.Append(' ');
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }

        private static bool SectorNamesMatch(string displayName, string mapLabel)
        {
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(mapLabel)) return false;
            return string.Equals(displayName, mapLabel, StringComparison.OrdinalIgnoreCase);
        }

        private static GameObject statusOverlay;
        private static TMPro.TextMeshProUGUI statusOverlayText;
        private static string myPlayerName = "";
        private static string myLinkCode = "";

        /// <summary>
        /// Small persistent top-left HUD overlay: player name, current sector,
        /// and (until linked) the Discord link code. Stays visible for the
        /// whole session — harmless to leave the code line up after linking,
        /// keeps things simple (no link-status feedback channel needed).
        /// </summary>
        private static void UpdateStatusOverlay()
        {
            try
            {
                string sectorDisplay = (!string.IsNullOrEmpty(currentSector) && currentSector != "none")
                    ? SectorToDisplayName(currentSector)
                    : "—";
                string text = $"{myPlayerName} — {sectorDisplay}";
                if (!string.IsNullOrEmpty(myLinkCode))
                {
                    text += $"\nDiscord-Link-Code: {myLinkCode}";
                }
                else
                {
                    text += "\nSlipstream";
                }

                if (statusOverlayText != null)
                {
                    statusOverlayText.text = text;
                    return;
                }

                var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                TMPro.TextMeshProUGUI sourceTMP = null;
                if (allTMP != null)
                {
                    foreach (var tmp in allTMP)
                    {
                        if (tmp != null && tmp.gameObject.scene.IsValid())
                        {
                            sourceTMP = tmp;
                            break;
                        }
                    }
                }
                if (sourceTMP == null)
                {
                    StarTruckMP.Log.LogWarning("UpdateStatusOverlay: no source TMP found, skipping overlay.");
                    return;
                }

                GameObject canvasObj = new GameObject("StarTruckMP_StatusCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasObj);
                var canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 999;
                canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();

                GameObject labelObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, canvasObj.transform);
                labelObj.name = "StatusLabel";
                var rt = labelObj.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(20f, -20f);
                    rt.sizeDelta = new Vector2(500f, 70f);
                    rt.localScale = Vector3.one;
                }
                statusOverlayText = labelObj.GetComponent<TMPro.TextMeshProUGUI>();
                if (statusOverlayText != null)
                {
                    statusOverlayText.text = text;
                    statusOverlayText.fontSize = 20;
                    statusOverlayText.color = Color.yellow;
                    statusOverlayText.alignment = TMPro.TextAlignmentOptions.TopLeft;
                    statusOverlayText.raycastTarget = false;
                }
                statusOverlay = canvasObj;
                StarTruckMP.Log.LogInfo($"UpdateStatusOverlay: displaying '{text.Replace("\n", " | ")}'");
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"UpdateStatusOverlay error: {ex.Message}");
            }
        }

        private static void CreateMapIndicator(MapSectorButton btn, int playerCount)
        {
            try
            {
                // Root container — centered on the node
                GameObject root = new GameObject($"PlayerCount_{playerCount}");
                root.transform.SetParent(btn.transform, false);
                var rootRT = root.AddComponent<RectTransform>();
                rootRT.anchorMin = new Vector2(0.5f, 0.5f);
                rootRT.anchorMax = new Vector2(0.5f, 0.5f);
                rootRT.anchoredPosition = Vector2.zero; // dead center on node
                rootRT.sizeDelta = new Vector2(80f, 80f); // big enough to cover the gray circle

                // Orange filled circle — NO sprite, use built-in UI default
                GameObject dot = new GameObject("Dot");
                dot.transform.SetParent(root.transform, false);
                var dotRT = dot.AddComponent<RectTransform>();
                dotRT.anchorMin = Vector2.zero;
                dotRT.anchorMax = Vector2.one;
                dotRT.sizeDelta = Vector2.zero;
                dotRT.localScale = Vector3.one;

                var imgComp = dot.AddComponent<UnityEngine.UI.Image>();
                imgComp.color = new Color(1f, 0.3f, 0f, 0.9f); // bright orange, slightly transparent
                // Use Unity's built-in knob sprite for a circle
                imgComp.sprite = CreateCircleSprite();
                imgComp.type = UnityEngine.UI.Image.Type.Simple;
                imgComp.preserveAspect = true;
                imgComp.raycastTarget = false;

                StarTruckMP.Log.LogInfo($"  CreateMapIndicator: dot centered on node, size=80x80");

                // Player count text centered on the dot
                try
                {
                    var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                    TMPro.TextMeshProUGUI sourceTMP = null;
                    if (allTMP != null)
                    {
                        foreach (var tmp in allTMP)
                        {
                            if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                            {
                                sourceTMP = tmp;
                                break;
                            }
                        }
                    }

                    if (sourceTMP != null)
                    {
                        GameObject labelClone = UnityEngine.Object.Instantiate(sourceTMP.gameObject, root.transform);
                        labelClone.name = "CountLabel";
                        var lrt = labelClone.GetComponent<RectTransform>();
                        if (lrt != null)
                        {
                            lrt.anchorMin = new Vector2(0.5f, 0.5f);
                            lrt.anchorMax = new Vector2(0.5f, 0.5f);
                            lrt.anchoredPosition = Vector2.zero;
                            lrt.sizeDelta = new Vector2(80f, 80f);
                            lrt.localScale = Vector3.one;
                        }
                        var labelTMP = labelClone.GetComponent<TMPro.TextMeshProUGUI>();
                        if (labelTMP != null)
                        {
                            labelTMP.text = playerCount.ToString();
                            labelTMP.fontSize = 24;
                            labelTMP.color = Color.white;
                            labelTMP.alignment = TMPro.TextAlignmentOptions.Center;
                            labelTMP.raycastTarget = false;
                        }
                        mapIndicators.Add(labelClone);
                        StarTruckMP.Log.LogInfo($"  CreateMapIndicator: count label '{playerCount}' created");
                    }
                }
                catch (System.Exception ex2)
                {
                    StarTruckMP.Log.LogWarning($"  CreateMapIndicator: count label failed: {ex2.Message}");
                }

                mapIndicators.Add(root);
                StarTruckMP.Log.LogInfo($"  CreateMapIndicator: {playerCount} player(s) at '{btn.name}'");
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"CreateMapIndicator error: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a simple white circle sprite at runtime using a Texture2D.
        /// </summary>
        private static UnityEngine.Sprite CreateCircleSprite()
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size / 2f;
            float radius = size / 2f - 1f;
            Color transparent = new Color(0, 0, 0, 0);
            Color white = Color.white;

            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    tex.SetPixel(x, y, dist <= radius ? white : transparent);
                }
            }
            tex.Apply();

            return UnityEngine.Sprite.Create(tex, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static void UpdateIndicatorCounts()
        {
            try
            {
                // Re-scan all buttons and update existing indicator counts
                var allButtons = UnityEngine.Object.FindObjectsOfType<MapSectorButton>();
                if (allButtons == null || allButtons.Length == 0) return;

                // Build a map of existing indicators by button name
                var existingByButton = new System.Collections.Generic.Dictionary<string, GameObject>();
                foreach (var ind in mapIndicators)
                {
                    if (ind != null && ind.transform.parent != null)
                    {
                        existingByButton[ind.transform.parent.name] = ind;
                    }
                }

                for (int i = 0; i < allButtons.Length; i++)
                {
                    var btn = allButtons[i];
                    if (btn == null) continue;

                    string btnSectorName = "";
                    try
                    {
                        var tmps = btn.GetComponentsInChildren<TMPro.TextMeshProUGUI>();
                        if (tmps != null)
                        {
                            foreach (var tmp in tmps)
                            {
                                if (!string.IsNullOrEmpty(tmp.text) && tmp.text.Trim().Length > 1)
                                {
                                    btnSectorName = tmp.text.Trim();
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    // Count players
                    int playerCount = 0;
                    foreach (var kv in playerList)
                    {
                        if (string.IsNullOrEmpty(kv.Value.sector) || kv.Value.sector == "none") continue;
                        string playerDisplay = SectorToDisplayName(kv.Value.sector);
                        if (SectorNamesMatch(playerDisplay, btnSectorName))
                            playerCount++;
                    }
                    if (!string.IsNullOrEmpty(currentSector) && currentSector != "none")
                    {
                        string localDisplay = SectorToDisplayName(currentSector);
                        if (SectorNamesMatch(localDisplay, btnSectorName))
                            playerCount++;
                    }

                    // Update existing indicator or create/remove as needed
                    if (playerCount > 0)
                    {
                        if (existingByButton.ContainsKey(btn.name))
                        {
                            // Update the count label text
                            var root = existingByButton[btn.name];
                            var countLabel = root.transform.Find("CountLabel");
                            if (countLabel != null)
                            {
                                var tmp = countLabel.GetComponent<TMPro.TextMeshProUGUI>();
                                if (tmp != null && tmp.text != playerCount.ToString())
                                {
                                    tmp.text = playerCount.ToString();
                                }
                            }
                        }
                        else
                        {
                            // New indicator needed
                            SpawnMapIndicators();
                            return; // respawned everything
                        }
                    }
                    else
                    {
                        if (existingByButton.ContainsKey(btn.name))
                        {
                            // Remove indicator for this button
                            var root = existingByButton[btn.name];
                            mapIndicators.Remove(root);
                            GameObject.Destroy(root);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"UpdateIndicatorCounts error: {ex.Message}");
            }
        }

        private static void ClearMapIndicators()
        {
            foreach (var go in mapIndicators)
            {
                if (go != null) GameObject.Destroy(go);
            }
            mapIndicators.Clear();
            StarTruckMP.Log.LogInfo("ClearMapIndicators: destroyed all map indicators");
        }
    }
}
