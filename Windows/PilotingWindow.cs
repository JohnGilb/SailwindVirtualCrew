using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class PilotingWindow : MonoBehaviour
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(440, 20, 380, 800);
        private static readonly int windowId = "VirtualCrewPilotWindow".GetHashCode();

        private readonly PilotController controller = new PilotController();

        // Helm components
        private GPButtonSteeringWheel steeringWheel;
        private float currentInputMax;
        private bool  autopilotEngaged   = false;
        private float helmSearchCooldown = 0f;

        // Player order vs pilot's best attempt
        private float playerSelectedHeading;
        private bool  hasPlayerSelection = false;

        // Compass circle
        private const int CircleRadius = 100;
        private const int CircleSize   = CircleRadius * 2 + 1;  // 201 px
        private Texture2D circleRingTex;
        private Texture2D orderedDotTex;  // green  = player's ordered heading
        private Texture2D goalDotTex;     // yellow = pilot's helm (best attempt)
        private Texture2D currentDotTex;  // white  = actual current heading

        // PID text buffers
        private string kpStr = "0.1";
        private string kiStr = "0.0";
        private string kdStr = "0.05";

        // History graph
        private const int   GraphHeight    = 100;
        private const float GraphOutputMax = 50f;
        private Texture2D goalGraphTex;
        private Texture2D currentGraphTex;
        private Texture2D outputGraphTex;

        private void Update()
        {
            if (Plugin.ToggleCrewWindow.Value.IsDown())
                showWindow = !showWindow;

            // Helm search runs regardless of window visibility.
            helmSearchCooldown -= Time.deltaTime;
            if (steeringWheel == null && helmSearchCooldown <= 0f)
            {
                FindHelmComponents();
                helmSearchCooldown = 2f;
            }

            if (!autopilotEngaged) return;

            if (steeringWheel == null || !controller.TargetHeading.HasValue)
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
            if (GameState.currentBoat == null) return;
            var rudder = GameState.currentBoat.GetComponentInChildren<Rudder>();
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

        private float GetCurrentHeading()
        {
            if (GameState.currentBoat == null) return 0f;
            float raw = Vector3.SignedAngle(
                GameState.currentBoat.transform.forward, Vector3.forward, -Vector3.up);
            return PilotController.Normalize(raw);
        }

        // Stores the player's ordered heading and asks the pilot to steer their best attempt.
        private void SetPlayerTarget(float heading)
        {
            playerSelectedHeading = PilotController.Normalize(heading);
            hasPlayerSelection    = true;
            controller.SetTarget(playerSelectedHeading + ComputePilotError());
        }

        private void AdjustPlayerTarget(float delta)
        {
            if (!hasPlayerSelection) return;
            playerSelectedHeading = PilotController.Normalize(playerSelectedHeading + delta);
            controller.SetTarget(playerSelectedHeading + ComputePilotError());
        }

        // Returns a random heading error based on the pilot's Intelligence.
        // Range = +/- (7 - Intelligence): lower Intelligence → larger potential error.
        private float ComputePilotError()
        {
            var pilot = VirtualCrewManager.Instance.Pilot;
            if (pilot == null) return 0f;
            float range = (6f - pilot.Intelligence)*2;
            return Random.Range(-range, range);
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            windowRect = GUI.Window(windowId, windowRect, DrawWindow, "Piloting");
        }

        private void DrawWindow(int id)
        {
            // Suppress Tab so the game's free-look binding doesn't fire.
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            EnsureTextures();

            float  currentHeading = GetCurrentHeading();
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

            // ── Heading readout ────────────────────────────────────────────
            if (DeveloperMode.IsEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ordered:", GUILayout.Width(56));
                GUI.enabled = false;
                GUILayout.TextField(hasPlayerSelection ? $"{playerSelectedHeading:000.0}°" : "—", GUILayout.Width(70));
                GUI.enabled = true;
                GUILayout.Space(6);
                GUILayout.Label("Helm:", GUILayout.Width(38));
                GUI.enabled = false;
                GUILayout.TextField(helmTarget.HasValue ? $"{helmTarget.Value:000.0}°" : "—", GUILayout.Width(70));
                GUI.enabled = true;
                GUILayout.Space(6);
                if (GUILayout.Button("Clear"))
                {
                    controller.ClearTarget();
                    hasPlayerSelection = false;
                    if (autopilotEngaged) { ReleaseWheel(); autopilotEngaged = false; }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Current:", GUILayout.Width(56));
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
                {
                    controller.ClearTarget();
                    hasPlayerSelection = false;
                    if (autopilotEngaged) { ReleaseWheel(); autopilotEngaged = false; }
                }
                GUILayout.EndHorizontal();
            }

            // ── Relative adjustment buttons ────────────────────────────────
            GUI.enabled = hasPlayerSelection;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Port 90")) AdjustPlayerTarget(-90f);
            if (GUILayout.Button("Port 45")) AdjustPlayerTarget(-45f);
            if (GUILayout.Button("Port 15")) AdjustPlayerTarget(-15f);
            if (GUILayout.Button("Port 5"))  AdjustPlayerTarget( -5f);
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
            GUI.enabled = hasPlayerSelection && steeringWheel != null;
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
            GUILayout.BeginHorizontal();
            GUILayout.Label("P:", GUILayout.Width(16));
            kpStr = GUILayout.TextField(kpStr, GUILayout.Width(55));
            GUILayout.Label("I:", GUILayout.Width(16));
            kiStr = GUILayout.TextField(kiStr, GUILayout.Width(55));
            GUILayout.Label("D:", GUILayout.Width(16));
            kdStr = GUILayout.TextField(kdStr, GUILayout.Width(55));
            GUILayout.EndHorizontal();

            if (float.TryParse(kpStr, out float kp)) controller.Kp = kp;
            if (float.TryParse(kiStr, out float ki)) controller.Ki = ki;
            if (float.TryParse(kdStr, out float kd)) controller.Kd = kd;

            string status = autopilotEngaged ? "● Autopilot ON" : "○ Autopilot OFF";
            GUILayout.Label($"Output: {controller.Output:+0.00;-0.00;0.00}   {status}");

            // ── History graph ──────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUI.color = Color.yellow; GUILayout.Label("■ Helm",    GUILayout.Width(55));
            GUI.color = Color.white;  GUILayout.Label("■ Current", GUILayout.Width(70));
            GUI.color = Color.red;    GUILayout.Label("■ Output",  GUILayout.Width(70));
            GUI.color = Color.white;
            GUILayout.EndHorizontal();

            Rect graph = GUILayoutUtility.GetRect(0, GraphHeight,
                GUILayout.ExpandWidth(true), GUILayout.Height(GraphHeight));
            GUI.Box(graph, "");
            DrawGraph(graph);

            GUI.DragWindow();
        }

        // Draws an 8×8 dot on the circle rim at the given heading.
        // Game convention: 0=East (right), 90=South (down), 270=North (up).
        private void DrawCompassDot(Rect circ, float heading, Texture2D tex)
        {
            float rad = heading * Mathf.Deg2Rad;
            float r   = CircleRadius - 8f;
            GUI.DrawTexture(new Rect(
                circ.x + CircleRadius + r * Mathf.Cos(rad) - 4,
                circ.y + CircleRadius + r * Mathf.Sin(rad) - 4,
                8, 8), tex);
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
