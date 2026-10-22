using System;
using System.Collections.Generic;
using System.IO;
using SimuLean.Results;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The project's SimuLean settings (<c>ProjectSettings/SimuLeanSettings.json</c>, shared through version control):
    /// scene set-up, the project database and results (folder, confidence level, default export formats). Edited in the SimuLean window ▸
    /// Settings; read wherever needed (also at run time in the editor; a player without the file gets the defaults).
    /// </summary>
    [Serializable]
    public sealed class SimuLeanProjectSettings
    {
        public const string RelativePath = "ProjectSettings/SimuLeanSettings.json";

        // scene set-up (SimuLeanSceneSetup)
        public bool autoSetUpNewScenes = true;
        public bool initialSetUpDone;

        // data: the project database every data table, timetable, pick list and export uses
        [Tooltip("The project database (SQLite): relative to the project, or absolute.")]
        public string databasePath = DefaultDatabase;
        public const string DefaultDatabase = "SimuLeanData/simulean.db";

        // results (ResultsOutput)
        [Tooltip("Folder of the exported results: relative to the project, or absolute.")]
        public string resultsFolder = "SimuLeanResults";
        [Tooltip("Confidence level of the intervals across replications.")]
        public double confidence = 0.95;
        [Tooltip("Formats of an export made by hand (the clock's run, the Results tab).")]
        public ResultFormats defaultFormats = ResultFormats.Excel;
        [Tooltip("Keep every experiment, study and optimisation (with all its candidates) in the project database — " +
                 "the Results tab lists them; delete them there. Off by default: results are kept only with a Save button.")]
        public bool storeResults = false;

        /// <summary>The library packs shown in the SimuLean window (<see cref="SimuLeanPacks.Optional"/>; core is always shown).</summary>
        public List<string> shownPacks = new List<string>(SimuLeanPacks.DefaultShown);

        /// <summary>The Library also lists the visual-only presets (buildings, furniture, fences): off by default.</summary>
        public bool showVisuals = false;

        /// <summary>The settings file of the project at <paramref name="projectRoot"/> (defaults when missing or unreadable).</summary>
        public static SimuLeanProjectSettings Load(string projectRoot)
        {
            try
            {
                string path = Path.Combine(projectRoot, RelativePath);
                if (File.Exists(path)) return Sanitised(JsonUtility.FromJson<SimuLeanProjectSettings>(File.ReadAllText(path)));
            }
            catch { /* unreadable settings: defaults */ }
            return new SimuLeanProjectSettings();
        }

        /// <summary>The settings of this project (main thread: reads <see cref="Application.dataPath"/>).</summary>
        public static SimuLeanProjectSettings Load() => Load(ResultsOutput.ProjectRoot);

        public void Save(string projectRoot) => File.WriteAllText(Path.Combine(projectRoot, RelativePath), JsonUtility.ToJson(this, true));

        public void Save() => Save(ResultsOutput.ProjectRoot);

        private static SimuLeanProjectSettings Sanitised(SimuLeanProjectSettings s)
        {
            s = s ?? new SimuLeanProjectSettings();
            if (string.IsNullOrWhiteSpace(s.resultsFolder)) s.resultsFolder = "SimuLeanResults";
            if (string.IsNullOrWhiteSpace(s.databasePath)) s.databasePath = DefaultDatabase;   // also an older file
            if (!(s.confidence > 0 && s.confidence < 1)) s.confidence = 0.95;   // also an older file without the field (0)
            if (s.shownPacks == null) s.shownPacks = new List<string>(SimuLeanPacks.DefaultShown);   // an older file
            return s;
        }
    }
}
