using System;

namespace SailwindVirtualCrew
{
    internal static class CrewDebugLog
    {
        internal static void Ok(string phase, string message)
        {
            Write(phase, "OK", message);
        }

        internal static void Warn(string phase, string message)
        {
            Write(phase, "WARN", message);
        }

        internal static void Fail(string phase, string message)
        {
            Write(phase, "FAIL", message);
        }

        internal static void Info(string phase, string message)
        {
            Write(phase, "INFO", message);
        }

        private static void Write(string phase, string level, string message)
        {
            Console.WriteLine($"[VirtualCrew][{phase}][{level}] {message}");
        }
    }
}
