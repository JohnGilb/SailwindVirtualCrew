using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public class WinchTarget
    {
        public GPButtonRopeWinch Winch { get; }
        public float TargetLength { get; set; }  // 0.0–1.0
        public float StartLength { get; private set; }

        public const float TargetTolerance = 0.015f;
        private const float Kp        = 2.5f;
        public float MaxPower { get; set; } = 25f;  // set to Strength*5 when crewman is assigned

        public WinchTarget(GPButtonRopeWinch winch, float targetLength)
        {
            Winch        = winch;
            TargetLength = Mathf.Clamp01(targetLength);

            // Check for reverse reefing
            if (winch.rope is RopeControllerSailReef)
            {
                var controller = (RopeControllerSailReef)winch.rope;

                if (controller.reverseReefing) 
                {
                    TargetLength = 1 - targetLength;
                }
            }
        }

        public void RecordStart() => StartLength = Winch.rope.currentLength;

        // P controller: error is expressed as a percentage (0–100) so that Kp=2.5
        // saturates at MaxPower when >10% away and tapers smoothly to zero near the
        // target. Negative output lets the rope out; positive pulls it in.
        public float GetPower()
        {
            float errorPct = (Winch.rope.currentLength - TargetLength) * 100f;
            return Mathf.Clamp(Kp * errorPct, -MaxPower, MaxPower);
        }

        public bool IsAtTarget() =>
            Mathf.Abs(Winch.rope.currentLength - TargetLength) <= TargetTolerance;

        // 0 = rope at task-start length, 100 = rope at target length.
        public float GetProgress()
        {
            float range = TargetLength - StartLength;
            if (Mathf.Abs(range) < 0.001f) return 100f;
            return Mathf.Clamp01((Winch.rope.currentLength - StartLength) / range) * 100f;
        }
    }

    public enum WorkRequestStatus { Open, Positioning, InProgress, Complete }

    public class WorkRequest
    {
        public ICommonSailActions Sail            { get; }
        public string             CommandName     { get; }
        public WinchTarget[]      Targets         { get; }
        public WorkRequestStatus  Status          { get; set; }
        public Crewman            AssignedCrewman { get; set; }

        public float PositioningTimeTotal { get; private set; }
        private float positioningStartTime;
        private bool concretePositioning;

        public WorkRequest(ICommonSailActions sail, string commandName, params WinchTarget[] targets)
        {
            Sail        = sail;
            CommandName = commandName;
            Targets     = targets;
            Status      = WorkRequestStatus.Open;
        }

        public void BeginPositioning(Crewman crewman)
        {
            PositioningTimeTotal = 7 - crewman.Dexterity;
            positioningStartTime = Time.time;
            concretePositioning = Targets.Length > 0
                && CrewNavigationCoordinator.Instance.TryBeginWinchPositioning(this, crewman, Targets[0].Winch);
            Status = WorkRequestStatus.Positioning;
        }

        public bool IsPositioningComplete()
        {
            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.IsPositioningComplete(this);

            return Time.time >= positioningStartTime + PositioningTimeTotal;
        }

        // 100 = just started (full bar), 0 = arrived (empty bar) — drains continuously.
        public float GetPositioningProgress() =>
            concretePositioning
                ? CrewNavigationCoordinator.Instance.GetPositioningProgress(this)
                : PositioningTimeTotal <= 0f ? 0f
                    : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / PositioningTimeTotal) * 100f;

        public void Begin()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Complete(this);
                concretePositioning = false;
            }

            foreach (var t in Targets)
                t.RecordStart();
            Status = WorkRequestStatus.InProgress;
        }

        public void CancelPositioning()
        {
            if (!concretePositioning)
                return;

            CrewNavigationCoordinator.Instance.Cancel(this);
            concretePositioning = false;
        }

        public string DisplayLabel => Sail != null ? $"{CommandName} — {Sail.getSailName()}" : CommandName;

        public bool IsComplete() => Targets.All(t => t.IsAtTarget());

        // Average fraction across all targets, expressed as 0–100.
        public float GetProgress()
        {
            if (Targets.Length == 0) return 100f;
            return Targets.Average(t => t.GetProgress());
        }
    }
}
