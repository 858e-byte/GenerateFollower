# Third-party components / Composants tiers

GenerateFollower ships with these tools so that nothing else has to be installed. Each keeps its own license.
GenerateFollower itself is released under the GPL-3 (see `LICENSE.txt` and the `source` folder).

| Folder | Component | License | Source |
|---|---|---|---|
| `deps\python\` | CPython 3.14.7 (trimmed: no tests, tkinter, idlelib, ensurepip, turtledemo) with NumPy 2.5.3, Pillow 12.3.0, lz4 4.4.5 | PSF (`python\LICENSE.txt`); NumPy BSD-3; Pillow MIT-CMU (HPND); lz4 BSD-3 (texts in the `*.dist-info` folders) | python.org, numpy.org, python-pillow.org, github.com/python-lz4/python-lz4 |
| `deps\pynifly\` | PyNifly (`pyn` package and `NiflyDLL.dll`) by BadDog | GPL-3 (`pynifly\LICENSE.txt`) | github.com/BadDogSkyrim/PyNifly |
| `deps\7zip\` | 7-Zip (`7z.exe`, `7z.dll`) by Igor Pavlov | GNU LGPL + unRAR restriction (`7zip\License.txt`) | 7-zip.org |
| `deps\texconv\` | texconv (DirectXTex) by Microsoft | MIT (`texconv\LICENSE.txt`) | github.com/microsoft/DirectXTex |
| `deps\webview2\` (full package only, not in the Lite package) | Microsoft Edge WebView2 Runtime, Fixed Version 155.0.4283.62 (x64), unmodified, as downloaded from Microsoft and signed by Microsoft Corporation. It only shows GenerateFollower's own local window; SmartScreen is turned off, so nothing is checked with Microsoft. Not covered by the GPL-3 | Microsoft Software License Terms (`webview2\LICENSE.txt`) | Microsoft |
| `builder\vendor\three-0.160.0\` | three.js 0.160.0 | MIT (`LICENSE` in that folder) | threejs.org |
| built into `GenerateFollower.exe` | Mutagen (Mutagen-Modding), the .NET runtime and the WebView2 SDK (Microsoft.Web.WebView2) | Mutagen: GPL-3; .NET: MIT; WebView2 SDK: BSD-3-Clause (`WebView2SDK_LICENSE.txt`) | github.com/Mutagen-Modding/Mutagen, github.com/dotnet/runtime, nuget.org/packages/Microsoft.Web.WebView2 |

Compiled Papyrus scripts (`papyrus\pex\`) are built from the sources in `papyrus\` (same license as GenerateFollower).
