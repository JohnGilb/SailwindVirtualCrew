using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class NavigatorMapWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(500, 80, 620, 520);
        private static readonly int windowId = "VirtualCrewNavigatorMapWindow".GetHashCode();

        private WindowResizer _resizer;
        private Texture2D _lineTexture;
        private GUIStyle _axisLabelStyle;
        private GUIStyle _pointLabelStyle;
        private GUIStyle _latestShipLabelStyle;
        private GUIStyle _toolbarLabelStyle;
        private bool _stylesDarkMode;
        private bool _showShipLocations = true;
        private int _recentShipDays = 20;
        private bool _hasCustomView;
        private float _viewCenterLon;
        private float _viewCenterLat;
        private float _viewSpanLon;
        private float _viewSpanLat;
        private bool _panning;
        private Vector2 _lastPanMouse;

        private const float MinViewSpanDegrees = 4f;
        private const float ZoomFactorPerWheelStep = 1.2f;
        private const float GridStepDegrees = 1f;

        public string WindowKey => "NavigatorMapWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _resizer.UserHeight };
        public float[] GetDefaultPosition() => new[] { 500f, 80f, 0f };
        public void SetPosition(float x, float y, float userHeight) { windowRect.x = x; windowRect.y = y; _resizer.UserHeight = userHeight; }
        public void SetVisible(bool visible) { showWindow = visible; }

        private void OnGUI()
        {
            if (!showWindow) return;
            SailwindGuiStyle.Apply();

            windowRect.width = Mathf.Max(420f, windowRect.width);
            windowRect.height = _resizer.UserHeight > 0f ? _resizer.UserHeight : Mathf.Max(360f, windowRect.height);
            windowRect = WindowLayoutUtility.DrawClampedWindow(windowId, windowRect, DrawWindow, "Navigator Map", false);
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab)
                Event.current.Use();

            EnsureStyles();
            GUILayout.Space(4);

            var manager = VirtualCrewManager.Instance;
            var allShipFixes = manager.NavigatorShipLog
                .Where(e => e != null && e.HasPosition)
                .OrderBy(e => e.localDay)
                .ToList();
            var shipFixes = GetDisplayedShipFixes(allShipFixes);
            var islands = (manager.NavigatorIslandMap ?? new Dictionary<string, NavigatorIslandMapEntrySaveData>())
                .Values
                .Where(e => e != null && e.HasPosition)
                .OrderBy(e => e.name)
                .ToList();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Ship fixes: " + shipFixes.Count + "/" + allShipFixes.Count + "   Islands: " + islands.Count, _toolbarLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Ship Log", GUILayout.Width(82f)))
                ToggleShipLogWindow();
            GUI.enabled = allShipFixes.Count > 0;
            if (GUILayout.Button("Zoom to Ship", GUILayout.Width(110f)))
                ZoomToShip(allShipFixes.Last());
            GUI.enabled = true;
            if (GUILayout.Button("Fit All", GUILayout.Width(70f)))
                _hasCustomView = false;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_showShipLocations ? "Hide Ships" : "Show Ships", GUILayout.Width(86f)))
                _showShipLocations = !_showShipLocations;
            GUILayout.Label("Ship days: " + _recentShipDays, _toolbarLabelStyle, GUILayout.Width(92f));
            _recentShipDays = Mathf.RoundToInt(GUILayout.HorizontalSlider(_recentShipDays, 1f, 20f, GUILayout.Width(150f)));
            _recentShipDays = Mathf.Clamp(_recentShipDays, 1, 20);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            float chartHeight = Mathf.Max(220f, Mathf.Min(windowRect.width - 24f, windowRect.height - 136f));
            Rect chartRect = GUILayoutUtility.GetRect(0f, chartHeight, GUILayout.ExpandWidth(true));
            DrawChart(chartRect, shipFixes, islands);

            _resizer.HandleInWindow(ref windowRect);
            GUI.DragWindow();
        }

        private void ToggleShipLogWindow()
        {
            var logWindow = GetComponent<NavigatorShipLogWindow>();
            if (logWindow != null)
                logWindow.ToggleVisible();
        }

        private List<NavigatorShipLogEntrySaveData> GetDisplayedShipFixes(List<NavigatorShipLogEntrySaveData> allShipFixes)
        {
            if (!_showShipLocations || allShipFixes == null || allShipFixes.Count == 0)
                return new List<NavigatorShipLogEntrySaveData>();

            int newestDay = allShipFixes[allShipFixes.Count - 1].localDay;
            int earliestDay = newestDay - _recentShipDays + 1;
            return allShipFixes
                .Where(e => e.localDay >= earliestDay)
                .ToList();
        }

        private void DrawChart(Rect rect, List<NavigatorShipLogEntrySaveData> shipFixes, List<NavigatorIslandMapEntrySaveData> islands)
        {
            float availableWidth = rect.width - 96f;
            float availableHeight = rect.height - 78f;
            float plotSize = Mathf.Min(availableWidth, availableHeight);
            Rect plot = new Rect(
                rect.x + 68f + (availableWidth - plotSize) * 0.5f,
                rect.y + 30f + (availableHeight - plotSize) * 0.5f,
                plotSize,
                plotSize);
            if (plot.width <= 40f || plot.height <= 40f)
                return;

            Rect frame = new Rect(plot.x - 58f, plot.y - 24f, plot.width + 82f, plot.height + 64f);
            GUI.Box(frame, "");

            if (shipFixes.Count == 0 && islands.Count == 0)
            {
                GUI.Label(plot, "No map fixes yet.", _axisLabelStyle);
                return;
            }

            MapBounds dataBounds = CalculateBounds(shipFixes, islands);
            MapBounds bounds = GetViewBounds(dataBounds);
            HandleMapInput(plot, bounds);
            DrawGrid(plot, bounds);

            foreach (var island in islands)
            {
                if (!bounds.Contains(island.Longitude, island.Latitude))
                    continue;

                Vector2 point = Project(plot, bounds, island.Longitude, island.Latitude);
                if (!IsPointVisible(plot, point, 24f))
                    continue;
                DrawCircle(point, 6f, GetIslandColor(), 2f);
                GUI.Label(new Rect(point.x + 8f, point.y - 10f, 150f, 20f), GetIslandLabel(island), _pointLabelStyle);
            }

            NavigatorShipLogEntrySaveData latestShipFix = shipFixes.Count > 0
                ? shipFixes[shipFixes.Count - 1]
                : null;
            foreach (var fix in shipFixes)
            {
                if (!bounds.Contains(fix.Longitude, fix.Latitude))
                    continue;

                Vector2 point = Project(plot, bounds, fix.Longitude, fix.Latitude);
                if (!IsPointVisible(plot, point, 24f))
                    continue;

                bool isLatest = fix == latestShipFix;
                DrawX(point, 6f, isLatest ? GetLatestShipFixColor() : GetShipFixColor(), 2f);
                GUI.Label(
                    new Rect(point.x + 8f, point.y - 10f, 80f, 20f),
                    "D" + fix.localDay,
                    isLatest ? _latestShipLabelStyle : _pointLabelStyle);
            }

            GUI.Label(new Rect(plot.x, plot.yMax + 34f, plot.width, 20f), "Longitude", _axisLabelStyle);
            GUI.Label(new Rect(rect.x + 8f, plot.y - 26f, 58f, 20f), "Latitude", _axisLabelStyle);
        }

        private MapBounds CalculateBounds(List<NavigatorShipLogEntrySaveData> shipFixes, List<NavigatorIslandMapEntrySaveData> islands)
        {
            float minLon = 0f;
            float maxLon = 0f;
            float minLat = 0f;
            float maxLat = 0f;
            bool hasPoint = false;

            foreach (var fix in shipFixes)
                IncludePoint(fix.Longitude, fix.Latitude, ref minLon, ref maxLon, ref minLat, ref maxLat, ref hasPoint);
            foreach (var island in islands)
                IncludePoint(island.Longitude, island.Latitude, ref minLon, ref maxLon, ref minLat, ref maxLat, ref hasPoint);

            ExpandRange(ref minLon, ref maxLon);
            ExpandRange(ref minLat, ref maxLat);
            return new MapBounds(minLon, maxLon, minLat, maxLat);
        }

        private static void IncludePoint(float lon, float lat, ref float minLon, ref float maxLon, ref float minLat, ref float maxLat, ref bool hasPoint)
        {
            if (!hasPoint)
            {
                minLon = maxLon = lon;
                minLat = maxLat = lat;
                hasPoint = true;
                return;
            }

            minLon = Mathf.Min(minLon, lon);
            maxLon = Mathf.Max(maxLon, lon);
            minLat = Mathf.Min(minLat, lat);
            maxLat = Mathf.Max(maxLat, lat);
        }

        private static void ExpandRange(ref float min, ref float max)
        {
            float span = max - min;
            if (span < MinViewSpanDegrees)
            {
                float mid = (min + max) * 0.5f;
                min = mid - MinViewSpanDegrees * 0.5f;
                max = mid + MinViewSpanDegrees * 0.5f;
            }
            else
            {
                float padding = Mathf.Max(1f, span * 0.1f);
                min -= padding;
                max += padding;
            }

            min = Mathf.Floor(min);
            max = Mathf.Ceil(max);
        }

        private void DrawGrid(Rect plot, MapBounds bounds)
        {
            Color grid = GetGridColor();
            Color axis = GetAxisColor();

            for (float lon = Mathf.Ceil(bounds.MinLon); lon <= bounds.MaxLon + 0.001f; lon += GridStepDegrees)
            {
                float x = Project(plot, bounds, lon, bounds.MinLat).x;
                DrawLine(new Vector2(x, plot.yMin), new Vector2(x, plot.yMax), grid, 1f);
                GUI.Label(new Rect(x - 30f, plot.yMax + 8f, 60f, 20f), FormatCoord(lon), _axisLabelStyle);
            }

            for (float lat = Mathf.Ceil(bounds.MinLat); lat <= bounds.MaxLat + 0.001f; lat += GridStepDegrees)
            {
                float y = Project(plot, bounds, bounds.MinLon, lat).y;
                DrawLine(new Vector2(plot.xMin, y), new Vector2(plot.xMax, y), grid, 1f);
                GUI.Label(new Rect(plot.xMin - 62f, y - 10f, 54f, 20f), FormatCoord(lat), _axisLabelStyle);
            }

            if (bounds.MinLon <= 0f && bounds.MaxLon >= 0f)
            {
                float axisX = Project(plot, bounds, 0f, bounds.MinLat).x;
                DrawLine(new Vector2(axisX, plot.yMin), new Vector2(axisX, plot.yMax), axis, 2f);
            }

            if (bounds.MinLat <= 0f && bounds.MaxLat >= 0f)
            {
                float axisY = Project(plot, bounds, bounds.MinLon, 0f).y;
                DrawLine(new Vector2(plot.xMin, axisY), new Vector2(plot.xMax, axisY), axis, 2f);
            }
        }

        private static Vector2 Project(Rect plot, MapBounds bounds, float lon, float lat)
        {
            float x = Mathf.Lerp(plot.xMin, plot.xMax, Mathf.InverseLerp(bounds.MinLon, bounds.MaxLon, lon));
            float y = Mathf.Lerp(plot.yMax, plot.yMin, Mathf.InverseLerp(bounds.MinLat, bounds.MaxLat, lat));
            return new Vector2(x, y);
        }

        private static Vector2 Unproject(Rect plot, MapBounds bounds, Vector2 point)
        {
            float lon = Mathf.Lerp(bounds.MinLon, bounds.MaxLon, Mathf.InverseLerp(plot.xMin, plot.xMax, point.x));
            float lat = Mathf.Lerp(bounds.MaxLat, bounds.MinLat, Mathf.InverseLerp(plot.yMin, plot.yMax, point.y));
            return new Vector2(lon, lat);
        }

        private MapBounds GetViewBounds(MapBounds dataBounds)
        {
            if (!_hasCustomView)
                SetViewFromBounds(dataBounds);

            float span = Mathf.Max(MinViewSpanDegrees, _viewSpanLon, _viewSpanLat);
            _viewSpanLon = span;
            _viewSpanLat = span;
            return BoundsFromCenter(_viewCenterLon, _viewCenterLat, span);
        }

        private void SetViewFromBounds(MapBounds bounds)
        {
            _viewCenterLon = (bounds.MinLon + bounds.MaxLon) * 0.5f;
            _viewCenterLat = (bounds.MinLat + bounds.MaxLat) * 0.5f;
            float span = Mathf.Max(MinViewSpanDegrees, bounds.LonSpan, bounds.LatSpan);
            _viewSpanLon = span;
            _viewSpanLat = span;
        }

        private void ZoomToShip(NavigatorShipLogEntrySaveData fix)
        {
            if (fix == null || !fix.HasPosition)
                return;

            _hasCustomView = true;
            _viewCenterLon = fix.Longitude;
            _viewCenterLat = fix.Latitude;
            _viewSpanLon = MinViewSpanDegrees;
            _viewSpanLat = MinViewSpanDegrees;
        }

        private void HandleMapInput(Rect plot, MapBounds bounds)
        {
            Event e = Event.current;
            if (e == null)
                return;

            if (e.type == EventType.ScrollWheel && plot.Contains(e.mousePosition))
            {
                Vector2 before = Unproject(plot, bounds, e.mousePosition);
                float zoomFactor = Mathf.Pow(ZoomFactorPerWheelStep, e.delta.y);
                _hasCustomView = true;
                float span = Mathf.Max(MinViewSpanDegrees, bounds.LonSpan * zoomFactor);
                _viewSpanLon = span;
                _viewSpanLat = span;

                MapBounds zoomed = BoundsFromCenter(_viewCenterLon, _viewCenterLat, span);
                Vector2 after = Unproject(plot, zoomed, e.mousePosition);
                _viewCenterLon += before.x - after.x;
                _viewCenterLat += before.y - after.y;
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && plot.Contains(e.mousePosition))
            {
                _panning = true;
                _lastPanMouse = e.mousePosition;
                _hasCustomView = true;
                GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDrag && _panning && e.button == 0)
            {
                Vector2 delta = e.mousePosition - _lastPanMouse;
                _lastPanMouse = e.mousePosition;
                _viewCenterLon -= delta.x / plot.width * bounds.LonSpan;
                _viewCenterLat += delta.y / plot.height * bounds.LatSpan;
                e.Use();
                return;
            }

            if (e.rawType == EventType.MouseUp && _panning)
            {
                _panning = false;
                GUIUtility.hotControl = 0;
                if (e.type != EventType.Used)
                    e.Use();
            }
        }

        private static MapBounds BoundsFromCenter(float centerLon, float centerLat, float span)
        {
            span = Mathf.Max(MinViewSpanDegrees, span);
            return new MapBounds(
                centerLon - span * 0.5f,
                centerLon + span * 0.5f,
                centerLat - span * 0.5f,
                centerLat + span * 0.5f);
        }

        private static bool IsPointVisible(Rect plot, Vector2 point, float padding)
        {
            return point.x >= plot.xMin - padding
                && point.x <= plot.xMax + padding
                && point.y >= plot.yMin - padding
                && point.y <= plot.yMax + padding;
        }

        private void DrawX(Vector2 center, float radius, Color color, float thickness)
        {
            DrawLine(new Vector2(center.x - radius, center.y - radius), new Vector2(center.x + radius, center.y + radius), color, thickness);
            DrawLine(new Vector2(center.x - radius, center.y + radius), new Vector2(center.x + radius, center.y - radius), color, thickness);
        }

        private void DrawCircle(Vector2 center, float radius, Color color, float thickness)
        {
            const int segments = 24;
            Vector2 previous = center + new Vector2(radius, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector2 next = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                DrawLine(previous, next, color, thickness);
                previous = next;
            }
        }

        private void DrawLine(Vector2 start, Vector2 end, Color color, float thickness)
        {
            EnsureLineTexture();
            Color oldColor = GUI.color;

            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length <= 0f)
                return;

            GUI.color = color;

            if (Mathf.Abs(delta.x) < 0.001f)
            {
                float y = Mathf.Min(start.y, end.y);
                GUI.DrawTexture(new Rect(start.x - thickness * 0.5f, y, thickness, length), _lineTexture);
                GUI.color = oldColor;
                return;
            }

            if (Mathf.Abs(delta.y) < 0.001f)
            {
                float x = Mathf.Min(start.x, end.x);
                GUI.DrawTexture(new Rect(x, start.y - thickness * 0.5f, length, thickness), _lineTexture);
                GUI.color = oldColor;
                return;
            }

            Matrix4x4 oldMatrix = GUI.matrix;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, start);
            GUI.DrawTexture(new Rect(start.x, start.y - thickness * 0.5f, length, thickness), _lineTexture);
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        private void EnsureLineTexture()
        {
            if (_lineTexture != null)
                return;

            _lineTexture = new Texture2D(1, 1);
            _lineTexture.SetPixel(0, 0, Color.white);
            _lineTexture.Apply();
        }

        private void EnsureStyles()
        {
            if (_axisLabelStyle != null && !SailwindGuiStyle.HasThemeChanged(_stylesDarkMode))
                return;

            _stylesDarkMode = SailwindGuiStyle.IsDarkMode;
            _axisLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            ClearStyleBackground(_axisLabelStyle);
            _pointLabelStyle = new GUIStyle(_axisLabelStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold
            };
            _latestShipLabelStyle = new GUIStyle(_pointLabelStyle);
            SetStyleTextColor(_latestShipLabelStyle, GetLatestShipFixColor());
            _toolbarLabelStyle = new GUIStyle(_axisLabelStyle)
            {
                alignment = TextAnchor.MiddleLeft
            };
        }

        private static void SetStyleTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            style.focused.textColor = color;
            style.onNormal.textColor = color;
            style.onHover.textColor = color;
            style.onActive.textColor = color;
            style.onFocused.textColor = color;
        }

        private static void ClearStyleBackground(GUIStyle style)
        {
            style.normal.background = null;
            style.hover.background = null;
            style.active.background = null;
            style.focused.background = null;
            style.onNormal.background = null;
            style.onHover.background = null;
            style.onActive.background = null;
            style.onFocused.background = null;
        }

        private static string GetIslandLabel(NavigatorIslandMapEntrySaveData island)
        {
            return !string.IsNullOrEmpty(island.name) ? island.name : "Island";
        }

        private static string FormatCoord(float value)
        {
            return value.ToString("0");
        }

        private static Color GetShipFixColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.58f, 0.92f, 1f)
                : new Color(0.05f, 0.34f, 0.68f);
        }

        private static Color GetLatestShipFixColor()
        {
            return Color.red;
        }

        private static Color GetIslandColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(1f, 0.82f, 0.35f)
                : new Color(0.46f, 0.22f, 0.04f);
        }

        private static Color GetGridColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.75f, 0.68f, 0.56f, 0.28f)
                : new Color(0f, 0f, 0f, 0.2f);
        }

        private static Color GetAxisColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.86f, 0.8f, 0.68f, 0.65f)
                : new Color(0f, 0f, 0f, 0.45f);
        }

        private struct MapBounds
        {
            internal readonly float MinLon;
            internal readonly float MaxLon;
            internal readonly float MinLat;
            internal readonly float MaxLat;
            internal float LonSpan => MaxLon - MinLon;
            internal float LatSpan => MaxLat - MinLat;

            internal bool Contains(float lon, float lat)
            {
                return lon >= MinLon
                    && lon <= MaxLon
                    && lat >= MinLat
                    && lat <= MaxLat;
            }

            internal MapBounds(float minLon, float maxLon, float minLat, float maxLat)
            {
                MinLon = minLon;
                MaxLon = maxLon;
                MinLat = minLat;
                MaxLat = maxLat;
            }
        }
    }
}
