# Third-party notices

FATX Bridge depends on software maintained by other projects. Their licenses apply independently of the FATX Bridge MIT License.

## WinFsp and winfsp.net

FATX Bridge references `winfsp.net` version 2.1.25156 and requires an installed WinFsp runtime. The recommended FATX Bridge installer redistributes the official, unmodified `winfsp-2.1.25156.msi` and installs it only when WinFsp is missing.

Copyright 2015–2026 Bill Zissimopoulos and WinFsp contributors.

WinFsp is distributed under the GNU General Public License version 3 with a special exception for Free/Libre and Open Source Software. FATX Bridge is free software under the MIT License. Review WinFsp's complete current terms before distributing WinFsp binaries with FATX Bridge:

- Project: https://github.com/winfsp/winfsp
- License: https://github.com/winfsp/winfsp/blob/master/License.txt

The WinFsp runtime is not maintained by the FATX Bridge project.

## Microsoft .NET

The self-contained FATX Bridge packages redistribute components of the Microsoft .NET 10 runtime.

Copyright Microsoft Corporation and .NET contributors.

.NET runtime components are distributed under the MIT License. Source and license information:

- Project: https://github.com/dotnet/runtime
- License: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## Design references

The following MIT-licensed projects were consulted as FATX format design references. They are not runtime dependencies and their source was not copied into FATX Bridge.

- `emoose/xbox-winfsp`: https://github.com/emoose/xbox-winfsp
- `aerosoul94/FATXTools`: https://github.com/aerosoul94/FATXTools

The original Xbox fixed-layout, XBPartitioner, memory-unit, and timestamp behavior was also checked against public format documentation and the GPL-2.0 `mborgerson/fatx` implementation. No source from that project is included in FATX Bridge.

- Original Xbox hard-drive layout: https://xboxdevwiki.net/Hard_Drive
- Original Xbox memory units: https://xboxdevwiki.net/Xbox_Memory_Unit
- XBPartitioner table structure: https://xboxdevwiki.net/Xbox_Linux_Issues
- `mborgerson/fatx`: https://github.com/mborgerson/fatx
