using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public static class TrimConstants
    {
        public const float DangerZoneSafetyMargin = 0.20f;
    }

    public class TrimRequest
    {
        public SimpleSail Sail { get; }
        public WorkRequestStatus Status { get; set; }
        public Crewman AssignedCrewman { get; set; }
        public string CommandName => "Auto-Trim";

        public float PositioningTimeTotal { get; private set; }
        private float positioningStartTime;

        private enum TrimPhase { ScanIn, ScanOut, Optimize }
        private TrimPhase phase;

        private float startPosition;
        private float scanInTarget;
        private float scanOutTarget;

        private struct Sample { public float Position; public float Efficiency; }
        private readonly List<Sample> samples = new List<Sample>();

        private WinchTarget activeTarget;

        private static readonly FieldInfo unamplifiedForwardInfo =
            AccessTools.Field(typeof(Sail), "unamplifiedForwardForce");
        private static readonly FieldInfo unamplifiedSideInfo =
            AccessTools.Field(typeof(Sail), "unamplifiedSidewayForce");
        private static readonly FieldInfo totalWindForceInfo =
            AccessTools.Field(typeof(Sail), "totalWindForce");

        public TrimRequest(SimpleSail sail)
        {
            Sail = sail;
            Status = WorkRequestStatus.Open;
        }

        public void BeginPositioning(Crewman crewman)
        {
            PositioningTimeTotal = 7 - crewman.Dexterity;
            positioningStartTime = Time.time;
            Status = WorkRequestStatus.Positioning;
        }

        public bool IsPositioningComplete() =>
            Time.time >= positioningStartTime + PositioningTimeTotal;

        public float GetPositioningProgress() =>
            PositioningTimeTotal <= 0f ? 0f
                : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / PositioningTimeTotal) * 100f;

        public void Begin(Crewman crewman)
        {
            var winch = Sail.getSheetWinch();
            startPosition = winch.rope.currentLength;
            scanInTarget  = Mathf.Clamp01(startPosition - 0.25f);
            scanOutTarget = Mathf.Clamp01(startPosition + 0.25f);
            samples.Clear();

            activeTarget = new WinchTarget(winch, scanInTarget);
            activeTarget.MaxPower = crewman.Strength * 5f;
            activeTarget.RecordStart();
            phase = TrimPhase.ScanIn;
            Status = WorkRequestStatus.InProgress;
            VirtualCrewManager.Instance.crewWinchInstructions[winch] = activeTarget;
        }

        public bool IsComplete() =>
            phase == TrimPhase.Optimize && activeTarget.IsAtTarget();

        // 0–100: ScanIn occupies 0–33, ScanOut 33–66, Optimize 66–100.
        public float GetProgress()
        {
            if (phase == TrimPhase.ScanIn)  return activeTarget.GetProgress() * 0.33f;
            if (phase == TrimPhase.ScanOut) return 33f + activeTarget.GetProgress() * 0.33f;
            return 66f + activeTarget.GetProgress() * 0.34f;
        }

        // Called every frame while InProgress.
        public void UpdateFrame()
        {
            if (phase == TrimPhase.ScanIn)
            {
                samples.Add(new Sample
                {
                    Position   = Sail.getSheetWinch().rope.currentLength,
                    Efficiency = ScoredEfficiency()
                });
                if (activeTarget.IsAtTarget())
                {
                    activeTarget.TargetLength = scanOutTarget;
                    activeTarget.RecordStart();
                    phase = TrimPhase.ScanOut;
                }
            }
            else if (phase == TrimPhase.ScanOut)
            {
                samples.Add(new Sample
                {
                    Position   = Sail.getSheetWinch().rope.currentLength,
                    Efficiency = ScoredEfficiency()
                });
                if (activeTarget.IsAtTarget())
                    BeginOptimize();
            }
            // Optimize: P controller drives to best position; IsComplete() polls for arrival.
        }

        private void BeginOptimize()
        {
            float bestEfficiency = float.MinValue;
            float bestPosition   = startPosition;
            foreach (var s in samples)
                if (s.Efficiency > bestEfficiency) { bestEfficiency = s.Efficiency; bestPosition = s.Position; }

            float safePosition = FindSafeOptimum(samples, startPosition);

            System.Console.WriteLine(
                $"Trim optimize — best pos: {safePosition:F3}  eff: {bestEfficiency:F1}  samples: {samples.Count}" +
                (safePosition != bestPosition ? $"  (safety-adjusted from {bestPosition:F3})" : ""));

            activeTarget.TargetLength = safePosition;
            activeTarget.RecordStart();
            phase = TrimPhase.Optimize;
        }

        // Returns the best-efficiency position that is at least 0.10 rope units away from any
        // danger zone (efficiency < 10). Falls back to the absolute best position if no safe
        // position exists.
        private static float FindSafeOptimum(List<Sample> samples, float fallback)
        {
            const float dangerThreshold = 10f;
            const float safetyMargin    = TrimConstants.DangerZoneSafetyMargin;

            float bestEff = float.MinValue, bestPos = fallback;
            float bestSafeEff = float.MinValue, bestSafePos = float.NaN;

            foreach (var s in samples)
            {
                if (s.Efficiency > bestEff) { bestEff = s.Efficiency; bestPos = s.Position; }

                bool nearDanger = false;
                foreach (var other in samples)
                {
                    if (other.Efficiency < dangerThreshold && Mathf.Abs(other.Position - s.Position) < safetyMargin)
                    { nearDanger = true; break; }
                }

                if (!nearDanger && s.Efficiency > bestSafeEff)
                { bestSafeEff = s.Efficiency; bestSafePos = s.Position; }
            }

            return float.IsNaN(bestSafePos) ? bestPos : bestSafePos;
        }

        private float ScoredEfficiency()
        {
            float raw = CombinedEfficiencyRaw();
            return raw <= 0f ? -500f : raw;
        }

        private float CombinedEfficiencyRaw()
        {
            var sail = Sail.getRealSail();
            float total = GetTotalForce(sail);
            if (total == 0f) return 0f;
            float eff = SailEfficiency(sail, total);
            if (eff <= 0f) return eff;
            float ineff = 100 - SailInefficiency(sail, total);
            return Mathf.Round((eff + ineff) / 2f);
        }

        private static float SailEfficiency(Sail sail, float totalForce) =>
            Mathf.Round((float)unamplifiedForwardInfo.GetValue(sail) / totalForce * 100f);

        private static float SailInefficiency(Sail sail, float totalForce) =>
            Mathf.Abs(Mathf.Round((float)unamplifiedSideInfo.GetValue(sail) / totalForce * 100f));

        private float GetTotalForce(Sail sail)
        {
            float applied = sail.appliedWindForce;
            if (applied == 0f)
                return (float)totalWindForceInfo.GetValue(sail);
            return applied / sail.GetCapturedForceFraction();
        }
    }
}
