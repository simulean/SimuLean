using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>Editor: routes the engine's <see cref="SimLog"/> to the Unity console after every domain reload.</summary>
    [InitializeOnLoad]
    internal static class SimLogEditorBridge
    {
        static SimLogEditorBridge() => SimLogUnityBridge.Install();
    }
}
