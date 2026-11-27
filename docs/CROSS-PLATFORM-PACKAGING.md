# Cross-Platform Packaging Plan (Windows / Linux / Android)

> **Notice**: This project and packaging architecture were created end-to-end with **Gemini AI** as an open-source technical reference and are not planned for long-term maintenance. Forks and community extensions are welcome!

This document covers **BOTH** cross-platform packaging dimensions:
1. **Packaging Modded Game Builds** for **Linux / Steam Deck** and **Android** (from inside the Desktop Studio or CLI).
2. **Packaging & Building this MOD Manager itself** for **Windows (`win-x64`)**, **Linux (`linux-x64`)**, and **Android (LAN PWA / Termux / WebView wrapper)**.

---

## Part 1: Packaging Modded Games for Linux & Android

### 1.1 Linux / Steam Deck (`POST /api/export/linux-bundle`)
Official Linux builds (`YGO-Rogue-Linux-x86_64.tar.gz`) use the **exact same `Game/<version>` bundle format** as Windows under:
- `$XDG_DATA_HOME/Yu-Gi-Oh! Rogue Duelist/Game/` (default: `~/.local/share/Yu-Gi-Oh! Rogue Duelist/Game/`)

In Desktop Studio (**Settings -> Cross-Platform Export -> Export Linux / Steam Deck Bundle**), the manager compiles all enabled MODs into:
- `exports/linux/YGO-Rogue-Linux-ModBundle-900.x.y.zip` containing:
  - `900.x.y.zip` + `900.x.y.sha256` (zero-touch virtual bundle with `-mod` identity tag)
  - `install-linux-mod.sh` (auto-detects `~/.local/share/Yu-Gi-Oh! Rogue Duelist/Game` and extracts cleanly)

Install on Linux / Steam Deck (Desktop Mode):
```bash
unzip YGO-Rogue-Linux-ModBundle-900.*.zip -d ygo-mod-bundle
cd ygo-mod-bundle
bash install-linux-mod.sh
```
To uninstall on Linux, simply delete `~/.local/share/Yu-Gi-Oh! Rogue Duelist/Game/900.*`.

---

### 1.2 Android Packaging (Two Complete Solutions)

#### Option A: Direct Offline Modded APK Packaging (`POST /api/export/android-bundle`)
1. Download the official `YGO-Rogue-Android.apk` from GitHub Releases.
2. In Desktop Studio (**Settings -> Cross-Platform Export -> Patch Official Android APK**), select the official APK.
3. Our `ModInjectionEngine.ExportAndroidBundleAsync` patches `assets/web/*` directly inside the APK:
   - Preserves `STORED` (uncompressed) entries required by Android (`resources.arsc`, `lib/**/*.so`).
   - Strips stale `META-INF/*.SF`, `*.RSA`, `*.DSA`, `*.EC`, `MANIFEST.MF` signature blocks.
   - Outputs `exports/android/YGO-Rogue-Android-Modded-unsigned.apk`.
4. Sign & zipalign before installing on Android:
   ```bash
   # Easiest cross-platform signer (uber-apk-signer):
   java -jar uber-apk-signer.jar --apks exports/android/YGO-Rogue-Android-Modded-unsigned.apk
   # Or Android SDK build-tools:
   zipalign -p -f 4 YGO-Rogue-Android-Modded-unsigned.apk YGO-Rogue-Android-Modded-aligned.apk
   apksigner sign --ks my-release.keystore --out YGO-Rogue-Android-Modded.apk YGO-Rogue-Android-Modded-aligned.apk
   ```
   *(Or sign directly on the phone using MT Manager / APK Signer!)*

#### Option B: Portable `assets/web` Overlay + Python Patcher
If you don't have the APK handy yet, click **Export Android Overlay (`assets/web`)** to export `YGO-Rogue-Android-Overlay-*.zip` containing all patched `assets/web/*` files + a standalone `patch-android-apk.py` script that can patch any future APK on Windows/Linux/Termux.

#### Option C: Zero-Resign Android LAN PWA (Instant Play & Live Hot-Reload!)
1. Start **Hot-Reload** in Desktop Studio (or run Desktop binary with `--serve`).
2. Open the displayed LAN URL (`http://<PC-LAN-IP>:17845/`) in Android Chrome / Edge.
3. Tap **Add to Home Screen** — runs full-screen with instant live MOD edits and zero APK re-signing!

---

## Part 2: Packaging THIS MOD Manager for Windows, Linux & Android

### 2.1 Windows (`win-x64`) & Linux (`linux-x64`) — Native AOT CI/CD Matrix
All 4 projects (`Core`, `CLI`, `Mcp`, `Desktop`) target cross-platform **`net10.0`** with zero hard Win32 dependencies outside `OperatingSystem.IsWindows()` guards:
- `CardArtEncoder.cs`: Uses Win32 GDI+ on Windows, and zero-dependency raw image byte encoding on Linux/macOS/Android.
- `Desktop/Program.cs`: Uses frameless Win11 DWM chrome on Windows, and native GTK/WebKitGTK window chrome on Linux (`Photino.NET`).
- `.github/workflows/release.yml` automatically builds **both** `win-x64` (`.zip`) and `linux-x64` (`.tar.gz`) Native AOT packages for GUI, CLI, and MCP on every release!

#### Local Linux Build (Ubuntu / Debian / SteamOS / Fedora)
```bash
# 1. Build React + CodeMirror 6 frontend (embeds into Desktop/wwwroot/)
cd frontend && pnpm install --frozen-lockfile && pnpm build && cd ..

# 2. Install Linux Native AOT build tools (Ubuntu/Debian)
sudo apt-get install -y clang zlib1g-dev libwebkit2gtk-4.1-dev

# 3. Publish Native AOT linux-x64 binaries
dotnet publish YGORogueDuelistCardModInstaller.Desktop -c Release -r linux-x64 -o out/linux/gui
dotnet publish YGORogueDuelistCardModInstaller         -c Release -r linux-x64 -o out/linux/cli
dotnet publish YGORogueDuelistCardModInstaller.Mcp     -c Release -r linux-x64 -o out/linux/mcp

# 4. Run Desktop GUI (needs WebKitGTK) OR Headless Web Studio UI (works everywhere!)
./out/linux/gui/YGORogueDuelistCardModInstaller.Desktop          # GTK + WebKitGTK window
./out/linux/gui/YGORogueDuelistCardModInstaller.Desktop --serve  # Open http://127.0.0.1:17850/ in browser
```

#### Optional Linux AppImage / `.desktop` Packaging Blueprint
To wrap `out/linux/gui` into a single-file `.AppImage`:
```text
AppDir/
├── AppRun                                 # exec "$HERE/usr/bin/YGORogueDuelistCardModInstaller.Desktop" "$@"
├── ygo-mod-studio.desktop                 # Type=Application; Categories=Game;Utility;
├── ygo-mod-studio.png
└── usr/bin/
    ├── YGORogueDuelistCardModInstaller.Desktop
    └── libPhotino.Native.so
```
Build with `appimagetool AppDir YGO-Rogue-ModStudio-x86_64.AppImage`.

---

### 2.2 Android Import-Only MOD Loader APK (`android-loader/`)

We provide a ready-to-build, **zero-Gradle pure-Java Android Import-Only MOD Loader APK** under [`android-loader/`](../android-loader/) (`package="com.ygorogue.modloader"`):
- **Ultra-lightweight (< 100 KB APK)**: Pure Java + Android System WebView + Android's built-in `SQLiteDatabase` (for `.ypk` `.cdb` parsing) — installs side-by-side with the official `com.shadowrun.game` APK without overwriting saves!
- **Import-Only Mobile UX**:
  - Downloads latest official `web-*.zip` (direct or `ghfast.top` mirror) OR imports a local `web-*.zip` offline.
  - Imports **`.yrdpk`**, **`.zip`** (Mod Spec v1, Workspace Modpacks, OR Desktop Studio Android Overlay `.zip`), and **`.ypk`** (YGOPro/MDPro3 expansions) via Android's native Scoped Storage file picker.
  - Enables/disables, reorders load priority, applies MODs via **Zero-Copy Overlay Serving** (`filesDir/game/overlay/` -> fallback to `vanilla-web.zip`), restores clean vanilla in 1 tap, and launches the full-screen game with the complete `ShadowNative` bridge (`AtomicFile` saves, vibration, orientation, deck image export).
- **Zero-Gradle Local & CI Build**:
  ```bash
  # Requires only JDK + Android SDK (aapt2, d8, zipalign, apksigner) — auto-detected!
  python android-loader/build_apk.py --version 1.3.0 --out android-loader/dist/YGO-Rogue-ModLoader-Android.apk
  ```
- **Automated GitHub Actions Build**: `.github/workflows/release.yml` automatically builds and signs **`YGO-Rogue-ModLoader-Android-<tag>.apk`** on `ubuntu-latest` and publishes it right alongside the Windows (`.zip`) and Linux (`.tar.gz`) release archives!

#### Alternative Android Modes
1. **Android Termux (`linux-arm64` `--serve` Web Studio)**: Run `./YGORogueDuelistCardModInstaller.Desktop --serve` inside Termux/proot-distro and open `http://127.0.0.1:17850/` if you also want the full editor on-device.
2. **LAN Hot-Reload PWA**: Start Hot-Reload on PC and open `http://<PC-IP>:17845/` in Android Chrome.

---

## Part 3: Standalone `web-*.zip` Mode (Play & Update Without Original `.exe`)

Even if you have **never installed** `YGO-Rogue-Windows-Setup.exe`:
1. Click **No EXE Needed · Download `web-.zip` & Play** in the Onboarding Wizard, Top Banner, Live Preview tab, or Update Center.
2. The manager downloads only `web-X.Y.Z.zip` into `Game/` (and uses **~5.5 MB `.delta.gz` incremental patches** for all future updates via `DeltaPatchService.cs`!).
3. Click **Start Hot-Reload** or **Apply & Launch** — with no `.exe` present, the manager automatically serves the full game (with all your MODs composed in-memory!) on `http://127.0.0.1:17845/` inside the Embedded Preview tab or your default browser!
