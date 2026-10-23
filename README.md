# GenerateFollower

Turns Skyrim Special Edition RaceMenu presets (`.jslot`) into recruitable, standalone followers, with the exact face
RaceMenu shows (its *Export Head*). A Windows application with a local web interface, the **Follower Builder**, checks
every preset (a bit like the Creation Kit would), previews the head in 3D, lets you pick a skin per follower (UBE,
CBBE/3BA, BHUNP, HIMBO) and builds an ESL follower pack to install with Vortex or Mod Organizer 2.

Status: **1.0.0 (V1, first stable version)** (1.0.0: a single program, `GenerateFollower.exe`, with nothing else to install: no more Python,
PyNifly, 7-Zip, texconv or bundled WebView2 engine, everything they did is now done in C#; one download instead of two,
the Builder opens in its own window with the WebView2 engine already in Windows, or in the default browser; lossless face
tint; voices played by Windows; skin archives read without 7-Zip; nothing written outside the program's folder; HIMBO
skins from an archive use the normal and specular maps of the same option, the race's face normal map, and are applied to
the genitals (TNG) too; 0.14.0: Mod Organizer 2 without going through MO2; 0.13.0: starting location; 0.12.0: Erin race
support, BHUNP skins). Download: [Releases](https://github.com/858e-byte/GenerateFollower/releases) or
[Nexus Mods](https://www.nexusmods.com/skyrimspecialedition/mods/196050). Earlier versions (betas): tags `0.9.0` to `0.14.0` (tag `0.15.0` = the code of 1.0.0 before it was renamed);
the code of 0.9.0 is also kept in the `archive/0.9.0` branch.
For players, see `beta/README.txt` (English) or `beta/LISEZMOI.txt` (French).

## How it works

- `src/` - C# (.NET 10) application using [Mutagen](https://github.com/Mutagen-Modding/Mutagen) to read the load order
  and write the follower plugins, [NiflySharp](https://github.com/ousnius/NiflySharp) for the `.nif` meshes,
  BCnEncoder.Net for the `.dds` textures, SharpCompress for skin archives and Vortice.XAudio2 for the voice samples.
  Entry point `Program.cs`; with no argument it shows a loading window, then the Follower Builder in its own WebView2
  window, or in the default browser if WebView2 is missing (`AppWindow.cs`), and serves `builder/` on localhost only
  (`Builder.cs`). The Builder pages, the compiled Papyrus scripts and `config.json` are built into the exe and unpacked
  once next to it, in `work/app/<version>/` (`AppResources.cs`).
- `builder/` - the Follower Builder page (HTML/JS, three.js for the 3D preview) and its translations (`i18n/en.json`).
- `papyrus/` - in-game scripts (summon power, menu, overlays) and their compiled `.pex`.
- `beta/` - packaging script, icon script, privacy check, player guides and changelog.

Up to 0.14 the application also shipped Python scripts (`offline/`) and third-party tools (`deps/`); they are no longer
used and were removed from this repository in 1.0.0 (they remain in the earlier tags).

## License

GPL-3 (see [LICENSE.txt](LICENSE.txt)), because GenerateFollower uses Mutagen and NiflySharp, which are GPL-3.
Third-party components and their licenses: [LICENCES.md](LICENCES.md).

Modified versions must be clearly marked as modified and must not be presented as the original GenerateFollower.
Followers generated with the application contain assets from other mods and preset authors: they are not covered by
this license and are meant for personal use.
