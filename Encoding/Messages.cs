using Riptide;
using StarTruckMP.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StarTruckMP.Encoding
{
    internal class Messages
    {
        public static playerInfo createPlayer(ushort playerId, Vector3 position, Vector3 rotation, string sector)
        {
            try
            {
                GameObject sectorGO = GameObject.Find("[Sector]");
                var myRigid = StarTruckClient.StarTruckClient.myTruck.GetComponent<Rigidbody>();
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 1: sectorGO={sectorGO != null}, myRigid={myRigid != null}");

                //Spawn new Truck GameObject
                GameObject newTruck = new GameObject("RemoteTruck" + playerId);
                SceneManager.MoveGameObjectToScene(newTruck, sectorGO.scene);
                newTruck.transform.SetParent(null);
                var newRigid = newTruck.AddComponent<Rigidbody>();
                newRigid.useGravity = myRigid.useGravity;
                newRigid.drag = myRigid.drag;
                newRigid.angularDrag = myRigid.angularDrag;
                newRigid.mass = myRigid.mass;
                newRigid.centerOfMass = myRigid.centerOfMass;
                newRigid.detectCollisions = false;
                newRigid.isKinematic = myRigid.isKinematic;
                newRigid.maxAngularVelocity = myRigid.maxAngularVelocity;
                newRigid.maxDepenetrationVelocity = myRigid.maxDepenetrationVelocity;
                newRigid.inertiaTensor = myRigid.inertiaTensor;
                newRigid.inertiaTensorRotation = myRigid.inertiaTensorRotation;
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 2: truck rigidbody copied");

                GameObject exteriorObj = GameObject.Find("Exterior");
                if (exteriorObj == null)
                {
                    StarTruckMP.Log.LogError($"createPlayer[{playerId}]: 'Exterior' GameObject not found - cannot build a visible truck exterior for this player.");
                }
                else
                {
                    GameObject newExterior = GameObject.Instantiate(exteriorObj, Vector3.zero, Quaternion.Euler(Vector3.zero), newTruck.transform);
                    newExterior.name = "ClientExterior" + playerId;
                    StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 3: exterior instantiated");

                    TryDisable(newExterior.transform, "StarTruck_Hatch/Marker", playerId);
                    TryDestroyComponent<DoorAnimator>(newExterior.transform, "StarTruck_Hatch", playerId);
                    TryDestroyComponent<GameEventListener>(newExterior.transform, "StarTruck_Hatch", playerId);
                    TryDestroyComponent<EPOOutline.TargetStateListener>(newExterior.transform, "StarTruck_Hatch", playerId);
                    TryDisable(newExterior.transform, "MonitorCameras", playerId);
                    TryDisable(newExterior.transform, "PlayerSpawnMarker", playerId);
                    TryDisable(newExterior.transform, "ThrusterCameraShakeController", playerId);

                    var customization = newExterior.transform.GetComponent<CustomizationApplier>();
                    var livDamApp = newExterior.transform.GetComponent<LiveryAndDamageApplierTruckExterior>();
                    if (customization != null && livDamApp != null)
                    {
                        customization.m_linkedLiveryApplier = livDamApp;
                    }
                    else
                    {
                        StarTruckMP.Log.LogWarning($"createPlayer[{playerId}]: CustomizationApplier not found on exterior, skipping livery link.");
                    }

                    //Disable Truck Collision
                    foreach (var item in newExterior.GetComponentsInChildren<Collider>())
                    {
                        item.enabled = false;
                    }
                    StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 4: exterior cosmetics done");
                }

                //Spawn new Player GameObject
                GameObject newPlayer = new GameObject("RemotePlayer" + playerId);
                SceneManager.MoveGameObjectToScene(newPlayer, sectorGO.scene);
                newPlayer.transform.SetParent(null);
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 5: player object spawned");

                var localSpaceSuitObj = StarTruckClient.StarTruckClient.spaceSuitObj;
                if (localSpaceSuitObj == null)
                {
                    StarTruckMP.Log.LogError($"createPlayer[{playerId}]: local spaceSuitObj is null (ConnectToServer failed to resolve it earlier) - cannot spawn a visible model for player {playerId}, only an empty placeholder will exist.");
                    playerInfo emptyPlayer = new playerInfo();
                    emptyPlayer.Player = newPlayer;
                    emptyPlayer.Truck = newTruck;
                    emptyPlayer.sector = sector;
                    emptyPlayer.truckTrans.Pos = position;
                    emptyPlayer.truckTrans.Rot = rotation;
                    emptyPlayer.playerTrans.Pos = position;
                    emptyPlayer.playerTrans.Rot = rotation;
                    return emptyPlayer;
                }

                GameObject newSuit = GameObject.Instantiate(localSpaceSuitObj, Vector3.zero, Quaternion.Euler(Vector3.zero), newPlayer.transform);
                var newSuitRenderer = newSuit.GetComponent<MeshRenderer>();
                if (newSuitRenderer != null && StarTruckClient.StarTruckClient.spaceSuitMats != null)
                {
                    newSuitRenderer.materials = StarTruckClient.StarTruckClient.spaceSuitMats;
                }
                newSuit.active = true;
                newSuit.name = "ClientSuit" + playerId;
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 6: suit instantiated");

                TryDestroyComponent<SpaceSuitController>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<UnityEngine.CapsuleCollider>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<OutlinableSetterUpper>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<EPOOutline.Outlinable>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<EPOOutline.TargetStateListener>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<MaterialSwitcher>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<InteractTarget>(newSuit.transform, "(suit root)", playerId);
                TryDestroyComponent<DoorController>(newSuit.transform, "(suit root)", playerId);
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 7: suit components stripped");

                myRigid = StarTruckClient.StarTruckClient.myPlayer.GetComponent<Rigidbody>();
                var newPlayerRigid = newPlayer.AddComponent<Rigidbody>();
                newPlayerRigid.useGravity = myRigid.useGravity;
                newPlayerRigid.drag = myRigid.drag;
                newPlayerRigid.angularDrag = myRigid.angularDrag;
                newPlayerRigid.mass = myRigid.mass;
                newPlayerRigid.centerOfMass = myRigid.centerOfMass;
                newPlayerRigid.detectCollisions = false;
                newPlayerRigid.isKinematic = myRigid.isKinematic;
                newPlayerRigid.maxAngularVelocity = myRigid.maxAngularVelocity;
                newPlayerRigid.maxDepenetrationVelocity = myRigid.maxDepenetrationVelocity;
                newPlayerRigid.inertiaTensor = myRigid.inertiaTensor;
                newPlayerRigid.inertiaTensorRotation = myRigid.inertiaTensorRotation;
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 8: player rigidbody copied, spawn complete");

                newTruck.transform.position = position - StarTruckClient.StarTruckClient.floatingOrigin.m_currentOrigin;
                newTruck.transform.eulerAngles = rotation;
                newPlayer.transform.position = position - StarTruckClient.StarTruckClient.floatingOrigin.m_currentOrigin;
                newPlayer.transform.eulerAngles = rotation;
                StarTruckMP.Log.LogInfo($"createPlayer[{playerId}] checkpoint 9: initial transform set to ({position.x:F2}, {position.y:F2}, {position.z:F2}) (world pos was ({newTruck.transform.position.x:F2}, {newTruck.transform.position.y:F2}, {newTruck.transform.position.z:F2}))");

                playerInfo currentPlayer = new playerInfo();
                currentPlayer.Player = newPlayer;
                currentPlayer.Truck = newTruck;
                currentPlayer.sector = sector;
                currentPlayer.truckTrans.Pos = position;
                currentPlayer.truckTrans.Rot = rotation;
                currentPlayer.playerTrans.Pos = position;
                currentPlayer.playerTrans.Rot = rotation;

                return currentPlayer;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogError($"createPlayer failed for player {playerId} in sector '{sector}': {ex}");
                playerInfo fallback = new playerInfo();
                fallback.sector = sector;
                return fallback;
            }
        }

        public static GameObject createTrailerPlaceholder(ushort playerId)
        {
            GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            placeholder.name = "RemoteTrailerPlaceholder" + playerId;
            placeholder.transform.localScale = new Vector3(2.2f, 2.4f, 5.5f);
            var collider = placeholder.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; }
            var renderer = placeholder.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0.15f, 0.35f, 0.75f);
                renderer.material = mat;
            }
            var sectorGO = GameObject.Find("[Sector]");
            if (sectorGO != null)
            {
                SceneManager.MoveGameObjectToScene(placeholder, sectorGO.scene);
            }
            return placeholder;
        }

        public static Message createMovementMessage(ushort playerId, Vector3 position, Vector3 rotation, Vector3 velocity, Vector3 angVel, bool isTruck, bool inSeat)
        {
            float[] playerTransform = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, velocity.x, velocity.y, velocity.z, angVel.x, angVel.y, angVel.z};

            Message message = Message.Create(MessageSendMode.Unreliable, (ushort)messageType.movementUpdate);
            message.AddUShort(playerId);
            message.AddFloats(playerTransform);
            message.AddBool(isTruck);
            message.AddBool(inSeat);

            return message;
        }

        public static Message createTrailerMovementMessage(ushort playerId, bool hitched, Vector3 position, Vector3 rotation)
        {
            float[] trailerTransform = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z };

            Message message = Message.Create(MessageSendMode.Unreliable, (ushort)messageType.trailerMovementUpdate);
            message.AddUShort(playerId);
            message.AddBool(hitched);
            message.AddFloats(trailerTransform);

            return message;
        }

        public static void updateMovement(GameObject playerObject, Vector3 position, Vector3 rotation, Vector3 velocity, Vector3 angVel)
        {
            if (playerObject != null)
            {
                playerObject.transform.position = position - StarTruckClient.StarTruckClient.floatingOrigin.m_currentOrigin;
                playerObject.transform.eulerAngles = rotation;
                var rb = playerObject.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = velocity;
                    rb.angularVelocity = angVel;
                }
            }
        }

        public static Message updateLivery(ushort playerId, string itemId)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, (ushort)messageType.updateLivery);
            message.AddUShort(playerId);
            message.AddString(itemId);

            return message;
        }

        public static Message updateSector(ushort playerId, string sector)
        {
            Message message = Message.Create(MessageSendMode.Reliable, (ushort)messageType.updateSector);
            message.AddUShort(playerId);
            message.AddString(sector);

            return message;
        }

        private static Transform FindPath(Transform root, string path)
        {
            var current = root;
            foreach (var part in path.Split('/'))
            {
                if (current == null) return null;
                current = current.Find(part);
            }
            return current;
        }

        private static void TryDisable(Transform root, string path, ushort playerId)
        {
            var t = FindPath(root, path);
            if (t == null)
            {
                StarTruckMP.Log.LogWarning($"createPlayer[{playerId}]: '{path}' not found under exterior, skipping.");
                return;
            }
            t.gameObject.SetActive(false);
        }

        private static void TryDestroyComponent<T>(Transform root, string path, ushort playerId) where T : Component
        {
            Transform t;
            if (path == "(suit root)" || path == "(suit root)")
            {
                t = root;
            }
            else
            {
                t = FindPath(root, path);
                if (t != null && t.childCount > 0) t = t.GetChild(0);
            }

            if (t == null)
            {
                StarTruckMP.Log.LogWarning($"createPlayer[{playerId}]: '{path}' (for component strip) not found, skipping.");
                return;
            }
            var comp = t.GetComponent<T>();
            if (comp != null)
            {
                GameObject.Destroy(comp);
            }
        }
    }
}
