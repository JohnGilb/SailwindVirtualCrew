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
        public Crewman            AssignedCrewman2 { get; set; }
        public bool               RequiresTwoDeckhands { get; set; }

        public float PositioningTimeTotal { get; private set; }
        private float positioningStartTime;
        private bool concretePositioning;
        private bool concretePositioning2;

        public WorkRequest(ICommonSailActions sail, string commandName, params WinchTarget[] targets)
        {
            Sail        = sail;
            CommandName = commandName;
            Targets     = targets;
            Status      = WorkRequestStatus.Open;
        }

        public void BeginPositioning(Crewman crewman)
        {
            BeginPositioning(crewman, null);
        }

        public void BeginPositioning(Crewman crewman, Crewman crewman2)
        {
            AssignedCrewman = crewman;
            AssignedCrewman2 = crewman2;
            PositioningTimeTotal = 7 - crewman.Dexterity;
            if (RequiresTwoDeckhands && crewman2 != null)
                PositioningTimeTotal = Mathf.Max(PositioningTimeTotal, 7 - crewman2.Dexterity);
            positioningStartTime = Time.time;
            concretePositioning = Targets.Length > 0
                && CrewNavigationCoordinator.Instance.TryBeginWinchPositioning(this, crewman, Targets[0].Winch);
            concretePositioning2 = RequiresTwoDeckhands
                && crewman2 != null
                && Targets.Length > 1
                && CrewNavigationCoordinator.Instance.TryBeginWinchPositioning((this, 1), crewman2, Targets[1].Winch);
            Status = WorkRequestStatus.Positioning;
        }

        public bool IsPositioningComplete()
        {
            bool firstComplete = concretePositioning
                ? CrewNavigationCoordinator.Instance.IsPositioningComplete(this)
                : Time.time >= positioningStartTime + PositioningTimeTotal;

            if (!RequiresTwoDeckhands)
                return firstComplete;

            bool secondComplete = concretePositioning2
                ? CrewNavigationCoordinator.Instance.IsPositioningComplete((this, 1))
                : Time.time >= positioningStartTime + PositioningTimeTotal;

            return firstComplete && secondComplete;
        }

        // 100 = just started (full bar), 0 = arrived (empty bar) — drains continuously.
        public float GetPositioningProgress() =>
            RequiresTwoDeckhands
                ? (GetSinglePositioningProgress(this, concretePositioning)
                   + GetSinglePositioningProgress((this, 1), concretePositioning2)) * 0.5f
                : GetSinglePositioningProgress(this, concretePositioning);

        public void Begin()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Complete(this);
                concretePositioning = false;
            }
            if (concretePositioning2)
            {
                CrewNavigationCoordinator.Instance.Complete((this, 1));
                concretePositioning2 = false;
            }

            foreach (var t in Targets)
                t.RecordStart();
            Status = WorkRequestStatus.InProgress;
        }

        public void CancelPositioning()
        {
            if (!concretePositioning)
            {
                if (!concretePositioning2)
                    return;
            }
            else
            {
                CrewNavigationCoordinator.Instance.Cancel(this);
                concretePositioning = false;
            }

            if (concretePositioning2)
            {
                CrewNavigationCoordinator.Instance.Cancel((this, 1));
                concretePositioning2 = false;
            }
        }

        private float GetSinglePositioningProgress(object owner, bool concrete)
        {
            return concrete
                ? CrewNavigationCoordinator.Instance.GetPositioningProgress(owner)
                : PositioningTimeTotal <= 0f ? 0f
                    : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / PositioningTimeTotal) * 100f;
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
