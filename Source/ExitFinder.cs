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
        private List<Transform> fireExitTargets = new List<Transform>();

        // Linear mode rendering
        private List<LineRenderer> mainEntranceSegments = new List<LineRenderer>();
        private List<LineRenderer> mainEntranceDiamonds = new List<LineRenderer>();
        private List<GameObject> mainEntranceShapes = new List<GameObject>();
        private GameObject mainEntranceRoot = null;

        private List<List<LineRenderer>> fireExitSegmentSets = new List<List<LineRenderer>>();
        private List<List<LineRenderer>> fireExitDiamondSets = new List<List<LineRenderer>>();
        private List<List<GameObject>> fireExitShapeSets = new List<List<GameObject>>();
        private List<GameObject> fireExitRoots = new List<GameObject>();

        // Compass mode rendering
        private GameObject compassOverlayRoot = null;
        private RectTransform mainEntrancePip = null;
        private RectTransform fireExitPip = null;

        private float updateInterval = 0.1f;
        private float updateTimer = 999f;
        private float pulseTimer = 0f; // accumulates deltaTime, drives locked-door pulse
        private bool wasInsideFactory = false;

        private Vector3 smoothedPlayerPos = Vector3.zero;
        private bool smoothedPosInitialized = false;
        private float smoothSpeed = 8f;
        private int bezierSteps = 12;

        private NavigationMode lastNavMode = NavigationMode.LinearMode;

        // Compass overlay constants matching game HUD
        private const string COMPASS_PATH = "Systems/UI/Canvas/IngamePlayerHUD";
        private const float COMPASS_WIDTH = 500f;
        //        private const float COMPASS_WIDTH = 359.56f;
        private const float COMPASS_HEIGHT_UI = 36.39f;
        private const float COMPASS_ANCHOR_X = 0f;
        private const float COMPASS_ANCHOR_Y = -28.0f;
        private const float COMPASS_FOV = 240f; // degrees visible in strip
                                                //       private const float COMPASS_FOV = 180f; // degrees visible in strip
        private const float COMPASS_VERTICAL_OFFSET = 12f; // how far above native compass to render pips
        private const float COMPASS_FADE_ZONE = 0.15f; // fraction of edge that fades (0.15 = last 15% of each side)
        private const float COMPASS_HIDE_MARGIN = 5f; // degrees past FOV edge before pip hides entirely

        // Returned by GetPath - carries both the point list and whether it's a locked-door fallback
        private struct PathResult
        {
            public List<Vector3> Points;
            public bool IsLockedDoor;
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

            if (!isActive) return;

            pulseTimer += deltaTime;

            updateTimer += deltaTime;
            if (updateTimer >= updateInterval)
            {
                updateTimer = 0f;
                if (Plugin.NavMode.Value == NavigationMode.CompassMode)
                    UpdateCompassOverlay();
                else
                    UpdatePaths();
            }
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

            foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
            {
                if (obj.name.Contains("EntranceTeleportA") && obj.name.Contains("Clone"))
                {
                    mainEntranceTarget = obj.transform;
                    Plugin.Logger.LogInfo($"LeadMeOut: Main entrance at {obj.transform.position}");
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
            // Position our overlay ABOVE the game's native compass strip.
            // COMPASS_VERTICAL_OFFSET controls how high above the native compass the pips sit.
            rootRt.anchoredPosition = new Vector2(COMPASS_ANCHOR_X, COMPASS_ANCHOR_Y + COMPASS_HEIGHT_UI + COMPASS_VERTICAL_OFFSET);
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

            // Use Image - same as working test box
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

            // Use camera container rotation for accurate look direction
            Transform camContainer = player.transform.Find("ScavengerModel/metarig/CameraContainer");
            float yRot = camContainer != null ? camContainer.eulerAngles.y : player.transform.eulerAngles.y;
            Vector3 forward = new Vector3(Mathf.Sin(yRot * Mathf.Deg2Rad), 0f, Mathf.Cos(yRot * Mathf.Deg2Rad));

            var showLines = Plugin.ShowLines.Value;
            Plugin.Logger.LogDebug($"LeadMeOut: UpdateCompass - forward={forward}, mainPip={mainEntrancePip != null}, firePip={fireExitPip != null}, mainTarget={mainEntranceTarget != null}, fireCount={fireExitTargets.Count}, showLines={Plugin.ShowLines.Value}");

            // Update main entrance pip
            if (mainEntrancePip != null && mainEntranceTarget != null)
            {
                mainEntrancePip.gameObject.SetActive(showLines != ShowLinesPreset.FireExitsOnly);
                if (showLines != ShowLinesPreset.FireExitsOnly)
                {
                    Color mainColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.MainEntranceColorPreset.Value, Plugin.MainEntranceCustomColor.Value, Color.green));
                    float mainWidth = Plugin.ResolvePipWidth(Plugin.MainEntranceLineWidth.Value);
                    mainEntrancePip.sizeDelta = new Vector2(mainWidth, mainEntrancePip.sizeDelta.y);
                    UpdatePipPosition(mainEntrancePip, player.transform.position, mainEntranceTarget.position, forward, mainColor);
                }
            }

            // Update fire exit pip
            if (fireExitPip != null && fireExitTargets.Count > 0)
            {
                fireExitPip.gameObject.SetActive(showLines != ShowLinesPreset.MainEntranceOnly);
                if (showLines != ShowLinesPreset.MainEntranceOnly)
                {
                    Color fireColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.FireExitColorPreset.Value, Plugin.FireExitCustomColor.Value, Color.red));
                    float fireWidth = Plugin.ResolvePipWidth(Plugin.FireExitLineWidth.Value);
                    fireExitPip.sizeDelta = new Vector2(fireWidth, fireExitPip.sizeDelta.y);
                    UpdatePipPosition(fireExitPip, player.transform.position, fireExitTargets[0].position, forward, fireColor);
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

                // Hide entirely when target is behind player past the FOV + margin
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

                // Fade alpha based on how close pip is to the FOV edge
                float alpha = 1f;
                float normalized = absAngle / halfFov; // 0 = center, 1 = edge, >1 = past edge
                float fadeStart = 1f - COMPASS_FADE_ZONE;
                if (normalized > fadeStart)
                {
                    alpha = Mathf.InverseLerp(1f, fadeStart, normalized);
                    alpha = Mathf.Clamp01(alpha);
                }

                Plugin.Logger.LogDebug($"LeadMeOut: PipPos angle={angle:F1} xPos={xPos:F1} alpha={alpha:F2} active={pip.gameObject.activeSelf}");

                // Update color on Image with fade alpha
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

        private void UpdatePaths()
        {
            if (!smoothedPosInitialized) return;

            var showLines = Plugin.ShowLines.Value;

            Color mainColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.MainEntranceColorPreset.Value, Plugin.MainEntranceCustomColor.Value, Color.green));
            LineStyle mainStyle = Plugin.MainEntranceLineStyle.Value;
            float mainWidth = Plugin.ResolveWidth(Plugin.MainEntranceLineWidth.Value);

            Color fireColor = Plugin.ApplyBrightness(Plugin.ResolveColor(Plugin.FireExitColorPreset.Value, Plugin.FireExitCustomColor.Value, Color.red));
            LineStyle fireStyle = Plugin.FireExitLineStyle.Value;
            float fireWidth = Plugin.ResolveWidth(Plugin.FireExitLineWidth.Value);

            float lateralOffset = 0.18f;

            if (mainEntranceRoot != null && mainEntranceTarget != null && showLines != ShowLinesPreset.FireExitsOnly)
            {
                mainEntranceRoot.SetActive(true);
                PathResult? result = GetPath(smoothedPlayerPos, mainEntranceTarget.position);
                if (result.HasValue)
                    RenderPath(mainEntranceRoot, mainEntranceSegments, mainEntranceDiamonds, mainEntranceShapes,
                        result.Value.Points, mainColor, mainStyle, mainWidth, 0f, 0f,
                        mainEntranceTarget.position, result.Value.IsLockedDoor);
                else
                {
                    HideSegments(mainEntranceSegments);
                    HideShapes(mainEntranceShapes);
                }
            }
            else if (mainEntranceRoot != null)
                mainEntranceRoot.SetActive(false);

            if (showLines != ShowLinesPreset.MainEntranceOnly)
            {
                for (int i = 0; i < fireExitTargets.Count; i++)
                {
                    if (i >= fireExitRoots.Count) break;

                    fireExitRoots[i].SetActive(true);
                    float exitLateralOffset = lateralOffset * (i + 1);
                    Color exitColor = i == 0 ? fireColor : DarkenColor(fireColor, 0.15f * i);

                    PathResult? result = GetPath(smoothedPlayerPos, fireExitTargets[i].position);
                    if (result.HasValue)
                        RenderPath(fireExitRoots[i], fireExitSegmentSets[i], fireExitDiamondSets[i], fireExitShapeSets[i],
                            result.Value.Points, exitColor, fireStyle, fireWidth, exitLateralOffset, 0f,
                            fireExitTargets[i].position, result.Value.IsLockedDoor);
                    else
                    {
                        HideSegments(fireExitSegmentSets[i]);
                        HideShapes(fireExitShapeSets[i]);
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

        private PathResult? GetPath(Vector3 from, Vector3 to)
        {
            NavMeshHit fromHit, toHit;
            bool fromValid = NavMesh.SamplePosition(from, out fromHit, 15f, NavMesh.AllAreas);
            bool toValid = NavMesh.SamplePosition(to, out toHit, 15f, NavMesh.AllAreas);

            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 30f, NavMesh.AllAreas);
            if (!toValid) toValid = NavMesh.SamplePosition(to, out toHit, 50f, NavMesh.AllAreas);

            if (!fromValid || !toValid) return null;

            NavMeshPath path = new NavMeshPath();
            NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path);

            bool isLockedDoor = path.status != NavMeshPathStatus.PathComplete;

            List<Vector3> corners;

            if (path.corners.Length >= 2)
            {
                // Use whatever corners we got (full path or partial)
                corners = new List<Vector3>(path.corners);
                corners[0] = from;

                if (isLockedDoor)
                {
                    // Append a straight line to the actual destination so the path
                    // continues through the locked door rather than stopping dead
                    corners.Add(to);
                }
            }
            else
            {
                // NavMesh gave us nothing at all — draw a straight line so the
                // player at least has a direction and the pulse warns them why
                corners = new List<Vector3> { from, to };
                isLockedDoor = true;
            }

            List<Vector3> smoothed = SmoothPath(CenterCorners(corners.ToArray()));

            for (int i = 0; i < smoothed.Count; i++)
                smoothed[i] = smoothed[i] + Vector3.up * 0.1f;

            float maxDist = Plugin.ResolveRenderDistance(Plugin.RenderDistance.Value);
            if (maxDist < float.MaxValue && !isLockedDoor)
                smoothed = CullByDistance(smoothed, from, maxDist);

            if (smoothed.Count < 2) return null;

            return new PathResult { Points = smoothed, IsLockedDoor = isLockedDoor };
        }

        private List<Vector3> CullByDistance(List<Vector3> points, Vector3 origin, float maxDist)
        {
            var result = new List<Vector3>();
            float accum = 0f;
            result.Add(points[0]);

            for (int i = 1; i < points.Count; i++)
            {
                accum += Vector3.Distance(points[i - 1], points[i]);
                if (accum > maxDist)
                {
                    float overshoot = accum - maxDist;
                    float segLen = Vector3.Distance(points[i - 1], points[i]);
                    float t = 1f - (overshoot / segLen);
                    result.Add(Vector3.Lerp(points[i - 1], points[i], t));
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
            // Pulse brightness when the path goes through a locked door
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
            // Two triangles, both sides so it renders from above or below
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private Mesh BuildPawprintMesh()
        {
            // Pawprint = 1 main pad (heel) + 4 toe pads. Points forward along +Z.
            Mesh mesh = new Mesh();
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            int circleSegments = 12;

            // Helper: add a flat ellipse at (cx, cz) with radii (rx, rz)
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
                    // Both winding orders so the pad is visible from either side
                    tris.Add(centerIdx); tris.Add(a); tris.Add(b);
                    tris.Add(centerIdx); tris.Add(b); tris.Add(a);
                }
            }

            // Heel pad (large, behind)
            AddEllipse(0f, -0.15f, 0.22f, 0.22f);
            // Front-center toes (two side-by-side, slightly ahead)
            AddEllipse(-0.10f, 0.22f, 0.09f, 0.11f);
            AddEllipse(0.10f, 0.22f, 0.09f, 0.11f);
            // Outer toes (flanking, slightly lower than front toes)
            AddEllipse(-0.24f, 0.08f, 0.08f, 0.10f);
            AddEllipse(0.24f, 0.08f, 0.08f, 0.10f);

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

        private Vector3[] CenterCorners(Vector3[] corners)
        {
            if (corners.Length < 3) return corners;

            var result = new Vector3[corners.Length];
            result[0] = corners[0];
            result[corners.Length - 1] = corners[corners.Length - 1];

            for (int i = 1; i < corners.Length - 1; i++)
            {
                // Forward direction at this corner = prev-to-next, flattened to XZ
                Vector3 forward = (corners[i + 1] - corners[i - 1]);
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f) { result[i] = corners[i]; continue; }
                forward.Normalize();

                // Skip centering on stairs/ramps — horizontal probes give wrong results
                // when the path is climbing or descending vertically
                float yDelta = Mathf.Abs(corners[i + 1].y - corners[i - 1].y);
                if (yDelta > 0.75f) { result[i] = corners[i]; continue; }

                result[i] = CenterInCorridor(corners[i], forward);
            }
            return result;
        }

        // Probes NavMesh left and right of a point perpendicular to the path,
        // then shifts the point to the midpoint between the two wall boundaries.
        private Vector3 CenterInCorridor(Vector3 point, Vector3 forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            const float probeLength = 3f; // max half-width to probe; covers most doorways

            NavMeshHit rightHit, leftHit;
            float rightDist = probeLength;
            float leftDist = probeLength;

            if (NavMesh.Raycast(point, point + right * probeLength, out rightHit, NavMesh.AllAreas))
                rightDist = rightHit.distance;
            if (NavMesh.Raycast(point, point - right * probeLength, out leftHit, NavMesh.AllAreas))
                leftDist = leftHit.distance;

            // Positive shift = move right, negative = move left
            float shift = (rightDist - leftDist) * 0.5f;
            Vector3 centered = point + right * shift;

            // Re-snap to NavMesh surface in case the shift drifted off-mesh
            NavMeshHit snap;
            if (NavMesh.SamplePosition(centered, out snap, 0.5f, NavMesh.AllAreas))
                return snap.position;

            return point; // if snap fails, keep original
        }

        private List<Vector3> SmoothPath(Vector3[] corners)
        {
            var result = new List<Vector3>();
            if (corners.Length < 2) { result.AddRange(corners); return result; }

            result.Add(corners[0]);
            for (int i = 1; i < corners.Length - 1; i++)
            {
                Vector3 p0 = Vector3.Lerp(corners[i], corners[i - 1], 0.5f);
                Vector3 p2 = Vector3.Lerp(corners[i], corners[i + 1], 0.5f);
                for (int s = 0; s <= bezierSteps; s++)
                {
                    float t = s / (float)bezierSteps;
                    float u = 1f - t;
                    result.Add(u * u * p0 + 2f * u * t * corners[i] + t * t * p2);
                }
            }
            result.Add(corners[corners.Length - 1]);
            return result;
        }

        private void ClearLineRoots()
        {
            if (mainEntranceRoot != null) { GameObject.Destroy(mainEntranceRoot); mainEntranceRoot = null; }
            mainEntranceSegments.Clear();
            mainEntranceDiamonds.Clear();
            mainEntranceShapes.Clear();

            foreach (var root in fireExitRoots)
                if (root != null) GameObject.Destroy(root);

            fireExitRoots.Clear();
            fireExitSegmentSets.Clear();
            fireExitDiamondSets.Clear();
            fireExitShapeSets.Clear();
        }

        private void ClearAll()
        {
            ClearLineRoots();
            ClearCompassOverlay();
            mainEntranceTarget = null;
            fireExitTargets.Clear();
        }
    }
}