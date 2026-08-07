# DevDX privacy policy

**Last updated:** 4 August 2026
**Applies to:** DevDX for Windows 1.0 and later, distributed as a portable ZIP from GitHub Releases
and as a Microsoft Store app.

This is the document the in-app link (**Settings ▸ About ▸ Privacy policy**) points at, and the one
submitted as the privacy policy URL for the Microsoft Store listing.

---

## The short version

DevDX does not collect anything. There is no account, no telemetry, no analytics, no advertising,
no crash reporting service and no profiling. Everything you paste, open, format, decode, hash or
mask is processed on your own PC, in the DevDX process, and is never transmitted anywhere.

## What DevDX stores, and where

| What | Where | Why |
|---|---|---|
| Your dock layout, pinned tools, shortcuts and preferences | `%AppData%\DevDX\dock.json` | So the dock looks the same next time you sign in |
| A custom icon you choose for a dock item | Referenced by path from `dock.json`; the image itself stays where you put it | So the icon survives a restart |
| A diagnostic log | `%Temp%\devdx.log`, overwritten on every launch | So a crash or a window-placement fault leaves a trace to debug |

That is the complete list of what DevDX writes. All of it stays on your device. None of it is
uploaded, synchronised or backed up by DevDX. You can delete `%AppData%\DevDX` at any time; DevDX
starts again from defaults.

**Tool content is never persisted.** The text in a tool window lives in memory for as long as that
window is open and is discarded when it closes. DevDX has no session restore, no history, and no
scratch cache.

**The diagnostic log never contains your content.** It records window/layout events, failure
reasons and exception types. The Data Masker in particular is bound by a rule that it logs neither
the text it analyses nor the findings it produces.

## Network use

**DevDX makes no network connection unless you turn one on.**

The app has exactly one networked capability, and it ships **off**:

- **Check for updates** (Settings ▸ About). When you enable it, DevDX asks
  `https://api.github.com/repos/framedparadox/dev-dx/releases/latest` whether a newer release
  exists. The request carries no account, no token, no device identifier and no usage data beyond
  the HTTP request itself — GitHub sees an anonymous request the same way it would from a browser,
  and GitHub's own privacy statement governs what it logs. DevDX downloads and installs nothing;
  the most it does is offer a link to the release page.
  This capability is hidden entirely in the Microsoft Store build, because the Store delivers
  updates itself.

Every outbound call in DevDX is gated by a single component (`Services/NetworkPolicy.cs`) that
refuses by default, and an automated test in the build fails if any other part of the code tries to
open a connection around it. If you never enable the update check, DevDX opens no sockets.

## Clipboard

DevDX never polls or reads the clipboard in the background. When a tool window opens it may check
whether the clipboard currently holds *text of a shape that tool understands* in order to offer a
one-click "Paste clipboard" chip. The content is only read — and only into that window — if you
click the chip.

## Files

DevDX opens a file only when you pick it in the Windows file dialog or drop it on the dock. It uses
the standard Windows file pickers, which grant access to that one file. DevDX does not request
broad filesystem access, does not index or scan your disk, and does not open anything you did not
point it at.

## Permissions DevDX requests

The Microsoft Store build declares one capability, `runFullTrust`. It is the capability every
full-trust Win32 desktop app packaged as MSIX must declare; DevDX needs it for the Win32 window
chrome (rounded corners, always-on-top, the notification-area icon and global shortcuts), which
have no WinRT equivalent. DevDX does not request the location, camera, microphone, contacts,
calendar, health, or broad-filesystem capabilities, and it launches no other program.

## Children

DevDX is a developer utility. It is not directed at children, and because it collects nothing it
holds no personal data about anyone, including children.

## Your rights

Because DevDX holds no data about you on any server, there is nothing to request, export, correct
or erase from us. Data DevDX wrote on your own PC is yours to inspect or delete directly:

- Settings and layout — `%AppData%\DevDX\` (also exportable as a file from Settings ▸ Backup)
- Diagnostic log — `%Temp%\devdx.log`

Uninstalling DevDX and deleting `%AppData%\DevDX` removes everything it ever wrote.

## Third parties

DevDX contains no advertising SDK, no analytics SDK and no third-party service integration. Its only
runtime dependencies are Microsoft's Windows App SDK and the .NET runtime, both shipped inside the
app. No data is sold, shared, or disclosed to anyone, because none is collected.

## Changes to this policy

Material changes will be published in this file and noted in the release notes for the version that
introduces them. The "Last updated" date above always reflects the current revision.

## Contact

Questions or a privacy concern: open an issue at
<https://github.com/framedparadox/dev-dx/issues>.
