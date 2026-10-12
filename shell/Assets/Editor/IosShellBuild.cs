using System;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the iOS player "shell" with Unity 2020.3.1f1 (the game's exact version): an IL2CPP player that
// contains the patched retail People Playground assemblies (Assets/Plugins/Game, from the private repo)
// plus the iOS mod. The game's own data files are swapped into the .app afterwards by tools/package_ipa.py.
public static class IosShellBuild
{
    const string ScenePath = "Assets/Scenes/Boot.unity";

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("SHELL_OUT") ?? "Build/iOS";

        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

        PlayerSettings.companyName = "Studio Minus";
        PlayerSettings.productName = "People Playground";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, "nl.studiominus.peopleplayground.ios");
        PlayerSettings.bundleVersion = "1.0";
        PlayerSettings.iOS.buildNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "1";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetArchitecture(BuildTargetGroup.iOS, 1); // ARM64
        // Low is the least stripping IL2CPP allows in 2020.3; link.xml keeps every game assembly whole.
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.iOS, ManagedStrippingLevel.Low);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.iOS, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.iOS, ApiCompatibilityLevel.NET_4_6);
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Linear;      // same as the retail game
        PlayerSettings.gpuSkinning = false;                 // same as the retail game
        PlayerSettings.MTRendering = true;
        PlayerSettings.iOS.targetOSVersionString = "14.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.requiresFullScreen = true;
        PlayerSettings.iOS.hideHomeButton = true;
        PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
        PlayerSettings.iOS.statusBarStyle = iOSStatusBarStyle.Default;
        PlayerSettings.statusBarHidden = true;
        PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;
        PlayerSettings.accelerometerFrequency = 0;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.enableInternalProfiler = false;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;
        // The retail game's PlayerSettings (in its globalgamemanagers) replace these at runtime anyway,
        // but the shell must agree on anything baked into the player binary.

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outDir,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[IosShellBuild] result: " + report.summary.result + " errors=" + report.summary.totalErrors);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            // Keep the linked assemblies IL2CPP choked on (Temp is wiped when the editor quits) so CI can
            // hand them to the private repo for inspection. They contain game code: never publish them here.
            string dbg = Environment.GetEnvironmentVariable("SHELL_DEBUG_OUT");
            if (!string.IsNullOrEmpty(dbg) && Directory.Exists("Temp/StagingArea/Data/Managed"))
            {
                Directory.CreateDirectory(dbg);
                foreach (var f in Directory.GetFiles("Temp/StagingArea/Data/Managed", "*.dll"))
                    File.Copy(f, Path.Combine(dbg, Path.GetFileName(f)), true);
                Debug.Log("[IosShellBuild] copied staged assemblies to " + dbg);
            }
            EditorApplication.Exit(1);
        }
    }

    [PostProcessBuild(100)]
    public static void OnPostprocess(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;
        root.SetString("CFBundleDisplayName", "People Playground");
        root.SetBoolean("UIFileSharingEnabled", true);           // Documents visible in Files / Finder
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("UISupportsDocumentBrowser", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true); // allow 120Hz on ProMotion
        root.SetBoolean("UIRequiresFullScreen", true);
        root.SetBoolean("UIStatusBarHidden", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        plist.WriteToFile(plistPath);

        // Unsigned build for TrollStore: strip signing requirements from the Xcode project.
        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        foreach (var guid in new[] { proj.GetUnityMainTargetGuid(), proj.GetUnityFrameworkTargetGuid() })
        {
            proj.SetBuildProperty(guid, "CODE_SIGNING_ALLOWED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGNING_REQUIRED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGN_IDENTITY", "");
            proj.SetBuildProperty(guid, "ENABLE_BITCODE", "NO");
            proj.SetBuildProperty(guid, "IPHONEOS_DEPLOYMENT_TARGET", "14.0");
            // Xcode 15's new linker breaks Unity 2020-era static initialisers; use the classic one.
            proj.AddBuildProperty(guid, "OTHER_LDFLAGS", "-ld_classic");
            // IL2CPP output from 2020.3 trips warnings that newer clang turns into errors.
            proj.AddBuildProperty(guid, "WARNING_CFLAGS", "-Wno-error");
            proj.SetBuildProperty(guid, "GCC_TREAT_WARNINGS_AS_ERRORS", "NO");
        }
        proj.WriteToFile(projPath);
    }
}
