using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Welcome window shown on first editor startup.
    /// Introduces VRseBuilder Unity MCP and can be permanently dismissed.
    /// </summary>
    [InitializeOnLoad]
    public class MCPWelcomeWindow : EditorWindow
    {
        private const string HideKey = "UnityMCP_HideWelcome";
        private const string ShownSessionKey = "UnityMCP_WelcomeShownThisSession";

        private static readonly Vector2 WindowSize = new Vector2(520, 420);

        private Vector2 _scrollPosition;
        private GUIStyle _titleStyle;
        private GUIStyle _headingStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _accentButtonStyle;
        private bool _stylesReady;

        private static readonly Color SubtleGrey = new Color(0.6f, 0.6f, 0.6f);

        static MCPWelcomeWindow()
        {
            EditorApplication.delayCall += ShowOnStartup;
        }

        private static void ShowOnStartup()
        {
            // Don't show if user opted out permanently
            if (EditorPrefs.GetBool(HideKey, false))
                return;

            // Only show once per editor session (survives domain reloads)
            if (SessionState.GetBool(ShownSessionKey, false))
                return;

            SessionState.SetBool(ShownSessionKey, true);

            // Small delay so the editor is fully loaded
            EditorApplication.delayCall += () =>
            {
                var window = GetWindow<MCPWelcomeWindow>(true, "Welcome to VRseBuilder Unity MCP", true);
                window.minSize = WindowSize;
                window.maxSize = new Vector2(WindowSize.x + 60, WindowSize.y + 120);
                window.ShowUtility();
                window.CenterOnScreen();
            };
        }

        private void CenterOnScreen()
        {
            var mainWindow = EditorGUIUtility.GetMainWindowPosition();
            var pos = position;
            pos.x = mainWindow.x + (mainWindow.width - pos.width) * 0.5f;
            pos.y = mainWindow.y + (mainWindow.height - pos.height) * 0.5f;
            position = pos;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;

            _titleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                richText = true,
            };

            _headingStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                richText = true,
            };

            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                wordWrap = true,
                richText = true,
            };

            _accentButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                fixedHeight = 36,
                richText = true,
            };

            _stylesReady = true;
        }

        private void OnGUI()
        {
            InitStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.Space(12);

            // ─── Title ───
            EditorGUILayout.LabelField("VRseBuilder Unity MCP", _titleStyle, GUILayout.Height(32));
            EditorGUILayout.Space(2);

            var prevColor = GUI.contentColor;
            GUI.contentColor = SubtleGrey;
            EditorGUILayout.LabelField("by AutoVRse", new GUIStyle(_bodyStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
            });
            GUI.contentColor = prevColor;

            EditorGUILayout.Space(12);
            DrawSeparator();
            EditorGUILayout.Space(8);

            // ─── About ───
            EditorGUILayout.LabelField("What is this?", _headingStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "VRseBuilder Unity MCP bridges AI assistants directly into the Unity Editor — " +
                "giving them access to tools that read, modify, and interact with your scenes, " +
                "assets, scripts, and more.",
                _bodyStyle);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                "Open <b>Window → VRseBuilder Unity MCP</b> to view the dashboard and confirm the " +
                "bridge is running.",
                _bodyStyle);

            EditorGUILayout.Space(16);
            DrawSeparator();
            EditorGUILayout.Space(8);

            // ─── Bottom buttons ───
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Don't show this again", EditorStyles.miniButton, GUILayout.Height(24)))
            {
                EditorPrefs.SetBool(HideKey, true);
                Close();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("  Got it, let's go!  ", _accentButtonStyle))
            {
                Close();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            EditorGUILayout.EndScrollView();
        }

        private void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.x += 20;
            rect.width -= 40;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        }
    }
}
