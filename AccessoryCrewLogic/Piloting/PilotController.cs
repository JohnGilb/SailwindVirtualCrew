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
        private bool  hasLastError = false;
        private float oppositeHeadingSign = 0f;
        public  float Output    { get; private set; }

        // 10 s of history at one sample per 0.1 s = 100 slots.
        public const int   MaxSamples     = 100;
        public const float SampleInterval = 0.1f;

        private const float OppositeHeadingEnterDeg   = 179f;
        private const float OppositeHeadingReleaseDeg = 170f;

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
            ResetDerivativeHistory();
        }

        public void UpdateTarget(float heading)
        {
            float target = Normalize(heading);
            if (TargetHeading.HasValue && Mathf.Abs(Mathf.DeltaAngle(TargetHeading.Value, target)) > 5f)
                ResetDerivativeHistory();
            TargetHeading = target;
        }

        public void ClearTarget()
        {
            TargetHeading = null;
            Output        = 0f;
            integral      = 0f;
            lastError     = 0f;
            ResetDerivativeHistory();
        }

        public void AdjustTarget(float delta)
        {
            if (TargetHeading == null) return;
            TargetHeading = Normalize(TargetHeading.Value + delta);
            if (Mathf.Abs(delta) > 5f)
                ResetDerivativeHistory();
        }

        // Call every frame. Positive output = steer port (Sailwind wheel convention).
        public float Tick(float currentHeading, float deltaTime)
        {
            if (TargetHeading == null) { Output = 0f; return 0f; }

            float current = Normalize(currentHeading);
            // Mathf.DeltaAngle(target, current) = shortest path from target to current.
            // Positive = current is clockwise of target → steer port to correct.
            float error = Mathf.DeltaAngle(TargetHeading.Value, current);
            error = StabilizeOppositeHeadingError(error);

            integral  += error * deltaTime;
            float deriv = hasLastError && deltaTime > 0f ? (error - lastError) / deltaTime : 0f;
            lastError   = error;
            hasLastError = true;

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

        private float StabilizeOppositeHeadingError(float error)
        {
            float absError = Mathf.Abs(error);
            if (absError >= OppositeHeadingEnterDeg)
            {
                if (oppositeHeadingSign == 0f)
                {
                    oppositeHeadingSign = hasLastError ? Mathf.Sign(lastError) : Mathf.Sign(error);
                    if (oppositeHeadingSign == 0f)
                        oppositeHeadingSign = 1f;
                }

                return absError * oppositeHeadingSign;
            }

            if (oppositeHeadingSign == 0f)
                return error;

            if (absError <= OppositeHeadingReleaseDeg)
            {
                oppositeHeadingSign = 0f;
                return error;
            }

            return absError * oppositeHeadingSign;
        }

        private void ResetDerivativeHistory()
        {
            hasLastError = false;
            oppositeHeadingSign = 0f;
        }
    }
}
