using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewApiProbe
    {
        private const string Phase = "Phase00";

        internal static ProbeResult LastResult { get; private set; }

        internal static ProbeResult Run()
        {
            var result = new ProbeResult();
            CrewDebugLog.Info(Phase, "Running environment and navigation API probe.");

            CrewDebugLog.Ok(Phase, "Unity version: " + Application.unityVersion);

            var aiAssembly = FindAssembly("UnityEngine.AIModule");
            if (aiAssembly != null)
                CrewDebugLog.Ok(Phase, "Found UnityEngine.AIModule: " + aiAssembly.FullName);
            else
                CrewDebugLog.Warn(Phase, "UnityEngine.AIModule is not currently loaded; type lookups will attempt assembly-qualified resolution.");

            result.NavMesh = CheckType("UnityEngine.AI.NavMesh");
            result.NavMeshAgent = CheckType("UnityEngine.AI.NavMeshAgent");
            result.NavMeshData = CheckType("UnityEngine.AI.NavMeshData");
            result.NavMeshBuilder = CheckType("UnityEngine.AI.NavMeshBuilder");
            result.NavMeshBuildSource = CheckType("UnityEngine.AI.NavMeshBuildSource");
            result.NavMeshObstacle = CheckType("UnityEngine.AI.NavMeshObstacle");

            var navMeshBuilder = FindType("UnityEngine.AI.NavMeshBuilder");
            result.UpdateNavMeshData = CheckMethod(navMeshBuilder, "UpdateNavMeshData");
            result.CollectSources = CheckMethod(navMeshBuilder, "CollectSources");

            result.NavMeshSurfaceTypeName = FindNavMeshSurfaceTypeName();
            if (result.NavMeshSurfaceTypeName != null)
                CrewDebugLog.Ok(Phase, "Found NavMeshSurface: " + result.NavMeshSurfaceTypeName);
            else
                CrewDebugLog.Warn(Phase, "NavMeshSurface not found; will use low-level NavMeshBuilder backend if runtime baking is available.");

            if (result.HasCoreNavMeshApis)
                CrewDebugLog.Ok(Phase, "Core NavMesh APIs are available for proxy navigation experiments.");
            else
                CrewDebugLog.Fail(Phase, "Core NavMesh APIs are incomplete; sampled grid navigation may be required.");

            LastResult = result;
            return result;
        }

        private static bool CheckType(string fullName)
        {
            var type = FindType(fullName);
            if (type != null)
            {
                CrewDebugLog.Ok(Phase, "Found " + fullName + ".");
                return true;
            }

            CrewDebugLog.Fail(Phase, "Missing " + fullName + ".");
            return false;
        }

        private static bool CheckMethod(Type type, string methodName)
        {
            if (type == null)
            {
                CrewDebugLog.Fail(Phase, "Cannot check NavMeshBuilder." + methodName + " because NavMeshBuilder is missing.");
                return false;
            }

            bool found = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name == methodName);
            if (found)
                CrewDebugLog.Ok(Phase, "Found " + type.FullName + "." + methodName + ".");
            else
                CrewDebugLog.Fail(Phase, "Missing " + type.FullName + "." + methodName + ".");
            return found;
        }

        private static Assembly FindAssembly(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch
                {
                    // Some mod/plugin assemblies can throw during reflection. They are not relevant here.
                }

                if (type != null)
                    return type;
            }

            return Type.GetType(fullName + ", UnityEngine.AIModule", false);
        }

        private static string FindNavMeshSurfaceTypeName()
        {
            string[] likelyNames =
            {
                "UnityEngine.AI.NavMeshSurface",
                "NavMeshSurface"
            };

            foreach (var name in likelyNames)
            {
                var type = FindType(name);
                if (type != null)
                    return type.FullName;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (type != null && type.Name == "NavMeshSurface")
                        return type.FullName;
                }
            }

            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        }

        internal sealed class ProbeResult
        {
            internal bool NavMesh { get; set; }
            internal bool NavMeshAgent { get; set; }
            internal bool NavMeshData { get; set; }
            internal bool NavMeshBuilder { get; set; }
            internal bool NavMeshBuildSource { get; set; }
            internal bool NavMeshObstacle { get; set; }
            internal bool UpdateNavMeshData { get; set; }
            internal bool CollectSources { get; set; }
            internal string NavMeshSurfaceTypeName { get; set; }

            internal bool HasCoreNavMeshApis => NavMesh && NavMeshAgent && NavMeshData && NavMeshBuilder;

            internal string Summary
            {
                get
                {
                    return HasCoreNavMeshApis
                        ? "Core NavMesh APIs found. Check logs for method and NavMeshSurface details."
                        : "Core NavMesh APIs missing. Check logs before continuing past Phase 0.";
                }
            }
        }
    }
}
