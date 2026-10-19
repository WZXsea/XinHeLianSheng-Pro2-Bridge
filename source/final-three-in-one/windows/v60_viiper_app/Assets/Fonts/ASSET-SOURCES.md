# Shared desktop typeface

Both Windows and macOS load the same bundled Noto Sans SC static fonts through
`avares://PRO2ControllerAssistant/Assets/Fonts#Noto Sans SC`. UI controls,
configuration annotations and controller cap lettering use this resource.

- Source: the official Noto CJK repository, downloaded 2026-10-19.
- Fonts: https://github.com/notofonts/noto-cjk/tree/main/Sans/SubsetOTF/SC
- Files: `NotoSansSC-Regular.otf`, `NotoSansSC-Medium.otf`, `NotoSansSC-Bold.otf`.
- Licence: SIL Open Font License 1.1; original copyright and licence are retained in `OFL.txt`.
- The font bytes and names are unmodified. Static weights let the bundled
  Avalonia/Skia text stack select regular and heavier UI text consistently.

The shared UI retains the Windows theme's sizes, spacing and colours. This
bundled face supplies consistent Chinese glyphs across platforms in place of
the original platform-specific Segoe UI / Microsoft YaHei fallback chain.
