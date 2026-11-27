#!/usr/bin/env python3
"""
Zero-Gradle builder for the lightweight Android Import-Only MOD Loader APK
(YGO-Rogue-ModLoader-Android.apk, < 100 KB, works on Android 8.0+ arm64/armv7/x86_64).

Works identically on Windows, Linux, macOS, and GitHub Actions (ubuntu-latest).
Requires only JDK (javac/jar/keytool) + Android SDK (aapt2, d8, zipalign, apksigner).
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
IS_WIN = os.name == "nt"


def find_sdk() -> Path:
    candidates = [
        os.environ.get("ANDROID_HOME"),
        os.environ.get("ANDROID_SDK_ROOT"),
        str(Path.home() / "AppData/Local/Android/Sdk") if IS_WIN else str(Path.home() / "Android/Sdk"),
        "/usr/local/lib/android/sdk",
    ]
    for c in candidates:
        if c and Path(c).is_dir():
            return Path(c)
    raise RuntimeError("Android SDK not found. Set ANDROID_HOME or install Android SDK build-tools + platforms.")


def find_highest_subdir(parent: Path, prefix: str = "") -> Path:
    if not parent.is_dir():
        raise RuntimeError(f"Missing Android SDK directory: {parent}")
    dirs = [p for p in parent.iterdir() if p.is_dir() and p.name.startswith(prefix)]
    if not dirs:
        raise RuntimeError(f"No matching entries under {parent}")

    def sort_key(p: Path):
        tail = p.name[len(prefix):]
        return tuple(int(x) if x.isdigit() else 0 for x in tail.replace("-", ".").split("."))

    dirs.sort(key=sort_key, reverse=True)
    return dirs[0]


def find_jdk_tool(name: str) -> str:
    ext = ".exe" if IS_WIN else ""
    java_home = os.environ.get("JAVA_HOME")
    if java_home:
        cand = Path(java_home) / "bin" / f"{name}{ext}"
        if cand.is_file():
            return str(cand)
    found = shutil.which(name)
    if found:
        return found
    raise RuntimeError(f"JDK tool '{name}' not found on PATH or JAVA_HOME.")


def run(cmd, cwd=ROOT):
    print("  $", " ".join(str(x) for x in cmd), flush=True)
    subprocess.run([str(x) for x in cmd], check=True, cwd=str(cwd))


def build_apk(version_name: str, out_apk: Path, keystore: Path | None, ks_pass: str, ks_alias: str):
    sdk = find_sdk()
    bt = find_highest_subdir(sdk / "build-tools")
    plat_dir = find_highest_subdir(sdk / "platforms", "android-")
    android_jar = plat_dir / "android.jar"
    target_api = plat_dir.name.replace("android-", "")

    ext = ".exe" if IS_WIN else ""
    aapt2 = bt / f"aapt2{ext}"
    zipalign = bt / f"zipalign{ext}"
    d8_jar = bt / "lib" / "d8.jar"
    apksigner_jar = bt / "lib" / "apksigner.jar"

    javac = find_jdk_tool("javac")
    jar_bin = find_jdk_tool("jar")
    java_bin = find_jdk_tool("java")
    keytool = find_jdk_tool("keytool")

    print(f"[APK Build] SDK={sdk} | build-tools={bt.name} | platform={plat_dir.name} | version={version_name}")

    build_dir = ROOT / "build"
    if build_dir.exists():
        shutil.rmtree(build_dir)
    classes_dir = build_dir / "classes"
    compiled_res = build_dir / "compiled-res"
    linked_res = build_dir / "linked-res"
    classes_dir.mkdir(parents=True)
    compiled_res.mkdir(parents=True)
    linked_res.mkdir(parents=True)

    # 1. Compile Java sources -> classes.jar -> classes.dex
    java_files = sorted((ROOT / "src").rglob("*.java"))
    run([javac, "-encoding", "UTF-8", "-source", "8", "-target", "8",
         "-classpath", android_jar, "-d", classes_dir, *java_files])
    classes_jar = build_dir / "classes.jar"
    run([jar_bin, "cf", classes_jar, "-C", classes_dir, "."])
    run([java_bin, "-cp", d8_jar, "com.android.tools.r8.D8",
         "--min-api", "26", "--lib", android_jar, "--output", build_dir, classes_jar])

    # 2. Compile & link Android resources via aapt2
    run([aapt2, "compile", "--dir", ROOT / "res", "-o", compiled_res])
    flats = sorted(compiled_res.glob("*.flat"))
    for folder in (ROOT / "res").rglob("*"):
        if folder.is_dir():
            (linked_res / "res" / folder.relative_to(ROOT / "res")).mkdir(parents=True, exist_ok=True)
    version_code = str(int(time.time() // 60))
    run([
        aapt2, "link", *flats,
        "-o", linked_res, "--output-to-dir",
        "--manifest", ROOT / "AndroidManifest.xml",
        "-I", android_jar,
        "--replace-version",
        "--version-code", version_code,
        "--version-name", version_name,
        "--target-sdk-version", target_api,
    ])

    # 3. Assemble unsigned APK (resources.arsc MUST be ZIP_STORED for Android 11+!)
    unsigned_apk = build_dir / "unsigned.apk"
    with zipfile.ZipFile(unsigned_apk, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for p in sorted(linked_res.rglob("*")):
            if p.is_file():
                rel = p.relative_to(linked_res).as_posix()
                comp = zipfile.ZIP_STORED if rel == "resources.arsc" else zipfile.ZIP_DEFLATED
                zf.write(p, rel, compress_type=comp)
        zf.write(build_dir / "classes.dex", "classes.dex")
        assets_root = ROOT / "assets"
        if assets_root.is_dir():
            for p in sorted(assets_root.rglob("*")):
                if p.is_file():
                    rel = "assets/" + p.relative_to(assets_root).as_posix()
                    zf.write(p, rel, compress_type=zipfile.ZIP_DEFLATED)

    # 4. zipalign -f 4
    aligned_apk = build_dir / "aligned.apk"
    run([zipalign, "-f", "4", unsigned_apk, aligned_apk])

    # 5. Sign & verify with apksigner
    ks_path = keystore or (build_dir / "modloader-debug.jks")
    if not ks_path.is_file():
        ks_path.parent.mkdir(parents=True, exist_ok=True)
        run([
            keytool, "-genkeypair", "-keystore", ks_path,
            "-storepass", ks_pass, "-keypass", ks_pass,
            "-alias", ks_alias,
            "-dname", "CN=YGO Rogue MOD Loader,O=Community,C=US",
            "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
        ])

    out_apk.parent.mkdir(parents=True, exist_ok=True)
    run([
        java_bin, "-jar", apksigner_jar, "sign",
        "--ks", ks_path, "--ks-key-alias", ks_alias,
        "--ks-pass", f"pass:{ks_pass}",
        "--out", out_apk, aligned_apk,
    ])
    run([java_bin, "-jar", apksigner_jar, "verify", "--verbose", out_apk])
    print(f"[APK Build] SUCCESS: {out_apk} ({out_apk.stat().st_size / 1024:.1f} KB)", flush=True)


def main():
    p = argparse.ArgumentParser(description="Build lightweight Android Import-Only MOD Loader APK")
    p.add_argument("--version", default="1.3.0", help="APK versionName (e.g. 1.3.0)")
    p.add_argument("--out", default=str(ROOT / "dist" / "YGO-Rogue-ModLoader-Android.apk"), help="Output signed APK path")
    p.add_argument("--keystore", default="", help="Optional existing JKS keystore path")
    p.add_argument("--storepass", default="ygomodloader", help="Keystore password")
    p.add_argument("--alias", default="ygomodloader", help="Keystore alias")
    args = p.parse_args()
    ks = Path(args.keystore) if args.keystore else None
    build_apk(args.version, Path(args.out).resolve(), ks, args.storepass, args.alias)


if __name__ == "__main__":
    main()
