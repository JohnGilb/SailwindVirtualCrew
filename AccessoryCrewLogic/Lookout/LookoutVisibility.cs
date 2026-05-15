using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SailwindVirtualCrew
{
    internal struct LookoutWaveSample
    {
        internal Vector3 SightlineWorldPosition;
        internal float Distance;
        internal float WaveHeight;
        internal float SightlineHeight;
        internal bool BlocksLineOfSight;
    }

    internal struct LookoutVisibilityResult
    {
        internal bool IsVisible;
        internal bool WaveBlocked;
        internal float Distance;
        internal float CurrentDrop;
        internal float PeakAboveRoot;
        internal float VisibleHeight;
        internal float AngleDeg;
        internal float ThresholdDeg;
        internal float WaveBlockDistance;
        internal float WaveBlockHeight;
        internal float SightlineHeightAtBlock;
        internal float WaveSampleMaxDistance;
        internal float WaveSampleSpacing;
        internal int WaveSampleCount;
        internal Vector3 PeakWorldPosition;
    }

    internal static class LookoutVisibility
    {
        internal const float MaxWaveOcclusionDistance = 100f;
        internal const float WaveSampleSpacing = 1f;
        internal const float FirstWaveSampleDistance = 2f;
        internal const float WaveOcclusionClearance = 0.2f;
        internal const int MaxWaveSamples = 100;

        internal static bool TryEvaluate(
            IslandHorizon island,
            Vector3 observerWorld,
            Crewman lookout,
            float spyglassZoom,
            out LookoutVisibilityResult result,
            List<LookoutWaveSample> waveSamples = null)
        {
            result = new LookoutVisibilityResult();
            waveSamples?.Clear();
            if (island == null || lookout == null)
                return false;

            if (!TryGetPeakWorldPosition(island, out var peakWorld))
                return false;

            float distance = Vector3.Distance(peakWorld, observerWorld);
            if (distance <= 0f)
                return false;

            float currentDrop = GetInitialHeight(island) - island.transform.localPosition.y;
            float peakAboveRoot = peakWorld.y - island.transform.position.y;
            float visibleHeight = peakAboveRoot - currentDrop;
            float angleDeg = Mathf.Atan2(visibleHeight, distance) * Mathf.Rad2Deg;
            float threshold = GetEffectiveVisibilityThreshold(lookout, spyglassZoom);

            result.Distance = distance;
            result.CurrentDrop = currentDrop;
            result.PeakAboveRoot = peakAboveRoot;
            result.VisibleHeight = visibleHeight;
            result.AngleDeg = angleDeg;
            result.ThresholdDeg = threshold;
            result.PeakWorldPosition = peakWorld;

            bool angleVisible = visibleHeight > 0f && angleDeg >= threshold;
            if (!angleVisible && waveSamples == null)
                return true;

            result.WaveBlocked = IsBlockedByWave(
                observerWorld,
                peakWorld,
                out result.WaveBlockDistance,
                out result.WaveBlockHeight,
                out result.SightlineHeightAtBlock,
                out result.WaveSampleMaxDistance,
                out result.WaveSampleSpacing,
                out result.WaveSampleCount,
                waveSamples);
            result.IsVisible = angleVisible && !result.WaveBlocked;
            return true;
        }

        internal static float GetBaseVisibilityThreshold(Crewman lookout)
        {
            float threshold = 1f - lookout.Wisdom * 0.1f;
            if (IsNightwatch()) threshold *= 5f;
            return threshold;
        }

        internal static float GetEffectiveVisibilityThreshold(Crewman lookout, float spyglassZoom)
        {
            return GetBaseVisibilityThreshold(lookout) / Mathf.Max(1f, spyglassZoom);
        }

        internal static string GetIslandKey(IslandHorizon island)
        {
            if (island == null)
                return string.Empty;

            if (island.islandIndex >= 0)
                return "scene:" + island.islandIndex;

            string name = island.gameObject != null ? island.gameObject.name : null;
            return !string.IsNullOrEmpty(name)
                ? "name:" + name
                : "instance:" + island.GetInstanceID();
        }

        private static bool TryGetPeakWorldPosition(IslandHorizon island, out Vector3 peakWorld)
        {
            peakWorld = Vector3.zero;
            float maxY = float.MinValue;
            Bounds bestBounds = default(Bounds);
            bool found = false;

            foreach (var renderer in island.GetComponentsInChildren<Renderer>())
                ConsiderRenderer(renderer, ref maxY, ref bestBounds, ref found);

            if (island.islandIndex > 0 && island.SceneLoaded())
            {
                var scene = SceneManager.GetSceneByBuildIndex(island.islandIndex);
                if (scene.isLoaded)
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                            ConsiderRenderer(renderer, ref maxY, ref bestBounds, ref found);
            }

            if (!found)
                return false;

            peakWorld = new Vector3(bestBounds.center.x, maxY, bestBounds.center.z);
            return true;
        }

        private static void ConsiderRenderer(Renderer renderer, ref float maxY, ref Bounds bestBounds, ref bool found)
        {
            if (renderer == null || renderer.bounds.min.y >= 250f || renderer.bounds.max.y <= maxY)
                return;

            maxY = renderer.bounds.max.y;
            bestBounds = renderer.bounds;
            found = true;
        }

        private static bool IsBlockedByWave(
            Vector3 observerWorld,
            Vector3 peakWorld,
            out float blockDistance,
            out float waveHeight,
            out float sightlineHeight,
            out float sampledMaxDistance,
            out float actualSpacing,
            out int sampleCount,
            List<LookoutWaveSample> waveSamples)
        {
            blockDistance = 0f;
            waveHeight = 0f;
            sightlineHeight = 0f;
            sampledMaxDistance = 0f;
            actualSpacing = 0f;
            sampleCount = 0;

            Vector2 observerXZ = new Vector2(observerWorld.x, observerWorld.z);
            Vector2 peakXZ = new Vector2(peakWorld.x, peakWorld.z);
            float horizontalDistance = Vector2.Distance(observerXZ, peakXZ);
            if (horizontalDistance <= FirstWaveSampleDistance)
                return false;

            float maxDistance = Mathf.Min(horizontalDistance - 0.1f, MaxWaveOcclusionDistance);
            float startDistance = Mathf.Min(FirstWaveSampleDistance, maxDistance);
            float sampleSpan = Mathf.Max(0f, maxDistance - startDistance);
            int samples = Mathf.Min(MaxWaveSamples, Mathf.Max(1, Mathf.CeilToInt(sampleSpan / WaveSampleSpacing) + 1));
            if (samples <= 0)
                return false;

            sampledMaxDistance = maxDistance;
            actualSpacing = samples > 1 ? sampleSpan / (samples - 1) : 0f;
            sampleCount = samples;

            bool blocked = false;
            for (int i = 0; i < samples; i++)
            {
                float sampleDistance = samples == 1
                    ? startDistance
                    : startDistance + sampleSpan * i / (samples - 1);
                float t = sampleDistance / horizontalDistance;
                Vector3 samplePoint = Vector3.Lerp(observerWorld, peakWorld, t);
                if (!WaveHeightSampler.TryGetHeight(samplePoint, out float sampledWaveHeight))
                    continue;

                bool blocksLineOfSight = sampledWaveHeight + WaveOcclusionClearance > samplePoint.y;
                if (waveSamples != null)
                {
                    waveSamples.Add(new LookoutWaveSample
                    {
                        SightlineWorldPosition = samplePoint,
                        Distance = sampleDistance,
                        WaveHeight = sampledWaveHeight,
                        SightlineHeight = samplePoint.y,
                        BlocksLineOfSight = blocksLineOfSight
                    });
                }

                if (blocksLineOfSight && !blocked)
                {
                    blockDistance = sampleDistance;
                    waveHeight = sampledWaveHeight;
                    sightlineHeight = samplePoint.y;
                    blocked = true;

                    if (waveSamples == null)
                        return true;
                }
            }

            return blocked;
        }

        private static float GetInitialHeight(IslandHorizon island)
        {
            try { return Traverse.Create(island).Field("initialHeight").GetValue<float>(); }
            catch { return 0f; }
        }

        private static bool IsNightwatch()
        {
            if (Sun.sun == null)
                return false;

            float t = Sun.sun.localTime;
            return t >= 20f || t < 4f;
        }

        private static class WaveHeightSampler
        {
            private static bool _crestInitialized;
            private static bool _crestAvailable;
            private static object _crestHelper;
            private static MethodInfo _crestGetHeight;

            internal static bool TryGetHeight(Vector3 worldPos, out float height)
            {
                if (TryGetCrestHeight(worldPos, out height))
                    return true;

                var ocean = Ocean.Singleton;
                if (ocean != null)
                {
                    height = ocean.GetHeightChoppyAtLocation2(worldPos.x, worldPos.z);
                    return true;
                }

                height = 0f;
                return false;
            }

            private static bool TryGetCrestHeight(Vector3 worldPos, out float height)
            {
                height = 0f;
                EnsureCrestInitialized();
                if (!_crestAvailable)
                    return false;

                try
                {
                    height = (float)_crestGetHeight.Invoke(null, new object[] { _crestHelper, worldPos, 0.6f });
                    return true;
                }
                catch
                {
                    _crestAvailable = false;
                    return false;
                }
            }

            private static void EnsureCrestInitialized()
            {
                if (_crestInitialized)
                    return;

                _crestInitialized = true;
                try
                {
                    var helperType = AccessTools.TypeByName("Crest.SampleHeightHelper");
                    var oceanHeightType = AccessTools.TypeByName("OceanHeight");
                    if (helperType == null || oceanHeightType == null)
                        return;

                    _crestHelper = Activator.CreateInstance(helperType);
                    _crestGetHeight = oceanHeightType.GetMethod(
                        "GetHeight",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { helperType, typeof(Vector3), typeof(float) },
                        null);
                    _crestAvailable = _crestHelper != null && _crestGetHeight != null;
                }
                catch
                {
                    _crestAvailable = false;
                }
            }
        }
    }
}
