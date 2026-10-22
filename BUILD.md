# Building GenerateFollower

The released ZIP is produced by `beta/make_package.ps1` from this repository plus a few third-party tools placed in
`deps/` (they are not stored here; all are redistributed under their own licenses, see `deps/LICENCES.md`).

## 1. Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the application is published self-contained: players don't
  need .NET)
- Python 3.14 with the `py` launcher (used by the packaging script)
- Skyrim Special Edition (the application reads the game's load order when it runs; it is not needed to compile)

## 2. Third-party tools in `deps/`

| Folder | What to put there | Source |
|---|---|---|
| `deps/python/` | A copy of CPython 3.14 for Windows x64 (`python.exe`, `python3.dll`, `python314.dll`, `vcruntime140*.dll`, `DLLs/`, `Lib/` without `test`, `idlelib`, `tkinter`, `turtledemo`, `ensurepip`) with `numpy`, `Pillow` and `lz4` installed in `Lib/site-packages` | python.org, then `python -m pip install numpy pillow lz4` |
| `deps/pynifly/` | `pyn/` package and `NiflyDLL.dll` from the PyNifly Blender add-on (`io_scene_nifly`) | github.com/BadDogSkyrim/PyNifly (releases) |
| `deps/7zip/` | `7z.exe`, `7z.dll`, `License.txt` | 7-zip.org |
| `deps/texconv/` | `texconv.exe` (and its `LICENSE.txt`) | github.com/microsoft/DirectXTex (releases) |
| `deps/webview2/` | Full package only: the Microsoft Edge WebView2 Runtime, **Fixed Version**, x64, extracted unmodified from Microsoft's `.cab` (`expand <file>.cab -F:* <folder>`, then rename the folder to `webview2`), with the license text from Microsoft's download page as `LICENSE.txt` | Microsoft's WebView2 download page, "Fixed Version" section (get it from Microsoft directly) |
| `offline/pylib/` | `lz4` package (same version as above), for running the scripts with another Python during development | `python -m pip install lz4 --target offline/pylib` |

The packaging script removes unused parts of Python (`pythonw.exe`, `venv`, `sqlite3`, `ssl`, `libtommath`).

## 3. Compile and run (development)

```
dotnet build src -o work/bin
dotnet work/bin/GenerateFollower.dll          # opens the Follower Builder
dotnet work/bin/GenerateFollower.dll diag     # diagnostic report
```

## 4. Papyrus scripts (optional)

The compiled scripts in `papyrus/pex/` are used as they are. They are recompiled only when a `.psc` is newer than its
`.pex`, which needs the Creation Kit's Papyrus compiler (`<game>/Papyrus Compiler/PapyrusCompiler.exe`), its
`TESV_Papyrus_Flags.flg` in `deps/`, and the RaceMenu and UIExtensions script sources in
`deps/racemenu/scripts/source` and `deps/uiext/scripts/source`. The user name and computer name that the compiler
writes into each `.pex` are removed (`offline/sanitize_pex.py`).

## 5. Package

```
powershell -ExecutionPolicy Bypass -File beta/make_package.ps1
```

It runs `dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None`, assembles `work/publish/GenerateFollower/`
(executable, `config.json`, `builder/`, `offline/`, `papyrus/`, `deps/`, guides, licenses, sources), checks that it
contains no personal information (`beta/check_privacy.py`) and writes two ZIPs: `dist/GenerateFollower_<version>.zip`
(full) and `dist/GenerateFollower_<version>_Lite.zip` (same files without `deps/webview2/`).

The executable is a Windows (GUI) application: with no argument it shows its own windows; command-line commands
(`build`, `scan`, `diag`…) write to the console they are started from (`ParentConsole.cs`).

The executable is built with `<CetCompat>false</CetCompat>` (in `src/GenerateFollower.csproj`). Since .NET 9 the app
host opts into CET (hardware-enforced stack protection, "shadow stack"); when Mod Organizer 2 starts the program, its
virtual file system (USVFS) hooks into the process and CET closed it at startup (access violation 0xc0000005, before any
of our code runs). The same code started through `dotnet.exe` was not affected. Other protections are unchanged: the
Builder's HTTP server listens on `localhost` only, API calls from other origins are refused (`Origin` /
`Sec-Fetch-Site` check in `Builder.cs`), static files cannot leave `builder/`, and the program never connects to the
internet. The WebView2 window runs in Microsoft's own `msedgewebview2.exe` processes, which keep their protections.
