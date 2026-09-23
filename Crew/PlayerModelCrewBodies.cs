using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using SailwindPlayerModel;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>What a crew body is doing with its hands this frame.</summary>
    internal enum CrewBodyAction
    {
        None,
        Helm,
        Crank,
        Push,
        Rope,
    }

    /// <summary>Ways to sit on the deck itself (the Player Model mod's floor seat poses).</summary>
    internal enum CrewSeatPose
    {
        LegsOut,
        CrossLegged,
        KneeUp,
        KneesHugged,
    }

    /// <summary>
    /// An animated crew body. Callers set the per-frame inputs and then call Tick once the root has been placed.
    /// Everything but SpeedMps is per-frame: one frame without the call and the body eases out of it.
    /// </summary>
    internal interface ICrewBodyAnimator
    {
        float SpeedMps { set; }
        void SetAction(CrewBodyAction action, Transform target);
        // Tilts the head and upper body up or down toward a point, within what a neck and back can do.
        void SetLookTarget(Vector3 worldPoint);
        // Holds an item in the hands, gripped to suit it. True when the body places the item itself this frame;
        // otherwise the item stays wherever the caller put it.
        bool SetHeldItem(Transform item, bool big);
        // Lies on the back: head at headWorld, feet toward alongWorld, chest toward upWorld.
        void SetLying(Vector3 headWorld, Vector3 alongWorld, Vector3 upWorld);
        // Sits on the deck: hip joints at hipsWorld, facing forwardWorld, over a floor at floorWorldY.
        void SetSeat(Vector3 hipsWorld, Vector3 forwardWorld, CrewSeatPose pose, float floorWorldY);
        // Re-dresses the body in place in another look (a PlayerAppearance string).
        void RefreshAppearance(string appearance);
        void Tick(float deltaTime);
        void Destroy();
    }

    /// <summary>
    /// An open edit of one crewman's look: a list of rows (gender, each part, each color) that step through their
    /// options, with a rotating preview. See CrewAppearanceWindow.
    /// </summary>
    internal interface ICrewAppearanceEditor
    {
        int RowCount { get; }
        bool IsRowShown(int row);
        string RowLabel(int row);
        string RowValue(int row);
        void Step(int row, int direction);
        void Randomize();
        // The preview render, or null when there is none.
        Texture PreviewTexture { get; }
        float PreviewHeightPerWidth { get; }
        void TurnPreview(float degrees);
        // The edited look, as a PlayerAppearance string.
        string Result { get; }
        // Ends the edit. Without keeping, the crewman's body goes back to the look it had.
        void Close(bool keep);
    }

    /// <summary>
    /// Optional integration with the Sailwind Player Model mod, which builds humanoid bodies and animates them
    /// procedurally (walk gait, hands on the helm and winches). Every reference to a SailwindPlayerModel type
    /// lives in this file behind non-inlined methods, so nothing touches that assembly unless the plugin is
    /// actually loaded. Each entry point checks IsEnabled and only then calls a separate *Core method: compiling
    /// a method that names one of the classes below can load it, and their fields are Player Model types.
    /// </summary>
    internal static class PlayerModelCrewBodies
    {
        private const string Phase = "PlayerModel";
        internal const string PluginGuid = "com.diamondminer99.playermodel";

        private static bool? _available;

        internal static bool IsEnabled
        {
            get
            {
                if (Plugin.UsePlayerModelBodies != null && !Plugin.UsePlayerModelBodies.Value)
                    return false;

                if (!_available.HasValue)
                    _available = CheckAvailable();
                return _available.Value;
            }
        }

        // Checked once, on first use in game, long after every plugin's Awake has run.
        private static bool CheckAvailable()
        {
            if (!Chainloader.PluginInfos.ContainsKey(PluginGuid))
            {
                CrewDebugLog.Ok(Phase, "Sailwind Player Model not installed; crew use cloned NPC bodies.");
                return false;
            }

            // Installed is not the same as running: next to an older Sailwind Co-op it stands down in Awake,
            // binding none of its settings, and its bodies would then throw on their first frame.
            bool loaded;
            try
            {
                loaded = IsLoadedCore();
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Sailwind Player Model could not be checked (" + e.Message + "); crew use cloned NPC bodies.");
                return false;
            }

            if (!loaded)
                CrewDebugLog.Warn(Phase, "Sailwind Player Model is installed but not running (it stands down beside an older Sailwind Co-op); crew use cloned NPC bodies.");
            return loaded;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsLoadedCore()
        {
            return BodyTuning.CrouchDropMeters != null
                && InteractionTuning.Enabled != null
                && HeldToolPose.Mode != null
                && ItemPoseTuning.CarryRight != null;
        }

        /// <summary>
        /// Build a Player Model body under <paramref name="root"/>, whose origin is at the crewman's feet, wearing
        /// <paramref name="appearance"/> (a PlayerAppearance string, see <see cref="DefaultAppearance"/>). Returns
        /// null when the mod is absent, its NPC template has not loaded yet, or the build failed.
        /// </summary>
        internal static ICrewBodyAnimator TryCreate(Transform root, string name, string appearance)
        {
            if (!root || !IsEnabled)
                return null;

            return TryCreateCore(root, name, appearance);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ICrewBodyAnimator TryCreateCore(Transform root, string name, string appearance)
        {
            try
            {
                return PlayerModelCrewBody.TryBuild(root, name, appearance);
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Player Model body build failed for '" + name + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The look a crewman gets before anyone chooses one: varied parts and colors picked from
        /// <paramref name="seed"/> (the crewman's id, so it is the same look every time), male or female.
        /// Null when the mod is absent.
        /// </summary>
        internal static string DefaultAppearance(string seed, bool female)
        {
            if (!IsEnabled)
                return null;

            return DefaultAppearanceCore(seed, female);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string DefaultAppearanceCore(string seed, bool female)
        {
            try
            {
                return PlayerModelAppearanceEditor.DefaultAppearance(StableSeed(seed), female);
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Could not make a default appearance: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Start editing a look. <paramref name="liveBody"/>, if the crewman has one, is restyled as the edit
        /// goes. Null when the mod is absent or its NPC template has not loaded yet.
        /// </summary>
        internal static ICrewAppearanceEditor TryBeginEdit(string appearance, ICrewBodyAnimator liveBody)
        {
            if (!IsEnabled || string.IsNullOrEmpty(appearance))
                return null;

            return TryBeginEditCore(appearance, liveBody);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ICrewAppearanceEditor TryBeginEditCore(string appearance, ICrewBodyAnimator liveBody)
        {
            try
            {
                return PlayerModelAppearanceEditor.TryOpen(appearance, liveBody);
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Could not open the appearance editor: " + e.Message);
                return null;
            }
        }

        // FNV-1a, so a crewman keeps the same look across sessions (string.GetHashCode is not guaranteed stable).
        private static ulong StableSeed(string value)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in value ?? string.Empty)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash == 0UL ? 1UL : hash;
        }
    }

    internal sealed class PlayerModelCrewBody : ICrewBodyAnimator
    {
        // A winch below this fraction of the body's height (about the waist) is worked from a squat. The squat
        // deepens the lower the winch sits, reaching full crouch once it is a full crouch drop below the waist.
        private const float CrankWaistHeightFraction = 0.55f;
        private const float StandingHeightFallback = 1.8f;
        private const float EyeHeightFraction = 0.93f;
        // Neck and back together: a crewman can look well up a mast, less far down.
        private const float MaxLookUpDeg = 60f;
        private const float MaxLookDownDeg = 45f;
        // The Player Model mod reads a bottle or food item's distance from the head as how far along a drink or a
        // bite it is (the game's hold distance is 1.15 m). Held items are reported this far out, in front of the
        // chest, so a carried cup or loaf stays carried.
        private const float RestingItemForward = 1.1f;
        private const float RestingItemHeightFraction = 0.55f;

        private readonly SyntyBody _body;
        private readonly Transform _root;
        private readonly string _name;
        private float _crouchThisFrame;
        private float _lookPitchThisFrame;
        // Set when the Player Model mod throws. The body is then left as it stands rather than letting the
        // exception out into our per-frame update, where it would stop everything after it every frame.
        private bool _faulted;

        private PlayerModelCrewBody(SyntyBody body, Transform root, string name)
        {
            _body = body;
            _root = root;
            _name = name;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ICrewBodyAnimator TryBuild(Transform root, string name, string appearance)
        {
            var body = SyntyBody.TryBuild(root, name, PlayerAppearance.Deserialize(appearance), 0, () => 0f);
            return body != null ? new PlayerModelCrewBody(body, root, name) : null;
        }

        private void Fault(string call, Exception e)
        {
            if (_faulted)
                return;

            _faulted = true;
            CrewDebugLog.Warn("PlayerModel", "Body '" + _name + "' stopped animating after " + call + " threw: " + e);
        }

        public float SpeedMps
        {
            set { if (!_faulted) _body.SpeedMps = value; }
        }

        public void SetAction(CrewBodyAction action, Transform target)
        {
            if (_faulted || action == CrewBodyAction.None || !target)
                return;

            try
            {
                _body.SetInteraction(ToInteractionKind(action), target);
                if (action == CrewBodyAction.Crank)
                    _crouchThisFrame = Mathf.Max(_crouchThisFrame, CrouchForLowControl(target));
            }
            catch (Exception e) { Fault("SetAction", e); }
        }

        public void SetLookTarget(Vector3 worldPoint)
        {
            if (_faulted || !_root)
                return;

            Vector3 eye = _root.position + _root.up * (StandingHeight() * EyeHeightFraction);
            Vector3 toTarget = worldPoint - eye;
            float rise = Vector3.Dot(toTarget, _root.up);
            float across = Vector3.ProjectOnPlane(toTarget, _root.up).magnitude;
            float pitch = Mathf.Atan2(rise, Mathf.Max(across, 0.01f)) * Mathf.Rad2Deg;
            _lookPitchThisFrame = Mathf.Clamp(pitch, -MaxLookDownDeg, MaxLookUpDeg);
        }

        public bool SetHeldItem(Transform item, bool big)
        {
            if (_faulted || !item || !_root)
                return false;

            try
            {
                Vector3 restingPosition = _root.position
                    + _root.forward * RestingItemForward
                    + _root.up * (StandingHeight() * RestingItemHeightFraction);
                _body.SetHeldItemPose(item, null, restingPosition, item.rotation, big);
                return _body.PlacesHeldItemInHand;
            }
            catch (Exception e)
            {
                Fault("SetHeldItem", e);
                return false;
            }
        }

        public void SetLying(Vector3 headWorld, Vector3 alongWorld, Vector3 upWorld)
        {
            if (!_faulted)
                _body.SetLying(headWorld, alongWorld, upWorld);
        }

        public void SetSeat(Vector3 hipsWorld, Vector3 forwardWorld, CrewSeatPose pose, float floorWorldY)
        {
            if (!_faulted)
                _body.SetSeat(hipsWorld, forwardWorld, ToSeatPose(pose), floorWorldY);
        }

        private static SeatPose ToSeatPose(CrewSeatPose pose)
        {
            switch (pose)
            {
                case CrewSeatPose.CrossLegged: return SeatPose.FloorCrossLegged;
                case CrewSeatPose.KneeUp: return SeatPose.FloorKneeUp;
                case CrewSeatPose.KneesHugged: return SeatPose.FloorKneesHugged;
                default: return SeatPose.FloorLegsOut;
            }
        }

        public void RefreshAppearance(string appearance)
        {
            if (_faulted || string.IsNullOrEmpty(appearance))
                return;

            try { _body.RefreshAppearance(PlayerAppearance.Deserialize(appearance)); }
            catch (Exception e) { Fault("RefreshAppearance", e); }
        }

        public void Tick(float deltaTime)
        {
            if (_faulted)
                return;

            // Like the interaction, the crouch and the look last only while they are asked for each frame; the
            // body eases back once they stop.
            _body.Crouch01Target = _crouchThisFrame;
            _body.LookPitchDegTarget = _lookPitchThisFrame;
            _crouchThisFrame = 0f;
            _lookPitchThisFrame = 0f;
            try { _body.Tick(deltaTime); }
            catch (Exception e) { Fault("Tick", e); }
        }

        private float StandingHeight()
        {
            return _body.MeasuredHeight > 0.5f ? _body.MeasuredHeight : StandingHeightFallback;
        }

        private float CrouchForLowControl(Transform target)
        {
            if (!_root)
                return 0f;

            float waist = StandingHeight() * CrankWaistHeightFraction;
            float controlHeight = Vector3.Dot(target.position - _root.position, _root.up);
            float drop = BodyTuning.CrouchDropMeters != null ? BodyTuning.CrouchDropMeters.Value : 0.6f;
            if (drop <= 0.01f)
                return 0f;

            return Mathf.Clamp01((waist - controlHeight) / drop);
        }

        public void Destroy()
        {
            try { _body.Destroy(); }
            catch (Exception e) { CrewDebugLog.Warn("PlayerModel", "Body '" + _name + "' teardown threw: " + e.Message); }
        }

        private static InteractionKind ToInteractionKind(CrewBodyAction action)
        {
            switch (action)
            {
                case CrewBodyAction.Helm: return InteractionKind.Helm;
                case CrewBodyAction.Crank: return InteractionKind.Crank;
                case CrewBodyAction.Push: return InteractionKind.Push;
                case CrewBodyAction.Rope: return InteractionKind.Rope;
                default: return InteractionKind.None;
            }
        }
    }

    /// <summary>
    /// Edits a crewman's look with the Player Model mod's own pieces: its slot and color lists and its option
    /// counting and wrapping (the same rules as its character screen), with a <see cref="CrewPreviewStudio"/> for
    /// the rotating model. The character screen itself only edits the player, so this is our own front end.
    /// </summary>
    internal sealed class PlayerModelAppearanceEditor : ICrewAppearanceEditor
    {
        private const int GenderSlot = 0;
        private const string FacialHairKey = "facialhair";

        private readonly ICrewBodyAnimator _liveBody;
        private readonly string _original;
        private readonly bool _femaleAvailable;
        private CrewPreviewStudio _studio;
        // The look being edited, kept as PlayerAppearance's two arrays rather than as a PlayerAppearance field. A
        // struct field from the Player Model assembly makes this class fail to load when that mod is absent, which
        // breaks anything that lists our types (Assembly.GetTypes). Working wraps the arrays again, and writes
        // through it land in them (they are created full size, so PlayerAppearance never swaps in new ones).
        private readonly byte[] _values;
        private readonly byte[] _colors;
        private PlayerAppearance Working => new PlayerAppearance { Values = _values, Colors = _colors };

        private void SetValue(int slot, byte value)
        {
            var working = Working;
            working[slot] = value;
        }

        private PlayerModelAppearanceEditor(PlayerAppearance working, string original, ICrewBodyAnimator liveBody)
        {
            _values = working.Values;
            _colors = working.Colors;
            _original = original;
            _liveBody = liveBody;
            _femaleAvailable = FemaleAvailable();
            if (!_femaleAvailable)
                SetValue(GenderSlot, 1);
            Normalize();
            // A color saved as 0 means "as the cloned NPC wears it"; show the nearest named color instead, so
            // every row reads as a choice (as the character screen does).
            for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
            {
                if (Working.GetColor(i) == 0)
                {
                    int near = PlayerAppearance.NearestPaletteIndex(i);
                    if (near > 0)
                        Working.SetColor(i, (byte)near);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string DefaultAppearance(ulong seed, bool female)
        {
            // The seeded look is the one crew wore before looks were saved, so existing crew keep their faces.
            var look = PlayerAppearance.DeterministicFor(seed);
            look[GenderSlot] = (byte)(female ? 2 : 1);
            return look.Serialize();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ICrewAppearanceEditor TryOpen(string appearance, ICrewBodyAnimator liveBody)
        {
            if (!BodyTemplate.Available)
                return null;

            // A copy: PlayerAppearance is a struct over a byte array, so it must not share one with the saved look.
            var working = PlayerAppearance.Deserialize(appearance);
            working[GenderSlot] = (byte)(working[GenderSlot] == 2 ? 2 : 1);
            var editor = new PlayerModelAppearanceEditor(working, appearance, liveBody);
            editor._studio = CrewPreviewStudio.TryOpen(editor.Result);
            return editor;
        }

        public int RowCount => PlayerAppearance.SlotCount + PlayerAppearance.ColorSlotCount;

        public bool IsRowShown(int row)
        {
            if (row == GenderSlot)
                return _femaleAvailable;
            // Facial hair is male-only in the game's rig.
            return !(row < PlayerAppearance.SlotCount
                     && PlayerAppearance.Slots[row].Key == FacialHairKey
                     && Working[GenderSlot] == 2);
        }

        public string RowLabel(int row)
        {
            return row < PlayerAppearance.SlotCount
                ? PlayerAppearance.Slots[row].Label
                : PlayerAppearance.ColorSlots[row - PlayerAppearance.SlotCount].Label;
        }

        public string RowValue(int row)
        {
            if (row == GenderSlot)
                return Working[GenderSlot] == 2 ? "Female" : "Male";

            if (row < PlayerAppearance.SlotCount)
            {
                int count = CountOptions(row);
                int value = Working[row];
                if (count <= 0)
                    return "-";
                return value == 0 ? "None" : value + " / " + count;
            }

            int colorSlot = row - PlayerAppearance.SlotCount;
            return PlayerAppearance.DescribeColor(colorSlot, Working.GetColor(colorSlot));
        }

        public void Step(int row, int direction)
        {
            if (row == GenderSlot)
            {
                if (!_femaleAvailable)
                    return;
                SetValue(GenderSlot, (byte)(Working[GenderSlot] == 2 ? 1 : 2));
                // Every other part counts against the other gender's lists now.
                Normalize();
            }
            else if (row < PlayerAppearance.SlotCount)
            {
                int count = CountOptions(row);
                if (count <= 0)
                    return;
                SetValue(row, PlayerAppearance.NormalizeValue(row, count, Working[row] + direction));
            }
            else
            {
                int colorSlot = row - PlayerAppearance.SlotCount;
                int options = PlayerAppearance.ColorSlots[colorSlot].Palette.Length;
                if (options <= 0)
                    return;
                int value = Working.GetColor(colorSlot);
                int next = ((value - 1 + direction) % options + options) % options + 1;
                Working.SetColor(colorSlot, (byte)next);
            }

            Preview();
        }

        // Everything but gender, which is a choice the player makes on purpose.
        public void Randomize()
        {
            for (int i = 1; i < PlayerAppearance.SlotCount; i++)
            {
                int count = CountOptions(i);
                if (count > 0)
                    SetValue(i, PlayerAppearance.NormalizeValue(i, count, UnityEngine.Random.Range(0, count + 1)));
            }
            for (int i = 0; i < PlayerAppearance.ColorSlotCount; i++)
                Working.SetColor(i, (byte)UnityEngine.Random.Range(1, PlayerAppearance.ColorSlots[i].Palette.Length + 1));

            Preview();
        }

        public Texture PreviewTexture => _studio != null ? _studio.Texture : null;

        public float PreviewHeightPerWidth => CrewPreviewStudio.HeightPerWidth;

        public void TurnPreview(float degrees)
        {
            _studio?.Turn(degrees);
        }

        public string Result => Working.Serialize();

        public void Close(bool keep)
        {
            _studio?.Close();
            _studio = null;

            if (!keep)
                _liveBody?.RefreshAppearance(_original);
        }

        private void Preview()
        {
            try
            {
                _studio?.SetAppearance(Working.Serialize());
                _liveBody?.RefreshAppearance(Working.Serialize());
            }
            catch (Exception e) { CrewDebugLog.Warn("PlayerModel", "Appearance preview failed: " + e.Message); }
        }

        // Fold every part into the range the chosen gender actually has.
        private void Normalize()
        {
            for (int i = 1; i < PlayerAppearance.SlotCount; i++)
            {
                int count = CountOptions(i);
                if (count > 0)
                    SetValue(i, PlayerAppearance.NormalizeValue(i, count, Working[i]));
            }
        }

        // Options are counted on the shared body template, whose parts lists are split by gender. Its gender flag
        // is set to the one being edited just for the count and put straight back.
        private int CountOptions(int slot)
        {
            var customizer = TemplateCustomizer();
            if (customizer == null)
                return 0;

            bool wasFemale = customizer.isFemale;
            try
            {
                customizer.isFemale = Working[GenderSlot] == 2;
                return PlayerAppearance.VariantCount(customizer, slot);
            }
            finally
            {
                customizer.isFemale = wasFemale;
            }
        }

        // Applying a look only makes a body female where the rig has female parts (see PlayerAppearance.Apply).
        private static bool FemaleAvailable()
        {
            var customizer = TemplateCustomizer();
            return customizer != null && customizer.female != null
                && customizer.female.torso != null && customizer.female.torso.Count > 0;
        }

        private static PsychoticLab.CharacterCustomizer TemplateCustomizer()
        {
            var template = BodyTemplate.Instance;
            return template != null ? template.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true) : null;
        }
    }

    /// <summary>
    /// A small photo studio for the appearance editor: a copy of the Player Model mod's body template, dressed
    /// in the look being edited, far below the world with its own lights and a camera that renders it into a
    /// texture once a frame. The mod has one of these, but it tears itself down whenever its own character
    /// screen is not open, so it cannot serve another window.
    ///
    /// Everything here lives on one layer that nothing in the game uses, and the lights and camera see only
    /// that layer, so the studio never shows up in, or lights, the game.
    /// </summary>
    internal sealed class CrewPreviewStudio
    {
        private const int StudioLayer = 31;
        private static readonly Vector3 StudioOrigin = new Vector3(0f, -9000f, 0f);
        private const int TextureWidth = 420;
        private const int TextureHeight = 620;
        private static readonly Color Background = new Color(0.42f, 0.28f, 0.24f);
        // Frames spent re-framing after a change: the game builds the model's mesh a frame after it is made or
        // restyled, and its size can change with a hat or a longer coat.
        private const int SettleFrames = 10;

        private GameObject _root;
        private GameObject _mannequin;
        private Camera _camera;
        private RenderTexture _texture;
        private float _yaw;
        private int _settleFrames;

        internal static float HeightPerWidth => (float)TextureHeight / TextureWidth;

        internal Texture Texture => _texture;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static CrewPreviewStudio TryOpen(string appearance)
        {
            var studio = new CrewPreviewStudio();
            try
            {
                studio.Build(appearance);
                return studio;
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn("PlayerModel", "Could not build the appearance preview: " + e.Message);
                studio.Close();
                return null;
            }
        }

        private void Build(string appearance)
        {
            var template = BodyTemplate.Instance;
            if (template == null)
                throw new InvalidOperationException("no body template yet");

            _root = new GameObject("VC_AppearanceStudio");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.transform.position = StudioOrigin;
            _root.AddComponent<CrewPreviewStudioTicker>().Studio = this;

            // The template is kept inactive, and a look has to be written before the copy wakes: the game builds
            // the model from it when it first activates.
            _mannequin = UnityEngine.Object.Instantiate(template);
            _mannequin.name = "VC_AppearanceMannequin";
            _mannequin.transform.SetParent(_root.transform, false);
            _mannequin.transform.localPosition = Vector3.zero;
            _mannequin.transform.localRotation = Quaternion.identity;
            PlayerAppearance.Deserialize(appearance).Apply(_mannequin);
            _mannequin.SetActive(true);
            foreach (var renderer in _mannequin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.enabled = true;
                renderer.allowOcclusionWhenDynamic = false;
            }
            SetLayerRecursive(_mannequin.transform, StudioLayer);

            MakeLight(new Vector3(30f, -30f, 0f), 1.15f, new Color(1f, 0.97f, 0.92f));
            MakeLight(new Vector3(15f, 160f, 0f), 0.55f, new Color(0.75f, 0.82f, 0.95f));

            // Disabled: rendered by hand once a frame, so it never draws into the game's own frame.
            var cameraObject = new GameObject("VC_AppearanceCamera");
            cameraObject.transform.SetParent(_root.transform, false);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.cullingMask = 1 << StudioLayer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Background;
            _camera.fieldOfView = 32f;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;

            _texture = new RenderTexture(TextureWidth, TextureHeight, 16) { antiAliasing = 2, hideFlags = HideFlags.HideAndDontSave };
            _camera.targetTexture = _texture;
            _settleFrames = SettleFrames;
        }

        internal void SetAppearance(string appearance)
        {
            if (_mannequin == null)
                return;

            var customizer = _mannequin.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
            if (customizer != null)
                PlayerAppearance.Deserialize(appearance).ApplyLive(customizer);
            // A restyle can switch on parts that are not on the studio layer yet.
            SetLayerRecursive(_mannequin.transform, StudioLayer);
            _settleFrames = SettleFrames;
        }

        internal void Turn(float degrees)
        {
            _yaw += degrees;
            if (_mannequin != null)
                _mannequin.transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        internal void Render()
        {
            if (_camera == null || _texture == null)
                return;

            if (_settleFrames > 0)
            {
                _settleFrames--;
                if (_mannequin != null)
                    SetLayerRecursive(_mannequin.transform, StudioLayer);
                FrameSubject();
            }

            _camera.Render();
        }

        internal void Close()
        {
            try
            {
                if (_camera != null)
                    _camera.targetTexture = null;
                if (_texture != null)
                {
                    _texture.Release();
                    UnityEngine.Object.Destroy(_texture);
                }
                if (_mannequin != null)
                    PlayerAppearance.ReleaseOwnedMaterial(_mannequin);
                if (_root != null)
                    UnityEngine.Object.Destroy(_root);
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn("PlayerModel", "Appearance preview teardown: " + e.Message);
            }
            _root = null;
            _mannequin = null;
            _camera = null;
            _texture = null;
        }

        // Aim the camera at the model as built, fitting it in both directions with a margin.
        private void FrameSubject()
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in _mannequin.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!any)
                return;

            float height = Mathf.Max(bounds.size.y, 0.1f);
            float width = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.z), 0.1f);
            float fovV = _camera.fieldOfView * Mathf.Deg2Rad;
            float fovH = 2f * Mathf.Atan(Mathf.Tan(fovV * 0.5f) * ((float)TextureWidth / TextureHeight));
            float distance = Mathf.Max((height * 0.5f) / Mathf.Tan(fovV * 0.5f), (width * 0.5f) / Mathf.Tan(fovH * 0.5f)) * 1.35f;

            Vector3 center = bounds.center;
            Vector3 position = center + new Vector3(0f, height * 0.04f, distance);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(center - position, Vector3.up);
            _camera.nearClipPlane = Mathf.Max(0.05f, distance - height * 3f);
            _camera.farClipPlane = distance + height * 6f;
        }

        private void MakeLight(Vector3 euler, float intensity, Color color)
        {
            var lightObject = new GameObject("VC_AppearanceLight");
            lightObject.transform.SetParent(_root.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(euler);
            lightObject.layer = StudioLayer;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.color = color;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << StudioLayer;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }
    }

    /// <summary>Renders a <see cref="CrewPreviewStudio"/> once a frame, after everything has moved.</summary>
    internal sealed class CrewPreviewStudioTicker : MonoBehaviour
    {
        internal CrewPreviewStudio Studio;

        private void LateUpdate()
        {
            try { Studio?.Render(); }
            catch (Exception e)
            {
                CrewDebugLog.Warn("PlayerModel", "Appearance preview render failed: " + e.Message);
                Studio = null;
            }
        }
    }
}
