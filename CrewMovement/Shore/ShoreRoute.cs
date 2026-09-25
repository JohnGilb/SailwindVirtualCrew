using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// How deckhands get between the boat and the shore: a spot on deck (<see cref="BoatAnchorLocal"/>) and a spot ashore
    /// (<see cref="DockWorld"/>) they jump between. Moored, that's the mooring rope and its dock post. Anchored off a port
    /// with no dock to moor at, it's the edge of the deck facing the shore and the nearest walkable point with a way to
    /// the trader (<see cref="ShoreNavMesh.TryFindLanding"/>).
    /// </summary>
    internal sealed class ShoreRoute
    {
        // Anchored, the boat may be no further than this from where the deckhands land.
        private const float MaxLandingDistance = 25f;
        // How far along the deck, from the middle towards the landing, to look for its edge.
        private const float DeckSearchDistance = 14f;
        private const float DeckSearchStep = 0.5f;
        private static readonly float[] DeckSearchHeights = { 0.5f, 1.5f, 2.5f, 3.5f };
        private const float NoticeCooldown = 10f;

        private static float _nextNoticeTime;

        private Transform _boat;
        private GPButtonDockMooring _dock;
        private Transform _landingFrame;
        private Vector3 _landingOffset;

        internal Vector3 BoatAnchorLocal { get; private set; }
        internal Vector3 BoatAnchorWorld => _boat ? _boat.TransformPoint(BoatAnchorLocal) : BoatAnchorLocal;
        internal Vector3 DockWorld => _dock
            ? _dock.transform.position
            : (_landingFrame ? _landingFrame.position : Vector3.zero) + _landingOffset;
        internal string Label { get; private set; }
        internal bool IsAnchored => !_dock;

        /// <summary>The way ashore for the active boat: its mooring when moored, otherwise a landing when anchored.</summary>
        internal static bool TryFind(Vector3 fromWorld, out ShoreRoute route)
        {
            route = null;
            if (MooringLocator.TryFindActiveRoute(fromWorld, out var mooring))
            {
                route = new ShoreRoute
                {
                    _boat = CrewBoatContextResolver.GetActiveWorldBoat(),
                    _dock = mooring.Dock.Mooring,
                    BoatAnchorLocal = mooring.BoatAnchorLocal,
                    Label = "mooring '" + mooring.Dock.Mooring.name + "'"
                };
                return true;
            }

            if (!MooringLocator.IsCurrentBoatAnchoredFast())
                return false;

            string problem = TryFindAnchored(out route);
            if (problem == null)
                return true;

            if (Time.unscaledTime >= _nextNoticeTime)
            {
                _nextNoticeTime = Time.unscaledTime + NoticeCooldown;
                NotificationUi.instance?.ShowNotification(problem);
            }
            return false;
        }

        /// <summary>Whether the boat is held in place (moored or anchored), so deckhands can work ashore.</summary>
        internal static bool IsBoatHeld()
        {
            return MooringLocator.IsCurrentBoatMooredFast() || MooringLocator.IsCurrentBoatAnchoredFast();
        }

        private static string TryFindAnchored(out ShoreRoute route)
        {
            route = null;
            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !SupercargoTradeService.TryFindNearestPortDude(out var dude))
                return "No trader within reach of the boat";

            if (!ShoreNavMesh.IsReady)
                return "The crew are still looking over the shore";

            Transform boat = context.WorldBoat;
            if (!ShoreNavMesh.TryFindLanding(boat.position, dude.transform.position, MaxLandingDistance, out var landing))
                return "Anchored too far from shore for the crew to land";

            if (!TryFindDeckEdge(boat, landing, out var edgeLocal))
                return "The crew can't find a way off the deck";

            route = new ShoreRoute
            {
                _boat = boat,
                _landingFrame = ShoreNavMesh.Frame,
                _landingOffset = landing - ShoreNavMesh.Frame.position,
                BoatAnchorLocal = edgeLocal,
                Label = "landing near " + dude.GetPort().GetPortName()
            };
            CrewDebugLog.Info("ShoreNav", "Anchored landing " + Vector3.Distance(boat.TransformPoint(edgeLocal), landing).ToString("0.0")
                + "m from the deck edge at " + edgeLocal);
            return null;
        }

        // The furthest point along the deck towards the landing that's on the crew's deck NavMesh.
        private static bool TryFindDeckEdge(Transform boat, Vector3 landingWorld, out Vector3 edgeLocal)
        {
            edgeLocal = Vector3.zero;
            var navigation = CrewNavigationCoordinator.Instance;
            Vector3 local = boat.InverseTransformPoint(landingWorld);
            Vector3 direction = new Vector3(local.x, 0f, local.z);
            direction = direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;

            for (float distance = DeckSearchDistance; distance >= 0f; distance -= DeckSearchStep)
            {
                foreach (float height in DeckSearchHeights)
                {
                    Vector3 candidate = direction * distance + Vector3.up * height;
                    if (!navigation.TryProjectLocalToNavMeshQuiet(candidate, 0.9f, out var projected))
                        continue;

                    Vector3 offset = projected - candidate;
                    offset.y = 0f;
                    if (offset.sqrMagnitude > 0.36f)
                        continue;

                    edgeLocal = projected;
                    return true;
                }
            }
            return false;
        }
    }
}
