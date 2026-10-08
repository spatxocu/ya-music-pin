Ya Mini Player
==============
A tiny always-on-top remote for Yandex Music on Windows 10 / 11.

How to use
----------
1. Start Yandex Music (the desktop app, or music.yandex.ru in a browser) and play something.
2. Run YaMiniPlayer.exe. Nothing to install.

   The first time, Windows SmartScreen may say "Windows protected your PC"
   because the app is not signed. Click "More info" -> "Run anyway".

Controls
--------
- Previous / Play-Pause / Next buttons
- Drag anywhere on the player to move it
- Pin button: yellow upright pin = always on top, tilted outline = normal window
- Double-arrow button (or double-click the player): switch between full and compact size
- Right-click for the menu: Compact mode, Always on top, Open Yandex Music, Exit
- If nothing is playing, the play button opens Yandex Music

Position and mode are remembered in %APPDATA%\YaMiniPlayer\settings.txt.

Notes
-----
- The player uses Windows' own media controls, so it never asks for your Yandex login.
- It prefers the Yandex Music app. If that is not running it controls whatever
  Windows reports as the current media player (e.g. a browser tab).
- To start it with Windows: press Win+R, type shell:startup, and put a shortcut
  to YaMiniPlayer.exe in the folder that opens.
