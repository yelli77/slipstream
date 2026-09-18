using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// Static 'airport departure board' style world-space Canvas+TMP sign for warp gates.
    /// Mounted next to each warp gate showing which players have that gate as their destination.
    /// Format: 'POS 1. PlayerName --- 3.2km', ranked by distance.
    /// The sign is STATIC — fixed orientation set once at creation, no camera-facing rotation.
    /// Like a real airport departure board you read as you fly past.
    /// </summary>
    public static class JumpgateOption1
    {
        // ─── State ───
        private static readonly Dictionary<string, GateBoard> boards = new();
        private static float lastUpdate = 0f;
        private static readonly float UpdateInterval = 1.5f; // seconds between text refreshes
        private static bool initialized = false;

        // Alternates "* "/" *" in front of POS 2-9's position number each UpdatePositions
        // tick (i.e. roughly every UpdateInterval seconds) for a slow blinking-light effect.
        private static bool blinkState = false;
        private static string BlinkMarker => blinkState ? "* " : " *";

        // ─── Constants ───
        private const float HeightOffset = 100f;   // meters directly above the gate, centered
        private const float SignWidth = 12300f;   // canvas units - the 8500 crop was too aggressive (DISTANCE overflowed past the board edge); 13000 was known-good for the old 10-wide DISTANCE column, this accounts for narrowing that column to 8 (was 8500)
        private const float SignHeight = 9000f;   // canvas units (9000x9000)
        private const float FontSizeValue = 600f;  // as requested

        // ─── GateBoard: holds everything for one gate's departure board ───
        private class GateBoard
        {
            public string gateEntryId;
            public GameObject rootObject;
            public TMPro.TextMeshProUGUI tmpLabel;
            public float lastTextUpdate;
            public List<PlayerEntry> currentPlayerEntries = new();
        }

        // ─── PlayerEntry: snapshot of one player heading to a gate ───
        private class PlayerEntry
        {
            public string playerName;
            public float distanceFromGate;
            public bool isLocal;
        }

        // ─── Reflection cache for WarpGate.entryGateId ───
        private static FieldInfo fi_entryGateId;



        /// <summary>
        /// Find a suitable source TextMeshProUGUI to clone from the scene.
        /// Fail-safe against the black-box bug (build-321): a TMP clone whose
        /// template had no usable font asset/material renders as a black box.
        /// Prefer templates that carry a live font; log what we picked.
        /// </summary>
        private static TMPro.TextMeshProUGUI FindSourceTMP()
        {
            TMPro.TextMeshProUGUI fallback = null;
            try
            {
                var allTMP = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                if (allTMP == null) return null;
                foreach (var tmp in allTMP)
                {
                    if (tmp != null && !string.IsNullOrEmpty(tmp.text) && tmp.gameObject.scene.IsValid())
                    {
                        // build-322: never clone a TMP whose component is disabled —
                        // Instantiate copies the enabled state, producing an invisible
                        // black-box board. Also skip our own departure-board TMPs so a
                        // board can never become the template for another board.
                        bool enabled = false;
                        try { enabled = tmp.enabled; } catch { }
                        if (!enabled) continue;
                        bool ownBoard = false;
                        try { ownBoard = tmp.gameObject.name == "DepartureText" || tmp.transform.root.name.StartsWith("DepartureBoard_"); } catch { }
                        if (ownBoard) continue;

                        bool hasFont = false;
                        try { hasFont = tmp.font != null; } catch { }
                        if (hasFont) return tmp;           // best candidate: text + live font
                        fallback ??= tmp;                   // keep as last resort
                    }
                }
            }
            catch { }
            if (fallback != null)
                StarTruckMP.Log.LogWarning("JumpgateOption1: only a font-less TMP template available — board rendering may be unreliable (no font/material).");
            return fallback;
        }

        /// <summary>
        /// Called once when sector loads. Creates departure boards next to each warp gate
        /// that has at least one player heading to it.
        /// </summary>
        public static void CreateBoards()
        {
            try
            {
                JumpgateUtils.CacheReflection();
                Cleanup();
                CleanupLogThrottleState();

                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                string sector = StarTruckClient.currentSector;
                if (string.IsNullOrEmpty(sector) || sector == "none")
                {
                    StarTruckMP.Log.LogInfo("JumpgateOption1: no sector, skipping CreateBoards.");
                    return;
                }

                // Find all warp gates in scene
                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch (Exception ex)
                {
                    StarTruckMP.Log.LogWarning($"JumpgateOption1: FindObjectsOfType<WarpTriggerZone> failed: {ex.Message}");
                    return;
                }

                if (allGates == null || allGates.Length == 0)
                {
                    StarTruckMP.Log.LogInfo("JumpgateOption1: no WarpTriggerZone found in scene.");
                    return;
                }

                StarTruckMP.Log.LogInfo($"JumpgateOption1: found {allGates.Length} gates in sector '{sector}'");

                var sourceTMP = FindSourceTMP();
                if (sourceTMP == null)
                {
                    StarTruckMP.Log.LogWarning("JumpgateOption1: no source TMP found, cannot create boards.");
                    return;
                }

                int boardsCreated = 0;
                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                    // Get entryGateId via reflection on WarpGate component
                    string entryId = JumpgateUtils.GetEntryGateIdForZone(zone);

                    // Find all players heading to this gate
                    // (entryId bleibt als Anzeige-Label unnormalisiert, Boards-Dict-Key
                    // wird unten unveraendert gefuehrt — Normalisierung nur im Vergleich)
                    var playerEntries = CollectPlayersForGate(entryId, zone.transform.position);

                    // Create the board (always — show FREE when no players)
                    var board = CreateBoardForGate(entryId, zone.transform, playerEntries, sourceTMP);
                    if (board != null)
                    {
                        boards[entryId] = board;
                        boardsCreated++;
                    }
                }

                StarTruckMP.Log.LogInfo($"JumpgateOption1: {boardsCreated} departure board(s) created in '{sector}'.");
                initialized = true;
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.CreateBoards error: {ex}");
            }
        }

        // custom-build-335: Log-Drossel fuer die per-Frame-Diagnose in CollectPlayersForGate
        // ('gate mismatch' + 'matched=0' wurden pro Board pro Spieler pro Frame geloggt -
        // 8,9 MB Log). Pro Gate-Board nur loggen, wenn sich der Zustand aendert (Spieler-
        // Satz/Gate-Kombi) oder max. 1x pro 10s.
        private const float LOG_THROTTLE_SECONDS = 10f;
        private static readonly Dictionary<string, float> lastMismatchLog = new Dictionary<string, float>();
        private static readonly Dictionary<string, string> lastMismatchSignature = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> lastCollectLog = new Dictionary<string, float>();
        private static readonly Dictionary<string, string> lastCollectSignature = new Dictionary<string, string>();

        private static bool ShouldLogOnChangeOnly(Dictionary<string, string> sigMap,
            string key, string signature)
        {
            // Build-368: ausschliesslich Aenderungsgetrieben — derselbe Zustand wird NIE
            // erneut geloggt (kein Zeitfenster), nur die Veraenderung selbst.
            if (sigMap.TryGetValue(key, out var prevSig) && prevSig == signature) return false;
            sigMap[key] = signature;
            return true;
        }

        private static bool ShouldLogThrottled(Dictionary<string, float> timeMap, Dictionary<string, string> sigMap,
            string key, string signature, float now)
        {
            bool sigChanged = !sigMap.TryGetValue(key, out var prevSig) || prevSig != signature;
            timeMap.TryGetValue(key, out var last);
            if (!sigChanged && now - last < LOG_THROTTLE_SECONDS) return false;
            sigMap[key] = signature;
            timeMap[key] = now;
            return true;
        }

        private static void CleanupLogThrottleState()
        {
            lastMismatchLog.Clear();
            lastMismatchSignature.Clear();
            lastCollectLog.Clear();
            lastCollectSignature.Clear();
        }

        /// <summary>
        /// Collect all players heading to a specific gate, ranked by distance.
        /// </summary>
        private static List<PlayerEntry> CollectPlayersForGate(string entryGateId, Vector3 gateWorldPos)
        {
            var entries = new List<PlayerEntry>();

            // Remote players
            string wantGate = JumpgateUtils.NormalizeGateId(entryGateId);
            int candidates = 0, matched = 0;
            try
            {
                foreach (var kv in StarTruckClient.playerList)
                {
                    var p = kv.Value;
                    candidates++;
                    // Bug 2: exakter String-Vergleich scheiterte, wenn Sender und Board
                    // unterschiedliche Gate-ID-Formate verwenden (entry vs. exit zone,
                    // GameObject-Name vs. entryGateId). Vergleich ueber den kanonischen
                    // Klammer-Kern ('03_Alpha') statt des rohen Strings.
                    if (JumpgateUtils.NormalizeGateId(p.destinationGateId) != wantGate)
                    {
                        // custom-build-335: gedrosselt - frueher pro Frame pro Spieler pro
                        // Board geloggt (8,9 MB Log). Nur bei geaenderter Kombination
                        // (Board/Gate vs. Spieler-Destination) oder max. 1x pro 10s.
                        try
                        {
                            // Build-368: nur noch bei destGate-AENDERUNG des Spielers loggen
                            // (frueher max. 1x/10s pro Board — das war pro Board x Spieler
                            // immer noch Dauerspam, wenn destGate leer blieb). TODO(368):
                            // destGate ist im movementUpdate-Protokoll vollstaendig verdrahtet
                            // (Sender Encoding/Messages.cs:442, Server dedicated/MessageHandler.cs:195-208,
                            // Empfaenger Client/Client.cs:655-720), Log 367 zeigt aber trotzdem
                            // dauerhaft '' — vermutlich laeuft der Remote-Spieler noch ohne
                            // destGate-faehigen Build; hier neu pruefen, sobald beide Seiten 368 haben.
                            string mmSig = $"{kv.Key}:{p.destinationGateId}";
                            string mmKey = "mm:" + (entryGateId ?? "?") + ":" + kv.Key;
                            if (ShouldLogOnChangeOnly(lastMismatchSignature, mmKey, mmSig))
                                StarTruckMP.Log.LogInfo($"JumpgateOption1: gate mismatch — board '{entryGateId}' (norm '{wantGate}') vs player {kv.Key} destGate '{p.destinationGateId}' (norm '{JumpgateUtils.NormalizeGateId(p.destinationGateId)}')");
                        }
                        catch { }
                        continue;
                    }
                    matched++;

                    // p.truckTrans.Pos is the network-synced ABSOLUTE position (origin + local),
                    // while gateWorldPos is the local/recentered scene position — convert to the
                    // same frame before comparing, same bug class as the local-player distance below.
                    Vector3 remoteLocalPos = StarTruckClient.floatingOrigin != null
                        ? p.truckTrans.Pos - StarTruckClient.floatingOrigin.m_currentOrigin
                        : p.truckTrans.Pos;
                    float dist = Vector3.Distance(gateWorldPos, remoteLocalPos);
                    entries.Add(new PlayerEntry
                    {
                        playerName = p.Name ?? $"Player_{kv.Key}",
                        distanceFromGate = dist,
                        isLocal = false
                    });
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: playerList iteration error: {ex.Message}");
            }
            // custom-build-335: gedrosselt - nur bei veraenderter Spieler-Situation oder
            // max. 1x pro 10s pro Board (frueher pro Frame).
            try
            {
                string sig = $"candidates={candidates},matched={matched},entries={entries.Count}";
                string key = entryGateId ?? "?";
                if (ShouldLogThrottled(lastCollectLog, lastCollectSignature, key, sig, Time.realtimeSinceStartup))
                    StarTruckMP.Log.LogInfo($"JumpgateOption1: CollectPlayersForGate('{entryGateId}' -> norm '{JumpgateUtils.NormalizeGateId(entryGateId)}'): candidates={candidates}, matched={matched}");
            }
            catch { }

            // Local player
            try
            {
                if (!string.IsNullOrEmpty(StarTruckClient.currentDestinationGateId)
                    && JumpgateUtils.NormalizeGateId(StarTruckClient.currentDestinationGateId) == JumpgateUtils.NormalizeGateId(entryGateId)
                    && StarTruckClient.myTruck != null)
                {
                    // NOTE: gateWorldPos (zone.transform.position) is already in the local/
                    // recentered scene frame, same as myTruck.transform.position — do NOT add
                    // floatingOrigin.m_currentOrigin here, that mixes local and absolute coordinate
                    // spaces and produced a bogus constant offset (~size of the origin shift) in the
                    // displayed distance, e.g. showing "2.5 km" while standing right at the gate.
                    Vector3 myPos = StarTruckClient.myTruck.transform.position;
                    float dist = Vector3.Distance(gateWorldPos, myPos);
                    string myName = StarTruckClient.myPlayerName ?? "Du";
                    entries.Add(new PlayerEntry
                    {
                        playerName = myName,
                        distanceFromGate = dist,
                        isLocal = true
                    });
                }
            }
            catch { }

            // POS 1 is 'sticky': the player who was FIRST to come within arrival range of
            // this gate keeps the top slot even if someone else is currently physically
            // closer. Everyone else (and POS 1 itself, if nobody has arrived yet) is
            // ordered by current distance.
            ApplyStickyFirstArrivalOrdering(entryGateId, entries);
            return entries;
        }

        // Per-gate: playerName -> the Time.realtimeSinceStartup at which that player was
        // FIRST seen within ArrivalThresholdMeters of this gate. Drives the sticky POS-1 rule.
        private static readonly Dictionary<string, Dictionary<string, float>> arrivalTimes = new();
        private const float ArrivalThresholdMeters = 500f;

        private static void ApplyStickyFirstArrivalOrdering(string gateEntryId, List<PlayerEntry> entries)
        {
            if (!arrivalTimes.TryGetValue(gateEntryId, out var gateArrivals))
            {
                gateArrivals = new Dictionary<string, float>();
                arrivalTimes[gateEntryId] = gateArrivals;
            }

            float now = Time.realtimeSinceStartup;
            var currentKeys = new HashSet<string>();
            foreach (var e in entries)
            {
                currentKeys.Add(e.playerName);
                if (e.distanceFromGate < ArrivalThresholdMeters && !gateArrivals.ContainsKey(e.playerName))
                {
                    gateArrivals[e.playerName] = now;
                }
            }

            // Forget players no longer registered to this gate, so a later unrelated
            // player can't inherit a stale arrival slot.
            if (gateArrivals.Count > 0)
            {
                List<string> stale = null;
                foreach (var key in gateArrivals.Keys)
                {
                    if (!currentKeys.Contains(key))
                    {
                        stale ??= new List<string>();
                        stale.Add(key);
                    }
                }
                if (stale != null)
                    foreach (var key in stale) gateArrivals.Remove(key);
            }

            // Default order: closest first.
            entries.Sort((a, b) => a.distanceFromGate.CompareTo(b.distanceFromGate));

            // Find whoever arrived earliest (if anyone has arrived at all) and bump them to POS 1.
            PlayerEntry firstArrived = null;
            float earliestTime = float.MaxValue;
            foreach (var e in entries)
            {
                if (gateArrivals.TryGetValue(e.playerName, out var t) && t < earliestTime)
                {
                    earliestTime = t;
                    firstArrived = e;
                }
            }

            if (firstArrived != null && entries.Count > 0 && entries[0] != firstArrived)
            {
                entries.Remove(firstArrived);
                entries.Insert(0, firstArrived);
            }
        }

        /// <summary>
        /// Build one fixed-width table row (monospaced via TMP's &lt;mspace&gt; tag).
        /// `driverCell` must already be padded/formatted (plain text, or already wrapped
        /// in rich-text tags with the padding applied to the plain text underneath).
        /// </summary>
        private static string FormatRow(string pos, string driverCell, string distance)
        {
            return pos.PadRight(6) + driverCell + distance;
        }

        /// <summary>
        /// Build the multi-line departure text for a gate: header line 1 unchanged,
        /// line 2 is a table header (POS / DRIVER / DISTANCE), followed by one row per
        /// player currently registered to this gate, ranked by distance.
        /// </summary>
        // Hard character cap for driver names so a long name can never push the DISTANCE
        // column (or, for POS 1's enlarged text, the board edge) out of alignment.
        private const int DriverNameMaxChars = 6;

        // DISTANCE column is right-aligned under its header; wide enough for the
        // longest realistic value (e.g. "12.3km") plus a little breathing room.
        private const int DistanceColumnWidth = 8;

        private static string TruncateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length > DriverNameMaxChars ? name.Substring(0, DriverNameMaxChars) : name;
        }

        // Fixed number of position slots on the board (POS 1 = bottom/next to jump).
        // Hard cap: any player beyond MaxBoardPositions simply isn't shown (they still
        // count for gate-detection etc., just not on this particular sign), and unused
        // slots below the cap are rendered as dimmed empty placeholder rows so the board
        // always shows the same fixed layout.
        private const int MaxBoardPositions = 9;

        // Per-position color/size scheme:
        //   POS 1        - green, 3x size (about to jump)
        //   POS 2        - yellow, 1.5x size (up next)
        //   POS 3..9     - blue, normal size
        private const string Pos1Color = "#33FF66";
        private const string Pos2Color = "#FFE600";
        private const string RestColor = "#4DA6FF";
        private const float Pos1SizePercent = 300f;

        // POS 1's "*NOW*  name  *NOW*" layout: *NOW* stays at normal size/color, only the
        // name is enlarged. Pos1SidePadChars is the (normal-size) gap kept on each side of
        // the enlarged name, between it and each *NOW*.
        // Same blinkState tick as the POS 2-9 marker: alternates the stars on/off (keeping
        // the string a fixed 5 chars either way, so the name's position never shifts).
        private static string Pos1NowText => blinkState ? "*NOW*" : " NOW ";
        private const int Pos1SidePadChars = 6;

        private static string BuildDepartureText(List<PlayerEntry> entries)
        {
            var sb = new System.Text.StringBuilder();
            // Center just the title line; the table below stays left-aligned/monospaced.
            sb.AppendLine("<align=center>=== DEPARTURE GATE ===</align>");

            // <mspace> forces fixed-width character spacing so the padded columns
            // actually line up despite the proportional font.
            sb.Append("<mspace=0.6em>");
            sb.AppendLine(FormatRow("  POS", "DRIVER".PadRight(18), "DISTANCE".PadLeft(DistanceColumnWidth)));

            // Hard cut: only the first MaxBoardPositions entries (already ranked, POS 1
            // first) are shown on this board.
            int shownCount = Mathf.Min(entries.Count, MaxBoardPositions);

            // Build every row first (still keyed by its real rank, pos 1 = best/next to
            // jump), then render them BOTTOM-UP: pos 1 ends up as the last line (closest
            // to the gate on the sign), higher/farther positions stack above it - easier
            // to see who's up next while approaching.
            //
            // The WHOLE row (POS number, name/FREE, and distance) is colored and sized
            // together per position tier now - POS 1 and POS 2 each only ever occupy one
            // row, so there's no cross-row column alignment to preserve for them; POS 3-9
            // all share the same normal size, so the usual PadRight-based table alignment
            // still applies among themselves.
            var rows = new List<string>(MaxBoardPositions);
            for (int pos = 1; pos <= MaxBoardPositions; pos++)
            {
                string nameRaw;
                string distField;
                if (pos <= shownCount)
                {
                    var entry = entries[pos - 1];
                    nameRaw = TruncateName(entry.playerName);
                    distField = entry.distanceFromGate < 1000f
                        ? $"{entry.distanceFromGate:F0}m"
                        : $"{entry.distanceFromGate / 1000f:F1}km";
                }
                else
                {
                    // Empty slot - show FREE in that position's own color/size instead of
                    // a generic dimmed placeholder.
                    nameRaw = "FREE";
                    distField = "---";
                }

                string row;
                if (pos == 1)
                {
                    // POS 1 special layout: "*NOW*   <name>   *NOW*" - *NOW* flush to each
                    // edge (now at NORMAL size, same as POS 3-9) with just the name itself
                    // enlarged (3x) and centered between them. Fixed character budget on
                    // both sides so the *NOW*s always land in the same spot regardless of
                    // name length.
                    //
                    // Mixed sizes on one line reproduces the same TMP <mspace>/<size>
                    // interaction as the old table-highlight trick: <mspace> fixes each
                    // character's advance width to the point size in effect when the tag
                    // opened, so the enlarged name needs its OWN nested mspace scope (close
                    // the outer one, open a fresh one inside <size>, close it, reopen the
                    // outer one) rather than inheriting the outer mspace opened at normal size.
                    string namePadded = nameRaw.PadRight(DriverNameMaxChars);
                    string pad = new string(' ', Pos1SidePadChars);
                    string content =
                        Pos1NowText + pad
                        + $"</mspace><size={Pos1SizePercent}%><mspace=0.6em>{namePadded}</mspace></size><mspace=0.6em>";
                    row = $"<color={Pos1Color}>{content}</color>";
                }
                else if (pos == 2)
                {
                    // Same size/layout as POS 3-9 (normal table row), just yellow instead of blue.
                    string posLabel = BlinkMarker + pos;
                    string rowPlain = FormatRow(posLabel, nameRaw.PadRight(18), distField.PadLeft(DistanceColumnWidth));
                    row = $"<color={Pos2Color}>{rowPlain}</color>";
                }
                else
                {
                    string posLabel = BlinkMarker + pos;
                    string rowPlain = FormatRow(posLabel, nameRaw.PadRight(18), distField.PadLeft(DistanceColumnWidth));
                    row = $"<color={RestColor}>{rowPlain}</color>";
                }
                rows.Add(row);
            }

            for (int i = rows.Count - 1; i >= 0; i--)
                sb.AppendLine(rows[i]);

            sb.Append("</mspace>");
            return sb.ToString();
        }

        /// <summary>
        /// Create a single departure board next to a warp gate.
        /// Uses the from-scratch Canvas+TMP clone recipe from NOTES_WORLDSPACE_UI.md.
        /// </summary>
        private static GateBoard CreateBoardForGate(
            string entryGateId,
            Transform gateTransform,
            List<PlayerEntry> entries,
            TMPro.TextMeshProUGUI sourceTMP)
        {
            Vector3 gatePos = gateTransform.position;

            // Compute approach direction (use gate's forward, or fallback to world forward)
            Vector3 approachDir = gateTransform.forward;
            if (approachDir.sqrMagnitude < 0.01f)
                approachDir = Vector3.forward;
            approachDir = approachDir.normalized;

            // Position sign centered directly "above" the gate - using the GATE's own up
            // vector, not world-up. Some gates/rings are themselves tilted (mounted at an
            // angle on an asteroid, rotated for visual variety, etc.); offsetting and
            // orienting with world Vector3.up made the sign upright in world space but
            // visibly crooked relative to that particular tilted gate ring. Using the gate's
            // local up keeps the sign flush with the ring's own orientation at every gate.
            Vector3 gateUp = gateTransform.up;
            if (gateUp.sqrMagnitude < 0.01f)
                gateUp = Vector3.up;
            Vector3 signPos = gatePos + gateUp * HeightOffset;

            // Orientation: face back along the approach direction (like a roadside sign),
            // banked to match the gate's own tilt.
            Quaternion fixedRot = Quaternion.LookRotation(-approachDir, gateUp);

            // ── Create Canvas root ──
            GameObject canvasObj = new GameObject($"DepartureBoard_{entryGateId}");
            canvasObj.transform.position = signPos;
            canvasObj.transform.rotation = fixedRot;
            canvasObj.transform.localScale = Vector3.one * 0.01f; // Scale canvas down to world size

            // Parent to gate so Floating Origin shifts move the board automatically
            canvasObj.transform.SetParent(gateTransform, true);

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 500;

            var canvasRT = canvasObj.GetComponent<RectTransform>();
            if (canvasRT == null) canvasRT = canvasObj.AddComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(SignWidth, SignHeight);

            // ── Background panel (dark board backdrop behind the text) ──
            GameObject panelObj = new GameObject("BoardBackground");
            panelObj.transform.SetParent(canvasObj.transform, false);
            var panelRT = panelObj.AddComponent<RectTransform>();
            panelRT.anchorMin = Vector2.zero;
            panelRT.anchorMax = Vector2.one;
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;
            panelRT.localScale = Vector3.one;
            var panelImg = panelObj.AddComponent<UnityEngine.UI.Image>();
            panelImg.color = new Color(0.05f, 0.05f, 0.08f, 0.35f);  // much more transparent than before (was 0.85)
            panelObj.transform.SetAsFirstSibling();

            // ── Clone TMP from source ──
            GameObject tmpObj = UnityEngine.Object.Instantiate(sourceTMP.gameObject, canvasObj.transform);
            tmpObj.name = "DepartureText";

            // Reset transform to fill canvas
            var tmpRT = tmpObj.GetComponent<RectTransform>();
            if (tmpRT != null)
            {
                tmpRT.anchorMin = Vector2.zero;
                tmpRT.anchorMax = Vector2.one;
                tmpRT.offsetMin = Vector2.zero;
                tmpRT.offsetMax = Vector2.zero;
                tmpRT.localScale = Vector3.one;
            }

            var tmp = tmpObj.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp == null)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: TMP component missing on clone for gate '{entryGateId}'");
                UnityEngine.Object.Destroy(canvasObj);
                return null;
            }

            // 0-pre (build-322): IL2CPP Instantiate preserves the template's enabled state.
            //     If anything disabled the template TMP between generation rounds (our own
            //     rebuild path, other mods, or the game culling UIs in the departed sector),
            //     the clone would be born disabled => invisible text => black box. Force
            //     enabled=true unconditionally, symmetric to the alpha/canvas re-asserts.
            try
            {
                if (!tmp.enabled)
                {
                    StarTruckMP.Log.LogWarning($"JumpgateOption1: clone for gate '{entryGateId}' inherited tmp.enabled=false from template — forcing enabled=true (build-322).");
                    tmp.enabled = true;
                }
            }
            catch (Exception enEx)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: tmp.enabled re-assert failed for gate '{entryGateId}': {enEx.Message}");
            }

            // ── IL2CPP clone gotchas: fix them in the right order ──

            // 0. Fail-safe (build-321): a clone without ANY usable font asset/material
            //    renders as a black box in Purity (post-sector-change, URP). Rather than
            //    shipping a visibly broken board, bail out cleanly — no board at all.
            //    UpdatePositions will retry on the next tick with a fresh template.
            //    (build-322): same for a template whose TMP is disabled — don't clone it.
            try
            {
                if (tmp.font == null || sourceTMP.font == null || !sourceTMP.enabled)
                {
                    StarTruckMP.Log.LogWarning($"JumpgateOption1: clone for gate '{entryGateId}' has no usable font (clone.font={(tmp.font != null ? tmp.font.name : "null")}, template.font={(sourceTMP.font != null ? sourceTMP.font.name : "null")}, template.enabled={sourceTMP.enabled}) — refusing to create a black-box board.");
                    UnityEngine.Object.Destroy(canvasObj);
                    return null;
                }
            }
            catch (Exception fsEx)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: font fail-safe check failed for gate '{entryGateId}': {fsEx.Message}");
                UnityEngine.Object.Destroy(canvasObj);
                return null;
            }

            // 1. Disable auto-sizing BEFORE setting fontSize (IL2CPP quirk)
            try { tmp.enableAutoSizing = false; } catch { }

            // 2. Re-bind font and material from source — ALWAYS copy (not just when null),
            //    because IL2CPP Instantiate can leave a non-null but broken reference.
            try
            {
                if (sourceTMP.font != null)
                    tmp.font = sourceTMP.font;
                if (sourceTMP.fontSharedMaterial != null)
                    tmp.fontSharedMaterial = sourceTMP.fontSharedMaterial;
                if (sourceTMP.fontMaterial != null)
                    tmp.fontMaterial = sourceTMP.fontMaterial;
            }
            catch (Exception rebindEx)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: font/material re-bind failed for gate '{entryGateId}': {rebindEx.Message}");
            }

            // 2b. Last-resort fallback: if the template carried no font, grab any loaded
            //     TMP_FontAsset from memory so we never end up with a missing-font clone.
            try
            {
                if (tmp.font == null)
                {
                    var fonts = UnityEngine.Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0 && fonts[0] != null)
                    {
                        tmp.font = fonts[0];
                        StarTruckMP.Log.LogWarning($"JumpgateOption1: font fallback used for gate '{entryGateId}' (FontAsset '{fonts[0].name}').");
                    }
                }
            }
            catch (Exception ffEx)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: font-asset fallback failed for gate '{entryGateId}': {ffEx.Message}");
            }

            // 3. Set fontSize (must be AFTER disabling auto-sizing)
            tmp.fontSize = FontSizeValue;
            tmp.fontSizeMin = FontSizeValue;
            tmp.fontSizeMax = FontSizeValue;

            // 4. Alignment: bottom-left. The board's canvas/RectTransform is much taller
            // than the handful of text lines it holds; anchoring to the TOP left the whole
            // block (including POS 1, the row players actually care about) crammed near the
            // top edge with a big empty gap below. Bottom-left anchoring means the text block
            // grows UPWARD from the bottom edge, so combined with rows being appended
            // bottom-up (POS 1 last => POS 1 ends up as the very last/bottom-most line), POS 1
            // now sits right at the bottom of the sign - closest to eye level while approaching.
            tmp.alignment = TMPro.TextAlignmentOptions.BottomLeft;

            // Disable word wrapping so the table columns don't break onto new lines,
            // and let overflow show (the board/canvas is sized generously already).
            try { tmp.enableWordWrapping = false; } catch { }
            try { tmp.overflowMode = TMPro.TextOverflowModes.Overflow; } catch { }

            // 5. Text color: explicit white — Rich Text <color> tags handle per-line colors.
            //    IL2CPP clones can inherit a dark/black default that makes text invisible
            //    on the near-black background panel.
            tmp.color = new UnityEngine.Color(1f, 1f, 1f, 0.8f);

            // 5b. Alpha re-assert (NOTES_WORLDSPACE_UI): Instantiate() preserves whatever
            //     alpha state the source happened to have — a 0-alpha clone is invisible.
            try
            {
                var cr = tmpObj.GetComponent<CanvasRenderer>();
                if (cr != null) cr.SetAlpha(1f);
                var cg = tmpObj.GetComponent<UnityEngine.CanvasGroup>();
                if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = false; }
            }
            catch { }

            // 6. Re-assert active state
            tmpObj.SetActive(true);

            // 7. Raycast target off
            tmp.raycastTarget = false;

            // 8. Set initial text
            tmp.text = BuildDepartureText(entries);

            // 9. Force mesh update
            try { tmp.ForceMeshUpdate(); } catch { }

            // 10. Force canvas update
            try { Canvas.ForceUpdateCanvases(); } catch { }

            // 11. Diagnostic logging (build-321): make the next test unambiguous — log
            //     font/material/autosizing/alpha state at creation so a black box can be
            //     traced to its exact cause from the client log alone.
            try
            {
                string fontName = tmp.font != null ? tmp.font.name : "NULL";
                string matName = "NULL";
                try { matName = tmp.fontSharedMaterial != null ? tmp.fontSharedMaterial.name : "NULL"; } catch { }
                string crAlpha = "n/a";
                try { crAlpha = tmpObj.GetComponent<CanvasRenderer>() != null ? tmpObj.GetComponent<CanvasRenderer>().GetAlpha().ToString("F2") : "no-CR"; } catch { }
                bool canvasEnabled = canvas != null && canvas.enabled;
                bool tmpEnabled = tmp.enabled;
                StarTruckMP.Log.LogInfo($"JumpgateOption1: board render-state for gate '{entryGateId}': font='{fontName}', material='{matName}', autoSizing={tmp.enableAutoSizing}, fontSize={tmp.fontSize}, canvas.enabled={canvasEnabled}, tmp.enabled={tmpEnabled}, go.active={tmpObj.activeSelf}, canvasRenderer.alpha={crAlpha}");
            }
            catch (Exception diagEx)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1: render-state diagnostics failed for gate '{entryGateId}': {diagEx.Message}");
            }

            StarTruckMP.Log.LogInfo($"JumpgateOption1: board created for gate '{entryGateId}' at ({signPos.x:F0},{signPos.y:F0},{signPos.z:F0}) with {entries.Count} player(s).");

            return new GateBoard
            {
                gateEntryId = entryGateId,
                rootObject = canvasObj,
                tmpLabel = tmp,
                lastTextUpdate = Time.realtimeSinceStartup,
                currentPlayerEntries = new List<PlayerEntry>(entries)
            };
        }

        /// <summary>
        /// Called every frame from Harmony patch. Updates text content periodically
        /// (not every frame — string allocation is throttled).
        /// Handles players appearing/disappearing from gates.
        /// </summary>
        public static void UpdatePositions()
        {
            if (!initialized || boards.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            if (now - lastUpdate < UpdateInterval) return;
            lastUpdate = now;
            blinkState = !blinkState; // drives the POS 2-9 blink marker; flips once per tick

            try
            {
                if (!StarTruckClient.client.IsConnected) return;
                if (StarTruckClient.myTruck == null) return;

                // Find all gates again to refresh player lists
                WarpTriggerZone[] allGates;
                try { allGates = UnityEngine.Object.FindObjectsOfType<WarpTriggerZone>(); }
                catch { return; }

                if (allGates == null || allGates.Length == 0) return;

                // Track which gate IDs we've seen this update cycle
                var seenGateIds = new HashSet<string>();

                foreach (var zone in allGates)
                {
                    if (zone == null || zone.gameObject == null) continue;

                                        string entryId = JumpgateUtils.GetEntryGateIdForZone(zone);

                    seenGateIds.Add(entryId);

                    // Collect current players for this gate
                    var currentEntries = CollectPlayersForGate(entryId, zone.transform.position);

                    // Always keep board alive — update text to FREE when empty
                    // (boards are permanent, never destroyed)

                    // Always rebuild (the blink marker on POS 2-9 needs to flip every tick
                    // regardless of whether the player list itself changed).
                    bool changed = true;
                    if (boards.TryGetValue(entryId, out var board))
                    {

                        if (changed && board.tmpLabel != null)
                        {
                            board.tmpLabel.text = BuildDepartureText(currentEntries);
                            board.currentPlayerEntries = new List<PlayerEntry>(currentEntries);
                            try { board.tmpLabel.ForceMeshUpdate(); } catch { }
                        }
                    }
                    else
                    {
                        // New gate with players — create board
                        var sourceTMP = FindSourceTMP();
                        if (sourceTMP != null)
                        {
                            var newBoard = CreateBoardForGate(entryId, zone.transform, currentEntries, sourceTMP);
                            if (newBoard != null)
                                boards[entryId] = newBoard;
                        }
                    }
                }

                // Destroy boards for gates that no longer exist in scene
                var toRemove = new List<string>();
                foreach (var kv in boards)
                {
                    if (!seenGateIds.Contains(kv.Key))
                        toRemove.Add(kv.Key);
                }
                foreach (var key in toRemove)
                {
                    if (boards.TryGetValue(key, out var staleBoard))
                    {
                        if (staleBoard.rootObject != null)
                            UnityEngine.Object.Destroy(staleBoard.rootObject);
                        StarTruckMP.Log.LogInfo($"JumpgateOption1: removed stale board for gate '{key}' (gate not in scene).");
                    }
                    boards.Remove(key);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.UpdatePositions error: {ex}");
            }
        }

        /// <summary>
        /// Destroy all created boards and clear internal state.
        /// Safe to call multiple times.
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                foreach (var kv in boards)
                {
                    if (kv.Value?.rootObject != null)
                        UnityEngine.Object.Destroy(kv.Value.rootObject);
                }
                boards.Clear();
                initialized = false;
                lastUpdate = 0f;
                StarTruckMP.Log.LogInfo("JumpgateOption1: cleaned up all departure boards.");
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"JumpgateOption1.Cleanup error: {ex}");
            }
        }

        /// <summary>
        /// Force the next UpdatePositions() tick to run immediately.
        /// Call when destinationGateId changes so boards refresh without the 1.5s delay.
        /// </summary>
        public static void ForceRefresh()
        {
            // Idempotent: multiple calls per frame must be harmless. Only reset the
            // timer (and log) when a forced rebuild is actually pending or scheduled —
            // after lastUpdate=0 the next UpdatePositions tick rebuilds anyway.
            if (lastUpdate == 0f)
            {
                StarTruckMP.Log.LogDebug("JumpgateOption1: ForceRefresh requested - already pending, skipping duplicate.");
                return;
            }
            lastUpdate = 0f;
            StarTruckMP.Log.LogInfo("JumpgateOption1: ForceRefresh requested - departure boards rebuild on next UpdatePositions tick.");
        }
    }
}
