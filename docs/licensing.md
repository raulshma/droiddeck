# Licensing

DroidDeck is free software distributed under the GNU General Public License,
version 3 (GPL-3.0-only). The full text is in [LICENSE](../LICENSE) and is copied
next to the executable as `LICENSE`.

You may run, study, share, and modify DroidDeck. If you distribute the app or a
modified version, the GPL-3.0 requires you to:

- make the complete corresponding source code available under GPL-3.0,
- keep the copyright and license notices intact, and
- state significant changes made to the software.

DroidDeck comes with no warranty, to the extent permitted by law.

## Third-party notices

DroidDeck uses third-party components. Their notices and license texts are in
[Licenses/THIRD-PARTY-NOTICES.md](../Licenses/THIRD-PARTY-NOTICES.md) and in the
`Licenses` folder of every build and portable publish.

| Component | License | Distribution |
| --- | --- | --- |
| .NET runtime | MIT | Bundled with the self-contained build |
| Microsoft Windows App SDK (WinUI) | Microsoft Software License Terms | Bundled with the self-contained build |
| Microsoft.Extensions.DependencyInjection, Logging | MIT | Bundled |
| System.Security.Cryptography.ProtectedData | MIT | Bundled |
| QRCoder | MIT | Bundled (wireless pairing QR codes) |
| scrcpy | Apache-2.0 | Not bundled; detected installation |
| adb, Android SDK, emulator | Android SDK / Android Studio terms | Not bundled; detected installation |
| JDK, Gradle, Flutter, Node.js, Unity, .NET SDK | Vendor licenses | Not bundled; detected installation |

The Windows App SDK license terms and its full third-party `NOTICE` are copied from
the NuGet package into the build and publish output as
`Licenses/WINDOWSAPPSDK-LICENSE.txt` and `Licenses/WINDOWSAPPSDK-NOTICES.txt`.

### scrcpy

Screen mirroring uses [scrcpy](https://github.com/Genymobile/scrcpy), which is
licensed under Apache-2.0 by Genymobile and contributors. DroidDeck invokes the
scrcpy you installed; it does not redistribute scrcpy binaries in the current build.
Any distribution that bundles scrcpy must also ship scrcpy's `LICENSE`, `NOTICE`,
version, and source link, as Apache-2.0 requires. See the upstream project for the
canonical terms.

### Android

Android, Android Studio, adb, and the Android SDK are products of Google LLC.
DroidDeck does not bundle them and is not affiliated with or endorsed by Google.
Android is a trademark of Google LLC.

## Plugins

Plugins are separate works with their own licenses. The catalog shows a plugin's
declared license, and the plugin package should include its license file. Because
plugins are loaded in-process and link against the GPL-3.0 plugin SDK, distributed
plugins must be GPL-3.0-compatible. Installing a plugin changes neither the license
of DroidDeck nor the license of the plugin.

## About the app

**About** shows the DroidDeck version, runtime and Windows App SDK versions, data
locations, and links to the license, notices, privacy, and security documents. The
same files are available in the repository:

- [LICENSE](../LICENSE)
- [Third-party notices](../Licenses/THIRD-PARTY-NOTICES.md)
- [Privacy](privacy.md)
- [Security](security.md)

## Contributions

By contributing code, documentation, or samples to this repository, you agree that
your contribution is licensed under the same GPL-3.0 terms. Do not contribute code
you cannot license under these terms.

## Questions

Open an issue at <https://github.com/raulshma/droiddeck/issues> for licensing
questions or to report a missing notice.
