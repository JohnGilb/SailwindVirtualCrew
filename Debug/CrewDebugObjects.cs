using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewDebugObjects
    {
        private const string Phase = "Phase01";
        private const string Prefix = "VC_Debug_";
        private static readonly List<GameObject> Objects = new List<GameObject>();
        private static CrewDebugLocalMarker _localMarker;
        private static CrewAgent _testCrew;
        private static ProxyBoat _proxyBoat;
        private static ProxyNavMeshNavigationProvider _navMeshProvider;
        private static ProxyLogicAgent _logicAgent;
        private static ProxyToBoatPoseSync _poseSync;
        private static CrewStationTask _stationTask;
        private static List<CrewStation> _stations = new List<CrewStation>();
        private static int _selectedStationIndex;

        internal static void HighlightBoatContext()
        {
            Clear();

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            CreateMarker("TopBoat", context.TopBoat, Color.red, 0.7f);
            CreateMarker("WorldBoat", context.WorldBoat, Color.green, 0.55f);
            CreateMarker("WalkCol", context.WalkCol, Color.cyan, 0.45f);

            CrewDebugLog.Ok(Phase, "Created context highlight markers for topBoat, worldBoat, and walkCol origins.");
        }

        internal static void Clear()
        {
            ClearLocalMarker();
            CancelCurrentStationTask();
            DespawnTestCrewVisual();
            DestroyNavProxy();
            _stations.Clear();
            _selectedStationIndex = 0;

            foreach (var obj in Objects)
            {
                if (obj)
                    Object.Destroy(obj);
            }
            Objects.Clear();

            var transforms = Object.FindObjectsOfType<Transform>();
            foreach (var transform in transforms)
            {
                if (transform && transform.name.StartsWith(Prefix))
                    Object.Destroy(transform.gameObject);
            }

            CrewDebugLog.Ok(Phase, "Cleared debug objects.");
        }

        internal static void Tick()
        {
            if (_localMarker == null)
            {
                _logicAgent?.Tick();
                _stationTask?.Tick();
                _poseSync?.Tick();
                return;
            }

            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return;

            _localMarker.Update(new CrewSpaceMapper(context));
            _logicAgent?.Tick();
            _stationTask?.Tick();
            _poseSync?.Tick();
        }

        internal static void SpawnTestMarkerAtPlayer()
        {
            ClearLocalMarker();

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            if (Refs.observerMirror == null)
            {
                CrewDebugLog.Fail("Phase02", "Refs.observerMirror is null; cannot place marker at player.");
                return;
            }

            var mapper = new CrewSpaceMapper(context);
            var playerTransform = Refs.observerMirror.transform;
            Vector3 localPosition = mapper.WorldBoatLocalFromWorld(playerTransform.position);
            Quaternion localRotation = mapper.WorldBoatLocalRotationFromWorld(playerTransform.rotation);
            _localMarker = new CrewDebugLocalMarker(localPosition, localRotation);
            _localMarker.Update(mapper);
            _localMarker.LogPose(mapper);
            CrewDebugLog.Ok("Phase02", "Spawned test marker at player.");
        }

        internal static void MoveTestMarkerForwardLocal()
        {
            if (_localMarker == null)
            {
                CrewDebugLog.Warn("Phase02", "No local marker exists; spawn one before moving it.");
                return;
            }

            _localMarker.MoveLocal(Vector3.forward);
            DumpTestMarkerLocalPose();
        }

        internal static void DumpTestMarkerLocalPose()
        {
            if (_localMarker == null)
            {
                CrewDebugLog.Warn("Phase02", "No local marker exists; spawn one before dumping pose.");
                return;
            }

            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return;

            var mapper = new CrewSpaceMapper(context);
            _localMarker.Update(mapper);
            _localMarker.LogPose(mapper);
        }

        private static void ClearLocalMarker()
        {
            if (_localMarker != null)
            {
                _localMarker.Destroy();
                _localMarker = null;
                CrewDebugLog.Ok("Phase02", "Cleared local-space test marker.");
            }
        }

        internal static void SpawnTestCrewVisual()
        {
            DespawnTestCrewVisual();

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            if (Refs.observerMirror == null)
            {
                CrewDebugLog.Fail("Phase03", "Refs.observerMirror is null; cannot place crew visual near player.");
                return;
            }

            var mapper = new CrewSpaceMapper(context);
            var playerTransform = Refs.observerMirror.transform;
            Vector3 spawnWorldPosition = playerTransform.position + playerTransform.right * 1.2f;
            Vector3 localPosition = mapper.WorldBoatLocalFromWorld(spawnWorldPosition);
            Quaternion localRotation = mapper.WorldBoatLocalRotationFromWorld(Quaternion.LookRotation(playerTransform.forward, context.WorldBoat.up));

            _testCrew = CrewVisualFactory.SpawnTestCrewVisual(context, localPosition, localRotation);
        }

        internal static void DespawnTestCrewVisual()
        {
            _stationTask = null;
            _poseSync = null;
            if (_testCrew == null)
                return;

            _testCrew.Destroy();
            _testCrew = null;
            CrewDebugLog.Ok("Phase03", "Despawned test crew visual.");
        }

        internal static void DumpTestCrewPose()
        {
            CrewVisualFactory.LogPose(_testCrew);
        }

        internal static void CreateNavProxy()
        {
            DestroyNavProxy();

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            _proxyBoat = ProxyBoatBuilder.Create(context);
        }

        internal static void SetupProxyAgent()
        {
            CreateNavProxy();
            BakeProxyNavMesh();
            SpawnLogicAgent();
        }

        internal static void SetupSyncedAgent()
        {
            SetupProxyAgent();
            SyncVisualFromProxy();
        }

        internal static void DestroyNavProxy()
        {
            ClearProxyNavMesh();

            if (_proxyBoat == null)
                return;

            ProxyBoatBuilder.Destroy(_proxyBoat);
            _proxyBoat = null;
            CrewDebugLog.Ok("Phase04", "Destroyed nav proxy.");
        }

        internal static void DumpProxyBounds()
        {
            ProxyBoatBuilder.LogProxy(_proxyBoat);
        }

        internal static void ShowProxyMarkers()
        {
            ProxyBoatBuilder.ShowProxyMarkers(_proxyBoat);
        }

        internal static void BakeProxyNavMesh()
        {
            if (_proxyBoat == null || !_proxyBoat.IsValid)
                CreateNavProxy();

            if (_proxyBoat == null || !_proxyBoat.IsValid)
                return;

            _navMeshProvider = new ProxyNavMeshNavigationProvider(_proxyBoat);
            _navMeshProvider.Bake();
        }

        internal static void ClearProxyNavMesh()
        {
            DespawnLogicAgent();

            if (_navMeshProvider == null)
                return;

            _navMeshProvider.Clear();
            _navMeshProvider = null;
            CrewDebugLog.Ok("Phase05", "Cleared proxy NavMesh.");
        }

        internal static void SampleNavMeshAtMarker()
        {
            if (!EnsureNavMeshProvider())
                return;

            Vector3 local = GetSampleLocalPosition();
            _navMeshProvider.SampleLocal(local, 6f, out _);
        }

        internal static void CalculateTestPath()
        {
            if (!EnsureNavMeshProvider())
                return;

            Vector3 center = GetSampleLocalPosition();
            Vector3 from = center + Vector3.right * -3f;
            Vector3 to = center + Vector3.right * 3f;
            _navMeshProvider.CalculateTestPath(from, to);
        }

        internal static void SpawnLogicAgent()
        {
            DespawnLogicAgent();

            if (!EnsureNavMeshProvider())
                return;

            if (!TryFindAgentStart(out var local, out var world))
            {
                CrewDebugLog.Fail("Phase06", "Could not place logic agent on the proxy NavMesh.");
                return;
            }

            _logicAgent = new ProxyLogicAgent(_navMeshProvider.Proxy.Root.transform, world);
        }

        internal static void MoveLogicAgentToBow()
        {
            MoveLogicAgentToBoundsEnd(-1f, "bow");
        }

        internal static void MoveLogicAgentToStern()
        {
            MoveLogicAgentToBoundsEnd(1f, "stern");
        }

        internal static void DumpLogicAgentState()
        {
            if (_logicAgent == null)
            {
                CrewDebugLog.Warn("Phase06", "No logic agent exists.");
                return;
            }

            _logicAgent.DumpState();
        }

        internal static void DespawnLogicAgent()
        {
            _stationTask = null;
            _poseSync = null;
            if (_logicAgent == null)
                return;

            _logicAgent.Destroy();
            _logicAgent = null;
            CrewDebugLog.Ok("Phase06", "Destroyed logic agent.");
        }

        private static void MoveLogicAgentToBoundsEnd(float direction, string label)
        {
            if (_logicAgent == null || !_logicAgent.IsValid)
            {
                CrewDebugLog.Warn("Phase06", "No logic agent exists; spawn one first.");
                return;
            }

            if (!EnsureNavMeshProvider())
                return;

            Vector3 destinationLocal = GetBoundsLocalCenter();
            destinationLocal.x += (_proxyBoat.Bounds.size.x * 0.38f) * direction;
            destinationLocal.y = localDeckSearchY(destinationLocal.y);
            if (!_navMeshProvider.TryGetWorldOnNavMesh(destinationLocal, GetNavMeshSearchDistance(), out var destinationWorld))
            {
                CrewDebugLog.Fail("Phase06", "Could not sample " + label + " destination onto the proxy NavMesh.");
                return;
            }

            Vector3 sampledDestinationLocal = _navMeshProvider.WorldToProxyLocal(destinationWorld);
            CrewDebugLog.Ok("Phase06",
                "Moving logic agent to " + label
                + " sampledLocal=" + Format(sampledDestinationLocal));
            _logicAgent.SetDestination(destinationWorld, sampledDestinationLocal);
        }

        internal static void SyncVisualFromProxy()
        {
            if (_logicAgent == null || !_logicAgent.IsValid)
            {
                CrewDebugLog.Warn("Phase07", "No logic agent exists; spawn one before syncing a visual.");
                return;
            }

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            if (_testCrew == null || !_testCrew.VisualRoot)
                _testCrew = CrewVisualFactory.SpawnTestCrewVisual(context, _logicAgent.CurrentLocalPosition, _logicAgent.CurrentLocalRotation);

            _poseSync = new ProxyToBoatPoseSync(_testCrew, _logicAgent, context);
            _poseSync.SetPaused(false);
            _poseSync.Tick();
            CrewDebugLog.Ok("Phase07", "Started visual sync from proxy agent.");
        }

        internal static void MoveSyncedAgentToBow()
        {
            if (_poseSync == null)
                SyncVisualFromProxy();
            MoveLogicAgentToBow();
        }

        internal static void MoveSyncedAgentToStern()
        {
            if (_poseSync == null)
                SyncVisualFromProxy();
            MoveLogicAgentToStern();
        }

        internal static void ToggleVisualSyncPause()
        {
            if (_poseSync == null)
            {
                CrewDebugLog.Warn("Phase07", "Visual sync is not active.");
                return;
            }

            _poseSync.SetPaused(!_poseSync.IsPaused);
        }

        internal static void DumpVisualSyncState()
        {
            if (_poseSync == null)
            {
                CrewDebugLog.Warn("Phase07", "Visual sync is not active.");
                return;
            }

            _poseSync.Dump();
        }

        internal static void ScanWorkstations()
        {
            if (_navMeshProvider == null || !_navMeshProvider.IsBaked)
                SetupProxyAgent();

            if (!EnsureNavMeshProvider())
                return;

            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return;

            var scanner = new CrewStationScanner(context, _navMeshProvider);
            _stations = scanner.Scan();
            _selectedStationIndex = 0;
        }

        internal static void DumpStations()
        {
            if (_stations == null || _stations.Count == 0)
            {
                CrewDebugLog.Warn("Phase08", "No stations scanned.");
                return;
            }

            CrewDebugLog.Ok("Phase08", "Station count=" + _stations.Count + ", selectedIndex=" + _selectedStationIndex);
            for (int i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                CrewDebugLog.Ok("Phase08",
                    "station[" + i + "] id='" + station.Id
                    + "' type='" + station.TypeName
                    + "' projected=" + station.Projected
                    + " localStand=" + Format(station.ProjectedLocalStand)
                    + " path='" + station.TransformPath + "'");
            }
        }

        internal static void SelectNextStation()
        {
            if (_stations == null || _stations.Count == 0)
            {
                CrewDebugLog.Warn("Phase08", "No stations scanned.");
                return;
            }

            _selectedStationIndex = (_selectedStationIndex + 1) % _stations.Count;
            var station = _stations[_selectedStationIndex];
            CrewDebugLog.Ok("Phase08", "Selected station index=" + _selectedStationIndex + " id='" + station.Id + "'");
        }

        internal static void ShowStationMarkers()
        {
            if (_stations == null || _stations.Count == 0)
            {
                CrewDebugLog.Warn("Phase08", "No stations scanned.");
                return;
            }

            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return;

            int created = 0;
            foreach (var station in _stations)
            {
                if (station.Projected)
                {
                    var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    marker.name = "VC_Debug_Station_" + station.Id;
                    marker.transform.SetParent(context.WorldBoat, false);
                    marker.transform.localPosition = station.ProjectedLocalStand;
                    marker.transform.localRotation = station.LocalRotation;
                    marker.transform.localScale = new Vector3(0.45f, 0.08f, 0.45f);

                    var collider = marker.GetComponent<Collider>();
                    if (collider)
                        Object.Destroy(collider);

                    var renderer = marker.GetComponent<Renderer>();
                    if (renderer)
                        renderer.material.color = station.TypeName == "helm" ? Color.green : station.TypeName == "rope" ? Color.yellow : Color.cyan;

                    Objects.Add(marker);
                    created++;
                }
                else
                {
                    // Red sphere at the requested (unprojected) stand position so you can see where the projection attempt landed relative to the NavMesh.
                    var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.name = "VC_Debug_Station_Failed_" + station.Id;
                    marker.transform.SetParent(context.WorldBoat, false);
                    marker.transform.localPosition = station.RequestedLocalStand;
                    marker.transform.localScale = Vector3.one * 0.35f;

                    var collider = marker.GetComponent<Collider>();
                    if (collider)
                        Object.Destroy(collider);

                    var renderer = marker.GetComponent<Renderer>();
                    if (renderer)
                        renderer.material.color = Color.red;

                    Objects.Add(marker);
                    created++;
                }
            }

            CrewDebugLog.Ok("Phase08", "Created station markers count=" + created);
        }

        internal static void MoveAgentToSelectedStation()
        {
            if (_stations == null || _stations.Count == 0)
            {
                CrewDebugLog.Warn("Phase08", "No stations scanned.");
                return;
            }

            if (_logicAgent == null || !_logicAgent.IsValid)
                SpawnLogicAgent();

            if (_logicAgent == null || !_logicAgent.IsValid || !EnsureNavMeshProvider())
                return;

            var station = _stations[_selectedStationIndex];
            if (!station.Projected)
            {
                CrewDebugLog.Warn("Phase08", "Selected station is not projected: " + station.Id);
                return;
            }

            if (_poseSync == null)
                SyncVisualFromProxy();

            Vector3 destinationWorld = _navMeshProvider.Proxy.Root.transform.TransformPoint(station.ProjectedLocalStand);
            CrewDebugLog.Ok("Phase08", "Moving agent to selected station='" + station.Id + "'");
            _logicAgent.SetDestination(destinationWorld, station.ProjectedLocalStand);
        }

        internal static void AssignCrewToSelectedStation()
        {
            if (_stations == null || _stations.Count == 0)
            {
                CrewDebugLog.Warn("Phase09", "No stations scanned.");
                return;
            }

            if (_logicAgent == null || !_logicAgent.IsValid)
                SpawnLogicAgent();

            if (_logicAgent == null || !_logicAgent.IsValid || !EnsureNavMeshProvider())
                return;

            if (_poseSync == null)
                SyncVisualFromProxy();

            var station = _stations[_selectedStationIndex];
            if (!station.Projected)
            {
                CrewDebugLog.Warn("Phase09", "Selected station is not projected: " + station.Id);
                return;
            }

            if (_stationTask != null && _stationTask.State != CrewStationTaskState.Cancelled)
                _stationTask.Cancel();

            _stationTask = new CrewStationTask(station, _logicAgent, _navMeshProvider, _poseSync);
            _stationTask.Begin();
        }

        internal static void CancelCurrentStationTask()
        {
            if (_stationTask == null)
                return;

            _stationTask.Cancel();
            _stationTask = null;
        }

        internal static void DumpStationTaskState()
        {
            if (_stationTask == null)
            {
                CrewDebugLog.Ok("Phase09", "Task state=None reservedStation='none'");
                return;
            }

            _stationTask.Dump();
        }

        private static bool EnsureNavMeshProvider()
        {
            if (_navMeshProvider == null || !_navMeshProvider.IsBaked)
            {
                CrewDebugLog.Warn("Phase05", "Proxy NavMesh is not baked; click Bake Proxy NavMesh first.");
                return false;
            }

            return true;
        }

        private static Vector3 GetSampleLocalPosition()
        {
            if (_localMarker != null)
                return _localMarker.WorldBoatLocalPosition;

            if (_proxyBoat != null && _proxyBoat.IsValid)
                return _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.center);

            return Vector3.zero;
        }

        private static bool TryFindAgentStart(out Vector3 local, out Vector3 world)
        {
            local = GetSampleLocalPosition();
            world = Vector3.zero;

            if (_navMeshProvider.TryGetWorldOnNavMesh(local, GetNavMeshSearchDistance(), out world))
                return true;

            if (_proxyBoat == null || !_proxyBoat.IsValid)
                return false;

            Vector3 center = GetBoundsLocalCenter();
            float minY = _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.min).y;
            float maxY = _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.max).y;
            float[] candidateY =
            {
                local.y,
                center.y,
                minY + 2f,
                minY + 4f,
                minY + 6f,
                minY + 8f,
                (minY + maxY) * 0.5f
            };

            for (int i = 0; i < candidateY.Length; i++)
            {
                local = new Vector3(center.x, candidateY[i], center.z);
                if (_navMeshProvider.TryGetWorldOnNavMesh(local, GetNavMeshSearchDistance(), out world))
                    return true;
            }

            return false;
        }

        private static float GetNavMeshSearchDistance()
        {
            if (_proxyBoat == null || !_proxyBoat.IsValid)
                return 12f;

            return Mathf.Max(12f, _proxyBoat.Bounds.size.y + 2f);
        }

        private static float localDeckSearchY(float fallback)
        {
            if (_localMarker != null)
                return _localMarker.WorldBoatLocalPosition.y;

            if (_proxyBoat != null && _proxyBoat.IsValid)
                return _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.min).y + 6f;

            return fallback;
        }

        private static Vector3 GetBoundsLocalCenter()
        {
            if (_proxyBoat != null && _proxyBoat.IsValid)
                return _proxyBoat.Root.transform.InverseTransformPoint(_proxyBoat.Bounds.center);

            return Vector3.zero;
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }

        private static void CreateMarker(string name, Transform parent, Color color, float size)
        {
            if (!parent)
                return;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = Prefix + name;
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localScale = Vector3.one * size;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;

            Objects.Add(marker);
        }
    }
}
