using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Results;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The stored result sets in the Results tab: the results store of the project database
    /// (<see cref="ResultStore"/>, <see cref="ResultsOutput.Store"/>) listed newest first — open one, rename it, delete it
    /// (only by hand: nothing is deleted on its own), or tick several and compare them (<see cref="ResultSet.Compare"/>:
    /// one chart per metric with every set's scenarios side by side).
    /// </summary>
    public partial class SimuLeanWindow
    {
        private bool showStored;
        private List<StoredSet> storedSets;
        private string storedError;
        private readonly HashSet<int> storedPicked = new HashSet<int>();
        private int renamingId;
        private string renamingText = "";
        private Vector2 storedScroll;
        private volatile bool storedDirty = true;

        private void OnResultsStored(ResultSet set) { storedDirty = true; needsRepaint = true; }   // any thread

        private void RefreshStored()
        {
            storedDirty = false;
            try { storedSets = ResultsOutput.Store(ResultsOutput.ProjectRoot).List(); storedError = null; }
            catch (Exception e) { storedSets = new List<StoredSet>(); storedError = e.Message; }
            storedPicked.RemoveWhere(id => storedSets.All(s => s.Id != id));
        }

        private void DrawStored()
        {
            if (storedDirty || storedSets == null) RefreshStored();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Stored results ({storedSets.Count}) — {ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot)}", EditorStyles.miniBoldLabel);
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(60))) RefreshStored();
            EditorGUILayout.EndHorizontal();
            if (storedError != null) EditorGUILayout.HelpBox("The project database could not be read: " + storedError, MessageType.Warning);
            if (storedSets.Count == 0)
                EditorGUILayout.LabelField("None yet: experiments, studies and optimisations are kept here when they end (Settings ▸ Store results); " +
                                           "a single run with Save.", EditorStyles.wordWrappedMiniLabel);

            storedScroll = EditorGUILayout.BeginScrollView(storedScroll, GUILayout.MaxHeight(180));
            StoredSet toOpen = null, toDelete = null;
            foreach (var s in storedSets)
            {
                EditorGUILayout.BeginHorizontal();
                bool picked = storedPicked.Contains(s.Id);
                if (EditorGUILayout.Toggle(picked, GUILayout.Width(16)) != picked) { if (picked) storedPicked.Remove(s.Id); else storedPicked.Add(s.Id); }
                if (renamingId == s.Id)
                {
                    renamingText = EditorGUILayout.TextField(renamingText);
                    if (GUILayout.Button("OK", EditorStyles.miniButtonLeft, GUILayout.Width(32)) && renamingText.Trim().Length > 0)
                    {
                        ResultsOutput.Store(ResultsOutput.ProjectRoot).Rename(s.Id, renamingText.Trim());
                        if (resultsDoc?.Set != null && resultsDoc.Set.StoreId == s.Id) { resultsDoc.Set.Name = renamingText.Trim(); resultsDoc.Title = resultsDoc.Set.Name; }
                        renamingId = 0;
                        storedDirty = true;
                    }
                    if (GUILayout.Button("Cancel", EditorStyles.miniButtonRight, GUILayout.Width(50))) renamingId = 0;
                }
                else
                {
                    string counts = s.Candidates > 0 ? $"{s.Runs} runs · {s.Candidates} candidates" : $"{s.Scenarios} scen. · {s.Runs} runs";
                    GUILayout.Label(new GUIContent($"#{s.Id} {s.Name}", $"{s.Kind} · model {s.Model} · {s.Created:yyyy-MM-dd HH:mm:ss} · {counts}"));
                    GUILayout.Label($"{s.Kind} · {s.Model} · {s.Created:MM-dd HH:mm} · {counts}", EditorStyles.miniLabel, GUILayout.Width(250));
                    if (GUILayout.Button("Open", EditorStyles.miniButtonLeft, GUILayout.Width(42))) toOpen = s;
                    if (GUILayout.Button("Rename", EditorStyles.miniButtonMid, GUILayout.Width(54))) { renamingId = s.Id; renamingText = s.Name; }
                    if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(48))) toDelete = s;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = storedPicked.Count >= 2;
            if (GUILayout.Button(new GUIContent($"Compare ticked ({storedPicked.Count})", "The ticked sets as one: their scenarios side by side in every chart.")))
            {
                try
                {
                    var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
                    var sets = storedSets.Where(s => storedPicked.Contains(s.Id)).OrderBy(s => s.Id).Select(s => store.Load(s.Id)).ToList();
                    resultsDoc = Doc(ResultSet.Compare(sets));
                    followLatest = false;
                }
                catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", "Could not compare the sets: " + e.Message, "OK"); }
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (toOpen != null)
            {
                try { resultsDoc = Doc(ResultsOutput.Store(ResultsOutput.ProjectRoot).Load(toOpen.Id)); followLatest = false; }
                catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", "Could not open the set: " + e.Message, "OK"); }
            }
            if (toDelete != null && EditorUtility.DisplayDialog("SimuLean", $"Delete the stored results #{toDelete.Id} \"{toDelete.Name}\" ({toDelete.Runs} runs)? " +
                                                                       "This cannot be undone.", "Delete", "Cancel"))
            {
                ResultsOutput.Store(ResultsOutput.ProjectRoot).Delete(toDelete.Id);
                if (resultsDoc?.Set != null && resultsDoc.Set.StoreId == toDelete.Id) resultsDoc.Set.StoreId = 0;
                var latest = ResultsOutput.Last;
                if (latest != null && latest.StoreId == toDelete.Id) latest.StoreId = 0;
                storedDirty = true;
            }
        }
    }
}
