# AI Sales OS brand asset provenance

The distributed Windows application icon family is derived from a newly generated production master retained in `generation-records/`. The current Windows master is an edit of an earlier original project concept; both the sole reference image and untouched edited output are retained. The v5.19.1 icon update does not change macOS or PWA assets.

The provenance set contains:

- the exact production edit prompt and sole project-owned reference image;
- the untouched host-native image-generation output;
- a deterministic PowerShell derivation script;
- SHA-256 hashes and byte sizes for every distributed PNG and ICO asset;
- the project owner's explicit authorization to use the selected generated icon for the Windows program.

No stock artwork, marketplace asset or third-party logo was supplied to the generator. The only reference is an earlier original concept generated within this project and preserved in the same record directory. The generated output is not represented as a registered trademark or as guaranteed unique against every mark worldwide. A separate trademark clearance review remains appropriate before trademark registration.

Regenerate the derived assets on Windows with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\generate-brand-assets.ps1 `
  -DesktopOnly `
  -MasterPath .\docs\brand\generation-records\ai-sales-os-windows-icon-master-original-20260810-160930.png `
  -PromptPath .\docs\brand\generation-records\ai-sales-os-windows-icon-prompt-20260810-160930.md `
  -ReferencePath .\docs\brand\generation-records\ai-sales-os-windows-icon-reference-20260810-160930.png `
  -GeneratedAtUtc 2026-08-10T08:09:30Z
```
