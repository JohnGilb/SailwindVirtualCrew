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

        private float waterLevel = 0f;
        private float dirtyLevel = 0f;
        private float pollTimer = 0f;
        private int bucketCount = 0;
        private const float PollInterval = 10f;
        private const float ButtonHeight = 28f;
        private const float MugUnits = 3f;
        private const float BucketUnits = 10f;

        private static BoatDamage GetBoatDamage()
        {
            var topBoat = CrewBoatContextResolver.GetActiveTopBoat();
            return topBoat ? topBoat.GetComponent<BoatDamage>() : null;
        }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                showWindow = !showWindow;

            if (DeveloperMode.IsEnabled)
            {
                var bd = GetBoatDamage();
                if (bd != null) waterLevel = bd.waterLevel;
                dirtyLevel = SwabDecksRequest.GetDirtiness(SwabDecksRequest.GetCurrentShipCleanable());
            }
            else
            {
                pollTimer -= Time.deltaTime;
                if (pollTimer <= 0f)
                {
                    var bd = GetBoatDamage();
                    if (bd != null) waterLevel = bd.waterLevel;
                    dirtyLevel = SwabDecksRequest.GetDirtiness(SwabDecksRequest.GetCurrentShipCleanable());
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
                                + ButtonHeight  // dirty label
                                + ButtonHeight  // bail button
                                + ButtonHeight  // swab button
                                + ButtonHeight  // auto-bailing label
                                + ButtonHeight * 6 // threshold labels + sliders
                                + ButtonHeight * 3; // lantern toggles

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
            var bd = GetBoatDamage();
            var cleanable = SwabDecksRequest.GetCurrentShipCleanable();

            string waterStr = DeveloperMode.IsEnabled
                ? $"{waterLevel * 100f:F2}%"
                : $"{waterLevel * 100f:F0}%";
            GUILayout.Label($"Water: {waterStr}");
            string bucketLabel = bucketCount > 0 ? $"[{bucketCount}] Bucket" : "[ ] Bucket";
            GUILayout.Label($"  {bucketLabel}");
            string dirtyStr = DeveloperMode.IsEnabled
                ? $"{dirtyLevel * 100f:F2}%"
                : $"{dirtyLevel * 100f:F0}%";
            GUILayout.Label($"Dirty: {dirtyStr}");

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

            int swabQueued = manager.ActiveSwabDecksRequestCount;
            int swabCapacity = manager.SwabDecksRequestCapacity;
            GUI.enabled = cleanable != null && dirtyLevel >= 0.01f && swabQueued < swabCapacity;
            if (GUILayout.Button($"Swab Decks ({swabQueued}/{swabCapacity})"))
            {
                manager.AddSwabDecksRequest(new SwabDecksRequest(cleanable));
                dirtyLevel = SwabDecksRequest.GetDirtiness(cleanable);
            }
            GUI.enabled = true;

            GUILayout.Space(4);
            GUILayout.Label("Auto-Bailing");
            DrawThresholdSlider(
                "Start",
                manager.MaintenanceBailOneDeckhandThresholdPercent,
                manager.SetMaintenanceBailOneDeckhandThreshold);
            DrawThresholdSlider(
                "Two Deckhands",
                manager.MaintenanceBailTwoDeckhandsThresholdPercent,
                manager.SetMaintenanceBailTwoDeckhandsThreshold);
            DrawThresholdSlider(
                "All Deckhands",
                manager.MaintenanceBailAllDeckhandsThresholdPercent,
                manager.SetMaintenanceBailAllDeckhandsThreshold);

            GUILayout.Space(4);
            GUILayout.Label("Lanterns");
            bool autoLanterns = GUILayout.Toggle(manager.MaintenanceLanternAutoEnabled, "Automatic light/extinguish");
            if (autoLanterns != manager.MaintenanceLanternAutoEnabled)
                manager.SetMaintenanceLanternAutoEnabled(autoLanterns);

            bool refillLanterns = GUILayout.Toggle(manager.MaintenanceLanternRefillEnabled, "Quartermaster refills lanterns");
            if (refillLanterns != manager.MaintenanceLanternRefillEnabled)
                manager.SetMaintenanceLanternRefillEnabled(refillLanterns);

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
                        pollTimer = PollInterval;
                    }
                }
            }

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private static string Check(bool value) => value ? "[x]" : "[ ]";

        private static void DrawThresholdSlider(string label, float value, System.Action<float> setter)
        {
            GUILayout.Label(label + ": " + Mathf.RoundToInt(value) + "%");
            float next = GUILayout.HorizontalSlider(value, 0f, 100f);
            if (!Mathf.Approximately(next, value))
                setter(next);
        }

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
