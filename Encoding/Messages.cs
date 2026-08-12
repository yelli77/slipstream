using Riptide;
using StarTruckMP.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StarTruckMP.Encoding
{
    internal class Messages
    {
        public static playerInfo createPlayer(ushort playerId, Vector3 position, Vector3 rotation, string sector, string playerName)
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

                string displayName = (!string.IsNullOrEmpty(playerName)) ? playerName : "Player " + playerId;
                currentPlayer.NameLabel = CreateNameLabel(displayName, playerId);

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


        /// <summary>
        /// Spawns a visible trailer for a remote player by instantiating a
        /// matching CargoContainer mesh if available. Falls back to a placeholder
        /// cube if no local trailer is currently hitched.
        /// </summary>
        public static GameObject createTrailerMesh(ushort playerId, string containerType = null)
        {
            try
            {
                // Find a CargoContainer matching the requested type, or fall back to any
                var allCargo = GameObject.FindObjectsOfType<CargoContainer>();
                CargoContainer sampleCargo = null;

                if (!string.IsNullOrEmpty(containerType))
                {
                    // First pass: try to find a container matching the type via reflection
                    foreach (var cargo in allCargo)
                    {
                        if (cargo == null || cargo.gameObject == null) continue;
                        try
                        {
                            string cargoTypeStr = "";
                            var field = typeof(CargoContainer).GetField("containerType",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null) cargoTypeStr = field.GetValue(cargo) as string ?? "";
                            if (string.IsNullOrEmpty(cargoTypeStr))
                            {
                                var prop = typeof(CargoContainer).GetProperty("containerType",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (prop != null) cargoTypeStr = prop.GetValue(cargo) as string ?? "";
                            }
                            if (!string.IsNullOrEmpty(cargoTypeStr) && cargoTypeStr == containerType)
                            {
                                sampleCargo = cargo;
                                StarTruckMP.Log.LogInfo($"createTrailerMesh[{playerId}]: matched containerType='{containerType}'");
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // Second pass: fall back to any container if no match found
                if (sampleCargo == null)
                {
                    foreach (var cargo in allCargo)
                    {
                        if (cargo != null && cargo.gameObject != null)
                        {
                            sampleCargo = cargo;
                            if (!string.IsNullOrEmpty(containerType))
                                StarTruckMP.Log.LogWarning($"createTrailerMesh[{playerId}]: no container matching type='{containerType}', using fallback");
                            break;
                        }
                    }
                }

                if (sampleCargo == null)
                {
                    StarTruckMP.Log.LogWarning($"createTrailerMesh[{playerId}]: no CargoContainer found in scene, falling back to placeholder.");
                    return createTrailerPlaceholder(playerId);
                }

                GameObject cargoRoot = sampleCargo.gameObject;

                // Instantiate a copy of the cargo container
                GameObject newTrailer = GameObject.Instantiate(cargoRoot, Vector3.zero, Quaternion.Euler(Vector3.zero));
                newTrailer.name = "RemoteTrailer" + playerId;

                var sectorGO = GameObject.Find("[Sector]");
                if (sectorGO != null)
                {
                    SceneManager.MoveGameObjectToScene(newTrailer, sectorGO.scene);
                }
                newTrailer.transform.SetParent(null);


                // Strip game-logic components keep visuals only
                // Remove ConfigurableJoint BEFORE Rigidbody (dependency)
                foreach (var cj in newTrailer.GetComponentsInChildren<ConfigurableJoint>())
                {
                    try { GameObject.Destroy(cj); } catch { }
                }

                var cargoComp = newTrailer.GetComponent<CargoContainer>();
                if (cargoComp != null) try { GameObject.Destroy(cargoComp); } catch { }

                var rb = newTrailer.GetComponent<Rigidbody>();
                if (rb != null) try { GameObject.Destroy(rb); } catch { }

                // Disable all colliders to prevent unwanted collisions
                foreach (var col in newTrailer.GetComponentsInChildren<Collider>())
                {
                    col.enabled = false;
                }

                // Remove hitch-related components on children
                foreach (var hp in newTrailer.GetComponentsInChildren<MaglockHitchPoint>())
                {
                    if (hp != null) try { GameObject.Destroy(hp); } catch { }
                }

                // Disable any remaining Behaviour components that might cause issues
                foreach (var bh in newTrailer.GetComponentsInChildren<Behaviour>())
                {
                    if (bh != null)
                    {
                        try { bh.enabled = false; } catch { }
                    }
                }

                return newTrailer;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"createTrailerMesh[{playerId}] failed: {ex.Message}, falling back to placeholder.");
                return createTrailerPlaceholder(playerId);
            }
        }

        public static Message createPlayerNameMessage(ushort playerId, string name)
        {
            Message message = Message.Create(MessageSendMode.Reliable, (ushort)messageType.setPlayerName);
            message.AddUShort(playerId);
            message.AddString(name ?? "");
            return message;
        }

        public static Message createMovementMessage(ushort playerId, Vector3 position, Vector3 rotation, Vector3 velocity, Vector3 angVel, bool isTruck, bool inSeat, bool isHonking = false)
        {
            float[] playerTransform = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, velocity.x, velocity.y, velocity.z, angVel.x, angVel.y, angVel.z};

            Message message = Message.Create(MessageSendMode.Unreliable, (ushort)messageType.movementUpdate);
            message.AddUShort(playerId);
            message.AddFloats(playerTransform);
            message.AddBool(isTruck);
            message.AddBool(inSeat);
            message.AddBool(isHonking);

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

        public static Message createTrailerModelMessage(ushort playerId, string containerType)
        {
            Message message = Message.Create(MessageSendMode.Reliable, (ushort)messageType.updateTrailerModel);
            message.AddUShort(playerId);
            message.AddString(containerType ?? "");
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

        /// <summary>
        /// Creates a world-space name label using TextGenerator + Mesh.
        /// Includes dark background quad for contrast.
        /// </summary>
        public static GameObject CreateNameLabel(string name, ushort playerId)
        {
            try
            {
                var sectorGO = GameObject.Find("[Sector]");
                if (sectorGO == null) return null;

                // Diagnostic: find all text components in scene
                var allTMP = GameObject.FindObjectsOfType<TMPro.TextMeshPro>();
                StarTruckMP.Log.LogInfo($"CreateNameLabel[{playerId}]: found {allTMP?.Length ?? 0} TextMeshPro (3D) objects");
                if (allTMP != null && allTMP.Length > 0)
                {
                    for (int t = 0; t < Mathf.Min(allTMP.Length, 5); t++)
                    {
                        if (allTMP[t] != null)
                            StarTruckMP.Log.LogInfo($"  TMP3D[{t}]: '{allTMP[t].text}' name='{allTMP[t].gameObject.name}'");
                    }
                }

                var allTMPUGUI = GameObject.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                StarTruckMP.Log.LogInfo($"CreateNameLabel[{playerId}]: found {allTMPUGUI?.Length ?? 0} TextMeshProUGUI (Canvas) objects");

                // If we find a 3D TextMeshPro, clone it
                if (allTMP != null && allTMP.Length > 0)
                {
                    var source = allTMP[0];
                    StarTruckMP.Log.LogInfo($"CreateNameLabel[{playerId}]: cloning 3D TMP '{source.text}' from '{source.gameObject.name}'");
                    GameObject clone = GameObject.Instantiate(source.gameObject);
                    clone.name = "NameLabel_" + playerId;
                    SceneManager.MoveGameObjectToScene(clone, sectorGO.scene);
                    clone.transform.SetParent(null);

                    var tmp = clone.GetComponent<TMPro.TextMeshPro>();
                    if (tmp != null)
                    {
                        tmp.text = name;
                        tmp.fontSize = 36f;
                        tmp.alignment = TMPro.TextAlignmentOptions.Center;
                        tmp.color = Color.yellow;
                        tmp.enableAutoSizing = false;
                        tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
                        tmp.enableWordWrapping = false;
                    }
                    // Ensure renderer is enabled
                    var renderer = clone.GetComponent<MeshRenderer>();
                    if (renderer != null) renderer.enabled = true;
                    // Scale up the clone
                    clone.transform.localScale = new Vector3(5f, 5f, 5f);
                    StarTruckMP.Log.LogInfo($"CreateNameLabel[{playerId}]: clone ready, scale={clone.transform.localScale}");
                    return clone;
                }

                Font font = null;
                try { font = Font.CreateDynamicFontFromOSFont("Arial", 80); } catch { }
                if (font == null)
                {
                    StarTruckMP.Log.LogWarning($"CreateNameLabel[{playerId}]: no font available");
                    return null;
                }

                GameObject labelObj = new GameObject("NameLabel_" + playerId);
                SceneManager.MoveGameObjectToScene(labelObj, sectorGO.scene);
                labelObj.transform.SetParent(null);

                // Text mesh
                TextGenerator textGen = new TextGenerator();
                var settings = new TextGenerationSettings();
                settings.font = font;
                settings.fontSize = 80;
                settings.fontStyle = FontStyle.Bold;
                settings.textAnchor = TextAnchor.MiddleCenter;
                settings.color = Color.yellow;
                settings.scaleFactor = 1f;
                settings.lineSpacing = 1f;
                settings.richText = false;
                settings.resizeTextForBestFit = false;
                settings.resizeTextMinSize = 10;
                settings.resizeTextMaxSize = 40;
                settings.horizontalOverflow = HorizontalWrapMode.Overflow;
                settings.verticalOverflow = VerticalWrapMode.Overflow;
                settings.generationExtents = new Vector2(1200, 200);
                settings.pivot = new Vector2(0.5f, 0.5f);
                settings.updateBounds = true;
                settings.generateOutOfBounds = true;
                settings.alignByGeometry = false;

                textGen.Populate(name, settings);
                var vertList = new Il2CppSystem.Collections.Generic.List<UIVertex>();
                textGen.GetVertices(vertList);
                UIVertex[] uiVerts = vertList.ToArray();

                if (uiVerts == null || uiVerts.Length == 0)
                {
                    StarTruckMP.Log.LogWarning($"CreateNameLabel[{playerId}]: no vertices");
                    GameObject.Destroy(labelObj);
                    return null;
                }

                Mesh mesh = new Mesh();
                Vector3[] verts = new Vector3[uiVerts.Length];
                Vector2[] uvs = new Vector2[uiVerts.Length];
                Color32[] colors = new Color32[uiVerts.Length];
                for (int i = 0; i < uiVerts.Length; i += 4)
                {
                    // TextGenerator outputs: i=BL, i+1=TL, i+2=BR, i+3=TR
                    // We need standard quad: i=BL, i+1=TL, i+2=TR, i+3=BR
                    // So swap i+2 and i+3 positions
                    verts[i]   = uiVerts[i].position;
                    verts[i+1] = uiVerts[i+1].position;
                    verts[i+2] = uiVerts[i+3].position; // TR
                    verts[i+3] = uiVerts[i+2].position; // BR
                    float uvY0 = Mathf.Clamp(uiVerts[i].uv0.y, 0.01f, 0.99f);
                    float uvY1 = Mathf.Clamp(uiVerts[i+1].uv0.y, 0.01f, 0.99f);
                    float uvY2 = Mathf.Clamp(uiVerts[i+3].uv0.y, 0.01f, 0.99f);
                    float uvY3 = Mathf.Clamp(uiVerts[i+2].uv0.y, 0.01f, 0.99f);
                    uvs[i]   = new Vector2(uiVerts[i].uv0.x, uvY0);
                    uvs[i+1] = new Vector2(uiVerts[i+1].uv0.x, uvY1);
                    uvs[i+2] = new Vector2(uiVerts[i+3].uv0.x, uvY2);
                    uvs[i+3] = new Vector2(uiVerts[i+2].uv0.x, uvY3);
                    colors[i]   = uiVerts[i].color;
                    colors[i+1] = uiVerts[i+1].color;
                    colors[i+2] = uiVerts[i+3].color;
                    colors[i+3] = uiVerts[i+2].color;
                }
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.colors32 = colors;

                int quadCount = uiVerts.Length / 4;
                int[] tris = new int[quadCount * 6];
                int ti = 0;
                for (int i = 0; i < uiVerts.Length; i += 4)
                {
                    // Standard quad triangulation: BL,TL,TR + BL,TR,BR
                    tris[ti++] = i; tris[ti++] = i+1; tris[ti++] = i+2;
                    tris[ti++] = i; tris[ti++] = i+2; tris[ti++] = i+3;
                }
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                Bounds bounds = mesh.bounds;
                float targetW = 12f;
                float textScale = (bounds.size.x > 0) ? (targetW / bounds.size.x) : 0.01f;

                GameObject textObj = new GameObject("NameLabelTxt_" + playerId);
                SceneManager.MoveGameObjectToScene(textObj, labelObj.scene);
                textObj.transform.SetParent(labelObj.transform);
                textObj.transform.localPosition = new Vector3(0, 0, 0.02f);
                textObj.transform.localScale = new Vector3(textScale, textScale, textScale);

                MeshFilter mf = textObj.AddComponent<MeshFilter>();
                mf.mesh = mesh;

                MeshRenderer mr = textObj.AddComponent<MeshRenderer>();
                var textMat = new Material(font.material);
                textMat.SetInt("_Cull", 0); // Disable backface culling
                mr.material = textMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                StarTruckMP.Log.LogInfo($"CreateNameLabel[{playerId}]: created '{name}' ({uiVerts.Length} verts, scale={textScale:F4})");
                return labelObj;
            }
            catch (System.Exception ex)
            {
                StarTruckMP.Log.LogWarning($"CreateNameLabel[{playerId}] failed: {ex.Message}");
                return null;
            }
        }
    }
}
