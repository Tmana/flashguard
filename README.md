# Flash Guard

A Windows tray app that watches every display for flashing content (the kind that can trigger
photosensitive seizures) and suppresses it as it happens.

> Flash Guard is an aid, not a medical device. It reacts to flashing, so it can't promise that every
> flash gets caught. Keep using your other precautions.

## How it works

1. **Capture**: each monitor gets its own thread using DXGI Desktop Duplication. The thread sleeps
   in the driver until something on that screen changes.
2. **Detect**: the GPU shrinks each frame to a grid of about 128×72, and only that small grid is
   copied back to the CPU. Each cell is tracked for luminance swings using the WCAG 2.3.1 / Harding
   definitions: a ≥10% change in relative luminance where the darker state is below 0.8, plus
   saturated-red transitions. A transition only counts when enough of the screen changes together.
   Transitions are counted over a sliding 1-second window.
3. **Suppress**: once the count passes your trigger, a click-through overlay covers that monitor.
   Screen capture can't see the overlay, so detection keeps watching the real content underneath,
   and recordings and streams aren't affected.
   - **Smooth** (default) shows a GPU-filtered copy of the screen in which each pixel's brightness
     may only change at a set rate. At ≤0.6 × full range per second, nothing that flashes 3 or more
     times a second can swing more than 10%.
   - **Dim** puts a dark layer over the screen instead. It's cheaper but less effective.
4. Suppression stays on until there have been no flashes for the hold time, then fades out over
   0.6 s. While it's active, a small label appears in the corner you pick.

Background cost (3 monitors, 3440×1440@144 + 2560×1440 + 4K): about 4% of one CPU core and about
1.4% GPU while one screen is constantly animating, and close to zero when the screens are static.
Analysis is capped at 60 Hz on the newest frame, and readbacks never stall on the GPU.

## Use

- **Ctrl + Alt + Shift + F** (you can change it) opens or closes the settings. Left-clicking the
  tray icon does the same. Launching the exe again also opens the settings of the copy that's
  already running.
- Tray icon colours: teal = watching, amber = suppressing, grey = off.
- Settings are saved to `%APPDATA%\FlashGuard\settings.json`, and the log to `log.txt` in the same folder.
- `FlashGuard.exe --selftest` turns on a steady 15% dim for 2.5 s (no flashing) and checks that
  capture, click-through and capture exclusion all work on your setup. Results go to the log, and
  the exit code is 0 on success.
- Set `FLASHGUARD_STATS=1` before starting to log per-monitor performance every 10 s.

| Setting | Default | Notes |
|---|---|---|
| Trigger at | 2 flashes/s | 1–3. Lower reacts sooner. |
| Brightness change | 10% | The luminance swing that counts as a transition. |
| Flashing area | 3% of screen | How much of the screen must change together. |
| Detect red flashes | on | WCAG saturated-red rule. |
| Stay on for | 3 s | Hold time after the last flash. |
| Smooth: max change | 0.5 × range/s | Lower = stronger smoothing and more motion trails. |
| Always on | off | Smooth all the time, so even the first flash is caught. |

## Build

```
dotnet build src -c Release
dotnet test tests
dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Requires the .NET 9 SDK and Windows 10 2004 or later (needed for `WDA_EXCLUDEFROMCAPTURE`).

## Known limits

- **It reacts after the fact.** The first transitions, before the trigger is reached, do get
  through. At the default settings that's 2 flashes, roughly 0.1–0.3 s of flicker. Turn on
  *Always on* if you need protection from the very first frame.
- **DRM video** (Netflix and similar in browsers) shows up as black to screen capture, so
  flashing in it can't be detected.
- **Exclusive-fullscreen games** can block capture and draw over overlays. Use borderless or
  windowed mode. Flash Guard reconnects on its own when capture comes back.
- **HDR displays**: Smooth mode shows the SDR-mapped image while it's active.
- **Rotated displays** fall back to Dim mode.
