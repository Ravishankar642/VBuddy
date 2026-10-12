# ATC Ahead

Warns you by push notification when an online VATSIM controller's estimated
area lies ahead of you on your route, before you'd normally expect to hear
from them.

## Enable it

Add (or edit) this section in `VBuddy.ini`, next to your existing
`[Pushover]`/`[Telegram]`/etc. section — it works with whichever `Driver` you
already have configured in `[General]`:

```ini
[AtcAhead]
Enabled=true
DistanceNm=150
PollIntervalSeconds=30
NotifyOnce=true
Facilities=CTR,FSS
```

All settings and their defaults:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. |
| `Cid` | *(none)* | Identify your aircraft by VATSIM CID instead of callsign. |
| `DistanceNm` | `150` | Alert once a controller's estimated area is within this many NM ahead. |
| `MinDistanceNm` | `0` | Ignore controllers already closer than this (e.g. to skip your departure-area controller). |
| `PollIntervalSeconds` | `30` | How often to poll the public VATSIM data feed (min 15). |
| `NotifyOnce` | `true` | One alert per controller logon. Set `false` to repeat, subject to `CooldownMinutes`. |
| `CooldownMinutes` | `30` | Minimum time between repeat alerts for the same controller, when `NotifyOnce=false`. |
| `RouteRelevantOnly` | `true` | Only look along the filed route. Set `false` to also project your current heading when no usable route is available. |
| `Facilities` | `CTR,FSS` | Comma list from `DEL,GND,TWR,APP,DEP,CTR,FSS`. |
| `Priority` | `1` | Passed to your notification driver like the other message types. |
| `Sound` | *(none)* | Optional per-message sound name (Pushover or Bark only; ignored by Telegram/Gotify). |
| `TestOnConnect` | `false` | Sends one sample "ATC AHEAD" alert right after connecting, to confirm the pipeline works. |
| `BoundaryRefreshDays` | `14` | How often the cached airspace data file is refreshed. |
| `FixesFile` | *(none)* | Optional path to a waypoint CSV (see below) to resolve named enroute fixes. |
| `Verbose` | `false` | Logs every poll cycle to the vPilot debug console, for troubleshooting. |

## What it needs, and where the numbers come from

- **Your position, groundspeed, heading and filed flight plan** come from the
  public VATSIM data feed (`data.vatsim.net`), matched to you by callsign (or
  CID, if you set one). No SimConnect and no Navigraph.
- **Online controllers and their frequencies** also come from the VATSIM feed.
- **Where a controller's area roughly is** comes from the VATSIM community's
  own `vatspy-data-project` (FIR/sector polygons + airport coordinates,
  downloaded once and cached next to the plugin). Controllers below the
  center/FSS level (tower, approach, ground, delivery) don't have a polygon in
  that dataset, so they're shown as a circle around their airport instead.
- **The route itself** is built from your filed route string. Coordinate-style
  waypoints (`46N078W`, `1500N07800E`) and airports in the route are resolved
  for free; named fixes and airways are not, since that needs a real navdata
  subscription. If you provide `FixesFile` (a simple `ident,latitude_deg,longitude_deg`
  CSV — OurAirports' `navaids.csv` format works directly), those can be
  resolved too. Anything left unresolved is skipped, and the plugin falls back
  to a straight (great-circle) line to your destination — this is what "GC"
  or "great-circle" in the log/notification means.

## What "Estimated" means, honestly

This feature cannot tell you who your *next* controller will actually be. It
can only tell you that an online controller's coverage area lies somewhere
ahead of you, roughly how far, and on what basis (an FIR boundary, or an
airport-radius guess). Every notification says "Confidence: Estimated" and
names that basis. Reasons it can be wrong:

- A controller's listed position is a login, not a guarantee of which sector
  they're actually working, or that their sector matches published FIR
  boundaries.
- Great-circle fallback doesn't reflect the airways/holds/vectors you'll
  actually fly.
- Named waypoints/airways are not resolved unless you supply a `FixesFile`.

## A test message

With `TestOnConnect=true`, or by triggering it from your own testing build,
you'll get something like:

```
📡 ATC AHEAD
Controller: Chennai Control (VOMF_CTR)
Frequency: 126.2
Distance: Approximately 150 NM
Status: Online
Confidence: Estimated (filed route; FIR boundary)
Not a guarantee this will be your next controller.
```

## Notes on reliability

- Runs on its own background timer; a failure here (feed unreachable, bad
  data, anything) is caught, logged to the vPilot debug console, and retried
  with backoff — it never touches your other notifications (radio, private,
  SELCAL, Hoppie, disconnect).
- The airspace data file (~2–3 MB) is downloaded once and cached; it's
  refreshed automatically every `BoundaryRefreshDays`.
- Disable it entirely by removing/leaving `Enabled=false` — no other behavior
  changes.
