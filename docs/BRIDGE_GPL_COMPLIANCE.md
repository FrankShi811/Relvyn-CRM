# WhatsApp Bridge GPL-3.0 distribution and installation information

## Component boundary

AI Sales OS ships `WAFlow.WhatsApp.Bridge.exe` as a separate companion process.
The desktop application communicates with it through newline-delimited JSON on
standard input and standard output. The Bridge is not linked into or embedded
as a resource inside `AISalesOS.exe`.

The Bridge and its original source under `bridge/` are licensed under
GPL-3.0-only. The desktop application remains governed by the root project
license. Third-party modules retain the licenses recorded in
`THIRD_PARTY_NOTICES.md`.

## Corresponding source supplied with every release

Every Windows GitHub Release publishes an asset named:

`AI-Sales-OS-WhatsApp-Bridge-VERSION-source.zip`

The archive contains:

- the exact Bridge source and build scripts used for that release;
- `package.json`, `pnpm-lock.yaml` and workspace configuration;
- the installed source trees of the resolved production and build dependencies;
- the exact GPL-3.0 text as `COPYING`;
- third-party notices and this installation document;
- a SHA-256 manifest for the complete source archive contents.

Node.js 22.23.1 is used to build the Single Executable Application. Its source
is available from the Node.js project at:

`https://nodejs.org/dist/v22.23.1/node-v22.23.1.tar.gz`

The source archive is offered beside the corresponding object-code release at
no additional charge. Release assets and source must stay available together.

## Rebuild

On Windows x64, install Node.js 22.23.1 and pnpm 10.14.0, extract the source
archive, then run:

```powershell
pnpm install --frozen-lockfile
pnpm run build:exe
```

The rebuilt companion is written to:

`dist/WAFlow.WhatsApp.Bridge.exe`

## Install or run a modified Bridge

Set the user or process environment variable below to the absolute path of the
modified executable before starting AI Sales OS:

```powershell
$env:AI_SALES_OS_WHATSAPP_BRIDGE_PATH = 'C:\path\to\WAFlow.WhatsApp.Bridge.exe'
```

The desktop application checks that variable before the installed companion.
It does not require a vendor signature or vendor hash for an override. The
legacy `WAFLOW_BRIDGE_EXE` variable remains accepted for compatibility.

Automatic application updates can replace the installed default companion, but
they do not change the user's override path. Remove the variable to return to
the version supplied by the installer.

## Protocol and diagnostics

The Bridge accepts JSON commands on standard input and emits JSON responses and
events on standard output. Run `pnpm smoke:exe` after rebuilding for a local
protocol smoke test. WhatsApp account pairing and live messaging require the
user's own authorized account and network access.

This document records the project's engineering distribution route. It is not
specialist legal advice.
