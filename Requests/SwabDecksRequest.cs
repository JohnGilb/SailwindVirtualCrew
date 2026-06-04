using UnityEngine;

namespace SailwindVirtualCrew
{
    public class SwabDecksRequest
    {
        public Crewman AssignedCrewman { get; private set; }
        public WorkRequestStatus Status { get; set; } = WorkRequestStatus.Open;

        private readonly CleanableObject cleanable;
        private float cycleStart;

        private const float CycleDuration = 5f;
        private const float CleanAmount = 0.01f;
        private const float DoneThreshold = 0.01f;

        public SwabDecksRequest(CleanableObject cleanable)
        {
            this.cleanable = cleanable;
        }

        public void Begin(Crewman crewman)
        {
            AssignedCrewman = crewman;
            crewman.CurrentTask = this;
            Status = WorkRequestStatus.InProgress;
            StartCycle();
        }

        public void Tick()
        {
            if (Status != WorkRequestStatus.InProgress)
                return;

            if (IsDone())
            {
                Complete();
                return;
            }

            if (Time.time < cycleStart + CycleDuration)
                return;

            ImproveCleanliness(cleanable, CleanAmount);
            if (IsDone())
                Complete();
            else
                StartCycle();
        }

        public float GetProgress()
        {
            return Mathf.Clamp01((Time.time - cycleStart) / CycleDuration) * 100f;
        }

        public bool IsDone()
        {
            return cleanable == null || GetDirtiness(cleanable) < DoneThreshold;
        }

        public void Cancel()
        {
            CrewNavigationCoordinator.Instance.Cancel(this);
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
            Status = WorkRequestStatus.Complete;
        }

        private void StartCycle()
        {
            cycleStart = Time.time;
            CrewNavigationCoordinator.Instance.TryBeginRandomDeckMovement(this, AssignedCrewman, "swab decks");
        }

        private void Complete()
        {
            CrewNavigationCoordinator.Instance.Cancel(this);
            Status = WorkRequestStatus.Complete;
            if (AssignedCrewman != null && AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }

        public static CleanableObject GetCurrentShipCleanable()
        {
            var topBoat = CrewBoatContextResolver.GetActiveTopBoat();
            var saveable = topBoat ? topBoat.GetComponent<SaveableObject>() : null;
            return saveable ? saveable.GetCleanable() : null;
        }

        public static float GetDirtiness(CleanableObject cleanable)
        {
            var texture = cleanable != null ? cleanable.GetCurrentDirtTex() as Texture2D : null;
            if (texture == null)
                return 0f;

            Color[] pixels;
            try
            {
                pixels = texture.GetPixels();
            }
            catch (UnityException)
            {
                return 0f;
            }

            if (pixels.Length == 0)
                return 0f;

            float total = 0f;
            for (int i = 0; i < pixels.Length; i++)
                total += pixels[i].a;

            return Mathf.Clamp01(total / pixels.Length);
        }

        private static bool ImproveCleanliness(CleanableObject cleanable, float amount)
        {
            var texture = cleanable != null ? cleanable.GetCurrentDirtTex() as Texture2D : null;
            if (texture == null || amount <= 0f)
                return false;

            Color[] pixels;
            try
            {
                pixels = texture.GetPixels();
            }
            catch (UnityException)
            {
                return false;
            }

            if (pixels.Length == 0)
                return false;

            float total = 0f;
            for (int i = 0; i < pixels.Length; i++)
                total += pixels[i].a;

            float current = total / pixels.Length;
            if (current <= 0f)
                return false;

            float target = Mathf.Max(0f, current - amount);
            float factor = target / current;
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a *= factor;

            texture.SetPixels(pixels);
            texture.Apply();
            return true;
        }
    }
}
