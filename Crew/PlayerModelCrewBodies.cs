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
        void Tick(float deltaTime);
        void Destroy();
    }

    /// <summary>
    /// Optional integration with the Sailwind Player Model mod, which builds humanoid bodies and animates them
    /// procedurally (walk gait, hands on the helm and winches). Every reference to a SailwindPlayerModel type
    /// lives in this file behind non-inlined methods, so nothing touches that assembly unless the plugin is
    /// actually loaded.
    /// </summary>
    internal static class PlayerModelCrewBodies
    {
        private const string Phase = "PlayerModel";
        internal const string PluginGuid = "com.diamondminer99.playermodel";

        private static bool _loggedUnavailable;

        internal static bool IsEnabled
        {
            get
            {
                if (Plugin.UsePlayerModelBodies != null && !Plugin.UsePlayerModelBodies.Value)
                    return false;

                if (Chainloader.PluginInfos.ContainsKey(PluginGuid))
                    return true;

                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    CrewDebugLog.Ok(Phase, "Sailwind Player Model not installed; crew use cloned NPC bodies.");
                }
                return false;
            }
        }

        /// <summary>
        /// Build a Player Model body under <paramref name="root"/>, whose origin is at the crewman's feet, wearing
        /// <paramref name="appearance"/> (a PlayerAppearance string). With no appearance, a look is made up from
        /// <paramref name="appearanceSeed"/>. <paramref name="appearanceUsed"/> is the look the body wears, for the
        /// caller to keep. Returns null when the mod is absent, its NPC template has not loaded yet, or the build failed.
        /// </summary>
        internal static ICrewBodyAnimator TryCreate(Transform root, string name, string appearance, string appearanceSeed,
            out string appearanceUsed)
        {
            appearanceUsed = null;
            if (!root || !IsEnabled)
                return null;

            try
            {
                return PlayerModelCrewBody.TryBuild(root, name, appearance, StableSeed(appearanceSeed), out appearanceUsed);
            }
            catch (Exception e)
            {
                CrewDebugLog.Warn(Phase, "Player Model body build failed for '" + name + "': " + e.Message);
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
        private float _crouchThisFrame;
        private float _lookPitchThisFrame;

        private PlayerModelCrewBody(SyntyBody body, Transform root)
        {
            _body = body;
            _root = root;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ICrewBodyAnimator TryBuild(Transform root, string name, string appearance, ulong seed, out string appearanceUsed)
        {
            // The seeded look is the one crew wore before looks were saved, so existing crew keep their faces.
            var look = string.IsNullOrEmpty(appearance)
                ? PlayerAppearance.DeterministicFor(seed)
                : PlayerAppearance.Deserialize(appearance);
            appearanceUsed = look.Serialize();

            var body = SyntyBody.TryBuild(root, name, look, 0, () => 0f);
            return body != null ? new PlayerModelCrewBody(body, root) : null;
        }

        public float SpeedMps
        {
            set { _body.SpeedMps = value; }
        }

        public void SetAction(CrewBodyAction action, Transform target)
        {
            if (action == CrewBodyAction.None || !target)
                return;

            _body.SetInteraction(ToInteractionKind(action), target);
            if (action == CrewBodyAction.Crank)
                _crouchThisFrame = Mathf.Max(_crouchThisFrame, CrouchForLowControl(target));
        }

        public void SetLookTarget(Vector3 worldPoint)
        {
            if (!_root)
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
            if (!item || !_root)
                return false;

            Vector3 restingPosition = _root.position
                + _root.forward * RestingItemForward
                + _root.up * (StandingHeight() * RestingItemHeightFraction);
            _body.SetHeldItemPose(item, null, restingPosition, item.rotation, big);
            return _body.PlacesHeldItemInHand;
        }

        public void SetLying(Vector3 headWorld, Vector3 alongWorld, Vector3 upWorld)
        {
            _body.SetLying(headWorld, alongWorld, upWorld);
        }

        public void Tick(float deltaTime)
        {
            // Like the interaction, the crouch and the look last only while they are asked for each frame; the
            // body eases back once they stop.
            _body.Crouch01Target = _crouchThisFrame;
            _body.LookPitchDegTarget = _lookPitchThisFrame;
            _crouchThisFrame = 0f;
            _lookPitchThisFrame = 0f;
            _body.Tick(deltaTime);
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
            _body.Destroy();
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
}
