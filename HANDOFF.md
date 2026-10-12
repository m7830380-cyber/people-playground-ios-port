# Handoff: People Playground → iPhone 16 Pro Max (latest iOS, no jailbreak)

Written 2026-10-12 by the previous Claude session for the next Claude. Read all of this before touching anything.

## 1. What the user wants

Move this existing port (built for a jailbroken iPhone 14 Pro Max on iOS 16.2, installed with TrollStore) to an
**iPhone 16 Pro Max on the latest iOS**. Use the **delivery model of [c22dev/muguet](https://github.com/c22dev/muguet)**:

* a small app that is **sideloaded**, not installed through TrollStore;
* the user's **game files come as a zip, which is unpacked into the app's Documents folder** (Files app → On My iPhone → People Playground).

Requirements from the original task are unchanged: Steam is stubbed, mods are disabled, and saves, contraptions and
settings live in Documents. Logging goes to `Documents/PeoplePlayground-iOS.log` (and `-prev.log`), with an on-screen
log viewer opened by tapping the top-right fps overlay. The game runs at 60 or 120 fps, has full touch controls with a
HUD, and must never crash silently.

### About the user

* Non-technical. Give short, honest, plain-language updates. After each milestone, tell them exactly what to install
  and how.
* **They must not spend any money.** Use free GitHub Actions only. They don't have a paid Apple developer account
  unless they say otherwise; ask before assuming one.
* Windows 10 PC. **No Mac.**
* Logs and screenshots arrive through Telegram Desktop in `C:\Users\v0id\Downloads\cockadoodledo\Telegram Desktop\`.
  That folder also holds files from their other ports (Portal 2, ULTRAKILL), so check a log's content before
  trusting its file name.
* GitHub account `m7830380-cyber`, and the `gh` CLI is logged in. Never send the user's email address to any
  unrelated service.

### Hard rules (the user agreed to these, so keep them)

* **Public repo** `m7830380-cyber/people-playground-ios-port`: only our own code. Never commit, upload as an artifact
  or cache anything derived from the game: DLLs (patched or not), IPAs that contain compiled game code, converted data
  or test outputs.
* **Private repo** `m7830380-cyber/ppg-ios-private`: holds patched DLLs (`Plugins/`) and IPA releases (`shell-latest`,
  `ipa-latest`). Game-derived things go here only.
* Put downloaded files in their own directories.

## 2. Important: what muguet is (and isn't)

Muguet is **not a general game loader.** It is a native port of **GTA V's WebAssembly build**: it uses wasmtime and wgpu
on Metal, compiles `game.wasm` AOT into the iOS app at build time, and the user supplies the game as a zip. It cannot
run Unity games. Take from it **only the distribution model**:

| muguet | what it means for us |
|---|---|
| Game files are supplied by the user as a zip and are not baked into the app | Ship the converted game data as a zip, separate from the IPA. The IPA holds the engine plus code only |
| Game code is compiled AOT into the iOS binary (no JIT) | Same for us: IL2CPP already does this. Unity on iOS cannot load managed DLLs at runtime, so **compiled game code stays inside the IPA**. That means the IPA stays private |
| Installed with AltStore, works with a free Apple ID (`FREE=1`, own bundle id) | The user's install path. AltServer runs on Windows. Free accounts get 7-day signing and a 3-app limit |
| Needs the increased-memory-limit entitlement | We probably don't: PPG is small and the 16 Pro Max has 8 GB of RAM. Verify that free-account signing doesn't strip anything we need |
| Launcher asks for the zip (document picker) | User said the zip "will be unpacked in the documents of the app". The Files app can unzip natively. An in-app picker plus unzip is a nice extra, not a requirement |

## 3. How the current port works (all working code is in this repo)

The retail Windows build (Unity **2020.3.1f1**, Mono, game version 1.27.18, at
`C:\Program Files (x86)\Steam\steamapps\common\People Playground`) is **not rebuilt from source**. Instead:

1. **`tools/build_plugins.py`** builds `shim/` (`PPGShim.dll`) and the Cecil patcher (`tools/patcher`). It then patches
   the retail `Managed/*.dll` into `../ppg-ios-private/Plugins/` and builds `mod/` (`PPGiOS.dll`) against them. The
   last run reported **1330 patches, 0 failures**. The patcher:
   * stubs Steam, the mod compiler and the TCP server;
   * turns P/Invokes into stubs and blocks `Process.Start`;
   * fixes Windows paths;
   * routes all keyboard and mouse reads through `PPGShim.VInput` (driven by touch);
   * routes vSync and targetFrameRate through the shim;
   * replaces `__makeref` (IL2CPP 2020.3 can't handle it);
   * redirects Ceras to `shim/CerasCompat.cs`, an AOT-safe reimplementation of the Ceras wire format. Real Ceras
     crashed under AOT in `Global.Awake`, which broke the camera and all input. Verified with `tools/cerastest`
     against real Ceras on 83 contraptions: 0 differences;
   * sets PPv2 `supportsComputeShaders` to false. This disables auto-exposure, MSVO and the 3D LUT, so lighting may
     differ slightly.
2. **CI** (`.github/workflows/build-shell.yml`, macos-14 runner, Xcode 15.4) checks out the private `Plugins/` using
   the secret `PRIVATE_REPO_TOKEN`. It then:
   * installs the Intel Unity 2020.3.1f1 editor plus the iOS module under Rosetta (cached);
   * activates Unity with the secrets `UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD`;
   * runs `shell/Assets/Editor/IosShellBuild.cs` for an IL2CPP Xcode project. Its post-process sets Info.plist keys
     including file sharing and `CADisableMinimumFrameDurationOnPhone` (120 Hz), and adds `-ld_classic` and
     `-Wno-error`;
   * runs `xcodebuild`, **unsigned**, and publishes `PPG-shell.ipa` to the **private** release `shell-latest`.
   * Trigger: a push to `shell/**`, `ci/**` or the workflow file, or
     `gh workflow run build-shell.yml --ref main` (run from inside the repo dir).
3. **`tools/convert_data.py`** (Python + UnityPy, locally on the PC) converts the retail data files:
   * changes the platform tag from Windows (19) to iOS (9);
   * translates DX11 shaders to Metal (`convert_shader.py`, `dx2metal.py`, `tools/hlslcc_cli`);
   * re-encodes BCn and crunch textures as ASTC 4x4;
   * copies the `.resS` and `.resource` files (audio).
   Output goes to `C:\Users\v0id\ppg-work\Data`.
4. **`tools/package_ipa.py`** builds the final IPA. It:
   * takes the shell IPA;
   * deletes its `level*`, `sharedassets*`, `resources.*` and `globalgamemanagers*`;
   * copies in the converted data;
   * merges `RuntimeInitializeOnLoads.json` (retail plus the shell's PPGiOS entries).
   Run: `python tools/package_ipa.py --work /c/Users/v0id/ppg-work --shell /c/Users/v0id/ppg-work/shell/PPG-shell.ipa`.
   The result (~371 MB) is uploaded to the private release `ipa-latest`.
5. **`mod/`** contains the in-game additions:
   * `Bootstrap.cs`: sets up Documents and makes it the current directory, so the game's relative paths land there;
   * `Log.cs`: log file plus spam collapsing;
   * `DebugOverlay.cs`: fps overlay and log viewer;
   * `Hud.cs`, `TouchInput.cs` and `TouchSim.cs`: touch controls and on-screen buttons;
   * `SafeArea.cs`.

### Status: be honest with the user about this

* Milestones 1 to 3 are done: the CI shell IPA works, the game boots and writes a log, and the main menu works.
* The latest build (commit `5b02468`, with the Ceras, audio, compute and log-spam fixes) was uploaded to `ipa-latest`.
  **It has not yet been tested on a device.** The previous log showed the Ceras crash, which that build fixes.
* Not done yet:
  * milestone 4: in a map, spawn, drag and select;
  * milestone 5: full touch controls;
  * milestone 6: save/load, sound and performance.
* Possible later work: port the compute shaders to Metal to bring back auto-exposure and MSVO.

## 4. What has to change for the new phone (suggested plan)

1. **Split the IPA from the data.**
   * Keep only the shell's own files in the app bundle's `Data/`:
     * IL2CPP `Managed/Metadata/global-metadata.dat`, which must match the binary;
     * `unity default resources`;
     * `RuntimeInitializeOnLoads.json`, `ScriptingAssemblies.json` and the like.
   * Zip the converted retail data (`level*`, `sharedassets*`, `resources.*`, `globalgamemanagers*`, `.resS`,
     `.resource`, `Resources/unity_builtin_extra`) as e.g. `PPG-data.zip`. Publish it to a **private** release only.
   * Change `package_ipa.py` (or add a mode) to produce both files.
2. **Make the player load its data from Documents.** Unity on iOS reads `Data/` from the bundle. Patch the generated
   Xcode trampoline from `IosShellBuild.OnPostprocess`, or with a script in CI after the Unity build.
   * The data-directory hook in 2020.3 is around `UnityDataBundleDir()` / `-[UnityFramework setDataBundleId:]`.
     **Verify the exact names in the generated `Classes/` and `UnityFramework` sources.** Don't guess.
   * Likely approach, in native code, before Unity starts:
     1. If `Documents/PeoplePlayground/Data/globalgamemanagers` is missing, show a native screen ("Put the game zip in
        Files → People Playground and unzip it", optionally with a document picker plus unzip).
     2. Otherwise copy or symlink the bundle's shell-only `Data/` files next to the user's data. Do it once, and again
        whenever the app version changes.
     3. Point the data dir there.
   * Keep save files separate from the game data folder so that re-unpacking a new data zip never deletes saves.
     Currently `Bootstrap.cs` uses `Application.persistentDataPath` = Documents.
   * The user's choice of folder layout inside Documents is theirs. Write the expected layout into the on-screen
     message and the README, and make the check tolerant (e.g. the zip unpacks into an extra nested folder).
3. **Signing / sideloading on the latest iOS.**
   * TrollStore does **not** work on current iOS. The user installs through AltStore or SideStore with their Apple ID,
     and AltServer runs on Windows. CI can keep producing an **unsigned** IPA: AltStore re-signs it.
   * Change the bundle id to something unique; free accounts can't use `nl.studiominus.*`-style ids registered
     elsewhere. Check that nothing relies on entitlements a free account can't have.
   * Explain the 7-day re-sign to the user. Saves survive refreshes as long as the bundle id stays the same.
4. **Toolchain for latest iOS.**
   * `targetOSVersionString` is 14.0, which is fine. The 16 Pro Max runs arm64e but loads arm64 apps.
   * The `macos-14` image and Xcode 15.4 may change or disappear. If CI breaks, check `ls -d /Applications/Xcode_*`
     first.
   * Unity 2020.3 has no Apple-silicon editor. That's why the editor is the Intel build running under Rosetta, so
     keep that.
   * **Risk:** if a future runner lacks Rosetta or an old enough Xcode, the IL2CPP output of 2020.3 may need more
     clang flags.
5. **120 Hz.** `CADisableMinimumFrameDurationOnPhone` is already set, and `shim/Platform.cs` sets `targetFrameRate`.
   Verify on the 16 Pro Max. Unity 2020.3's `UnityView` may need `preferredFramesPerSecond` = 120 on the display link
   in the trampoline.
6. **Safe area / Dynamic Island.** `SafeArea.cs` exists. Check it for the 16 Pro Max's landscape insets.
7. **Install steps for the user.** Every milestone needs exact steps:
   * download from the private release in Safari while logged in to GitHub, then install with AltStore;
   * download the data zip, then unzip it in Files into the app's folder;
   * where the logs are.

## 5. Practical notes for working on this PC

* There is no `jq`; use `gh --jq`. Run `gh` from inside a repo dir.
* The Bash cwd resets to the game dir after every call, so use absolute paths.
* Write Python to scratch files rather than inline. `python -I` breaks UnityPy, so don't use it for these tools.
* The dotnet output is in Russian locale.
* **The C: drive has only about 14 GB free.** Data conversion and IPA staging are large, so clean up `ppg-work/ipa_stage`.
* Work dir: `C:\Users\v0id\ppg-work`. It contains `Data/` (converted), `shell/PPG-shell.ipa` and `PeoplePlayground.ipa`.
* Local repos: `C:\Users\v0id\people-playground-ios-port` (public) and `C:\Users\v0id\ppg-ios-private` (private).
* Full history of the previous session (long):
  `C:\Users\v0id\.claude\projects\C--Program-Files--x86--Steam-steamapps-common-People-Playground\08bad424-761c-40d3-a6ea-4467e7c0e5cc.jsonl`.
