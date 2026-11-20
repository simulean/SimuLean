using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The Models tab: the project's models (<see cref="SimuLeanModels"/>: thumbnail, name, description, last
    /// modified, recent first) with New, Open, Duplicate, Rename, Card and Delete (to the trash, after a confirmation), and
    /// the examples, which open as a copy (the first time a new model, then "Open my copy" or "New copy"). Everything that
    /// opens a scene asks first about unsaved changes (Unity's save prompt).
    /// </summary>
    public partial class SimuLeanWindow
    {
        private enum ModelEdit { None, New, Duplicate, Rename }

        private string modelsSearch = "";
        private ModelEdit modelEdit;
        private SimuLeanModels.Model modelEditTarget;
        private string modelEditText = "";
        private List<SimuLeanModels.Model> modelList;
        private List<SimuLeanModels.Example> exampleList;
        private string modelsError;
        private Vector2 modelsScroll;
        private static GUIStyle modelName, modelNote;

        private void RefreshModels()
        {
            try { modelList = SimuLeanModels.List(); exampleList = SimuLeanModels.Examples(); modelsError = null; }
            catch (Exception e) { modelList = new List<SimuLeanModels.Model>(); exampleList = new List<SimuLeanModels.Example>(); modelsError = e.Message; }
        }

        private void OnProjectChanged() { modelList = null; Repaint(); }

        private void DrawModels()
        {
            if (modelList == null || exampleList == null) RefreshModels();
            if (modelName == null)
            {
                modelName = new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip };
                modelNote = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { clipping = TextClipping.Clip };
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("New model", "A new model in its own folder: a scene ready for modelling, saved at once, and its card."),
                                 EditorStyles.miniButton, GUILayout.Width(80)))
                StartModelEdit(ModelEdit.New, null, SimuLeanModels.FreeName("Model"));
            modelsSearch = EditorGUILayout.TextField(modelsSearch, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22))) { modelsSearch = ""; GUI.FocusControl(null); }
            if (GUILayout.Button(new GUIContent("↻", "Read the models folder again."), EditorStyles.miniButton, GUILayout.Width(22))) RefreshModels();
            EditorGUILayout.EndHorizontal();
            string folder;
            try { folder = SimuLeanModels.ModelsFolder; } catch (Exception e) { folder = null; modelsError = e.Message; }
            EditorGUILayout.LabelField(folder != null ? $"Models folder: {folder} (Settings ▸ Models)" : "Models folder: not usable", EditorStyles.miniLabel);
            if (modelsError != null) EditorGUILayout.HelpBox(modelsError, MessageType.Warning);
            if (modelEdit == ModelEdit.New) DrawModelNameField(null);

            modelsScroll = EditorGUILayout.BeginScrollView(modelsScroll);
            string active = EditorSceneManager.GetActiveScene().path;
            var mine = modelList.Where(m => ModelMatches(m.DisplayName, m.Name, m.Description)).ToList();
            GroupHeader("My models", "Flow", modelList.Count.ToString());
            if (modelList.Count == 0)
                EditorGUILayout.LabelField("None yet: make one with New model, or open an example (it opens as your own copy).", EditorStyles.wordWrappedMiniLabel);
            SimuLeanModels.Model act = null; string action = null;
            foreach (var m in mine)
            {
                string a = DrawModelRow(m, m.ScenePath == active);
                if (a != null) { act = m; action = a; }
            }

            GroupHeader("Examples", "Presets", "open as a copy");
            foreach (var group in exampleList.Where(e => ModelMatches(e.Name, e.Category, e.DisplayName)).GroupBy(e => e.Category))
            {
                EditorGUILayout.LabelField(group.Key, EditorStyles.miniBoldLabel);
                foreach (var e in group) DrawExampleRow(e);
            }
            EditorGUILayout.EndScrollView();

            if (action != null) RunModelAction(act, action);
        }

        private bool ModelMatches(string a, string b, string c)
            => string.IsNullOrEmpty(modelsSearch)
               || new[] { a, b, c }.Any(s => (s ?? "").IndexOf(modelsSearch, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>One model: thumbnail, name, description, date and its buttons; returns the action clicked, else null.</summary>
        private string DrawModelRow(SimuLeanModels.Model m, bool isOpen)
        {
            string clicked = null;
            var row = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            if (isOpen) EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.9f, 0.18f));
            var thumbRect = GUILayoutUtility.GetRect(96, 54, GUILayout.Width(96), GUILayout.Height(54));
            var thumb = AssetDatabase.LoadAssetAtPath<Texture2D>(m.ThumbnailPath);
            if (thumb != null) GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleAndCrop);
            else
            {
                EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));
                GUI.Label(thumbRect, "no picture\n(saved with the scene)", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
            }
            EditorGUILayout.BeginVertical();
            if (modelEdit == ModelEdit.Rename && modelEditTarget?.Folder == m.Folder) DrawModelNameField(m);
            else GUILayout.Label(new GUIContent(m.DisplayName + (isOpen ? "  (open)" : ""), m.ScenePath), modelName);
            if (!string.IsNullOrWhiteSpace(m.Description)) GUILayout.Label(m.Description, modelNote);
            string from = string.IsNullOrEmpty(m.CopiedFrom) ? "" : $" · copy of {System.IO.Path.GetFileNameWithoutExtension(m.CopiedFrom)}";
            GUILayout.Label($"modified {m.Modified:yyyy-MM-dd HH:mm}{from}", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Open", "Opens the model (asks first about unsaved changes)."), EditorStyles.miniButtonLeft, GUILayout.Width(74))) clicked = "open";
            if (GUILayout.Button(new GUIContent("Duplicate", "A copy of the whole model folder under a new name."), EditorStyles.miniButtonMid, GUILayout.Width(74))) clicked = "duplicate";
            if (GUILayout.Button(new GUIContent("Rename", "Renames its folder, scene and card."), EditorStyles.miniButtonMid, GUILayout.Width(74))) clicked = "rename";
            if (GUILayout.Button(new GUIContent("Card", "Selects the model card: description and the notes for agents (KPIs, parameters, assumptions)."), EditorStyles.miniButtonMid, GUILayout.Width(74))) clicked = "card";
            if (GUILayout.Button(new GUIContent("Delete", "Moves the model folder to the trash (after a confirmation)."), EditorStyles.miniButtonRight, GUILayout.Width(74))) clicked = "delete";
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (modelEdit == ModelEdit.Duplicate && modelEditTarget?.Folder == m.Folder) DrawModelNameField(m);
            return clicked;
        }

        private void DrawExampleRow(SimuLeanModels.Example e)
        {
            int copies = modelList.Count(m => m.CopiedFrom == e.ScenePath);
            var r = EditorGUILayout.GetControlRect();
            GUI.Label(new Rect(r.x + 10, r.y, r.width * 0.55f, r.height),
                      new GUIContent(e.DisplayName, string.IsNullOrWhiteSpace(e.Description) ? e.ScenePath : e.Description + "\n" + e.ScenePath));
            GUI.Label(new Rect(r.x + 10 + r.width * 0.55f, r.y, r.width * 0.45f - 90, r.height),
                      copies == 0 ? "" : copies == 1 ? "you have a copy" : $"you have {copies} copies", EditorStyles.miniLabel);
            if (GUI.Button(new Rect(r.xMax - 80, r.y, 80, r.height),
                           new GUIContent("Open", "Opens your copy of the example (made the first time): the example itself is never edited."), EditorStyles.miniButton))
                OpenExample(e);
            if (!string.IsNullOrWhiteSpace(e.Description))
                GUILayout.Label(e.Description, new GUIStyle(modelNote) { padding = new RectOffset(10, 0, 0, 2) });
        }

        private void StartModelEdit(ModelEdit edit, SimuLeanModels.Model target, string text)
        {
            modelEdit = edit;
            modelEditTarget = target;
            modelEditText = text;
            GUI.FocusControl(null);
        }

        /// <summary>The name field of New / Duplicate / Rename with its OK and Cancel.</summary>
        private void DrawModelNameField(SimuLeanModels.Model target)
        {
            EditorGUILayout.BeginHorizontal();
            string label = modelEdit == ModelEdit.New ? "New model" : modelEdit == ModelEdit.Duplicate ? "Copy's name" : "New name";
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(72));
            GUI.SetNextControlName("SimuLeanModelName");
            modelEditText = EditorGUILayout.TextField(modelEditText);
            bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "SimuLeanModelName";
            bool ok = GUILayout.Button("OK", EditorStyles.miniButtonLeft, GUILayout.Width(32)) || enter;
            if (GUILayout.Button("Cancel", EditorStyles.miniButtonRight, GUILayout.Width(50))) { modelEdit = ModelEdit.None; GUIUtility.ExitGUI(); }
            EditorGUILayout.EndHorizontal();
            if (!ok) return;
            var edit = modelEdit;
            string name = modelEditText;
            modelEdit = ModelEdit.None;
            // after this GUI pass: a new scene must not be opened in the middle of a layout
            EditorApplication.delayCall += () => ApplyModelEdit(edit, target, name);
            GUIUtility.ExitGUI();
        }

        private void ApplyModelEdit(ModelEdit edit, SimuLeanModels.Model target, string name)
        {
            try
            {
                switch (edit)
                {
                    case ModelEdit.New:
                        SimuLeanModels.CheckNewName(name);
                        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                        SimuLeanModels.Create(name, open: true);
                        break;
                    case ModelEdit.Duplicate:
                        SimuLeanModels.Duplicate(target, name);
                        break;
                    case ModelEdit.Rename:
                        SimuLeanModels.Rename(target, name);
                        break;
                }
            }
            catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", e.Message, "OK"); StartModelEdit(edit, target, name); }
            RefreshModels();
            Repaint();
        }

        private void RunModelAction(SimuLeanModels.Model m, string action)
        {
            switch (action)
            {
                case "duplicate": StartModelEdit(ModelEdit.Duplicate, m, SimuLeanModels.FreeName(m.Name)); return;
                case "rename": StartModelEdit(ModelEdit.Rename, m, m.Name); return;
                case "card":
                    if (m.Card != null) { Selection.activeObject = m.Card; EditorGUIUtility.PingObject(m.Card); }
                    else EditorUtility.DisplayDialog("SimuLean", $"'{m.Name}' has no model card ({m.CardPath}).", "OK");
                    return;
            }
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (action == "open")
                    {
                        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) SimuLeanModels.Open(m);
                    }
                    else if (action == "delete")
                    {
                        if (!EditorUtility.DisplayDialog("Delete model", $"Move the model '{m.DisplayName}' ({m.Folder}: its scene, card and data) to the trash?",
                                                         "Move to trash", "Cancel")) return;
                        if (EditorSceneManager.GetActiveScene().path == m.ScenePath || UnityEngine.SceneManagement.SceneManager.GetSceneByPath(m.ScenePath).IsValid())
                        {
                            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                            EditorSceneManager.OpenScene(SimuLeanTestRunner.NeutralScene, OpenSceneMode.Single);
                        }
                        SimuLeanModels.Delete(m);
                    }
                }
                catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", e.Message, "OK"); }
                RefreshModels();
                Repaint();
            };
        }

        /// <summary>An example opens as a copy: made the first time; then "Open my copy" or "New copy".</summary>
        private void OpenExample(SimuLeanModels.Example e)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var copies = SimuLeanModels.CopiesOf(e.ScenePath);
                    SimuLeanModels.Model mine = copies.FirstOrDefault();
                    bool newCopy = mine == null;
                    if (!newCopy)
                    {
                        int pick = EditorUtility.DisplayDialogComplex(e.Name,
                            $"You already have a copy of this example: '{mine.DisplayName}' ({mine.Folder}, modified {mine.Modified:yyyy-MM-dd HH:mm}). " +
                            "Open it, or make a new copy of the example?", "Open my copy", "Cancel", "New copy");
                        if (pick == 1) return;
                        newCopy = pick == 2;
                    }
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                    if (newCopy) SimuLeanModels.CopyExample(e.ScenePath, null, open: true);
                    else SimuLeanModels.Open(mine);
                }
                catch (Exception x) { EditorUtility.DisplayDialog("SimuLean", x.Message, "OK"); }
                RefreshModels();
                Repaint();
            };
        }
    }
}
