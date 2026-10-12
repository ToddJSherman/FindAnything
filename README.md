# Find Anything

A Windows app that finds text inside files. Point it at a folder or a whole drive, type `Govee H6056`, and get a
list of every file that contains it. Double-click a file to open it with every match highlighted, even when the
file is 25 GB.

Everything runs on your PC. Nothing goes online.

## Install

1. Download **FindAnything-Setup.exe** from the [latest release](https://github.com/ToddJSherman/FindAnything/releases/latest).
2. Run it and click **Install**. It installs for your user only, so it needs no admin rights. You can choose:
   - a Start menu shortcut, with **Ctrl+Alt+F** to open the app from anywhere
   - a desktop shortcut
   - **Find Anything here** on Explorer's right-click menu for folders and drives. On Windows 11 it is under
     *Show more options*.
3. To remove it, use **Settings > Apps > Installed apps > Find Anything > Uninstall**.

The files are not code-signed, so Windows SmartScreen may say it "protected your PC". Click **More info**, then
**Run anyway**. Prefer no installer? Download **FindAnything.exe** from the same release and run it from anywhere.

Requires Windows 10 or 11, which already include the .NET Framework 4.8 it needs.

## What it does

- **Searches inside files**: any file type, including binaries. It also searches **file names** if you tick that box.
- **Wildcards**: `*` matches any run of characters on the same line, and `?` matches one character.
  `Govee*6056` finds "Govee RGBIC H6056". `H60?6` finds H6056 and H6066.
- **Match case** and **whole word** switches.
- **File types**: `*.txt` searches only TXT files, so a DOC that contains the text is not counted. Separate several
  types with `;`. **Except** skips types such as `*.exe;*.iso`.
- **Reads the real text in PDF, DOCX, XLSX, PPTX and OpenDocument files.** Their text is compressed, so a plain byte
  search misses it. The PDF reader is built in and handles compressed and encrypted PDFs (the kind that open without
  a password). Scanned PDFs are pictures of text, so they have nothing to find. They are listed under *notes*.
- **Unicode (UTF-16) text**: Notepad "Unicode" files, old `.doc` files, and strings inside programs.
- **Skip folders** by name (wildcards allowed), skip files over a size, and set how many files are searched at once.
- **OneDrive online-only files are skipped**, so a search never downloads your cloud files.
- **Paths longer than 260 characters** work without changing any Windows setting.

## Big files

Nothing is ever loaded whole.

- The search streams each file through a 4 MB buffer. A 25 GB file costs the same memory as a 4 KB one.
  Measured: a 25 GB file scanned in 18 s with a peak of 20 MB of memory.
- The viewer shows 1 MB of the file at a time (small files show whole). It opens a 25 GB file instantly at the first
  match. **Next / Previous** (F3 / Shift+F3) search the rest of the file in the background with a progress readout,
  and Esc stops them. The slider and the Page buttons move through the file.
- **Open with its usual program** and **Open in Notepad++** warn you first when a file is over 200 MB, and offer the
  built-in viewer instead.

## Using it

| Do this | To |
|---|---|
| Enter in the Find box | Search |
| Esc | Stop the search |
| Double-click or Enter on a result | Open it in the viewer with matches highlighted |
| Right-click a result | Open with its usual program, open in Notepad++ at the line, show in folder, copy path, save the list as CSV |
| Click a column header | Sort by it |
| Drop a folder on the window | Set *Look in* |
| `C:\;D:\` in *Look in*, or pick `(all drives)` | Search several places |
| *notes* link in the status bar | See locked files, access-denied folders, password-protected or scanned PDFs |

**Right-click menu.** `tools\explorer-menu.ps1` adds **Find Anything here** to Explorer's right-click menu for
folders, for empty space inside a folder, and for drives. It is per-user, so no admin is needed. On Windows 11 it is
under **Show more options** (or Shift+F10). `-Uninstall` removes it.

```
powershell -ExecutionPolicy Bypass -File tools\explorer-menu.ps1
```

Command line: `FindAnything.exe "D:\Some folder"` starts with *Look in* set.
`FindAnything.exe --view <file> --find <text>` opens only the viewer.

Settings live in `%APPDATA%\FindAnything\settings.txt`, one plain-text `key=value` per line.

## Build

```
build.cmd          builds bin\FindAnything.exe and the installer bin\FindAnything-Setup.exe
build.cmd test     also builds and runs the tests
```

It uses the C# compiler that ships with Windows (.NET Framework 4.8), so there is nothing to install. The tests
generate extra PDFs with Python (PyMuPDF and reportlab) when those are available. Those PDFs are written by other
libraries, which checks the PDF reader against files it did not write itself. Without Python those tests are
skipped.

## Layout

```
src/Core/   search engine: BytePattern (wildcard matcher), Scanner (streaming), SearchJob (folders, threads),
            DocText (Office/ODF), PdfDoc + PdfText + PdfLexer (PDF reader), LongPath
src/App/    WinForms window, viewer, settings, folder picker, About box
src/Setup/  installer and uninstaller (one exe; the app and LICENSE are embedded in it)
src/AppInfo.cs  version, credits and file details shared by both exes
tools/      explorer-menu.ps1 (right-click menu for a copy run without the installer)
tests/      Tests.cs (67 checks), make_pdfs.py (test PDFs from PyMuPDF and reportlab)
design/     icon concepts and the app icon source (SVG)
```

## Limits

- Case-insensitive matching folds A-Z only. In UTF-8 files, `é` and `É` are different.
- `?` matches one byte in UTF-8 text, so it stands for one ASCII character but only part of an accented one.
- Text inside ZIP, 7z or ISO archives and inside email stores is not searched, apart from the Office formats above.
- PDFs larger than 512 MB are searched as raw bytes instead of being parsed.
- No OCR: text that only exists as an image cannot be found.

## Credits

Created by **[ToddJSherman](https://github.com/ToddJSherman)**.

The search engine, PDF reader, viewer and installer are written from scratch in C#, with no third-party libraries.

## License

[MIT](LICENSE). Copyright (c) 2026 ToddJSherman. Use it, change it and share it. Keep the copyright notice.
