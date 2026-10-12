using UnityEngine;

namespace PPGiOS
{
    // FPS counter in the top-right corner. Tap it to open the log viewer (last log lines); tap anywhere
    // to close it again.
    public class DebugOverlay : MonoBehaviour
    {
        static bool s_ShowLog;
        static Rect s_Corner;     // pixels, origin bottom-left
        float m_Fps, m_Accum;
        int m_Frames;
        GUIStyle m_Style, m_Small;

        public static bool TryBeginTouch(Touch t)
        {
            if (s_ShowLog)
            {
                s_ShowLog = false;
                return true;
            }
            if (s_Corner.Contains(t.position))
            {
                s_ShowLog = true;
                return true;
            }
            return false;
        }

        void Update()
        {
            m_Accum += Time.unscaledDeltaTime;
            m_Frames++;
            if (m_Accum >= 0.5f)
            {
                m_Fps = m_Frames / m_Accum;
                m_Accum = 0;
                m_Frames = 0;
            }
            Rect safe = Screen.safeArea;
            float s = Screen.height * 0.09f;
            s_Corner = new Rect(safe.xMax - s * 2.2f, Screen.height - s, s * 2.2f, s);
            // the bottom of the corner zone must not cover the HUD's top button
            if (Input.GetKeyDown(KeyCode.F12)) s_ShowLog = !s_ShowLog;
        }

        void OnGUI()
        {
            if (m_Style == null)
            {
                m_Style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight };
                m_Small = new GUIStyle(GUI.skin.label) { wordWrap = true, alignment = TextAnchor.LowerLeft };
            }
            float w = Screen.width, h = Screen.height;
            m_Style.fontSize = Mathf.RoundToInt(h * 0.022f);
            string txt = $"{m_Fps:0} fps";
            if (Log.ErrorCount > 0) txt += $"  {Log.ErrorCount} err";
            var r = new Rect(s_Corner.x, h - s_Corner.yMax + 4, s_Corner.width - 6, s_Corner.height);
            GUI.color = new Color(1, 1, 1, 0.75f);
            GUI.Label(r, txt, m_Style);
            GUI.color = Color.white;
            if (!s_ShowLog) return;

            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
            Rect safe = Screen.safeArea;
            m_Small.fontSize = Mathf.RoundToInt(h * 0.019f);
            string all = string.Join("\n", Log.Snapshot());
            GUI.Label(new Rect(safe.xMin + 10, h * 0.02f, safe.width - 20, h * 0.96f), all, m_Small);
        }
    }
}
