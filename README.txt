Ya Mini Player 1.0.0
====================
A tiny always-on-top remote for Yandex Music on Windows 10 / 11.

Nothing else needs to be installed. Everything the app uses is already part of Windows.

Start using it
--------------
1. Extract this zip to any folder.
2. Start Yandex Music (the desktop app, or music.yandex.ru in a browser) and play something.
3. Either:
   - double-click YaMiniPlayer.exe to just run it, or
   - double-click Install.cmd to install it. That adds Desktop and Start menu shortcuts
     and an entry under Settings > Apps. No administrator rights are needed.

The first time, Windows SmartScreen may say "Windows protected your PC" because the app
is not signed. Click "More info" -> "Run anyway".

Controls
--------
- Previous / Play-Pause / Next buttons
- Drag anywhere on the player to move it
- Pin button: yellow upright pin = always on top, tilted outline = normal window
- Double-arrow button (or double-click the player): switch view, full -> compact -> vinyl
- Vinyl view: a record with the cover on its label that spins while music plays
- Volume bar on the left: click or drag it, or scroll the mouse wheel over the player.
  It changes only Yandex Music's volume, never the computer's master volume.
  It works with the Yandex Music desktop app once it has started playing.
- Right-click for the menu: Full size / Compact / Vinyl, Volume bar, Always on top,
  Open Yandex Music, Open log folder, Exit
- If nothing is playing, the play button opens Yandex Music

If something goes wrong
-----------------------
- "Press play to open" means Yandex Music is closed or has not played anything yet.
  Start a track in Yandex Music and the player picks it up within a second.
- The player keeps a log of problems. Right-click it and choose "Open log folder",
  then send log.txt to whoever gave you the app.
- Settings (position, view, pin) are kept in the same folder as settings.txt.
  Delete that file to reset the player.

Remove it
---------
- If you used Install.cmd: Settings > Apps > Installed apps > Ya Mini Player > Uninstall,
  or run Uninstall.cmd. This also removes the saved settings.
- If you only ran the exe: close the player and delete the folder.

Notes
-----
- Requires Windows 10 version 1809 or later, or Windows 11.
- The player uses Windows' own media controls, so it never asks for your Yandex login.
- It prefers the Yandex Music app. If that is not running it controls whatever
  Windows reports as the current media player (e.g. a browser tab).
- It has no taskbar button. If you cannot find it, start it again: it stays on top
  when pinned, and a second start does nothing while one is already running.
