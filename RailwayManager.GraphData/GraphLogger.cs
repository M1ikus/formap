using System;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Logger callbacks for the shared library. The caller (Unity → Debug.Log, formap → Console.WriteLine)
    /// sets the Info/Warn/Error sinks. The library does not hardcode UnityEngine or System.Console.
    /// </summary>
    public static class GraphLogger
    {
        public static Action<string>? Info;
        public static Action<string>? Warn;
        public static Action<string>? Error;

        internal static void LogInfo(string msg) => Info?.Invoke(msg);
        internal static void LogError(string msg) => Error?.Invoke(msg);
    }
}
