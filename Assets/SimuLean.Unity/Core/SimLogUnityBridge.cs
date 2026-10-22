using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Routes the engine's <see cref="SimLog"/> (the engine assembly has no UnityEngine reference) to the Unity
    /// console. Installed in the editor on every domain reload (<c>SimLogEditorBridge</c>) and by
    /// <see cref="UnitySimClock"/> on <c>Awake</c> (players). Only sets the log sinks: nothing is added to scenes.
    /// <c>Debug.Log*</c> is thread-safe, so headless runs on worker threads may log too.
    /// </summary>
    public static class SimLogUnityBridge
    {
        public static void Install()
        {
            SimLog.InfoSink = m => Debug.Log(m);
            SimLog.WarningSink = m => Debug.LogWarning(m);
            SimLog.ErrorSink = m => Debug.LogError(m);
        }
    }
}
