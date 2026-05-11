using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum WeatherState { Clear, PartlyCloudy, Cloudy, Rain, Storm }

    internal static class WeatherUtils
    {
        // Mirrors the cloudyBorder and rainBorder SerializeField defaults in WeatherStorms.
        private const float CloudyBorder = 0.4f;
        private const float RainBorder   = 0.1f;

        public static WeatherState GetWeatherState()
        {
            var storms = WeatherStorms.instance;
            var weather = Weather.instance;
            if (storms == null || weather?.currentRegion == null)
                return WeatherState.Clear;

            var currentStorm = storms.GetCurrentStorm();
            float radius     = currentStorm != null ? currentStorm.GetRadius() : 0f;
            float normalized = (WeatherStorms.currentStormDistance - radius)
                               / weather.currentRegion.stormRange;
            normalized = Mathf.Clamp01(normalized);

            if (normalized >= 1f)           return WeatherState.Clear;
            if (normalized > CloudyBorder)  return WeatherState.PartlyCloudy;
            if (normalized > RainBorder)    return WeatherState.Cloudy;
            if (normalized > 0f)            return WeatherState.Rain;
            return WeatherState.Storm;
        }

        // Celestial tools (quadrant, sun compass, chronometer, chronocompass) need
        // an unobstructed sky — blocked whenever there is any cloud cover.
        public static bool IsCelestialViewBlocked() =>
            GetWeatherState() >= WeatherState.PartlyCloudy;
    }
}
