# AI Sales OS Windows icon production prompt

Generation date: 2026-08-10

Purpose: Refine the original R/loop-arrow concept created earlier in this project into the Windows-only production application icon for AI Sales OS v5.19.1. The reference image, untouched host-native output and this exact edit prompt are retained together.

Reference image: `ai-sales-os-windows-icon-reference-20260810-160930.png`

The reference is an original AI Sales OS concept generated earlier in the same project. No stock image, marketplace asset, existing company logo or third-party brand mark was supplied.

```text
Use the provided original AI Sales OS icon created earlier in this project as the only visual reference. Produce a production application-icon master that preserves the same distinctive interlocking capital-R / closed-loop arrow silhouette and the same deep navy, green, and white identity. Refine it for excellent legibility at 16 px: crisp vector-like geometry, clean joins, even optical weight, centered composition, generous safe area, and a full-bleed square background. Use flat solid colors only: deep midnight navy #071B33, clear emerald green #1EB980, and white #FFFFFF. Remove glow, texture, shadow, bevel, and gradients. No text, no wordmark, no watermark, no mockup, no border, no extra symbols, and do not introduce or imitate any third-party logo. Output one square 1024×1024 PNG master suitable for deterministic Windows PNG and ICO downscaling.
```

The host runtime returned a square 1254×1254 PNG. `scripts/generate-brand-assets.ps1` deterministically resizes that untouched output for the Windows PNG and multi-resolution ICO family.
