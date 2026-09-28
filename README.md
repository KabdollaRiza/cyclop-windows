# Cyclop for Windows

A Windows port of [Cyclop](https://github.com/akalikbergenov/cyclop), the
macOS app that turns the MacBook notch into a working tool. Windows has no
notch, so a thin pill at the top centre of the screen stands in for one — the
same idea as the Mac version's stand-in on displays without a notch. Hover it
and the panel unfolds; move away and it folds back.

The code here is a rewrite in C# and WPF — the Mac app is built on AppKit and
SwiftUI, which Windows does not have — but the behaviour, the design and the
data formats follow the original.

## Download

**[Download Cyclop.exe](https://github.com/KabdollaRiza/cyclop-windows/releases/latest/download/Cyclop.exe)**
— Windows 10 (1809 or newer) or 11, 64-bit. One file, nothing to install:
.NET is built in.

1. Put `Cyclop.exe` somewhere it can stay, for example in
   `C:\Users\<you>\AppData\Local\Programs\Cyclop\` or in `Documents`.
2. Double-click it. The first time, Windows shows *"Windows protected your
   PC"*: the app is not signed with a paid certificate, which is all that
   warning means. Click **More info → Run anyway**. It asks only once.
3. The panel unfolds at the top centre of the screen. Cyclop has no ordinary
   window: when you move away, the panel folds into a thin black bar, and
   hovering the bar brings it back. The eye icon in the system tray (by the
   clock, possibly under the **^** arrow) holds the menu.
4. To start Cyclop with Windows, right-click the tray icon → **Launch at
   login**. Started that way it stays folded; opening `Cyclop.exe` again at
   any time — while it runs — just unfolds the panel.

To update, quit Cyclop from the tray menu and replace the file with the new
one. All releases are on the [releases page](https://github.com/KabdollaRiza/cyclop-windows/releases).

## What is ported

| Tab | Status |
|---|---|
| **Shelf** | ✅ Every screenshot lands here by itself (see below). Drag any file onto the bar at the top to keep it here too. Click a card to copy it (file *and* picture — Ctrl+V works in Explorer, chats and editors), Ctrl+click for several, drag a card out to drop the file anywhere, double-click to open, right-click for more |
| **Music** | ✅ Whatever is playing — a YouTube video in any browser, Spotify, any app that shows up on Windows' volume overlay. Thumbnail, title, channel, scrubber, play/pause, previous/next video, ±10 s. With several apps playing, a click on the source name switches between them. Nothing to install in the browser |
| **Clipboard** | ✅ The last 40 copies (text and files), click to copy back. Event-driven via `WM_CLIPBOARDUPDATE` — no polling. Respects the opt-out formats password managers set |
| **Snippets** | ✅ Search, add, delete, click to copy. Same `snippets.json` format as the Mac |
| **Notes** | ✅ Scratch notes, blank ones sweep themselves out. Same `notes.json` format as the Mac |
| Currency, Teleprompter | Planned |
| Calendar, Translate | Not planned for now |

The tray icon replaces the menu bar item: a left click toggles the panel, the
right-click menu has *Catch screenshots*, *Open screenshots folder*, *Launch
at login*, *Open data folder* and *Quit*.

## Screenshots

Every way Windows takes a screenshot ends up on the shelf:

| How | Where Windows puts it | What Cyclop does |
|---|---|---|
| PrtSc, Alt+PrtSc, Win+Shift+S, Snipping Tool, ShareX, Lightshot… | the clipboard | saves it as a PNG in `Pictures\Cyclop` and puts it on the shelf |
| Win+PrtSc, Snipping Tool auto-save | `Pictures\Screenshots` | puts that file on the shelf |
| Xbox Game Bar (Win+Alt+PrtSc) | `Videos\Captures` | puts that file on the shelf |

One snip often goes both ways at once — Snipping Tool copies *and* auto-saves.
A clipboard picture and a new file that arrive within four seconds with the
same pixel size are taken for one snip: the file Windows saved is kept and no
second copy is made.

A copy that carries text as well as a picture — cells from Excel, a paragraph
from Word — is a text copy and is not saved as a screenshot. Pictures copied
back out of the shelf are not saved again.

Nothing in `Pictures\Cyclop` is ever deleted by Cyclop; *Clear* on the shelf
only empties the shelf. Turn the whole thing off with *Catch screenshots* in
the tray menu.

Data lives in `%APPDATA%\Cyclop`. The JSON files there can be copied to and
from `~/Library/Application Support/Cyclop` on a Mac.

## Requirements

- Windows 10 (1809 or newer) or 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build;
  the .NET 9 Desktop Runtime to run a framework-dependent build

## Building

```powershell
cd Cyclop
dotnet build -c Release
.\bin\Release\net9.0-windows10.0.19041.0\Cyclop.exe
```

A single `.exe` to hand to someone else:

```powershell
# needs the .NET 9 Desktop Runtime on the target machine, ~26 MB
# (most of it is the Windows API projection the Music tab needs)
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

# runs anywhere, no runtime needed, ~80 MB — how release builds are made
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

The output is in `bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\`.

The version number lives in one place, the `version` file at the root.

Every push to `main` is built by GitHub Actions; the `.exe` is attached to the
run as an artifact.

## How it differs from the Mac version

- **Opening waits 110 ms instead of 50.** The top edge of a Windows screen is
  where maximised title bars live; a pointer on its way to one should pass
  without unfolding anything.
- **A window dragged to the top edge does not open the panel.** That is how
  Windows maximises it. Any other drag — a file, most likely — opens the panel
  straight on the shelf, ready for the drop.
- **Nothing opens over fullscreen apps** (games, videos, presentations).
- **Typing tabs take the keyboard on click, not on hover.** Windows does not
  let a background app take focus on its own; the click is what grants it.
  When the panel folds, focus goes back to the window that had it.
- **Click-through** is `WS_EX_TRANSPARENT`, toggled by pointer position — the
  counterpart of `ignoresMouseEvents` on the Mac.
