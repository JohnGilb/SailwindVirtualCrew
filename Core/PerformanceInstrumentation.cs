using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class PerformanceInstrumentation
    {
        private const string DefaultOutputDirectory = "BepInEx\\VirtualCrewProfiles";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, long> FoldedExclusiveTicks = new Dictionary<string, long>();

        [ThreadStatic]
        private static Stack<ActiveSpan> activeSpans;

        private static StreamWriter writer;
        private static string sessionId;
        private static float startedRealtime;
        private static float nextFlushRealtime;
        private static long eventCount;

        internal static bool IsRunning { get; private set; }
        internal static string LastError { get; private set; }
        internal static string OutputDirectory { get; private set; }
        internal static string RawTsvPath { get; private set; }
        internal static string FoldedStackPath { get; private set; }
        internal static long EventCount => eventCount;

        internal static bool IsCollectionAllowed =>
            Plugin.InstrumentationEnabled != null && Plugin.InstrumentationEnabled.Value;

        internal static float ElapsedSeconds =>
            IsRunning ? Mathf.Max(0f, Time.realtimeSinceStartup - startedRealtime) : 0f;

        internal static Measurement Measure(string function)
        {
            if (!IsRunning || !IsCollectionAllowed || writer == null)
                return default(Measurement);

            if (activeSpans == null)
                activeSpans = new Stack<ActiveSpan>();

            ActiveSpan parent = activeSpans.Count > 0 ? activeSpans.Peek() : null;
            var span = new ActiveSpan(function, parent, activeSpans.Count);
            activeSpans.Push(span);
            return new Measurement(span);
        }

        internal static bool StartSession()
        {
            lock (SyncRoot)
            {
                LastError = null;

                if (IsRunning)
                    return true;

                if (!IsCollectionAllowed)
                {
                    LastError = "Instrumentation config is disabled.";
                    return false;
                }

                try
                {
                    OutputDirectory = ResolveOutputDirectory();
                    Directory.CreateDirectory(OutputDirectory);

                    sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    RawTsvPath = Path.Combine(OutputDirectory, "VirtualCrew_Profile_" + sessionId + ".tsv");
                    FoldedStackPath = Path.Combine(OutputDirectory, "VirtualCrew_Profile_" + sessionId + ".folded");

                    writer = new StreamWriter(RawTsvPath, false, Encoding.UTF8, 65536);
                    writer.WriteLine("SessionId\tFrameNumber\tRealtimeSeconds\tFunction\tDurationMicroseconds\tExclusiveMicroseconds\tDepth\tParentFunction\tThreadId");

                    FoldedExclusiveTicks.Clear();
                    eventCount = 0;
                    startedRealtime = Time.realtimeSinceStartup;
                    nextFlushRealtime = startedRealtime + GetFlushIntervalSeconds();
                    IsRunning = true;
                    Console.WriteLine("[VirtualCrew][Instrumentation][INFO] Started profiling session " + sessionId + " at " + OutputDirectory);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    CloseWriter();
                    IsRunning = false;
                    Console.WriteLine("[VirtualCrew][Instrumentation][FAIL] Failed to start profiling: " + ex);
                    return false;
                }
            }
        }

        internal static void StopSession()
        {
            lock (SyncRoot)
            {
                if (!IsRunning && writer == null)
                    return;

                try
                {
                    FlushLocked();
                    WriteFoldedStacksLocked();
                    Console.WriteLine("[VirtualCrew][Instrumentation][INFO] Stopped profiling session " + sessionId + " with " + eventCount + " events.");
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Console.WriteLine("[VirtualCrew][Instrumentation][FAIL] Failed to stop profiling cleanly: " + ex);
                }
                finally
                {
                    CloseWriter();
                    IsRunning = false;
                    if (activeSpans != null)
                        activeSpans.Clear();
                }
            }
        }

        internal static void Update()
        {
            if (IsRunning && !IsCollectionAllowed)
            {
                StopSession();
                return;
            }

            FlushIfDue();
        }

        internal static void FlushNow()
        {
            lock (SyncRoot)
                FlushLocked();
        }

        private static void FlushIfDue()
        {
            if (!IsRunning || writer == null || Time.realtimeSinceStartup < nextFlushRealtime)
                return;

            lock (SyncRoot)
            {
                FlushLocked();
                nextFlushRealtime = Time.realtimeSinceStartup + GetFlushIntervalSeconds();
            }
        }

        private static void Record(ActiveSpan span, long inclusiveTicks, long exclusiveTicks)
        {
            if (inclusiveTicks < 0)
                inclusiveTicks = 0;
            if (exclusiveTicks < 0)
                exclusiveTicks = 0;

            string function = SanitizeTsv(span.Function);
            string parentFunction = span.Parent != null ? SanitizeTsv(span.Parent.Function) : string.Empty;
            double inclusiveMicroseconds = TicksToMicroseconds(inclusiveTicks);
            double exclusiveMicroseconds = TicksToMicroseconds(exclusiveTicks);

            lock (SyncRoot)
            {
                if (!IsRunning || writer == null)
                    return;

                writer.Write(sessionId);
                writer.Write('\t');
                writer.Write(Time.frameCount.ToString(CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(Time.realtimeSinceStartup.ToString("0.000000", CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(function);
                writer.Write('\t');
                writer.Write(inclusiveMicroseconds.ToString("0.###", CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(exclusiveMicroseconds.ToString("0.###", CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(span.Depth.ToString(CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(parentFunction);
                writer.Write('\t');
                writer.Write(Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine();

                if (exclusiveTicks > 0)
                {
                    string stack = BuildFoldedStack(span);
                    long existing;
                    FoldedExclusiveTicks.TryGetValue(stack, out existing);
                    FoldedExclusiveTicks[stack] = existing + exclusiveTicks;
                }

                eventCount++;
            }
        }

        private static string ResolveOutputDirectory()
        {
            string configured = Plugin.InstrumentationOutputDirectory != null
                ? Plugin.InstrumentationOutputDirectory.Value
                : DefaultOutputDirectory;

            if (string.IsNullOrWhiteSpace(configured))
                configured = DefaultOutputDirectory;

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Environment.CurrentDirectory, configured);
        }

        private static float GetFlushIntervalSeconds()
        {
            float interval = Plugin.InstrumentationFlushIntervalSeconds != null
                ? Plugin.InstrumentationFlushIntervalSeconds.Value
                : 5f;
            return Mathf.Clamp(interval, 1f, 60f);
        }

        private static void FlushLocked()
        {
            writer?.Flush();
        }

        private static void WriteFoldedStacksLocked()
        {
            if (string.IsNullOrEmpty(FoldedStackPath))
                return;

            using (var folded = new StreamWriter(FoldedStackPath, false, Encoding.UTF8, 65536))
            {
                foreach (var kv in FoldedExclusiveTicks)
                {
                    long microseconds = Math.Max(1L, (long)Math.Round(TicksToMicroseconds(kv.Value)));
                    folded.Write(kv.Key);
                    folded.Write(' ');
                    folded.WriteLine(microseconds.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static void CloseWriter()
        {
            if (writer == null)
                return;

            writer.Dispose();
            writer = null;
        }

        private static double TicksToMicroseconds(long ticks)
        {
            return ticks * 1000000.0 / Stopwatch.Frequency;
        }

        private static string SanitizeTsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace('\t', ' ')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private static string SanitizeFoldedFrame(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";

            return value.Replace(';', ',')
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static string BuildFoldedStack(ActiveSpan span)
        {
            var frames = new List<string>();
            for (ActiveSpan current = span; current != null; current = current.Parent)
                frames.Add(SanitizeFoldedFrame(current.Function));

            frames.Reverse();
            return string.Join(";", frames.ToArray());
        }

        internal sealed class ActiveSpan
        {
            internal readonly string Function;
            internal readonly ActiveSpan Parent;
            internal readonly int Depth;
            internal readonly long StartTicks;
            internal long ChildTicks;

            internal ActiveSpan(string function, ActiveSpan parent, int depth)
            {
                Function = function;
                Parent = parent;
                Depth = depth;
                StartTicks = Stopwatch.GetTimestamp();
            }
        }

        internal struct Measurement : IDisposable
        {
            private readonly ActiveSpan span;

            internal Measurement(ActiveSpan span)
            {
                this.span = span;
            }

            public void Dispose()
            {
                if (span == null)
                    return;

                long elapsedTicks = Stopwatch.GetTimestamp() - span.StartTicks;
                long exclusiveTicks = elapsedTicks - span.ChildTicks;

                if (activeSpans != null && activeSpans.Count > 0 && ReferenceEquals(activeSpans.Peek(), span))
                    activeSpans.Pop();

                if (span.Parent != null)
                    span.Parent.ChildTicks += elapsedTicks;

                long recordStartTicks = Stopwatch.GetTimestamp();
                try
                {
                    Record(span, elapsedTicks, exclusiveTicks);
                }
                finally
                {
                    if (span.Parent != null)
                        span.Parent.ChildTicks += Stopwatch.GetTimestamp() - recordStartTicks;
                }
            }
        }
    }
}
