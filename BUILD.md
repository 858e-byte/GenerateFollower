# Building GenerateFollower

Since 1.0.0 the released ZIP contains a single program, `GenerateFollower.exe`, produced by `beta/make_package.ps1`
from this repository alone: no third-party tools have to be added.

## 1. Requirements

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the application is published self-contained: players don't
  need .NET)
- Python 3 with the `py` launcher, only for the packaging script's privacy check (`beta/check_privacy.py`) and to
  redraw the icon (`beta/make_icon_gf.py`, needs Pillow)
- Skyrim Special Edition (the application reads the game's load order when it runs; it is not needed to compile)

## 2. Compile and run (development)

```
dotnet build src -o work/bin
dotnet work/bin/GenerateFollower.dll          # opens the Follower Builder
dotnet work/bin/GenerateFollower.dll diag     # diagnostic report
```

A development build uses the `builder/`, `papyrus/` and `config.json` of the repository directly; the published exe
carries its own copy (`AppResources.cs`).

## 3. Papyrus scripts (optional)

The compiled scripts in `papyrus/pex/` are used as they are and are built into the exe. They are recompiled only when a
`.psc` is newer than its `.pex`, which needs the Creation Kit's Papyrus compiler
(`<game>/Papyrus Compiler/PapyrusCompiler.exe`), its `TESV_Papyrus_Flags.flg` in `deps/`, and the RaceMenu and
UIExtensions script sources in `deps/racemenu/scripts/source` and `deps/uiext/scripts/source` (not stored here). The
user name and computer name that the compiler writes into each `.pex` are removed (`Packaging.SanitizePex`).

## 4. Package

```
powershell -ExecutionPolicy Bypass -File beta/make_package.ps1
```

It checks that every `.pex` is up to date and that the files built into the exe contain no personal information, runs
`dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true -p:DebugType=None`, assembles
`work/publish/GenerateFollower/` (the exe, guides, changelog, `LICENSE.txt`, `LICENCES.md` and the source code in
`source/`), checks it again (`beta/check_privacy.py`) and writes `dist/GenerateFollower_<version>.zip`.

The only native DLL, `WebView2Loader.dll` (WebView2 SDK), is built into the exe and unpacked into `work/app/<version>/`
next to it instead of Windows' temporary folder. The window uses the WebView2 runtime already installed with Windows.

The executable is a Windows (GUI) application: with no argument it shows its own windows; command-line commands
(`build`, `scan`, `diag`…) write to the console they are started from (`ParentConsole.cs`).

The executable is built with `<CetCompat>false</CetCompat>` (in `src/GenerateFollower.csproj`). Since .NET 9 the app
host opts into CET (hardware-enforced stack protection, "shadow stack"); when Mod Organizer 2 starts the program, its
virtual file system (USVFS) hooks into the process and CET closed it at startup (access violation 0xc0000005, before any
of our code runs). The same code started through `dotnet.exe` was not affected. Other protections are unchanged: the
Builder's HTTP server listens on `localhost` only, API calls from other origins are refused (`Origin` /
`Sec-Fetch-Site` check in `Builder.cs`), static files cannot leave `builder/`, and the program never connects to the
internet. The WebView2 window runs in Microsoft's own `msedgewebview2.exe` processes, which keep their protections.
