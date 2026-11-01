# assets/brand

The Rigbay logo kit: the Dock mark, the app icon, the Tile module family, social images, test sheets and the scripts
that build and check all of it. Guidelines: [`docs/BRAND.md`](../../docs/BRAND.md). What the app needs:
[`integration/README.md`](integration/README.md).

| Folder | What |
| --- | --- |
| `rigbay/` | symbol, wordmark and lockups: colour, reversed, black, white, accent-variable, small cut, quiet |
| `app-icon/` | `rigbay.ico`, PNG 16 to 512, `tray/` (light, dark, neutral), `high-contrast/` |
| `modules/` | XTU, PredatorSense, Steam access, template, unknown: tile, small cut, quiet glyph, unsupported; PNG 16 to 128 |
| `social/` | `social-preview-1280x640.png`, `org-avatar-512.png`, `readme-banner` |
| `tests/` | the test sheets (PNG), `svg-audit.txt`, `contrast.txt`, `verification.txt`, `ico-load-check.txt` |
| `integration/` | generated hand-over for the app task |
| `tools/` | Node scripts; `tools/lib/marks.mjs` is the one place where the geometry lives |
| `concepts/` | the earlier concept round (L1), kept as the record |

## Rebuild

Needs Node 20 or newer and Microsoft Edge (used headless to render; nothing is captured from the screen). No package
install, no Python.

```
node assets/brand/tools/build-all.mjs        # SVGs, PNGs, ICOs, social, sheets, integration, audit, contrast, verify
powershell -NoProfile -File assets/brand/tools/check-ico.ps1   # loads every .ico through Windows' LoadImage
```

Everything except `concepts/` and `integration/README.md` is generated: edit `tools/lib/marks.mjs` (geometry),
`tools/build-svgs.mjs` (file set), then rebuild. Do not edit the SVGs by hand.

The marks contain no text and use no font. The PNG test sheets contain labels rendered in the system UI font as
pictures. See `docs/BRAND.md` section 12 for the licence note.
