using System;

namespace SailwindVirtualCrew
{
    public class NavigationResult
    {
        public NavigationMethod Method  { get; }
        public int Day { get; }
        public float LocalTime { get; }
        public bool HasLatitude   { get; }
        public bool HasLongitude  { get; }
        public float Latitude { get; }
        public float Longitude { get; }
        public string LatitudeText  { get; }
        public string LongitudeText { get; }
        public string Header        { get; }
        public string FailureMessage { get; }
        public bool IsFailure => FailureMessage != null;

        public NavigationResult(NavigationMethod method, int day, float localTime,
            bool hasLat, float lat, bool hasLon, float lon)
            : this(method, day, localTime, hasLat, lat, hasLon, lon, false)
        {
        }

        public NavigationResult(NavigationMethod method, int day, float localTime,
            bool hasLat, float lat, bool hasLon, float lon, bool hasPreciseTime)
        {
            Method        = method;
            Day           = day;
            LocalTime     = localTime;
            HasLatitude   = hasLat;
            HasLongitude  = hasLon;
            Latitude      = lat;
            Longitude     = lon;
            LatitudeText  = hasLat ? FormatLat(lat) : null;
            LongitudeText = hasLon ? FormatLon(lon) : null;
            Header = FormatHeader(method, day, localTime, hasPreciseTime);
        }

        public static NavigationResult Failure(NavigationMethod method, WeatherState weather)
        {
            string tool    = GetDeviceLabel(method);
            string conditions = WeatherConditionLabel(weather);
            return new NavigationResult(method, $"I can't use the {tool}; I can't see through {conditions}!");
        }

        private NavigationResult(NavigationMethod method, string failureMessage)
        {
            Method = method;
            FailureMessage = failureMessage;
        }

        private static string FormatHeader(NavigationMethod method, int day, float localTime, bool hasPreciseTime)
        {
            bool preciseTime = hasPreciseTime
                            || method == NavigationMethod.Chronometer
                            || method == NavigationMethod.Chronocompass;
            string timeStr  = preciseTime ? FormatPreciseTime(localTime) : GetTimePeriod(localTime);
            return $"D{day} {timeStr} {GetDeviceLabel(method)}";
        }

        private static string FormatPreciseTime(float localTime)
        {
            int totalMinutes = (int)Math.Round(localTime * 60f);
            totalMinutes %= 24 * 60;
            if (totalMinutes < 0)
                totalMinutes += 24 * 60;

            int hour = totalMinutes / 60;
            int minute = totalMinutes % 60;
            return $"H{hour:00}:{minute:00}";
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
                default:                 return "the weather";
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
