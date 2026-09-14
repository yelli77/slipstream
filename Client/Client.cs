using Riptide;
using System.Reflection;
using System;
using UnityEngine;
using StarTruckMP.Utilities;
using StarTruckMP.Encoding;
using StarTruckMP.MainMenu;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx;

namespace StarTruckMP.StarTruckClient
{
    public class StarTruckClient
    {
        public static Client client = new Client();
        public static Dictionary<ushort, playerInfo> playerList = new Dictionary<ushort, playerInfo>();
        public static string currentSector = "none";
        public static string currentDestinationGateId = "";

        // BUG 1 (Name-Race) Fix: SetPlayerName-Broadcast kann ankommen, BEVOR der
        // Empfänger den Spieler über clientJoin/playerConnected/movementUpdate in
        // seiner playerList hat — der Name wurde dann still verworfen und das
        //movementUpdate-Registration fiel spaeter auf den Placeholder 'Player_N'
        // zurueck. Spaet ankommende Namen werden hier gepuffert und beim (spaeteren)
        // Registrieren des Spielers angewendet.
        public static readonly Dictionary<ushort, string> pendingNames = new Dictionary<ushort, string>();
        public static movementTrans playerTrans = new movementTrans();
        public static movementTrans truckTrans = new movementTrans();
        public static movementTrans trailerTrans = new movementTrans();
        public static bool trailerHitchedLastSent = false;
        private static string lastTrailerModel = "";
        public static bool sentFirstUpdate = false;
        // Set when a new client joined (playerConnected/clientJoin received): forces exactly
        // ONE full movementUpdate snapshot on the next SendMovement tick, so parked/stationary
        // clients re-announce position + destinationGateId to the newcomer (bug: newcomer saw
        // existing trucks only after they moved).
        public static bool forceStateResend = false;
        private static bool roundtripDone = false; // Build-328: Roundtrip-Verifikation einmalig
        private static int roundtripDiagLogged = 0; // Build-329: Diagnose-Zeile max. 2x (Warum blockiert der Trigger?)
        private static bool roundtripDisabledLogged = false; // Build-329: einmal 'env not set' loggen
        private static float roundtripFallbackAt = -1f; // Build-329: Sektor-Eintritt + 5s Fallback-Trigger
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
        // Cooldown for deferred spawn retries during sector transitions.
        private static System.Collections.Generic.Dictionary<ushort, float> deferredSpawnLastAttempt
            = new System.Collections.Generic.Dictionary<ushort, float>();
        private static float hornMaxLength = 0f;
        private static bool hornMaxLengthFetched = false;
        private static System.Collections.Generic.Dictionary<ushort, float> honkPlayingUntil
            = new System.Collections.Generic.Dictionary<ushort, float>();

        private static bool isLinked = false;
        private static float nextLinkStatusPollTime = 0f;
        private const float LinkStatusPollIntervalSeconds = 8f;

        // Retry sending our sector shortly after connect, in case [Sector] wasn't
        // ready yet at the moment OnArrivedAtSector() was first called.
        private static bool pendingSectorRetry = false;
        private static float nextSectorRetryTime = 0f;
        private static float sectorRetryDeadline = 0f;
        private const float SectorRetryIntervalSeconds = 1f;
        private const float SectorRetryTimeoutSeconds = 15f;

        private static void RetrySendSectorAfterConnect()
        {
            pendingSectorRetry = true;
            nextSectorRetryTime = Time.realtimeSinceStartup + SectorRetryIntervalSeconds;
            sectorRetryDeadline = Time.realtimeSinceStartup + SectorRetryTimeoutSeconds;
        }

        public static void FixedUpdate()
        {
            client.Update();
            ReanchorRemotePlayersToFloatingOrigin();
            SmoothTrailerMovement();
            SmoothTruckMovement();
            BillboardNameLabels();
            UpdateMapIndicators();
            DetectDestinationGates();
            JobBoardSync.TryApplyPending();
            JobBoardIdSync.Update();
            // Build-330: Roundtrip-Trigger = Env-Var ODER Config-Datei. Die Env-Var erreicht den
            // Spiel-Prozess nicht zuverlaessig (Start via steam:// - Steam ist der Elternprozess,
            // unsere Variablen kommen dort nicht an). Die Datei <BepInEx>/config/
            // STRUCKMP_ROUNDTRIP.txt funktioniert startart-unabhaengig (nur Existenz zaehlt).
            string rtEnv = Environment.GetEnvironmentVariable("STRUCKMP_ROUNDTRIP");
            bool rtFile;
            try
            {
                rtFile = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "STRUCKMP_ROUNDTRIP.txt"));
            }
            catch { rtFile = false; }
            if (!roundtripDone)
            {
                if (rtEnv == "1" || rtFile)
                {
                    if (roundtripDiagLogged < 2)
                    {
                        roundtripDiagLogged++;
                        StarTruckMP.Log.LogInfo($"Roundtrip-Wait: QuestTracker.ready={QuestTracker.ready} generator={ProceduralJobGenerator.Get() != null}");
                    }
                    if (QuestTracker.ready && ProceduralJobGenerator.Get() != null)
                    {
                        roundtripDone = true;
                        JobBoardSync.RunRoundtripVerification();
                    }
                }
                else if (!roundtripDisabledLogged)
                {
                    // einmalig loggen - beweist, dass weder Env-Var noch Datei vorhanden sind
                    roundtripDisabledLogged = true;
                    StarTruckMP.Log.LogInfo($"roundtrip disabled (env={rtEnv ?? "<null>"} file={rtFile})");
                }
            }
            // Build-329: Fallback-Trigger - 5s nach Sektor-Eintritt nochmal probieren,
            // falls der normale Pfad oben bis dahin nicht gefeuert hat.
            if (!roundtripDone && roundtripFallbackAt > 0f && Time.realtimeSinceStartup >= roundtripFallbackAt)
            {
                roundtripFallbackAt = -1f; // nur ein Fallback-Versuch
                string fbEnv = Environment.GetEnvironmentVariable("STRUCKMP_ROUNDTRIP");
                bool fbFile;
                try
                {
                    fbFile = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "STRUCKMP_ROUNDTRIP.txt"));
                }
                catch { fbFile = false; }
                StarTruckMP.Log.LogInfo($"Roundtrip-Fallback (5s nach Sektor): env={fbEnv ?? "<null>"} file={fbFile} ready={QuestTracker.ready} generator={ProceduralJobGenerator.Get() != null}");
                if ((fbEnv == "1" || fbFile) && QuestTracker.ready && ProceduralJobGenerator.Get() != null)
                {
                    roundtripDone = true;
                    JobBoardSync.RunRoundtripVerification();
                }
            }
            ChunkedBlobTransfer.Update();
            ChunkedBlobTransfer.ReceiveMaintenance();

            if (pendingSectorRetry && client.IsConnected && Time.realtimeSinceStartup >= nextSectorRetryTime)
            {
                try
                {
                    OnArrivedAtSector();
                    DockingBayHUD.OnSectorChanged();
                    WarpGateBillboard.OnSectorChanged();
                    JumpgateOption1.CreateBoards();
                    pendingSectorRetry = false;
                }
                catch (System.Exception exSectorRetry)
                {
                    if (Time.realtimeSinceStartup >= sectorRetryDeadline)
                    {
                        pendingSectorRetry = false;
                        StarTruckMP.Log.LogWarning($"Giving up sending initial sector after retries: {exSectorRetry.Message}");
                    }
                    else
                    {
                        nextSectorRetryTime = Time.realtimeSinceStartup + SectorRetryIntervalSeconds;
                    }
                }
            }

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

        // Wird gesetzt, wenn der Server die Verbindung wegen veralteter Mod-Version abgelehnt hat.
        // Blockiert weitere automatische Reconnect-Versuche - erneutes Verbinden waere ohnehin
        // sinnlos, ohne dass der Spieler zuerst Slipstream aktualisiert.
        public static bool versionRejected = false;
        public static string versionRejectedReason = "";
        // Server voll: Server hat maximale Spieleranzahl erreicht. Blockiert automatischen
        // Reconnect (sinnlos, waere sofort wieder abgelehnt) und zeigt dem Spieler eine
        // sichtbare Meldung im Status-Overlay.
        public static bool serverFullRejected = false;
        public static string serverFullMessage = "";
        private static float nextConnectAttemptTime = 0f;
        // War 5s, auf Wunsch (nach Test mit schnellem Offline/Online-Hin-und-Her-Klicken) auf 10s
        // erhoeht: dem Server muss genug Zeit bleiben, den alten Disconnect sauber abzuschliessen
        // (SteamID-basierter Ghost-Fix, _players-Aufraeumen, ClientDisconnect-Broadcast an alle),
        // bevor derselbe Spieler wieder als neue Verbindung reinkommt. Nebenbei eine kleine
        // Spam-Bremse gegen sehr schnelles Reconnect-Toggeln.
        private const float ConnectRetryDelaySeconds = 10f;

        // Setzt die Reconnect-Sperre sofort, statt nur auf das asynchrone Disconnected-Event zu
        // warten - wird sowohl von dort als auch direkt beim manuellen Offline-Umschalten
        // (OnlineModeToggle) aufgerufen, damit die Sperre garantiert in dem Moment aktiv ist, in
        // dem der Disconnect angestossen wird, nicht erst wenn das Event irgendwann durchkommt.
        public static void ArmReconnectCooldown()
        {
            nextConnectAttemptTime = Time.realtimeSinceStartup + ConnectRetryDelaySeconds;
        }

        public static void Update()
        {
            // Direkter Online-Modus: kein manueller Verbindungsaufbau mehr noetig. Sobald die
            // fuer den Verbindungsaufbau benoetigten Spielobjekte existieren (Spieler ist im Truck
            // geladen), wird automatisch verbunden. Bei Verbindungsabbruch/-fehler wird nach kurzer
            // Verzoegerung automatisch erneut versucht. Nur wenn der Online/Offline-Umschalter im
            // Hauptmenue auf Online steht.
            // StarTruckMP.LaunchedViaSlipstream als zusaetzliche, explizite Absicherung (defense
            // in depth): OnlineModeEnabled kann eigentlich ohnehin nie true werden, wenn der
            // Launcher-Marker fehlt, weil dann schon gar kein Umschalter-Button erzeugt wird (siehe
            // OnlineModeToggle.CreateToggleButton) - trotzdem hier nochmal hart geprueft, damit ein
            // automatischer Verbindungsversuch bei einem Nicht-Slipstream-Start unter keinen
            // Umstaenden stattfinden kann.
            if (StarTruckMP.LaunchedViaSlipstream && OnlineModeToggle.OnlineModeEnabled && !versionRejected && !serverFullRejected && !client.IsConnected && !isConnecting && Time.realtimeSinceStartup >= nextConnectAttemptTime)
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
                // WICHTIG: erst ab-, dann wieder anmelden (idempotent). ConnectToServer wird bei
                // jedem Reconnect (Auto-Connect-Retry, manuelles Online/Offline-Umschalten) erneut
                // aufgerufen: ohne dieses Unsubscribe-zuerst-Pattern haeufte sich pro Reconnect eine
                // weitere Subscription auf denselben statischen "client" an. Ergebnis: jede
                // eingehende Nachricht loeste Client_MessageReceived mehrfach fuer dasselbe Message-
                // Objekt aus - der erste Aufruf las die Felder komplett (Lesecursor am Ende), der
                // zweite/dritte Aufruf las dieselbe Nachricht dann ab einer bereits erschoepften
                // Position weiter und lief ins Leere: IndexOutOfRangeException/OverflowException in
                // GetFloats()/GetUShort() bei movementUpdate/trailerMovementUpdate, dazu
                // Geister-Spieler wie 'Player 0' mit leerem Namen/Sektor aus so einem Fehl-Read.
                // Gleichzeitig erklaert es die doppelten "Connected to Server"/"Sent player name"-
                // Logzeilen, da auch Client_Connected mehrfach subscribed war.
                client.Connected -= Client_Connected;
                client.ConnectionFailed -= Client_ConnectionFailed;
                client.MessageReceived -= Client_MessageReceived;
                client.ClientConnected -= Client_ClientConnected;
                client.ClientDisconnected -= Client_ClientDisconnected;
                client.Disconnected -= Client_Disconnected;

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
            ArmReconnectCooldown();

            if (e.Reason == DisconnectReason.Kicked)
            {
                string reason = "";
                try { if (e.Message != null) reason = e.Message.GetString(); } catch { }
                if (!string.IsNullOrEmpty(reason))
                {
                    versionRejected = true;
                    versionRejectedReason = reason;
                    StarTruckMP.Log.LogWarning($"Verbindung vom Server abgelehnt: {reason}");
                }
            }

            foreach (var player in playerList.Values)
            {
                GameObject.Destroy(player.Player);
                GameObject.Destroy(player.Truck);
                if (player.Trailer != null) GameObject.Destroy(player.Trailer);
                if (player.NameLabel != null) GameObject.Destroy(player.NameLabel);
            }
            ushort[] keys = playerList.Keys.ToArray<ushort>();
            foreach (var pId in keys) { playerList.Remove(pId); }

            if (statusOverlay != null && !serverFullRejected)
            {
                statusOverlay.SetActive(false);
            }

            // Ohne dieses Reset zaehlt die Kartenanzeige den lokalen Spieler weiterhin im zuletzt
            // bekannten Sektor mit (SpawnMapIndicators/UpdateMapIndicators pruefen currentSector
            // unabhaengig vom tatsaechlichen Verbindungsstatus).
            DockingBayHUD.Cleanup();
            WarpGateBillboard.Cleanup();
            JumpgateOption1.Cleanup();
            TruckTrafficDisabler.Cleanup();
            currentSector = "none";
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

                client.Send(Messages.createClientVersionMessage(client.Id, StarTruckMP.protocolBuildNumber));
                StarTruckMP.Log.LogInfo($"Sent client version: {StarTruckMP.protocolBuildNumber}");

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
            try { OnArrivedAtSector(); DockingBayHUD.OnSectorChanged(); WarpGateBillboard.OnSectorChanged(); JumpgateOption1.CreateBoards(); }
            catch (System.Exception exSector)
            {
                StarTruckMP.Log.LogWarning($"OnArrivedAtSector at connect failed, will retry: {exSector.Message}");
                RetrySendSectorAfterConnect();
            }
        }

        private static void Client_ClientConnected(object sender, ClientConnectedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Client Connected: {e.Id}");
        }

        public static void Client_ConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
            StarTruckMP.Log.LogInfo($"Connection Failed (Reason: {e.Reason})");
            isConnecting = false;
            ArmReconnectCooldown();

            if (e.Reason == RejectReason.ServerFull)
            {
                serverFullRejected = true;
                serverFullMessage = "Server ist voll — zu viele Spieler";
                StarTruckMP.Log.LogWarning("Server ist voll: maximale Spieleranzahl erreicht.");
                ShowServerFullOverlay();
            }
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
                    string remoteDestGate = "";
                    try { remoteDestGate = e.Message.GetString(); } catch { }
                    if (!playerList.ContainsKey(id))
                    {
                        playerInfo newPlayer = new playerInfo();
                        newPlayer.Trailers = new Dictionary<long, GameObject>();
                        newPlayer.trailerExtraTargets = new Dictionary<long, movementTrans>();
                        newPlayer.trailerExtraDriftTimers = new Dictionary<long, float>();
                        newPlayer.trailerExtraSmoothVels = new Dictionary<long, Vector3>();
                        newPlayer.sector = sector;
                        // Bug 1: gepufferten (frueheren) SetPlayerName anwenden, falls er vor der Registrierung kam.
                        if (pendingNames.TryGetValue(id, out string pendingJoinName) && !string.IsNullOrEmpty(pendingJoinName))
                        {
                            newPlayer.Name = pendingJoinName;
                            pendingNames.Remove(id);
                            StarTruckMP.Log.LogInfo($"clientJoin: applied buffered name '{pendingJoinName}' for player {id}");
                        }
                        else newPlayer.Name = remoteName;
                        newPlayer.destinationGateId = remoteDestGate;
                        JumpgateOption1.ForceRefresh();
                        forceStateResend = true;
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
                string remoteSector = "none";
                try { remoteSector = e.Message.GetString(); } catch { }
                if (!playerList.ContainsKey(id))
                {
                    playerInfo newPlayer = new playerInfo();
                    newPlayer.Trailers = new Dictionary<long, GameObject>();
                    newPlayer.trailerExtraTargets = new Dictionary<long, movementTrans>();
                    newPlayer.trailerExtraDriftTimers = new Dictionary<long, float>();
                    newPlayer.trailerExtraSmoothVels = new Dictionary<long, Vector3>();
                    newPlayer.sector = string.IsNullOrEmpty(remoteSector) ? "none" : remoteSector;
                    // Bug 1: gepufferten (frueheren) SetPlayerName anwenden, falls er vor der Registrierung kam.
                    if (pendingNames.TryGetValue(id, out string pendingConnName) && !string.IsNullOrEmpty(pendingConnName))
                    {
                        newPlayer.Name = pendingConnName;
                        pendingNames.Remove(id);
                        StarTruckMP.Log.LogInfo($"playerConnected: applied buffered name '{pendingConnName}' for player {id}");
                    }
                    else newPlayer.Name = remoteName;
                    playerList.Add(id, newPlayer);
                    forceStateResend = true;
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
                    string remoteDestGate = "";
                    try { remoteDestGate = e.Message.GetString(); } catch { }

                    playerInfo currentPlayer;
                    bool foundPlayer = playerList.TryGetValue(playerId, out currentPlayer);

                    // ON-THE-FLY REGISTRATION: movementUpdates from players we never
                    // registered (clientJoin/playerConnected lost or raced) were silently
                    // dropped, so the truck never spawned and the departure board stayed
                    // empty. Register here from the message itself — id + destinationGateId
                    // are known, the name is a placeholder and our own currentSector is the
                    // best available guess. A later updateSector can still correct the sector.
                    if (!foundPlayer)
                    {
                        currentPlayer = new playerInfo();
                        currentPlayer.Trailers = new Dictionary<long, GameObject>();
                        currentPlayer.trailerExtraTargets = new Dictionary<long, movementTrans>();
                        currentPlayer.trailerExtraDriftTimers = new Dictionary<long, float>();
                        currentPlayer.trailerExtraSmoothVels = new Dictionary<long, Vector3>();
                        currentPlayer.sector = currentSector;
                        // Bug 1: echten Namen aus pendingNames bevorzugen — der Placeholder
                        // 'Player_N' wurde sonst dauerhaft angezeigt, wenn das Name-Broadcast
                        // vor der Registrierung ankam und verworfen wurde.
                        if (pendingNames.TryGetValue(playerId, out string pendingMvName) && !string.IsNullOrEmpty(pendingMvName))
                        {
                            currentPlayer.Name = pendingMvName;
                            pendingNames.Remove(playerId);
                            StarTruckMP.Log.LogInfo($"movementUpdate: applied buffered name '{pendingMvName}' for player {playerId}");
                        }
                        else currentPlayer.Name = $"Player_{playerId}";
                        currentPlayer.destinationGateId = remoteDestGate;
                        currentPlayer.truckTrans.Pos = playerPos;
                        currentPlayer.truckTrans.Rot = playerRot;
                        currentPlayer.playerTrans.Pos = playerPos;
                        currentPlayer.playerTrans.Rot = playerRot;
                        playerList.Add(playerId, currentPlayer);
                        JumpgateOption1.ForceRefresh();
                        StarTruckMP.Log.LogInfo($"movementUpdate: registered unknown player {playerId} on the fly (sector={currentSector}, destGate='{remoteDestGate}')");
                    }

                    // Spawn-Guard (318, Bug B): Spieler in einem ANDEREN Sektor duerfen hier
                    // KEINEN Spawn ausloesen. Nach eigenem Jumpgate-Sprung laufen sonst
                    // movementUpdates fuer den fremden Spieler in den deferred-spawn-Pfad
                    // und erzeugen sein Truck im EIGENEN (falschen) Sektor — "nach jump
                    // durchs gate habe ich den anderen nicht mehr gesehen". Nur Tracking-
                    // Daten (Pos/Rot/destGate) aktualisieren; der Spawn passiert spaeter,
                    // wenn updateSector den Spieler in den eigenen Sektor bringt (dort
                    // ruft RemoveFromSector auf und spawnt sofort korrekt).
                    bool inMySector = string.IsNullOrEmpty(currentPlayer.sector)
                        || currentPlayer.sector == "none"
                        || string.IsNullOrEmpty(currentSector)
                        || currentSector == "none"
                        || currentPlayer.sector == currentSector;

                    // Proceed — currentPlayer was just ensured above (existing entry or
                    // freshly registered), so handle this movementUpdate unconditionally.
                    {
                        if (!inMySector)
                        {
                            // Fremder Sektor: nur Tracking-Daten, kein Spawn/Appear.
                            currentPlayer.truckTrans.Pos = playerPos;
                            currentPlayer.truckTrans.Rot = playerRot;
                            currentPlayer.truckTrans.Vel = playerVel;
                            currentPlayer.truckTrans.AngVel = playerAngVel;
                            currentPlayer.playerTrans.Pos = playerPos;
                            currentPlayer.playerTrans.Rot = playerRot;
                            bool gateChanged = remoteDestGate != currentPlayer.destinationGateId;
                            currentPlayer.destinationGateId = remoteDestGate;
                            if (gateChanged) JumpgateOption1.ForceRefresh();
                            lastRemoteHonking[playerId] = remoteIsHonking;
                            playerList[playerId] = currentPlayer;
                        }
                        else if (isTruck)
                        {
                            // DEFERRED SPAWN: if truck doesn't exist yet (e.g. playerConnected
                            // sent no position, RemoveFromSector deferred), create it here at
                            // the correct position from the first movementUpdate.
                            if (currentPlayer.Truck == null)
                            {
                                // Build 319 (Michael): Kein Spawn aus einer Zero/NaN-Position
                                // (Join-Race: playerConnected/clientJoin ohne echte Position).
                                // Warten auf die erste echte movementUpdate-Position — die
                                // spawnt den Neuling dann exakt an seiner Netzwerk-Position.
                                if (playerPos.sqrMagnitude < 1f ||
                                    float.IsNaN(playerPos.x) || float.IsNaN(playerPos.y) || float.IsNaN(playerPos.z))
                                {
                                    StarTruckMP.Log.LogWarning($"movementUpdate: REFUSED deferred spawn for player {playerId} — zero/NaN position");
                                    playerList[playerId] = currentPlayer;
                                }
                                else
                                {
                                // Throttle retries during sector transitions: createPlayer may
                                // return a fallback (Truck=null) when [Sector]/myTruck are null.
                                float lastTry;
                                deferredSpawnLastAttempt.TryGetValue(playerId, out lastTry);
                                if (UnityEngine.Time.time - lastTry < 1.0f)
                                {
                                    // Too soon — just store latest position for next attempt
                                    bool gateChanged = remoteDestGate != currentPlayer.destinationGateId;
                                    currentPlayer.truckTrans.Pos = playerPos;
                                    currentPlayer.truckTrans.Rot = playerRot;
                                    currentPlayer.destinationGateId = remoteDestGate;
                                    if (gateChanged) JumpgateOption1.ForceRefresh();
                                }
                                else
                                {
                                deferredSpawnLastAttempt[playerId] = UnityEngine.Time.time;
                                StarTruckMP.Log.LogInfo($"movementUpdate: deferred spawn for player {playerId} at {playerPos}");
                                playerInfo spawned = Messages.createPlayer(playerId, playerPos, playerRot, currentPlayer.sector, currentPlayer.Name);
                                if (spawned.Truck == null)
                                {
                                    // createPlayer deferred (sector transition) — try again next tick
                                    bool gateChanged = remoteDestGate != currentPlayer.destinationGateId;
                                    currentPlayer.truckTrans.Pos = playerPos;
                                    currentPlayer.truckTrans.Rot = playerRot;
                                    currentPlayer.destinationGateId = remoteDestGate;
                                    if (gateChanged) JumpgateOption1.ForceRefresh();
                                }
                                else
                                {
                                currentPlayer.Truck = spawned.Truck;
                                currentPlayer.Player = spawned.Player;
                                currentPlayer.NameLabel = spawned.NameLabel;
                                currentPlayer.truckTargetPos = playerPos;
                                currentPlayer.truckTargetRot = playerRot;
                                currentPlayer.spawnTime = UnityEngine.Time.time;
                                currentPlayer.isColliding = false;
                                // Hide spacesuit
                                if (currentPlayer.Player != null)
                                {
                                    var suitR = currentPlayer.Player.GetComponentInChildren<MeshRenderer>();
                                    if (suitR != null) suitR.enabled = false;
                                }
                                // Attach collision helper
                                if (currentPlayer.Truck != null)
                                {
                                    var helper = currentPlayer.Truck.AddComponent<RemoteTruckCollisionHelper>();
                                    if (helper != null) helper.Init(playerId);
                                }
                                // Hard-snap to correct position - no spring correction needed
                                var origin = floatingOrigin;
                                Vector3 localPos = (origin != null) ? playerPos - origin.m_currentOrigin : playerPos;
                                currentPlayer.Truck.transform.position = localPos;
                                currentPlayer.Truck.transform.eulerAngles = playerRot;
                                var spawnedRb = currentPlayer.Truck.GetComponent<Rigidbody>();
                                if (spawnedRb != null)
                                {
                                    spawnedRb.position = localPos;
                                    spawnedRb.rotation = Quaternion.Euler(playerRot);
                                    spawnedRb.velocity = playerVel;
                                    spawnedRb.angularVelocity = playerAngVel;
                                }
                                SnapRemotePlayerToLocal(playerId, currentPlayer);
                                } // end else (Truck != null)
                                } // end zero/NaN position guard
                                } // end else (cooldown)
                            }
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
                        // Receiver-side edge detection: only play on false→true, stop on true→false
                        bool wasRemoteHonking = false;
                        lastRemoteHonking.TryGetValue(playerId, out wasRemoteHonking);
                        if (remoteIsHonking && !wasRemoteHonking && currentPlayer.Truck != null)
                            HandleRemoteHonk(playerId);
                        else if (!remoteIsHonking && wasRemoteHonking && currentPlayer.Truck != null)
                            HandleRemoteHonkStop(playerId);
                        lastRemoteHonking[playerId] = remoteIsHonking;
                        if (remoteDestGate != currentPlayer.destinationGateId)
                        {
                            currentPlayer.destinationGateId = remoteDestGate;
                            JumpgateOption1.ForceRefresh();
                        }
                        else
                        {
                            currentPlayer.destinationGateId = remoteDestGate;
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
                    string remoteTrailerModel = e.Message.GetString();

                    playerInfo currentPlayer;
                    bool foundPlayer = playerList.TryGetValue(playerId, out currentPlayer);
                    if (!foundPlayer) goto skipTrailer;

                    // v1 multi-trailer detection: px < 0 and sentinel string "MULTI"
                    bool isMultiTrailer = tt[0] < -0.5f && remoteTrailerModel == "MULTI";

                    if (isMultiTrailer)
                    {
                        // v1 format: read trailer array
                        int trailerCount = (int)tt[1];
                        e.Message.GetUShort(); // skip sentinel ushort (was already read as part of the 6 floats)
                        // Actually, the ushort trailerCount is AFTER the string
                        ushort countFromMsg = e.Message.GetUShort();
                        trailerCount = countFromMsg;

                        // Track which tracking IDs we see this frame
                        var seenIds = new System.Collections.Generic.HashSet<long>();

                        for (int i = 0; i < trailerCount; i++)
                        {
                            long trackingId = e.Message.GetLong();
                            string containerType = e.Message.GetString();
                            float[] tpos = e.Message.GetFloats();
                            seenIds.Add(trackingId);

                            GameObject trailerObj;
                            bool hadTrailer = currentPlayer.Trailers.TryGetValue(trackingId, out trailerObj);

                            if (!hadTrailer || trailerObj == null)
                            {
                                // Spawn new trailer
                                trailerObj = Messages.createTrailerMesh(playerId, containerType);
                                if (trailerObj != null)
                                {
                                    trailerObj.transform.position = new Vector3(tpos[0], tpos[1], tpos[2]) - floatingOrigin.m_currentOrigin;
                                    trailerObj.transform.eulerAngles = new Vector3(tpos[3], tpos[4], tpos[5]);
                                }
                                currentPlayer.Trailers[trackingId] = trailerObj;
                                StarTruckMP.Log.LogInfo($"MultiTrailer: spawned trailer {trackingId} type='{containerType}' for player {playerId}");
                            }

                            // Update position target for smoothing
                            if (trailerObj != null)
                            {
                                // Store target in a per-trailer dict (reuse trailerTrans for first, extra for dict)
                                if (!currentPlayer.trailerExtraTargets.ContainsKey(trackingId))
                                    currentPlayer.trailerExtraTargets[trackingId] = new movementTrans();
                                var et = currentPlayer.trailerExtraTargets[trackingId];
                                et.Pos = new Vector3(tpos[0], tpos[1], tpos[2]);
                                et.Rot = new Vector3(tpos[3], tpos[4], tpos[5]);
                                currentPlayer.trailerExtraTargets[trackingId] = et;
                            }
                        }

                        // Destroy trailers no longer in the list
                        var toRemove = new System.Collections.Generic.List<long>();
                        foreach (var kv in currentPlayer.Trailers)
                        {
                            if (!seenIds.Contains(kv.Key))
                            {
                                if (kv.Value != null) GameObject.Destroy(kv.Value);
                                toRemove.Add(kv.Key);
                            }
                        }
                        foreach (var rid in toRemove)
                        {
                            currentPlayer.Trailers.Remove(rid);
                            currentPlayer.trailerExtraTargets.Remove(rid);
                            StarTruckMP.Log.LogInfo($"MultiTrailer: destroyed trailer {rid} for player {playerId}");
                        }

                        currentPlayer.trailerHitched = trailerCount > 0;
                        // Velocity-Extrapolation: Zeitstempel des letzten Trailer-Targets
                        // (Legacy UND Multi setzen ihn beim Empfang).
                        currentPlayer.lastTrailerTargetTime = UnityEngine.Time.realtimeSinceStartup;
                    }
                    else
                    {
                        // v0 legacy single-trailer format
                        Vector3 trailerPos;
                        trailerPos.x = tt[0]; trailerPos.y = tt[1]; trailerPos.z = tt[2];
                        Vector3 trailerRot;
                        trailerRot.x = tt[3]; trailerRot.y = tt[4]; trailerRot.z = tt[5];

                        currentPlayer.trailerHitched = hitched;
                        currentPlayer.trailerTrans.Pos = trailerPos;
                        currentPlayer.trailerTrans.Rot = trailerRot;

                        if (!string.IsNullOrEmpty(remoteTrailerModel))
                            currentPlayer.trailerModel = remoteTrailerModel;

                        if (hitched && currentPlayer.Trailer == null)
                        {
                            currentPlayer.Trailer = Messages.createTrailerMesh(playerId, currentPlayer.trailerModel);
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
                            currentPlayer.trailerTargetPos = trailerPos;
                            currentPlayer.trailerTargetRot = trailerRot;
                        }
                        // Velocity-Extrapolation: Zeitstempel des letzten Trailer-Targets
                        // (Legacy-Pfad).
                        currentPlayer.lastTrailerTargetTime = UnityEngine.Time.realtimeSinceStartup;
                    }

                    playerList[playerId] = currentPlayer;
                    skipTrailer:;
                }
            }

            if (e.MessageId == (ushort)messageType.setPlayerName)
            {
                ushort namePlayerId = e.Message.GetUShort();
                string newName = e.Message.GetString();
                if (namePlayerId != client.Id)
                {
                    playerInfo currentPlayer;
                    if (!playerList.TryGetValue(namePlayerId, out currentPlayer))
                    {
                        // Name-Race: Spieler noch nicht registriert — Name puffern,
                        // statt ihn zu verwerfen (Fix fuer "Player_1" statt Steam-Name).
                        pendingNames[namePlayerId] = newName;
                        StarTruckMP.Log.LogInfo($"Player {namePlayerId} name '{newName}' buffered (player not yet registered)");
                    }
                    else
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
                pendingNames.Remove(clientId);
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

                    // Job-/Cargo-Sync fuer Spaetankoemmlinge: bisher wurde nur beim MOMENT der
                    // lokalen Generierung gesendet. Wenn ich (allein) schon ein Board fuer diesen
                    // Sektor habe und jetzt jemand NEU in meinen aktuellen Sektor kommt, wuerde
                    // der sonst nie etwas empfangen - meine Generierung liegt ja schon in der
                    // Vergangenheit, es feuert kein neuer Trigger. Deshalb hier: wenn der andere
                    // Spieler jetzt in meinem Sektor ist UND ich (jetzt) die Autoritaet bin,
                    // schicke ich meinen bereits bestehenden Stand erneut (kein Neu-Generieren,
                    // nur Re-Broadcast der aktuellen Live-Daten).
                    if (clientInfo.sector == currentSector && JobBoardSync.IsAuthorityForCurrentSector())
                    {
                        try { CargoSync.OnLocalCargoSpawned(); } catch (Exception ex) { StarTruckMP.Log.LogWarning($"CargoSync Re-Broadcast bei Spielerankunft fehlgeschlagen: {ex.Message}"); }
                        try { JobBoardSync.OnLocalJobsGenerated(); } catch (Exception ex) { StarTruckMP.Log.LogWarning($"JobBoardSync Re-Broadcast bei Spielerankunft fehlgeschlagen: {ex.Message}"); }
                        try { JobBoardIdSync.OnLocalJobsGenerated(); } catch (Exception ex) { StarTruckMP.Log.LogWarning($"JobBoardIdSync Re-Broadcast bei Spielerankunft fehlgeschlagen: {ex.Message}"); }
                    }
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

            if (e.MessageId == (ushort)messageType.jobBoardSync)
            {
                JobBoardSync.HandleIncoming(e);
            }

            if (e.MessageId == (ushort)messageType.cargoSync)
            {
                CargoSync.HandleIncoming(e);
            }

            if (e.MessageId == (ushort)messageType.jobBoardIdents)
            {
                JobBoardIdSync.HandleIncoming(e);
            }
        }

            }
            catch (System.Exception ex)
            {
                // Vollen Stacktrace + MessageType statt nur ex.Message loggen, damit ein
                // kuenftiger Fall sofort auf Handler+Zeile zeigt statt nur "Index was outside
                // the bounds of the array" ohne jeden Kontext.
                string msgTypeName;
                try { msgTypeName = ((messageType)e.MessageId).ToString(); }
                catch { msgTypeName = e.MessageId.ToString(); }
                StarTruckMP.Log.LogWarning($"Client_MessageReceived error (MessageType={msgTypeName}): {ex}");
            }
        }

        public static void CheckHonk()
        {
            if (!client.IsConnected) return;
            // Use GetKey (held) so honk lasts as long as the key is pressed
            isHonking = UnityEngine.Input.GetKey(StarTruckMP.HonkKey);
        }

        /// <summary>
        /// Reads the player's ACTUAL selected route from the galactic map.
        /// GameStatePersistence.destinationEntryGate/destinationSector turned out to only be
        /// populated during an active warp jump, NOT while a route is merely selected on the
        /// map — confirmed empty via diagnostics while a route was clearly set (map showed a
        /// highway-shield route marker). The real source of truth is the persisted
        /// JourneyTracker route: GameStatePersistence.instance.journeyTrackerState.waypoints
        /// is the ordered list of sector ids the player picked on the map; waypoints[0] is the
        /// immediate next sector. We match that against each WarpGate's DestinationSectorId in
        /// the current sector to find which physical gate leads there.
        /// </summary>
        private static float lastDestGateDiagLog = 0f;

        public static void DetectDestinationGates()
        {
            try
            {
                if (!client.IsConnected) return;

                string gateId = "";
                string nextSectorId = "";
                int waypointCount = -1;

                try
                {
                    // NOTE: GameStatePersistence.instance.journeyTrackerState is a SAVE-DATA
                    // snapshot (JourneyTrackerData) - it only reflects what was last written to
                    // the save file (e.g. on autosave/dock), NOT the player's live in-session
                    // route selection. Switching destinations on the galaxy map updates the
                    // LIVE JourneyTracker instance immediately, but that change doesn't reach
                    // journeyTrackerState until the next save - which is why the old gate's
                    // board never cleared after a route change. The live route lives on
                    // PlayerProperties.instance.journeyTracker.CurrentJourney instead.
                    var pp = PlayerProperties.instance;
                    var jt = pp != null ? pp.journeyTracker : null;
                    if (jt != null)
                    {
                        var journey = jt.CurrentJourney;
                        if (journey != null)
                        {
                            int wpCount = journey.Count;
                            waypointCount = wpCount;
                            if (wpCount > 0)
                            {
                                var firstSector = journey[0];
                                nextSectorId = firstSector != null ? firstSector.name : "";
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"DetectDestinationGates: live journeyTracker read failed: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(nextSectorId))
                {
                    try
                    {
                        var allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>();
                        if (allGates != null)
                        {
                            foreach (var z in allGates)
                            {
                                if (z == null || z.gameObject == null) continue;
                                string destSectorName = "";
                                try { destSectorName = z.DestinationSectorId != null ? z.DestinationSectorId.name : ""; } catch { }
                                if (!string.IsNullOrEmpty(destSectorName) && destSectorName == nextSectorId)
                                {
                                    gateId = JumpgateUtils.GetEntryGateIdForZone(z);
                                    break;
                                }
                            }
                        }
                    }
                    catch (System.Exception ex2)
                    {
                        StarTruckMP.Log.LogWarning($"DetectDestinationGates: gate matching failed: {ex2.Message}");
                    }
                }

                if (gateId != currentDestinationGateId)
                {
                    StarTruckMP.Log.LogInfo($"DetectDestinationGates CHANGE: '{currentDestinationGateId}' -> '{gateId}' (nextSectorId='{nextSectorId}', waypointCount={waypointCount})");
                    JumpgateOption1.ForceRefresh();
                }
                currentDestinationGateId = gateId;
            }
            catch { }
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
                    if (!sentFirstUpdate || forceStateResend || sendHonk || (floatingOrigin.m_currentOrigin + myTruck.transform.position) != truckTrans.Pos || myTruck.transform.eulerAngles != truckTrans.Rot || myTruckRigid.velocity != truckTrans.Vel || myTruckRigid.angularVelocity != truckTrans.AngVel)
                    {
                        client.Send(Messages.createMovementMessage(client.Id, floatingOrigin.m_currentOrigin + myTruck.transform.position, myTruck.transform.eulerAngles, myTruckRigid.velocity, myTruckRigid.angularVelocity, true, false, sendHonk, currentDestinationGateId));
                        truckTrans.Pos = floatingOrigin.m_currentOrigin + myTruck.transform.position;
                        truckTrans.Rot = myTruck.transform.eulerAngles;
                        truckTrans.Vel = myTruckRigid.velocity;
                        truckTrans.AngVel = myTruckRigid.angularVelocity;
                    }
                }
                if (myPlayer != null && playerLocation != null && myPlayerRigid != null)
                {
                    if (!sentFirstUpdate || forceStateResend || PlayerLocation.worldPosition != playerTrans.Pos || playerCam.transform.eulerAngles != playerTrans.Rot || myPlayerRigid.velocity != playerTrans.Vel || myPlayerRigid.angularVelocity != playerTrans.AngVel)
                    {
                        client.Send(Messages.createMovementMessage(client.Id, PlayerLocation.worldPosition + new Vector3(0, -1, 0), playerCam.transform.eulerAngles, myPlayerRigid.velocity, myPlayerRigid.angularVelocity, false, false, false, currentDestinationGateId));
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

                if (forceStateResend)
                {
                    forceStateResend = false;
                    StarTruckMP.Log.LogInfo($"SendMovement: forced state re-send after player join (truckPos=({truckTrans.Pos.x:F2}, {truckTrans.Pos.y:F2}, {truckTrans.Pos.z:F2}), destGate='{currentDestinationGateId}')");
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

            try
            {
                // Find ALL CargoContainers near our truck (not just the closest)
                var allCargo = GameObject.FindObjectsOfType<CargoContainer>();
                var nearbyTrailers = new System.Collections.Generic.List<Messages.TrailerData>();

                foreach (var cargo in allCargo)
                {
                    if (cargo == null) continue;
                    float dist = Vector3.Distance(myTruck.transform.position, cargo.transform.position);
                    if (dist < HitchDistanceThreshold)
                    {
                        string typeId = Messages.GetContainerTypeIdentifier(cargo);
                        if (string.IsNullOrEmpty(typeId))
                        {
                            typeId = cargo.gameObject?.name?.Replace("(Clone)", "").Trim() ?? "";
                        }
                        Vector3 pos = (cargo.rb != null ? cargo.rb.position : cargo.transform.position)
                                      + floatingOrigin.m_currentOrigin;
                        Vector3 rot = cargo.transform.eulerAngles;
                        long trackingId = 0;
                        try { trackingId = cargo.record.trackingId; } catch { }

                        nearbyTrailers.Add(new Messages.TrailerData
                        {
                            trackingId = trackingId,
                            containerType = typeId,
                            px = pos.x, py = pos.y, pz = pos.z,
                            rx = rot.x, ry = rot.y, rz = rot.z
                        });
                    }
                }

                bool hitched = nearbyTrailers.Count > 0;

                if (hitched && nearbyTrailers.Count > 0)
                {
                    string modelStr = nearbyTrailers[0].containerType;
                    if (modelStr != lastTrailerModel)
                    {
                        StarTruckMP.Log.LogInfo($"SendTrailerMovement: {nearbyTrailers.Count} trailer(s) nearby, model='{modelStr}'");
                        lastTrailerModel = modelStr;
                    }

                    if (nearbyTrailers.Count == 1)
                    {
                        // Single trailer — use legacy format for backward compat
                        var t = nearbyTrailers[0];
                        client.Send(Messages.createTrailerMovementMessage(client.Id, true,
                            new Vector3(t.px, t.py, t.pz), new Vector3(t.rx, t.ry, t.rz), t.containerType));
                    }
                    else
                    {
                        // Multi-trailer — use v1 array format
                        client.Send(Messages.createMultiTrailerMovementMessage(client.Id, nearbyTrailers.ToArray()));
                    }
                    trailerHitchedLastSent = true;
                }
                else if (trailerHitchedLastSent)
                {
                    client.Send(Messages.createTrailerMovementMessage(client.Id));
                    lastTrailerModel = "";
                    trailerHitchedLastSent = false;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"SendTrailerMovement error: {ex.Message}");
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
                var sectorScene = GameObject.Find("[Sector]");
                currentSector = (sectorScene != null) ? sectorScene.scene.name : "none";
                // If the sector scene isn't resolvable yet (early connect), don't send a
                // bogus 'none' — schedule a retry so other clients don't permanently
                // see us in sector 'none' and RemoveFromSector despawns us on arrival.
                if (currentSector == "none")
                {
                    StarTruckMP.Log.LogWarning("OnArrivedAtSector: [Sector] not found yet - retrying initial updateSector send");
                    RetrySendSectorAfterConnect();
                    return;
                }
                currentDestinationGateId = "";  // reset on sector change
                JumpgateOption1.ForceRefresh();
                // Build-329: Sicherheitsnetz - Roundtrip-Trigger 5s nach Sektor-Eintritt
                // erneut probieren (FixedUpdate-Hook allein feuerte in 328 nicht).
                roundtripFallbackAt = Time.realtimeSinceStartup + 5f;
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
                // DEFER: playerConnected sends no position data, so truckTrans.Pos
                // defaults to zero. Don't spawn at origin - the first movementUpdate
                // will create the truck at the correct position instead.
                // Build 319 (Michael): Guard verschaerft — auch NaN abfangen. Kein Spawn
                // mit unvollstaendigen Join-Daten; die erste echte movementUpdate-Position
                // spawnt den Neuling exakt.
                if (clientInfo.truckTrans.Pos.sqrMagnitude < 1f ||
                    float.IsNaN(clientInfo.truckTrans.Pos.x) || float.IsNaN(clientInfo.truckTrans.Pos.y) || float.IsNaN(clientInfo.truckTrans.Pos.z))
                {
                    StarTruckMP.Log.LogInfo($"RemoveFromSector: deferring spawn for player {clientId} - position not yet received (pos={clientInfo.truckTrans.Pos})");
                    return;
                }
                StarTruckMP.Log.LogInfo($"Spawning player {clientId} in sector '{currentSector}' at pos {playerList[clientId].truckTrans.Pos}");
                playerInfo player = Messages.createPlayer(clientId, playerList[clientId].truckTrans.Pos, playerList[clientId].truckTrans.Rot, currentSector, playerList[clientId].Name);
                clientInfo.Truck = player.Truck;
                clientInfo.Player = player.Player;
                clientInfo.NameLabel = player.NameLabel;
                clientInfo.truckTargetPos = player.truckTargetPos;
                clientInfo.truckTargetRot = player.truckTargetRot;
                clientInfo.spawnTime = UnityEngine.Time.time;
                clientInfo.isColliding = false;
                // Hide spacesuit by default — only show when player is outside truck (EVA)
                if (clientInfo.Player != null)
                {
                    var suitRenderer = clientInfo.Player.GetComponentInChildren<MeshRenderer>();
                    if (suitRenderer != null) suitRenderer.enabled = false;
                }
                // Attach collision helper for 120s grace period + collision detection
                if (clientInfo.Truck != null)
                {
                    var helper = clientInfo.Truck.AddComponent<RemoteTruckCollisionHelper>();
                    if (helper != null) helper.Init(clientId);
                }
                SnapRemotePlayerToLocal(clientId, clientInfo);
                StarTruckMP.Log.LogInfo($"Spawn result for player {clientId}: truck={(clientInfo.Truck != null ? "OK" : "NULL")}, player={(clientInfo.Player != null ? "OK" : "NULL")}");
            }
        }

        private static Vector3 lastAnchoredOrigin = Vector3.zero;

        // 318 (Bug B, Michael-Zusatz): nach JEDEM createPlayer/Respawn (deferred-spawn-Pfad
        // UND RemoveFromSector-Rueckpfad) das Truck-GameObject SOFORT hart auf die korrekte
        // lokale Szene-Position setzen: truckTrans.Pos (ABS) minus floatingOrigin. Sonst
        // bleibt ein im selben Origin respawntes Truck an einer falschen Stelle haengen:
        // ReanchorRemotePlayersToFloatingOrigin greift nicht (lastAnchoredOrigin ist schon
        // gleich m_currentOrigin — Reanchor feuert nur bei Origin-AENDERUNG), und der
        // Spawn-Pfad positioniert evtl. abs statt local.truckLocalScenePos muss immer
        // lastKnownAbsPos - origin sein.
        private static void SnapRemotePlayerToLocal(ushort id, playerInfo p)
        {
            if (floatingOrigin == null) return;
            // Build 319 (Michael: Join-Exaktheit): KEIN Snap mit (0,0,0) oder NaN —
            // der 318er-Test zeigte 'SnapRemotePlayerToLocal[2]: abs=(0,0,0)' (Join-Race:
            // playerConnected traegt keine Position). Ohne echte Position ist Snappen
            // falsch; der Spawn/Snap passiert stattdessen mit der ersten echten
            // movementUpdate-Position.
            if (p.truckTrans.Pos == Vector3.zero ||
                float.IsNaN(p.truckTrans.Pos.x) || float.IsNaN(p.truckTrans.Pos.y) || float.IsNaN(p.truckTrans.Pos.z))
            {
                StarTruckMP.Log.LogWarning($"SnapRemotePlayerToLocal[{id}]: REFUSED — position is zero/NaN (no real movementUpdate received yet)");
                return;
            }
            if (p.Truck != null)
            {
                Vector3 localPos = p.truckTrans.Pos - floatingOrigin.m_currentOrigin;
                p.Truck.transform.position = localPos;
                p.Truck.transform.eulerAngles = p.truckTrans.Rot;
                var rb = p.Truck.GetComponent<Rigidbody>();
                if (rb != null) { rb.position = localPos; rb.rotation = Quaternion.Euler(p.truckTrans.Rot); }
            }
            if (p.Player != null)
            {
                p.Player.transform.position = p.playerTrans.Pos - floatingOrigin.m_currentOrigin;
            }
            // Interpolations-Ziel auf denselben (abs-)Frame setzen, damit SmoothTruckMovement
            // nicht von einer alten Local-Position los-lerpt.
            p.truckTargetPos = p.truckTrans.Pos;
            p.truckTargetRot = p.truckTrans.Rot;
            playerList[id] = p;
            StarTruckMP.Log.LogInfo($"SnapRemotePlayerToLocal[{id}]: abs={p.truckTrans.Pos}, origin={floatingOrigin.m_currentOrigin}, local={p.truckTrans.Pos - floatingOrigin.m_currentOrigin}");
        }


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
    // Velocity-Extrapolation: max Vorschau in Sekunden — bei Paket-Stau nicht endlos extrapoliert.
    private static readonly float TrailerVelocityPreview = 0.25f;

        public static void SmoothTrailerMovement()
        {
            foreach (var kv in playerList)
            {
                playerInfo rp = kv.Value;

                // ---- Legacy single-trailer path (rp.Trailer) ----
                if (rp.Trailer != null && rp.trailerHitched)
                {
                    rp = SmoothSingleTrailer(kv.Key, rp, rp.Trailer, rp.trailerTargetPos, rp.trailerTargetRot,
                        ref rp.trailerDriftTimer, ref rp.trailerSmoothVel, -1);
                }

                // ---- Multi-container path: EVERY extra trailer against its own target ----
                if (rp.Trailers != null && rp.trailerExtraTargets != null)
                {
                    // Snapshot keys — the loop writes nothing to the dict itself.
                    var ids = new List<long>(rp.Trailers.Keys);
                    foreach (long trackingId in ids)
                    {
                        GameObject trailerObj = rp.Trailers[trackingId];
                        if (trailerObj == null) continue;
                        if (!rp.trailerExtraTargets.TryGetValue(trackingId, out movementTrans et)) continue;

                        if (!rp.trailerExtraDriftTimers.ContainsKey(trackingId))
                            rp.trailerExtraDriftTimers[trackingId] = 0f;
                        if (!rp.trailerExtraSmoothVels.ContainsKey(trackingId))
                            rp.trailerExtraSmoothVels[trackingId] = Vector3.zero;

                        float driftT = rp.trailerExtraDriftTimers[trackingId];
                        Vector3 smoothV = rp.trailerExtraSmoothVels[trackingId];
                        rp = SmoothSingleTrailer(kv.Key, rp, trailerObj, et.Pos, et.Rot, ref driftT, ref smoothV,
                            trackingId);
                        rp.trailerExtraDriftTimers[trackingId] = driftT;
                        rp.trailerExtraSmoothVels[trackingId] = smoothV;
                    }
                }

                // Struktur-Copy-Semantik: playerInfo ist ein Struct — ohne diesen Write
                // verfallen alle Feld-Änderungen (Timer, SmoothVel, ...).
                playerList[kv.Key] = rp;
            }
        }

        // Shared per-trailer smoothing: snap threshold, sustained-drift guard, SmoothDamp,
        // Slerp — plus velocity extrapolation of the target between 100ms updates.
        private static playerInfo SmoothSingleTrailer(ushort playerKey, playerInfo rp, GameObject trailerObj,
            Vector3 targetAbsPos, Vector3 targetRot, ref float driftTimer, ref Vector3 smoothVel,
            long trackingId)
        {
            string idTag = (trackingId >= 0) ? $" trackingId={trackingId}," : "";

            Vector3 targetLocal = targetAbsPos - floatingOrigin.m_currentOrigin;

            // Velocity extrapolation: between the ~100ms network updates the trailer target
            // moves on with the remote truck's velocity (capped at 0.25s preview so packet
            // stall can't extrapolate endlessly).
            float dt = Time.realtimeSinceStartup - rp.lastTrailerTargetTime;
            if (dt > 0f)
            {
                float clampedDt = Mathf.Min(dt, TrailerVelocityPreview);
                targetLocal += rp.truckTrans.Vel * clampedDt;
            }

            float trailerErrDist = (targetLocal - trailerObj.transform.position).magnitude;
            if (trailerErrDist > TruckSnapThreshold)
            {
                // Build 319: Riesen-Delta (z.B. Gate-Jump des Remote-Spielers) — hart snappen.
                trailerObj.transform.position = targetLocal;
                trailerObj.transform.rotation = Quaternion.Euler(targetRot);
                smoothVel = Vector3.zero;
                driftTimer = 0f;
                StarTruckMP.Log.LogInfo($"SmoothTrailerMovement[player={playerKey},{idTag}]: hard snap, errorDist={trailerErrDist:F0}m > {TruckSnapThreshold}m, targetLocal={targetLocal}");
                return rp;
            }
            // Build 320: Sustained drift (per trailer, getrennt vom Truck-Timer) — kein bleibender Offset.
            if (trailerErrDist > SustainedDriftError)
                driftTimer += Time.deltaTime;
            else
                driftTimer = 0f;
            if (driftTimer > SustainedDriftTime)
            {
                trailerObj.transform.position = targetLocal;
                trailerObj.transform.rotation = Quaternion.Euler(targetRot);
                smoothVel = Vector3.zero;
                driftTimer = 0f;
                StarTruckMP.Log.LogInfo($"SmoothTrailerMovement[player={playerKey},{idTag}]: sustained-drift snap, errorDist={trailerErrDist:F1}m persistiert > {SustainedDriftTime}s, targetLocal={targetLocal}");
                return rp;
            }
            trailerObj.transform.position = Vector3.SmoothDamp(
                trailerObj.transform.position,
                targetLocal,
                ref smoothVel,
                TrailerSmoothTime
            );

            Quaternion targetQuat = Quaternion.Euler(targetRot);
            trailerObj.transform.rotation = Quaternion.Slerp(
                trailerObj.transform.rotation,
                targetQuat,
                Time.deltaTime * TrailerRotationSpeed
            );
            return rp;
        }

        // Smooth velocity-correction for remote trucks.
        // Instead of hard-snapping transform.position (which fights rb.velocity extrapolation),
        // we apply a gentle velocity correction to close the gap between extrapolated and
        // target position. The spring-like correction works WITH the physics, not against it.
        private static readonly float TruckCorrectionK = 5.0f;      // spring constant for position
        private static readonly float TruckRotCorrectionK = 8.0f;   // spring constant for rotation
        private static readonly float TruckMaxCorrection = 10f;      // max correction distance (meters)
        // Build 319: Gate-Sprung des Remote-Spielers springt truckTargetPos um Kilometer.
        // Der velocity-cap (TruckMaxCorrection) liess den Truck minutenlang crawlen —
        // ab diesem Fehler-Abstand hart snappen statt smooth korrigieren.
        private static readonly float TruckSnapThreshold = 250f;     // meters; above = hard snap
        // Build 320: Steady-State-Exaktheit. Fehler > 50m, der > 1.5s persistiert, ist kein
        // Netzwerk-Jitter mehr (der loest sich in ~0.1-0.2s auf) — dann snappen wir hart, damit
        // keine bleibende Offset-Abweichung entstehen kann.
        private static readonly float SustainedDriftError = 50f;     // meters
        private static readonly float SustainedDriftTime = 1.5f;     // seconds
        private static readonly float MaxVelocity = 40f;              // hard clamp: max linear velocity (m/s)
        private static readonly float MaxAngularVelocity = 10f;       // hard clamp: max angular velocity (rad/s)
        private static readonly float ReadyCorrectionK = 2.5f;        // moderate K after grace period (before first contact)

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
                if (errorDist > TruckSnapThreshold)
                {
                    // Build 319: Riesen-Delta (z.B. Gate-Jump des Remote-Spielers / Origin-Reanchor)
                    // — hart snappen wie SnapRemotePlayerToLocal, sonst crawlt der Truck minutenlang.
                    rp.Truck.transform.position = targetPos;
                    rp.Truck.transform.eulerAngles = rp.truckTargetRot;
                    rb.position = targetPos;
                    rb.rotation = Quaternion.Euler(rp.truckTargetRot);
                    rb.velocity = rp.truckTrans.Vel;
                    rb.angularVelocity = rp.truckTrans.AngVel;
                    playerList[kv.Key] = rp;
                    StarTruckMP.Log.LogInfo($"SmoothTruckMovement[{kv.Key}]: hard snap, errorDist={errorDist:F0}m > {TruckSnapThreshold}m, targetLocal={targetPos}");
                    rp.driftTimer = 0f;
                    continue;
                }

                // Build 320 (Michael: "das muss immer exakt richtig sein die position"):
                // Der velocity-add-Pfad garantiert keine Konvergenz — der Truck kann dauerhaft
                // mit Restfehler daneben hängen (siehe 318er-Log: kleine bis wachsende Offsets).
                // Guard: bleibt der Fehler > SustainedDriftError über SustainedDriftTime, liegt
                // kein transienter Netzwerk-Jitter vor → Snap setzt die Position exakt auf
                // abs - origin. Nach dem Snap ist der Fehler definitionsgemäß 0 und wird im
                // nächsten Frame direkt wieder mit der frischen Netzwerk-Position neu angesetzt.
                if (errorDist > SustainedDriftError)
                    rp.driftTimer += Time.deltaTime;
                else
                    rp.driftTimer = 0f;
                if (rp.driftTimer > SustainedDriftTime)
                {
                    rp.Truck.transform.position = targetPos;
                    rp.Truck.transform.eulerAngles = rp.truckTargetRot;
                    rb.position = targetPos;
                    rb.rotation = Quaternion.Euler(rp.truckTargetRot);
                    rb.velocity = rp.truckTrans.Vel;
                    rb.angularVelocity = rp.truckTrans.AngVel;
                    rp.driftTimer = 0f;
                    playerList[kv.Key] = rp;
                    StarTruckMP.Log.LogInfo($"SmoothTruckMovement[{kv.Key}]: sustained-drift snap, errorDist={errorDist:F1}m persistiert > {SustainedDriftTime}s, targetLocal={targetPos}");
                    continue;
                }
                if (errorDist > TruckMaxCorrection)
                    error = error.normalized * TruckMaxCorrection;

                // Apply correction as velocity addition — works WITH physics, not against it
                // Three tiers: full K normally, moderate K after grace period (before first contact),
                // minimal K during active collision — prevents crash on first-contact frame.
                float effectiveCorrectionK;
                if (rp.isColliding)
                    effectiveCorrectionK = TruckCorrectionK * 0.1f;
                else if (rp.collisionReady)
                    effectiveCorrectionK = ReadyCorrectionK;
                else
                    effectiveCorrectionK = TruckCorrectionK;
                Vector3 newVelocity = rp.truckTrans.Vel + error * effectiveCorrectionK;
                // NaN/Infinity guard — prevent PhysX native crash
                if (float.IsNaN(newVelocity.x) || float.IsNaN(newVelocity.y) || float.IsNaN(newVelocity.z) ||
                    float.IsInfinity(newVelocity.x) || float.IsInfinity(newVelocity.y) || float.IsInfinity(newVelocity.z))
                {
                    global::StarTruckMP.StarTruckMP.Log.LogWarning($"SmoothTruckMovement[{kv.Key}]: NaN/Infinity velocity detected, skipping set");
                }
                else
                {
                    // Hard clamp to absolute maximum
                    float spd = newVelocity.magnitude;
                    if (spd > MaxVelocity)
                        newVelocity = newVelocity.normalized * MaxVelocity;
                    rb.velocity = newVelocity;
                }

                // Rotation: angular velocity correction via numerically stable small-angle
                // approximation (avoids Quaternion.ToAngleAxis, which divides by sin(angle/2)
                // internally and produces NaN when the rotation error is small - which is the
                // NORMAL case once a truck has converged close to its target rotation).
                Quaternion targetQuat = Quaternion.Euler(rp.truckTargetRot);
                Quaternion rotError = targetQuat * Quaternion.Inverse(rb.rotation);
                // Ensure shortest-path rotation (quaternion double-cover: q and -q represent
                // the same rotation, but we want the one with smallest angle).
                if (rotError.w < 0f)
                {
                    rotError.x = -rotError.x;
                    rotError.y = -rotError.y;
                    rotError.z = -rotError.z;
                    rotError.w = -rotError.w;
                }
                // For a unit quaternion (x,y,z,w) representing a small-to-moderate rotation,
                // axis*angle (in radians) is well approximated by 2*(x,y,z) - this is exact in
                // the limit of small angles and reasonably accurate up to large angles too,
                // with NO division and therefore NO possibility of NaN from this step.
                Vector3 axisAngle = new Vector3(rotError.x, rotError.y, rotError.z) * 2f;

                if (axisAngle.sqrMagnitude > 0.0001f) // ~ (0.01 rad)^2, mirrors old 0.01f angle threshold
                {
                    float effectiveRotK = rp.isColliding ? TruckRotCorrectionK * 0.1f : TruckRotCorrectionK;
                    Vector3 newAngVel = rp.truckTrans.AngVel + axisAngle * effectiveRotK;
                    if (float.IsNaN(newAngVel.x) || float.IsNaN(newAngVel.y) || float.IsNaN(newAngVel.z) ||
                        float.IsInfinity(newAngVel.x) || float.IsInfinity(newAngVel.y) || float.IsInfinity(newAngVel.z))
                    {
                        global::StarTruckMP.StarTruckMP.Log.LogWarning($"SmoothTruckMovement[{kv.Key}]: NaN/Infinity angularVelocity detected, skipping set");
                    }
                    else
                    {
                        float angSpd = newAngVel.magnitude;
                        if (angSpd > MaxAngularVelocity)
                            newAngVel = newAngVel.normalized * MaxAngularVelocity;
                        rb.angularVelocity = newAngVel;
                    }
                }
                else
                {
                    Vector3 newAngVel = rp.truckTrans.AngVel;
                    if (float.IsNaN(newAngVel.x) || float.IsNaN(newAngVel.y) || float.IsNaN(newAngVel.z) ||
                        float.IsInfinity(newAngVel.x) || float.IsInfinity(newAngVel.y) || float.IsInfinity(newAngVel.z))
                    {
                        global::StarTruckMP.StarTruckMP.Log.LogWarning($"SmoothTruckMovement[{kv.Key}]: NaN/Infinity angularVelocity (else branch) detected, skipping set");
                    }
                    else
                    {
                        rb.angularVelocity = newAngVel;
                    }
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
        public static string myPlayerName = "";
        private static string myLinkCode = "";

        /// <summary>
        /// Returns "Slipstream" as a rainbow-colored rich-text string.
        /// Each letter gets its own color for a vibrant space/neon look.
        /// Palette: hot pink → coral → orange → yellow → lime → cyan → sky → blue → indigo → violet.
        /// </summary>
        private static string BuildColorfulSlipstreamText()
        {
            // "Slip" in blue/steel gradient, "Stream" in gold/orange gradient,
            // matching the Slipstream logo reference image.
            string[] slipColors = { "#B8D4E8", "#8FB8DC", "#5A8FC0", "#3A6EA5" };   // S-l-i-p
            string[] streamColors = { "#FFD700", "#FFC42E", "#F2A93C", "#E8933A", "#DC7E30", "#D97B29" }; // S-t-r-e-a-m
            string slip = "Slip";
            string stream = "Stream";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < slip.Length; i++)
                sb.Append($"<color={slipColors[i]}>{slip[i]}</color>");
            for (int i = 0; i < stream.Length; i++)
                sb.Append($"<color={streamColors[i]}>{stream[i]}</color>");
            return sb.ToString();
        }

        /// <summary>
        /// Zeigt dem Spieler eine sichtbare Meldung, dass der Server voll ist.
        /// Nutzt den selben Status-Overlay-Mechanismus wie die Spielstand-Anzeige.
        /// </summary>
        private static void ShowServerFullOverlay()
        {
            if (statusOverlay == null || statusOverlayText == null)
            {
                // Overlay noch nicht initialisiert — wird beim naechsten UpdateStatusOverlay()-Aufruf
                // erzeugt. ServerFull-Text wird dort geprueft.
                return;
            }
            statusOverlayText.text = $"<color=#E53935>Server ist voll</color>\n<color=#E53935>{serverFullMessage}</color>";
            statusOverlayText.fontSize = 22;
            if (!statusOverlay.activeSelf)
            {
                statusOverlay.SetActive(true);
            }
        }

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
                // Server voll: Meldung im Overlay anzeigen, normalen Status-Text ueberschreiben
                if (serverFullRejected)
                {
                    if (statusOverlayText != null)
                    {
                        statusOverlayText.text = $"<color=#E53935>Server ist voll</color>\n<color=#E53935>{serverFullMessage}</color>";
                        statusOverlayText.fontSize = 22;
                        if (statusOverlay != null && !statusOverlay.activeSelf)
                        {
                            statusOverlay.SetActive(true);
                        }
                    }
                    return;
                }

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
                    text += "\n" + BuildColorfulSlipstreamText() + " " + StarTruckMP.customBuildNumber;
                }

                if (statusOverlayText != null)
                {
                    statusOverlayText.text = text;
                    if (statusOverlay != null && !statusOverlay.activeSelf)
                    {
                        statusOverlay.SetActive(true);
                    }
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
                    statusOverlayText.richText = true;
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
        }
    }
}
