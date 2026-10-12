# Atticus for Windows

Hold <kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>Space</kbd>, say what you need, let go.
Atticus moves your windows, finds your files, reads your screen and gets it done.

A Windows port of [SaarthurR/atticus-mac](https://github.com/SaarthurR/atticus-mac). Same brain, same overlay, same idea:

- **Fast.** Simple asks skip the AI: "open Notepad", "Chrome on the left of my monitor", "search for pizza near me". Ask for an action once and Atticus learns it, so next time it runs with no AI and for free.
- **No API key.** It runs on the Claude Pro or Max plan you already have, through Claude Code.
- **Remembers you.** Say "remember…" and every ask after that knows it. It picks up who you are and fixes its own mistakes when you correct it.
- **Private ears.** Whisper turns your voice into text on your PC. Audio never leaves it.

## Just ask

| You say | Atticus |
|---|---|
| *"Code on my big screen, Chrome and Terminal on the laptop."* | Arranges three windows across two displays. |
| *"Open Spotify."* · *"Notepad on the left."* | Runs instantly, with no AI at all. |
| *"Find my notes about the Tokyo trip."* | Searches your files (Windows Search) and opens the match. |
| *"What's wrong with this code?"* | Looks at your screen and answers in a line. |
| *"Reply: sounds good, see you at 7."* | Types it into the app you're in. It never sends. |
| *"Message Sam on Discord: running 10 minutes late."* | Opens the chat with Ctrl+K and types it; you Allow the send. |
| *"Play my late night coding playlist on Spotify."* | Plays it, no clicks. |
| *"When I say focus mode, close Slack and open my to-do list."* | Remembers it. Next time, two words do it all. |
| *"Tell me when the download finishes."* | Watches that window with Windows' own OCR, no AI while it waits. |
| *"Note in Joplin: call the landlord about the heater."* | Writes it through Joplin's own API (turn on Tools → Options → Web Clipper). Find, read and add to notes too. |
| *"In Spotify, turn on shuffle."* | Finds the button through UI Automation and presses it, no mouse needed. |

## Install

You need:
- **Windows 11** (Windows 10 2004+ works, without the glass look)
- **A paid Claude plan** (Pro or Max) and [Claude Code](https://claude.com/claude-code). No Claude Code yet? The setup window installs it and signs you in.
- Git for Windows and Python 3.11+ (the installer gets them with winget if missing)

Paste this in PowerShell:

```
irm https://raw.githubusercontent.com/FindlayHi/atticus-windows/main/install.ps1 | iex
```

It downloads Atticus to `%USERPROFILE%\.atticus`, builds `%LOCALAPPDATA%\Programs\Atticus\Atticus.exe` (the .NET 10 SDK is fetched per user if missing, no admin), and opens it. Atticus starts at login (Settings → General) and updates itself: every 6 hours it pulls from GitHub, builds the new version beside the running one, and swaps it in when you're not using it (turn that off in Settings → About & Updates). Local edits in `.atticus` or a failed build leave the running version alone.

Working on the code instead? Clone anywhere and run `powershell -ExecutionPolicy Bypass -File .\build.ps1` after each change.

The first launch opens a setup window: Claude sign-in, the microphone, and the speech model (~150 MB, once). After that Atticus lives in the tray. Open it from Start ("Atticus") for Settings.

Remove: quit Atticus from the tray, then delete `%LOCALAPPDATA%\Programs\Atticus`, `%LOCALAPPDATA%\Atticus`, `%APPDATA%\Atticus` (your settings and history), the Start menu shortcut, and the `Atticus` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## How it works

- `win/Atticus/` (C#, WinForms + WebView2): the tray app. A low-level keyboard hook for the shortcut, the microphone and Whisper (`Speech.cs`), the overlay (`Overlay.cs`, a port of the Mac app's `App` class), Settings and setup windows, window OCR for watches (`WindowText.cs`).
- `atticus.py`: the brain, kept close to upstream so fixes merge. Instant commands, learned shortcuts, the system prompt, and `claude -p --model haiku` runs.
- `winctl.py`: the Windows hands that replace osascript, mdfind, screencapture, pbpaste and Peekaboo: displays (with laptop vs monitor from the display configuration), window placement, Start-menu apps, clipboard and typing, screenshots, Windows Search, toasts, Recycle Bin, and UI Automation for buttons, menus and fields inside apps (`atticus --ui see "Spotify"`, then `atticus --ui click "Shuffle"`), plus the browser's address as context.
- `ui/`: the upstream overlay, Settings and setup pages. A small shim maps WKWebView's `webkit.messageHandlers` onto WebView2, so the pages run unchanged apart from Windows wording.

The AI's Bash is Git Bash. Its helpers are one short command, `atticus --place …`, `atticus --find …`, from a two-line script Atticus puts on its PATH.

## Integrations

Settings → Integrations lists every service Atticus knows, with what it lets you say, the fields to fill in (with example addresses), and a "Where's my key?" button that opens the right page on your server. Set up the ones you run:

| Service | Say things like |
|---|---|
| **Home Assistant** (MCP) | "turn off the office lights", "is there motion in the office?", "set the heat to 21" |
| **Jellyfin** | "what should I keep watching?", "play Breaking Bad on the living room TV" |
| **Seerr** | "add Severance season 2", "what have I requested?" |
| **Sonarr/Radarr** | "what's downloading?", "when's the next Abbott Elementary?" |
| **Immich** | "show me photos from the cottage last summer", "open the Japan album" |
| **Nextcloud** | "find my lease PDF", "share the trip photos folder", "what's on my calendar this week?" |
| **Paperless** | "find my car insurance policy", "when does the fridge warranty end?" |
| **n8n** | "run the morning report": every workflow with a Webhook trigger becomes a command |

Keys live in Windows Credential Manager. The AI only hears about a service when your request is about it, so each one costs nothing until you use it. Locks, doors, garages and alarms, public share links and n8n runs wait for your Allow.

**Plugins or MCP?** Most services are small plugins: one line of help for the AI, one command. Home Assistant goes through its own MCP server instead (its Assist tools already know your devices); Atticus connects to it only for requests about the home, because MCP tool lists add to every request they ride on.

**Add your own:** one file in `integrations/` (see `integrations/__init__.py`): the fields it needs, a regex of words that bring it up, one line of help, and `run()`. Or `MCP()` returning an HTTP MCP server, like `integrations/homeassistant.py`.

## Safety

Safe mode (on by default) shows an Allow card before anything deletes, sends, buys, changes a file or runs a system command (`Remove-Item`, `reg add`, `taskkill`, `Stop-Computer`…). Clicks inside apps are judged by the button's name, so pressing "Send", "Delete" or "Buy now" asks first, and so does Ctrl+Enter. Enter asks too when it would send a message just typed (Discord, Slack, Teams), but not after typing into a search box like Discord's Ctrl+K switcher. `rm`, `del`, `format`, `diskpart` and shutdown are always blocked. Deletes go to the Recycle Bin. Every exact command is in the ask log.

Unlike the Mac version, there is no sandbox: Claude Code's sandbox isn't available on native Windows, so on Windows safe mode is the Allow check alone.

## Not on Windows yet

- File previews in the "which one?" card.
- Reminders and Notes scripting: notes go to `Documents\Atticus Notes.md`, timed reminders use a watch.

## Test

The app checks for updates at launch + 1 min and every 6 hours; `Settings → About & Updates → Check Now` checks at once.

`python -m unittest discover -s tests -v` and `python atticus.py --selftest` (with `PYTHONUTF8=1`), plus `Atticus.exe --selftest` after building. `Atticus.exe --transcribe <16 kHz wav>` runs a file through the speech path; `Atticus.exe --text "<app>" [title words]` prints what OCR reads in that window. `ATTICUS_DEMO="<request>"` runs one request at launch without the mic; `ATTICUS_CAPTURABLE=1` lets screenshots see the box (it hides from capture by default).

## Budget

Same limits as upstream: instant commands $0; AI asks a median under $0.002 and 6 s over the last 50 (`python atticus.py --budget`); a system prompt under 10,000 characters with every option on (a test enforces it); $0 while anything waits.

## License

MIT. See `LICENSE`. Original Atticus by SaarthurR. `ui/orbs.js` is from [thinking-orbs](https://github.com/Jakubantalik/thinking-orbs), also MIT. Speech by [whisper.cpp](https://github.com/ggerganov/whisper.cpp) through [Whisper.net](https://github.com/sandrohanea/whisper.net) (MIT).
