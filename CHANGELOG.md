# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [2026.10.12.4] - 2026-10-12

### Changed
- The **CPU use** slider now drives the regular text search too and replaces the Threads box under Filters: it
  sets how many files are searched at once, is always shown in Search text and Find keys, applies mid-search, and
  file searching runs at below-normal priority. The old Threads setting is dropped in favour of `CpuUse`.

## [2026.10.12.3] - 2026-10-12

### Added
- **CPU use** now also governs OCR: shown in Search text and Find keys while Pictures (OCR) is on, it caps how many
  pictures and scanned PDFs are OCR'd at once (Windows OCR uses one CPU thread each; measured 0.7 threads busy at 1,
  6.1 at 8). Plain files are not held back, OCR runs at below-normal priority, and changes apply mid-search. One
  setting for both kinds of heavy work (`CpuUse`; the old `PictureWorkers` value is carried over).

## [2026.10.12.2] - 2026-10-12

### Added
- **CPU use** slider for Similar pictures: how many pictures are read at once, from 1 CPU thread to all of them.
  Default is a quarter of the threads (2 on an 8-thread laptop). It takes effect immediately, even mid-search, and is
  remembered.

### Fixed
- Similar pictures overloaded the processor: it read 6 pictures at once and gave each one every CPU thread, so 144
  threads fought over 24 and the machine ran hot. Each picture now runs on one thread, at below-normal priority. On an
  i7-12900K the default went from 22.5 threads busy and 811 ms of CPU per picture (28 pictures a second) to 4.4
  threads busy and 78 ms per picture (57 a second): twice as fast for a fifth of the load.

## [2026.10.12] - 2026-10-12

### Changed
- **Similar pictures now matches sample pictures with Meta's DINOv2-small** instead of CLIP. CLIP judged "what kind of
  photo" (a close-up face on a couch), so a photo of a dog ranked unrelated photos of people near the top. Measured on one
  dog's 22 photos against 242 unrelated pictures, every photo as the sample in turn: unrelated pictures ranked above
  the dog's worst photo went from 9.7 per search (CLIP) to none (DINOv2); the highest unrelated score fell from 0.75
  to 0.22. It is also faster (about 75 pictures a second on an i7-12900K). Descriptions still use CLIP.
- Only pictures that really look alike are listed (DINOv2 score 0.25 or more; for descriptions, the top band of
  scores). The status bar counts the ones left out, and **Show weaker matches** lists every picture, ranked.
- With several samples, a picture is scored against the sample it is closest to, instead of their average.
- The picture-model download (pack 2, 137 MB, release `picture-model-2`) adds DINOv2 (Apache 2.0) and its license.
  Copies with pack 1 are asked once to download pack 2.
- Remembered fingerprints are kept per model.

## [2026.10.11.8] - 2026-10-11

### Changed
- Similar pictures: **Pick samples** is now a full-size button in the search row, between the box and Find pictures,
  with an amber edge and a hand-drawn Polaroid of Grandma (the dog the feature was tested on) that tilts up under the
  pointer. It shows how many samples are chosen ("3 samples"). The same Polaroid greets you on the empty results list.
- Motion: buttons and chips fade between their resting and hover looks, the selected mode tab slides to its new place,
  new result rows and thumbnails fade in instead of popping, the hover preview fades in, and empty states rise in
  gently. All of it runs on one shared timer and is off when Windows' "Animation effects" setting is off.

## [2026.10.11.7] - 2026-10-11

### Added
- **Similar pictures** mode: pick or drop one or more sample pictures, or describe what is in them ("a dog in a
  costume"), and every picture under the folder is ranked by how alike it is, with a bar and a rank. Uses OpenAI's
  CLIP (ViT-B/32, 8-bit) through Microsoft ONNX Runtime 1.20.1 on the CPU, called through its C API (no managed
  package). About 40 pictures a second on an i7-12900K. Pictures are read upright (EXIF) and centre-cropped as CLIP
  expects.
- The picture model is an optional, one-time 117 MB download from this project's releases, only after the user agrees,
  checked against a pinned SHA-256 and unpacked to `%LOCALAPPDATA%\FindAnything\ai`. ONNX Runtime's usage events are
  turned off.
- **Remember fingerprints** toggle (off by default, same rules as Remember OCR text): keeps each picture's fingerprint
  in `%LOCALAPPDATA%\FindAnything\pictures`; turning it off deletes them.
- **Find similar pictures** on the results' right-click menu, on Explorer's right-click menu for pictures (installer
  and `tools\explorer-menu.ps1`), and as `FindAnything.exe --similar <picture>`.
- Tests: CLIP tokenizer ids, ranking by a sample picture and by a description (skipped when the model is absent).
- **Only pictures** toggle next to Pictures (OCR): one click limits a search to picture files (JPG, PNG, HEIC, WebP, GIF,
  BMP, TIFF) and turns OCR on. Also a "Pictures" preset under File types.
- Picture results show a thumbnail. Hovering it pops up a large preview with the file name and real pixel size, and it
  disappears when the pointer leaves. Every picture, 64 px or 8000 px, gets the same box with its proportions kept;
  tiny ones scale up with crisp pixels. Thumbnails come from Windows' own thumbnail service (fast, cached by Windows,
  upright, any installed codec).

### Fixed
- Searching pictures with **Inside files** and **File names** both off refused to run ("Pick at least one of..."). Reading
  pictures is now its own source: Only pictures works whatever Inside files is set to, and OCR alone searches the pictures.
- "1 files searched" is now "1 file searched".

## [2026.10.11.6] - 2026-10-11

### Added
- **Find keys** mode: finds product keys, license keys, serial numbers, passwords, PINs and door/alarm codes, API
  tokens (GitHub, AWS, Google, Stripe, Slack, OpenAI, Anthropic, GitLab, npm, JWT), private key files, card numbers
  (checksum-valid) and US Social Security numbers. It needs no search text, shows one row per key with its kind, has
  "Copy the key", and an optional filter word. The detectors are plain character checks, and dates, GUIDs, phone
  numbers, URLs and placeholders are rejected (tested).
- **Pictures (OCR)**: text in photos, screenshots and scanned PDFs, read by Windows' built-in OCR (offline) and cached
  per file. A new picture viewer boxes the match on the photo and shows the recognised text.
- **Loose** matching: ignores spaces, dashes and case; O=0, I=l=1, S=5, B=8, Z=2; and OCR's misreads ("1-1" for H,
  "rn" for m, "vv" for w, "cl" for d).
- **Drive check** mode: reads a whole physical drive, read-only, reports how much of it still holds data, and finds a
  text anywhere on it, deleted files included. It offers to restart as administrator. Matches open in the viewer
  straight from the drive.
- Mode tabs (Search text / Find keys / Drive check), and the `--keys` and `--drive-check` start options.

### Changed
- Nothing typed is saved any more: no last search, no recent searches, no remembered folders. History written by
  earlier versions is erased from the settings file the next time the app starts. Toggles and filters are still
  remembered.
- The OCR cache is now opt-in: a **Remember OCR text** toggle, off by default, with a tooltip on the trade-off
  (faster repeat searches against storing the text of your pictures). While it is off nothing is written, and turning it
  off deletes what was cached.

### Fixed
- A double-click in the results now opens the row that was clicked, even if the mouse moved off it.

## [2026.10.11.5] - 2026-10-11

### Changed
- New look across the app, the viewer, the installer and the About box: a dark slate theme with an amber accent,
  matching the icon.
  - A large search field with a focus glow and an amber Search button that becomes Stop.
  - Toggle chips instead of checkboxes, and a Filters panel that folds away.
  - The results list shows a colour badge per file type, the name over its folder, the first match with the hit
    highlighted, and a hit-count pill.
  - A thin overlay scrollbar, a status bar with an animated progress line, dark menus and tooltips, and a dark
    title bar.
- The design is an HTML/CSS mockup (`design/ui/`). `tools/make-skin.ps1` renders its controls into PNG sprites that the
  app draws 9-sliced, so it stays sharp at any display scaling.
- Credits now read "Todd J Sherman" (LICENSE, About box, installer, file details).
- README screenshots.

## [2026.10.11.4] - 2026-10-11

### Fixed
- Ctrl+Alt+F stopped working after updating or reinstalling. The installer deleted and recreated the Start menu
  shortcut, and Explorer dropped the hotkey on the delete without picking up the new file. The installer now saves
  over the existing shortcut and tells Explorer it changed.

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
