using System;

namespace SimuLean.Unity
{
    /// <summary>
    /// The scene's model runs only in the scene, not headless (e.g. a team moved by Unity physics without a travel
    /// calibration). Callers that fall back to a scene run (the Experimenter) or skip such a scene catch this type, not a
    /// message text. An <see cref="InvalidOperationException"/>, so callers that catch that still do.
    /// </summary>
    public sealed class SceneNotHeadlessException : InvalidOperationException
    {
        public SceneNotHeadlessException(string message) : base(message) { }
    }
}
