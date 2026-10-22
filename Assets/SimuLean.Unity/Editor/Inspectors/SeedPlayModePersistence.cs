using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Keeps the seed settings of the scene's <see cref="UnitySimClock"/> that were changed during Play
    /// mode (Keep / New / typed seed in the inspector or the runtime HUD), which Unity would otherwise
    /// revert when Play mode ends: on exit they are stored in <see cref="SessionState"/> and applied
    /// (with Undo, scene marked dirty) to the clock of the same scene in Edit mode. Editor-only; nothing
    /// is added to the scene and nothing runs in builds.
    /// </summary>
    [InitializeOnLoad]
    internal static class SeedPlayModePersistence
    {
        private const string Key = "SimuLean.SeedPlayModePersistence";

        static SeedPlayModePersistence()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                var clock = UnityCompat.FindAny<UnitySimClock>();
                if (clock != null && clock.SeedSettingsChangedInPlayMode)
                    SessionState.SetString(Key, $"{(int)clock.SeedMode}|{clock.Seed}|{clock.gameObject.scene.path}");
                else
                    SessionState.EraseString(Key);
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                string v = SessionState.GetString(Key, "");
                SessionState.EraseString(Key);
                if (string.IsNullOrEmpty(v)) return;

                string[] p = v.Split(new[] { '|' }, 3);
                if (p.Length < 3 || !int.TryParse(p[0], out int mode) || !int.TryParse(p[1], out int seed)) return;
                var clock = UnityCompat.FindAny<UnitySimClock>();
                if (clock == null || clock.gameObject.scene.path != p[2]) return;
                if (clock.SeedMode == (SeedMode)mode && clock.Seed == seed) return;

                Undo.RecordObject(clock, "Keep Seed Settings From Play Mode");
                clock.Seed = seed;
                clock.SeedMode = (SeedMode)mode;
                EditorUtility.SetDirty(clock);
                EditorSceneManager.MarkSceneDirty(clock.gameObject.scene);
                Debug.Log($"[UnitySimClock] Seed settings from Play mode kept: {(SeedMode)mode}" +
                          ((SeedMode)mode == SeedMode.Fixed ? $", seed {seed}." : "."));
            }
        }
    }
}
