using UnityEngine;

namespace SailwindVirtualCrew
{
    public class PilotController
    {
        public float? TargetHeading { get; private set; }

        public float Kp = 0.1f;
        public float Ki = 0.0f;
        public float Kd = 0.05f;

        private float integral  = 0f;
        private float lastError = 0f;
        public  float Output    { get; private set; }

        // 10 s of history at one sample per 0.1 s = 100 slots.
        public const int   MaxSamples     = 100;
        public const float SampleInterval = 0.1f;

        private float sampleTimer = 0f;

        public readonly float[] GoalHistory    = new float[MaxSamples];
        public readonly float[] CurrentHistory = new float[MaxSamples];
        public readonly float[] OutputHistory  = new float[MaxSamples];

        public int SampleCount { get; private set; }
        public int SampleHead  { get; private set; }

        public void SetTarget(float heading)
        {
            TargetHeading = Normalize(heading);
            integral      = 0f;
            lastError     = 0f;
        }

        public void UpdateTarget(float heading)
        {
            TargetHeading = Normalize(heading);
        }

        public void ClearTarget()
        {
            TargetHeading = null;
            Output        = 0f;
            integral      = 0f;
            lastError     = 0f;
        }

        public void AdjustTarget(float delta)
        {
            if (TargetHeading == null) return;
            TargetHeading = Normalize(TargetHeading.Value + delta);
        }

        // Call every frame. Positive output = steer port (Sailwind wheel convention).
        public float Tick(float currentHeading, float deltaTime)
        {
            if (TargetHeading == null) { Output = 0f; return 0f; }

            float current = Normalize(currentHeading);
            // Mathf.DeltaAngle(target, current) = shortest path from target to current.
            // Positive = current is clockwise of target → steer port to correct.
            float error = Mathf.DeltaAngle(TargetHeading.Value, current);

            integral  += error * deltaTime;
            float deriv = deltaTime > 0f ? (error - lastError) / deltaTime : 0f;
            lastError   = error;

            Output = Kp * error + Ki * integral + Kd * deriv;

            sampleTimer += deltaTime;
            if (sampleTimer >= SampleInterval)
            {
                sampleTimer -= SampleInterval;
                GoalHistory   [SampleHead] = TargetHeading.Value;
                CurrentHistory[SampleHead] = current;
                OutputHistory [SampleHead] = Output;
                SampleHead = (SampleHead + 1) % MaxSamples;
                if (SampleCount < MaxSamples) SampleCount++;
            }

            return Output;
        }

        // Wraps any heading to [0, 360).
        public static float Normalize(float h)
        {
            h %= 360f;
            return h < 0f ? h + 360f : h;
        }
    }
}
