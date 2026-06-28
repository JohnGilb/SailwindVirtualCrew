using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class PilotingWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(440, 20, 470, 800);
        private static readonly int windowId = "VirtualCrewPilotWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "PilotingWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 440f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        public PilotingSaveData GetPilotingSaveData()
        {
            return new PilotingSaveData
            {
                autopilotEngaged = autopilotEngaged,
                hasPlayerSelection = hasPlayerSelection,
                holdWindAngle = holdWindAngle,
                playerSelectedHeading = playerSelectedHeading,
                playerSelectedWindAngle = playerSelectedWindAngle
            };
        }

        public void RestorePilotingSaveData(PilotingSaveData data)
        {
            if (data == null || !data.hasPlayerSelection)
            {
                ResetPilotingOrder();
                return;
            }

            playerSelectedHeading = PilotController.Normalize(data.playerSelectedHeading);
            playerSelectedWindAngle = Mathf.Clamp(data.playerSelectedWindAngle, -179f, 179f);
            hasPlayerSelection = true;
            holdWindAngle = data.holdWindAngle;
            autopilotEngaged = data.autopilotEngaged;
            restoredOrderPending = true;
            restoredAutopilotPending = autopilotEngaged;
            pilotHeadingError = ComputePilotError();

            if (holdWindAngle)
                UpdateWindAngleTarget(updateOnly: false);
            else
                controller.SetTarget(playerSelectedHeading + pilotHeadingError);
        }

        private readonly PilotController controller = new PilotController();

        // Helm components
        private GPButtonSteeringWheel steeringWheel;
        private float currentInputMax;
        private bool  autopilotEngaged   = false;
        private float helmSearchCooldown = 0f;
        private PilotTask observedPilotTask;

        // Player order vs pilot's best attempt
        private float playerSelectedHeading;
        private float playerSelectedWindAngle;
        private float pilotHeadingError;
        private bool  hasPlayerSelection = false;
        private bool  holdWindAngle = false;
        private bool  restoredOrderPending = false;
        private bool  restoredAutopilotPending = false;
        private Transform headingBoat;
        private bool hasFilteredHeading = false;
        private float filteredHeading = 0f;
        private int headingFrame = -1;
        private float cachedHeading = 0f;

        // Compass circle
        private const int CircleRadius = 100;
        private const int CircleSize   = CircleRadius * 2 + 1;  // 201 px
        private Texture2D circleRingTex;
        private Texture2D orderedDotTex;  // green  = player's ordered heading
        private Texture2D goalDotTex;     // yellow = pilot's helm (best attempt)
        private Texture2D currentDotTex;  // white  = actual current heading
        private Texture2D windDotTex;     // cyan   = apparent wind direction
        private Texture2D windLineTex;

        // PID values
        private float kp = 0.1f;
        private float ki = 0.0f;
        private float kd = 0.05f;

        // History graph
        private const int   GraphHeight    = 100;
        private const float GraphOutputMax = 50f;
        private const float HeadingFilterSeconds = 0.25f;
        private Texture2D goalGraphTex;
        private Texture2D currentGraphTex;
        private Texture2D outputGraphTex;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            SyncActivePilotTask();

            // Helm search runs regardless of window visibility.
            helmSearchCooldown -= Time.deltaTime;
            if (steeringWheel == null && helmSearchCooldown <= 0f)
            {
                FindHelmComponents();
                helmSearchCooldown = 2f;
            }

            if (!autopilotEngaged) return;

            if (steeringWheel == null)
            {
                if (restoredAutopilotPending)
                    return;

                autopilotEngaged = false;
                return;
            }

            restoredAutopilotPending = false;

            if (holdWindAngle)
                UpdateWindAngleTarget(updateOnly: true);

            if (!controller.TargetHeading.HasValue)
            {
                autopilotEngaged = false;
                return;
            }

            // Disengage if the player grabs the wheel.
            if (steeringWheel.IsCliked() || steeringWheel.IsStickyClicked())
            {
                ReleaseWheel();
                autopilotEngaged = false;
                return;
            }

            float output  = controller.Tick(GetCurrentHeading(), Time.deltaTime);
            float command = Mathf.Clamp(output, -1f, 1f);

            if (!(bool)Traverse.Create(steeringWheel).Field("locked").GetValue())
                Traverse.Create(steeringWheel).Field("locked").SetValue(true);

            steeringWheel.currentInput = currentInputMax * command;
        }

        private void FindHelmComponents()
        {
            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!worldBoat) return;

            var rudder = worldBoat.GetComponentInChildren<Rudder>();
            if (rudder == null) return;
            foreach (var wheel in FindObjectsOfType<GPButtonSteeringWheel>())
            {
                var r = Traverse.Create(wheel).Field("rudder").GetValue<Rudder>();
                if (r != rudder) continue;
                steeringWheel   = wheel;
                currentInputMax = wheel.attachedRudder.limits.max * wheel.gearRatio;
                return;
            }
        }

        private void ReleaseWheel()
        {
            if (steeringWheel != null)
                Traverse.Create(steeringWheel).Field("locked").SetValue(false);
        }

        private void SyncActivePilotTask()
        {
            var manager = VirtualCrewManager.Instance;
            PilotTask activePilotTask = manager.ActivePilotTask;
            if (observedPilotTask == activePilotTask)
                return;

            observedPilotTask = activePilotTask;
            bool preserveOrder = manager.ShouldPreservePilotOrderForShiftHandoff(activePilotTask)
                && autopilotEngaged
                && hasPlayerSelection;
            if (!preserveOrder && restoredOrderPending && hasPlayerSelection && activePilotTask != null)
                preserveOrder = true;
            restoredOrderPending = false;
            manager.ClearPilotShiftHandoff(activePilotTask);

            if (preserveOrder)
            {
                RetargetPilotOrderForNewPilot();
                return;
            }

            ResetPilotingOrder();
        }

        private void RetargetPilotOrderForNewPilot()
        {
            pilotHeadingError = ComputePilotError();
            if (holdWindAngle)
                UpdateWindAngleTarget(updateOnly: false);
            else
                controller.SetTarget(playerSelectedHeading + pilotHeadingError);
        }

        private void ResetPilotingOrder()
        {
            controller.ClearTarget();
            hasPlayerSelection = false;
            holdWindAngle = false;
            restoredOrderPending = false;
            restoredAutopilotPending = false;
            pilotHeadingError = 0f;

            if (autopilotEngaged)
            {
                ReleaseWheel();
                autopilotEngaged = false;
            }
        }

        private float GetCurrentHeading()
        {
            if (headingFrame == Time.frameCount)
                return cachedHeading;

            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!worldBoat)
            {
                ResetHeadingFilter();
                headingFrame = Time.frameCount;
                cachedHeading = 0f;
                return cachedHeading;
            }

            if (headingBoat != worldBoat)
            {
                ResetHeadingFilter();
                headingBoat = worldBoat;
            }

            float raw;
            if (!TryHeadingFromForward(worldBoat.transform.forward, out raw))
                raw = hasFilteredHeading ? filteredHeading : 0f;

            if (!hasFilteredHeading)
            {
                filteredHeading = raw;
                hasFilteredHeading = true;
            }
            else
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / HeadingFilterSeconds);
                filteredHeading = Mathf.LerpAngle(filteredHeading, raw, alpha);
            }

            headingFrame = Time.frameCount;
            cachedHeading = PilotController.Normalize(filteredHeading);
            return cachedHeading;
        }

        private float GetCompassHeading()
        {
            return PilotController.Normalize(GetCurrentHeading() + 90f);
        }

        private float GetApparentWindAngle()
        {
            return WindAngleUtils.TryGetApparentWindAngle(out float angle) ? angle : 0f;
        }

        private bool TryGetSailInfoApparentWind(out Vector3 apparentWind)
        {
            return WindAngleUtils.TryGetSailInfoApparentWind(out apparentWind);
        }

        private Transform GetSailInfoBoatTransform()
        {
            return WindAngleUtils.GetSailInfoBoatTransform();
        }

        private Rigidbody GetSailInfoBoatRigidbody(Transform boat)
        {
            return WindAngleUtils.GetSailInfoBoatRigidbody(boat);
        }

        private static string Cardinal(float heading)
        {
            string[] dirs =
            {
                "N", "NNE", "NE", "ENE",
                "E", "ESE", "SE", "SSE",
                "S", "SSW", "SW", "WSW",
                "W", "WNW", "NW", "NNW"
            };
            int index = Mathf.RoundToInt(heading / 22.5f) % dirs.Length;
            return dirs[index];
        }

        private static string FormatWindAngle(float angle)
        {
            if (Mathf.Abs(angle) < 0.5f) return "000.0 Ahead";
            return $"{Mathf.Abs(angle):000.0} {(angle > 0f ? "Stbd" : "Port")}";
        }

        private static string FormatWindAngleCoarse(float angle)
        {
            return WindAngleUtils.FormatWindAngleCoarse(angle);
        }

        private static string FormatTarget(float heading, float windAngle, bool windHold)
        {
            if (DeveloperMode.IsEnabled)
                return windHold ? FormatWindAngle(windAngle) : $"{heading:000.0}";

            return windHold ? FormatWindAngleCoarse(windAngle) : Cardinal(heading + 90f);
        }

        // Stores the player's ordered heading and asks the pilot to steer their best attempt.
        private void SetPlayerTarget(float heading)
        {
            playerSelectedHeading = PilotController.Normalize(heading);
            hasPlayerSelection    = true;
            holdWindAngle         = false;
            pilotHeadingError     = ComputePilotError();
            controller.SetTarget(playerSelectedHeading + pilotHeadingError);
        }

        private void SetWindAngleTarget(float angle)
        {
            playerSelectedWindAngle = Mathf.Clamp(angle, -179f, 179f);
            hasPlayerSelection      = true;
            holdWindAngle           = true;
            pilotHeadingError       = ComputePilotError();
            UpdateWindAngleTarget(updateOnly: false);
        }

        private void AdjustPlayerTarget(float delta)
        {
            if (!hasPlayerSelection) return;
            if (holdWindAngle)
            {
                playerSelectedWindAngle = Mathf.Clamp(playerSelectedWindAngle + delta, -179f, 179f);
                UpdateWindAngleTarget(updateOnly: false);
            }
            else
            {
                playerSelectedHeading = PilotController.Normalize(playerSelectedHeading + delta);
                pilotHeadingError = ComputePilotError();
                controller.SetTarget(playerSelectedHeading + pilotHeadingError);
            }
        }

        private void AdjustWindAngleMagnitude(float delta)
        {
            float sign = playerSelectedWindAngle < 0f ? -1f : 1f;
            float magnitude = Mathf.Clamp(Mathf.Abs(playerSelectedWindAngle) + delta, 20f, 170f);
            SetWindAngleTarget(sign * magnitude);
        }

        private void UpdateWindAngleTarget(bool updateOnly)
        {
            if (!TryGetSailInfoApparentWind(out Vector3 apparentWind)) return;

            // SailInfo reports AWA as SignedAngle(-boat.forward, apparentWind, up).
            // Solve that equation directly for boat.forward:
            //   apparentWind = Rotate(desiredAwa) * -forward
            //   forward = -Rotate(-desiredAwa) * apparentWind
            Vector3 targetForward = -(Quaternion.AngleAxis(-playerSelectedWindAngle, Vector3.up) * apparentWind.normalized);
            targetForward.y = 0f;
            if (targetForward.sqrMagnitude < 0.001f) return;

            Transform sailInfoBoat = GetSailInfoBoatTransform();
            Transform worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (sailInfoBoat != null && worldBoat != null)
            {
                float sailInfoHeading;
                float worldHeading;
                if (!TryHeadingFromForward(sailInfoBoat.forward, out sailInfoHeading)
                    || !TryHeadingFromForward(worldBoat.transform.forward, out worldHeading))
                    return;

                float headingDelta = Mathf.DeltaAngle(sailInfoHeading, worldHeading);
                targetForward = Quaternion.AngleAxis(headingDelta, Vector3.up) * targetForward;
                targetForward.y = 0f;
                if (targetForward.sqrMagnitude < 0.001f) return;
            }

            playerSelectedHeading = PilotController.Normalize(
                HeadingFromHorizontalForward(targetForward.normalized));

            float helmHeading = PilotController.Normalize(playerSelectedHeading + pilotHeadingError);
            if (updateOnly)
                controller.UpdateTarget(helmHeading);
            else
                controller.SetTarget(helmHeading);
        }

        // Returns a random heading error based on the pilot's Intelligence.
        // Range = +/- (7 - Intelligence): lower Intelligence → larger potential error.
        private float ComputePilotError()
        {
            var pilot = VirtualCrewManager.Instance.Pilot;
            if (pilot == null) return 0f;
            float range = Mathf.Max(0f, (6f - pilot.Intelligence) * 2f);
            return Random.Range(-range, range);
        }

        private static bool TryHeadingFromForward(Vector3 forward, out float heading)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
            {
                heading = 0f;
                return false;
            }

            heading = HeadingFromHorizontalForward(forward.normalized);
            return true;
        }

        private static float HeadingFromHorizontalForward(Vector3 forward)
        {
            return PilotController.Normalize(
                Vector3.SignedAngle(forward, Vector3.forward, -Vector3.up));
        }

        private void ResetHeadingFilter()
        {
            headingBoat = null;
            hasFilteredHeading = false;
            headingFrame = -1;
            cachedHeading = 0f;
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();
            if (_resizer.UserHeight > 0f) windowRect.height = _resizer.UserHeight;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Piloting");
        }

        private void DrawWindow(int id)
        {
            SyncActivePilotTask();

            // Suppress Tab so the game's free-look binding doesn't fire.
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            EnsureTextures();

            // ── Pilot assignment ───────────────────────────────────────────
            var activePilotTask = VirtualCrewManager.Instance.ActivePilotTask;
            if (activePilotTask == null)
            {
                var freshest = VirtualCrewManager.Instance.FreshestCrewman(ShipRole.Pilot);
                GUI.enabled = freshest != null;
                if (GUILayout.Button(freshest != null ? $"Assign freshest Pilot ({freshest.Name})" : "Assign freshest Pilot"))
                    VirtualCrewManager.Instance.StartPilot(freshest);
                GUI.enabled = true;
                _resizer.HandleInWindow(ref windowRect);
                GUI.DragWindow();
                return;
            }

            GUILayout.Label($"Pilot: {activePilotTask.AssignedCrewman.Name}  [{activePilotTask.AssignedCrewman.FatigueTag}]");

            float  currentHeading = GetCurrentHeading();
            float  compassHeading = GetCompassHeading();
            float  apparentWindAngle = GetApparentWindAngle();
            float? helmTarget     = controller.TargetHeading;

            // ── Compass circle ─────────────────────────────────────────────
            // Game convention: 0=East (screen right), 90=South (screen down), 270=North (screen up).
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Rect circ = GUILayoutUtility.GetRect(CircleSize, CircleSize,
                GUILayout.Width(CircleSize), GUILayout.Height(CircleSize));
            GUI.DrawTexture(circ, circleRingTex);

            float cx = circ.x + CircleRadius, cy = circ.y + CircleRadius;
            GUI.Label(new Rect(cx - 5,         circ.y + 3,     12, 14), "N");
            GUI.Label(new Rect(circ.xMax - 14, cy - 7,         12, 14), "E");
            GUI.Label(new Rect(cx - 5,         circ.yMax - 16, 12, 14), "S");
            GUI.Label(new Rect(circ.x + 2,     cy - 7,         12, 14), "W");

            // Draw order: current (white) first, then ordered (green), then helm (yellow) on top.
            if (DeveloperMode.IsEnabled)
                DrawCompassDot(circ, currentHeading, currentDotTex);
            if (hasPlayerSelection)
                DrawCompassDot(circ, playerSelectedHeading, orderedDotTex);
            if (DeveloperMode.IsEnabled && helmTarget.HasValue)
                DrawCompassDot(circ, helmTarget.Value, goalDotTex);
            DrawCompassLine(circ, currentHeading + apparentWindAngle, windLineTex);
            DrawCompassDot(circ, currentHeading + apparentWindAngle, windDotTex);
            DrawCompassDot(circ, currentHeading, currentDotTex);

            // Click inside the ring to set a target heading.
            if (Event.current.type == EventType.MouseDown && circ.Contains(Event.current.mousePosition))
            {
                float dx = Event.current.mousePosition.x - cx;
                float dy = Event.current.mousePosition.y - cy;
                if (dx * dx + dy * dy <= (float)(CircleRadius * CircleRadius))
                {
                    // atan2(dy, dx): right=0°, down=90°, left=180°, up=-90° → matches game convention.
                    SetPlayerTarget(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
                    Event.current.Use();
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ── Navigation readouts ────────────────────────────────────────
            GUIStyle noWrapLabel = new GUIStyle(GUI.skin.label)
            {
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

            GUILayout.BeginHorizontal();
            GUILayout.Label("HDG", noWrapLabel, GUILayout.Width(58));
            GUI.enabled = false;
            GUILayout.TextField(DeveloperMode.IsEnabled
                ? $"{compassHeading:000.0} {Cardinal(compassHeading)}"
                : Cardinal(compassHeading), GUILayout.Width(96));
            GUI.enabled = true;
            GUILayout.Space(12);
            GUILayout.Label("AWA", noWrapLabel, GUILayout.Width(58));
            GUI.enabled = false;
            GUILayout.TextField(DeveloperMode.IsEnabled
                ? FormatWindAngle(apparentWindAngle)
                : FormatWindAngleCoarse(apparentWindAngle), GUILayout.Width(118));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // ── Heading readout ────────────────────────────────────────────
            if (DeveloperMode.IsEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ordered:", GUILayout.Width(66));
                GUI.enabled = false;
                GUILayout.TextField(hasPlayerSelection ? $"{playerSelectedHeading:000.0}°" : "—", GUILayout.Width(70));
                GUI.enabled = true;
                GUILayout.Space(6);
                GUILayout.Label("Helm:", GUILayout.Width(48));
                GUI.enabled = false;
                GUILayout.TextField(helmTarget.HasValue ? $"{helmTarget.Value:000.0}°" : "—", GUILayout.Width(70));
                GUI.enabled = true;
                GUILayout.Space(6);
                if (GUILayout.Button("Clear"))
                    ResetPilotingOrder();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Current:", GUILayout.Width(66));
                GUI.enabled = false;
                GUILayout.TextField($"{currentHeading:000.0}°", GUILayout.Width(70));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear"))
                    ResetPilotingOrder();
                GUILayout.EndHorizontal();
            }

            // ── Wind-angle hold ────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode", noWrapLabel, GUILayout.Width(58));
            GUI.enabled = false;
            GUILayout.TextField(holdWindAngle ? "Wind Angle" : "Heading", GUILayout.Width(96));
            GUI.enabled = true;
            GUILayout.Space(12);
            GUILayout.Label("Target", noWrapLabel, GUILayout.Width(72));
            GUI.enabled = false;
            GUILayout.TextField(hasPlayerSelection
                ? FormatTarget(playerSelectedHeading, playerSelectedWindAngle, holdWindAngle)
                : "-", GUILayout.Width(118));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Port Close")) SetWindAngleTarget(-35f);
            if (GUILayout.Button("Port Beam")) SetWindAngleTarget(-90f);
            if (GUILayout.Button("Port Broad")) SetWindAngleTarget(-135f);
            if (GUILayout.Button("Port Run")) SetWindAngleTarget(-170f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stbd Close")) SetWindAngleTarget(35f);
            if (GUILayout.Button("Stbd Beam")) SetWindAngleTarget(90f);
            if (GUILayout.Button("Stbd Broad")) SetWindAngleTarget(135f);
            if (GUILayout.Button("Stbd Run")) SetWindAngleTarget(170f);
            GUILayout.EndHorizontal();

            GUI.enabled = holdWindAngle;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Closer 10")) AdjustWindAngleMagnitude(-10f);
            if (GUILayout.Button("Closer 2")) AdjustWindAngleMagnitude(-2f);
            if (GUILayout.Button("Farther 2")) AdjustWindAngleMagnitude(2f);
            if (GUILayout.Button("Farther 10")) AdjustWindAngleMagnitude(10f);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            // ── Relative adjustment buttons ────────────────────────────────
            GUI.enabled = hasPlayerSelection;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Port 5")) AdjustPlayerTarget(-5f);
            if (GUILayout.Button("Port 15")) AdjustPlayerTarget(-15f);
            if (GUILayout.Button("Port 45")) AdjustPlayerTarget(-45f);
            if (GUILayout.Button("Port 90")) AdjustPlayerTarget(-90f);            
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stbd 5"))  AdjustPlayerTarget(  5f);
            if (GUILayout.Button("Stbd 15")) AdjustPlayerTarget( 15f);
            if (GUILayout.Button("Stbd 45")) AdjustPlayerTarget( 45f);
            if (GUILayout.Button("Stbd 90")) AdjustPlayerTarget( 90f);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            // ── Autopilot engage/disengage ─────────────────────────────────
            GUILayout.BeginHorizontal();

            bool pilotExists = VirtualCrewManager.Instance.Pilot != null;
            GUI.enabled = hasPlayerSelection && steeringWheel != null && pilotExists;

            if (autopilotEngaged)
            {
                if (GUILayout.Button("Disengage Autopilot"))
                {
                    ReleaseWheel();
                    autopilotEngaged = false;
                }
            }
            else
            {
                if (GUILayout.Button("Engage Autopilot"))
                    autopilotEngaged = true;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // ── PID parameters ─────────────────────────────────────────────
            float maxP = Plugin.PidMaxP.Value;
            float maxI = Plugin.PidMaxI.Value;
            float maxD = Plugin.PidMaxD.Value;

            GUILayout.BeginHorizontal();
            GUILayout.Label("P:", GUILayout.Width(26));
            kp = GUILayout.HorizontalSlider(Mathf.Clamp(kp, 0f, maxP), 0f, maxP);
            GUILayout.Label($"{kp:F3}", GUILayout.Width(54));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("I:", GUILayout.Width(26));
            ki = GUILayout.HorizontalSlider(Mathf.Clamp(ki, 0f, maxI), 0f, maxI);
            GUILayout.Label($"{ki:F3}", GUILayout.Width(54));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("D:", GUILayout.Width(26));
            kd = GUILayout.HorizontalSlider(Mathf.Clamp(kd, 0f, maxD), 0f, maxD);
            GUILayout.Label($"{kd:F3}", GUILayout.Width(54));
            GUILayout.EndHorizontal();

            controller.Kp = kp;
            controller.Ki = ki;
            controller.Kd = kd;

            string status = autopilotEngaged ? "● Autopilot ON" : "○ Autopilot OFF";
            GUILayout.Label($"Output: {controller.Output:+0.00;-0.00;0.00}   {status}");

            // ── History graph (developer mode only) ───────────────────────
            if (DeveloperMode.IsEnabled)
            {
                GUILayout.BeginHorizontal();
                GUI.color = Color.yellow; GUILayout.Label("■ Helm",    GUILayout.Width(66));
                GUI.color = Color.white;  GUILayout.Label("■ Current", GUILayout.Width(80));
                GUI.color = Color.red;    GUILayout.Label("■ Output",  GUILayout.Width(80));
                GUI.color = Color.white;
                GUILayout.EndHorizontal();

                Rect graph = GUILayoutUtility.GetRect(0, GraphHeight,
                    GUILayout.ExpandWidth(true), GUILayout.Height(GraphHeight));
                GUI.Box(graph, "");
                DrawGraph(graph);
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        // Draws an 8×8 dot on the circle rim at the given heading.
        // Game convention: 0=East (right), 90=South (down), 270=North (up).
        private void DrawCompassDot(Rect circ, float heading, Texture2D tex)
        {
            heading = PilotController.Normalize(heading);
            float rad = heading * Mathf.Deg2Rad;
            float r   = CircleRadius - 8f;
            GUI.DrawTexture(new Rect(
                circ.x + CircleRadius + r * Mathf.Cos(rad) - 4,
                circ.y + CircleRadius + r * Mathf.Sin(rad) - 4,
                8, 8), tex);
        }

        private void DrawCompassLine(Rect circ, float heading, Texture2D tex)
        {
            heading = PilotController.Normalize(heading);
            float rad = heading * Mathf.Deg2Rad;
            float cx = circ.x + CircleRadius;
            float cy = circ.y + CircleRadius;
            float end = CircleRadius - 14f;
            int steps = Mathf.RoundToInt(end / 4f);
            for (int i = 0; i <= steps; i++)
            {
                float dist = i * 4f;
                GUI.DrawTexture(new Rect(
                    cx + dist * Mathf.Cos(rad) - 1,
                    cy + dist * Mathf.Sin(rad) - 1,
                    2, 2), tex);
            }
        }

        private void DrawGraph(Rect r)
        {
            int count = controller.SampleCount;
            if (count == 0) return;
            int head = controller.SampleHead;

            for (int i = 0; i < count; i++)
            {
                int   idx   = (head - 1 - i + PilotController.MaxSamples * 2) % PilotController.MaxSamples;
                float xFrac = 1f - (float)i / (PilotController.MaxSamples - 1);
                float px    = r.x + xFrac * r.width;

                // Helm and current headings: 0° at top, 360° at bottom.
                float gy = r.y + controller.GoalHistory[idx]    / 360f * r.height;
                GUI.DrawTexture(new Rect(px, gy, 2, 2), goalGraphTex);

                float cy = r.y + controller.CurrentHistory[idx] / 360f * r.height;
                GUI.DrawTexture(new Rect(px, cy, 2, 2), currentGraphTex);

                // PID output: 0 = middle; positive (port correction) = up.
                float outNorm = 0.5f - controller.OutputHistory[idx] / (2f * GraphOutputMax);
                float oy = r.y + Mathf.Clamp01(outNorm) * r.height;
                GUI.DrawTexture(new Rect(px, oy, 2, 2), outputGraphTex);
            }
        }

        private void EnsureTextures()
        {
            if (circleRingTex   == null) circleRingTex   = BuildCircleRing();
            if (orderedDotTex   == null) orderedDotTex   = SolidTex(Color.green);
            if (goalDotTex      == null) goalDotTex      = SolidTex(Color.yellow);
            if (currentDotTex   == null) currentDotTex   = SolidTex(Color.white);
            if (windDotTex      == null) windDotTex      = SolidTex(Color.cyan);
            if (windLineTex     == null) windLineTex     = SolidTex(new Color(0f, 1f, 1f, 0.75f));
            if (goalGraphTex    == null) goalGraphTex    = SolidTex(Color.yellow);
            if (currentGraphTex == null) currentGraphTex = SolidTex(Color.white);
            if (outputGraphTex  == null) outputGraphTex  = SolidTex(Color.red);
        }

        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private static Texture2D BuildCircleRing()
        {
            var   tex    = new Texture2D(CircleSize, CircleSize);
            var   pixels = new Color[CircleSize * CircleSize];
            int   outerSq = CircleRadius * CircleRadius;
            int   innerSq = (CircleRadius - 5) * (CircleRadius - 5);
            Color ring    = new Color(0.7f, 0.7f, 0.7f, 1f);
            Color spoke   = new Color(0.45f, 0.45f, 0.45f, 0.7f);

            // Radial spokes every 22.5° (16 total), drawn before the ring so the ring paints on top.
            int spokeEnd = CircleRadius - 6;
            for (int tick = 0; tick < 16; tick++)
            {
                float angle = tick * 22.5f * Mathf.Deg2Rad;
                float cosA  = Mathf.Cos(angle);
                float sinA  = Mathf.Sin(angle);
                for (int dist = 0; dist <= spokeEnd; dist++)
                {
                    int px = CircleRadius + Mathf.RoundToInt(dist * cosA);
                    int py = CircleRadius + Mathf.RoundToInt(dist * sinA);
                    if ((uint)px < CircleSize && (uint)py < CircleSize)
                        pixels[py * CircleSize + px] = spoke;
                }
            }

            // Ring drawn on top so it cleanly terminates the spokes.
            for (int y = 0; y < CircleSize; y++)
            for (int x = 0; x < CircleSize; x++)
            {
                int dx = x - CircleRadius, dy = y - CircleRadius;
                int distSq = dx * dx + dy * dy;
                if (distSq >= innerSq && distSq <= outerSq)
                    pixels[y * CircleSize + x] = ring;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
