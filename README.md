# GenerateFollower

Turns Skyrim Special Edition RaceMenu presets (`.jslot`) into recruitable, standalone followers, with the exact face
RaceMenu shows (its *Export Head*). A Windows application with a local web interface, the **Follower Builder**, checks
every preset (a bit like the Creation Kit would), previews the head in 3D, lets you pick a skin per follower (UBE,
CBBE/3BA, HIMBO) and builds an ESL follower pack to install with Vortex or Mod Organizer 2.

Status: **0.11.0 beta** (consistency checks of the follower's choices, skins at their original size, the Builder in
its own window without Microsoft Edge, no console window). Two downloads, same program:
`GenerateFollower_<version>.zip` (own window, WebView2 engine included) and `GenerateFollower_<version>_Lite.zip`
(opens in your default web browser). Download: [Releases](https://github.com/858e-byte/GenerateFollower/releases) or
[Nexus Mods](https://www.nexusmods.com/skyrimspecialedition/mods/196050). Earlier versions: tags `0.9.0` and `0.10.0`;
the code of 0.9.0 is also kept in the `archive/0.9.0` branch.
For players, see `beta/README_BETA.txt` (English) or `beta/LISEZMOI_BETA.txt` (French).

## How it works

- `src/` - C# (.NET 10) application using [Mutagen](https://github.com/Mutagen-Modding/Mutagen) to read the load order
  and write the follower plugins. Entry point `Program.cs`; with no argument it shows a loading window, then the
  Follower Builder in its own WebView2 window, or in the default browser for the Lite package (`AppWindow.cs`), and
  serves `builder/` on localhost only (`Builder.cs`).
- `builder/` - the Follower Builder page (HTML/JS, three.js for the 3D preview) and its translations (`i18n/en.json`).
- `offline/` - Python scripts called by the application: NIF reading with
  [PyNifly](https://github.com/BadDogSkyrim/PyNifly), BSA extraction, textures, skin catalog…
- `papyrus/` - in-game scripts (summon power, menu, overlays) and their compiled `.pex`.
- `beta/` - packaging script, privacy check, player guides and changelog.

The released package also contains third-party tools in `deps/` (Python, PyNifly, 7-Zip, texconv, and in the full
package the Microsoft Edge WebView2 Runtime, Fixed Version), not stored in this repository: see [BUILD.md](BUILD.md).

## License

GPL-3 (see [LICENSE.txt](LICENSE.txt)), because GenerateFollower uses Mutagen and PyNifly, which are GPL-3.
Third-party components and their licenses: [deps/LICENCES.md](deps/LICENCES.md).

Modified versions must be clearly marked as modified and must not be presented as the original GenerateFollower.
Followers generated with the application contain assets from other mods and preset authors: they are not covered by
this license and are meant for personal use.
