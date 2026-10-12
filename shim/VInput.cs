using System;
using UnityEngine;

namespace PPGShim
{
    /// <summary>
    /// Replacement for UnityEngine.Input. The patcher rewrites every Input call in the game assemblies
    /// (Assembly-CSharp, UnityEngine.UI, TextMeshPro, ...) to the member with the same name here.
    ///
    /// While <see cref="Virtual"/> is on (set by the iOS touch layer) the mouse is the virtual one the
    /// touch layer drives, and keys are the virtual ones OR the real keyboard (a Bluetooth keyboard keeps
    /// working). With Virtual off everything forwards to UnityEngine.Input unchanged.
    /// </summary>
    public static class VInput
    {
        public static bool Virtual;

        // ---- virtual mouse -------------------------------------------------------------------
        public static Vector3 VMousePosition;
        /// Scroll accumulated by the touch layer; becomes this frame's mouseScrollDelta at BeginFrame.
        public static Vector2 VScroll;
        static Vector2 scrollThisFrame;
        static readonly bool[] mouseWant = new bool[3];
        static readonly bool[] mouseCur = new bool[3];
        static readonly bool[] mousePrev = new bool[3];
        static readonly int[] mouseTapFrames = new int[3];

        public static void SetMouseButton(int b, bool held) { if (b >= 0 && b < 3) mouseWant[b] = held; }
        /// Press and release a button, guaranteeing at least one frame down.
        public static void TapMouseButton(int b) { if (b >= 0 && b < 3) mouseTapFrames[b] = 1; }
        public static bool IsMouseHeld(int b) => b >= 0 && b < 3 && mouseCur[b];

        // ---- virtual keys ----------------------------------------------------------------------
        const int MaxKey = 512;
        static readonly bool[] keyWant = new bool[MaxKey];
        static readonly bool[] keyCur = new bool[MaxKey];
        static readonly bool[] keyPrev = new bool[MaxKey];
        static readonly int[] keyTapFrames = new int[MaxKey];
        static bool anyVirtualKey, anyVirtualKeyDown;

        public static void SetKey(KeyCode k, bool held)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyWant[i] = held;
        }
        /// Press a key for <paramref name="frames"/> frames, then release it.
        public static void TapKey(KeyCode k, int frames = 1)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyTapFrames[i] = Math.Max(keyTapFrames[i], frames);
        }
        public static bool IsVirtualKeyHeld(KeyCode k) { int i = (int)k; return i > 0 && i < MaxKey && keyWant[i]; }

        public static int Frame { get; private set; }

        /// Called once per frame by the touch layer before any game Update runs: commits the wanted
        /// state so Down/Up edges are stable for the whole frame (same semantics as UnityEngine.Input).
        public static void BeginFrame()
        {
            Frame++;
            for (int b = 0; b < 3; b++)
            {
                mousePrev[b] = mouseCur[b];
                bool tap = mouseTapFrames[b] > 0;
                if (tap) mouseTapFrames[b]--;
                mouseCur[b] = mouseWant[b] || tap;
            }
            anyVirtualKey = anyVirtualKeyDown = false;
            for (int i = 1; i < MaxKey; i++)
            {
                keyPrev[i] = keyCur[i];
                bool tap = keyTapFrames[i] > 0;
                if (tap) keyTapFrames[i]--;
                keyCur[i] = keyWant[i] || tap;
                anyVirtualKey |= keyCur[i];
                anyVirtualKeyDown |= keyCur[i] && !keyPrev[i];
            }
            scrollThisFrame = VScroll;
            VScroll = Vector2.zero;
        }

        static bool VKey(int i) => i > 0 && i < MaxKey && keyCur[i];
        static bool VKeyDown(int i) => i > 0 && i < MaxKey && keyCur[i] && !keyPrev[i];
        static bool VKeyUp(int i) => i > 0 && i < MaxKey && !keyCur[i] && keyPrev[i];

        static int MouseIndex(KeyCode k) => k >= KeyCode.Mouse0 && k <= KeyCode.Mouse2 ? (int)(k - KeyCode.Mouse0) : -1;

        // ---- UnityEngine.Input surface ---------------------------------------------------------
        public static bool GetKey(KeyCode k)
        {
            if (!Virtual) return Input.GetKey(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return mouseCur[mb] || VKey((int)k);
            return VKey((int)k) || Input.GetKey(k);
        }
        public static bool GetKeyDown(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyDown(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (mouseCur[mb] && !mousePrev[mb]) || VKeyDown((int)k);
            return VKeyDown((int)k) || Input.GetKeyDown(k);
        }
        public static bool GetKeyUp(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyUp(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (!mouseCur[mb] && mousePrev[mb]) || VKeyUp((int)k);
            return VKeyUp((int)k) || Input.GetKeyUp(k);
        }
        public static bool GetKey(string name) => Input.GetKey(name);
        public static bool GetKeyDown(string name) => Input.GetKeyDown(name);
        public static bool GetKeyUp(string name) => Input.GetKeyUp(name);

        public static bool GetMouseButton(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] : Input.GetMouseButton(b);
        public static bool GetMouseButtonDown(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] && !mousePrev[b] : Input.GetMouseButtonDown(b);
        public static bool GetMouseButtonUp(int b) => Virtual ? b >= 0 && b < 3 && !mouseCur[b] && mousePrev[b] : Input.GetMouseButtonUp(b);

        public static Vector3 mousePosition => Virtual ? VMousePosition : Input.mousePosition;
        public static Vector2 mouseScrollDelta => Virtual ? scrollThisFrame : Input.mouseScrollDelta;
        public static bool mousePresent => Virtual || Input.mousePresent;
        /// Game/UI code must not see raw touches: the touch layer turns them into mouse input.
        public static int touchCount => Virtual ? 0 : Input.touchCount;
        public static Touch GetTouch(int i) => Input.GetTouch(i);
        public static bool touchSupported => !Virtual && Input.touchSupported;

        public static bool anyKey => Virtual ? anyVirtualKey || mouseCur[0] || mouseCur[1] || mouseCur[2] || Input.anyKey : Input.anyKey;
        public static bool anyKeyDown => Virtual ? anyVirtualKeyDown || GetMouseButtonDown(0) || GetMouseButtonDown(1) || Input.anyKeyDown : Input.anyKeyDown;

        public static float GetAxis(string axis)
        {
            if (Virtual && axis == "Mouse ScrollWheel") return scrollThisFrame.y * 0.1f;
            return Input.GetAxis(axis);
        }
        public static float GetAxisRaw(string axis)
        {
            if (Virtual && axis == "Mouse ScrollWheel") return scrollThisFrame.y * 0.1f;
            return Input.GetAxisRaw(axis);
        }
        public static bool GetButton(string b) => Input.GetButton(b);
        public static bool GetButtonDown(string b) => Input.GetButtonDown(b);
        public static bool GetButtonUp(string b) => Input.GetButtonUp(b);

        public static string inputString => Input.inputString;
        public static string compositionString => Input.compositionString;
        public static IMECompositionMode imeCompositionMode { get => Input.imeCompositionMode; set => Input.imeCompositionMode = value; }
        public static Vector2 compositionCursorPos { get => Input.compositionCursorPos; set => Input.compositionCursorPos = value; }
    }
}
