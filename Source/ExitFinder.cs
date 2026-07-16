using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

namespace LeadMeOut
{
    public class ExitFinder
    {
        private bool isActive = false;

        private Transform mainEntranceTarget = null;
        private Transform mineshaftBottomTarget = null; // BottomElevatorPanel
        private Transform mineshaftTopTarget = null;    // EntranceTeleportA (top floor exit door)
        private Transform realMainEntrance = null;      // EntranceTeleportA, always — fallback if a mineshaft path fails
        private List<Transform> fireExitTargets = new List<Transform>();

        // Reusable NavMeshPath instances. NavMeshPath wraps native memory, and allocating one
        // per pathfind (this was happening up to ~4x per GetPath, times 3 lines, times 10Hz) is a
        // steady source of GC pressure — the kind that shows up as movement micro-freezes. These
        // are scratch buffers: each is filled and read within a single synchronous call before
        // the next use, so sharing them is safe. NOT thread-safe, but all pathfinding here runs
        // on the main thread.
        private readonly NavMeshPath scratchPath = new NavMeshPath();
        private readonly NavMeshPath scratchPathDoor = new NavMeshPath();
        // Separate instance for the reachability helpers (ClosestReachableDistanceTo /
        // IsPathComplete). These run in tight loops during door-graph construction and must not
        // clobber the scratch paths a GetPath call may be mid-way through using.
        private readonly NavMeshPath scratchPathReach = new NavMeshPath();

        // Linear mode rendering
        private List<LineRenderer> mainEntranceSegments = new List<LineRenderer>();
        private List<LineRenderer> mainEntranceDiamonds = new List<LineRenderer>();
        private List<GameObject> mainEntranceShapes = new List<GameObject>();
        private GameObject mainEntranceRoot = null;
        private GameObject mainEntranceMarker = null; // 4-way dead-end marker

        private List<List<LineRenderer>> fireExitSegmentSets = new List<List<LineRenderer>>();
        private List<List<LineRenderer>> fireExitDiamondSets = new List<List<LineRenderer>>();
        private List<List<GameObject>> fireExitShapeSets = new List<List<GameObject>>();
        private List<GameObject> fireExitRoots = new List<GameObject>();
        private List<GameObject> fireExitMarkers = new List<GameObject>(); // 4-way dead-end markers

        // Compass mode rendering
        private GameObject compassOverlayRoot = null;
        private RectTransform mainEntrancePip = null;
        private RectTransform fireExitPip = null;

        private float updateTimer = 999f;
        private float pulseTimer = 0f; // accumulates deltaTime, drives locked-door pulse
        private bool wasInsideFactory = false;

        // "Which locked door unlocks the route" solver — throttled + cached
        private float doorSimTimer = 999f;
        private const float DOOR_SIM_INTERVAL = 2f;   // re-solve at most once every 2 seconds

        // Shared locked-door cache.
        // FindObjectsOfType<DoorLock>() walks EVERY object in the scene and allocates a new
        // array each call. Three separate systems needed the door list, and between them they
        // were running that scan ~30x/second — and it gets more expensive as the level fills
        // with entities and loot. Doors don't spawn mid-round, so scan rarely and share the
        // result. Locked STATE is still checked live at each use, since doors do get unlocked.
        private List<(DoorLock door, Collider col)> cachedLockedDoors = null;
        private float doorCacheTimer = 999f;
        private const float DOOR_CACHE_INTERVAL = 5f; // rebuild the door list every 5 seconds

        private List<(DoorLock door, Collider col)> GetDoorCache()
        {
            if (cachedLockedDoors == null)
            {
                cachedLockedDoors = new List<(DoorLock, Collider)>();
                foreach (DoorLock d in GameObject.FindObjectsOfType<DoorLock>())
                {
                    if (d == null) continue;
                    Collider c = d.GetComponentInChildren<Collider>();
                    if (c == null) continue;
                    cachedLockedDoors.Add((d, c));
                }
                Plugin.Logger.LogDebug($"LeadMeOut: Door cache rebuilt — {cachedLockedDoors.Count} door(s) with colliders.");
            }
            return cachedLockedDoors;
        }

        private Vector3 smoothedPlayerPos = Vector3.zero;
        private bool smoothedPosInitialized = false;
        private float smoothSpeed = 8f;
        private int bezierSteps = 12;

        private NavigationMode lastNavMode = NavigationMode.LinearMode;

        // Compass overlay constants matching game HUD
        private const string COMPASS_PATH = "Systems/UI/Canvas/IngamePlayerHUD";
        private const float COMPASS_WIDTH = 500f;
        private const float COMPASS_HEIGHT_UI = 36.39f;
        private const float COMPASS_ANCHOR_X = 0f;
        private const float COMPASS_ANCHOR_Y = -28.0f;
        private const float COMPASS_FOV = 240f;
        private const float COMPASS_VERTICAL_OFFSET = 12f;
        private const float COMPASS_FADE_ZONE = 0.15f;
        private const float COMPASS_HIDE_MARGIN = 5f;

        // Returned by GetPath - carries the point list and how the path terminates
        private struct PathResult
        {
            public List<Vector3> Points;
            public bool IsLockedDoor; // path blocked but making progress — pulse the line
            public bool IsDeadEnd;    // path stopped with no clear continuation — show 4-way marker

            // True ONLY when a real DoorLock was actually identified as the blocker.
            //
            // IsLockedDoor is a misnomer: it's the "pulse this line" flag, and GetPath raises it
            // for ANY unbridgeable partial path — including ones with no door anywhere near them.
            // Anything that needs to know "is a genuine locked door stopping this line?" must ask
            // IsDoorBlocked, not IsLockedDoor.
            public bool IsDoorBlocked;
        }

        public void Toggle()
        {
            var player = GetLocalPlayer();
            bool insideFactory = player != null && player.isInsideFactory;

            if (Plugin.AutoEnableOnEntry.Value && insideFactory && isActive)
            {
                isActive = false;
                Plugin.Logger.LogInfo("LeadMeOut: Temporarily disabled by hotkey.");
                ClearAll();
                return;
            }

            if (!insideFactory)
            {
                Plugin.Logger.LogInfo("LeadMeOut: Not inside facility, ignoring toggle.");
                return;
            }

            isActive = !isActive;
            Plugin.Logger.LogInfo($"LeadMeOut: {(isActive ? "ON" : "OFF")}");

            if (isActive)
            {
                smoothedPosInitialized = false;
                FindExits();
                if (Plugin.NavMode.Value == NavigationMode.CompassMode)
                    CreateCompassOverlay();
                else
                    CreateLineRoots();
            }
            else
            {
                ClearAll();
            }
        }

        public void Tick(float deltaTime)
        {
            var player = GetLocalPlayer();
            if (player != null)
            {
                bool insideNow = player.isInsideFactory;

                if (!wasInsideFactory && insideNow && Plugin.AutoEnableOnEntry.Value && !isActive)
                {
                    Plugin.Logger.LogInfo("LeadMeOut: Auto-enabling on facility entry.");
                    isActive = true;
                    smoothedPosInitialized = false;
                    FindExits();
                    if (Plugin.NavMode.Value == NavigationMode.CompassMode)
                        CreateCompassOverlay();
                    else
                        CreateLineRoots();
                }

                if (wasInsideFactory && !insideNow && isActive)
                {
                    Plugin.Logger.LogInfo("LeadMeOut: Player left facility, clearing.");
                    isActive = false;
                    ClearAll();
                }

                wasInsideFactory = insideNow;

                if (isActive)
                {
                    Vector3 actualPos = player.transform.position;
                    if (!smoothedPosInitialized)
                    {
                        smoothedPlayerPos = actualPos;
                        smoothedPosInitialized = true;
                    }
                    else
                    {
                        smoothedPlayerPos = Vector3.Lerp(smoothedPlayerPos, actualPos, deltaTime * smoothSpeed);
                    }
                }
            }

            // Detect mode change
            if (isActive && Plugin.NavMode.Value != lastNavMode)
            {
                lastNavMode = Plugin.NavMode.Value;
                ClearAll();
                FindExits();
                if (lastNavMode == NavigationMode.CompassMode)
                    CreateCompassOverlay();
                else
                    CreateLineRoots();
            }

            // ── Door graph maintenance ──────────────────────────────────
            // Deliberately ABOVE the isActive gate. The dungeon (and its DoorLocks and NavMesh)
            // exists from the moment the moon finishes generating, well before you set foot
            // inside — so the region graph can be built during the landing animation, when
            // nothing else is competing for frames. By the time you walk in, it's done and a
            // blocked path shows its padlock instantly instead of a second of search icon.
            //
            // This does NOT render anything. Every line, pip, and marker lives below the
            // isActive gate; this block only does reachability maths. Nothing can appear
            // outside the facility.
            MaintainDoorGraph(deltaTime);

            if (!isActive) return;

            pulseTimer += deltaTime;
            doorSimTimer += deltaTime;

            updateTimer += deltaTime;

            // Read the update rate live so LethalConfig changes apply without a restart. Clamped
            // defensively in case the config is hand-edited outside the accepted range.
            int hz = Mathf.Clamp(Plugin.PathUpdateRate.Value, 1, 10);
            float interval = 1f / hz;

            if (updateTimer >= interval)
            {
                updateTimer = 0f;
                Plugin.Logger.LogDebug($"LeadMeOut: Tick mode={Plugin.NavMode.Value}");
                if (Plugin.NavMode.Value == NavigationMode.CompassMode)
                    UpdateCompassOverlay();
                else
                    UpdatePaths();
            }
        }

        // Keeps the locked-door region graph current. Safe to call at any time, in any scene:
        // if there's no dungeon, there are no DoorLocks, and this does nothing.
        private void MaintainDoorGraph(float deltaTime)
        {
            // Periodically refresh the shared locked-door list
            doorCacheTimer += deltaTime;
            if (doorCacheTimer >= DOOR_CACHE_INTERVAL)
            {
                doorCacheTimer = 0f;
                cachedLockedDoors = null;
            }

            // No dungeon yet (orbit, main menu, or still generating) — nothing to do.
            // Also covers the level unloading: the DoorLocks vanish, the signature falls back
            // to its empty value, and the graph resets itself for the next moon.
            if (GetDoorCache().Count == 0)
            {
                if (doorEdges.Count > 0) ResetDoorGraph();
                graphLockSignature = 0;
                return;
            }

            // If a door was locked or unlocked, the region graph is stale — rebuild it.
            // Otherwise keep chipping away at any build already in progress.
            int lockSig = ComputeLockSignature();
            if (lockSig != graphLockSignature)
            {
                graphLockSignature = lockSig;
                BeginDoorGraph();
            }
            StepDoorGraph();
        }

        private GameNetcodeStuff.PlayerControllerB GetLocalPlayer()
        {
            var gnm = GameNetworkManager.Instance;
            if (gnm != null && gnm.localPlayerController != null)
                return gnm.localPlayerController;
            return null;
        }

        private void FindExits()
        {
            mainEntranceTarget = null;
            fireExitTargets.Clear();
            mineshaftBottomTarget = null;
            mineshaftTopTarget = null;
            realMainEntrance = null;

            // Look for the Mineshaft's BottomElevatorPanel.
            //
            // This is the ONLY thing that decides "are we in a Mineshaft?", so it has to be
            // strict. It used to accept any object whose name merely contained the string, and
            // fall back to a non-clone instance if no proper one was found. That misfired on
            // Offense — a moon that can roll EITHER Mineshaft or Mansion — where a stray match on
            // a Mansion level flipped the mod into Mineshaft mode. The main entrance then aimed at
            // a phantom elevator, GetPath failed, and the green line vanished entirely while the
            // (separately-targeted) red fire exit lines kept working. That's the exact "green line
            // missing on the Mansion interior" report.
            //
            // So: require a genuine spawned scene instance (parented under a "(Clone)"), and no
            // loose fallback. No panel, no Mineshaft mode.
            Transform bottomElevatorPanel = null;
            foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
            {
                if (!obj.name.Contains("BottomElevatorPanel")) continue;

                Transform parent = obj.transform.parent;
                if (parent != null && parent.name.Contains("(Clone)"))
                {
                    bottomElevatorPanel = obj.transform;
                    Plugin.Logger.LogInfo($"LeadMeOut: BottomElevatorPanel (scene instance) found at {obj.transform.position}, parent={parent.name}");
                    break;
                }
            }

            foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
            {
                if (obj.name.Contains("EntranceTeleportA") && obj.name.Contains("Clone"))
                {
                    // EntranceTeleportA is the real main entrance door, always — remember it so a
                    // failed Mineshaft path can fall back to it instead of drawing nothing.
                    realMainEntrance = obj.transform;

                    // Only treat this as a Mineshaft if the elevator panel is BOTH present AND
                    // plausible: reachable on the NavMesh, and genuinely below the top entrance.
                    // A phantom or mis-matched object fails one of these, and we fall through to
                    // treating EntranceTeleportA as an ordinary main entrance — which is exactly
                    // what every non-Mineshaft interior wants.
                    bool elevatorIsReal = false;
                    if (bottomElevatorPanel != null)
                    {
                        NavMeshHit navCheck;
                        bool onNavMesh = NavMesh.SamplePosition(bottomElevatorPanel.position, out navCheck, 4f, NavMesh.AllAreas);
                        bool below = bottomElevatorPanel.position.y < obj.transform.position.y - 4f;
                        elevatorIsReal = onNavMesh && below;

                        if (!elevatorIsReal)
                            Plugin.Logger.LogWarning(
                                $"LeadMeOut: BottomElevatorPanel present but rejected (onNavMesh={onNavMesh}, below={below}). " +
                                $"Treating as a normal (non-Mineshaft) interior.");
                    }

                    if (elevatorIsReal)
                    {
                        // Store both — we'll pick dynamically based on player Y each tick
                        mineshaftBottomTarget = bottomElevatorPanel;
                        mineshaftTopTarget = obj.transform;
                        mainEntranceTarget = bottomElevatorPanel; // default to bottom
                        Plugin.Logger.LogInfo($"LeadMeOut: Mineshaft mode — bottom={bottomElevatorPanel.position} top={obj.transform.position}");
                    }
                    else
                    {
                        mainEntranceTarget = obj.transform;
                        Plugin.Logger.LogInfo($"LeadMeOut: Main entrance at {obj.transform.position}");
                    }
                }
                if (obj.name.Contains("EntranceTeleportB") && obj.name.Contains("Clone"))
                {
                    fireExitTargets.Add(obj.transform);
                    Plugin.Logger.LogInfo($"LeadMeOut: Fire exit #{fireExitTargets.Count} at {obj.transform.position}");
                }
            }

            Plugin.Logger.LogInfo($"LeadMeOut: Found {fireExitTargets.Count} fire exit(s).");
        }

        // ── Compass Overlay Mode ──────────────────────────────────────────

        private void CreateCompassOverlay()
        {
            ClearCompassOverlay();

            // Find the game's compass parent
            GameObject hudObj = GameObject.Find(COMPASS_PATH);
            if (hudObj == null)
            {
                Plugin.Logger.LogInfo("LeadMeOut: Could not find HUD canvas path.");
                return;
            }

            // Create our overlay root, parented to the HUD
            compassOverlayRoot = new GameObject("LeadMeOut_CompassOverlay");
            compassOverlayRoot.transform.SetParent(hudObj.transform, false);

            compassOverlayRoot.AddComponent<CanvasRenderer>();
            RectTransform rootRt = compassOverlayRoot.AddComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0f);
            rootRt.anchorMax = new Vector2(0.5f, 0f);
            rootRt.anchoredPosition = new Vector2(0f, COMPASS_ANCHOR_Y + COMPASS_HEIGHT_UI + COMPASS_VERTICAL_OFFSET);
            rootRt.sizeDelta = new Vector2(COMPASS_WIDTH, COMPASS_HEIGHT_UI);

            // Place at end of sibling list so it renders on top
            compassOverlayRoot.transform.SetAsLastSibling();



            var showLines = Plugin.ShowLines.Value;

            // Main entrance pip (green)
            if (mainEntranceTarget != null && showLines != ShowLinesPreset.FireExitsOnly)
            {
                Color mainColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.MainEntranceColorPreset.Value, Plugin.MainEntranceCustomColor.Value, Color.green));
                float mainWidth = Plugin.ResolvePipWidth(Plugin.MainEntranceLineWidth.Value);
                mainEntrancePip = CreatePip(compassOverlayRoot, mainColor, mainWidth);
            }

            // Fire exit pip (red) - use first fire exit for compass
            if (fireExitTargets.Count > 0 && showLines != ShowLinesPreset.MainEntranceOnly)
            {
                Color fireColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.FireExitColorPreset.Value, Plugin.FireExitCustomColor.Value, Color.red));
                float fireWidth = Plugin.ResolvePipWidth(Plugin.FireExitLineWidth.Value);
                fireExitPip = CreatePip(compassOverlayRoot, fireColor, fireWidth);
            }

            // Log actual RectTransform info for debugging
            RectTransform rootCheck = compassOverlayRoot.GetComponent<RectTransform>();
            Plugin.Logger.LogInfo($"LeadMeOut: Compass overlay created. Parent={hudObj.name} anchorPos={rootCheck.anchoredPosition} size={rootCheck.sizeDelta} siblingIndex={compassOverlayRoot.transform.GetSiblingIndex()}");
        }

        private RectTransform CreatePip(GameObject parent, Color color, float width)
        {
            GameObject pipObj = new GameObject("Pip");
            pipObj.transform.SetParent(parent.transform, false);

            RectTransform rt = pipObj.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, 18f);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 0f);

            UnityEngine.UI.Image img = pipObj.AddComponent<UnityEngine.UI.Image>();
            img.color = color;

            Plugin.Logger.LogDebug($"LeadMeOut: Pip created as Image");

            return rt;
        }

        private void UpdateCompassOverlay()
        {
            if (compassOverlayRoot == null) return;

            var player = GetLocalPlayer();
            if (player == null) return;

            // Mineshaft: switch compass target based on player Y, same as UpdatePaths.
            // Reads go through TryGetPosition for the same reason — these Transforms can be
            // destroyed on a scene transition while this runner keeps ticking.
            Vector3 topPosC;
            if (mineshaftBottomTarget != null && mineshaftTopTarget != null
                && TryGetPosition(mineshaftTopTarget, out topPosC))
            {
                float playerY = player.transform.position.y;
                float topY = topPosC.y;
                mainEntranceTarget = (playerY >= topY - 8f) ? mineshaftTopTarget : mineshaftBottomTarget;
            }

            // Use camera container rotation for accurate look direction
            Transform camContainer = player.transform.Find("ScavengerModel/metarig/CameraContainer");
            float yRot = camContainer != null ? camContainer.eulerAngles.y : player.transform.eulerAngles.y;
            Vector3 forward = new Vector3(Mathf.Sin(yRot * Mathf.Deg2Rad), 0f, Mathf.Cos(yRot * Mathf.Deg2Rad));

            var showLines = Plugin.ShowLines.Value;
            Plugin.Logger.LogDebug($"LeadMeOut: UpdateCompass - forward={forward}, mainPip={mainEntrancePip != null}, firePip={fireExitPip != null}, mainTarget={mainEntranceTarget != null}, fireCount={fireExitTargets.Count}, showLines={Plugin.ShowLines.Value}");

            // Update main entrance pip
            Vector3 mainPipPos;
            if (mainEntrancePip != null && TryGetPosition(mainEntranceTarget, out mainPipPos))
            {
                mainEntrancePip.gameObject.SetActive(showLines != ShowLinesPreset.FireExitsOnly);
                if (showLines != ShowLinesPreset.FireExitsOnly)
                {
                    Color mainColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.MainEntranceColorPreset.Value, Plugin.MainEntranceCustomColor.Value, Color.green));
                    float mainWidth = Plugin.ResolvePipWidth(Plugin.MainEntranceLineWidth.Value);
                    mainEntrancePip.sizeDelta = new Vector2(mainWidth, mainEntrancePip.sizeDelta.y);
                    // Apply same lateral nudge as the line when targeting the elevator panel
                    Vector3 mainPipTarget = mainPipPos;
                    if (mainEntranceTarget == mineshaftBottomTarget)
                        mainPipTarget = ElevatorNudge(player.transform.position, mainPipTarget, 1.8f);
                    UpdatePipPosition(mainEntrancePip, player.transform.position, mainPipTarget, forward, mainColor);
                }
            }

            // Update fire exit pip — on mineshaft top floor, reroute to elevator
            if (fireExitPip != null && fireExitTargets.Count > 0)
            {
                fireExitPip.gameObject.SetActive(showLines != ShowLinesPreset.MainEntranceOnly);
                if (showLines != ShowLinesPreset.MainEntranceOnly)
                {
                    Color fireColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.FireExitColorPreset.Value, Plugin.FireExitCustomColor.Value, Color.red));
                    float fireWidth = Plugin.ResolvePipWidth(Plugin.FireExitLineWidth.Value);
                    fireExitPip.sizeDelta = new Vector2(fireWidth, fireExitPip.sizeDelta.y);
                    Vector3 topPosCF;
                    bool playerOnTopFloor = mineshaftBottomTarget != null
                        && TryGetPosition(mineshaftTopTarget, out topPosCF)
                        && player.transform.position.y >= topPosCF.y - 8f;

                    Vector3 firePipTarget;
                    Vector3 bottomElevPosC, fireExit0Pos;
                    if (playerOnTopFloor && TryGetPosition(mineshaftBottomTarget, out bottomElevPosC))
                    {
                        firePipTarget = ElevatorNudge(player.transform.position, bottomElevPosC, 2.2f);
                    }
                    else if (TryGetPosition(fireExitTargets[0], out fireExit0Pos))
                    {
                        firePipTarget = fireExit0Pos;
                    }
                    else
                    {
                        // Target gone — skip this frame's fire pip update
                        firePipTarget = player.transform.position;
                    }
                    UpdatePipPosition(fireExitPip, player.transform.position, firePipTarget, forward, fireColor);
                }
            }
        }

        private void UpdatePipPosition(RectTransform pip, Vector3 playerPos, Vector3 targetPos, Vector3 forward, Color color)
        {
            try
            {
                Vector3 toTarget = targetPos - playerPos;
                toTarget.y = 0f;
                if (toTarget.magnitude < 0.01f) return;
                toTarget.Normalize();

                float angle = Vector3.SignedAngle(forward, toTarget, Vector3.up);
                float halfFov = COMPASS_FOV * 0.5f;
                float absAngle = Mathf.Abs(angle);

                // Hide entirely when target is behind player past FOV + margin
                if (absAngle > halfFov + COMPASS_HIDE_MARGIN)
                {
                    pip.gameObject.SetActive(false);
                    return;
                }

                pip.gameObject.SetActive(true);

                float clampedAngle = Mathf.Clamp(angle, -halfFov, halfFov);
                float t = clampedAngle / halfFov;
                float xPos = t * (COMPASS_WIDTH * 0.5f);
                pip.anchoredPosition = new Vector2(xPos, 0f);

                // Fade alpha near edges
                float alpha = 1f;
                float normalized = absAngle / halfFov;
                float fadeStart = 1f - COMPASS_FADE_ZONE;
                if (normalized > fadeStart)
                    alpha = Mathf.Clamp01(Mathf.InverseLerp(1f, fadeStart, normalized));

                Plugin.Logger.LogDebug($"LeadMeOut: PipPos angle={angle:F1} xPos={xPos:F1} alpha={alpha:F2} active={pip.gameObject.activeSelf}");

                UnityEngine.UI.Image img = pip.GetComponentInChildren<UnityEngine.UI.Image>();
                if (img != null)
                {
                    Color c = color;
                    c.a = alpha;
                    img.color = c;
                }


            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogInfo($"LeadMeOut: PipPos exception: {e.Message}");
            }
        }

        private void ClearCompassOverlay()
        {
            if (compassOverlayRoot != null)
            {
                GameObject.Destroy(compassOverlayRoot);
                compassOverlayRoot = null;
            }
            mainEntrancePip = null;
            fireExitPip = null;
        }

        // ── Linear Mode ──────────────────────────────────────────────────

        private void CreateLineRoots()
        {
            ClearLineRoots();

            var showLines = Plugin.ShowLines.Value;

            if (mainEntranceTarget != null && showLines != ShowLinesPreset.FireExitsOnly)
            {
                mainEntranceRoot = new GameObject("LeadMeOut_MainLine");
                GameObject.DontDestroyOnLoad(mainEntranceRoot);
            }

            if (showLines != ShowLinesPreset.MainEntranceOnly)
            {
                for (int i = 0; i < fireExitTargets.Count; i++)
                {
                    GameObject root = new GameObject($"LeadMeOut_FireLine_{i}");
                    GameObject.DontDestroyOnLoad(root);
                    fireExitRoots.Add(root);
                    fireExitSegmentSets.Add(new List<LineRenderer>());
                    fireExitDiamondSets.Add(new List<LineRenderer>());
                    fireExitShapeSets.Add(new List<GameObject>());
                }
            }

            updateTimer = 999f;
        }

        // Safely read a Transform's position. Returns false if the transform is null OR has been
        // destroyed by Unity (its managed wrapper can outlive the native object, in which case a
        // plain field access still throws on .position). Every per-tick read of a cached exit
        // Transform goes through here, because those objects are destroyed on scene transitions
        // while this runner keeps ticking with stale references for a frame.
        private static bool TryGetPosition(Transform t, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (t == null) return false;      // Unity's overloaded == also catches destroyed objects
            try
            {
                pos = t.position;
                return true;
            }
            catch (MissingReferenceException) { return false; }
            catch (System.NullReferenceException) { return false; }
        }

        private void UpdatePaths()
        {
            if (!smoothedPosInitialized) return;

            // Mineshaft: switch target based on player Y relative to the top exit door.
            //
            // Reads go through TryGetPosition because these Transforms can be DESTROYED on a scene
            // transition while this runner — which survives scene loads (DontDestroyOnLoad) — keeps
            // ticking with stale references for a frame or two before FindExits re-runs. A
            // destroyed Transform's managed wrapper can survive and throw on .position. This was
            // throwing a NullReferenceException every tick on Mineshaft levels (220 in one run).
            Vector3 topPos;
            if (mineshaftBottomTarget != null && mineshaftTopTarget != null
                && TryGetPosition(mineshaftTopTarget, out topPos))
            {
                float playerY = smoothedPlayerPos.y;
                float topY = topPos.y;
                // If player is within 8 units below the top exit, route to the door; otherwise route to elevator
                mainEntranceTarget = (playerY >= topY - 8f) ? mineshaftTopTarget : mineshaftBottomTarget;
            }

            // If the chosen target is gone, don't touch it — hide the main line and wait for
            // FindExits to re-establish valid targets on the next level. Fire exits draw below
            // from their own list and are unaffected.
            Vector3 mainTargetPos;
            if (!TryGetPosition(mainEntranceTarget, out mainTargetPos))
            {
                if (mainEntranceRoot != null) mainEntranceRoot.SetActive(false);
                mainEntranceTarget = null;
            }

            // Nudge only the TARGET POINT for mineshaft elevator, not the whole path.
            // mainTargetPos was already fetched safely above (Vector3.zero if the target is gone).
            bool mainTargetIsElevator = mineshaftBottomTarget != null && mainEntranceTarget == mineshaftBottomTarget;
            if (mainTargetIsElevator)
                mainTargetPos = ElevatorNudge(smoothedPlayerPos, mainTargetPos, 1.8f);

            var showLines = Plugin.ShowLines.Value;

            Color mainColor = Plugin.ResolveColor(Plugin.MainEntranceColorPreset.Value, Plugin.MainEntranceCustomColor.Value, Color.green);
            LineStyle mainStyle = Plugin.MainEntranceLineStyle.Value;
            float mainWidth = Plugin.ResolveWidth(Plugin.MainEntranceLineWidth.Value);

            Color fireColor = Plugin.ResolveColor(Plugin.FireExitColorPreset.Value, Plugin.FireExitCustomColor.Value, Color.red);
            LineStyle fireStyle = Plugin.FireExitLineStyle.Value;
            float fireWidth = Plugin.ResolveWidth(Plugin.FireExitLineWidth.Value);

            float lateralOffset = 0.18f;

            if (mainEntranceRoot != null && mainEntranceTarget != null && showLines != ShowLinesPreset.FireExitsOnly)
            {
                mainEntranceRoot.SetActive(true);
                // Cache key = the un-nudged exit position, so the door sim cache stays stable.
                // Fetch it safely; if the target vanished this tick, fall back to mainTargetPos.
                Vector3 mainCacheKey;
                if (!TryGetPosition(mainEntranceTarget, out mainCacheKey)) mainCacheKey = mainTargetPos;
                PathResult? result = GetPath(smoothedPlayerPos, mainTargetPos, mainCacheKey);

                // Safety net: if we're in Mineshaft mode and the elevator path produced nothing,
                // fall back to the real entrance door so the green line still draws. A wrong-but-
                // present line beats a missing one, and this also covers any future case where
                // mineshaft detection misfires.
                if (!result.HasValue && mineshaftBottomTarget != null && realMainEntrance != null
                    && mainEntranceTarget != realMainEntrance)
                {
                    Plugin.Logger.LogWarning("LeadMeOut: mineshaft main path failed — falling back to the real entrance door.");
                    mainTargetPos = realMainEntrance.position;
                    mainTargetIsElevator = false;
                    result = GetPath(smoothedPlayerPos, mainTargetPos, realMainEntrance.position);
                }

                if (result.HasValue)
                {
                    // Does this line end at the elevator? Then the endpoint is an ESCAPE, not a
                    // blocker: elevator icon, no pulse, no dimming.
                    //
                    // Checked independently of IsDeadEnd and of which floor we think you're on.
                    // From the bottom floor the path COMPLETES at the elevator panel
                    // (IsDeadEnd == false), so the icon has to be able to appear on a perfectly
                    // successful path too.
                    // mainTargetIsElevator: only true when the main line is actually routing to
                    // the elevator (player is BELOW). On the top floor it heads for the real exit
                    // door, which happens to sit near the shaft — hence the intent check.
                    bool mainAtElevator = LineEndsAtElevator(result.Value, mainTargetIsElevator);

                    RenderPath(mainEntranceRoot, mainEntranceSegments, mainEntranceDiamonds, mainEntranceShapes,
                        result.Value.Points, mainColor, mainStyle, mainWidth, 0f, 0f,
                        mainTargetPos, result.Value.IsLockedDoor && !mainAtElevator);

                    float b = (result.Value.IsDeadEnd && !mainAtElevator)
                        ? Mathf.Lerp(0.3f, 1f, Mathf.Sin(pulseTimer * 4f) * 0.5f + 0.5f) : 1f;
                    mainEntranceMarker = UpdateDeadEndMarker(mainEntranceMarker, mainEntranceRoot,
                        result.Value.IsDeadEnd || mainAtElevator, result.Value.Points, mainColor, b,
                        mainAtElevator);
                }
                else
                {
                    HideSegments(mainEntranceSegments);
                    HideShapes(mainEntranceShapes);
                    if (mainEntranceMarker != null) mainEntranceMarker.SetActive(false);
                }
            }
            else if (mainEntranceRoot != null)
                mainEntranceRoot.SetActive(false);

            if (showLines != ShowLinesPreset.MainEntranceOnly)
            {
                Vector3 topPosF;
                bool playerOnTopFloor = mineshaftBottomTarget != null
                    && TryGetPosition(mineshaftTopTarget, out topPosF)
                    && smoothedPlayerPos.y >= topPosF.y - 8f;

                // If we're routing fire exits to the elevator, we need its position safely.
                // If it can't be read, drop out of top-floor mode rather than throw.
                Vector3 bottomElevPos = Vector3.zero;
                if (playerOnTopFloor && !TryGetPosition(mineshaftBottomTarget, out bottomElevPos))
                    playerOnTopFloor = false;

                // Track marker endpoints so duplicates at the same locked door are suppressed
                var shownMarkerPositions = new List<Vector3>();

                for (int i = 0; i < fireExitTargets.Count; i++)
                {
                    if (i >= fireExitRoots.Count) break;

                    // Skip a fire-exit target that's been destroyed out from under us (scene
                    // transition). Its position read would throw; wait for FindExits to refresh.
                    Vector3 fireExitPos;
                    bool haveFireExitPos = TryGetPosition(fireExitTargets[i], out fireExitPos);
                    if (!haveFireExitPos && !playerOnTopFloor)
                    {
                        if (fireExitRoots[i] != null) fireExitRoots[i].SetActive(false);
                        continue;
                    }

                    // Ensure a marker slot exists for this exit
                    while (fireExitMarkers.Count <= i) fireExitMarkers.Add(null);

                    fireExitRoots[i].SetActive(true);
                    float exitLateralOffset = lateralOffset * (i + 1);
                    Color exitColor = i == 0 ? fireColor : DarkenColor(fireColor, 0.15f * i);

                    // On mineshaft top floor, route to elevator instead of unreachable lower exit
                    Vector3 fireTarget = playerOnTopFloor
                        ? ElevatorNudge(smoothedPlayerPos, bottomElevPos, 2.2f)
                        : fireExitPos;

                    // Cache key = the un-nudged exit position, so the door sim cache stays stable
                    Vector3 fireSimKey = playerOnTopFloor
                        ? bottomElevPos
                        : fireExitPos;

                    PathResult? result = GetPath(smoothedPlayerPos, fireTarget, fireSimKey);
                    if (result.HasValue)
                    {
                        // On the top floor the fire exit line routes down to the elevator. It
                        // can't descend the shaft, so it stops at the doors up top — which IS
                        // the elevator, just at the other end of it. Once you ride down, the
                        // line retargets the real fire exit and this stops matching on its own,
                        // so normal dead-end behaviour resumes with no special-casing.
                        // playerOnTopFloor: the fire exit line only routes to the elevator from
                        // the top floor. Once you ride down it targets the real fire exit and can
                        // no longer claim the icon.
                        bool fireAtElevator = LineEndsAtElevator(result.Value, playerOnTopFloor);

                        RenderPath(fireExitRoots[i], fireExitSegmentSets[i], fireExitDiamondSets[i], fireExitShapeSets[i],
                            result.Value.Points, exitColor, fireStyle, fireWidth, exitLateralOffset, 0f,
                            fireTarget, result.Value.IsLockedDoor && !fireAtElevator);

                        // Only show one marker per distinct endpoint — if several fire exit
                        // paths all stop at the same locked door (or the same elevator), don't
                        // stack markers there.
                        bool showMarker = result.Value.IsDeadEnd || fireAtElevator;
                        if (showMarker)
                        {
                            Vector3 endPt = result.Value.Points[result.Value.Points.Count - 1];
                            foreach (var p in shownMarkerPositions)
                            {
                                if (Vector3.Distance(p, endPt) < 2f) { showMarker = false; break; }
                            }
                            if (showMarker) shownMarkerPositions.Add(endPt);
                        }

                        float b = (result.Value.IsDeadEnd && !fireAtElevator)
                            ? Mathf.Lerp(0.3f, 1f, Mathf.Sin(pulseTimer * 4f) * 0.5f + 0.5f) : 1f;
                        fireExitMarkers[i] = UpdateDeadEndMarker(fireExitMarkers[i], fireExitRoots[i],
                            showMarker, result.Value.Points, exitColor, b,
                            fireAtElevator, exitLateralOffset);
                    }
                    else
                    {
                        HideSegments(fireExitSegmentSets[i]);
                        HideShapes(fireExitShapeSets[i]);
                        if (i < fireExitMarkers.Count && fireExitMarkers[i] != null)
                            fireExitMarkers[i].SetActive(false);
                    }
                }
            }
            else
            {
                foreach (var root in fireExitRoots)
                    if (root != null) root.SetActive(false);
            }
        }

        private Color DarkenColor(Color c, float amount)
        {
            bool isBlack = c.r < 0.05f && c.g < 0.05f && c.b < 0.05f;
            if (isBlack)
                return new Color(Mathf.Min(1f, c.r + amount), Mathf.Min(1f, c.g + amount), Mathf.Min(1f, c.b + amount), c.a);
            return new Color(Mathf.Max(0f, c.r - amount), Mathf.Max(0f, c.g - amount), Mathf.Max(0f, c.b - amount), c.a);
        }

        // Walks the path and finds the first point where it passes through a solid wall.
        // Only tests against the "Room" layer (walls and floors) — doorframes, railings,
        // and other metal framing live on "MiscLevelGeometry" and must NOT block the path.
        private bool PathHitsWall(List<Vector3> corners, out Vector3 blockPoint, out int blockIndex)
        {
            blockPoint = Vector3.zero;
            blockIndex = -1;

            int roomLayer = LayerMask.NameToLayer("Room");
            if (roomLayer < 0) return false;
            int mask = 1 << roomLayer;

            // Test at multiple heights — a real wall blocks all of them; a doorway won't.
            float[] heights = { 0.4f, 1.0f, 1.6f };

            for (int i = 1; i < corners.Count; i++)
            {
                Vector3 a = corners[i - 1];
                Vector3 b = corners[i];

                Vector3 flat = b - a;
                flat.y = 0f;
                float dist = flat.magnitude;
                if (dist < 0.05f) continue;

                int blockedCount = 0;
                RaycastHit firstHit = default;

                foreach (float h in heights)
                {
                    Vector3 ra = a + Vector3.up * h;
                    Vector3 rb = b + Vector3.up * h;
                    Vector3 dir = (rb - ra).normalized;
                    float d = Vector3.Distance(ra, rb);

                    RaycastHit hit;
                    if (Physics.Raycast(ra, dir, out hit, d, mask, QueryTriggerInteraction.Ignore))
                    {
                        if (blockedCount == 0) firstHit = hit;
                        blockedCount++;
                    }
                }

                // Only treat it as a wall if EVERY height is blocked. A doorway will have
                // at least one clear height; a solid wall blocks them all.
                if (blockedCount == heights.Length)
                {
                    Vector3 dir = (b - a).normalized;
                    blockPoint = firstHit.point - dir * 0.4f;
                    blockPoint.y = a.y;
                    blockIndex = i;
                    return true;
                }
            }
            return false;
        }

        // doorSimKey: a STABLE identifier for this destination, used only as the door-sim
        // cache key. On Mineshaft the actual "to" point is nudged relative to the player and
        // therefore changes every tick, which would make the cache miss every single time.
        // Callers pass the un-nudged exit position here so the cache actually holds.
        private PathResult? GetPath(Vector3 from, Vector3 to, Vector3? doorSimKey = null)
        {
            NavMeshHit fromHit, toHit;
            bool fromValid = NavMesh.SamplePosition(from, out fromHit, 15f, NavMesh.AllAreas);

            // Sample the target tightly first so we lock onto the exit's OWN floor,
            // not a floor above/below it. Widen only if the tight sample fails.
            bool toValid = NavMesh.SamplePosition(to, out toHit, 2f, NavMesh.AllAreas);
            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 5f, NavMesh.AllAreas);
            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 15f, NavMesh.AllAreas);
            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 30f, NavMesh.AllAreas);
            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 50f, NavMesh.AllAreas);

            if (!fromValid || !toValid) return null;

            // Try to build a full path, bridging small NavMesh gaps on the same floor
            List<Vector3> allCorners = new List<Vector3>();
            allCorners.Add(from);

            Vector3 currentFrom = fromHit.position;
            bool isLockedDoor = false;
            bool bridged = false;
            bool isDeadEnd = false;
            bool isDoorBlocked = false;   // set ONLY when a real DoorLock is identified

            const int maxHops = 3;
            const float hopRadius = 2f;      // search radius per hop (units)
            const float yTolerance = 1.5f;   // max Y difference to stay on same floor
            const int sampleAngles = 12;      // directions to probe around the break point

            for (int hop = 0; hop < maxHops; hop++)
            {
                NavMeshPath path = scratchPath;
                NavMesh.CalculatePath(currentFrom, toHit.position, NavMesh.AllAreas, path);

                if (path.status == NavMeshPathStatus.PathComplete && path.corners.Length >= 2)
                {
                    // Full path found — add remaining corners and done
                    foreach (var c in path.corners)
                        allCorners.Add(c);
                    bridged = true;
                    break;
                }

                if (path.corners.Length < 2)
                {
                    // Unity found NO route at all — the door sits on a NavMesh island genuinely
                    // disconnected from the player's island (confirmed visually: a real gap
                    // between the mesh and the door frame, not just a small seam).
                    //
                    // The earlier fix here tried to hop toward wherever the player currently stood
                    // each tick, which never actually closed the gap — it just kept drawing a
                    // fresh 2-4 unit stub near the player that appeared to "follow" them and never
                    // built toward the door. That's fixed by doing ONE validated search instead of
                    // a per-tick guess: sample points near the RAW door position at increasing
                    // radius, and for each candidate confirm a real path exists from the player
                    // before accepting it. This finds the closest point on the PLAYER'S OWN island
                    // to the door — a fixed, correct destination — rather than probing blindly.
                    if (hop == 0)
                    {
                        Vector3 nearestReachable;
                        if (FindNearestReachablePoint(fromHit.position, to, out nearestReachable))
                        {
                            NavMeshPath toNear = scratchPathDoor;
                            NavMesh.CalculatePath(fromHit.position, nearestReachable, NavMesh.AllAreas, toNear);
                            if (toNear.status == NavMeshPathStatus.PathComplete && toNear.corners.Length >= 2)
                            {
                                foreach (var c in toNear.corners)
                                    allCorners.Add(c);
                                bridged = true;
                                isDeadEnd = true; // stops short of the actual door — flag as such
                                Plugin.Logger.LogDebug($"LeadMeOut: reached nearest point on player's island: {nearestReachable} (door unreachable — separate NavMesh island).");
                                break;
                            }
                        }
                    }

                    isLockedDoor = true;
                    break;
                }

                // A partial path whose endpoint already sits essentially AT the exit is good
                // enough — accept it as-is. This is the staircase-entrance case: the door anchor
                // sits a hair past the NavMesh edge, so the path comes back "partial" even though
                // it climbs the stairs correctly and stops inches from the door. Follow that
                // routed line and finish, rather than treating it as a dead end.
                Vector3 partialEnd = path.corners[path.corners.Length - 1];
                float gapToExit = Vector3.Distance(partialEnd, toHit.position);
                if (gapToExit <= 1.5f)
                {
                    foreach (var c in path.corners)
                        allCorners.Add(c);
                    bridged = true;
                    break;
                }

                // Partial path — check whether it makes progress toward the exit.
                // Measured against the SNAPPED target (toHit.position) — the same point the path
                // above was calculated toward. Using the raw target here caused off-mesh exits
                // (a main entrance atop a staircase) to look like they were heading "backward" on
                // the very first hop, so GetPath bailed and the line never drew at all.
                Vector3 exitRef = toHit.position;
                Vector3 breakPoint = path.corners[path.corners.Length - 1];
                float playerDist = Vector3.Distance(currentFrom, exitRef);
                float breakDist = Vector3.Distance(breakPoint, exitRef);

                // Only reject if the path heads SIGNIFICANTLY away from the exit.
                // A small backward drift (e.g. rounding a corner) is fine — we keep the
                // partial line as long as it's not clearly leading the wrong way.
                // "Significantly backward" = ending up more than 3 units further from
                // the exit than we started this hop.
                if (breakDist > playerDist + 3f)
                {
                    // Clearly wrong direction — stop following, but keep whatever forward
                    // progress we already have. On hop 0 we have none yet, so this leaves the
                    // corner list short; the end-of-method logic then decides how to finish.
                    isLockedDoor = true;
                    break;
                }

                // Add corners up to the break point
                foreach (var c in path.corners)
                    allCorners.Add(c);

                // If the break point didn't actually get us closer, don't bother
                // trying to bridge — just show what we have and pulse it
                if (breakDist >= playerDist)
                {
                    isLockedDoor = true;
                    break;
                }

                // Try to bridge the gap — probe outward on the same floor level
                Vector3 bestCandidate = Vector3.zero;
                float bestCandidateDist = breakDist;
                bool foundCandidate = false;

                for (int a = 0; a < sampleAngles; a++)
                {
                    float angle = a * (360f / sampleAngles) * Mathf.Deg2Rad;
                    Vector3 probe = breakPoint + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * hopRadius;

                    NavMeshHit sampleHit;
                    if (!NavMesh.SamplePosition(probe, out sampleHit, hopRadius, NavMesh.AllAreas))
                        continue;

                    // Stay on same floor — reject samples too far above or below break point
                    if (Mathf.Abs(sampleHit.position.y - breakPoint.y) > yTolerance)
                        continue;

                    // Only consider candidates that are closer to the exit than the break point
                    float candidateDist = Vector3.Distance(sampleHit.position, exitRef);
                    if (candidateDist >= bestCandidateDist) continue;

                    // NEVER hop THROUGH a locked door.
                    //
                    // A locked door carves a thin strip out of the NavMesh — indistinguishable
                    // from the seam this code exists to cross. Left unguarded, a probe lands on
                    // the far side and the line walks straight through a locked door.
                    Vector3 doorHit;
                    if (SegmentCrossesLockedDoor(breakPoint, sampleHit.position, out doorHit))
                    {
                        Plugin.Logger.LogDebug(
                            $"LeadMeOut: hop rejected — would cross a locked door at {doorHit}");
                        continue;
                    }

                    // NEVER hop THROUGH a wall.
                    //
                    // The door check above is not enough on its own: the hop doesn't have to
                    // cross the door to cheat. It can go through the wall BESIDE the door and
                    // land in the same room, which is exactly what was happening — lines cutting
                    // through the wall to the left or right of a locked doorway to reach the exit
                    // beyond it. Bridging is for NavMesh seams, not for walking through walls.
                    if (!HopCrossesSeamOnly(breakPoint, sampleHit.position))
                    {
                        Plugin.Logger.LogDebug(
                            $"LeadMeOut: hop rejected — gap to {sampleHit.position} is too wide to be a NavMesh seam (wall).");
                        continue;
                    }

                    bestCandidateDist = candidateDist;
                    bestCandidate = sampleHit.position;
                    foundCandidate = true;
                }

                if (!foundCandidate)
                {
                    // No viable bridge found — partial path is as far as we get.
                    // This is a dead-end: line stopped with no clear continuation.
                    isLockedDoor = true;
                    isDeadEnd = true;
                    break;
                }

                // Jump to the candidate and try again from there
                allCorners.Add(bestCandidate);
                currentFrom = bestCandidate;
            }

            if (!bridged && !isDeadEnd)
            {
                // Ran out of hops without bridging or explicitly dead-ending
                isLockedDoor = true;
                isDeadEnd = true;
            }

            // If the path dead-ended, try to find WHICH locked door would actually unlock
            // the route. If we find one, reroute the line to that door instead of the
            // arbitrary dead-end point.
            if (isDeadEnd)
            {
                // Use the SNAPPED exit position (toHit.position), not the raw target. For an exit
                // that sits off the NavMesh — a main entrance at the top of a staircase, where the
                // mesh is patchy — the raw point can be several units from any walkable region, so
                // the door sim's region check reports "exit is in no region" and gives up, and the
                // green line never draws. The snapped point is the same place the pathfinder itself
                // aimed at, so the two halves finally agree.
                Vector3 exitForSim = toHit.position;

                Plugin.Logger.LogDebug($"LeadMeOut: PATH DEAD-END — attempting door simulation for target {exitForSim}");
                Vector3 blockingDoor;
                if (FindBlockingDoorCached(from, exitForSim, doorSimKey ?? exitForSim, out blockingDoor))
                {
                    // Path to the blocking door — this should be reachable
                    NavMeshPath toDoor = scratchPathDoor;
                    NavMeshHit doorHit;
                    if (NavMesh.SamplePosition(blockingDoor, out doorHit, 3f, NavMesh.AllAreas) &&
                        NavMesh.CalculatePath(fromHit.position, doorHit.position, NavMesh.AllAreas, toDoor) &&
                        toDoor.corners.Length >= 2)
                    {
                        allCorners = new List<Vector3>(toDoor.corners);
                        allCorners[0] = from;
                        // Extend to the door itself so the lock icon lands on it
                        allCorners.Add(blockingDoor);
                        isLockedDoor = true;
                        isDeadEnd = true;
                        isDoorBlocked = true;   // a real DoorLock, identified by the region graph
                    }
                }
            }

            // Need at least a start and one more point
            if (allCorners.Count < 2) return null;

            // Remove duplicate first point (fromHit vs from)
            allCorners[0] = from;

            // NOTE: Wall validation via Physics.Raycast is disabled. Even restricted to the
            // "Room" layer it false-positives on security doors (BigDoor) and other large
            // structural geometry, truncating valid paths. NavMesh remains the authority.

            // Check if the path crosses a locked door (some locked doors don't carve
            // the NavMesh, so the path completes right through them). If so, truncate
            // the path at the door and flag it as blocked.
            Vector3 lockedDoorPoint;
            if (!isLockedDoor && PathCrossesLockedDoor(allCorners, out lockedDoorPoint))
            {
                // Find the corner index where the path is closest to the door, then
                // truncate there so the line ends at the door.
                int closestIdx = 0;
                float closestDist = float.MaxValue;
                for (int i = 0; i < allCorners.Count; i++)
                {
                    float d = Vector3.Distance(allCorners[i], lockedDoorPoint);
                    if (d < closestDist) { closestDist = d; closestIdx = i; }
                }

                var truncated = new List<Vector3>();
                for (int i = 0; i <= closestIdx; i++)
                    truncated.Add(allCorners[i]);

                // Ensure the line visually reaches the door
                if (truncated.Count > 0 &&
                    Vector3.Distance(truncated[truncated.Count - 1], lockedDoorPoint) > 0.5f)
                    truncated.Add(lockedDoorPoint);

                if (truncated.Count >= 2)
                {
                    allCorners = truncated;
                    isLockedDoor = true;
                    isDeadEnd = true; // triggers marker logic, which shows the lock icon
                    isDoorBlocked = true;   // a real DoorLock the path ran straight through
                }
            }

            // Path post-processing, in order:
            //   1. CenterCorners      — nudge corners toward the middle of the corridor
            //   2. SnapThroughDoorways — force a straight, centred run through each opening
            //   3. SmoothPath          — round the remaining corners, leaving pinned points alone
            Vector3[] centered = CenterCorners(allCorners.ToArray());

            HashSet<int> pinnedDoorway;
            List<Vector3> throughDoors = SnapThroughDoorways(new List<Vector3>(centered), out pinnedDoorway);

            List<Vector3> smoothed = SmoothPath(throughDoors, pinnedDoorway);

            for (int i = 0; i < smoothed.Count; i++)
                smoothed[i] = smoothed[i] + Vector3.up * 0.1f;

            float maxDist = Plugin.ResolveRenderDistance(Plugin.RenderDistance.Value);
            if (maxDist < float.MaxValue && !isLockedDoor)
                smoothed = CullByDistance(smoothed, from, maxDist);

            if (smoothed.Count < 2) return null;

            return new PathResult { Points = smoothed, IsLockedDoor = isLockedDoor, IsDeadEnd = isDeadEnd, IsDoorBlocked = isDoorBlocked };
        }

        // ── Locked-door region graph ─────────────────────────────────────
        //
        // Locked doors chop the NavMesh into disconnected REGIONS. Each locked door is an
        // edge joining exactly two regions. To find the door you actually need to open, we
        // build that graph and breadth-first search from the player's region to the exit's
        // region, then point at the FIRST door along the chain.
        //
        // The previous approach scored each door on its own merit: "if I opened THIS door and
        // stepped through, how much closer to the exit could I get?" That collapses the moment
        // doors are chained — the far side of a gating door is a pocket sealed by the NEXT
        // locked door, so every candidate scores WORSE than standing still and all of them get
        // rejected. BFS has no such blind spot: it doesn't care whether the first door helps on
        // its own, only whether it's the first step on a chain that eventually reaches the exit.
        //
        // Building the graph costs a lot of reachability tests, and FAILED pathfinds are the
        // expensive kind, so the build is spread across frames on a fixed per-frame budget
        // (StepDoorGraph) and cached until a door's lock state actually changes.

        private class DoorEdge
        {
            public DoorLock door;
            public Vector3 center;
            public Vector3 sideA, sideB;
            public int regionA = -1;
            public int regionB = -1;
        }

        private class SidePoint
        {
            public Vector3 pos;
            public int region = -1;
            public DoorEdge owner;
            public bool isSideA;
        }

        private List<DoorEdge> doorEdges = new List<DoorEdge>();
        private List<SidePoint> graphPoints = new List<SidePoint>();
        private List<Vector3> regionReps = new List<Vector3>();

        private bool graphReady = false;
        private bool graphBuilding = false;
        private int graphPointIndex = 0;
        private int graphRepIndex = 0;
        private int graphLockSignature = 0;

        private const int GRAPH_PATHFINDS_PER_TICK = 6;  // per-frame budget while building
        private const int GRAPH_MAX_DOORS = 64;          // sanity cap for pathological maps

        // How close (HORIZONTALLY) a line's endpoint has to be to the elevator shaft before we
        // call it "at the elevator" and swap the dead-end marker for the elevator icon.
        //
        // The test is XZ-only, and that's the whole trick. The elevator is a vertical shaft, so
        // its top access and its bottom panel sit at nearly the same X/Z but wildly different Y.
        // From the BOTTOM floor the line reaches the panel and the path completes. From the TOP
        // floor the line can't descend the shaft, so it dead-ends at the doors up top — tens of
        // units above the panel it was aiming at. A 3D distance test fails that second case
        // badly, but ignore Y and both endpoints land right on the shaft.
        //
        // Generous on purpose, for two reasons. The bottom panel sits beside the shaft rather
        // than dead-centre in it, so the top doorway and the bottom panel aren't at identical
        // X/Z. And the NavMesh stops short of the elevator's physical geometry, so the line
        // visibly ends a few units before the doors. If the icon still misses, the debug line in
        // LineEndsAtElevator now prints the measured distance — tune this against that number.
        private const float ELEVATOR_XZ_RADIUS = 10f;

        // Cheap fingerprint of which doors are currently locked. When it changes — a door was
        // opened, or a new level loaded — the region graph is stale and gets rebuilt.
        private int ComputeLockSignature()
        {
            // Order-independent on purpose. The previous version folded instance IDs in sequence
            // (sig = sig*31 + id), so the result depended on the order GetDoorCache happened to
            // return doors in — and that order isn't guaranteed stable when the cache rebuilds
            // every few seconds. An order change with no actual lock change would shift the
            // signature, throw away a perfectly good region graph, and trigger a full rebuild:
            // a periodic hitch for no reason. XOR-accumulating a per-door hash removes the order
            // dependence, so the signature changes only when a door genuinely locks or unlocks.
            int sig = 0;
            foreach (var entry in GetDoorCache())
            {
                if (entry.door == null) continue;
                if (!entry.door.isLocked) continue;

                // Mix the id so nearby ids don't produce nearby hashes, then XOR (commutative)
                int h = entry.door.GetInstanceID();
                h ^= (h >> 16);
                h *= unchecked((int)0x45d9f3b);
                h ^= (h >> 16);
                sig ^= h;
            }
            return sig;
        }

        private void ResetDoorGraph()
        {
            doorEdges.Clear();
            graphPoints.Clear();
            regionReps.Clear();
            graphReady = false;
            graphBuilding = false;
            graphPointIndex = 0;
            graphRepIndex = 0;
        }

        // Phase 1 — collect locked doors and sample a NavMesh point either side of each.
        // Cheap: NavMesh.SamplePosition is a local lookup, not a search.
        private void BeginDoorGraph()
        {
            ResetDoorGraph();

            foreach (var entry in GetDoorCache())
            {
                if (doorEdges.Count >= GRAPH_MAX_DOORS) break;
                if (entry.door == null || entry.col == null) continue;
                if (!entry.door.isLocked) continue;

                Vector3 doorCenter = entry.col.bounds.center;
                Vector3 e = entry.col.bounds.extents;

                // The slab is thin along its through-axis; step out to either side of it
                Vector3 through = (e.x < e.z) ? Vector3.right : Vector3.forward;
                float slabHalf = Mathf.Min(e.x, e.z);

                NavMeshHit hitA, hitB;
                if (!NavMesh.SamplePosition(doorCenter + through * (slabHalf + 1.2f), out hitA, 2f, NavMesh.AllAreas)) continue;
                if (!NavMesh.SamplePosition(doorCenter - through * (slabHalf + 1.2f), out hitB, 2f, NavMesh.AllAreas)) continue;

                DoorEdge edge = new DoorEdge
                {
                    door = entry.door,
                    center = doorCenter,
                    sideA = hitA.position,
                    sideB = hitB.position
                };
                doorEdges.Add(edge);

                graphPoints.Add(new SidePoint { pos = edge.sideA, owner = edge, isSideA = true });
                graphPoints.Add(new SidePoint { pos = edge.sideB, owner = edge, isSideA = false });
            }

            graphBuilding = doorEdges.Count > 0;
            graphReady = doorEdges.Count == 0; // nothing locked — trivially "solved" and empty

            Plugin.Logger.LogDebug($"LeadMeOut: DoorGraph — build started: {doorEdges.Count} locked door(s), {graphPoints.Count} side point(s).");
        }

        // Phase 2 — assign each side point to a region, spending at most
        // GRAPH_PATHFINDS_PER_TICK reachability tests per frame. Driven from Tick().
        private void StepDoorGraph()
        {
            if (!graphBuilding || graphReady) return;

            int budget = GRAPH_PATHFINDS_PER_TICK;

            while (budget > 0 && graphPointIndex < graphPoints.Count)
            {
                SidePoint gp = graphPoints[graphPointIndex];

                if (gp.region >= 0)
                {
                    graphPointIndex++;
                    graphRepIndex = 0;
                    continue;
                }

                if (graphRepIndex < regionReps.Count)
                {
                    bool connected = IsPathComplete(gp.pos, regionReps[graphRepIndex]);
                    budget--;

                    if (connected)
                    {
                        gp.region = graphRepIndex;
                        graphPointIndex++;
                        graphRepIndex = 0;
                    }
                    else graphRepIndex++;
                }
                else
                {
                    // Connects to no known region — it starts a new one
                    gp.region = regionReps.Count;
                    regionReps.Add(gp.pos);
                    graphPointIndex++;
                    graphRepIndex = 0;
                }
            }

            if (graphPointIndex >= graphPoints.Count)
            {
                // Write the region assignments back onto the edges
                foreach (var gp in graphPoints)
                {
                    if (gp.isSideA) gp.owner.regionA = gp.region;
                    else gp.owner.regionB = gp.region;
                }

                graphReady = true;
                graphBuilding = false;

                // A freshly built graph invalidates any cached answers
                doorSimCache.Clear();
                doorSimMisses.Clear();

                Plugin.Logger.LogInfo($"LeadMeOut: DoorGraph — built: {doorEdges.Count} locked door(s) across {regionReps.Count} region(s).");
            }
        }

        // Which region does an arbitrary world point sit in? -1 if it connects to none.
        private int RegionOf(Vector3 p)
        {
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(p, out hit, 5f, NavMesh.AllAreas)) return -1;

            for (int r = 0; r < regionReps.Count; r++)
                if (IsPathComplete(hit.position, regionReps[r])) return r;

            return -1;
        }

        // Throttled wrapper — results are cached per exit between refreshes.
        private Dictionary<Vector3, Vector3> doorSimCache = new Dictionary<Vector3, Vector3>();
        private HashSet<Vector3> doorSimMisses = new HashSet<Vector3>();

        private bool FindBlockingDoorCached(Vector3 from, Vector3 to, Vector3 cacheKey, out Vector3 doorPoint)
        {
            doorPoint = Vector3.zero;

            // Serve from cache between refreshes
            if (doorSimTimer < DOOR_SIM_INTERVAL)
            {
                if (doorSimCache.TryGetValue(cacheKey, out doorPoint)) return true;
                if (doorSimMisses.Contains(cacheKey)) return false;
                // Not cached for this exit yet — fall through and solve it
            }
            else
            {
                // Refresh window — clear stale results
                doorSimTimer = 0f;
                doorSimCache.Clear();
                doorSimMisses.Clear();
            }

            bool found = FindBlockingDoor(from, to, out doorPoint);
            if (found) doorSimCache[cacheKey] = doorPoint;
            else doorSimMisses.Add(cacheKey);
            return found;
        }

        // Breadth-first search across the region graph for the shortest chain of locked doors
        // between the player and the exit. Returns the FIRST door in that chain — the one the
        // player can actually walk up to and open right now.
        private bool FindBlockingDoor(Vector3 from, Vector3 to, out Vector3 doorPoint)
        {
            doorPoint = Vector3.zero;

            // Still building — the caller shows the search marker until it lands
            if (!graphReady)
            {
                Plugin.Logger.LogDebug("LeadMeOut: DoorSim — region graph still building.");
                return false;
            }

            if (doorEdges.Count == 0) return false;

            int startRegion = RegionOf(from);
            int goalRegion = RegionOf(to);

            Plugin.Logger.LogDebug($"LeadMeOut: DoorSim — playerRegion={startRegion} exitRegion={goalRegion} (regions={regionReps.Count}, doors={doorEdges.Count})");

            if (startRegion < 0 || goalRegion < 0)
            {
                Plugin.Logger.LogDebug("LeadMeOut: DoorSim — player or exit sits in no door-bounded region. Blockage is structural, not a door.");
                return false;
            }

            if (startRegion == goalRegion)
            {
                // Same region, yet no walkable path — that's a NavMesh gap, not a locked door
                Plugin.Logger.LogDebug("LeadMeOut: DoorSim — player and exit share a region. Blockage is not a door.");
                return false;
            }

            // BFS over regions. cameFromDoor[r] = the door crossed to first reach region r.
            HashSet<int> visited = new HashSet<int>();
            Dictionary<int, DoorEdge> cameFromDoor = new Dictionary<int, DoorEdge>();
            Dictionary<int, int> cameFromRegion = new Dictionary<int, int>();
            Queue<int> queue = new Queue<int>();

            visited.Add(startRegion);
            queue.Enqueue(startRegion);
            bool reached = false;

            while (queue.Count > 0 && !reached)
            {
                int current = queue.Dequeue();

                foreach (DoorEdge edge in doorEdges)
                {
                    if (edge.regionA < 0 || edge.regionB < 0) continue;
                    if (edge.regionA == edge.regionB) continue; // door separates nothing

                    int next;
                    if (edge.regionA == current) next = edge.regionB;
                    else if (edge.regionB == current) next = edge.regionA;
                    else continue;

                    if (visited.Contains(next)) continue;

                    visited.Add(next);
                    cameFromDoor[next] = edge;
                    cameFromRegion[next] = current;
                    queue.Enqueue(next);

                    if (next == goalRegion) { reached = true; break; }
                }
            }

            if (!reached)
            {
                Plugin.Logger.LogDebug($"LeadMeOut: DoorSim — no chain of locked doors links region {startRegion} to region {goalRegion}.");
                return false;
            }

            // Walk the chain backwards until we hit the door adjacent to the player's region
            int walk = goalRegion;
            DoorEdge firstDoor = null;
            int chainLength = 0;

            while (walk != startRegion)
            {
                firstDoor = cameFromDoor[walk];
                walk = cameFromRegion[walk];
                chainLength++;
            }

            if (firstDoor == null) return false;

            doorPoint = firstDoor.center;
            Plugin.Logger.LogInfo($"LeadMeOut:   >>> BLOCKING DOOR at {doorPoint} — first of {chainLength} locked door(s) between you and this exit.");
            return true;
        }

        // How close to 'to' can an agent starting at 'from' actually get?
        // Uses the partial path endpoint when the full path isn't possible.
        private float ClosestReachableDistanceTo(Vector3 from, Vector3 to)
        {
            NavMeshHit fh, th;
            if (!NavMesh.SamplePosition(from, out fh, 3f, NavMesh.AllAreas)) return float.MaxValue;
            if (!NavMesh.SamplePosition(to, out th, 5f, NavMesh.AllAreas)) return float.MaxValue;

            NavMeshPath p = scratchPathReach;
            NavMesh.CalculatePath(fh.position, th.position, NavMesh.AllAreas, p);

            if (p.status == NavMeshPathStatus.PathComplete) return 0f;
            if (p.corners.Length == 0) return Vector3.Distance(fh.position, th.position);

            Vector3 end = p.corners[p.corners.Length - 1];
            return Vector3.Distance(end, th.position);
        }

        private bool IsPathComplete(Vector3 a, Vector3 b)
        {
            NavMeshPath p = scratchPathReach;
            if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, p)) return false;
            return p.status == NavMeshPathStatus.PathComplete;
        }

        // Checks whether the path actually passes THROUGH a locked door (not merely near one).
        // Is the straight hop from a to b crossing a genuine NavMesh SEAM, or a WALL?
        //
        // Bridging exists to cross tiny NavMesh baking gaps at thresholds and stairs. Those gaps
        // are a few centimetres wide. A wall is a different animal entirely: the NavMesh is
        // carved back by the agent radius on BOTH faces, so even a thin wall leaves an unwalkable
        // strip well over a metre across. Walk the segment, measure the longest continuous
        // stretch with no NavMesh beneath it, and judge on that. Short = seam, bridge it.
        // Long = wall, refuse.
        //
        // Without this, NavMesh.SamplePosition happily finds walkable ground 2 units away on the
        // far side of a wall and doesn't care what's in between. A probe lands in the room
        // beyond, scores as "closer to the exit", gets accepted — and the line sails through
        // solid concrete to the exit, solid and unpulsed, because `bridged` clears both flags.
        //
        // IMPORTANT: this is done ENTIRELY with NavMesh queries. It is NOT the Physics.Raycast
        // wall validation that was tried twice and abandoned for false-positiving on BigDoor and
        // structural geometry. There is no collision geometry involved here to be confused by.
        private bool HopCrossesSeamOnly(Vector3 a, Vector3 b)
        {
            const float step = 0.1f;          // how finely we walk the segment
            const float sampleRadius = 0.25f; // how far off the line we'll look for NavMesh
            const float maxSeamGap = 0.4f;    // widest unwalkable run we'll still call a "seam"

            Vector3 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.001f) return true;
            Vector3 dir = delta / len;

            float gapRun = 0f;

            for (float d = 0f; d <= len; d += step)
            {
                Vector3 p = a + dir * d;

                NavMeshHit hit;
                if (NavMesh.SamplePosition(p, out hit, sampleRadius, NavMesh.AllAreas))
                {
                    gapRun = 0f; // back on the mesh
                }
                else
                {
                    gapRun += step;
                    if (gapRun > maxSeamGap)
                        return false; // too wide to be a baking seam — this is a wall
                }
            }

            return true;
        }

        // Does the straight segment a->b pass through a LOCKED door's collider?
        //
        // Tested against the collider's world-space AABB rather than a hand-rolled plane, so it
        // doesn't care how the door is rotated. The previous approach assumed every door's
        // through-axis was world X or world Z, picking whichever AABB extent was smaller. For a
        // door at any other angle the AABB is roughly square, that guess is meaningless, and the
        // test silently never fires — letting paths sail straight through locked doors.
        //
        // Note this only ever tests the doors' OWN colliders, never the world at large, so it
        // can't reproduce the false-positive problem that got Physics.Raycast wall validation
        // abandoned twice. There's no geometry here to be confused by.
        private bool SegmentCrossesLockedDoor(Vector3 a, Vector3 b, out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;

            Vector3 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.001f) return false;
            Vector3 dir = delta / len;

            foreach (var entry in GetDoorCache())
            {
                if (entry.door == null || entry.col == null) continue;
                if (!entry.door.isLocked) continue;

                Bounds bounds = entry.col.bounds;

                // Slack: a little laterally so a path skimming the jamb still counts, and more
                // vertically because path corners sit on the floor while the door's collider may
                // start slightly above it.
                bounds.Expand(new Vector3(0.4f, 1f, 0.4f));

                // Segment starting inside the door volume
                if (bounds.Contains(a)) { hitPoint = a; return true; }

                float dist;
                if (bounds.IntersectRay(new Ray(a, dir), out dist) && dist <= len)
                {
                    hitPoint = a + dir * dist;
                    return true;
                }
            }

            return false;
        }

        private bool PathCrossesLockedDoor(List<Vector3> corners, out Vector3 doorPoint)
        {
            doorPoint = Vector3.zero;

            for (int i = 1; i < corners.Count; i++)
            {
                if (SegmentCrossesLockedDoor(corners[i - 1], corners[i], out doorPoint))
                {
                    Plugin.Logger.LogDebug(
                        $"LeadMeOut: path runs THROUGH a locked door at {doorPoint} — truncating there.");
                    return true;
                }
            }

            return false;
        }

        private float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abLen = ab.magnitude;
            if (abLen < 0.001f) return Vector3.Distance(p, a);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / (abLen * abLen));
            Vector3 proj = a + ab * t;
            return Vector3.Distance(p, proj);
        }

        private List<Vector3> CullByDistance(List<Vector3> points, Vector3 origin, float maxDist)
        {
            var result = new List<Vector3>();

            for (int i = 0; i < points.Count; i++)
            {
                float distFromPlayer = Vector3.Distance(origin, points[i]);
                if (distFromPlayer > maxDist)
                {
                    // Interpolate the last segment to end exactly at maxDist
                    if (i > 0)
                    {
                        float prevDist = Vector3.Distance(origin, points[i - 1]);
                        float segLen = Vector3.Distance(points[i - 1], points[i]);
                        if (segLen > 0.001f)
                        {
                            float t = (maxDist - prevDist) / (distFromPlayer - prevDist);
                            result.Add(Vector3.Lerp(points[i - 1], points[i], Mathf.Clamp01(t)));
                        }
                    }
                    break;
                }
                result.Add(points[i]);
            }

            return result;
        }

        private void RenderPath(GameObject root, List<LineRenderer> segments, List<LineRenderer> diamonds,
            List<GameObject> shapes, List<Vector3> points, Color color, LineStyle style, float width,
            float lateralOffset, float phaseOffset, Vector3 targetPos, bool isLockedDoor = false)
        {
            // Pulse brightness when path goes through a locked/blocked door
            if (isLockedDoor)
            {
                float pulse = Mathf.Sin(pulseTimer * 4f) * 0.5f + 0.5f; // 0..1
                float brightness = Mathf.Lerp(0.3f, 1f, pulse);          // 30%..100%
                color = new Color(color.r * brightness, color.g * brightness, color.b * brightness, color.a);
            }
            if (lateralOffset != 0f)
                points = ApplyLateralOffset(points, lateralOffset);

            bool isShapeStyle = style == LineStyle.Arrow || style == LineStyle.Triangle
                             || style == LineStyle.Diamond || style == LineStyle.Heart
                             || style == LineStyle.Pawprint;

            if (isShapeStyle)
            {
                HideSegments(segments);
                HideSegments(diamonds);
                RenderShapes(root, shapes, points, color, style, width, targetPos);
            }
            else
            {
                HideShapes(shapes);

                float dashLen, gapLen;
                if (style == LineStyle.Dotted)
                {
                    dashLen = width;
                    gapLen = width * 8f + 0.2f;
                }
                else
                {
                    dashLen = 0.4f;
                    gapLen = 0.35f;
                }

                List<(Vector3 start, Vector3 end)> dashes = style == LineStyle.Solid
                    ? BuildSolid(points)
                    : BuildDashes(points, dashLen, gapLen, phaseOffset);

                UpdateLineSegments(root, segments, dashes, color, width);

                if (style == LineStyle.Dotted)
                    UpdateDiamonds(root, diamonds, dashes, color, width);
                else
                    HideSegments(diamonds);
            }
        }

        // Shows or hides a dead-end marker at the end of a path.
        // If a locked door is detected near the endpoint, shows a lock icon on the door.
        // Otherwise shows a 4-way arrow flat against the terminating surface.
        // Does this line END at the mineshaft elevator?
        //
        // Needs BOTH conditions, and both for good reason:
        //
        //   targetIsElevator — is this line actually routing to the elevator right now? In
        //     Mineshaft the top exit door sits physically CLOSE to the shaft, so an endpoint-only
        //     test stamps an elevator icon on the main entrance line while it's heading for the
        //     real exit door. The two lines are complementary: on the top floor the green line
        //     goes to the exit and the red one goes down the elevator; below, the green line goes
        //     up the elevator and the red one goes to the real fire exit. Exactly one of them is
        //     elevator-bound at any moment, and the other must never claim the icon.
        //
        //   endpoint near the shaft (XZ) — judged HORIZONTALLY. The elevator is a vertical shaft:
        //     its top doorway and its bottom panel share nearly the same X/Z but differ hugely in
        //     Y. From the bottom floor the line reaches the panel; from the top floor it can't
        //     descend the shaft and stops at the doors up top, tens of units above the panel it
        //     was aiming at. Ignore Y and both endpoints land on the same shaft.
        private bool LineEndsAtElevator(PathResult result, bool targetIsElevator)
        {
            if (mineshaftBottomTarget == null) return false;

            // Not routing to the elevator at all — this line can never earn the icon
            if (!targetIsElevator) return false;

            // Resolve the shaft position safely — it may have been destroyed mid-transition
            Vector3 shaft;
            if (!TryGetPosition(mineshaftBottomTarget, out shaft)) return false;

            // Blocked by a REAL locked door on the way there? Then the line stops AT THE DOOR,
            // and that's where the padlock belongs. Elevators themselves are never locked.
            //
            // This MUST test IsDoorBlocked, not IsLockedDoor. IsLockedDoor is the "pulse the
            // line" flag and GetPath raises it for any unbridgeable partial path — which the
            // top-floor route to the elevator ALWAYS is, since it can't descend the shaft.
            if (result.IsDoorBlocked) return false;

            var pts = result.Points;
            if (pts == null || pts.Count == 0) return false;

            Vector3 end = pts[pts.Count - 1];

            float dx = end.x - shaft.x;
            float dz = end.z - shaft.z;
            float xz = Mathf.Sqrt(dx * dx + dz * dz);

            // Logged only when a marker would actually appear, so it stays quiet in normal play
            if (result.IsDeadEnd)
            {
                Plugin.Logger.LogDebug(
                    $"LeadMeOut: elevator check — line ends {xz:F1} away (XZ) from the shaft, " +
                    $"dY {Mathf.Abs(end.y - shaft.y):F1}, radius {ELEVATOR_XZ_RADIUS} => " +
                    (xz <= ELEVATOR_XZ_RADIUS ? "ELEVATOR" : "not the elevator"));
            }

            return xz <= ELEVATOR_XZ_RADIUS;
        }

        private GameObject UpdateDeadEndMarker(GameObject marker, GameObject root, bool show,
            List<Vector3> points, Color color, float brightness, bool elevatorEndpoint,
            float lateralOffset = 0f)
        {
            if (!show || points == null || points.Count < 2)
            {
                if (marker != null) marker.SetActive(false);
                return marker;
            }

            Vector3 endPoint = points[points.Count - 1];
            Vector3 prevPoint = points[points.Count - 2];
            Vector3 lineDir = (endPoint - prevPoint).normalized;

            // Back out the line's lateral offset so all markers sit centered on the path,
            // even though the lines themselves stay offset from each other.
            if (Mathf.Abs(lateralOffset) > 0.001f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, lineDir).normalized;
                endPoint -= right * lateralOffset;
                prevPoint -= right * lateralOffset;
            }

            // Icon precedence: ELEVATOR wins outright.
            //
            // An elevator can't be locked, so any DoorLock the proximity scan turns up near an
            // elevator endpoint is incidental — a door that happens to sit nearby, not one
            // that's blocking anything. Letting a padlock win there would be a false positive.
            //
            // The case where a locked door genuinely blocks the route TO the elevator is already
            // handled upstream: the path dead-ends AT THE DOOR, so IsLockedDoor comes back true,
            // the caller doesn't flag this as an elevator endpoint, and the padlock lands on the
            // door where it belongs. That holds even when the door sits close to the shaft.
            bool elevator = elevatorEndpoint;

            Vector3 doorPos = Vector3.zero;
            Vector3 doorNormal = Vector3.up;
            bool lockedDoor = false;
            if (!elevator)
                lockedDoor = FindLockedDoorNear(endPoint, out doorPos, out doorNormal);

            // The elevator is a way OUT, not a blocker — never pulse it, never dim it.
            if (elevator) brightness = 1f;

            // Create the marker on first use
            if (marker == null)
            {
                marker = new GameObject("DeadEndMarker");
                marker.transform.SetParent(root.transform, false);
                marker.AddComponent<MeshFilter>();
                MeshRenderer mr = marker.AddComponent<MeshRenderer>();
                mr.material = new Material(Shader.Find("HDRP/Unlit"));
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            marker.SetActive(true);

            // Swap mesh based on marker type (tracked via name to avoid rebuilding every frame)
            MeshFilter mf = marker.GetComponent<MeshFilter>();
            string wantType = lockedDoor ? "lock" : elevator ? "elevator" : "arrow";
            if (marker.name != "DeadEndMarker_" + wantType)
            {
                mf.mesh = lockedDoor ? BuildLockMesh()
                        : elevator ? BuildElevatorMesh()
                        : Build4WayArrowMesh();
                marker.name = "DeadEndMarker_" + wantType;
            }

            Vector3 surfaceNormal;
            Vector3 markerPos;

            if (lockedDoor)
            {
                // Pull the marker back from the door toward the approach direction so it
                // doesn't clip inside the door's geometry.
                Vector3 backOff = -lineDir; // back along the direction we approached from
                backOff.y = 0f;
                if (backOff.sqrMagnitude < 0.001f) backOff = -doorNormal;
                backOff.Normalize();

                surfaceNormal = doorNormal;
                markerPos = doorPos + backOff * 0.6f;
            }
            else
            {
                // Default: sit flat at the line's actual endpoint on the floor
                surfaceNormal = Vector3.up;
                markerPos = endPoint + Vector3.up * 0.05f;

                RaycastHit hit;
                int mask = ~0;

                // Only snap to a wall if the endpoint is very close to one in the line's
                // direction (within 1 unit) — otherwise keep it flat at the endpoint.
                if (Physics.Raycast(prevPoint, lineDir, out hit, 1f, mask, QueryTriggerInteraction.Ignore)
                    && Mathf.Abs(hit.normal.y) < 0.5f)
                {
                    surfaceNormal = hit.normal;
                    // Align horizontally with the endpoint (project endpoint onto the wall),
                    // then lift up so the full icon clears the floor.
                    Vector3 wallPoint = endPoint - Vector3.Dot(endPoint - hit.point, surfaceNormal) * surfaceNormal;
                    markerPos = wallPoint + surfaceNormal * 0.05f + Vector3.up * (0.5f * 0.5f);
                    // (0.5f marker scale * 0.5f = half the marker height lifted up)
                }
                else if (Physics.Raycast(endPoint + Vector3.up * 0.5f, Vector3.down, out hit, 2f, mask, QueryTriggerInteraction.Ignore))
                {
                    // Snap to the floor surface beneath the endpoint, keeping XZ centered
                    surfaceNormal = hit.normal;
                    markerPos = new Vector3(endPoint.x, hit.point.y, endPoint.z) + surfaceNormal * 0.05f;
                }
            }

            marker.transform.position = markerPos + Vector3.up * 0.7f;

            // Billboard: always face the player, upright. This avoids surface-axis
            // confusion and keeps the icon fully readable from any approach angle.
            var player = GetLocalPlayer();
            if (player != null)
            {
                Vector3 toPlayer = player.transform.position - marker.transform.position;
                toPlayer.y = 0f; // keep the marker upright (no tilting up/down)
                if (toPlayer.sqrMagnitude > 0.001f)
                {
                    // Mesh is built on XZ (face normal +Y, icon-up along +Z). We want the
                    // face (+Y) to point AT the player and icon-up (+Z) to point world-up.
                    //
                    // The "upwards" argument used to be NEGATED here, which aimed the front
                    // face away from the camera. The meshes are double-sided so they still
                    // drew — but we were seeing their BACK, which renders every icon mirrored
                    // left-to-right. Nothing tumbles when this is corrected: the rotation is
                    // about the world-up axis, and icon-up is aligned to world-up, so vertical
                    // orientation is preserved by construction. Only the visible face swaps.
                    Quaternion faceRot = Quaternion.LookRotation(Vector3.up, toPlayer.normalized);
                    marker.transform.rotation = faceRot;
                }
            }
            marker.transform.localScale = Vector3.one * 0.8f;

            MeshRenderer rend = marker.GetComponent<MeshRenderer>();
            if (rend != null)
            {
                Color c = new Color(color.r * brightness, color.g * brightness, color.b * brightness, 1f);
                rend.material.color = c;
            }

            return marker;
        }

        // Searches for a locked door near a point. Returns its position and facing normal.
        private bool FindLockedDoorNear(Vector3 point, out Vector3 doorPos, out Vector3 doorNormal)
        {
            doorPos = point;
            doorNormal = Vector3.up;

            const float searchRadius = 4f;
            DoorLock nearest = null;
            float nearestDist = searchRadius;

            foreach (var entry in GetDoorCache())
            {
                DoorLock door = entry.door;
                if (door == null) continue;
                if (!door.isLocked) continue;
                float d = Vector3.Distance(point, door.transform.position);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = door;
                }
            }

            if (nearest == null) return false;

            doorPos = nearest.transform.position;
            // Door's forward is typically its facing direction; use it as the surface normal
            doorNormal = nearest.transform.forward;
            // Flatten to horizontal so the lock sits vertically on the door
            doorNormal.y = 0f;
            if (doorNormal.sqrMagnitude < 0.001f) doorNormal = Vector3.forward;
            doorNormal.Normalize();

            return true;
        }

        private void RenderShapes(GameObject root, List<GameObject> shapes, List<Vector3> points,
            Color color, LineStyle style, float width, Vector3 targetPos)
        {
            float spacing;
            if (style == LineStyle.Heart || style == LineStyle.Pawprint) spacing = width * 12f + 0.4f;
            else spacing = width * 20f + 0.6f;
            if (spacing < 0.05f) spacing = 0.05f;

            List<(Vector3 pos, Vector3 dir)> placements = GetShapePlacements(points, spacing);

            for (int i = 0; i < shapes.Count; i++)
            {
                if (i < placements.Count)
                {
                    shapes[i].SetActive(true);
                    MeshFilter mf = shapes[i].GetComponent<MeshFilter>();
                    if (mf != null) mf.mesh = style == LineStyle.Arrow ? BuildArrowMesh()
                                                : style == LineStyle.Triangle ? BuildTriangleMesh()
                                                : style == LineStyle.Diamond ? BuildDiamondMesh()
                                                : style == LineStyle.Pawprint ? BuildPawprintMesh()
                                                : BuildHeartMesh();
                    ApplyShapeTransform(shapes[i], placements[i].pos, placements[i].dir, width);
                    shapes[i].GetComponent<MeshRenderer>().material.color = color;
                }
                else shapes[i].SetActive(false);
            }

            for (int i = shapes.Count; i < placements.Count; i++)
            {
                GameObject shape = CreateShapeMesh(root, style, color, width);
                ApplyShapeTransform(shape, placements[i].pos, placements[i].dir, width);
                shapes.Add(shape);
            }
        }

        private List<(Vector3 pos, Vector3 dir)> GetShapePlacements(List<Vector3> points, float spacing)
        {
            var placements = new List<(Vector3, Vector3)>();
            int maxShapes = 200;
            float distSinceLastPlace = spacing * 0.5f;

            for (int i = 1; i < points.Count; i++)
            {
                if (placements.Count >= maxShapes) break;

                Vector3 from = points[i - 1];
                Vector3 to = points[i];
                float segLen = Vector3.Distance(from, to);
                if (segLen < 0.001f) continue;

                Vector3 dir = (to - from).normalized;
                float traveled = 0f;

                while (traveled < segLen && placements.Count < maxShapes)
                {
                    float toNext = spacing - distSinceLastPlace;
                    if (traveled + toNext <= segLen)
                    {
                        traveled += toNext;
                        placements.Add((from + dir * traveled, dir));
                        distSinceLastPlace = 0f;
                    }
                    else
                    {
                        distSinceLastPlace += segLen - traveled;
                        traveled = segLen;
                    }
                }
            }

            return placements;
        }

        private void ApplyShapeTransform(GameObject shape, Vector3 pos, Vector3 dir, float width)
        {
            shape.transform.position = pos;
            if (dir != Vector3.zero)
                shape.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            float scale = width * 6f;
            shape.transform.localScale = new Vector3(scale, scale, scale);
        }

        private GameObject CreateShapeMesh(GameObject root, LineStyle style, Color color, float width)
        {
            GameObject obj = new GameObject("Shape");
            obj.transform.SetParent(root.transform);

            MeshFilter mf = obj.AddComponent<MeshFilter>();
            MeshRenderer mr = obj.AddComponent<MeshRenderer>();

            mf.mesh = style == LineStyle.Arrow ? BuildArrowMesh()
                    : style == LineStyle.Triangle ? BuildTriangleMesh()
                    : style == LineStyle.Diamond ? BuildDiamondMesh()
                    : style == LineStyle.Pawprint ? BuildPawprintMesh()
                    : BuildHeartMesh();

            Material mat = new Material(Shader.Find("HDRP/Unlit"));
            mat.color = color;
            mr.material = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return obj;
        }

        private Mesh BuildArrowMesh()
        {
            Mesh mesh = new Mesh();
            Vector3[] verts = new Vector3[]
            {
                new Vector3(-0.15f, 0f, -0.5f), new Vector3( 0.15f, 0f, -0.5f),
                new Vector3( 0.15f, 0f,  0.1f), new Vector3(-0.15f, 0f,  0.1f),
                new Vector3(-0.35f, 0f,  0.1f), new Vector3( 0.35f, 0f,  0.1f),
                new Vector3( 0f,    0f,  0.5f),
            };
            mesh.vertices = verts;
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 2, 1, 0, 3, 2, 0, 6, 5, 4 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildTriangleMesh()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3( 0f, 0f,  0.5f),
                new Vector3(-0.5f, 0f, -0.4f),
                new Vector3( 0.5f, 0f, -0.4f),
            };
            mesh.triangles = new int[] { 0, 1, 2, 2, 1, 0 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildHeartMesh()
        {
            Mesh mesh = new Mesh();
            int segments = 20;
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            verts.Add(new Vector3(0f, 0f, 0f));

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments * Mathf.PI * 2f;
                float x = 0.4f * Mathf.Pow(Mathf.Sin(t), 3f);
                float z = 0.4f * (0.8125f * Mathf.Cos(t)
                                - 0.3125f * Mathf.Cos(2f * t)
                                - 0.125f * Mathf.Cos(3f * t)
                                - 0.0625f * Mathf.Cos(4f * t));
                verts.Add(new Vector3(x, 0f, z));
            }

            int faceCount = segments;
            for (int i = 1; i <= faceCount; i++)
            {
                tris.Add(0); tris.Add(i); tris.Add(i < faceCount ? i + 1 : 1);
            }
            int fc = tris.Count;
            for (int i = 0; i < fc; i += 3)
            {
                tris.Add(tris[i + 2]); tris.Add(tris[i + 1]); tris.Add(tris[i]);
            }

            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildDiamondMesh()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3( 0f,    0f,  0.6f),  // front (elongated)
                new Vector3( 0.28f, 0f,  0f),    // right (narrower)
                new Vector3( 0f,    0f, -0.6f),  // back (elongated)
                new Vector3(-0.28f, 0f,  0f),    // left (narrower)
            };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildPawprintMesh()
        {
            Mesh mesh = new Mesh();
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();
            int circleSegments = 12;

            void AddEllipse(float cx, float cz, float rx, float rz)
            {
                int centerIdx = verts.Count;
                verts.Add(new Vector3(cx, 0f, cz));
                int firstRim = verts.Count;
                for (int i = 0; i < circleSegments; i++)
                {
                    float t = (float)i / circleSegments * Mathf.PI * 2f;
                    verts.Add(new Vector3(cx + rx * Mathf.Cos(t), 0f, cz + rz * Mathf.Sin(t)));
                }
                for (int i = 0; i < circleSegments; i++)
                {
                    int a = firstRim + i;
                    int b = firstRim + (i + 1) % circleSegments;
                    tris.Add(centerIdx); tris.Add(a); tris.Add(b);
                    tris.Add(centerIdx); tris.Add(b); tris.Add(a);
                }
            }

            AddEllipse(0f, -0.15f, 0.22f, 0.22f); // heel pad
            AddEllipse(-0.10f, 0.22f, 0.09f, 0.11f); // front-left toe
            AddEllipse(0.10f, 0.22f, 0.09f, 0.11f); // front-right toe
            AddEllipse(-0.24f, 0.08f, 0.08f, 0.10f); // outer-left toe
            AddEllipse(0.24f, 0.08f, 0.08f, 0.10f); // outer-right toe

            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh Build4WayArrowMesh()
        {
            // "Search around" icon: 4 outward arrows + a magnifying glass containing an eye.
            // Built flat on the XZ plane (normal +Y), centered at origin, ~1 unit across.
            Mesh mesh = new Mesh();
            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Triangulate a convex/simple polygon as a fan from its first vertex.
            void Poly(params Vector3[] p)
            {
                int b = verts.Count;
                foreach (var v in p) verts.Add(v);
                for (int i = 1; i < p.Length - 1; i++)
                {
                    tris.Add(b); tris.Add(b + i); tris.Add(b + i + 1);
                    tris.Add(b); tris.Add(b + i + 1); tris.Add(b + i); // double-sided
                }
            }

            // A ring (annulus) — used for the lens and the eye outline
            void Ring(float cx, float cz, float rOuter, float rInner, int seg,
                      float startAngle = 0f, float sweep = Mathf.PI * 2f)
            {
                for (int i = 0; i < seg; i++)
                {
                    float a0 = startAngle + sweep * (i / (float)seg);
                    float a1 = startAngle + sweep * ((i + 1) / (float)seg);
                    Vector3 o0 = new Vector3(cx + Mathf.Cos(a0) * rOuter, 0, cz + Mathf.Sin(a0) * rOuter);
                    Vector3 o1 = new Vector3(cx + Mathf.Cos(a1) * rOuter, 0, cz + Mathf.Sin(a1) * rOuter);
                    Vector3 i0 = new Vector3(cx + Mathf.Cos(a0) * rInner, 0, cz + Mathf.Sin(a0) * rInner);
                    Vector3 i1 = new Vector3(cx + Mathf.Cos(a1) * rInner, 0, cz + Mathf.Sin(a1) * rInner);
                    Poly(i0, o0, o1, i1);
                }
            }

            // A filled disc — used for the pupil
            void Disc(float cx, float cz, float r, int seg)
            {
                int c = verts.Count;
                verts.Add(new Vector3(cx, 0, cz));
                int first = verts.Count;
                for (int i = 0; i < seg; i++)
                {
                    float a = Mathf.PI * 2f * (i / (float)seg);
                    verts.Add(new Vector3(cx + Mathf.Cos(a) * r, 0, cz + Mathf.Sin(a) * r));
                }
                for (int i = 0; i < seg; i++)
                {
                    int a = first + i;
                    int b = first + (i + 1) % seg;
                    tris.Add(c); tris.Add(a); tris.Add(b);
                    tris.Add(c); tris.Add(b); tris.Add(a);
                }
            }

            // ---- Four outward arrows (from the reference SVG) ----
            // Each arrow is split into a convex shaft quad + a convex head triangle,
            // because fan-triangulating the concave arrow outline distorts the head.

            // UP: shaft from z=0.181 to 0.287, head from 0.287 to 0.477
            Poly(new Vector3(-0.072f, 0f, 0.181f), new Vector3(0.070f, 0f, 0.181f),
                 new Vector3(0.070f, 0f, 0.287f), new Vector3(-0.072f, 0f, 0.287f));
            Poly(new Vector3(-0.167f, 0f, 0.287f), new Vector3(0.166f, 0f, 0.287f),
                 new Vector3(-0.001f, 0f, 0.477f));

            // DOWN
            Poly(new Vector3(-0.072f, 0f, -0.181f), new Vector3(0.070f, 0f, -0.181f),
                 new Vector3(0.070f, 0f, -0.287f), new Vector3(-0.072f, 0f, -0.287f));
            Poly(new Vector3(0.166f, 0f, -0.287f), new Vector3(-0.167f, 0f, -0.287f),
                 new Vector3(-0.001f, 0f, -0.477f));

            // LEFT
            Poly(new Vector3(-0.181f, 0f, -0.071f), new Vector3(-0.181f, 0f, 0.071f),
                 new Vector3(-0.288f, 0f, 0.071f), new Vector3(-0.288f, 0f, -0.071f));
            Poly(new Vector3(-0.288f, 0f, 0.166f), new Vector3(-0.288f, 0f, -0.166f),
                 new Vector3(-0.478f, 0f, 0f));

            // RIGHT
            Poly(new Vector3(0.180f, 0f, -0.071f), new Vector3(0.180f, 0f, 0.071f),
                 new Vector3(0.286f, 0f, 0.071f), new Vector3(0.286f, 0f, -0.071f));
            Poly(new Vector3(0.286f, 0f, -0.166f), new Vector3(0.286f, 0f, 0.166f),
                 new Vector3(0.476f, 0f, 0f));

            // ---- Magnifying glass ----
            // Lens ring
            Ring(0f, 0.02f, 0.145f, 0.115f, 24);

            // Handle — a short thick bar angled down-right from the lens
            Vector3 hA = new Vector3(0.095f, 0f, -0.075f);
            Vector3 hB = new Vector3(0.175f, 0f, -0.155f);
            Vector3 hPerp = new Vector3(0.022f, 0f, 0.022f);
            Poly(hA - hPerp, hA + hPerp, hB + hPerp, hB - hPerp);

            // ---- Eye inside the lens ----
            // Almond outline: two arcs bulging away from a horizontal axis.
            // Approximated as a thin lens/ellipse ring.
            int eyeSeg = 20;
            float eyeRx = 0.085f, eyeRz = 0.048f, eyeThick = 0.014f;
            for (int i = 0; i < eyeSeg; i++)
            {
                float a0 = Mathf.PI * 2f * (i / (float)eyeSeg);
                float a1 = Mathf.PI * 2f * ((i + 1) / (float)eyeSeg);
                Vector3 o0 = new Vector3(Mathf.Cos(a0) * eyeRx, 0, 0.02f + Mathf.Sin(a0) * eyeRz);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * eyeRx, 0, 0.02f + Mathf.Sin(a1) * eyeRz);
                Vector3 i0 = new Vector3(Mathf.Cos(a0) * (eyeRx - eyeThick), 0, 0.02f + Mathf.Sin(a0) * (eyeRz - eyeThick));
                Vector3 i1 = new Vector3(Mathf.Cos(a1) * (eyeRx - eyeThick), 0, 0.02f + Mathf.Sin(a1) * (eyeRz - eyeThick));
                Poly(i0, o0, o1, i1);
            }

            // Pupil
            Disc(0f, 0.02f, 0.026f, 12);

            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildLockMesh()
        {
            // Padlock icon: a solid body (rounded rectangle) + a shackle arch on top.
            // Built flat on XZ plane, centered, roughly 1x1 unit (scaled later to ~0.5).
            Mesh mesh = new Mesh();
            var verts = new List<Vector3>();
            var tris = new List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
            }

            // Lock body (main rectangle, lower portion)
            // Shifted up so the padlock's visual center sits near the mesh origin,
            // matching how the arrow mesh is centered.
            float bodyW = 0.30f;   // half width
            float bodyTop = 0.22f;
            float bodyBot = -0.23f;
            Quad(new Vector3(-bodyW, 0, bodyBot), new Vector3(-bodyW, 0, bodyTop),
                 new Vector3(bodyW, 0, bodyTop), new Vector3(bodyW, 0, bodyBot));

            // Shackle arch (U-shape) — approximate with segments of a torus-like ring
            float shackleOuter = 0.22f;
            float shackleInner = 0.12f;
            float shackleBaseZ = 0.22f; // where the shackle meets the body
            int arcSegments = 10;
            for (int i = 0; i < arcSegments; i++)
            {
                float a0 = Mathf.PI * (i / (float)arcSegments);       // 0..PI (half circle)
                float a1 = Mathf.PI * ((i + 1) / (float)arcSegments);

                // Outer and inner points, arch opens downward toward the body
                Vector3 o0 = new Vector3(-Mathf.Cos(a0) * shackleOuter, 0, shackleBaseZ + Mathf.Sin(a0) * shackleOuter);
                Vector3 o1 = new Vector3(-Mathf.Cos(a1) * shackleOuter, 0, shackleBaseZ + Mathf.Sin(a1) * shackleOuter);
                Vector3 i0 = new Vector3(-Mathf.Cos(a0) * shackleInner, 0, shackleBaseZ + Mathf.Sin(a0) * shackleInner);
                Vector3 i1 = new Vector3(-Mathf.Cos(a1) * shackleInner, 0, shackleBaseZ + Mathf.Sin(a1) * shackleInner);

                Quad(i0, o0, o1, i1);
            }

            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildElevatorMesh()
        {
            // Elevator icon: a standing figure beside stacked up/down triangles — the standard
            // "lift" pictogram. Built flat on the XZ plane (normal +Y, icon-up along +Z),
            // centered at origin, ~1 unit across, matching the other marker meshes.
            //
            // The reference art is a white glyph on a black tile, but the marker renders in a
            // single configurable colour, so only the glyph is built. It stays legible: the
            // silhouette and the triangles are chunky enough to read at distance, and there are
            // no thin strokes to vanish when the icon is scaled down.
            Mesh mesh = new Mesh();
            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Triangulate a simple polygon as a fan, double-sided
            void Poly(params Vector3[] p)
            {
                int b = verts.Count;
                foreach (var v in p) verts.Add(v);
                for (int i = 1; i < p.Length - 1; i++)
                {
                    tris.Add(b); tris.Add(b + i); tris.Add(b + i + 1);
                    tris.Add(b); tris.Add(b + i + 1); tris.Add(b + i); // reversed = double-sided
                }
            }

            // Axis-aligned rectangle on XZ
            void Rect(float x0, float z0, float x1, float z1)
            {
                Poly(new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0),
                     new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1));
            }

            // Filled disc — the figure's head
            void Disc(float cx, float cz, float r, int seg)
            {
                int c = verts.Count;
                verts.Add(new Vector3(cx, 0f, cz));
                int first = verts.Count;
                for (int i = 0; i < seg; i++)
                {
                    float a = Mathf.PI * 2f * (i / (float)seg);
                    verts.Add(new Vector3(cx + Mathf.Cos(a) * r, 0f, cz + Mathf.Sin(a) * r));
                }
                for (int i = 0; i < seg; i++)
                {
                    int a = first + i;
                    int b = first + (i + 1) % seg;
                    tris.Add(c); tris.Add(a); tris.Add(b);
                    tris.Add(c); tris.Add(b); tris.Add(a); // double-sided
                }
            }

            // ---- Standing figure (left) ----
            Disc(-0.29f, 0.33f, 0.105f, 16);          // head
            Rect(-0.42f, 0.19f, -0.16f, -0.10f);      // torso
            Rect(-0.42f, -0.10f, -0.32f, -0.42f);     // left leg
            Rect(-0.26f, -0.10f, -0.16f, -0.42f);     // right leg

            // ---- Up / down triangles (right) ----
            Poly(new Vector3(0.06f, 0f, 0.10f),       // up: base left
                 new Vector3(0.46f, 0f, 0.10f),       //     base right
                 new Vector3(0.26f, 0f, 0.45f));      //     apex

            Poly(new Vector3(0.06f, 0f, -0.02f),      // down: base left
                 new Vector3(0.46f, 0f, -0.02f),      //       base right
                 new Vector3(0.26f, 0f, -0.37f));     //       apex

            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private List<(Vector3, Vector3)> BuildSolid(List<Vector3> points)
        {
            var result = new List<(Vector3, Vector3)>();
            for (int i = 1; i < points.Count; i++)
                result.Add((points[i - 1], points[i]));
            return result;
        }

        private List<(Vector3, Vector3)> BuildDashes(List<Vector3> points, float dashLength, float gapLength, float phaseOffset)
        {
            var dashes = new List<(Vector3, Vector3)>();
            float distIntoPattern = phaseOffset % (dashLength + gapLength);
            bool inDash = distIntoPattern < dashLength;
            float segmentRemaining = inDash ? dashLength - distIntoPattern : gapLength - (distIntoPattern - dashLength);

            for (int i = 1; i < points.Count; i++)
            {
                Vector3 segStart = points[i - 1];
                Vector3 segEnd = points[i];
                float segLength = Vector3.Distance(segStart, segEnd);
                float traveled = 0f;

                while (traveled < segLength)
                {
                    float stepSize = Mathf.Min(segmentRemaining, segLength - traveled);
                    Vector3 p0 = Vector3.Lerp(segStart, segEnd, traveled / segLength);
                    Vector3 p1 = Vector3.Lerp(segStart, segEnd, (traveled + stepSize) / segLength);

                    if (inDash) dashes.Add((p0, p1));

                    traveled += stepSize;
                    segmentRemaining -= stepSize;

                    if (segmentRemaining <= 0.001f)
                    {
                        inDash = !inDash;
                        segmentRemaining = inDash ? dashLength : gapLength;
                    }
                }
            }
            return dashes;
        }

        private void UpdateLineSegments(GameObject root, List<LineRenderer> segments,
            List<(Vector3 start, Vector3 end)> dashes, Color color, float width)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (i < dashes.Count)
                {
                    segments[i].gameObject.SetActive(true);
                    segments[i].startWidth = width;
                    segments[i].endWidth = width;
                    segments[i].startColor = color;
                    segments[i].endColor = color;
                    segments[i].material.color = color;
                    segments[i].SetPosition(0, dashes[i].start);
                    segments[i].SetPosition(1, dashes[i].end);
                }
                else segments[i].gameObject.SetActive(false);
            }

            for (int i = segments.Count; i < dashes.Count; i++)
            {
                GameObject segObj = new GameObject($"Seg_{i}");
                segObj.transform.SetParent(root.transform);
                LineRenderer lr = segObj.AddComponent<LineRenderer>();
                SetupLineRenderer(lr, color, width);
                lr.SetPosition(0, dashes[i].start);
                lr.SetPosition(1, dashes[i].end);
                segments.Add(lr);
            }
        }

        private void UpdateDiamonds(GameObject root, List<LineRenderer> diamonds,
            List<(Vector3 start, Vector3 end)> dashes, Color color, float width)
        {
            float[] rotations = new float[] { 22.5f, 45f, 158.5f };
            int layerSize = dashes.Count;
            int totalNeeded = layerSize * rotations.Length;

            for (int d = 0; d < diamonds.Count; d++)
            {
                if (d < totalNeeded)
                {
                    int di = d % layerSize;
                    float rot = rotations[d / layerSize];
                    Vector3 mid = (dashes[di].start + dashes[di].end) * 0.5f;
                    Vector3 rotated = Quaternion.Euler(0, rot, 0) * (dashes[di].end - dashes[di].start);
                    diamonds[d].gameObject.SetActive(true);
                    diamonds[d].startWidth = width;
                    diamonds[d].endWidth = width;
                    diamonds[d].startColor = color;
                    diamonds[d].endColor = color;
                    diamonds[d].material.color = color;
                    diamonds[d].SetPosition(0, mid - rotated * 0.5f);
                    diamonds[d].SetPosition(1, mid + rotated * 0.5f);
                }
                else diamonds[d].gameObject.SetActive(false);
            }

            for (int d = diamonds.Count; d < totalNeeded; d++)
            {
                int di = d % layerSize;
                float rot = rotations[d / layerSize];
                GameObject dObj = new GameObject($"Diamond_{d}");
                dObj.transform.SetParent(root.transform);
                LineRenderer lr = dObj.AddComponent<LineRenderer>();
                SetupLineRenderer(lr, color, width);
                Vector3 mid = (dashes[di].start + dashes[di].end) * 0.5f;
                Vector3 rotated = Quaternion.Euler(0, rot, 0) * (dashes[di].end - dashes[di].start);
                lr.SetPosition(0, mid - rotated * 0.5f);
                lr.SetPosition(1, mid + rotated * 0.5f);
                diamonds.Add(lr);
            }
        }

        private void SetupLineRenderer(LineRenderer lr, Color color, float width)
        {
            lr.material = new Material(Shader.Find("HDRP/Unlit"));
            lr.material.color = color;
            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
        }

        private List<Vector3> ApplyLateralOffset(List<Vector3> points, float offset)
        {
            var result = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 dirBefore = i > 0 ? (points[i] - points[i - 1]).normalized : Vector3.zero;
                Vector3 dirAfter = i < points.Count - 1 ? (points[i + 1] - points[i]).normalized : Vector3.zero;
                Vector3 dir = (dirBefore + dirAfter).normalized;
                if (dir == Vector3.zero) dir = dirBefore != Vector3.zero ? dirBefore : dirAfter;
                Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                result.Add(points[i] + perp * offset);
            }
            return result;
        }

        private void HideSegments(List<LineRenderer> segments)
        {
            foreach (var s in segments)
                if (s != null) s.gameObject.SetActive(false);
        }

        private void HideShapes(List<GameObject> shapes)
        {
            foreach (var s in shapes)
                if (s != null) s.SetActive(false);
        }

        // Nudges a target point perpendicular-left relative to the approach direction
        // from the player, so the offset is always relative to how you're facing the elevator
        private Vector3 ElevatorNudge(Vector3 from, Vector3 target, float amount)
        {
            Vector3 approach = target - from;
            approach.y = 0f;
            if (approach.sqrMagnitude < 0.001f) return target;
            approach.Normalize();
            // Left of approach direction
            Vector3 left = Vector3.Cross(approach, Vector3.up);
            return target + left * amount;
        }

        private Vector3[] CenterCorners(Vector3[] corners)
        {
            if (corners.Length < 3) return corners;

            var result = new Vector3[corners.Length];
            result[0] = corners[0];
            result[corners.Length - 1] = corners[corners.Length - 1];

            for (int i = 1; i < corners.Length - 1; i++)
            {
                Vector3 forward = (corners[i + 1] - corners[i - 1]);
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f) { result[i] = corners[i]; continue; }
                forward.Normalize();

                // Skip centering on stairs/ramps — horizontal probes give wrong results
                float yDelta = Mathf.Abs(corners[i + 1].y - corners[i - 1].y);
                if (yDelta > 0.75f) { result[i] = corners[i]; continue; }

                result[i] = CenterInCorridor(corners[i], forward);
            }
            return result;
        }

        private Vector3 CenterInCorridor(Vector3 point, Vector3 forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            const float probeLength = 3f;

            NavMeshHit rightHit, leftHit;
            float rightDist = probeLength;
            float leftDist = probeLength;

            if (NavMesh.Raycast(point, point + right * probeLength, out rightHit, NavMesh.AllAreas))
                rightDist = rightHit.distance;
            if (NavMesh.Raycast(point, point - right * probeLength, out leftHit, NavMesh.AllAreas))
                leftDist = leftHit.distance;

            float shift = (rightDist - leftDist) * 0.5f;
            Vector3 centered = point + right * shift;

            NavMeshHit snap;
            if (NavMesh.SamplePosition(centered, out snap, 0.5f, NavMesh.AllAreas))
                return snap.position;

            return point;
        }

        private Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abLen = ab.magnitude;
            if (abLen < 0.001f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / (abLen * abLen));
            return a + ab * t;
        }

        // Forces the line to run STRAIGHT THROUGH doorway openings instead of curving across
        // their corners, which is what makes it clip doorframes and wall corners.
        //
        // How it finds the opening matters. The obvious approach — snap to the door collider's
        // centre — is actively wrong, because Lethal Company's doors SWING. An open door's
        // collider has rotated 90 degrees out of the frame, so its centre sits off to the SIDE
        // of the opening. Snapping to that would drag the line into the wall: the exact bug
        // we're fixing, but worse.
        //
        // So the door is used only as a HINT of where to look, and the NAVMESH decides where the
        // opening actually is. The walkable mesh is pinched at a doorway no matter which way the
        // leaf is swinging, and CenterInCorridor already finds the middle of that pinch using
        // NavMesh.Raycast. Once we have the centre, we lay down three collinear points through
        // it — before, centre, after — and pin them. A quadratic Bezier over collinear points IS
        // a straight line, so a pinned run simply cannot be curved through a doorframe.
        //
        // Note: NavMesh.Raycast, not Physics.Raycast. This queries the walkable mesh, not
        // collision geometry, so there is nothing here for BigDoor or structural props to
        // false-positive on. That's what sank the old wall validation; it doesn't apply.
        private const float DOORWAY_HINT_RADIUS = 3f;     // how near a door we bother looking
        // Half-length of the dead-straight run through an opening. Generous on purpose: it has
        // to cover the depth of the doorframe PLUS enough margin that the Bezier rounding at the
        // corner outside can't bulge sideways into the frame on an angled approach.
        private const float DOORWAY_STRAIGHT = 1.0f;     // half-length of the straight run

        // Original corners this close to an opening get dropped. Must comfortably exceed
        // DOORWAY_STRAIGHT, or a surviving jamb corner sits right where the straight run ends and
        // reintroduces a kink. Mansion doorframes are chunky, so this leans wide.
        private const float DOORWAY_CLEAR_RADIUS = 1.8f; // original corners this close get dropped
        private const float DOORWAY_MAX_SHIFT = 1.5f;    // furthest we'll trust a centring correction

        private struct DoorwayCrossing
        {
            public int seg;         // index of the path segment that passes through the opening
            public float t;         // how far along that segment (0..1)
            public Vector3 centre;  // middle of the opening, per the NavMesh
            public Vector3 dir;     // direction of travel through it
        }

        private List<Vector3> SnapThroughDoorways(List<Vector3> corners, out HashSet<int> pinned)
        {
            pinned = new HashSet<int>();
            if (corners == null || corners.Count < 2) return corners;

            // ---- 1. Find where the path passes through a doorway ----
            var crossings = new List<DoorwayCrossing>();

            // Cheap reject bounds: the path's XZ axis-aligned box, expanded by the hint radius.
            // Most doors in a level are nowhere near any given path, and without this every one of
            // them pays for a full per-segment scan on every update. One pass to build the box,
            // then a couple of float compares per door throws out the ones that can't matter.
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 c = corners[i];
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.z > maxZ) maxZ = c.z;
            }
            minX -= DOORWAY_HINT_RADIUS; maxX += DOORWAY_HINT_RADIUS;
            minZ -= DOORWAY_HINT_RADIUS; maxZ += DOORWAY_HINT_RADIUS;

            foreach (var entry in GetDoorCache())
            {
                if (entry.door == null) continue;

                // The door's TRANSFORM (its hinge) stays on the frame whether the leaf is open or
                // shut — unlike its collider, which swings away and would drag the line sideways
                // into the wall. Used purely as a hint of "there's an opening around here"; the
                // NavMesh decides where the opening actually is.
                Vector3 pivot = entry.door.transform.position;

                // Cheap reject: outside the path's bounding box, it can't be near any segment
                if (pivot.x < minX || pivot.x > maxX || pivot.z < minZ || pivot.z > maxZ) continue;

                int bestSeg = -1;
                float bestDist = float.MaxValue;
                Vector3 bestPoint = Vector3.zero;

                for (int i = 0; i < corners.Count - 1; i++)
                {
                    Vector3 p = ClosestPointOnSegment(pivot, corners[i], corners[i + 1]);
                    float d = Vector3.Distance(p, pivot);
                    if (d < bestDist) { bestDist = d; bestSeg = i; bestPoint = p; }
                }

                if (bestSeg < 0 || bestDist > DOORWAY_HINT_RADIUS) continue;

                Vector3 dir = corners[bestSeg + 1] - corners[bestSeg];
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) continue;
                dir.Normalize();

                // NavMesh.Raycast probes left and right to find the middle of the walkable pinch
                Vector3 centre = CenterInCorridor(bestPoint, dir);

                // A large correction means the probes found an open room, not a doorway pinch.
                // Don't trust it — yanking the line 3m sideways is how you get a hook.
                if (Vector3.Distance(centre, bestPoint) > DOORWAY_MAX_SHIFT) continue;

                // Never stack two runs on the same opening
                bool duplicate = false;
                foreach (var c in crossings)
                {
                    if (Vector3.Distance(c.centre, centre) < DOORWAY_CLEAR_RADIUS * 2f)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;

                Vector3 segVec = corners[bestSeg + 1] - corners[bestSeg];
                float segLen = segVec.magnitude;
                float tAlong = segLen > 0.001f
                    ? Mathf.Clamp01(Vector3.Dot(bestPoint - corners[bestSeg], segVec) / (segLen * segLen))
                    : 0f;

                crossings.Add(new DoorwayCrossing { seg = bestSeg, t = tAlong, centre = centre, dir = dir });
            }

            if (crossings.Count == 0) return corners;

            // Multiple openings on one segment must come out in travel order
            crossings.Sort((x, y) => x.seg != y.seg ? x.seg.CompareTo(y.seg) : x.t.CompareTo(y.t));

            // ---- 2. Rebuild the path ----
            //
            // The critical part. NavMesh corners HUG THE INSIDE OF THE DOORWAY JAMB — that's the
            // original cause of the line clipping the frame. An earlier version inserted centred
            // points but LEFT THOSE JAMB CORNERS IN, so the line zig-zagged between the two
            // competing sets: out to the jamb, back to the centre, out again. That produced the
            // curls and hooks.
            //
            // The centred run has to REPLACE the jamb corners, not sit alongside them. So any
            // original corner near an opening is dropped outright (never the first or last — those
            // are the player and the target).
            var result = new List<Vector3>();

            for (int i = 0; i < corners.Count; i++)
            {
                bool drop = false;

                if (i != 0 && i != corners.Count - 1)
                {
                    foreach (var c in crossings)
                    {
                        if (Vector3.Distance(corners[i], c.centre) < DOORWAY_CLEAR_RADIUS)
                        {
                            drop = true;
                            break;
                        }
                    }
                }

                if (!drop) result.Add(corners[i]);

                // Lay down any doorway runs belonging to the segment leaving corner i
                foreach (var c in crossings)
                {
                    if (c.seg != i) continue;

                    Vector3 pre = c.centre - c.dir * DOORWAY_STRAIGHT;
                    Vector3 post = c.centre + c.dir * DOORWAY_STRAIGHT;

                    Vector3 preOk, centreOk, postOk;
                    if (!SnapToMeshKeepingXZ(pre, out preOk)) continue;
                    if (!SnapToMeshKeepingXZ(c.centre, out centreOk)) continue;
                    if (!SnapToMeshKeepingXZ(post, out postOk)) continue;

                    // The approach into the opening must itself be walkable. Without this, dropping
                    // a jamb corner can leave a straight shot from the previous corner to the
                    // doorway that cuts through a wall. If it isn't clear, skip the snap and let
                    // the original (clipping but honest) corners stand.
                    if (result.Count > 0)
                    {
                        NavMeshHit blocked;
                        if (NavMesh.Raycast(result[result.Count - 1], preOk, out blocked, NavMesh.AllAreas))
                            continue;
                    }

                    pinned.Add(result.Count); result.Add(preOk);
                    pinned.Add(result.Count); result.Add(centreOk);
                    pinned.Add(result.Count); result.Add(postOk);
                }
            }

            return result;
        }

        // Confirms a point is on the NavMesh and fixes its height, but keeps its X/Z. Sampling
        // normally would return hit.position, which can slide the point sideways off the centre
        // of the doorway — the one thing we're trying to preserve.
        private bool SnapToMeshKeepingXZ(Vector3 p, out Vector3 snapped)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(p, out hit, 1f, NavMesh.AllAreas))
            {
                snapped = new Vector3(p.x, hit.position.y, p.z);
                return true;
            }
            snapped = p;
            return false;
        }

        // Searches for the closest walkable point to a target that the player can ACTUALLY reach,
        // for cases where the target itself sits on a disconnected NavMesh island (a real gap
        // between the mesh and door geometry — not a small seam). Unlike a plain SamplePosition
        // widen, every candidate here is validated with a real CalculatePath before being
        // accepted, so this can't return a point that merely looks close but isn't connected.
        private bool FindNearestReachablePoint(Vector3 from, Vector3 target, out Vector3 result)
        {
            result = Vector3.zero;
            float[] radii = { 2f, 4f, 6f, 9f, 13f };
            const int ringSamples = 16;

            foreach (float r in radii)
            {
                for (int i = 0; i < ringSamples; i++)
                {
                    float angle = i * (360f / ringSamples) * Mathf.Deg2Rad;
                    Vector3 probe = target + new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);

                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(probe, out hit, 2f, NavMesh.AllAreas)) continue;
                    if (Mathf.Abs(hit.position.y - target.y) > 4f) continue; // stay on the same floor

                    if (IsPathComplete(from, hit.position))
                    {
                        result = hit.position;
                        return true;
                    }
                }
            }
            return false;
        }

        private List<Vector3> SmoothPath(Vector3[] corners)
        {
            return SmoothPath(new List<Vector3>(corners), null);
        }

        private List<Vector3> SmoothPath(List<Vector3> corners, HashSet<int> pinned)
        {
            var result = new List<Vector3>();
            if (corners.Count < 2) { result.AddRange(corners); return result; }

            // Maximum corner rounding, in metres.
            //
            // This used to round each corner from the MIDPOINT of the adjacent segments
            // (Lerp 0.5), which makes the rounding radius scale with segment length. On a long
            // straight approach to a doorway that means the curve begins several metres out and
            // cuts the corner clean through the wall beside the door instead of passing through
            // the opening. Cap it: corners get rounded tightly and the line stays in the doorway.
            const float maxRound = 0.9f;

            result.Add(corners[0]);

            for (int i = 1; i < corners.Count - 1; i++)
            {
                // Doorway points are pinned: emitted exactly as-is, never rounded. Three
                // collinear pinned points give a dead-straight run through the opening, which
                // is the entire point — a curve cannot clip a doorframe it never enters.
                if (pinned != null && pinned.Contains(i))
                {
                    result.Add(corners[i]);
                    continue;
                }

                Vector3 toPrev = corners[i - 1] - corners[i];
                Vector3 toNext = corners[i + 1] - corners[i];

                float prevLen = toPrev.magnitude;
                float nextLen = toNext.magnitude;

                if (prevLen < 0.001f || nextLen < 0.001f)
                {
                    result.Add(corners[i]);
                    continue;
                }

                // Never round more than half a segment (which would overshoot the next corner),
                // and never more than maxRound regardless of how long the segment is.
                float r0 = Mathf.Min(maxRound, prevLen * 0.5f);
                float r1 = Mathf.Min(maxRound, nextLen * 0.5f);

                Vector3 p0 = corners[i] + (toPrev / prevLen) * r0;
                Vector3 p2 = corners[i] + (toNext / nextLen) * r1;

                for (int s = 0; s <= bezierSteps; s++)
                {
                    float t = s / (float)bezierSteps;
                    float u = 1f - t;
                    result.Add(u * u * p0 + 2f * u * t * corners[i] + t * t * p2);
                }
            }

            result.Add(corners[corners.Count - 1]);
            return result;
        }

        private void ClearLineRoots()
        {
            if (mainEntranceRoot != null) { GameObject.Destroy(mainEntranceRoot); mainEntranceRoot = null; }
            mainEntranceSegments.Clear();
            mainEntranceDiamonds.Clear();
            mainEntranceShapes.Clear();
            mainEntranceMarker = null; // child of root, destroyed with it

            foreach (var root in fireExitRoots)
                if (root != null) GameObject.Destroy(root);

            fireExitRoots.Clear();
            fireExitSegmentSets.Clear();
            fireExitDiamondSets.Clear();
            fireExitShapeSets.Clear();
            fireExitMarkers.Clear(); // children of roots, destroyed with them
        }

        private void ClearAll()
        {
            ClearLineRoots();
            ClearCompassOverlay();
            mainEntranceTarget = null;
            mineshaftBottomTarget = null;
            mineshaftTopTarget = null;
            fireExitTargets.Clear();

            // Cached door-sim answers are keyed to this level's exits — drop them
            doorSimCache.Clear();
            doorSimMisses.Clear();
            doorSimTimer = 999f;

            // NOTE: the region graph is deliberately NOT reset here. ClearAll() fires whenever
            // navigation is toggled off or the player steps outside, and the graph is a property
            // of the LEVEL, not of whether the mod is currently drawing. Keeping it means
            // re-entering the facility is instant instead of costing another rebuild.
            // MaintainDoorGraph() resets it on its own once the dungeon unloads.
        }
    }
}