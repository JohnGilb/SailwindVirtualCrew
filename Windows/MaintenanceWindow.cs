using UnityEngine;

namespace SailwindVirtualCrew
{
    public class MaintenanceWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(560, 340, 300, 200);
        private static readonly int windowId = "VirtualCrewMaintenanceWindow".GetHashCode();

        private WindowResizer _resizer;

        public string WindowKey => "MaintenanceWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 560f, 340f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }

        private float waterLevel   = 0f;
        private float pollTimer    = 0f;
        private int   bucketCount  = 0;
        private const float PollInterval  = 10f;
        private const float ButtonHeight  = 28f;
        private const float MugUnits    = 3f;
        private const float BucketUnits = 10f;

        private static BoatDamage GetBoatDamage() =>
            GameState.lastBoat != null ? GameState.lastBoat.GetComponent<BoatDamage>() : null;

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            if (DeveloperMode.IsEnabled)
            {
                var bd = GetBoatDamage();
                if (bd != null) waterLevel = bd.waterLevel;
            }
            else
            {
                pollTimer -= Time.deltaTime;
                if (pollTimer <= 0f)
                {
                    var bd = GetBoatDamage();
                    if (bd != null) waterLevel = bd.waterLevel;
                    pollTimer = PollInterval;
                }
            }
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            float contentHeight = ButtonHeight  // water label
                                + ButtonHeight  // bucket status
                                + ButtonHeight; // bail button

            if (DeveloperMode.IsEnabled)
                contentHeight += 4f + ButtonHeight; // dev button

            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : contentHeight + 300f;
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Maintenance");
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();
            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var bd      = GetBoatDamage();

            // ── Water level ─────────────────────────────────────────────────
            string waterStr = DeveloperMode.IsEnabled
                ? $"{waterLevel * 100f:F2}%"
                : $"{waterLevel * 100f:F0}%";
            GUILayout.Label($"Water: {waterStr}");
            string bucketLabel = bucketCount > 0 ? $"[{bucketCount}] Bucket" : "[ ] Bucket";
            GUILayout.Label($"  {bucketLabel}");

            // ── Bail button ──────────────────────────────────────────────────
            GUI.enabled = bd != null && waterLevel > 0.05f;
            if (GUILayout.Button("Bail Until Empty") && bd != null)
            {
                bucketCount = LocatorUtils.findItemCounts(new[] { "bucket" })[0];
                int bucketUsersQueued = 0;
                foreach (var b in manager.BailRequests)
                    if (b.UnitsPerScoop >= BucketUnits) bucketUsersQueued++;
                float units = bucketUsersQueued < bucketCount ? BucketUnits : MugUnits;
                manager.AddBailRequest(new BailRequest(bd, units));
            }
            GUI.enabled = true;

            // ── Developer controls ───────────────────────────────────────────
            if (DeveloperMode.IsEnabled)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Set Half-Flooded"))
                {
                    var freshBd = GetBoatDamage();
                    if (freshBd != null)
                    {
                        freshBd.waterLevel = 0.5f;
                        waterLevel = 0.5f;
                        pollTimer  = PollInterval;
                    }
                }
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static string Check(bool value) => value ? "[x]" : "[ ]";

        private Texture2D fillTexture;

        private void DrawProgressBar(float progress)
        {
            if (fillTexture == null)
            {
                fillTexture = new Texture2D(1, 1);
                fillTexture.SetPixel(0, 0, Color.cyan);
                fillTexture.Apply();
            }
            Rect bar = GUILayoutUtility.GetRect(0, 14, GUILayout.ExpandWidth(true));
            GUI.Box(bar, "");
            float fillWidth = (bar.width - 4) * Mathf.Clamp01(progress / 100f);
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(bar.x + 2, bar.y + 2, fillWidth, bar.height - 4), fillTexture);
        }
    }
}
