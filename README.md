<p align="center">
  <img src="src/SwinKnife/Assets/swinknife.png" width="96" alt="SwinKnife logo">
</p>

<h1 align="center">SwinKnife</h1>

<p align="center">
  <b>The Swiss army knife for Windows.</b><br>
  Open any file, convert, edit PDFs and photos, capture the screen, find duplicates, recover deleted files and clean up your PC: all in one free app.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/badge/Download-Windows%2010%20%7C%2011-E5484D?style=for-the-badge&logo=windows&logoColor=white" alt="Download for Windows"></a>
</p>

<p align="center">
  <b>English</b> · <a href="README.it.md">Italiano</a> · <a href="README.de.md">Deutsch</a> · <a href="README.es.md">Español</a> · <a href="README.fr.md">Français</a>
</p>

<p align="center">
  <img src="docs/screenshots/en/home.webp" alt="SwinKnife home screen" width="900">
</p>

## Download and install

1. Open the **[latest release](../../releases/latest)** and download **`SwinKnife-Setup-<version>.exe`**.
2. Run it. No administrator rights are needed; you can choose to add SwinKnife to the right-click menu of File Explorer.
3. Start SwinKnife from the Start menu.

> [!NOTE]
> The app is not code-signed yet, so Windows SmartScreen may show *"Windows protected your PC"*.
> Click **More info → Run anyway**. You can check the download against `SHA256SUMS.txt` in the release.

- **Portable version:** download `SwinKnife-<version>-portable.zip`, extract it anywhere and run `SwinKnife.exe`.
- **Update:** download the new setup and install it over the old version; settings are kept.
- **Uninstall:** *Settings → Apps → Installed apps → SwinKnife*.

**Requirements:** Windows 10 (version 1809 or later) or Windows 11, 64-bit. Everything else is included, .NET too.
Optional: Microsoft Office or LibreOffice to convert Word, Excel and PowerPoint files.
FFmpeg (audio, video, screen recording) and 7-Zip (creating .7z archives) are downloaded automatically the first time you need them.

## Screenshots

<table>
  <tr>
    <td><img src="docs/screenshots/en/pdf.webp" alt="PDF editor"><br><sub><b>PDF editor</b>: edit text, sign, fill in forms, OCR, merge and split</sub></td>
    <td><img src="docs/screenshots/en/photo.webp" alt="Photo editor"><br><sub><b>Photo editor</b>: adjustments, filters and crop, like on the iPhone</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/en/rename.webp" alt="Bulk rename"><br><sub><b>Bulk rename</b>: date taken, numbering, find and replace, with preview</sub></td>
    <td><img src="docs/screenshots/en/disk.webp" alt="Disk analyzer"><br><sub><b>Disk analyzer</b>: see what takes up space with a treemap</sub></td>
  </tr>
</table>

## What it can do

### Files and documents
| Tool | What it does |
|---|---|
| **Open file** | Images (also HEIC, AVIF, JPEG XL, PSD, RAW, animated GIF), PDF, XPS, Word/Excel/PowerPoint/ODT, CSV and XLSX as tables, text and code with syntax highlighting, Markdown/HTML with preview, SVG, audio, video, archives, fonts; anything else in hexadecimal. File details, MD5/SHA-256 and **Ask Gemini** (summarize, explain, translate, fix mistakes). |
| **Convert** | Images ↔ JPG/PNG/WEBP/AVIF/JPEG XL/BMP/GIF/TIFF/ICO/PDF · Word ↔ PDF/DOCX/ODT/RTF/TXT/HTML · PDF → Word/PNG/JPG/TXT · Excel/CSV/JSON · PowerPoint → PDF/images · Markdown/HTML/TXT → PDF · audio and video · **OCR**: image → text, scanned PDF → searchable PDF. |
| **PDF editor** | Edit existing text, add text, highlighter, whiteout, redaction, signature, drawing, notes; **fill in forms**; **OCR**; merge, split, rotate and reorder pages; watermark, page numbers, compression, AES-256 password; undo/redo. |
| **Archives** | Create ZIP (AES-256 password), 7z (encrypted contents and names) and TAR.GZ; extract ZIP, 7z, RAR, TAR, GZ, BZ2, XZ, password-protected ones too. |

### Photos and screen
| Tool | What it does |
|---|---|
| **Photo editor** | Like the iPhone Photos app: *Adjust* (15 sliders + Auto), *Filters*, *Crop*. Saves a copy and keeps the EXIF data. |
| **Screen capture** | Screenshots of an area, a window or the whole screen, with arrows, shapes, highlighter, text, numbered steps and pixelation for sensitive data. Screen recording to **MP4** (with microphone) or **GIF**. |
| **Bulk rename** | Templates with `{name}`, `{num}`, `{date}` (date the photo was taken), `{time}`, `{folder}`; find and replace (regex too), letter case, extensions, accents. Preview, conflict warnings, undo. |

### Disk and system
| Tool | What it does |
|---|---|
| **Disk analyzer** | Like WinDirStat: folders by size, statistics by file type and a treemap. Skips OneDrive cloud-only files. Run as administrator for **fast mode**: it reads the NTFS file table directly, like WinDirStat and WizTree, and scans a whole disk in seconds. |
| **Find duplicates** | Identical files and **similar photos** (recognizes resized or edited copies). Picks what to keep for you, shows previews side by side, moves the rest to the Recycle Bin. |
| **PC cleanup** | Temporary files, Recycle Bin, browser and app caches, thumbnails, error reports, Windows Update leftovers. Shows how much space you get back and the file list before deleting. |
| **File recovery** | *Quick, with original names*: NTFS, FAT12/16/32 and exFAT. *Deep scan*: finds files by their content, even after formatting. Read-only; works on disk images too. |
| **System info** | Windows, CPU, RAM, graphics card, disks with S.M.A.R.T. health, battery wear and cycles, network and Wi-Fi, startup programs you can turn on and off, live CPU and memory usage. |

### Utilities
| Tool | What it does |
|---|---|
| **QR & passwords** | QR codes for links, Wi-Fi, contacts, email and SMS, as PNG or SVG. Secure password generator and strength check, all offline. |
| **Gemini** | Google's AI in a built-in panel that stays signed in, or in Chrome with your existing profile. |
| **Settings** | Language, right-click menu, add-on components, data folder and error log. |

The **right-click menu** in File Explorer (on Windows 11 under *Show more options*) gives quick access to: open, convert,
add to an archive and ask Gemini for any file; edit, compress and OCR for PDFs; edit and convert for images;
extract here for archives; analyze space, find duplicates, rename and compress for folders.

## Privacy

SwinKnife works offline on your PC and collects no data. It goes online only to download FFmpeg or 7-Zip
the first time a tool needs them, and when you use Gemini, which sends your request to Google.
Settings and logs stay in `%LOCALAPPDATA%\SwinKnife`.

## Languages

SwinKnife is available in English, Italian, German, Spanish and French. It uses the Windows language
(English if yours isn't available yet), and you can change it in *Settings*.
Found a clumsy translation or want to add your language? See **[CONTRIBUTING.md](CONTRIBUTING.md#translations)**: it only takes editing a text file.

## For developers

SwinKnife is written in **C# / WPF (.NET 10)** with the Fluent design of Windows 11 ([WPF-UI](https://github.com/lepoco/wpfui)).
To build it, install the .NET 10 SDK and double-click `build.bat`, or run `dotnet run --project src/SwinKnife`.
Code structure, how to add a tool, translations and releases are explained in **[CONTRIBUTING.md](CONTRIBUTING.md)**.

## License

[MIT](LICENSE): free to use, modify and share.
