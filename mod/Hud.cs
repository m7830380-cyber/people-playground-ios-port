using System.Collections.Generic;
using PPGShim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PPGiOS
{
    // On-screen buttons for actions that need a keyboard on PC. They press the key the game's own
    // control scheme (ControlScheme.json) binds to the action, so rebinding in the game's menu still works.
    // Shown only in the sandbox / map editor and never over the pause/settings menus or dialogs.
    public class Hud : MonoBehaviour
    {
        enum Kind { Tap, Hold, Modifier, RightClick, Hide }

        sealed class Btn
        {
            public string Label;
            public string Action;      // game InputSystem action id
            public KeyCode Key;        // fixed key (modifiers)
            public Kind Kind;
            public Rect Screen;        // pixels, origin bottom-left
            public int Finger = -1;
            public bool On;            // modifier toggled / right-click mode
            public float FlashUntil;
            public KeyCode HeldKey, HeldSecondary;
        }

        static Hud s_Instance;
        static readonly List<Btn> s_Right = new List<Btn>();
        static readonly List<Btn> s_Bottom = new List<Btn>();
        static Btn s_HideBtn;
        static readonly List<Btn> s_All = new List<Btn>();
        public static bool Hidden;
        static bool s_Visible;
        static string s_Flash;
        static float s_FlashUntil;
        GUIStyle m_Label, m_FlashStyle;
        Texture2D m_Tex;

        // Unbound actions get a key nobody types, so a button can still trigger them.
        static readonly Dictionary<string, KeyCode> s_FallbackKeys = new Dictionary<string, KeyCode>();
        static KeyCode s_NextFallback = KeyCode.F13;

        void Awake()
        {
            s_Instance = this;
            Btn B(string label, string action, Kind kind = Kind.Tap, KeyCode key = KeyCode.None)
            {
                var b = new Btn { Label = label, Action = action, Kind = kind, Key = key };
                s_All.Add(b);
                return b;
            }
            s_Right.Add(B("SPAWN\nMENU", "toybox"));
            s_Right.Add(B("ACTIVATE", "activateDirect"));
            s_Right.Add(B("ROTATE\n↺", "left", Kind.Hold));
            s_Right.Add(B("ROTATE\n↻", "right", Kind.Hold));
            s_Right.Add(B("FREEZE", "freezeSelection"));
            s_Right.Add(B("DELETE", "delete"));
            s_Right.Add(B("UNDO", "undo"));

            s_Bottom.Add(B("MENU", "pause"));
            s_Bottom.Add(B("PAUSE\nTIME", "time"));
            s_Bottom.Add(B("SLOW\nMO", "slowmo"));
            s_Bottom.Add(B("COPY", "copy"));
            s_Bottom.Add(B("PASTE", "paste"));
            s_Bottom.Add(B("SHIFT", null, Kind.Modifier, KeyCode.LeftShift));
            s_Bottom.Add(B("CTRL", null, Kind.Modifier, KeyCode.LeftControl));
            s_Bottom.Add(B("ALT", null, Kind.Modifier, KeyCode.LeftAlt));
            s_Bottom.Add(B("RIGHT\nCLICK", null, Kind.RightClick));
            s_HideBtn = B("HIDE", null, Kind.Hide);
        }

        public static bool InGame
        {
            get
            {
                if (Global.main == null) return false;
                string scene = SceneManager.GetActiveScene().name;
                if (scene != "Main" && scene != "Map Editor") return false;
                if (Global.main.GetPausedMenu() || DialogBox.IsAnyDialogboxOpen) return false;
                return true;
            }
        }

        void Update()
        {
            bool vis = VInput.Virtual && InGame;
            if (vis != s_Visible)
            {
                s_Visible = vis;
                if (!vis) ReleaseHolds();
            }
            Layout();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) TouchInput.ReleaseAll();
            Log.Info(paused ? "app paused (background)" : "app resumed");
        }

        static void Layout()
        {
            Rect safe = UnityEngine.Screen.safeArea;
            float h = UnityEngine.Screen.height;
            float dpi = UnityEngine.Screen.dpi > 0 ? UnityEngine.Screen.dpi : 326f;
            float margin = Mathf.Max(8f, dpi * 0.03f);
            float size = Mathf.Min(dpi * 0.34f, (safe.height - 2 * margin) / 8.4f);
            float gap = size * 0.12f;

            // right column, top to bottom, ending above the bottom row
            float x = safe.xMax - margin - size;
            float y = safe.yMax - margin - size;
            foreach (var b in s_Right)
            {
                b.Screen = new Rect(x, y, size, size);
                y -= size + gap;
            }
            // bottom row, centred, plus HIDE at the right end
            float bh = size * 0.8f, bw = size * 1.05f;
            float total = s_Bottom.Count * (bw + gap) - gap;
            float bx = Mathf.Max(safe.xMin + margin, safe.center.x - total * 0.5f);
            float by = safe.yMin + margin * 0.5f;
            foreach (var b in s_Bottom)
            {
                b.Screen = new Rect(bx, by, bw, bh);
                bx += bw + gap;
            }
            s_HideBtn.Screen = new Rect(safe.xMax - margin - size, by, size, bh);
        }

        static IEnumerable<Btn> ActiveButtons()
        {
            if (!s_Visible) yield break;
            yield return s_HideBtn;
            if (Hidden) yield break;
            foreach (var b in s_Right) yield return b;
            foreach (var b in s_Bottom) yield return b;
        }

        // ---- touch routing (called from TouchInput.Tick) -----------------------------------------
        public static bool TryBeginTouch(Touch t)
        {
            foreach (var b in ActiveButtons())
            {
                if (!b.Screen.Contains(t.position)) continue;
                b.Finger = t.fingerId;
                Press(b);
                return true;
            }
            return false;
        }

        public static void UpdateTouch(Touch t) { }

        public static void EndTouch(Touch t)
        {
            foreach (var b in s_All)
            {
                if (b.Finger != t.fingerId) continue;
                b.Finger = -1;
                Release(b);
            }
        }

        static void Press(Btn b)
        {
            b.FlashUntil = Time.unscaledTime + 0.15f;
            switch (b.Kind)
            {
                case Kind.Tap:
                    if (Resolve(b.Action, out var k, out var k2))
                    {
                        if (k2 != KeyCode.None) VInput.TapKey(k2, 2);
                        VInput.TapKey(k, 2);
                    }
                    break;
                case Kind.Hold:
                    if (Resolve(b.Action, out b.HeldKey, out b.HeldSecondary))
                    {
                        if (b.HeldSecondary != KeyCode.None) VInput.SetKey(b.HeldSecondary, true);
                        VInput.SetKey(b.HeldKey, true);
                    }
                    break;
                case Kind.Modifier:
                    b.On = !b.On;
                    VInput.SetKey(b.Key, b.On);
                    break;
                case Kind.RightClick:
                    b.On = !b.On;
                    TouchInput.RightClickMode = b.On;
                    Flash(b.On ? "one finger = right mouse button" : "one finger = left mouse button");
                    break;
                case Kind.Hide:
                    Hidden = !Hidden;
                    if (Hidden) ReleaseHolds();
                    break;
            }
        }

        static void Release(Btn b)
        {
            if (b.Kind == Kind.Hold)
            {
                if (b.HeldKey != KeyCode.None) VInput.SetKey(b.HeldKey, false);
                if (b.HeldSecondary != KeyCode.None) VInput.SetKey(b.HeldSecondary, false);
                b.HeldKey = b.HeldSecondary = KeyCode.None;
            }
        }

        static void ReleaseHolds()
        {
            foreach (var b in s_All)
            {
                if (b.Kind == Kind.Hold) Release(b);
                b.Finger = -1;
            }
        }

        public static void ReleaseAll()
        {
            ReleaseHolds();
        }

        static bool Resolve(string action, out KeyCode key, out KeyCode secondary)
        {
            key = secondary = KeyCode.None;
            if (action == null || InputSystem.Actions == null || !InputSystem.Actions.TryGetValue(action, out var a))
            {
                Log.Warn("HUD: unknown action " + action);
                return false;
            }
            if (a.Key == KeyCode.None)
            {
                if (!s_FallbackKeys.TryGetValue(action, out var fk))
                {
                    fk = s_NextFallback++;
                    s_FallbackKeys[action] = fk;
                }
                a.Key = fk;
                a.SecondaryKey = KeyCode.None;
                InputSystem.Actions[action] = a;   // in memory only until the game saves its controls
                Log.Info($"HUD: action '{action}' had no key, using {fk}");
            }
            key = a.Key;
            secondary = a.SecondaryKey;
            return true;
        }

        public static void Flash(string msg)
        {
            s_Flash = msg;
            s_FlashUntil = Time.unscaledTime + 1.2f;
        }

        // ---- drawing -------------------------------------------------------------------------
        void OnGUI()
        {
            if (!s_Visible && Time.unscaledTime > s_FlashUntil) return;
            if (m_Label == null)
            {
                m_Tex = Texture2D.whiteTexture;
                m_Label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, fontStyle = FontStyle.Bold };
                m_FlashStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            }
            float h = UnityEngine.Screen.height;
            foreach (var b in ActiveButtons())
            {
                var r = new Rect(b.Screen.x, h - b.Screen.yMax, b.Screen.width, b.Screen.height);
                bool lit = b.On || b.Finger >= 0 || Time.unscaledTime < b.FlashUntil;
                GUI.color = lit ? new Color(1f, 0.85f, 0.3f, 0.55f) : new Color(0.08f, 0.08f, 0.08f, 0.35f);
                GUI.DrawTexture(r, m_Tex);
                GUI.color = new Color(1, 1, 1, lit ? 0.95f : 0.7f);
                m_Label.fontSize = Mathf.RoundToInt(b.Screen.height * (b.Label.Contains("\n") ? 0.17f : 0.2f));
                string label = b.Kind == Kind.Hide && Hidden ? "SHOW" : b.Label;
                GUI.Label(r, label, m_Label);
            }
            if (Time.unscaledTime < s_FlashUntil && !string.IsNullOrEmpty(s_Flash))
            {
                m_FlashStyle.fontSize = Mathf.RoundToInt(h * 0.03f);
                var r = new Rect(0, h * 0.12f, UnityEngine.Screen.width, h * 0.06f);
                GUI.color = new Color(0, 0, 0, 0.5f);
                GUI.DrawTexture(new Rect(UnityEngine.Screen.width * 0.3f, r.y, UnityEngine.Screen.width * 0.4f, r.height), m_Tex);
                GUI.color = Color.white;
                GUI.Label(r, s_Flash, m_FlashStyle);
            }
            GUI.color = Color.white;
        }
    }
}
