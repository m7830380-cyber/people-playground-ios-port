using System.Collections.Generic;
using PPGShim;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PPGiOS
{
    // Turns raw touches into the virtual mouse the patched game reads (PPGShim.VInput).
    //
    //  * UI (anything an EventSystem raycast hits): the finger is the mouse with the left button held -
    //    tap = click, drag = sliders / scroll views.
    //  * World, one finger: tap = left click; touch-and-move = left drag (grab) starting exactly where
    //    the finger went down; holding still for a while = right click (context menu).
    //    With the RMB toggle on, the finger is the right mouse button instead.
    //  * World, two fingers: pinch = zoom, drag = pan (drives the game's camera directly). A second
    //    finger while an object is held is ignored, so the object is never flung.
    //  * Touches that start on an on-screen button belong to the HUD.
    public static class TouchInput
    {
        enum Mode { Idle, Pending, WorldLeft, WorldRight, Ui, Camera, Consumed }

        public static bool RightClickMode;
        public static bool LongPressEnabled = true;
        public const float LongPressSeconds = 0.6f;
        const float CommitSeconds = 0.12f;

        static Mode s_Mode = Mode.Idle;
        static int s_Primary = -1, s_Secondary = -1;
        static Vector2 s_StartPos, s_LastPrimary, s_LastSecondary;
        static float s_StartTime;
        static bool s_Moved;
        static int s_PendingRightTap;   // frames until a queued right click fires
        static Vector2 s_RightTapPos;

        static readonly HashSet<int> s_HudFingers = new HashSet<int>();
        static readonly List<RaycastResult> s_Hits = new List<RaycastResult>();
        static PointerEventData s_Ped;
        static EventSystem s_PedOwner;
        static CameraControlBehaviour s_Cam;

        public static bool IsGrabbing => s_Mode == Mode.WorldLeft;
        public static string State => s_Mode.ToString();

        static float MoveThreshold => Mathf.Max(10f, (Screen.dpi > 0 ? Screen.dpi : 326f) * 0.06f);

        public static void Tick()
        {
            if (!VInput.Virtual) return;

            if (s_PendingRightTap > 0 && --s_PendingRightTap == 0)
            {
                VInput.VMousePosition = s_RightTapPos;
                VInput.TapMouseButton(1);
            }

            int n = Input.touchCount;
            bool primarySeen = false, secondarySeen = false;
            Touch primary = default, secondary = default;
            for (int i = 0; i < n; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began)
                {
                    if (Hud.TryBeginTouch(t) || DebugOverlay.TryBeginTouch(t))
                    {
                        s_HudFingers.Add(t.fingerId);
                        continue;
                    }
                    if (s_Primary < 0) BeginPrimary(t);
                    else if (s_Secondary < 0) BeginSecondary(t);
                }
                if (s_HudFingers.Contains(t.fingerId))
                {
                    Hud.UpdateTouch(t);
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                    {
                        Hud.EndTouch(t);
                        s_HudFingers.Remove(t.fingerId);
                    }
                    continue;
                }
                if (t.fingerId == s_Primary) { primary = t; primarySeen = true; }
                else if (t.fingerId == s_Secondary) { secondary = t; secondarySeen = true; }
            }

            // a finger can vanish without an Ended phase (app switch, system gesture)
            if (s_Primary >= 0 && !primarySeen) { EndPrimary(s_LastPrimary); }
            if (s_Secondary >= 0 && !secondarySeen) s_Secondary = -1;
            if (n == 0) s_HudFingers.Clear();

            if (primarySeen) UpdatePrimary(primary);
            if (s_Mode == Mode.Camera && primarySeen && secondarySeen) UpdateCamera(primary, secondary);
            if (secondarySeen)
            {
                s_LastSecondary = secondary.position;
                if (secondary.phase == TouchPhase.Ended || secondary.phase == TouchPhase.Canceled)
                {
                    s_Secondary = -1;
                    if (s_Mode == Mode.Camera) s_Mode = Mode.Consumed;   // rest of gesture does nothing
                }
            }
            if (primarySeen && (primary.phase == TouchPhase.Ended || primary.phase == TouchPhase.Canceled))
                EndPrimary(primary.position);
        }

        static void BeginPrimary(Touch t)
        {
            s_Primary = t.fingerId;
            s_StartPos = s_LastPrimary = t.position;
            s_StartTime = Time.unscaledTime;
            s_Moved = false;
            VInput.VMousePosition = t.position;
            if (OverUi(t.position))
            {
                s_Mode = Mode.Ui;
                VInput.SetMouseButton(0, true);
            }
            else if (RightClickMode)
            {
                s_Mode = Mode.WorldRight;
                VInput.SetMouseButton(1, true);
            }
            else
            {
                s_Mode = Mode.Pending;  // wait: tap, drag or the start of a two-finger gesture
            }
        }

        static void BeginSecondary(Touch t)
        {
            s_Secondary = t.fingerId;
            s_LastSecondary = t.position;
            if (s_Mode == Mode.Pending || s_Mode == Mode.Consumed)
            {
                s_Mode = Mode.Camera;
                s_Cam = s_Cam ? s_Cam : Object.FindObjectOfType<CameraControlBehaviour>();
            }
            // while grabbing / in UI the second finger is ignored
        }

        static void UpdatePrimary(Touch t)
        {
            Vector2 p = t.position;
            if (!s_Moved && (p - s_StartPos).magnitude > MoveThreshold) s_Moved = true;
            if (s_Mode == Mode.Camera) return;   // UpdateCamera needs last frame's position
            s_LastPrimary = p;
            float held = Time.unscaledTime - s_StartTime;
            switch (s_Mode)
            {
                case Mode.Pending:
                    if (s_Moved || held > CommitSeconds)
                    {
                        // press exactly where the finger went down; following frames track the finger
                        VInput.VMousePosition = s_StartPos;
                        VInput.SetMouseButton(0, true);
                        s_Mode = Mode.WorldLeft;
                    }
                    break;
                case Mode.WorldLeft:
                    VInput.VMousePosition = p;
                    if (LongPressEnabled && !s_Moved && held > LongPressSeconds && !RightClickMode)
                    {
                        // long press = right click at the press position
                        VInput.SetMouseButton(0, false);
                        s_RightTapPos = s_StartPos;
                        s_PendingRightTap = 2;
                        s_Mode = Mode.Consumed;
                        Hud.Flash("right click");
                    }
                    break;
                case Mode.WorldRight:
                case Mode.Ui:
                    VInput.VMousePosition = p;
                    break;
            }
        }

        static void EndPrimary(Vector2 pos)
        {
            switch (s_Mode)
            {
                case Mode.Pending:
                    // quick tap: one-frame click where the finger went down
                    VInput.VMousePosition = s_StartPos;
                    VInput.TapMouseButton(0);
                    break;
                case Mode.WorldLeft:
                case Mode.Ui:
                    VInput.VMousePosition = pos;
                    VInput.SetMouseButton(0, false);
                    break;
                case Mode.WorldRight:
                    VInput.VMousePosition = pos;
                    VInput.SetMouseButton(1, false);
                    break;
            }
            s_Primary = -1;
            // if a second finger is still down it does nothing until everything is lifted
            s_Mode = s_Secondary >= 0 ? Mode.Consumed : Mode.Idle;
            if (s_Secondary >= 0) { s_Primary = s_Secondary; s_Secondary = -1; s_LastPrimary = s_LastSecondary; }
        }

        static void UpdateCamera(Touch a, Touch b)
        {
            if (!s_Cam) s_Cam = Object.FindObjectOfType<CameraControlBehaviour>();
            Vector2 a0 = s_LastPrimary, b0 = s_LastSecondary;   // previous frame
            Vector2 a1 = a.position, b1 = b.position;
            s_LastPrimary = a1;
            s_LastSecondary = b1;
            if (!s_Cam || Global.main == null || Global.main.camera == null) return;
            if (Global.main.GetPausedMenu() || DialogBox.IsAnyDialogboxOpen) return;

            float h = Screen.height;
            Vector2 centre = new Vector2(Screen.width * 0.5f, h * 0.5f);
            Vector2 midPrev = (a0 + b0) * 0.5f, midCur = (a1 + b1) * 0.5f;
            float dPrev = (a0 - b0).magnitude, dCur = (a1 - b1).magnitude;

            float zoom = s_Cam.targetZoom;
            Vector3 pos = s_Cam.targetPos;
            float upp = 2f * zoom / h;
            Vector2 worldUnderMid = (Vector2)pos + (midPrev - centre) * upp;

            float newZoom = zoom;
            if (dPrev > 20f && dCur > 20f)
                newZoom = Mathf.Clamp(zoom * dPrev / dCur, s_Cam.MinZoom, s_Cam.MaxZoom);
            float upp2 = 2f * newZoom / h;
            Vector2 newPos = worldUnderMid - (midCur - centre) * upp2;

            s_Cam.CurrentlyFollowing.Clear();
            s_Cam.targetZoom = newZoom;
            s_Cam.targetPos = new Vector3(newPos.x, newPos.y, -10f);
        }

        static bool OverUi(Vector2 pos)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            if (s_Ped == null || s_PedOwner != es) { s_Ped = new PointerEventData(es); s_PedOwner = es; }
            s_Ped.Reset();
            s_Ped.position = pos;
            s_Hits.Clear();
            es.RaycastAll(s_Ped, s_Hits);
            return s_Hits.Count > 0;
        }

        /// Lifts every virtual button (e.g. when the app goes to the background).
        public static void ReleaseAll()
        {
            VInput.SetMouseButton(0, false);
            VInput.SetMouseButton(1, false);
            VInput.SetMouseButton(2, false);
            s_Mode = Mode.Idle;
            s_Primary = s_Secondary = -1;
            s_HudFingers.Clear();
            Hud.ReleaseAll();
        }
    }
}
