GenerateFollower 0.15.0-beta - beta
==================================

Turns your RaceMenu presets (.jslot) into recruitable followers (one ESL plugin per pack), with the exact face that
your RaceMenu shows. Everything happens outside the game, in the "Follower Builder".

Thanks for testing! This version has been validated in game on ONE setup (Vortex, 3BA, HIMBO, High Poly Head, UBE),
then with a BHUNP body and the Erin race. Your feedback tells us whether it works elsewhere.


1. Requirements
---------------
- Windows 10 or 11 (64-bit). Nothing else to install: GenerateFollower.exe is a single program that contains
  everything it needs.
- The Builder opens in its own window, with the WebView2 engine Windows already has (Windows 11, up-to-date Windows 10;
  Microsoft Edge is not needed). If it is missing, the Builder opens in your default web browser, whichever it is: a
  small GenerateFollower window then stays open, closing it stops the application.
- Skyrim Special Edition / Anniversary Edition, with in game: SKSE, RaceMenu, PapyrusUtil, UIExtensions.
- For UBE presets: UBE (UBE_AllRace.esp). For vanilla races: preferably High Poly Head.
- For Erin presets: the Erin Race mod (ErinRace.esm and its SKSE plugin), also needed in game for those followers.
- BHUNP body: also install BHUNP feet and hands (BodySlide: "BHUNP 3BBB Advanced Feet", "BHUNP 3BBB Advanced Hands
  Ver 3"), otherwise a black band shows at the ankles with a BHUNP skin.
- Optional, only as a fallback: 7-Zip (for a skin archive the program could not read) and ffmpeg (in the PATH, if
  Windows cannot play a voice sample). Normally, neither is needed.


2. Installation
---------------
- Unzip the folder anywhere (avoid "Program Files"), for example D:\Tools\GenerateFollower.
  DON'T RUN GenerateFollower.exe FROM INSIDE THE OPEN ZIP: Windows would run it from a temporary folder and your
  settings, reviews and packs would be lost. Right-click the ZIP > "Extract All…" first.
- Vortex: double-click GenerateFollower.exe.
- Mod Organizer 2: double-click GenerateFollower.exe too (no need to start it from MO2 anymore). In the Builder's
  Settings, choose "MO2" and enter your instance's ModOrganizer.ini file (or its "mods" folder), then restart the
  application: your mods are read directly in the MO2 folder, which is never modified. Starting it from MO2
  (Executables) still works. Started from MO2, if the Builder window closes and it opens in your browser instead: in
  MO2, add msedgewebview2.exe to the blacklist (Settings > Workarounds > Executables Blacklist); if your browser is
  Edge, add msedge.exe too.
- The first start takes one or two minutes (reading the load order and the presets). The Builder opens in its own
  window; closing it stops the application. The language follows Windows (Settings > Language to change it).
  Linux / Steam Deck (Proton), or if the window does not open: run "GenerateFollower.exe app --browser" (the Builder
  opens in your browser).
- The application is portable: it NEVER changes the game folder and only writes in its own folder (settings.json,
  work\, dist\; on the first start it also unpacks its pages and scripts into work\app\). Put it on any drive you like.
  Exception: if its folder is not writable (Program Files), the settings go to %LOCALAPPDATA%\GenerateFollower.


2b. Settings: good to know
--------------------------
- Almost everything is detected on its own: game folder (Steam/GOG), Vortex and its mods, body and head.
- A grey value starting with "auto:" is the detected value: the field is actually EMPTY. Leave it empty to keep it,
  or type your own path to replace it.
- Under each field, "Used" shows the value really used; "Not found" in red = needs fixing.
- Nothing is kept until you click "Save" (or press Enter). Changing the archives or mods folder rebuilds the skin
  catalog right away (a few seconds the first time).
- Archives folder: enter it if your mod manager downloads somewhere else than its standard folder (Vortex:
  Settings > Downloads). Otherwise you won't see skins you downloaded but didn't install.
- MO2: choose MO2 and enter your instance's ModOrganizer.ini file or "mods" folder (portable instance: next to
  ModOrganizer.exe); empty profile = the one selected in MO2. "MO2 mode": keep Automatic (mods read in the MO2 folder;
  started from MO2, Data is enough). After a change, restart the application.
- Game folder: only if the game is not found (copied install…), then restart the application.
- 7-Zip: leave empty (an installed 7-Zip is detected on its own, and only useful as a fallback).


3. How to use (short)
---------------------
1) In game, RaceMenu: load a preset, then "Export Head" with the EXACT name of the preset.
2) In the Builder: "Rescan", open the preset; the verdict says whether it is ready (green), needs fixing (orange) or
   misses a part (red). Follow the advice shown.
3) "Approve" the followers you want, then "Build a pack of approved followers": a ZIP is created in dist\.
4) Install this ZIP with your mod manager like any mod. In game: power "Summon: <pack name>".
   Test on a NEW save or a test save.


3b. Customizing a follower (new in 0.10)
----------------------------------------
On a follower's sheet, everything is optional (without a choice, the follower stays as before):
- Archetype: one click sets combat style, class, outfit, weapons, spells and perks (warrior, archer, assassin, mages…).
- Combat (style and class), Voice (with the available follower dialogue and a "Listen" button), Outfit, Starting
  weapons, Spells, Perks, Advanced settings (level, essential, behavior, height), Marriage, Starting location.
- "Several followers": copy choices or an archetype to every preset shown in the list.
A follower only casts spells from the schools they are good at: also pick a mage class.
New in 0.11: the "Consistency of choices" box flags what will not work (archer without a bow, mage without spells…).
New in 0.11: the Builder window no longer needs Microsoft Edge (WebView2 engine included), and the console window is
replaced by a loading window. Lite version: the Builder in your browser, smaller download.
New in 0.12: Erin race (Erin config and skin for each follower), BHUNP skins, and Erin in BHUNP with the rest of the game
in 3BA (each follower gets the skins of its body). For an Erin, keep "Game skin" or her Erin skin: a realistic skin
doesn't match her anime head.
A pack takes your game's body (3BA, BHUNP…): build your followers on the game where you will use them.
New in 0.13: starting location. Pick in the preset's sheet where the follower waits for you (inn, shop, temple, palace,
town square): they are there from the start of the game, or as soon as you install the pack. Summoning still works.
New in 0.14: Mod Organizer 2 without going through MO2. GenerateFollower reads your mods directly in the MO2 folder
(priorities, enabled mods, overwrite, the profile's plugin order), like the game started from MO2.
New in 0.15: a single program, GenerateFollower.exe, with no Python or tools shipped next to it (75 MB package instead of
400 MB, no more Lite version). Lossless face tint, voices played by Windows (no ffmpeg), skin archives read without
7-Zip. HIMBO skins taken from an archive (BnP male…) are more consistent, genitals (TNG) included.

Updating from 0.9.0: unzip the new version over the old one (your settings, approvals and choices are kept), then rebuild
your packs. Since 0.15, the old deps\, offline\, builder\ and papyrus\ folders are no longer used: you can delete them. Uninstall the old pack before installing the new one: copied files moved to a new folder
(meshes\GenerateFollower\, textures\GenerateFollower\). Saves stay valid.


4. What we'd like to know
-------------------------
- Your setup: mod manager (Vortex / MO2), body (3BA, CBBE, BHUNP…), head (HPH, vanilla…), UBE or not.
- Does the Builder start? Are the messages clear?
- In game, for 2 or 3 followers: is the face the same as in RaceMenu? Same face / body color? Hair, eyes, brows
  right? No grey face or purple texture?
- Does a skin chosen in the Builder apply?
- Any crash, error message or odd behavior.


5. Sending feedback
-------------------
- In the Builder: Settings > "Diagnostic report". A GF_Diagnostic_<date>.zip file is created in dist\ (setup, tools,
  presets, latest errors; your Windows user name is hidden). Nothing is sent automatically: you attach it yourself.
- If GenerateFollower stops on an error, it writes crash.txt next to GenerateFollower.exe (otherwise in
  %LOCALAPPDATA%\GenerateFollower): attach it too. If it doesn't open at all, not even the loading window, check your
  antivirus quarantine and, in GenerateFollower.exe's Properties, tick "Unblock" if the box is there (downloaded,
  unsigned file).
- Please add screenshots too: RaceMenu and in game, for the same follower.
- Where to send it: on the mod's Nexus page, "Bugs" tab (problem) or "Posts" tab (opinion). Nexus doesn't allow
  attachments: paste the content of diagnostic.txt (inside the ZIP) into your message, or link to the ZIP (Google
  Drive, Dropbox…). For screenshots, an imgur link or the mod's "Images" tab.


6. Known limitations
--------------------
- The face comes from RaceMenu's "Export Head": a preset without an export can't be built.
- Hair with physics (SMP) becomes static on followers.
- Validated in game with 3BA + HIMBO + High Poly Head (+ UBE), with BHUNP and with the Erin race; other bodies (CBBE
  without 3BA, UUNP, vanilla) are detected but untested.
- In one game, normal characters all share the same body (3BA OR BHUNP): only races with their own body (UBE, Erin) can
  have another one.
- No more console window: a small loading window appears, then the Builder. The startup log is in work\app.log
  (or %LOCALAPPDATA%\GenerateFollower\app.log). The build reports are in French.


7. Important: personal use
--------------------------
A generated follower contains files from other mods (hair, eyes, skins, makeup…) and the work of preset authors.
Keep the generated ZIPs for YOUR game: don't publish them (Nexus, etc.) without those authors' permission.

GenerateFollower is distributed under the GPL-3 license (it uses Mutagen and NiflySharp, both GPL-3): text in
LICENSE.txt, source code in the source\ folder and on https://github.com/858e-byte/GenerateFollower. Components built
into the program and their licenses: LICENCES.md.
