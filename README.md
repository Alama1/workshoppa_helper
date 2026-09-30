# Workshoppa Helper

A Dalamud plugin for FFXIV that automates the retainer logistics around [Workshoppa](https://github.com/VeraNala/Workshoppa): it fetches the materials your island/FC workshop queue still needs from your retainers, and stashes finished goods and clutter back into them — walking to the bell, driving the transfer windows, and returning to the fabrication station on its own.

## What it does

- **Pull materials from retainers** — reads Workshoppa's queue, computes which materials are still missing (accounting for phases already contributed), visits your retainers and withdraws exactly what's needed, then walks back to the fabrication station and resumes Workshoppa.
- **Stash into retainers** — deposits everything from your bags *except* materials the current queue still needs, topping up existing partial stacks first. Stops with a clear message if all retainers are full.
- **Auto-stash** — while Workshoppa is crafting, watches your free bag slots and automatically starts a stash run when they drop below the configured threshold (with a cooldown between runs).

Runs can be aborted at any time by pressing ESC twice, from the window, or via `/whelper abort`.

## Requirements

- The workshop territory (free company or island workshop), with a **retainer bell** reachable inside.
- [Workshoppa](https://github.com/VeraNala/Workshoppa) — either the standalone plugin or the VIWI module; the queue is read through it and it is paused/resumed around each run.
- [AutoRetainer](https://github.com/ffxiv-code/AutoRetainer) (optional) — if installed, it can be suppressed while a run is active, and its item protection list can be respected.

## Usage

Open the window with `/whelper` (or use the buttons in it):

| Command | Action |
| --- | --- |
| `/whelper` | Toggle the main window |
| `/whelper pull` | Pull missing queue materials from retainers |
| `/whelper stash` | Stash non-queue items into retainers |
| `/whelper abort` | Abort the running automation |

## Settings

- **Auto-stash when bags fill up** and the **free slot threshold** that triggers it.
- **Pull reserve slots** — stop withdrawing once your bags have this few slots left.
- **Suppress AutoRetainer during runs** / **respect its protection list**.
- **Movement timeout** and **addon timeout** (seconds) for each leg/wait state.
- **Excluded retainers** — retainers that are never visited.
- **Verbose chat output** for run progress and summaries.
