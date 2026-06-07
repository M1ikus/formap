using System;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Logger callbacks dla shared library. Caller (Unity → Debug.Log, formap → Console.WriteLine)
    /// sets Info/Warn/Error sinks. Library nie hardcoduje na UnityEngine ani System.Console.
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
