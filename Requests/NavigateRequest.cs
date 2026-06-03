using System;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum NavigationMethod { Quadrant, SunCompass, Chronometer, Chronocompass }

    public class NavigateRequest
    {
        public Crewman           Navigator  { get; private set; }
        public WorkRequestStatus Status     { get; set; }
        public NavigationMethod  Method     { get; }
        public bool RequireTimeWindow { get; }
        public bool CanEstimateLatitude  => Method != NavigationMethod.Chronometer;
        public bool CanEstimateLongitude => Method == NavigationMethod.Chronometer || Method == NavigationMethod.Chronocompass;
        public Action<NavigationResult> OnComplete { get; }

        private float startTime;
        private float duration;

        public float DurationTotal => duration;

        public NavigateRequest(NavigationMethod method, Action<NavigationResult> onComplete, bool requireTimeWindow = true)
        {
            Method            = method;
            RequireTimeWindow = requireTimeWindow;
            OnComplete        = onComplete;
            Status            = WorkRequestStatus.Open;
        }

        public void Begin(Crewman navigator)
        {
            Navigator         = navigator;
            navigator.CurrentTask = this;
            duration          = 10f - navigator.Dexterity;
            startTime         = Time.time;
            Status            = WorkRequestStatus.InProgress;
        }

        public bool IsComplete() =>
            Status == WorkRequestStatus.InProgress && Time.time >= startTime + duration;

        // 0 = just started, 100 = complete.
        public float GetProgress() =>
            duration <= 0f ? 100f
                : Mathf.Clamp01((Time.time - startTime) / duration) * 100f;
    }
}
