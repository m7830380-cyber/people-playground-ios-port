using System;
using System.Collections.Generic;
using UnityEngine;

namespace PPGiOS
{
    // Touch source for TouchInput. On the device: the real touches. On the PC, when the game is started with
    // -simulate-touch, the mouse plays the fingers so the touch layer and the HUD can be tested without a phone:
    //   left button  = one finger
    //   right button = two fingers mirrored around the point where it was pressed: dragging changes their
    //                  distance (pinch); with Shift held both fingers follow the mouse instead (two-finger pan)
    public static class TouchSim
    {
        public static bool Enabled { get; private set; }
        static readonly List<Touch> s_Touches = new List<Touch>();
        static bool s_LeftDown, s_RightDown;
        static Vector2 s_RightAnchor, s_RightOffsetStart, s_PrevA, s_PrevB;

        public static void Init()
        {
            Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "-simulate-touch") >= 0;
            if (Enabled) Log.Info("touch simulation from mouse enabled (-simulate-touch)");
        }

        public static int Count => Enabled ? s_Touches.Count : Input.touchCount;
        public static Touch Get(int i) => Enabled ? s_Touches[i] : Input.GetTouch(i);

        /// Builds this frame's simulated touches (call once per frame before reading).
        public static void Update()
        {
            if (!Enabled) return;
            s_Touches.Clear();
            Vector2 m = Input.mousePosition;

            bool left = Input.GetMouseButton(0);
            if (left || s_LeftDown)
                s_Touches.Add(Make(0, m, Phase(s_LeftDown, left), m - s_PrevA));
            if (left || s_LeftDown) s_PrevA = m;
            s_LeftDown = left;

            bool right = Input.GetMouseButton(1);
            if (right && !s_RightDown) { s_RightAnchor = m; s_RightOffsetStart = new Vector2(60, 0); }
            if (right || s_RightDown)
            {
                bool pan = Input.GetKey(KeyCode.LeftShift);
                Vector2 off = s_RightOffsetStart + (pan ? Vector2.zero : (m - s_RightAnchor));
                Vector2 centre = pan ? m : s_RightAnchor;
                Vector2 a = centre + off, b = centre - off;
                var ph = Phase(s_RightDown, right);
                s_Touches.Add(Make(10, a, ph, a - s_PrevA));
                s_Touches.Add(Make(11, b, ph, b - s_PrevB));
                s_PrevA = a; s_PrevB = b;
            }
            s_RightDown = right;
        }

        static TouchPhase Phase(bool was, bool now) =>
            now && !was ? TouchPhase.Began : !now ? TouchPhase.Ended : TouchPhase.Moved;

        static Touch Make(int id, Vector2 pos, TouchPhase phase, Vector2 delta) =>
            new Touch { fingerId = id, position = pos, rawPosition = pos, phase = phase, deltaPosition = delta, tapCount = 1 };
    }
}
