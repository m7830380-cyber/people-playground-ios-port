using System;
using System.IO;
using PPGShim;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace PPGiOS
{
    // Entry point of the iOS additions.
    public static class Bootstrap
    {
        public static string Documents { get; private set; }
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;

        // Runs before anything else in the player (before the first scene's Awake calls): the game opens
        // every file relative to the working directory (config.json, ControlScheme.json, Contraptions/,
        // Logs/ ...), so make that the app's Documents folder.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            Documents = Application.persistentDataPath;
            Log.Init(Documents);
            try
            {
                if (OnDevice)
                    Directory.SetCurrentDirectory(Documents);
                foreach (var d in new[] { "Contraptions", "People Playground_Data" })
                    Directory.CreateDirectory(Path.Combine(Documents, d));
            }
            catch (Exception e)
            {
                Log.Error("cannot prepare Documents: " + e);
            }
            Log.Info($"People Playground iOS starting. cwd={Environment.CurrentDirectory}");
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB cpu={SystemInfo.processorCount}x " +
                     $"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRate}Hz dpi={Screen.dpi} " +
                     $"safe={Screen.safeArea} unity={Application.unityVersion}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                TouchSim.Init();
                VInput.Virtual = OnDevice || Input.touchSupported || TouchSim.Enabled;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Platform.ApplyFrameRate();
                Log.Info($"virtual input={VInput.Virtual} targetFrameRate={Application.targetFrameRate}");

                InstallPlayerLoopHook();

                var go = new GameObject("PPGiOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<Hud>();
                go.AddComponent<DebugOverlay>();

                SceneManager.sceneLoaded += (s, mode) =>
                {
                    Log.Info($"scene loaded: '{s.name}' ({mode}) objects={s.rootCount}");
                    SafeArea.OnSceneLoaded();
                    Platform.ApplyFrameRate();
                };
                Application.lowMemory += () => Log.Warn("iOS low memory warning");
            }
            catch (Exception e)
            {
                Log.Error("PPGiOS init failed: " + e);
            }
        }

        // TouchInput.Tick runs at the start of every frame, after Unity has read the touches and before
        // any MonoBehaviour.Update, so the game always sees a consistent virtual mouse/keyboard.
        static void InstallPlayerLoopHook()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(PreUpdate)) continue;
                var subs = loop.subSystemList[i].subSystemList;
                var list = new PlayerLoopSystem[subs.Length + 1];
                Array.Copy(subs, list, subs.Length);
                list[subs.Length] = new PlayerLoopSystem { type = typeof(TouchInput), updateDelegate = SafeTick };
                loop.subSystemList[i].subSystemList = list;
                PlayerLoop.SetPlayerLoop(loop);
                Log.Info("touch input hooked into PreUpdate");
                return;
            }
            Log.Error("PreUpdate not found in player loop; touch input will not work");
        }

        static int s_TickErrors;
        static void SafeTick()
        {
            try
            {
                TouchInput.Tick();
            }
            catch (Exception e)
            {
                if (s_TickErrors++ < 20) Log.Error("touch input: " + e);
            }
            finally
            {
                VInput.BeginFrame();
            }
        }
    }
}
