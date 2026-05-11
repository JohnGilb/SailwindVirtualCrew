using System;

namespace SailwindVirtualCrew
{
    public class NavigationResult
    {
        public bool HasLatitude   { get; }
        public bool HasLongitude  { get; }
        public string LatitudeText  { get; }
        public string LongitudeText { get; }
        public string Header        { get; }
        public string FailureMessage { get; }
        public bool IsFailure => FailureMessage != null;

        public NavigationResult(NavigationMethod method, int day, float localTime,
            bool partlyCloudy, bool hasLat, float lat, bool hasLon, float lon)
        {
            HasLatitude   = hasLat;
            HasLongitude  = hasLon;
            LatitudeText  = hasLat ? FormatLat(lat) : null;
            LongitudeText = hasLon ? FormatLon(lon) : null;
            Header = FormatHeader(method, day, localTime, partlyCloudy);
        }

        public static NavigationResult Failure(NavigationMethod method, WeatherState weather)
        {
            string tool    = GetDeviceLabel(method);
            string conditions = WeatherConditionLabel(weather);
            return new NavigationResult($"I can't use the {tool}; I can't see through {conditions}!");
        }

        private NavigationResult(string failureMessage)
        {
            FailureMessage = failureMessage;
        }

        private static string FormatHeader(NavigationMethod method, int day, float localTime, bool partlyCloudy)
        {
            bool preciseTime = method == NavigationMethod.Chronometer
                            || method == NavigationMethod.Chronocompass;
            string timeStr  = preciseTime ? $"H{(int)localTime}" : GetTimePeriod(localTime);
            string cloudy   = partlyCloudy ? " [Cloudy]" : "";
            return $"D{day} {timeStr} {GetDeviceLabel(method)}{cloudy}";
        }

        private static string GetTimePeriod(float localTime)
        {
            if (localTime >= 4f  && localTime < 8f)  return "Dawn";
            if (localTime >= 8f  && localTime < 18f) return "Day";
            if (localTime >= 18f && localTime < 20f) return "Dusk";
            return "Night";
        }

        private static string GetDeviceLabel(NavigationMethod method)
        {
            switch (method)
            {
                case NavigationMethod.Quadrant:      return "Quadrant";
                case NavigationMethod.SunCompass:    return "Sun Compass";
                case NavigationMethod.Chronometer:   return "Chronometer";
                case NavigationMethod.Chronocompass: return "Chronocompass";
                default: return method.ToString();
            }
        }

        private static string WeatherConditionLabel(WeatherState weather)
        {
            switch (weather)
            {
                case WeatherState.Rain:  return "the rain";
                case WeatherState.Storm: return "the storm";
                default:                 return "the clouds";
            }
        }

        private static float RoundToQuarterDegree(float v) =>
            (float)Math.Round(v / 0.25) * 0.25f;

        private static string FormatLat(float lat)
        {
            lat = RoundToQuarterDegree(lat);
            string hemi = lat < 0 ? "S" : "N";
            lat = Math.Abs(lat);
            int deg = (int)lat;
            int min = (int)Math.Round((lat - deg) * 60);
            return $"{deg}° {min:D2}' {hemi}";
        }

        private static string FormatLon(float lon)
        {
            lon = RoundToQuarterDegree(lon);
            string hemi = lon < 0 ? "W" : "E";
            lon = Math.Abs(lon);
            int deg = (int)lon;
            int min = (int)Math.Round((lon - deg) * 60);
            return $"{deg}° {min:D2}' {hemi}";
        }
    }
}
