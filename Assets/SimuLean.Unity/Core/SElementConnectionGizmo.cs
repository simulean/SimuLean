using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Per-element switch of the connection drawing in the Scene view (added to every SElement). The connections are
    /// drawn by the editor's <c>ConnectionDrawing</c> / SimuLean Connect tool; turning this off hides the ones that
    /// start at this element.
    /// </summary>
    public class SElementConnectionGizmo : MonoBehaviour
    {
        [Tooltip("Draw the connections that start at this element")]
        public bool showConnections = true;
    }
}
