# Third-party components / Composants tiers

GenerateFollower is a single program, `GenerateFollower.exe`: nothing else is shipped or has to be installed (no Python,
no 7-Zip, no texconv, no WebView2 runtime). The components below are built into the exe; each keeps its own license.
GenerateFollower itself is released under the GPL-3 (see `LICENSE.txt`; source code: https://github.com/858e-byte/GenerateFollower).

GenerateFollower est un seul programme, `GenerateFollower.exe` : rien d'autre n'est livré ni à installer. Les composants
ci-dessous sont intégrés à l'exe ; chacun garde sa licence. GenerateFollower est sous GPL-3 (`LICENSE.txt` ; sources :
lien ci-dessus).

| Component | Used for | License | Source |
|---|---|---|---|
| .NET runtime (Microsoft) | the program itself | MIT | github.com/dotnet/runtime |
| Mutagen (Mutagen-Modding) and its dependencies (Loqui, Noggog.CSharpExt, GameFinder, K4os LZ4, SharpZipLib…) | reading plugins, writing the follower plugin, reading BSA archives | GPL-3 (Mutagen); MIT / Apache-2.0 (dependencies) | github.com/Mutagen-Modding/Mutagen |
| NiflySharp (NuGet `Nifly`) by ousnius, with miniball | reading .nif meshes | GPL-3 (NiflySharp); Apache-2.0 (miniball) | github.com/ousnius/NiflySharp |
| BCnEncoder.Net, with CommunityToolkit.HighPerformance | reading and writing .dds textures | MIT or Unlicense; MIT | github.com/Nominom/BCnEncoder.NET |
| SharpCompress | listing and extracting skins from .zip / .7z / .rar archives | MIT | github.com/adamhathcock/sharpcompress |
| Vortice.XAudio2, with SharpGen.Runtime and Vortice.Mathematics | playing voice samples through Windows (XAudio2, part of Windows) | MIT | github.com/amerkoleci/Vortice.Windows |
| WebView2 SDK (Microsoft.Web.WebView2, including `WebView2Loader.dll`) | the Builder window, using the WebView2 runtime already in Windows | BSD-3-Clause | nuget.org/packages/Microsoft.Web.WebView2 |
| three.js 0.160.0 | 3D preview in the Builder | MIT (text in `work\app\…\builder\vendor\three-0.160.0\LICENSE`) | threejs.org |

Files written by the program: only in its own folder (`work\`, `dist\`, `settings.json`). The Builder pages, compiled
Papyrus scripts and `WebView2Loader.dll` are unpacked once into `work\app\<version>\`.

Optional tools, not shipped, used only if already installed: 7-Zip (an archive the program cannot read) and ffmpeg (voice
sample when Windows cannot play it).

Compiled Papyrus scripts are built from the sources in the `papyrus` folder of the source code (same license as
GenerateFollower).
