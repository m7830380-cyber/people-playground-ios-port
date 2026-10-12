using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PPGShim
{
    /// <summary>Stand-ins for desktop-only calls the patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: there are no other processes on iOS ------------------
        public static Process ProcessStart(ProcessStartInfo info)
        {
            Debug.LogWarning("[PPG-iOS] Process.Start blocked: " + (info?.FileName ?? "<null>") + " " + (info?.Arguments ?? ""));
            return null;
        }
        public static Process ProcessStart(string file)
        {
            // URLs are the one useful case (help links etc.)
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Application.OpenURL(file);
                return null;
            }
            Debug.LogWarning("[PPG-iOS] Process.Start blocked: " + file);
            return null;
        }
        public static Process ProcessStart(string file, string args)
        {
            Debug.LogWarning("[PPG-iOS] Process.Start blocked: " + file + " " + args);
            return null;
        }
        public static bool ProcessStartInstance(Process p)
        {
            Debug.LogWarning("[PPG-iOS] Process.Start blocked: " + (p?.StartInfo?.FileName ?? "<null>"));
            return false;
        }
        public static Process[] GetProcessesByName(string name) => new Process[0];

        // ---- frame pacing ----------------------------------------------------------------------
        // iOS ignores QualitySettings.vSyncCount and runs at 30 fps unless targetFrameRate is set,
        // so the game's "VSync" option is mapped to "match the display" (60, or 120 on ProMotion).
        static int vSyncCount = 1;
        static int requestedFps = -1;

        /// Highest refresh rate the display supports (120 on ProMotion iPhones).
        public static int DisplayMaxFps
        {
            get
            {
                int r = Screen.currentResolution.refreshRate;
                return r >= 30 ? r : 60;
            }
        }

        /// Optional cap set by the iOS mod (0 = no cap) e.g. to save battery.
        public static int UserFpsCap;

        public static int EffectiveTargetFps
        {
            get
            {
                int max = DisplayMaxFps;
                if (UserFpsCap > 0) max = Math.Min(max, UserFpsCap);
                if (vSyncCount > 0 || requestedFps <= 0) return max;
                return Math.Min(requestedFps, max);
            }
        }

        public static void SetVSyncCount(int v)
        {
            vSyncCount = v;
            QualitySettings.vSyncCount = v;
            ApplyFrameRate();
        }

        public static void SetTargetFrameRate(int fps)
        {
            requestedFps = fps;
            ApplyFrameRate();
        }

        public static void ApplyFrameRate()
        {
            Application.targetFrameRate = EffectiveTargetFps;
        }
    }
}
