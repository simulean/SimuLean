using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A saved scene opened additively for a test, with every root under one <see cref="Root"/> (so the extractor sees
    /// only this scene); nothing is saved. When the scene is already open in the editor (often the user's, maybe with
    /// unsaved changes) a temporary copy of the saved file is opened instead, so the test never skips
    /// and never touches the open one. Dispose closes it (and deletes the copy).
    /// </summary>
    public sealed class IsolatedScene : IDisposable
    {
        private const string CopyPath = "Assets/__SimuLeanTestScene__.unity";

        public Scene Scene { get; }
        public GameObject Root { get; }
        private readonly bool copied;

        public IsolatedScene(string path)
        {
            Scene existing = SceneManager.GetSceneByPath(path);
            string open = path;
            if (existing.IsValid() && existing.isLoaded)
            {
                AssetDatabase.DeleteAsset(CopyPath);
                if (!AssetDatabase.CopyAsset(path, CopyPath)) throw new InvalidOperationException($"cannot copy {path}");
                open = CopyPath;
                copied = true;
            }
            Scene = EditorSceneManager.OpenScene(open, OpenSceneMode.Additive);
            Root = new GameObject("__extract_root__");
            SceneManager.MoveGameObjectToScene(Root, Scene);
            foreach (var r in Scene.GetRootGameObjects().Where(g => g != Root)) r.transform.SetParent(Root.transform, true);
        }

        public void Dispose()
        {
            if (Scene.IsValid()) EditorSceneManager.CloseScene(Scene, true);
            if (copied) AssetDatabase.DeleteAsset(CopyPath);
        }
    }
}
