# Third-Party Notices

StudioMCP is distributed under the repository's AGPL-3.0 license. Unmodified dependencies retain
their own licenses. See the included `LICENSE`, `licenses` directory and its provenance registry.

| Dependency family | Published package metadata | Included notice |
| --- | --- | --- |
| ModelContextProtocol / Core 2.3.0 | Apache-2.0 | Pinned SDK LICENSE, including its transition/MIT notice, and THIRD-PARTY-NOTICES |
| Microsoft.Extensions / System packages | MIT | Microsoft .NET and Extensions licenses; package-provided notices copied into release archives |
| Json.Net.Unity3D 9.0.1 | Upstream MIT license URL | Json.Net.Unity3D license, including Newtonsoft.Json attribution |
| WebSocketSharp 1.0.3-rc11 | Upstream MIT license URL | WebSocketSharp license |

Each release component includes `licenses/nuget-packages.json` listing the package IDs, exact
versions, authors and license metadata associated with its shipped DLL names. Package-provided
notice files are copied without rewriting their text. Historical packages without a repository
commit use their published license URL and the pinned license blob recorded in `provenance.json`.
This inventory is not a replacement for those license texts.

The host is framework-dependent: the .NET runtime is obtained separately, not redistributed in
these archives. Proprietary game/Unity assemblies, BepInEx/API/ME/Screencap installations and optional
rendering-provider DLLs are not bundled. Their own distribution terms remain separate.
