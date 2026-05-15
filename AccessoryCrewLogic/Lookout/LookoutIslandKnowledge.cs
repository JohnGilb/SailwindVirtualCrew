using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal struct LookoutIslandIdentificationInfo
    {
        internal bool HasPortName;
        internal bool HasVisited;
        internal bool CurrentlyVisible;
        internal bool AngleLargeEnough;
        internal bool Identified;
        internal float AngleDeg;
        internal float EffectiveAngleDeg;
        internal string IslandName;
    }

    internal static class LookoutIslandKnowledge
    {
        internal const float IdentificationAngleDeg = 5f;

        private static Type _sailAdexPortsVisitedType;
        private static PropertyInfo _sailAdexInstanceProperty;
        private static PropertyInfo _sailAdexVisitedPortsProperty;
        private static float _nextSailAdexLookupTime;

        internal static bool TryIdentifyIsland(
            IslandHorizon island,
            Vector3 observerWorld,
            Crewman lookout,
            float spyglassZoom,
            out string islandName,
            out LookoutIslandIdentificationInfo info)
        {
            islandName = null;
            info = GetIdentificationInfo(island, observerWorld, lookout, spyglassZoom);
            if (!info.Identified)
                return false;

            islandName = info.IslandName;
            return true;
        }

        internal static LookoutIslandIdentificationInfo GetIdentificationInfo(
            IslandHorizon island,
            Vector3 observerWorld,
            Crewman lookout,
            float spyglassZoom)
        {
            var info = new LookoutIslandIdentificationInfo();
            if (island == null)
                return info;

            info.HasPortName = TryGetPortName(island, out info.IslandName);
            info.HasVisited = info.HasPortName && HasPlayerVisitedPort(info.IslandName);

            if (lookout != null
                && LookoutVisibility.TryEvaluate(island, observerWorld, lookout, spyglassZoom, out var visibility))
            {
                info.AngleDeg = visibility.AngleDeg;
                info.EffectiveAngleDeg = visibility.AngleDeg * Mathf.Max(1f, spyglassZoom);
                info.CurrentlyVisible = visibility.IsVisible;
                info.AngleLargeEnough = info.EffectiveAngleDeg > IdentificationAngleDeg;
            }

            info.Identified = info.HasPortName
                && info.HasVisited
                && info.AngleLargeEnough;
            return info;
        }

        internal static bool HasPlayerVisitedIsland(IslandHorizon island)
        {
            if (!TryGetPortName(island, out string portName))
                return false;

            return HasPlayerVisitedPort(portName);
        }

        internal static bool TryGetPortName(IslandHorizon island, out string portName)
        {
            portName = null;
            if (island == null || Port.ports == null)
                return false;

            foreach (Port port in Port.ports)
            {
                if (port != null && island.economy != null && port.island == island.economy)
                {
                    portName = port.GetPortName();
                    return !string.IsNullOrEmpty(portName);
                }
            }

            return false;
        }

        private static bool HasPlayerVisitedPort(string portName)
        {
            return VirtualCrewManager.Instance.HasVisitedPort(portName)
                || SailAdexHasVisitedPort(portName);
        }

        private static bool SailAdexHasVisitedPort(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return false;

            if (!TryGetSailAdexVisitedPorts(out var visitedPorts))
                return false;

            return visitedPorts.TryGetValue(portName, out bool visited) && visited;
        }

        private static bool TryGetSailAdexVisitedPorts(out IReadOnlyDictionary<string, bool> visitedPorts)
        {
            visitedPorts = null;
            if (!EnsureSailAdexReflection())
                return false;

            try
            {
                object instance = _sailAdexInstanceProperty.GetValue(null, null);
                if (instance == null)
                    return false;

                visitedPorts = _sailAdexVisitedPortsProperty.GetValue(instance, null)
                    as IReadOnlyDictionary<string, bool>;
                return visitedPorts != null;
            }
            catch
            {
                _sailAdexPortsVisitedType = null;
                _sailAdexInstanceProperty = null;
                _sailAdexVisitedPortsProperty = null;
                return false;
            }
        }

        private static bool EnsureSailAdexReflection()
        {
            if (_sailAdexPortsVisitedType != null
                && _sailAdexInstanceProperty != null
                && _sailAdexVisitedPortsProperty != null)
                return true;

            if (Time.time < _nextSailAdexLookupTime)
                return false;

            _nextSailAdexLookupTime = Time.time + 5f;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("sailadex.PortsVisitedUI", false);
                if (type == null)
                    continue;

                _sailAdexPortsVisitedType = type;
                _sailAdexInstanceProperty = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _sailAdexVisitedPortsProperty = type.GetProperty("VisitedPorts", BindingFlags.Public | BindingFlags.Instance);
                return _sailAdexInstanceProperty != null && _sailAdexVisitedPortsProperty != null;
            }

            return false;
        }
    }
}
