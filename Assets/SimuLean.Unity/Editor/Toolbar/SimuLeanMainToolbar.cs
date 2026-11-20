using System;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Two buttons of the main toolbar, next to Play: <b>Game / Scene</b> brings the other view to the
    /// front (from a maximized Game view too), and <b>Maximize</b> (per user) plays with the Game view maximized and
    /// restores it when Play stops — Unity keeps that option in the Game view's own menu, where it is hard to find.
    /// Their place can be changed from the toolbar's context menu, as any main toolbar element.
    /// </summary>
    [InitializeOnLoad]
    public static class SimuLeanMainToolbar
    {
        private const string ViewPath = "SimuLean/Scene or Game";
        private const string MaximizePath = "SimuLean/Play Maximized";
        private const string MaximizePref = "SimuLean.PlayMaximized";

        private static readonly Type GameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        private static bool gameInFront;
        private static bool maximizedByUs;

        static SimuLeanMainToolbar()
        {
            EditorWindow.windowFocusChanged += OnFocusChanged;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        /// <summary>Play with the Game view maximized (per user, off by default).</summary>
        public static bool PlayMaximized
        {
            get => EditorPrefs.GetBool(MaximizePref, false);
            set => EditorPrefs.SetBool(MaximizePref, value);
        }

        [MainToolbarElement(ViewPath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        private static MainToolbarElement ViewButton()
        {
            string target = gameInFront ? "Scene" : "Game";
            Texture2D icon = EditorGUIUtility.IconContent(gameInFront ? "UnityEditor.SceneView" : "UnityEditor.GameView").image as Texture2D;
            return new MainToolbarButton(new MainToolbarContent(target, icon, $"Show the {target} view"), SwitchView);
        }

        [MainToolbarElement(MaximizePath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        private static MainToolbarElement MaximizeToggle()
            => new MainToolbarToggle(new MainToolbarContent("Maximize", "Play with the Game view maximized"), PlayMaximized,
                                     on => PlayMaximized = on);

        /// <summary>Brings the Scene view to the front when the Game view is, else the Game view (opened if closed).</summary>
        public static void SwitchView()
        {
            EditorWindow game = GameView(create: false);
            if (gameInFront)
            {
                if (game != null && game.maximized) game.maximized = false;
                EditorWindow.FocusWindowIfItsOpen<SceneView>();
                gameInFront = false;
            }
            else
            {
                game = GameView(create: true);
                game.Focus();
                if (EditorApplication.isPlaying && PlayMaximized) { game.maximized = true; maximizedByUs = true; }
                gameInFront = true;
            }
            MainToolbar.Refresh(ViewPath);
        }

        private static EditorWindow GameView(bool create)
        {
            if (GameViewType == null) return null;
            if (create) return EditorWindow.GetWindow(GameViewType);
            UnityEngine.Object[] open = UnityEngine.Resources.FindObjectsOfTypeAll(GameViewType);
            return open.Length > 0 ? (EditorWindow)open[0] : null;
        }

        private static void OnFocusChanged()
        {
            EditorWindow focused = EditorWindow.focusedWindow;
            if (focused == null) return;
            bool game = GameViewType != null && GameViewType.IsInstanceOfType(focused);
            if (!game && !(focused is SceneView)) return;   // another window: the views in front are unchanged
            if (game == gameInFront) return;
            gameInFront = game;
            MainToolbar.Refresh(ViewPath);
        }

        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && PlayMaximized)
            {
                EditorWindow game = GameView(create: true);
                game.maximized = true;
                maximizedByUs = true;
                gameInFront = true;
                MainToolbar.Refresh(ViewPath);
            }
            else if (change == PlayModeStateChange.ExitingPlayMode && maximizedByUs)
            {
                EditorWindow game = GameView(create: false);
                if (game != null) game.maximized = false;
                maximizedByUs = false;
            }
        }
    }
}
