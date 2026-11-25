# Ctrl Link Design Direction

Reference: `design/inspiration.png`, chosen by the user. Both apps follow this file, and it overrides generic skill advice
(including unslop's rule against uppercase tracked labels; here they are part of the chosen look).

## The idea

An editorial instrument panel on black. One thing per screen is set **huge in a serif** (a status, a code, a count, a percent),
with quiet uppercase labels around it. Everything else is monochrome. Color appears only where it encodes data (clip kinds).
Hairlines instead of cards. Plenty of black space.

## Tokens

| Token | Dark (signature) | Light | Use |
|---|---|---|---|
| `bg` | `#000000` | `#FFFFFF` | screen background |
| `surface` | `#0F0F10` | `#F4F4F5` | sheets, the rare inset area |
| `hairline` | `#2A2A2C` | `#DEDEE0` | dividers, chart axis, outlines |
| `text` | `#FFFFFF` | `#000000` | headlines, numerals |
| `textSecondary` | `#8E8E93` | `#6B6B70` | labels, captions, units |
| `positive` | `#3DD68C` | `#1E9E5E` | online dot, "+N" deltas, success |
| `danger` | `#F0472E` | `#D63A22` | failed / cancelled |

Clip-kind palette (data color only, same in both modes): text `#F5C26B` (amber), url `#3AA4F2` (sky), image `#E9B6E0` (pink),
files `#2549B8` (deep blue), pending: `textSecondary` drawn as a dashed/hatched bar.

## Type

- **Display serif: Newsreader** (variable, `opsz` + `wght`, OFL) in `design/fonts/Newsreader-Variable.ttf`. Bundle it in each app.
  - Hero numeral: 96–140 pt, weight 300, `opsz` 72, tight tracking (−2%). Digits are lining. Units next to it in the sans at 15 pt, secondary color, baseline-aligned (like `8ч 35мин`, `/100`).
  - Headline: 30–34 pt, weight 400, `opsz` 72, centered, max 2 lines (like "Средние показатели сна").
  - Medium numerals in rows (stats, times): 26 pt serif with sans units.
- **UI sans:** the system font (SF Pro on Mac, Roboto on Android).
  - Labels: 11–12 pt, weight 500, UPPERCASE, tracking +6%, `textSecondary` (like "КАЧЕСТВО СНА", "В СРЕДНЕМ 6 Ч 15 МИН").
  - Body/controls: 13–15 pt regular.

## Components

- **Segmented pill** (like Д / Н / М): outlined capsules with a 1 px `hairline`/`text` border. The selected segment is filled `text` with `bg` text. Used for top-level navigation: **Devices · History · Settings**.
- **Week strip** (like "Июнь 10–16"): horizontally scrollable text tabs, with the selected one underlined. Used in History for days.
- **Stacked bars:** History shows clips per day for the last 7 days, stacked by kind with square tops and 2 px gaps. The legend is a 2–3 column grid of an uppercase label over a short thick color bar (like the inspiration's legend).
- **Sparkline:** one thin `textSecondary` line with small point markers, ending in a filled dot with a value. Used for transfer speed while a file is sending.
- **Ring badge** (like the 90% circle, top right): a 28 pt circle outline with a small number. It shows the number of connected devices, or live transfer percent while sending.
- **Horizon:** behind the hero numeral on the main screen, a very large dim sphere edge (radial gray `#1C1C1E` → `#000`, no color, no glow). It suggests "the network" the same way the moon does. Dark mode only. Leave it out in light mode.
- **Stat row** (like bottom "Длительность сна / Время в кровати"): two columns split by a hairline, each an uppercase label over a medium serif numeral.
- **Floating round button** (like the ↑ circle): a 56 pt `surface` circle with a hairline border. On Android it means "Send clipboard". On Mac, put the same action as a round button bottom-right of the popover.
- Icons: SF Symbols / Material Symbols outlined, thin weight, `text` color. No filled colorful icons, no emoji.

## Screens mapped to the reference

**Devices (home).** Top row: a device-name dropdown (like "Сегодня ⌄") on the left, the ring badge on the right.
- Small label: `ON THIS NETWORK`.
- Headline: "Connected to Pixel 7", "2 devices connected", or "Looking for devices".
- Hero numeral with horizon: clips shared today, unit `/today` beside it, and a `+N` positive delta since the last open.
- Under it, the last-clip line: `13:05 → Pixel 7`, with the item's kind and size in the sans.
- Then the paired-device list: name in serif 22 pt, platform + last seen in labels, online dot `positive`, hairline separators.

**Pair (host).** Label `PAIR A DEVICE`, headline "Scan or enter the code".
- QR code: **black modules on a white rounded square** (12 pt radius, quiet zone of 4 modules), the only white block on the screen. Many phone scanners can't read inverted (light-on-dark) codes, so don't invert it.
- Below the QR, the **6-digit code as the hero numeral** in two groups `042 917`, with a thin countdown line that shrinks.
- SAS confirm: label `CONFIRM ON BOTH SCREENS`, the 4-digit SAS huge, headline "Pixel 7 wants to pair", and two pill buttons, Accept (filled) and Decline (outlined).

**Join.** The same composition. The code entry shows 6 large serif digit slots, each underlined with a hairline.

**Transfer in progress.** Label `SENDING TO PIXEL 7`, headline = file name (2 lines max), hero numeral = percent, a speed sparkline, and a stat row with `SIZE` and `TIME LEFT`.

**History.** Segmented pill on History, a week strip of days, headline "Busy week" / "Quiet week", hero = total clips, label `AVERAGE 14 A DAY`, stacked bars by kind, the legend, then the clip list for the selected day. Each row shows a kind color bar on the left edge, a preview, the source device and the time. Tap a row to copy it again.

**Settings.** Plain list on black. Uppercase section labels, rows with hairline separators, native toggles tinted `text` (not colored).

## Motion

Quiet. The hero numeral counts up over 400 ms ease-out when its value changes. Bars grow from the baseline over 300 ms, staggered 30 ms.
The countdown line is linear. No bounces, pulses or shimmer. Respect Reduce Motion (show final states).

## Platform notes

- **Mac** popover is 380 × ~640 pt. The same layout scales down: hero numeral 96 pt, headline 26 pt. The popover background is `bg` (not vibrancy material), so the editorial black reads.
- **Android:** edge-to-edge. Turn dynamic color OFF, since these tokens replace it. Status bar icons are light on dark. Use `FontVariation` (API 26+) for `opsz`/`wght`.
