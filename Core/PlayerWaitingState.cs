using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class PlayerWaitingState
    {
        private const float WaitTimeScale = 16f;
        private const float WaitFixedDeltaMultiplier = 10f;

        private static object owner;
        private static float previousTimeScale = 1f;
        private static float previousFixedDeltaTime = 0.02222f;

        internal static bool IsActive => owner != null;

        internal static bool Begin(object newOwner)
        {
            if (newOwner == null || IsActive)
                return false;

            owner = newOwner;
            previousTimeScale = Time.timeScale;
            previousFixedDeltaTime = Time.fixedDeltaTime;
            Time.timeScale = WaitTimeScale;
            Time.fixedDeltaTime = previousFixedDeltaTime * WaitFixedDeltaMultiplier;
            return true;
        }

        internal static bool IsOwner(object candidate)
        {
            return owner != null && ReferenceEquals(owner, candidate);
        }

        internal static void End(object candidate)
        {
            if (!IsOwner(candidate))
                return;

            RestoreTime();
            owner = null;
        }

        internal static void Interrupt(string reason)
        {
            if (GameState.sleeping && Sleep.instance != null)
                Sleep.instance.WakeUp();

            if (owner != null)
            {
                RestoreTime();
                owner = null;
            }

            if (!string.IsNullOrEmpty(reason))
                CrewDebugLog.Ok("PlayerWait", "Interrupted waiting state: " + reason);
        }

        internal static void Tick()
        {
            if (owner == null)
                return;

            if (HasMotionInput())
                Interrupt("player motion");
        }

        private static bool HasMotionInput()
        {
            return Input.GetKey(KeyCode.W)
                || Input.GetKey(KeyCode.A)
                || Input.GetKey(KeyCode.S)
                || Input.GetKey(KeyCode.D)
                || Input.GetKey(KeyCode.Space);
        }

        private static void RestoreTime()
        {
            Time.timeScale = previousTimeScale;
            Time.fixedDeltaTime = previousFixedDeltaTime;
        }
    }
}
