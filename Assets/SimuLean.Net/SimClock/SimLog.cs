using System;

namespace SimuLean
{
    /// <summary>
    /// Engine logging without Unity (the engine assembly has no UnityEngine reference). By default
    /// warnings and errors go to the console's error stream and info is dropped; the Unity layer routes
    /// everything to <c>UnityEngine.Debug</c> (<c>SimLogUnityBridge</c>). Sinks may be called from
    /// worker threads (headless runs).
    /// </summary>
    public static class SimLog
    {
        public static Action<string> InfoSink;
        public static Action<string> WarningSink = m => Console.Error.WriteLine("[SimuLean:WARNING] " + m);
        public static Action<string> ErrorSink = m => Console.Error.WriteLine("[SimuLean:ERROR] " + m);

        public static void Info(string message) => InfoSink?.Invoke(message);
        public static void Warning(string message) => WarningSink?.Invoke(message);
        public static void Error(string message) => ErrorSink?.Invoke(message);
    }
}
