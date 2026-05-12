using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class JibTrimRequest
    {
        public DualSheetSail Sail { get; }
        public WorkRequestStatus Status { get; set; }
        public Crewman AssignedCrewman { get; set; }
        public string CommandName => "Jib Auto-Trim";

        public float PositioningTimeTotal { get; private set; }
        private float positioningStartTime;

        private enum JibTrimPhase
        {
            W1ScanIn, W1ScanOut, W1Optimize,
            Repositioning,
            W2ScanIn, W2ScanOut, W2Optimize,
            Done
        }
        private JibTrimPhase phase;
        private bool doSecondWinch;
        private float repositioningStart;
        private float repositioningDuration;

        private GPButtonRopeWinch winch1;
        private GPButtonRopeWinch winch2;
        private WinchTarget activeTarget;

        private float w1StartPos, w1ScanInPos, w1ScanOutPos;
        private float w2StartPos, w2ScanInPos, w2ScanOutPos;

        private struct Sample { public float Position; public float Efficiency; }
        private readonly List<Sample> w1Samples = new List<Sample>();
        private readonly List<Sample> w2Samples = new List<Sample>();

        private static readonly FieldInfo unamplifiedForwardInfo =
            AccessTools.Field(typeof(Sail), "unamplifiedForwardForce");
        private static readonly FieldInfo unamplifiedSideInfo =
            AccessTools.Field(typeof(Sail), "unamplifiedSidewayForce");
        private static readonly FieldInfo totalWindForceInfo =
            AccessTools.Field(typeof(Sail), "totalWindForce");

        public JibTrimRequest(DualSheetSail sail)
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
            var port = Sail.getPortSheetWinch();
            var starboard = Sail.getStarboardSheetWinch();
            float portTension = port.rope.currentResistance;
            float starbTension = starboard.rope.currentResistance;

            System.Console.WriteLine(
                $"Jib trim enqueued — port tension: {portTension:F2}" +
                $"  starboard tension: {starbTension:F2}");

            if (portTension > 3f && starbTension < 0.1f)
            {
                winch1 = port;
                doSecondWinch = false;
            }
            else if (starbTension > 3f && portTension < 0.1f)
            {
                winch1 = starboard;
                doSecondWinch = false;
            }
            else
            {
                winch1 = port;
                winch2 = starboard;
                doSecondWinch = true;
            }

            StartW1Scan(crewman);
            Status = WorkRequestStatus.InProgress;
        }

        public bool IsComplete() =>
            phase == JibTrimPhase.Done ||
            (phase == JibTrimPhase.W2Optimize && activeTarget.IsAtTarget());

        public bool IsRepositioning => phase == JibTrimPhase.Repositioning;

        public float GetRepositioningProgress() =>
            repositioningDuration <= 0f ? 0f
                : Mathf.Clamp01(1f - (Time.time - repositioningStart) / repositioningDuration) * 100f;

        public float GetProgress()
        {
            if (!doSecondWinch)
            {
                switch (phase)
                {
                    case JibTrimPhase.W1ScanIn:   return activeTarget.GetProgress() * 0.33f;
                    case JibTrimPhase.W1ScanOut:  return 33f + activeTarget.GetProgress() * 0.33f;
                    case JibTrimPhase.W1Optimize: return 66f + activeTarget.GetProgress() * 0.34f;
                    default:                      return 100f;
                }
            }

            switch (phase)
            {
                case JibTrimPhase.W1ScanIn:      return activeTarget.GetProgress() * 0.14f;
                case JibTrimPhase.W1ScanOut:     return 14f + activeTarget.GetProgress() * 0.14f;
                case JibTrimPhase.W1Optimize:    return 28f + activeTarget.GetProgress() * 0.15f;
                case JibTrimPhase.Repositioning: return 43f;
                case JibTrimPhase.W2ScanIn:      return 50f + activeTarget.GetProgress() * 0.14f;
                case JibTrimPhase.W2ScanOut:     return 64f + activeTarget.GetProgress() * 0.14f;
                case JibTrimPhase.W2Optimize:    return 78f + activeTarget.GetProgress() * 0.22f;
                default:                         return 100f;
            }
        }

        public void UpdateFrame()
        {
            switch (phase)
            {
                case JibTrimPhase.W1ScanIn:
                    w1Samples.Add(new Sample { Position = winch1.rope.currentLength, Efficiency = ScoredEfficiency() });
                    if (activeTarget.IsAtTarget())
                    {
                        activeTarget.TargetLength = w1ScanOutPos;
                        activeTarget.RecordStart();
                        phase = JibTrimPhase.W1ScanOut;
                    }
                    break;

                case JibTrimPhase.W1ScanOut:
                    w1Samples.Add(new Sample { Position = winch1.rope.currentLength, Efficiency = ScoredEfficiency() });
                    if (activeTarget.IsAtTarget())
                        BeginW1Optimize();
                    break;

                case JibTrimPhase.W1Optimize:
                    if (activeTarget.IsAtTarget())
                    {
                        if (doSecondWinch)
                        {
                            VirtualCrewManager.Instance.crewWinchInstructions.Remove(winch1);
                            repositioningStart    = Time.time;
                            repositioningDuration = 7f - AssignedCrewman.Dexterity;
                            phase = JibTrimPhase.Repositioning;
                        }
                        else
                        {
                            phase = JibTrimPhase.Done;
                        }
                    }
                    break;

                case JibTrimPhase.Repositioning:
                    if (Time.time >= repositioningStart + repositioningDuration)
                        StartW2Scan();
                    break;

                case JibTrimPhase.W2ScanIn:
                    w2Samples.Add(new Sample { Position = winch2.rope.currentLength, Efficiency = ScoredEfficiency() });
                    if (activeTarget.IsAtTarget())
                    {
                        activeTarget.TargetLength = w2ScanOutPos;
                        activeTarget.RecordStart();
                        phase = JibTrimPhase.W2ScanOut;
                    }
                    break;

                case JibTrimPhase.W2ScanOut:
                    w2Samples.Add(new Sample { Position = winch2.rope.currentLength, Efficiency = ScoredEfficiency() });
                    if (activeTarget.IsAtTarget())
                        BeginW2Optimize();
                    break;

                // W2Optimize / Done: P controller drives to best position; IsComplete() polls for arrival.
            }
        }

        private void StartW1Scan(Crewman crewman)
        {
            w1Samples.Clear();
            w1StartPos   = winch1.rope.currentLength;
            w1ScanInPos  = Mathf.Clamp01(w1StartPos - 0.25f);
            w1ScanOutPos = Mathf.Clamp01(w1StartPos + 0.25f);

            activeTarget = new WinchTarget(winch1, w1ScanInPos);
            activeTarget.MaxPower = crewman.Strength * 5f;
            activeTarget.RecordStart();
            phase = JibTrimPhase.W1ScanIn;
            VirtualCrewManager.Instance.crewWinchInstructions[winch1] = activeTarget;
        }

        private void StartW2Scan()
        {
            w2Samples.Clear();
            w2StartPos   = winch2.rope.currentLength;
            w2ScanInPos  = Mathf.Clamp01(w2StartPos - 0.25f);
            w2ScanOutPos = Mathf.Clamp01(w2StartPos + 0.25f);

            activeTarget = new WinchTarget(winch2, w2ScanInPos);
            activeTarget.MaxPower = AssignedCrewman.Strength * 5f;
            activeTarget.RecordStart();
            phase = JibTrimPhase.W2ScanIn;
            VirtualCrewManager.Instance.crewWinchInstructions[winch2] = activeTarget;
        }

        private void BeginW1Optimize()
        {
            float bestEff = float.MinValue, bestPos = w1StartPos;
            foreach (var s in w1Samples)
                if (s.Efficiency > bestEff) { bestEff = s.Efficiency; bestPos = s.Position; }

            float safePos = FindSafeOptimum(w1Samples, w1StartPos);

            System.Console.WriteLine(
                $"Jib W1 optimize — best pos: {safePos:F3}  eff: {bestEff:F1}  samples: {w1Samples.Count}" +
                (safePos != bestPos ? $"  (safety-adjusted from {bestPos:F3})" : ""));

            activeTarget.TargetLength = safePos;
            activeTarget.RecordStart();
            phase = JibTrimPhase.W1Optimize;
        }

        private void BeginW2Optimize()
        {
            float bestEff = float.MinValue, bestPos = w2StartPos;
            foreach (var s in w2Samples)
                if (s.Efficiency > bestEff) { bestEff = s.Efficiency; bestPos = s.Position; }

            float safePos = FindSafeOptimum(w2Samples, w2StartPos);

            System.Console.WriteLine(
                $"Jib W2 optimize — best pos: {safePos:F3}  eff: {bestEff:F1}  samples: {w2Samples.Count}" +
                (safePos != bestPos ? $"  (safety-adjusted from {bestPos:F3})" : ""));

            activeTarget.TargetLength = safePos;
            activeTarget.RecordStart();
            phase = JibTrimPhase.W2Optimize;
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
