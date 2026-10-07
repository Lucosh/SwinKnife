# Contributing to SwinKnife

Thanks for helping! Bug reports, ideas, translations and pull requests are all welcome.
Please open an issue first for big changes, so we can agree on the approach.

- [Build and run](#build-and-run)
- [Code structure](#code-structure)
- [Adding a tool](#adding-a-tool)
- [Translations](#translations)
- [Making a release](#making-a-release)

## Build and run

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio or VS Code are optional.

| | |
|---|---|
| Run from source | `dotnet run --project src/SwinKnife` or open `SwinKnife.slnx` in Visual Studio |
| Install your build locally | double-click `build.bat`: publishes to `%LOCALAPPDATA%\SwinKnife\app`, creates Desktop and Start menu shortcuts and starts the app |
| Build the installer | `powershell -ExecutionPolicy Bypass -File tools\release\build.ps1` (needs [Inno Setup](https://jrsoftware.org/isdl.php) 6 or 7) |

Build output goes to `%LOCALAPPDATA%\SwinKnife\artifacts` (see `Directory.Build.props`), so a repository synced with OneDrive
doesn't upload hundreds of MB of native libraries.

Useful command-line options of `SwinKnife.exe`:

| Option | Effect |
|---|---|
| `"<file>"` | opens the file in the right tool |
| `--page <tool>` | opens a tool (`viewer`, `convert`, `pdf`, `archive`, `photo`, `capture`, `rename`, `disk`, `dupes`, `clean`, `recovery`, `sysinfo`, `qr`, `gemini`, `settings`) |
| `--action compress-pdf\|ocr-pdf\|gemini\|extract-here "<file>"` | runs an action directly (used by the right-click menu) |
| `--register-shell` / `--unregister-shell` | adds or removes the right-click menu without opening the window (used by the installer) |

The app runs as a single instance: a second launch passes its arguments to the window that is already open.

Environment variables for testing: `SWINKNIFE_LANG=de` forces a language without touching the settings;
`SWINKNIFE_MISSING=1` writes the texts shown without a translation to `%LOCALAPPDATA%\SwinKnife\missing-<language>.txt` on exit.

## Code structure

```
src/SwinKnife/
  App.xaml, MainWindow.xaml   startup, theme, single instance, sidebar and navigation
  Pages/                      one page per tool; Tools.cs lists them by sidebar group
  Controls/                   reusable views: images, PDF, media, treemap, annotations, screen selection
  Dialogs/                    Fluent dialogs (Dlg.cs)
  Core/                       logic without UI: conversions, Office, FFmpeg, OCR, archives, duplicates, cleanup,
                              recovery (carving + NTFS/FAT/exFAT), system info, Explorer menu, translations (L.cs)
  Pdf/                        PDFium (P/Invoke) to render and edit PDFs, PDFsharp for passwords, compression and forms
  Lang/                       translations (en, de, es, fr); Italian is the source language
installer/SwinKnife.iss       Inno Setup script
tools/i18n/                   translation checker
tools/release/build.ps1       builds installer, portable zip and checksums
.github/workflows/            Build (every push) and Release (on version tags)
```

Main libraries: [WPF-UI](https://github.com/lepoco/wpfui), [Magick.NET](https://github.com/dlemstra/Magick.NET),
[PDFium](https://github.com/bblanchon/pdfium-binaries), [PDFsharp](https://github.com/empira/PDFsharp),
[ClosedXML](https://github.com/ClosedXML/ClosedXML), [Markdig](https://github.com/xoofx/markdig),
[SharpCompress](https://github.com/adamhathcock/sharpcompress), [SharpZipLib](https://github.com/icsharpcode/SharpZipLib),
[AvalonEdit](https://github.com/icsharpcode/AvalonEdit), [QRCoder](https://github.com/codebude/QRCoder),
WebView2 and System.Management.

## Adding a tool

1. Create a `UserControl` in `Pages/` whose constructor receives the `MainWindow`.
2. Optionally implement `IToolPage`: `Accepts`, `OpenFile`, `AddFiles` (drag and drop, command line), `CanClose` (unsaved changes), `Shutdown`.
3. Add a row to `Tools.All` in `Pages/Tools.cs` with key, title, icon, sidebar group and description.
4. Write every visible text with `L.T(...)` and translate it (see below).

## Translations

The texts in the code are in Italian and are also the translation keys:

```csharp
L.T("Salva")                      // plain text
L.T($"Trovati {n} file")          // the key is "Trovati {0} file"
```
```xml
<TextBlock Text="{l:T 'Apri file'}" />
```

Translations are in `src/SwinKnife/Lang/<language>.json`, as `"Italian text": "translation"`.
Keys starting with `//` are comments that say which file the texts come from. An empty or missing translation is shown in Italian.

- **Fix a translation:** change the value in the `.json` file. Keep placeholders such as `{0}` and `{1}`, and the `\n` line breaks.
  Check the result with `SWINKNIFE_LANG=<code>`.
- **Add a language:** copy `en.json` to `<code>.json` (e.g. `pt.json`), translate the values, then add the language to `L.Languages`
  in `Core/L.cs` and to the `[Languages]` section of `installer/SwinKnife.iss`.
- **After adding texts to the code:** run `py tools/i18n/check.py --update`. It adds the new texts (empty) to every language file
  and reports missing texts and wrong placeholders. The *Build* workflow runs the same check on every push.

Technical names that must not change (Windows commands, PDF names, file formats) are left outside `L.T`.

## Making a release

1. Update `<Version>` in `src/SwinKnife/SwinKnife.csproj`.
2. Commit, then push a tag with the same version:
   ```
   git tag v0.4.0
   git push origin v0.4.0
   ```
3. The *Release* workflow builds the app (self-contained, so users don't need .NET), creates `SwinKnife-Setup-<version>.exe`,
   `SwinKnife-<version>-portable.zip` and `SHA256SUMS.txt` and publishes them in a GitHub release with notes generated from the commits.

You can also start the *Release* workflow by hand from the **Actions** tab: it builds the same files as a downloadable artifact without publishing anything.
To build the same files locally, run `tools\release\build.ps1`; they end up in `%LOCALAPPDATA%\SwinKnife\release`.

**Automatic updates.** Once a day the app asks the GitHub API for the latest release (`Core/Updater.cs`). If it is newer,
it downloads `SwinKnife-Setup-<version>.exe`, checks it against `SHA256SUMS.txt` and runs it with `/SILENT /RELAUNCH=1`:
the installer closes SwinKnife, updates it and starts it again. So every release must keep these two file names, and the tag
must be a plain version (`v1.2.3`). Copies not installed with the setup (portable zip, `build.bat`) only get a link to the download page.

The installer installs per user in `%LOCALAPPDATA%\Programs\SwinKnife` (no admin rights), or for all users if chosen in the setup.
Keep the `AppId` in `installer/SwinKnife.iss` unchanged, otherwise updates are no longer recognized.
