# Relvyn Third-Party Notices

Snapshot date: 2026-08-11. This file is generated from the resolved production dependency graphs and manually reviewed build contents. It is not legal advice. Third-party copyrights remain with their respective owners.

The Relvyn proprietary `LICENSE` does not apply to the components listed here. When a package contains a more specific notice, that package notice controls. Complete unmodified license snapshots are under [`licenses/third-party/`](licenses/third-party/).

## WhatsApp Bridge GPL-3.0 distribution route

`@whiskeysockets/baileys@7.0.0-rc13` imports `libsignal@6.0.0`, which declares **GPL-3.0**. The complete Windows Bridge combination is therefore distributed as a separate GPL-3.0-only companion executable rather than as proprietary embedded code.

Each Windows release must publish `AI-Sales-OS-WhatsApp-Bridge-VERSION-source.zip` beside the installer and update packages. That archive contains the exact Bridge source, build scripts, resolved dependency source trees, lockfile, GPL text, installation information and SHA-256 manifest. The desktop app accepts a user-built replacement through `AI_SALES_OS_WHATSAPP_BRIDGE_PATH`. See [`docs/BRIDGE_GPL_COMPLIANCE.md`](docs/BRIDGE_GPL_COMPLIANCE.md).

The release compliance gate fails if the Bridge is embedded into `AISalesOS.exe`, if the corresponding-source archive is absent, or if the replacement mechanism and license notices are removed. This is an engineering compliance route and is not specialist legal advice.

## License text index

| File | Covers |
|---|---|
| [`DOTNET-8.0.29-LICENSE.txt`](licenses/third-party/DOTNET-8.0.29-LICENSE.txt) and [`DOTNET-8.0.29-THIRD-PARTY-NOTICES.txt`](licenses/third-party/DOTNET-8.0.29-THIRD-PARTY-NOTICES.txt) | Self-contained .NET runtime and Microsoft runtime notices |
| [`NODEJS-22.23.1-LICENSE.txt`](licenses/third-party/NODEJS-22.23.1-LICENSE.txt) | Node.js runtime and the notices distributed with Node.js |
| [`BRIDGE-NCC-THIRD-PARTY-LICENSES.txt`](licenses/third-party/BRIDGE-NCC-THIRD-PARTY-LICENSES.txt) | License texts emitted by the actual `ncc` Bridge bundle |
| [`LIBSIGNAL-6.0.0-GPL-3.0.txt`](licenses/third-party/LIBSIGNAL-6.0.0-GPL-3.0.txt) | `libsignal@6.0.0` GPL-3.0 text; not sufficient by itself to satisfy source obligations |
| [`SHARP-WIN32-X64-0.35.3-LICENSE.txt`](licenses/third-party/SHARP-WIN32-X64-0.35.3-LICENSE.txt) | sharp Windows native package Apache-2.0/LGPL-3.0-or-later terms |
| [`BSD-3-CLAUSE-PROTOBUFJS-7.6.5.txt`](licenses/third-party/BSD-3-CLAUSE-PROTOBUFJS-7.6.5.txt) | protobufjs BSD-3-Clause text |
| [`BLUEOAK-LRU-CACHE-11.5.2.md`](licenses/third-party/BLUEOAK-LRU-CACHE-11.5.2.md) | lru-cache BlueOak-1.0.0 text |
| [`0BSD-TSLIB-2.8.1.txt`](licenses/third-party/0BSD-TSLIB-2.8.1.txt) | tslib 0BSD text |
| [`NOTO-SANS-CJK-SIL-OFL-1.1.txt`](licenses/third-party/NOTO-SANS-CJK-SIL-OFL-1.1.txt) | Embedded Noto Sans CJK fonts |
| [`MODEL-CONTEXT-PROTOCOL-CSHARP-SDK-1.4.1-LICENSE.txt`](licenses/third-party/MODEL-CONTEXT-PROTOCOL-CSHARP-SDK-1.4.1-LICENSE.txt) | Official MCP C# SDK 1.4.1 transitional Apache-2.0 / MIT license notice |
| [`MIT-REACT-18.3.1.txt`](licenses/third-party/MIT-REACT-18.3.1.txt), [`ISC-IDB-8.0.3.txt`](licenses/third-party/ISC-IDB-8.0.3.txt), [`APACHE-2.0-XLSX-0.20.3.txt`](licenses/third-party/APACHE-2.0-XLSX-0.20.3.txt) | Representative exact PWA production license texts |

## Windows desktop and shared .NET components

Unless a row says otherwise, these libraries are linked into the self-contained desktop or macOS package and redistributed. MIT/BSD notices must be retained; Apache-2.0 notices and license text must be retained; native-package notices must remain with the distribution. Project URLs are the package project/repository URLs recorded in NuGet metadata.

| Component | Version | Project URL | License | Copyright notice | How used | Redistributed | Obligation / assessment |
|---|---:|---|---|---|---|---|---|
| BouncyCastle.Cryptography | 2.6.2 | https://www.bouncycastle.org/csharp/ | MIT | Legion of the Bouncy Castle Inc. and contributors | Cryptography transitively through MailKit/MimeKit | Yes | Retain MIT notice; compatible with proprietary distribution |
| ClosedXML | 0.104.2 | https://github.com/ClosedXML/ClosedXML | MIT | ClosedXML contributors | XLSX import/export | Yes | Retain MIT notice; compatible |
| ClosedXML.Parser | 1.2.0 | https://github.com/ClosedXML/ClosedXML.Parser | MIT | ClosedXML contributors | Formula parsing dependency | Yes | Retain MIT notice; compatible |
| DocumentFormat.OpenXml | 3.5.1 | https://github.com/dotnet/Open-XML-SDK | MIT | .NET Foundation and contributors | DOCX/XLSX/PPTX document formats | Yes | Retain MIT notice; compatible |
| DocumentFormat.OpenXml.Framework | 3.5.1 | https://github.com/dotnet/Open-XML-SDK | MIT | .NET Foundation and contributors | Open XML framework | Yes | Retain MIT notice; compatible |
| ExcelNumberFormat | 1.1.0 | https://github.com/andersnm/ExcelNumberFormat | MIT | Anders N.M. and contributors | Excel number formatting | Yes | Retain MIT notice; compatible |
| libphonenumber-csharp | 9.0.35 | https://github.com/twcclegg/libphonenumber-csharp | Apache-2.0 | Google/libphonenumber and port contributors | Phone-number parsing | Yes | Retain copyright, NOTICE where supplied, and Apache-2.0 text; compatible |
| MailKit | 4.17.0 | https://github.com/jstedfast/MailKit | MIT | Jeffrey Stedfast and contributors | IMAP/SMTP client | Yes | Retain MIT notice; compatible |
| MimeKit | 4.17.0 | https://github.com/jstedfast/MimeKit | MIT | Jeffrey Stedfast and contributors | MIME mail parsing/creation | Yes | Retain MIT notice; compatible |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 8.0.19 | https://github.com/dotnet/efcore | MIT | .NET Foundation and contributors | SQLite access | Yes | Retain MIT notice; compatible |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Dependency injection abstractions | Yes | Retain MIT/runtime notices; compatible |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Logging abstractions | Yes | Retain MIT/runtime notices; compatible |
| ModelContextProtocol.Core | 1.4.1 | https://github.com/modelcontextprotocol/csharp-sdk | Apache-2.0 / MIT transitional | Model Context Protocol, a Series of LF Projects, LLC and contributors | Vendor-neutral MCP client, stdio/Streamable HTTP/SSE transports, capability discovery and tool invocation | Yes | Retain the exact SDK transitional license; compatible with proprietary distribution under the stated terms |
| Microsoft.Extensions.AI.Abstractions | 10.5.2 | https://github.com/dotnet/extensions | MIT | .NET Foundation and contributors | MCP SDK function abstractions | Yes | Retain MIT notice; compatible |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | 10.0.7 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | MCP SDK runtime abstractions | Yes | Retain Microsoft/.NET MIT and bundled notices |
| System.Diagnostics.DiagnosticSource, System.IO.Pipelines, System.Net.ServerSentEvents | 10.0.7 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | MCP transport and diagnostics dependencies | Yes | Retain Microsoft/.NET MIT and bundled notices |
| System.Text.Encodings.Web, System.Text.Json | 10.0.6 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | MCP protocol JSON serialization | Yes | Retain Microsoft/.NET MIT and bundled notices |
| PdfPig | 0.1.12 | https://github.com/UglyToad/PdfPig | Apache-2.0 | PdfPig contributors | PDF text extraction | Yes | Retain Apache-2.0 license and notices; compatible |
| PDFsharp | 6.2.4 | https://github.com/empira/PDFsharp | MIT | empira Software GmbH and contributors | PDF report output | Yes | Retain MIT notice; compatible |
| RBush | 4.0.0 | https://github.com/viceroypenguin/RBush | MIT | RBush contributors | Spatial indexing transitively used by PDF tooling | Yes | Retain MIT notice; compatible |
| SixLabors.Fonts | 1.0.0 | https://github.com/SixLabors/Fonts | Apache-2.0 | Six Labors and contributors | Font handling transitively through PDF tooling | Yes | Retain Apache-2.0 license/notices; compatible |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.6 | https://github.com/ericsink/SQLitePCL.raw | Apache-2.0 | Eric Sink and contributors; SQLite public-domain notice | Native SQLite bundle | Yes | Retain Apache-2.0 and bundled native notices; compatible |
| System.Formats.Asn1 | 8.0.1 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Cryptography dependency | Yes | Retain runtime notices; compatible |
| System.IO.Packaging | 8.0.1 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Open XML packaging | Yes | Retain runtime notices; compatible |
| System.Memory | 4.5.3 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Compatibility/runtime dependency | Yes | Retain runtime notices; compatible |
| System.Security.Cryptography.Pkcs | 8.0.1 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Cryptography dependency | Yes | Retain runtime notices; compatible |
| Velopack | 1.2.0 | https://github.com/velopack/velopack | MIT | Velopack contributors | Packaging and automatic updates | Yes | Retain MIT notice; compatible |
| Microsoft.NET.ILLink.Tasks | 8.0.29 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Publish-time trimming task | Build-time; runtime output affected | Retain runtime notices in self-contained output |
| .NET runtime | 8.0.29 | https://github.com/dotnet/runtime | MIT plus bundled notices | Microsoft and listed third parties | Self-contained Windows/macOS runtime | Yes | Distribute exact runtime license and third-party notices listed above |

## macOS/Avalonia additions

These packages are resolved only for the opt-in native macOS preview build (some runtime-specific native assets are selected per target RID).

| Component | Version | Project URL | License | Copyright notice | How used / redistributed | Obligation / assessment |
|---|---:|---|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Fonts.Inter, Avalonia.Themes.Fluent, Avalonia.FreeDesktop, Avalonia.Native, Avalonia.Remote.Protocol, Avalonia.Skia, Avalonia.Win32, Avalonia.X11 | 11.3.18 | https://github.com/AvaloniaUI/Avalonia | MIT | AvaloniaUI contributors | Native UI framework; applicable assemblies redistributed | Retain MIT notices; compatible |
| Avalonia.BuildServices | 11.3.2 | https://github.com/AvaloniaUI/Avalonia | MIT | AvaloniaUI contributors | Build-time tasks | Retain notice if included in output |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | https://github.com/AvaloniaUI/angle | License file supplied by package | Chromium/ANGLE and listed contributors | Windows native rendering transitively resolved; target output must be inspected | Preserve the package license exactly; release verification required |
| HarfBuzzSharp and NativeAssets.* | 8.3.1.1 | https://github.com/mono/SkiaSharp | MIT plus bundled notices | Microsoft/Xamarin and HarfBuzz contributors | Text shaping native assets for selected RID | Preserve MIT and native notices; compatible |
| SkiaSharp and NativeAssets.* | 2.88.9 | https://github.com/mono/SkiaSharp | MIT plus bundled notices | Microsoft/Xamarin and listed third parties | Rendering native assets | Preserve MIT and native notices; compatible |
| MicroCom.Runtime | 0.11.0 | https://github.com/AvaloniaUI/MicroCom | MIT | AvaloniaUI contributors | Native COM interop | Retain MIT notice; compatible |
| System.IO.Pipelines | 8.0.0 | https://github.com/dotnet/runtime | MIT | .NET Foundation and contributors | Runtime dependency | Covered by .NET notices |
| Tmds.DBus.Protocol | 0.21.3 | https://github.com/tmds/Tmds.DBus | MIT | Tom Deseyn and contributors | Linux desktop transitive dependency | Retain MIT notice when target output includes it |

## Windows WhatsApp Bridge production dependency graph

Source: `bridge/pnpm-lock.yaml` resolved with pnpm 10.14.0 and `pnpm licenses list --prod`. The production build uses `@vercel/ncc` and Node.js 22.23.1 to create a companion SEA executable. Unless noted, listed packages are bundled as JavaScript or can contribute code to the Bridge executable. Copyright holders and full notices are reproduced in the package license texts and the `ncc` bundle snapshot; each component's project URL can be resolved from its package metadata. MIT/ISC/BSD/0BSD/BlueOak notices must be retained.

| License | Components and exact resolved versions | Redistribution / obligation |
|---|---|---|
| MIT | `@borewit/text-codec@0.2.2`; `@cacheable/memory@2.2.0`; `@cacheable/node-cache@1.7.6`; `@cacheable/utils@2.5.0`; `@img/colour@1.1.0`; `@keyv/bigmap@1.3.1`; `@keyv/serialize@1.1.1`; `@pinojs/redact@0.4.0`; `@tokenizer/inflate@0.4.1`; `@tokenizer/token@0.3.0`; `@types/node@26.1.1`; `@whiskeysockets/baileys@7.0.0-rc13`; `agent-base@7.1.4`; `ansi-regex@5.0.1`; `ansi-styles@4.3.0`; `async-mutex@0.5.0`; `atomic-sleep@1.0.0`; `cacheable@2.5.0`; `camelcase@5.3.1`; `color-convert@2.0.1`; `color-name@1.1.4`; `content-type@2.0.0`; `curve25519-js@0.0.4`; `debug@4.4.3`; `decamelize@1.2.0`; `dijkstrajs@1.0.3`; `emoji-regex@8.0.0`; `eventemitter3@5.0.4`; `file-type@21.3.4`; `find-up@4.1.0`; `hashery@1.5.1`; `hookified@1.15.1,2.2.0`; `https-proxy-agent@7.0.6`; `ip-address@10.3.1`; `is-fullwidth-code-point@3.0.0`; `keyv@5.6.0`; `locate-path@5.0.0`; `media-typer@2.0.0`; `ms@2.1.3`; `music-metadata@11.14.0`; `on-exit-leak-free@2.1.2`; `path-exists@4.0.0`; `pino@9.14.0,10.3.1`; `pino-abstract-transport@2.0.0,3.0.0`; `pino-std-serializers@7.1.0`; `p-limit@2.3.0`; `p-locate@4.1.0`; `pngjs@5.0.0`; `p-queue@9.3.1`; `process-warning@5.0.0`; `p-timeout@7.0.1`; `p-try@2.2.0`; `qified@0.10.1`; `qrcode@1.5.4`; `quick-format-unescaped@4.0.4`; `real-require@0.2.0,1.0.0`; `require-directory@2.1.1`; `safe-stable-stringify@2.5.0`; `smart-buffer@4.2.0`; `socks@2.8.9`; `socks-proxy-agent@8.0.5`; `sonic-boom@4.2.1`; `string-width@4.2.3`; `strip-ansi@6.0.1`; `strtok3@10.3.5`; `thread-stream@3.2.0,4.2.0`; `token-types@6.1.2`; `uint8array-extras@1.5.0`; `undici@7.29.0`; `undici-types@8.3.0`; `whatsapp-rust-bridge@0.5.4`; `win-guid@0.2.1`; `wrap-ansi@6.2.0`; `ws@8.21.1`; `yargs@15.4.1` | Retain each copyright and MIT text. Commercially compatible by itself. |
| BSD-3-Clause | `@hapi/boom@9.1.4`; `@hapi/hoek@9.3.0`; `@protobufjs/aspromise@1.1.2`; `@protobufjs/base64@1.1.2`; `@protobufjs/codegen@2.0.5`; `@protobufjs/eventemitter@1.1.1`; `@protobufjs/fetch@1.1.1`; `@protobufjs/float@1.0.2`; `@protobufjs/path@1.1.2`; `@protobufjs/pool@1.1.0`; `@protobufjs/utf8@1.1.2`; `ieee754@1.2.1`; `protobufjs@7.6.5` | Retain copyright, conditions, disclaimer and no-endorsement clause. |
| ISC | `cliui@6.0.0`; `get-caller-file@2.0.5`; `require-main-filename@2.0.0`; `semver@7.8.5`; `set-blocking@2.0.0`; `split2@4.2.0`; `which-module@2.0.1`; `y18n@4.0.3`; `yargs-parser@18.1.3` | Retain copyright and ISC permission/disclaimer. |
| Apache-2.0 | `detect-libc@2.1.2`; `long@5.3.2`; `sharp@0.35.3` | Retain license, copyright and NOTICE files where supplied; compatible by itself. |
| Apache-2.0 AND LGPL-3.0-or-later | `@img/sharp-win32-x64@0.35.3` | Installed optional native package. No `.node`/DLL was detected in the inspected SEA output, but every release must verify actual files. If redistributed, preserve the exact license and satisfy LGPL relinking/source requirements. |
| GPL-3.0 | `libsignal@6.0.0` | Imported by Baileys and present in the Bridge. The entire companion Bridge is distributed under GPL-3.0-only with complete corresponding source and installation information beside each binary release. |
| BlueOak-1.0.0 | `lru-cache@11.5.2` | Retain BlueOak license notice; no source-disclosure condition identified. |
| 0BSD | `tslib@2.8.1` | Retain exact 0BSD text. |
| Node.js runtime | `node@22.23.1` | Node.js uses the MIT license plus many third-party notices. Distribute the exact Node license snapshot. |

Direct Bridge dependencies are Baileys 7.0.0-rc13, https-proxy-agent 7.0.6, pino 10.3.1, qrcode 1.5.4, socks-proxy-agent 8.0.5 and undici 7.29.0. The exact Undici license is retained as `licenses/third-party/MIT-UNDICI-7.29.0.txt`. `@vercel/ncc@0.38.4` and `postject` are build-time tools; their notices must be retained if their code is copied into an output.

## PWA production dependencies

The PWA is a separate browser deliverable. Only the production graph is listed here; development-only Vite/Vitest/TypeScript packages are not shipped as packages, although generated output must still be checked for embedded notices.

| Component | Version | Project URL | License | Copyright notice | How used | Redistributed | Obligation |
|---|---:|---|---|---|---|---|---|
| idb | 8.0.3 | https://github.com/jakearchibald/idb | ISC | Jake Archibald and contributors | IndexedDB wrapper | Bundled JS | Retain ISC notice |
| js-tokens | 4.0.0 | https://github.com/lydell/js-tokens | MIT | Simon Lydell | React dependency | Bundled JS | Retain MIT notice |
| loose-envify | 1.4.0 | https://github.com/zertosh/loose-envify | MIT | Andres Suarez and contributors | React build dependency | Bundled JS | Retain MIT notice |
| lucide-react | 0.468.0 | https://github.com/lucide-icons/lucide | ISC | Lucide contributors | UI icons | Bundled JS/SVG | Retain ISC notice |
| react | 18.3.1 | https://github.com/facebook/react | MIT | Meta Platforms, Inc. and affiliates | UI runtime | Bundled JS | Retain MIT notice |
| react-dom | 18.3.1 | https://github.com/facebook/react | MIT | Meta Platforms, Inc. and affiliates | DOM rendering | Bundled JS | Retain MIT notice |
| scheduler | 0.23.2 | https://github.com/facebook/react | MIT | Meta Platforms, Inc. and affiliates | React scheduling | Bundled JS | Retain MIT notice |
| xlsx | 0.20.3 | https://cdn.sheetjs.com/ | Apache-2.0 | SheetJS LLC and contributors | Spreadsheet import/export | Bundled JS | Retain Apache-2.0 text/notices |

## Fonts, artwork, examples, and repository assets

| Component | Version/source | License / rights status | Use and redistribution | Assessment |
|---|---|---|---|---|
| Noto Sans CJK SC Regular/Bold | Tracked OTF files | SIL Open Font License 1.1 | Embedded for PDF/report output and redistributed | Retain OFL text; fonts may not be sold alone under a reserved name |
| AI Sales OS logo and Windows/macOS/PWA icons | Original host-native image generation output dated 2026-08-10; no reference images supplied | Project-owner-authorized generated asset with prompt, untouched master, derivation script and SHA-256 manifest under `docs/brand/generation-records/` | Shipped in product, installer, shortcuts and PWA | Provenance is recorded and release hashes are gated; no representation is made that trademark clearance has been completed |
| Sample XLSX files | `samples/Relvyn-*.xlsx` | Project sample data; exact original authorship not recorded | Repository examples, not required in installer | Confirm no real personal data or third-party copyrighted dataset is present |
| README/release screenshots | None detected as tracked product screenshots in this snapshot | N/A | N/A | Re-audit when screenshots are added |

## Contribution and copied-code boundary

Git history shows commits authored as `FrankShi811 <shixuanda0811@gmail.com>` and `Codex <codex@local>`. No submodules or separately vendored source trees were detected. The repository owner must confirm that all Codex-authored changes were produced under an account and terms that permit the intended proprietary distribution. Brand-asset provenance for the replacement icon family is recorded under `docs/brand/generation-records/`. This notice does not assign rights to any person or entity.
