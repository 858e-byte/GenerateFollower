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
contains no personal information (`beta/check_privacy.py`) and writes `dist/GenerateFollower_<version>.zip`.
