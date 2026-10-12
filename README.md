# People Playground iOS port (TrollStore)

Runs the retail Steam build of People Playground natively on iPhone/iPad. It does not rebuild the game:
the game's own assemblies are patched and AOT-compiled into an IL2CPP player built with the game's exact
Unity version (2020.3.1f1), and the game's own data files are converted for Metal and dropped in.

* `tools/patcher` – Mono.Cecil patcher for the retail assemblies: Steam never starts, the mod compiler /
  TCP server never starts, P/Invokes become stubs, Process.Start is blocked, Windows paths are fixed,
  every keyboard/mouse read goes through `PPGShim.VInput` (driven by touch).
* `shim/` – `PPGShim.dll`: virtual input + platform stand-ins the patched game calls.
* `mod/` – `PPGiOS.dll`: touch controls, on-screen buttons, log file + log viewer, safe-area UI.
* `shell/` – minimal Unity 2020.3.1f1 project; CI builds the unsigned shell IPA from it.
* `tools/build_plugins.py` – builds shim + mod and runs the patcher (output goes to the private repo
  `ppg-ios-private`, which holds the game-derived files and the IPA releases; nothing game-derived is
  committed here).
