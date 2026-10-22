using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The inspector of every element without its own: its fields split between Parameters and
    /// Visual by kind (<see cref="SElementInspector.DrawRemaining"/>), the shared Flow / Resources / Statistics.
    /// </summary>
    [CustomEditor(typeof(SElement), true, isFallback = true)]
    public class GenericElementInspector : SElementInspector
    {
        protected override void DrawParameters() => DrawRemaining(false);
        protected override void DrawVisual() => DrawRemaining(true);
    }
}
