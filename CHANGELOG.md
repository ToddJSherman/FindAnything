# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [2026.10.11.3] - 2026-10-11

First public release, under the MIT License.

### Added
- Installer, `FindAnything-Setup.exe`. It installs for the current user with no admin prompt and offers a Start menu
  shortcut with Ctrl+Alt+F, a desktop shortcut and the Explorer right-click menu. It appears in Installed apps with
  an uninstaller that removes everything it added. It also updates or reinstalls in place, and `/silent` installs
  without a window.
- About box (status bar) with version, credits and the project link. The version, author and license also show in
  each exe's Properties > Details.
- MIT License.
- "Find Anything here" on Explorer's right-click menu for folders, folder backgrounds and drives
  (`tools\explorer-menu.ps1`, per-user, `-Uninstall` to remove).

### Fixed
- A drive passed on the command line as `"C:\"` arrived as `C:"` (the `\"` is read as an escaped quote) and was
  ignored. It now opens with Look in set to `C:\`.

## [2026.10.11.2] - 2026-10-11

### Changed
- New app icon: a page with a magnifying glass, with an amber highlighted line in the lens. It has a simplified
  drawing for 16-32 px and a white edge so it stays visible on dark taskbars.

## [2026.10.11] - 2026-10-11

### Added
- Search inside files across folders, drives or several roots at once, with `*` / `?` wildcards, match case,
  whole word, and file-name search.
- File type filter (include and except lists), skip-folder list, size limit and thread count.
- Streaming scanner with a 4 MB buffer: any file size at constant memory (25 GB scanned in 18 s, peak 20 MB).
- UTF-16 text search, with and without a byte-order mark.
- Text extraction for DOCX/XLSX/PPTX/OpenDocument and a from-scratch PDF reader: compressed and object streams,
  ToUnicode and font encodings, Type0 fonts, RC4/AES-128/AES-256 encryption opened with the empty password.
- Results list with hit counts, first-match line and preview, sorting, CSV export and copy path.
- Windowed viewer that opens files of any size instantly, highlights every match, and searches the rest of the file
  in the background for Next / Previous.
- Warning before handing files over 200 MB to Notepad++ or a file's usual program.
- Skips OneDrive online-only files so a search never downloads them; long paths work with no system changes.
- Test suite (67 checks) and the app icon.
