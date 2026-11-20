using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Model management: the project's models and the examples, shared by the SimuLean window's Models tab and
    /// the <c>simulean_models</c> / <c>simulean_card</c> commands, so people and agents get the same behaviour.
    /// <para>A <b>model</b> is a folder <c>&lt;modelsFolder&gt;/&lt;Name&gt;/</c> (project setting
    /// <see cref="SimuLeanProjectSettings.modelsFolder"/>, default <c>Assets/SimuLeanModels</c>) holding its scene
    /// <c>&lt;Name&gt;.unity</c>, its card (<see cref="SimuLeanModelCard"/>, <c>ModelCard.asset</c>), its thumbnail
    /// (<c>Thumbnail.png</c>, captured when the scene is saved) and its own data; its exported results go to
    /// <c>&lt;resultsFolder&gt;/&lt;Name&gt;/</c> (<see cref="ResultsOutput.Folder(string, string)"/>).</para>
    /// <para>An <b>example</b> is a scene under <c>Assets/Scenes</c> (not the neutral scene). It is never opened itself:
    /// <see cref="CopyExample"/> copies it — with the assets of its own folders (<c>&lt;Example&gt;/</c>,
    /// <c>&lt;Example&gt;_*/</c> beside it), references remapped to the copies — into a new model whose card names it
    /// (<c>copiedFrom</c>).</para>
    /// Nothing here prompts: the callers check for unsaved scenes first (the window with Unity's save prompt, the commands
    /// by refusing).
    /// </summary>
    [InitializeOnLoad]
    public static class SimuLeanModels
    {
        public const string ExamplesFolder = "Assets/Scenes";
        public const int ThumbnailWidth = 320, ThumbnailHeight = 180;

        /// <summary>Tests only: another models folder (under Assets), so test runs never touch the project's models or its settings.</summary>
        public static string ModelsFolderOverride { get; set; }

        static SimuLeanModels()
        {
            if (Application.isBatchMode) return;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        // ── what there is ────────────────────────────────────────────────────

        /// <summary>One of the project's models.</summary>
        public sealed class Model
        {
            public string Name, Folder, ScenePath;
            public SimuLeanModelCard Card;
            public DateTime Modified;
            public string DisplayName => Card != null && !string.IsNullOrWhiteSpace(Card.displayName) ? Card.displayName : Name;
            public string Description => Card != null ? Card.description ?? "" : "";
            public string CopiedFrom => Card != null ? Card.copiedFrom ?? "" : "";
            public string ThumbnailPath => Folder + "/" + SimuLeanModelCard.ThumbnailName;
            public string CardPath => Folder + "/" + SimuLeanModelCard.FileName;
        }

        /// <summary>One example scene of the repository, with its card when it has one (<see cref="ExampleCardPath"/>).</summary>
        public sealed class Example
        {
            public string Name, ScenePath, Category;
            public SimuLeanModelCard Card;
            public string DisplayName => Card != null && !string.IsNullOrWhiteSpace(Card.displayName) ? Card.displayName : Name;
            public string Description => Card != null ? Card.description ?? "" : "";
            public string CardPath => ExampleCardPath(ScenePath);
        }

        /// <summary>The models folder (an asset path under <c>Assets</c>).</summary>
        public static string ModelsFolder
        {
            get
            {
                string f = (ModelsFolderOverride ?? SimuLeanProjectSettings.Load().modelsFolder ?? "").Trim().Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(f)) f = SimuLeanProjectSettings.DefaultModelsFolder;
                if (!f.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new InvalidOperationException($"The models folder '{f}' is not a folder under Assets: set one such as " +
                                                        $"'{SimuLeanProjectSettings.DefaultModelsFolder}' (SimuLean window ▸ Settings ▸ Models).");
                return f;
            }
        }

        /// <summary>The project's models, recently modified first.</summary>
        public static List<Model> List()
        {
            var list = new List<Model>();
            string root = ModelsFolder;
            if (!AssetDatabase.IsValidFolder(root)) return list;
            foreach (string folder in AssetDatabase.GetSubFolders(root))
            {
                var m = ModelAt(folder);
                if (m != null) list.Add(m);
            }
            return list.OrderByDescending(m => m.Modified).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The model of <paramref name="folder"/> (one holding <c>&lt;its name&gt;.unity</c>), else null.</summary>
        public static Model ModelAt(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            string name = folder.Substring(folder.LastIndexOf('/') + 1);
            string scene = folder + "/" + name + ".unity";
            if (!File.Exists(Full(scene))) return null;
            var card = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(folder + "/" + SimuLeanModelCard.FileName);
            var modified = card != null ? card.ModifiedTime : DateTime.MinValue;
            if (modified == DateTime.MinValue) modified = File.GetLastWriteTime(Full(scene));
            return new Model { Name = name, Folder = folder, ScenePath = scene, Card = card, Modified = modified };
        }

        /// <summary>The model named <paramref name="name"/> (folder name or display name, any case), else null.</summary>
        public static Model Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            var all = List();
            return all.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(m => string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The model named <paramref name="name"/>, or an error listing the models.</summary>
        public static Model Get(string name)
            => Find(name) ?? throw new ArgumentException($"No model '{name}' in {ModelsFolder} (models: " +
                                                         $"{(List().Count == 0 ? "none" : string.Join(", ", List().Select(m => m.Name)))}).");

        /// <summary>The model whose scene is <paramref name="scenePath"/>, else null.</summary>
        public static Model ModelOfScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return null;
            var m = ModelAt(Path.GetDirectoryName(scenePath).Replace('\\', '/'));
            return m != null && m.ScenePath == scenePath ? m : null;
        }

        /// <summary>The card of the scene <paramref name="scenePath"/>: a model's (its folder's <c>ModelCard.asset</c>), else an
        /// example's (<see cref="ExampleCardPath"/>), else null.</summary>
        public static SimuLeanModelCard CardOf(string scenePath)
            => string.IsNullOrEmpty(scenePath) ? null
               : ModelCardOf(scenePath) ?? AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(ExampleCardPath(scenePath));

        /// <summary>The card of an example scene: <c>ModelCard.asset</c> in the example's own folder
        /// <c>&lt;Example&gt;/</c> beside its scene — examples share their folder, so the card cannot sit beside the scene. A
        /// copy of the example takes its card's text (<see cref="CopyExample"/>).</summary>
        public static string ExampleCardPath(string scenePath)
            => Path.GetDirectoryName(scenePath).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(scenePath) + "/" + SimuLeanModelCard.FileName;

        // A model's own card, beside its scene (the save hook and the thumbnail look only here: saving an example scene
        // leaves its folder and card alone).
        private static SimuLeanModelCard ModelCardOf(string scenePath)
            => string.IsNullOrEmpty(scenePath) ? null
               : AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(Path.GetDirectoryName(scenePath).Replace('\\', '/') + "/" + SimuLeanModelCard.FileName);

        /// <summary>The example scenes (under <see cref="ExamplesFolder"/>, the neutral scene left out), by category then name.</summary>
        public static List<Example> Examples()
        {
            var list = new List<Example>();
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { ExamplesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == SimuLeanTestRunner.NeutralScene || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                string dir = Path.GetDirectoryName(path).Replace('\\', '/');
                string category = dir == ExamplesFolder ? "Basics" : Humanise(dir.Substring(ExamplesFolder.Length + 1).Replace('/', ' '));
                list.Add(new Example { Name = Path.GetFileNameWithoutExtension(path), ScenePath = path, Category = category,
                                       Card = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(ExampleCardPath(path)) });
            }
            return list.OrderBy(e => e.Category == "Basics" ? 0 : 1).ThenBy(e => e.Category).ThenBy(e => e.Name).ToList();
        }

        /// <summary>The models copied from the example <paramref name="examplePath"/>, recent first.</summary>
        public static List<Model> CopiesOf(string examplePath) => List().Where(m => m.CopiedFrom == examplePath).ToList();

        // ── names ────────────────────────────────────────────────────────────

        private static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 _\-]*$");

        /// <summary>Checks <paramref name="name"/> as a new model's name (letters, digits, spaces, _ and -; not taken);
        /// returns it trimmed.</summary>
        public static string CheckNewName(string name, Model renaming = null)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("A model needs a name.");
            if (name.Length > 64) throw new ArgumentException($"'{name}' is too long (at most 64 characters).");
            if (!NamePattern.IsMatch(name))
                throw new ArgumentException($"'{name}' is not a valid model name: use letters, digits, spaces, '_' and '-', starting with a letter or digit.");
            // A folder of that name is taken when it holds a model; a folder without one (data put there first) is used by a
            // new model as it is. A rename moves the folder, so any folder of that name is taken for it.
            string existing = Directory.Exists(Full(ModelsFolder))
                ? Directory.GetDirectories(Full(ModelsFolder)).Select(Path.GetFileName)
                           .FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existing == null) return name;
            string existingFolder = ModelsFolder + "/" + existing;
            if (renaming != null)
            {
                if (existingFolder != renaming.Folder)
                    throw new ArgumentException($"There is already a folder '{existing}' (a free name: '{FreeName(name)}').");
                return name;
            }
            if (HoldsModel(existingFolder))
                throw new ArgumentException($"There is already a model '{existing}' (a free name: '{FreeName(name)}').");
            if (!string.Equals(existing, name, StringComparison.Ordinal))
                throw new ArgumentException($"There is a folder '{existing}' in {ModelsFolder} that differs from '{name}' only in case: use '{existing}' or another name.");
            return name;
        }

        /// <summary><paramref name="baseName"/> if no model has it, else the first free numbered name (<c>Name_2</c>, <c>Name_3</c>…).</summary>
        public static string FreeName(string baseName)
        {
            baseName = Regex.Replace((baseName ?? "").Trim(), @"[^A-Za-z0-9 _\-]", "_");
            if (baseName.Length == 0) baseName = "Model";
            var taken = new HashSet<string>(AssetDatabase.IsValidFolder(ModelsFolder)
                ? AssetDatabase.GetSubFolders(ModelsFolder).Select(f => f.Substring(f.LastIndexOf('/') + 1)) : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
                if (!taken.Contains($"{baseName}_{i}")) return $"{baseName}_{i}";
        }

        // ── create / copy / rename / delete ──────────────────────────────────

        /// <summary>
        /// A new model: its folder, a scene set up as a new scene is (<see cref="SimuLeanSceneSetup.SetUpScene"/>: the clock with
        /// its views, the camera, the floor) saved as <c>&lt;Name&gt;.unity</c>, and its card. With <paramref name="open"/> the
        /// scene replaces the open ones (the caller has dealt with unsaved changes); without, it is made beside them and
        /// closed (needs no untitled scene open).
        /// </summary>
        /// <param name="configure">Applied to the new scene after its set-up and before it is saved (the clock's time unit
        /// and horizon of <c>simulean_models action=new</c>).</param>
        public static Model Create(string name, bool open = true, string description = "", Action<Scene> configure = null)
        {
            name = CheckNewName(name);
            if (!open && AnyUntitledScene())
                throw new InvalidOperationException("An untitled scene is open: save it first (a new scene cannot be made beside it).");
            string folder = MakeModelFolder(name);
            string scenePath = folder + "/" + name + ".unity";
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, open ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                SimuLeanSceneSetup.SetUpScene(scene);
                configure?.Invoke(scene);
                if (!EditorSceneManager.SaveScene(scene, scenePath))
                    throw new InvalidOperationException($"The scene of '{name}' could not be saved to {scenePath}.");
                NewCard(folder, name, scenePath, "", description);
                if (open) CaptureThumbnailLater(scenePath);
            }
            finally
            {
                if (!open && scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            }
            return ModelAt(folder);
        }

        /// <summary>
        /// A copy of the example <paramref name="examplePath"/> as a new model (<paramref name="name"/>, by default the
        /// example's name or the first free numbered one): the scene and the assets of the example's own folders
        /// (<c>&lt;Example&gt;/</c>, <c>&lt;Example&gt;_*/</c> beside it), the copies' references to each other remapped, and
        /// a card whose <c>copiedFrom</c> is the example. Opened with <paramref name="open"/>.
        /// </summary>
        public static Model CopyExample(string examplePath, string name = null, bool open = true)
        {
            if (string.IsNullOrEmpty(examplePath) || !examplePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || !File.Exists(Full(examplePath)))
                throw new ArgumentException($"'{examplePath}' is not a scene of the project.");
            string exName = Path.GetFileNameWithoutExtension(examplePath);
            name = CheckNewName(string.IsNullOrWhiteSpace(name) ? FreeName(exName) : name);
            string folder = MakeModelFolder(name);
            string scenePath = folder + "/" + name + ".unity";
            var pairs = new List<(string from, string to)> { (examplePath, scenePath) };
            if (!AssetDatabase.CopyAsset(examplePath, scenePath)) throw new InvalidOperationException($"{examplePath} could not be copied to {scenePath}.");
            string dir = Path.GetDirectoryName(examplePath).Replace('\\', '/');
            foreach (string own in AssetDatabase.GetSubFolders(dir))
            {
                string sub = own.Substring(own.LastIndexOf('/') + 1);
                if (sub != exName && !sub.StartsWith(exName + "_", StringComparison.Ordinal)) continue;
                string target = folder + "/" + sub;
                if (!AssetDatabase.CopyAsset(own, target)) throw new InvalidOperationException($"{own} could not be copied to {target}.");
                pairs.AddRange(FilesIn(own).Select(f => (f, target + f.Substring(own.Length))));
            }
            RemapReferences(pairs);
            // the example's card (in its own folder, copied with it) becomes the model's card, beside the scene
            var exampleCard = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(ExampleCardPath(examplePath));
            string copiedDir = folder + "/" + exName, copiedCard = copiedDir + "/" + SimuLeanModelCard.FileName;
            if (AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(copiedCard) != null)
            {
                AssetDatabase.DeleteAsset(copiedCard);
                if (FilesIn(copiedDir).Count == 0 && AssetDatabase.GetSubFolders(copiedDir).Length == 0) AssetDatabase.DeleteAsset(copiedDir);
            }
            var card = NewCard(folder, name, scenePath, examplePath, $"A copy of the example {exName}.");
            if (exampleCard != null)
            {
                foreach (string f in new[] { "description", "represents", "kpis", "parameters", "assumptions" })
                    card.Set(f, exampleCard.Get(f));
                SaveCard(card);
            }
            if (open)
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                CaptureThumbnailLater(scenePath);
            }
            return ModelAt(folder);
        }

        /// <summary>A copy of the model <paramref name="source"/> named <paramref name="name"/> (default: the first free
        /// numbered name): its whole folder, the scene renamed, references inside it remapped to the copies, a new card date.</summary>
        public static Model Duplicate(Model source, string name = null, bool open = false)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            name = CheckNewName(string.IsNullOrWhiteSpace(name) ? FreeName(source.Name) : name);
            EnsureFolder(ModelsFolder);
            string folder = ModelsFolder + "/" + name;
            if (!AssetDatabase.CopyAsset(source.Folder, folder)) throw new InvalidOperationException($"{source.Folder} could not be copied to {folder}.");
            RemapReferences(FilesIn(source.Folder).Select(f => (f, folder + f.Substring(source.Folder.Length))).ToList());
            string copiedScene = folder + "/" + source.Name + ".unity", scenePath = folder + "/" + name + ".unity";
            string error = AssetDatabase.RenameAsset(copiedScene, name);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"The copied scene could not be renamed: {error}");
            var card = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(folder + "/" + SimuLeanModelCard.FileName);
            if (card == null) NewCard(folder, name, scenePath, source.CopiedFrom, source.Description);
            else
            {
                string now = DateTime.Now.ToString(SimuLeanModelCard.DateFormat);
                card.displayName = name;
                card.scenePath = scenePath;
                card.created = now;
                card.modified = now;
                SaveCard(card);
            }
            if (open) EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            return ModelAt(folder);
        }

        /// <summary>Renames <paramref name="model"/> (its folder, its scene, its card's name when it was the model's name).
        /// Its scene may be open, not with unsaved changes.</summary>
        public static Model Rename(Model model, string name)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            name = CheckNewName(name, model);
            if (name == model.Name) return model;
            var open = SceneManager.GetSceneByPath(model.ScenePath);
            if (open.IsValid() && open.isDirty) throw new InvalidOperationException($"'{model.Name}' has unsaved changes: save it first.");
            string error = AssetDatabase.RenameAsset(model.ScenePath, name);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"The scene could not be renamed: {error}");
            string folder = ModelsFolder + "/" + name;
            if (string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase))
            {   // a change of case only: through a temporary name (the file system ignores case)
                string temp = ModelsFolder + "/" + name + "__renaming";
                error = AssetDatabase.MoveAsset(model.Folder, temp);
                if (string.IsNullOrEmpty(error)) error = AssetDatabase.MoveAsset(temp, folder);
            }
            else error = AssetDatabase.MoveAsset(model.Folder, folder);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"The folder could not be renamed: {error}");
            var card = AssetDatabase.LoadAssetAtPath<SimuLeanModelCard>(folder + "/" + SimuLeanModelCard.FileName);
            if (card != null)
            {
                if (string.IsNullOrWhiteSpace(card.displayName) || card.displayName == model.Name) card.displayName = name;
                card.scenePath = folder + "/" + name + ".unity";
                SaveCard(card);
            }
            return ModelAt(folder);
        }

        /// <summary>Moves <paramref name="model"/>'s folder to the operating system's trash (never a hard delete). Its scene
        /// must not be open.</summary>
        public static void Delete(Model model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (SceneManager.GetSceneByPath(model.ScenePath).IsValid())
                throw new InvalidOperationException($"'{model.Name}' is open: open another scene first.");
            if (!AssetDatabase.MoveAssetToTrash(model.Folder))
                throw new InvalidOperationException($"{model.Folder} could not be moved to the trash.");
        }

        /// <summary>Opens <paramref name="model"/>'s scene alone (the caller has dealt with unsaved changes).</summary>
        public static void Open(Model model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            EditorSceneManager.OpenScene(model.ScenePath, OpenSceneMode.Single);
        }

        // ── the card ─────────────────────────────────────────────────────────

        /// <summary>The card fields <c>simulean_card</c> may set (the others are kept by SimuLean).</summary>
        public static readonly string[] WritableFields = { "displayName", "description", "author", "represents", "kpis", "parameters", "assumptions" };

        /// <summary>Sets a card field by name (<see cref="WritableFields"/>); saved at once.</summary>
        public static void SetCardField(SimuLeanModelCard card, string field, string value)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            string f = WritableFields.FirstOrDefault(w => string.Equals(w, (field ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (f == null)
            {
                bool known = SimuLeanModelCard.Fields.Any(x => string.Equals(x, (field ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                throw new ArgumentException(known
                    ? $"field: '{field}' is kept by SimuLean (writable: {string.Join(", ", WritableFields)})."
                    : $"field: the card has no field '{field}' (writable: {string.Join(", ", WritableFields)}).");
            }
            Undo.RecordObject(card, "Model card " + f);
            card.Set(f, value);
            SaveCard(card);
        }

        // ── thumbnails and dates (on save) ───────────────────────────────────

        private static void OnSceneSaved(Scene scene)
        {
            var card = ModelCardOf(scene.path);
            if (card == null) return;
            card.modified = DateTime.Now.ToString(SimuLeanModelCard.DateFormat);
            if (card.scenePath != scene.path) card.scenePath = scene.path;
            SaveCard(card);
            CaptureThumbnailLater(scene.path);
        }

        /// <summary>After this editor tick: the thumbnail of the open model scene <paramref name="scenePath"/> (when it is still
        /// the active scene, out of Play).</summary>
        private static void CaptureThumbnailLater(string scenePath)
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var s = SceneManager.GetActiveScene();
                    if (s.path == scenePath && s.isLoaded && !EditorApplication.isPlayingOrWillChangePlaymode && ModelCardOf(scenePath) != null)
                        CaptureThumbnail(s);
                }
                catch (Exception e) { Debug.LogWarning($"[SimuLean] The model's thumbnail could not be captured: {e.Message}"); }
            };
        }

        /// <summary>Renders <c>Thumbnail.png</c> beside <paramref name="scene"/> (its model framed and drawn as
        /// <c>simulean_capture</c> does; the whole scene when it has no element). Returns its asset path, or null when there
        /// is nothing to frame.</summary>
        public static string CaptureThumbnail(Scene scene)
        {
            TeamMemberPreviews.EnsureAll();
            var content = ModelLayout.OfModel(scene);
            if (content.Elements == 0)
                content = ModelLayout.OfObjects(scene.GetRootGameObjects().Where(r => r.GetComponentInChildren<Renderer>() != null));
            if (content.IsEmpty) return null;
            string path = Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + SimuLeanModelCard.ThumbnailName;
            var pose = SimuLeanCommands.Frame(content, 40f, 30f, 0f, 40f, (float)ThumbnailWidth / ThumbnailHeight);
            SimuLeanCommands.RenderPng(pose, 40f, ThumbnailWidth, ThumbnailHeight, Full(path),
                                       ModelLayout.Groups(SimuLeanCommands.DefaultConnections), content.Markers);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>The new model's folder: created, or — when a folder of that name already exists without a model (files
        /// put there before the model, e.g. its data) — that folder, imported if Unity has not seen it yet.</summary>
        private static string MakeModelFolder(string name)
        {
            EnsureFolder(ModelsFolder);
            string folder = ModelsFolder + "/" + name;
            if (Directory.Exists(Full(folder)))
            {
                if (HoldsModel(folder)) throw new InvalidOperationException($"There is already a model in {folder}.");
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.ImportAsset(folder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
                if (!AssetDatabase.IsValidFolder(folder)) throw new InvalidOperationException($"The folder {folder} exists but Unity could not import it.");
                return folder;
            }
            string made = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(ModelsFolder, name));
            if (made != folder)
            {
                // CreateFolder makes 'Name 1' when the name is in use in a way it does not report: never leave that behind
                if (!string.IsNullOrEmpty(made) && made != ModelsFolder) AssetDatabase.DeleteAsset(made);
                throw new InvalidOperationException($"The folder {folder} could not be created (Unity made '{made}' instead).");
            }
            return folder;
        }

        /// <summary>Whether <paramref name="folder"/> holds a model: a scene or a model card.</summary>
        private static bool HoldsModel(string folder)
            => Directory.Exists(Full(folder))
               && (Directory.GetFiles(Full(folder), "*.unity").Length > 0 || File.Exists(Full(folder + "/" + SimuLeanModelCard.FileName)));

        /// <summary>Creates the asset folder <paramref name="folder"/> (and its parents) when missing.</summary>
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = folder.Substring(0, folder.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(folder.LastIndexOf('/') + 1));
        }

        private static SimuLeanModelCard NewCard(string folder, string name, string scenePath, string copiedFrom, string description)
        {
            var card = ScriptableObject.CreateInstance<SimuLeanModelCard>();
            string now = DateTime.Now.ToString(SimuLeanModelCard.DateFormat);
            card.displayName = name;
            card.description = description ?? "";
            card.author = Author();
            card.scenePath = scenePath;
            card.copiedFrom = copiedFrom ?? "";
            card.created = now;
            card.modified = now;
            AssetDatabase.CreateAsset(card, folder + "/" + SimuLeanModelCard.FileName);
            AssetDatabase.SaveAssetIfDirty(card);
            return card;
        }

        private static void SaveCard(SimuLeanModelCard card)
        {
            EditorUtility.SetDirty(card);
            AssetDatabase.SaveAssetIfDirty(card);
        }

        private static string Author()
        {
            string user = CloudProjectSettings.userName;
            return string.IsNullOrWhiteSpace(user) || user == "anonymous" ? Environment.UserName : user;
        }

        private static bool AnyUntitledScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path)) return true;
            return false;
        }

        /// <summary>The asset files under <paramref name="folder"/> (subfolders included; no folders).</summary>
        private static List<string> FilesIn(string folder)
            => AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                            .Where(p => !AssetDatabase.IsValidFolder(p)).ToList();

        /// <summary>
        /// Makes the copies (<c>to</c>) reference each other instead of the originals (<c>from</c>): every GUID of an original
        /// is replaced by its copy's in the copies that are text assets (the project serialises as text).
        /// </summary>
        private static void RemapReferences(List<(string from, string to)> pairs)
        {
            var map = new Dictionary<string, string>();
            foreach (var (from, to) in pairs)
            {
                string a = AssetDatabase.AssetPathToGUID(from), b = AssetDatabase.AssetPathToGUID(to);
                if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b) map[a] = b;
            }
            if (map.Count == 0) return;
            var guid = new Regex(@"guid: ([0-9a-f]{32})");
            foreach (var (_, to) in pairs)
            {
                string file = Full(to);
                if (!File.Exists(file)) continue;
                string text;
                using (var reader = new StreamReader(file))
                {
                    char[] head = new char[5];
                    if (reader.Read(head, 0, 5) < 5 || new string(head) != "%YAML") continue;   // binary assets keep theirs
                }
                text = File.ReadAllText(file);
                string remapped = guid.Replace(text, m => map.TryGetValue(m.Groups[1].Value, out var b) ? "guid: " + b : m.Value);
                if (remapped == text) continue;
                File.WriteAllText(file, remapped);
                AssetDatabase.ImportAsset(to, ImportAssetOptions.ForceUpdate);
            }
        }

        private static string Full(string assetPath) => Path.Combine(ResultsOutput.ProjectRoot, assetPath);

        private static string Humanise(string s) => Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
