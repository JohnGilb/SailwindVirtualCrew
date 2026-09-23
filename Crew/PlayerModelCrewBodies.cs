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
    /// Interactions are per-frame: one frame without SetAction lowers the arms.
    /// </summary>
    internal interface ICrewBodyAnimator
    {
        float SpeedMps { set; }
        void SetAction(CrewBodyAction action, Transform target);
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
        /// Build a Player Model body under <paramref name="root"/>, whose origin is at the crewman's feet.
        /// Returns null when the mod is absent, its NPC template has not loaded yet, or the build failed.
        /// </summary>
        internal static ICrewBodyAnimator TryCreate(Transform root, string name, string appearanceSeed)
        {
            if (!root || !IsEnabled)
                return null;

            try
            {
                return PlayerModelCrewBody.TryBuild(root, name, StableSeed(appearanceSeed));
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

        private readonly SyntyBody _body;
        private readonly Transform _root;
        private float _crouchThisFrame;

        private PlayerModelCrewBody(SyntyBody body, Transform root)
        {
            _body = body;
            _root = root;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ICrewBodyAnimator TryBuild(Transform root, string name, ulong seed)
        {
            var body = SyntyBody.TryBuild(root, name, PlayerAppearance.DeterministicFor(seed), 0, () => 0f);
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

        public void Tick(float deltaTime)
        {
            // Like the interaction, the crouch lasts only while SetAction keeps asking for it; the body eases
            // back up once it stops.
            _body.Crouch01Target = _crouchThisFrame;
            _crouchThisFrame = 0f;
            _body.Tick(deltaTime);
        }

        private float CrouchForLowControl(Transform target)
        {
            if (!_root)
                return 0f;

            float standingHeight = _body.MeasuredHeight > 0.5f ? _body.MeasuredHeight : StandingHeightFallback;
            float waist = standingHeight * CrankWaistHeightFraction;
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
