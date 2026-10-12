using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PPGiOS
{
    // Mirrors the Unity log into Documents/PeoplePlayground-iOS.log (previous run: -prev.log) so it can be
    // pulled off the device with Files / Filza, and keeps the last lines for the on-screen log viewer.
    public static class Log
    {
        public const string FileName = "PeoplePlayground-iOS.log";
        public const string PrevFileName = "PeoplePlayground-iOS-prev.log";

        static StreamWriter s_Writer;
        static readonly object s_Lock = new object();
        static readonly Queue<string> s_Recent = new Queue<string>();
        public static int ErrorCount;
        public static int WarningCount;
        static readonly Dictionary<string, int> s_Counts = new Dictionary<string, int>();
        static bool s_Initialised;

        public static void Init(string dir)
        {
            if (s_Initialised) return;
            s_Initialised = true;
            try
            {
                string path = Path.Combine(dir, FileName);
                string prev = Path.Combine(dir, PrevFileName);
                if (File.Exists(path))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(path, prev);
                }
                s_Writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                s_Writer.AutoFlush = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PPG-iOS] cannot open log file: " + e.Message);
            }
            Application.logMessageReceivedThreaded += OnLog;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Write("E", "UNHANDLED EXCEPTION (terminating=" + e.IsTerminating + "): " + e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Write("E", "Unobserved task exception: " + e.Exception);
                e.SetObserved();
            };
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            // the game's Steam stats code reports this once when Steam is absent - expected on iOS
            if (type == LogType.Error && condition != null && condition.StartsWith("Max stat failure count")) { type = LogType.Log; stackTrace = null; }
            string tag = type == LogType.Log ? "I" : type == LogType.Warning ? "W" : "E";
            lock (s_Lock)
            {
                // the game can spam the same message (or the same few messages in turn) every frame:
                // after the first 3, only the 10th, 100th and every 1000th occurrence is written
                if (condition == null) condition = "";
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) ErrorCount++;
                else if (type == LogType.Warning) WarningCount++;
                s_Counts.TryGetValue(condition, out int seen);
                seen++;
                if (s_Counts.Count < 5000 || seen > 1) s_Counts[condition] = seen;
                if (seen > 3)
                {
                    if (seen != 10 && seen != 100 && seen % 1000 != 0) return;
                    condition = $"(seen x{seen}) " + condition;
                }
                bool withStack = type == LogType.Exception || type == LogType.Error || type == LogType.Assert;
                if (stackTrace != null && stackTrace.StartsWith("Stacktrace is not supported")) withStack = false;
                WriteLocked(tag, condition, withStack ? stackTrace : null);
            }
        }

        static void Write(string tag, string msg)
        {
            lock (s_Lock) WriteLocked(tag, msg, null);
        }

        static void WriteLocked(string tag, string msg, string stack)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {msg}";
            try
            {
                s_Writer?.WriteLine(line);
                if (!string.IsNullOrEmpty(stack))
                    s_Writer?.WriteLine(stack.TrimEnd());
            }
            catch { }
            s_Recent.Enqueue(line.Length > 400 ? line.Substring(0, 400) : line);
            while (s_Recent.Count > 60) s_Recent.Dequeue();
        }

        public static string[] Snapshot()
        {
            lock (s_Lock) { return s_Recent.ToArray(); }
        }

        public static void Info(string s) => Debug.Log("[PPG-iOS] " + s);
        public static void Warn(string s) => Debug.LogWarning("[PPG-iOS] " + s);
        public static void Error(string s) => Debug.LogError("[PPG-iOS] " + s);
    }
}
