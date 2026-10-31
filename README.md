# PDF Viewer

A very simple and fast PDF viewer for Windows 10 and 11. No toolbars, no ads, no account: a borderless
window with the page in it, and everything else in the right-click menu.

- **Instant page changes.** Pages are drawn from a cache, never waiting for the renderer: a thumbnail of
  every page is ready shortly after opening, and the next pages are pre-rendered at full resolution.
  One page per wheel notch, never skipping a page, however fast the wheel spins.
- **Full-screen page mode**, fit width / fit page, single page / continuous, always on top.
- **Small and portable:** a 4.7 MB folder (2.3 MB zipped), no installer. It runs on the WebView2 engine
  that Windows 11 already has.

## Install

1. Download `PDF Viewer <version>.zip` from the [latest release](https://github.com/Benn25/PDFviewer/releases/latest)
   and unzip it somewhere it can stay.
2. Double-click **Add to Windows.cmd**: a Start Menu entry, "PDF Viewer" in the *Open with* list of PDF
   files and an *Open with PDF Viewer* right-click entry. It writes only to your own account (HKCU), no
   administrator rights.
3. To open PDFs with it on double-click: right-click a PDF > *Open with* > *Choose another app* >
   *PDF Viewer* > *Always*.

The app isn't signed, so on first launch Windows may show "Windows protected your PC": *More info* >
*Run anyway*. To update, close the viewer and unzip the new version over the folder. To remove, run
**Remove from Windows.cmd** and delete the folder.

## Use

| | |
|---|---|
| Drag anywhere | move the window (drag its edges to resize) |
| Right-click | menu |
| Mouse near the top-right corner | a close button fades in |
| ← → / PgUp PgDn | previous / next page |
| ↑ ↓ / Space / wheel | scroll, and turn pages one by one |
| Home / End | first / last page |
| A number, then Enter | go to that page |
| F, Ctrl+P or double-click | full-screen page mode, and back (Esc leaves too) |
| Z | fit width ⇄ fit page |
| C | continuous ⇄ single page |
| T | always on top |
| Ctrl+O | open a PDF (or drop one on the window) |
| Ctrl+W | close |

## Build

Needs Windows 10/11 and Node.js (npm fetches pdf.js). The exe is compiled by the .NET Framework compiler
that ships with Windows, and the first build downloads the WebView2 SDK from NuGet.

```
npm install
npm run build     # dist\PDF Viewer\ and dist\PDF Viewer <version>.zip
npm run deploy    # the same, then copies it over an installed copy (path in package.json)
```

## Release

Bump `"version"` in `package.json`, commit, then tag and push:

```
git tag v2.3.0
git push origin main v2.3.0
```

GitHub then builds the app on Windows and publishes `PDF Viewer 2.3.0.zip` on the Releases page
(`.github/workflows/release.yml`). The tag has to match the version in `package.json`. The link to hand
out for the newest version is https://github.com/Benn25/PDFviewer/releases/latest

## How it's made

- `index.html`: the whole viewer, on [pdf.js](https://github.com/mozilla/pdf.js). It also works on its
  own in a browser.
- `host/PDFViewer.cs`: the window around it: WinForms and WebView2, with a real but invisible frame
  (shadow, rounded corners, snapping) and no title bar.
- `windows/`: the Add / Remove from Windows scripts and the read-me files shipped in the zip.
- `build/`: `build.ps1` and `make-icon.ps1`, which draws the icon.

The built app includes pdf.js (Apache-2.0) and the WebView2 SDK libraries (Microsoft, BSD-style
licence), each with its licence file.
