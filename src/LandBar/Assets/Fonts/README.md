# Noto Sans SC

These application-local font resources are Noto Sans SC, licensed under the SIL Open Font License 1.1. The copyright notice and complete license are included in `OFL.txt`.

The source font is Noto Sans SC from the Google Fonts / Noto project:

- https://github.com/google/fonts/tree/main/ofl/notosanssc
- https://github.com/notofonts/noto-cjk

For consistent WPF resource loading, the application uses two static instances of the weight axis: Regular (400) and SemiBold (600). They are generated from the variable font without glyph subsetting. No system font installation is required.

WPF resource family: `./Assets/Fonts/#Noto Sans SC`.

Modification: static weight-axis instantiation and style name normalization for application resource loading. Copyright and license metadata remain present.

The static files omit residual `STAT` variation metadata. This prevents WPF from substituting a legacy Bold/700 face when SemiBold/600 is requested. The glyph set is complete and unchanged; only the weight axis is pinned.

Reproduce with Python and fontTools:

```powershell
python tests/LandBar.Fonts.Smoke/prepare_static_fonts.py --source <NotoSansSC-variable.ttf>
dotnet run --project tests/LandBar.Fonts.Smoke -c Release
```
