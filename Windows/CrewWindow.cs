using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SailwindVirtualCrew
{
    public class CrewWindow : MonoBehaviour, IWindowPosition
    {
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 20, 440, 560);

        public string WindowKey => "CrewWindow";
        public float[] GetPosition() => new[] { windowRect.x, windowRect.y, _userHeight };
        public float[] GetDefaultPosition() => new[] { 20f, 20f, 0f };
        public void SetPosition(float x, float y, float userHeight)
        {
            windowRect.x = x;
            windowRect.y = y;
            _userHeight = userHeight;
            ApplyWindowRect();
        }

        private ICommonSailActions selectedSail;
        private string renameBuffer = "";
        private string vesselRenameBuffer = "";
        private bool _renamingSail;
        private bool _renamingVessel;
        private float _userHeight;

        private GameObject _rootObject;
        private Canvas _canvas;
        private RectTransform _panelRect;
        private RectTransform _contentRect;
        private Image _panelImage;
        private Image _headerImage;
        private Image _resizeHandleImage;
        private Text _vesselLabel;
        private InputField _vesselRenameInput;
        private InputField _sailRenameInput;

        private readonly HashSet<object> _pendingRopes = new HashSet<object>();
        private readonly Dictionary<ICommonSailActions, SailButtonBinding> _sailButtons = new Dictionary<ICommonSailActions, SailButtonBinding>();
        private readonly List<WinchButtonBinding> _winchButtons = new List<WinchButtonBinding>();
        private readonly List<MooringButtonBinding> _mooringButtons = new List<MooringButtonBinding>();
        private Button _dropAnchorButton;
        private Button _raiseAnchorButton;
        private bool _canMoorPort;
        private bool _canMoorStarboard;
        private float _nextMooringAvailabilityRefresh;
        private int _lastMooringRequestCount = -1;

        private bool _rebuildRequested = true;
        private ICommonSailActions _builtSelectedSail;
        private SailGroup _builtSelectedGroup;
        private int _builtSailCount = -1;
        private bool _builtRenamingSail;
        private bool _builtRenamingVessel;
        private int _pendingRopeCacheFrame = -1;

        private Font _buttonFont;
        private Font _labelFont;
        private bool _themeApplied;
        private bool _lastDarkMode;

        private const float DefaultWidth = 440f;
        private const float DefaultHeight = 560f;
        private const float HeaderHeight = 24f;
        private const float ResizeHandleHeight = 10f;
        private const float Padding = 8f;
        private const float RowHeight = 28f;
        private const float RowGap = 4f;
        private const float MooringAvailabilityRefreshSeconds = 5f;
        private const int SortingOrder = 1000;
        private const float ScrollSensitivity = 48f;

        private void Awake()
        {
            EnsureUi();
            UpdateVisibility();
        }

        private void OnDestroy()
        {
            if (_rootObject)
                Destroy(_rootObject);
        }

        private void Update()
        {
            if (WindowLayoutUtility.ShouldToggleWindowsThisFrame())
                SetVisible(!showWindow);

            EnsureUi();
            UpdateVisibility();
            if (!_rootObject.activeSelf)
                return;

            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.Update"))
            {
                ApplyThemeIfNeeded();

                var manager = VirtualCrewManager.Instance;
                var sails = manager.AllSails;
                if (selectedSail != null && !sails.Contains(selectedSail))
                {
                    selectedSail = null;
                    renameBuffer = "";
                    _renamingSail = false;
                    RequestRebuild();
                }

                RebuildIfNeeded(manager);
                RefreshState(manager);
                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.ApplyWindowRect"))
                    ApplyWindowRect();
            }
        }

        public void SetVisible(bool visible)
        {
            showWindow = visible;
            UpdateVisibility();
        }

        private void EnsureUi()
        {
            if (_rootObject)
                return;

            EnsureEventSystem();
            AssignLegacyFonts();

            _rootObject = new GameObject("VirtualCrew_DeckOrders_uGUI");
            _rootObject.transform.SetParent(transform, false);
            _canvas = _rootObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = SortingOrder;
            _rootObject.AddComponent<GraphicRaycaster>();

            var panel = new GameObject("Panel");
            panel.transform.SetParent(_rootObject.transform, false);
            _panelRect = panel.AddComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0f, 1f);
            _panelRect.anchorMax = new Vector2(0f, 1f);
            _panelRect.pivot = new Vector2(0f, 1f);
            _panelImage = panel.AddComponent<Image>();
            var panelDrag = panel.AddComponent<DragHandler>();
            panelDrag.Initialize(this);

            var header = CreateRect("Header", panel.transform);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(0f, -HeaderHeight);
            header.offsetMax = Vector2.zero;
            _headerImage = header.gameObject.AddComponent<Image>();
            var drag = header.gameObject.AddComponent<DragHandler>();
            drag.Initialize(this);
            AddText(header.transform, "Deck Orders", 18, TextAnchor.MiddleCenter);

            var scroll = CreateRect("Scroll View", panel.transform);
            scroll.anchorMin = new Vector2(0f, 0f);
            scroll.anchorMax = new Vector2(1f, 1f);
            scroll.offsetMin = new Vector2(Padding, Padding + ResizeHandleHeight);
            scroll.offsetMax = new Vector2(-Padding, -HeaderHeight - Padding);
            var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = ScrollSensitivity;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            var viewport = CreateRect("Viewport", scroll);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            viewport.gameObject.AddComponent<RectMask2D>();

            _contentRect = CreateRect("Content", viewport);
            _contentRect.anchorMin = new Vector2(0f, 1f);
            _contentRect.anchorMax = new Vector2(1f, 1f);
            _contentRect.pivot = new Vector2(0.5f, 1f);
            _contentRect.anchoredPosition = Vector2.zero;
            _contentRect.sizeDelta = Vector2.zero;
            var layout = _contentRect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = RowGap;
            var fitter = _contentRect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = _contentRect;
            scrollRect.viewport = viewport;

            var handle = CreateRect("Resize Handle", panel.transform);
            handle.anchorMin = new Vector2(0f, 0f);
            handle.anchorMax = new Vector2(1f, 0f);
            handle.pivot = new Vector2(0.5f, 0f);
            handle.offsetMin = Vector2.zero;
            handle.offsetMax = new Vector2(0f, ResizeHandleHeight);
            _resizeHandleImage = handle.gameObject.AddComponent<Image>();
            var resize = handle.gameObject.AddComponent<ResizeHandler>();
            resize.Initialize(this);

            ApplyTheme();
            ApplyWindowRect();
        }

        private void RebuildIfNeeded(VirtualCrewManager manager)
        {
            var sails = manager.AllSails;
            bool structureChanged = _rebuildRequested
                || _builtSelectedSail != selectedSail
                || _builtSelectedGroup != manager.SelectedGroup
                || _builtSailCount != sails.Count
                || _builtRenamingSail != _renamingSail
                || _builtRenamingVessel != _renamingVessel;

            if (!structureChanged)
                return;

            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.Rebuild"))
            {
                ClearContent();
                BuildVesselSection(manager);
                BuildAnchorSection(manager);
                BuildMooringSection(manager);
                BuildSailList(manager);
                BuildSelectedSailCommands(manager);
            }

            _builtSelectedSail = selectedSail;
            _builtSelectedGroup = manager.SelectedGroup;
            _builtSailCount = sails.Count;
            _builtRenamingSail = _renamingSail;
            _builtRenamingVessel = _renamingVessel;
            _rebuildRequested = false;
            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.LayoutRebuild"))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRect);
                Canvas.ForceUpdateCanvases();
            }
        }

        private void BuildVesselSection(VirtualCrewManager manager)
        {
            _vesselLabel = AddLabel("");
            if (!_renamingVessel)
            {
                AddRow(CreateButton("Rename", () =>
                {
                    _renamingVessel = true;
                    vesselRenameBuffer = manager.CurrentVesselFriendlyName ?? "";
                    RequestRebuild();
                }, 80f));
            }
            else
            {
                _vesselRenameInput = CreateInput(vesselRenameBuffer);
                AddRow(_vesselRenameInput.gameObject, CreateButton("Set", () =>
                {
                    string value = _vesselRenameInput.text != null ? _vesselRenameInput.text.Trim() : "";
                    if (value.Length == 0) return;
                    manager.SetVesselFriendlyName(value);
                    vesselRenameBuffer = value;
                    _renamingVessel = false;
                    RequestRebuild();
                }, 46f));
            }
        }

        private void BuildAnchorSection(VirtualCrewManager manager)
        {
            var anchors = manager.AnchorWinches;
            if (anchors.Count == 0)
                return;

            _dropAnchorButton = CreateButton("Drop Anchor", () =>
            {
                manager.AddWorkRequest(new WorkRequest(null, "Drop Anchor",
                    anchors.Select(w => new WinchTarget(w, 1f)).ToArray()));
            }).GetComponent<Button>();
            _raiseAnchorButton = CreateButton("Raise Anchor", () =>
            {
                manager.AddWorkRequest(new WorkRequest(null, "Raise Anchor",
                    anchors.Select(w => new WinchTarget(w, 0f)).ToArray()));
            }).GetComponent<Button>();
            AddRow(_dropAnchorButton.gameObject, _raiseAnchorButton.gameObject);
        }

        private void BuildMooringSection(VirtualCrewManager manager)
        {
            var port = CreateButton("Moor Port", () =>
            {
                manager.AddMooringRequests(MooringSide.Port);
                InvalidateMooringAvailability();
            }).GetComponent<Button>();
            var starboard = CreateButton("Moor Starboard", () =>
            {
                manager.AddMooringRequests(MooringSide.Starboard);
                InvalidateMooringAvailability();
            }).GetComponent<Button>();
            _mooringButtons.Add(new MooringButtonBinding(port, MooringSide.Port));
            _mooringButtons.Add(new MooringButtonBinding(starboard, MooringSide.Starboard));
            InvalidateMooringAvailability();
            AddRow(port.gameObject, starboard.gameObject);
        }

        private void BuildSailList(VirtualCrewManager manager)
        {
            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.Rebuild.SailList"))
            {
                AddLabel("Sails  (click to select)");
                var sails = manager.AllSails;
                if (sails.Count == 0)
                {
                    AddLabel("No sails mapped. Press V to scan the boat.");
                    return;
                }

                foreach (var sail in sails)
                {
                    var captured = sail;
                    var button = CreateButton(sail.getSailName(), () =>
                    {
                        if (selectedSail == captured)
                        {
                            selectedSail = null;
                            renameBuffer = "";
                            _renamingSail = false;
                        }
                        else
                        {
                            selectedSail = captured;
                            renameBuffer = captured.FriendlyName ?? "";
                            _renamingSail = false;
                        }
                        RequestRebuild();
                    }).GetComponent<Button>();
                _sailButtons[sail] = new SailButtonBinding(button, button.GetComponentInChildren<Text>());
                AddRow(button.gameObject);
            }
        }
        }

        private void BuildSelectedSailCommands(VirtualCrewManager manager)
        {
            if (selectedSail == null)
                return;

            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.Rebuild.SelectedSailCommands"))
            {
                AddSpacer(4f);
                AddLabel("Commands: " + selectedSail.getSailName());

                if (!_renamingSail)
                {
                    AddRow(CreateButton("Rename", () =>
                    {
                        _renamingSail = true;
                        renameBuffer = selectedSail.FriendlyName ?? "";
                        RequestRebuild();
                    }, 80f));
                }
                else
                {
                    _sailRenameInput = CreateInput(renameBuffer);
                    AddRow(_sailRenameInput.gameObject, CreateButton("Set", () =>
                    {
                        string value = _sailRenameInput.text != null ? _sailRenameInput.text.Trim() : "";
                        if (value.Length == 0) return;
                        manager.SetSailFriendlyName(selectedSail, value);
                        renameBuffer = value;
                        _renamingSail = false;
                        RequestRebuild();
                    }, 46f));
                }

                var selectedGroup = manager.SelectedGroup;
                if (selectedGroup != null && !selectedGroup.IsAllSails)
                {
                    bool inGroup = selectedGroup.Contains(selectedSail);
                    AddRow(CreateButton(inGroup ? "Remove from " + selectedGroup.Name : "Add to " + selectedGroup.Name, () =>
                    {
                        if (selectedGroup.Contains(selectedSail)) manager.RemoveSailFromGroup(selectedGroup, selectedSail);
                        else manager.AddSailToGroup(selectedGroup, selectedSail);
                        RequestRebuild();
                    }));
                }

                AddLabel("Halyard:");
                AddRow(
                    WinchButton(manager, "Reef", selectedSail.getHalyardWinch(), () => manager.AddWorkRequest(new WorkRequest(selectedSail, "Halyard Reef", new WinchTarget(selectedSail.getHalyardWinch(), 0.00f)))),
                    WinchButton(manager, "1/4", selectedSail.getHalyardWinch(), () => manager.AddWorkRequest(new WorkRequest(selectedSail, "Halyard 1/4", new WinchTarget(selectedSail.getHalyardWinch(), 0.25f)))),
                    WinchButton(manager, "1/2", selectedSail.getHalyardWinch(), () => manager.AddWorkRequest(new WorkRequest(selectedSail, "Halyard 1/2", new WinchTarget(selectedSail.getHalyardWinch(), 0.50f)))),
                    WinchButton(manager, "3/4", selectedSail.getHalyardWinch(), () => manager.AddWorkRequest(new WorkRequest(selectedSail, "Halyard 3/4", new WinchTarget(selectedSail.getHalyardWinch(), 0.75f)))),
                    WinchButton(manager, "Full", selectedSail.getHalyardWinch(), () => manager.AddWorkRequest(new WorkRequest(selectedSail, "Halyard Full", new WinchTarget(selectedSail.getHalyardWinch(), 1.00f)))));

                AddLabel("Sheet:");
                if (selectedSail is SimpleSail simple)
                    BuildSimpleSailCommands(manager, simple);
                else if (selectedSail is DualSheetSail dual)
                    BuildDualSailCommands(manager, dual);
            }
        }

        private void BuildSimpleSailCommands(VirtualCrewManager manager, SimpleSail sail)
        {
            AddRow(
                WinchButton(manager, "Hard", sail.getSheetWinch(), () => manager.AddWorkRequest(new WorkRequest(sail, "Sheet Hard", new WinchTarget(sail.getSheetWinch(), 0.00f)))),
                WinchButton(manager, "1/4", sail.getSheetWinch(), () => manager.AddWorkRequest(new WorkRequest(sail, "Sheet 1/4", new WinchTarget(sail.getSheetWinch(), 0.25f)))),
                WinchButton(manager, "1/2", sail.getSheetWinch(), () => manager.AddWorkRequest(new WorkRequest(sail, "Sheet 1/2", new WinchTarget(sail.getSheetWinch(), 0.50f)))),
                WinchButton(manager, "3/4", sail.getSheetWinch(), () => manager.AddWorkRequest(new WorkRequest(sail, "Sheet 3/4", new WinchTarget(sail.getSheetWinch(), 0.75f)))),
                WinchButton(manager, "Let Fly", sail.getSheetWinch(), () => manager.AddWorkRequest(new WorkRequest(sail, "Sheet Let Fly", new WinchTarget(sail.getSheetWinch(), 1.00f)))));

            AddRow(
                WinchButton(manager, "Harden Up", sail.getSheetWinch(), () =>
                {
                    var winch = sail.getSheetWinch();
                    manager.AddWorkRequest(new WorkRequest(sail, "Sheet Harden Up", new WinchTarget(winch, Mathf.Clamp01(winch.rope.currentLength - 0.10f))));
                }),
                WinchButton(manager, "Ease Out", sail.getSheetWinch(), () =>
                {
                    var winch = sail.getSheetWinch();
                    manager.AddWorkRequest(new WorkRequest(sail, "Sheet Ease Out", new WinchTarget(winch, Mathf.Clamp01(winch.rope.currentLength + 0.10f))));
                }),
                WinchButton(manager, "Trim", sail.getSheetWinch(), () => manager.AddTrimRequest(new TrimRequest(sail))));
        }

        private void BuildDualSailCommands(VirtualCrewManager manager, DualSheetSail sail)
        {
            if (sail.getSubtype() == DualSheetSail.DualSheetSailSubtype.Square)
            {
                AddRow(
                    DualWinchButton(manager, "Full Port", sail, () => AddDualSheetWork(manager, sail, "Full Port", 0.00f, 1.00f)),
                    DualWinchButton(manager, "1/2 Port", sail, () => AddDualSheetWork(manager, sail, "1/2 Port", 0.25f, 0.75f)),
                    DualWinchButton(manager, "Ahead", sail, () => AddDualSheetWork(manager, sail, "Ahead", 0.50f, 0.50f)),
                    DualWinchButton(manager, "1/2 Stbd", sail, () => AddDualSheetWork(manager, sail, "1/2 Stbd", 0.75f, 0.25f)),
                    DualWinchButton(manager, "Full Stbd", sail, () => AddDualSheetWork(manager, sail, "Full Stbd", 1.00f, 0.00f)));
                AddRow(DualWinchButton(manager, "Trim", sail, () => manager.AddSquareTrimRequest(new SquareTrimRequest(sail))));
                return;
            }

            AddRow(
                DualWinchButton(manager, "Full Port", sail, () => AddDualSheetWork(manager, sail, "Full Port", 0.00f, 1.00f)),
                DualWinchButton(manager, "3/4 Port", sail, () => AddDualSheetWork(manager, sail, "3/4 Port", 0.25f, 1.00f)),
                DualWinchButton(manager, "1/2 Port", sail, () => AddDualSheetWork(manager, sail, "1/2 Port", 0.50f, 1.00f)),
                DualWinchButton(manager, "1/4 Port", sail, () => AddDualSheetWork(manager, sail, "1/4 Port", 0.75f, 1.00f)));
            AddRow(DualWinchButton(manager, "Let Fly", sail, () => AddDualSheetWork(manager, sail, "Let Fly", 1.00f, 1.00f)));
            AddRow(
                DualWinchButton(manager, "Full Stbd", sail, () => AddDualSheetWork(manager, sail, "Full Stbd", 1.00f, 0.00f)),
                DualWinchButton(manager, "3/4 Stbd", sail, () => AddDualSheetWork(manager, sail, "3/4 Stbd", 1.00f, 0.25f)),
                DualWinchButton(manager, "1/2 Stbd", sail, () => AddDualSheetWork(manager, sail, "1/2 Stbd", 1.00f, 0.50f)),
                DualWinchButton(manager, "1/4 Stbd", sail, () => AddDualSheetWork(manager, sail, "1/4 Stbd", 1.00f, 0.75f)));
            AddRow(DualWinchButton(manager, "Trim", sail, () => manager.AddJibTrimRequest(new JibTrimRequest(sail))));
        }

        private static void AddDualSheetWork(VirtualCrewManager manager, DualSheetSail sail, string label, float portTarget, float starboardTarget)
        {
            manager.AddWorkRequest(new WorkRequest(sail, "Port Sheet " + label, new WinchTarget(sail.getPortSheetWinch(), portTarget)));
            manager.AddWorkRequest(new WorkRequest(sail, "Starboard Sheet " + label, new WinchTarget(sail.getStarboardSheetWinch(), starboardTarget)));
        }

        private GameObject WinchButton(VirtualCrewManager manager, string text, GPButtonRopeWinch winch, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = CreateButton(text, action);
            var button = buttonObject.GetComponent<Button>();
            _winchButtons.Add(new WinchButtonBinding(button, winch, null));
            button.interactable = !IsWinchPending(manager, winch);
            return buttonObject;
        }

        private GameObject DualWinchButton(VirtualCrewManager manager, string text, DualSheetSail sail, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = CreateButton(text, action);
            var button = buttonObject.GetComponent<Button>();
            var port = sail.getPortSheetWinch();
            var starboard = sail.getStarboardSheetWinch();
            _winchButtons.Add(new WinchButtonBinding(button, port, starboard));
            button.interactable = !IsWinchPending(manager, port) && !IsWinchPending(manager, starboard);
            return buttonObject;
        }

        private void RefreshState(VirtualCrewManager manager)
        {
            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState"))
            {
                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.VesselLabel"))
                {
                    string vesselKey = manager.CurrentVesselKey;
                    string vesselFriendly = manager.CurrentVesselFriendlyName;
                    string vesselDisplay = !string.IsNullOrEmpty(vesselFriendly) ? vesselFriendly
                        : !string.IsNullOrEmpty(vesselKey) ? vesselKey
                        : "(No vessel - press V to scan)";
                    if (_vesselLabel)
                        _vesselLabel.text = "Vessel: " + vesselDisplay;
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.AnchorButtons"))
                {
                    if (_dropAnchorButton || _raiseAnchorButton)
                    {
                        bool anchorBusy = AreAnyWinchesPending(manager, manager.AnchorWinches);
                        if (_dropAnchorButton) _dropAnchorButton.interactable = !anchorBusy;
                        if (_raiseAnchorButton) _raiseAnchorButton.interactable = !anchorBusy;
                    }
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.MooringButtons"))
                {
                    RefreshMooringButtonState(manager);
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.WinchButtons"))
                {
                    foreach (var binding in _winchButtons)
                        if (binding.Button)
                            binding.Button.interactable = !IsWinchPending(manager, binding.Primary) && !IsWinchPending(manager, binding.Secondary);
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.SailButtons"))
                {
                    foreach (var kv in _sailButtons)
                    {
                        if (!kv.Value.Button) continue;
                        if (kv.Value.Text) kv.Value.Text.text = kv.Key.getSailName();
                        SetButtonSelected(kv.Value.Button, kv.Value.Text, kv.Key == selectedSail);
                    }
                }
            }
        }

        private void ClearContent()
        {
            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.ClearContent"))
            {
                _winchButtons.Clear();
                _mooringButtons.Clear();
                _sailButtons.Clear();
                _dropAnchorButton = null;
                _raiseAnchorButton = null;
                _vesselLabel = null;
                _vesselRenameInput = null;
                _sailRenameInput = null;

                for (int i = _contentRect.childCount - 1; i >= 0; i--)
                    Destroy(_contentRect.GetChild(i).gameObject);
            }
        }

        private Text AddLabel(string text)
        {
            var row = CreateRow("Label Row");
            row.AddComponent<Image>().color = GetLabelColor();
            var drag = row.AddComponent<DragHandler>();
            drag.Initialize(this);
            var label = AddText(row.transform, text, 18, TextAnchor.MiddleCenter);
            label.font = _labelFont ?? _buttonFont;
            SetPreferredHeight(row, 24f);
            return label;
        }

        private void AddSpacer(float height)
        {
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(_contentRect, false);
            SetPreferredHeight(spacer, height);
        }

        private void AddRow(params GameObject[] children)
        {
            var row = CreateRow("Button Row");
            var group = row.AddComponent<HorizontalLayoutGroup>();
            group.spacing = RowGap;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = true;
            SetPreferredHeight(row, RowHeight);

            foreach (var child in children)
                child.transform.SetParent(row.transform, false);
        }

        private GameObject CreateRow(string name)
        {
            var row = new GameObject(name);
            row.transform.SetParent(_contentRect, false);
            var rect = row.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            return row;
        }

        private InputField CreateInput(string text)
        {
            var inputObject = new GameObject("Input");
            var image = inputObject.AddComponent<Image>();
            image.color = GetFieldColor();
            var input = inputObject.AddComponent<InputField>();

            var textRect = CreateRect("Text", inputObject.transform);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 2f);
            textRect.offsetMax = new Vector2(-6f, -2f);
            var textComponent = AddText(textRect.transform, text ?? "", 14, TextAnchor.MiddleLeft);
            input.textComponent = textComponent;
            input.text = text ?? "";
            SetPreferredHeight(inputObject, RowHeight);
            return input;
        }

        private GameObject CreateButton(string text, UnityEngine.Events.UnityAction action, float preferredWidth = 0f)
        {
            var buttonObject = new GameObject("Button " + text);
            var image = buttonObject.AddComponent<Image>();
            image.color = Color.white;
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = GetButtonColors();
            button.onClick.AddListener(() =>
            {
                action?.Invoke();
                _pendingRopeCacheFrame = -1;
                RefreshState(VirtualCrewManager.Instance);
            });

            var textRect = CreateRect("Text", buttonObject.transform);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            AddText(textRect.transform, text, 14, TextAnchor.MiddleCenter);

            var layout = buttonObject.AddComponent<LayoutElement>();
            layout.preferredHeight = RowHeight;
            if (preferredWidth > 0f)
            {
                layout.preferredWidth = preferredWidth;
                layout.flexibleWidth = 0f;
            }
            else
            {
                layout.flexibleWidth = 1f;
            }

            return buttonObject;
        }

        private Text AddText(Transform parent, string text, int fontSize, TextAnchor alignment)
        {
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(parent, false);
            var rect = textObject.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var component = textObject.AddComponent<Text>();
            component.font = _buttonFont;
            component.fontSize = fontSize;
            component.alignment = alignment;
            component.color = GetTextColor();
            component.text = text;
            return component;
        }

        private RectTransform CreateRect(string name, Transform parent)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj.AddComponent<RectTransform>();
        }

        private static void SetPreferredHeight(GameObject obj, float height)
        {
            var layout = obj.GetComponent<LayoutElement>() ?? obj.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
        }

        private void SetButtonSelected(Button button, Text text, bool selected)
        {
            if (text)
                text.color = selected ? Color.cyan : GetTextColor();
        }

        private ColorBlock GetButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;
            bool dark = SailwindGuiStyle.IsDarkMode;
            colors.normalColor = dark ? new Color(78f / 255f, 60f / 255f, 47f / 255f, 1f) : GetButtonNormalColor();
            colors.highlightedColor = dark ? new Color(98f / 255f, 76f / 255f, 59f / 255f, 1f) : new Color(244f / 255f, 204f / 255f, 173f / 255f, 1f);
            colors.pressedColor = dark ? new Color(116f / 255f, 86f / 255f, 62f / 255f, 1f) : new Color(196f / 255f, 150f / 255f, 118f / 255f, 1f);
            colors.disabledColor = dark ? new Color(0.16f, 0.14f, 0.12f, 0.72f) : new Color(0.45f, 0.42f, 0.38f, 0.72f);
            colors.colorMultiplier = 1f;
            return colors;
        }

        private Font FindUiFont(string exactName, string partialName)
        {
            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                if (font.name == exactName)
                    return font;

            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                if (font.name.IndexOf(partialName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return font;

            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private void ApplyThemeIfNeeded()
        {
            bool fontsChanged = AssignLegacyFonts();
            bool dark = SailwindGuiStyle.IsDarkMode;
            if (!fontsChanged && _themeApplied && _lastDarkMode == dark)
                return;

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            AssignLegacyFonts();

            bool dark = SailwindGuiStyle.IsDarkMode;
            _themeApplied = true;
            _lastDarkMode = dark;

            if (_panelImage) _panelImage.color = GetWindowColor();
            if (_headerImage) _headerImage.color = GetHeaderColor();
            if (_resizeHandleImage) _resizeHandleImage.color = GetResizeHandleColor();

            var colors = GetButtonColors();
            foreach (var button in _rootObject.GetComponentsInChildren<Button>(true))
            {
                button.colors = colors;
                if (button.targetGraphic)
                    button.targetGraphic.color = button.interactable ? colors.normalColor : colors.disabledColor;
            }

            foreach (var image in _rootObject.GetComponentsInChildren<Image>(true))
            {
                if (image.gameObject.name == "Label Row")
                    image.color = GetLabelColor();
                else if (image.gameObject.name == "Input")
                    image.color = GetFieldColor();
            }

            foreach (var text in _rootObject.GetComponentsInChildren<Text>(true))
                text.color = GetTextColor();
        }

        private bool AssignLegacyFonts()
        {
            var buttonFont = SailwindGuiStyle.ButtonFont ?? FindUiFont("ArchitectsDaughter", "Architects");
            var labelFont = SailwindGuiStyle.LabelFont ?? FindUiFont("IMMORTAL", "IMMORTAL") ?? buttonFont;
            bool changed = buttonFont != _buttonFont || labelFont != _labelFont;
            _buttonFont = buttonFont;
            _labelFont = labelFont;

            if (!_rootObject || !changed)
                return changed;

            foreach (var text in _rootObject.GetComponentsInChildren<Text>(true))
                text.font = IsLabelText(text) ? _labelFont : _buttonFont;

            return changed;
        }

        private static bool IsLabelText(Text text)
        {
            return text && text.transform.parent && text.transform.parent.name == "Label Row";
        }

        private static Color GetWindowColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(38f / 255f, 32f / 255f, 27f / 255f, 0.74f)
                : new Color(0.18f, 0.17f, 0.15f, 0.56f);
        }

        private static Color GetHeaderColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.18f, 0.16f, 0.14f, 0.88f)
                : new Color(0.58f, 0.55f, 0.50f, 0.86f);
        }

        private static Color GetResizeHandleColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.30f, 0.24f, 0.19f, 0.92f)
                : new Color(116f / 255f, 92f / 255f, 73f / 255f, 0.92f);
        }

        private static Color GetButtonNormalColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(78f / 255f, 60f / 255f, 47f / 255f, 1f)
                : new Color(230f / 255f, 187f / 255f, 156f / 255f, 1f);
        }

        private static Color GetLabelColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(55f / 255f, 42f / 255f, 35f / 255f, 0.92f)
                : new Color(153f / 255f, 103f / 255f, 93f / 255f, 0.92f);
        }

        private static Color GetFieldColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(46f / 255f, 38f / 255f, 32f / 255f, 1f)
                : new Color(242f / 255f, 218f / 255f, 190f / 255f, 1f);
        }

        private static Color GetTextColor()
        {
            return SailwindGuiStyle.IsDarkMode
                ? new Color(0.82f, 0.75f, 0.65f, 1f)
                : Color.black;
        }

        private void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
                return;

            var obj = new GameObject("EventSystem");
            obj.AddComponent<EventSystem>();
            obj.AddComponent<StandaloneInputModule>();
        }

        private void UpdateVisibility()
        {
            if (_rootObject)
                _rootObject.SetActive(showWindow && WindowLayoutUtility.ModLayerVisible);
        }

        private void ApplyWindowRect()
        {
            if (!_panelRect)
                return;

            float height = _userHeight > 0f ? _userHeight : DefaultHeight;
            windowRect.width = Mathf.Max(DefaultWidth, windowRect.width);
            windowRect.height = Mathf.Max(120f, height);
            WindowLayoutUtility.ClampToScreen(ref windowRect);

            _panelRect.anchoredPosition = new Vector2(windowRect.x, -windowRect.y);
            _panelRect.sizeDelta = new Vector2(windowRect.width, windowRect.height);
        }

        private void MoveWindow(Vector2 screenDelta)
        {
            windowRect.x += screenDelta.x;
            windowRect.y -= screenDelta.y;
            ApplyWindowRect();
        }

        private void ResizeWindow(float deltaY)
        {
            _userHeight = Mathf.Max(120f, (_userHeight > 0f ? _userHeight : windowRect.height) - deltaY);
            ApplyWindowRect();
        }

        private void RequestRebuild()
        {
            _rebuildRequested = true;
        }

        private void InvalidateMooringAvailability()
        {
            _nextMooringAvailabilityRefresh = 0f;
            _lastMooringRequestCount = -1;
        }

        private void RefreshMooringButtonState(VirtualCrewManager manager)
        {
            int requestCount = manager.MooringRequests.Count;
            bool shouldRefresh = Time.realtimeSinceStartup >= _nextMooringAvailabilityRefresh
                || requestCount != _lastMooringRequestCount;

            if (shouldRefresh)
            {
                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.MooringButtons.Scan"))
                {
                    int portCount;
                    int starboardCount;
                    using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.MooringButtons.Scan.AvailabilityCounts"))
                        MooringLocator.GetAvailableRopeCounts(null, out portCount, out starboardCount);

                    _canMoorPort = !manager.HasPendingMooringRequest(MooringSide.Port) && portCount > 0;
                    _canMoorStarboard = !manager.HasPendingMooringRequest(MooringSide.Starboard) && starboardCount > 0;

                    _lastMooringRequestCount = requestCount;
                    _nextMooringAvailabilityRefresh = Time.realtimeSinceStartup + MooringAvailabilityRefreshSeconds;
                }
            }

            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.RefreshState.MooringButtons.Apply"))
            {
                foreach (var binding in _mooringButtons)
                {
                    if (!binding.Button)
                        continue;

                    binding.Button.interactable = binding.Side == MooringSide.Port
                        ? _canMoorPort
                        : _canMoorStarboard;
                }
            }
        }

        private bool IsWinchPending(VirtualCrewManager manager, GPButtonRopeWinch winch)
        {
            if (!winch || winch.rope == null)
                return false;

            EnsurePendingRopeCache(manager);
            return _pendingRopes.Contains(winch.rope);
        }

        private bool AreAnyWinchesPending(VirtualCrewManager manager, IEnumerable<GPButtonRopeWinch> winches)
        {
            EnsurePendingRopeCache(manager);
            foreach (var winch in winches)
                if (winch && winch.rope != null && _pendingRopes.Contains(winch.rope))
                    return true;

            return false;
        }

        private void EnsurePendingRopeCache(VirtualCrewManager manager)
        {
            if (_pendingRopeCacheFrame == Time.frameCount)
                return;

            using (PerformanceInstrumentation.MeasureUGui("Deck Orders.PendingRopeCache.Build"))
            {
                _pendingRopeCacheFrame = Time.frameCount;
                _pendingRopes.Clear();

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.PendingRopeCache.WorkRequests"))
                {
                    foreach (var request in manager.WorkRequests)
                    {
                        if (request.Status == WorkRequestStatus.Complete || request.Targets == null)
                            continue;

                        foreach (var target in request.Targets)
                            AddPendingRope(target.Winch);
                    }
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.PendingRopeCache.TrimRequests"))
                {
                    foreach (var request in manager.TrimRequests)
                        if (request.Status != WorkRequestStatus.Complete)
                            AddPendingRope(request.Sail.getSheetWinch());
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.PendingRopeCache.JibTrimRequests"))
                {
                    foreach (var request in manager.JibTrimRequests)
                    {
                        if (request.Status == WorkRequestStatus.Complete)
                            continue;

                        AddPendingRope(request.Sail.getPortSheetWinch());
                        AddPendingRope(request.Sail.getStarboardSheetWinch());
                    }
                }

                using (PerformanceInstrumentation.MeasureUGui("Deck Orders.PendingRopeCache.SquareTrimRequests"))
                {
                    foreach (var request in manager.SquareTrimRequests)
                    {
                        if (request.Status == WorkRequestStatus.Complete)
                            continue;

                        AddPendingRope(request.Sail.getPortSheetWinch());
                        AddPendingRope(request.Sail.getStarboardSheetWinch());
                    }
                }
            }
        }

        private void AddPendingRope(GPButtonRopeWinch winch)
        {
            if (winch && winch.rope != null)
                _pendingRopes.Add(winch.rope);
        }

        private struct WinchButtonBinding
        {
            internal readonly Button Button;
            internal readonly GPButtonRopeWinch Primary;
            internal readonly GPButtonRopeWinch Secondary;

            internal WinchButtonBinding(Button button, GPButtonRopeWinch primary, GPButtonRopeWinch secondary)
            {
                Button = button;
                Primary = primary;
                Secondary = secondary;
            }
        }

        private struct MooringButtonBinding
        {
            internal readonly Button Button;
            internal readonly MooringSide Side;

            internal MooringButtonBinding(Button button, MooringSide side)
            {
                Button = button;
                Side = side;
            }
        }

        private struct SailButtonBinding
        {
            internal readonly Button Button;
            internal readonly Text Text;

            internal SailButtonBinding(Button button, Text text)
            {
                Button = button;
                Text = text;
            }
        }

        private sealed class DragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
        {
            private CrewWindow _owner;
            private Vector2 _last;

            internal void Initialize(CrewWindow owner) => _owner = owner;

            public void OnBeginDrag(PointerEventData eventData)
            {
                _last = eventData.position;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (_owner == null) return;
                Vector2 delta = eventData.position - _last;
                _last = eventData.position;
                _owner.MoveWindow(delta);
            }
        }

        private sealed class ResizeHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
        {
            private CrewWindow _owner;
            private Vector2 _last;

            internal void Initialize(CrewWindow owner) => _owner = owner;

            public void OnBeginDrag(PointerEventData eventData)
            {
                _last = eventData.position;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (_owner == null) return;
                Vector2 delta = eventData.position - _last;
                _last = eventData.position;
                _owner.ResizeWindow(delta.y);
            }
        }
    }
}
