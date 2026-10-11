# Telegram Control

Run vPilot from your Telegram bot, look up live information, and get alerts.
Private messages, radio, SELCAL, disconnect and Hoppie relaying are still handled by
VBuddy's normal notifier; this feature adds the **two-way** part.

## Setup

1. Create a bot and get your `BotToken` and `ChatId` (see [telegram.md](telegram.md)).
2. In `VBuddy.ini` fill in `[Telegram]` and set `[TelegramControl]` `Enabled=true` (see the sample `VBuddy.ini`).
3. Restart vPilot. You should get "VBuddy Telegram control is online." and a keyboard of quick buttons.
   Typing `/` in the chat also shows a menu of every command.

## Commands

### Information
| Command | What it does |
| --- | --- |
| `/where` | Your position, altitude, ground speed, heading, squawk, QNH, plan and a map pin |
| `/where CALLSIGN` | The same for any pilot online |
| `/status` | Connection state, callsign, how long online, Mode C, alert settings |
| `/atc` | Online ATC and ATIS for your departure and arrival airports, with frequencies |
| `/atc EGLL KLAX` | Online ATC for the airports/prefixes you name (`KLAX` also finds `LAX_TWR`) |
| `/freq CALLSIGN` | Frequency of an online controller (same lookup as `/atc`) |
| `/atis [ICAO]` | ATIS text (or controller info text) from the data feed |
| `/metar [ICAO...]` | METAR for up to 4 airports (defaults to your departure and arrival) |
| `/fp` | Your filed flight plan |
| `/history [n]` | Last messages seen this session |
| `/zulu` | Current Zulu time |

### Network
| Command | What it does |
| --- | --- |
| `/connect` | Connect with the last callsign / aircraft / SELCAL |
| `/connect CALLSIGN TYPE [SELCAL]` | Connect with details you give |
| `/disconnect` | Asks for `/confirm` within 30 s (`/disconnect now` skips the question) |
| `/confirm` | Confirms a pending `/disconnect` |
| `/fileplan ...` | File a flight plan. Send `/fileplan` alone to see the format |

### Communicate
| Command | What it does |
| --- | --- |
| `/msg CALLSIGN text` (or `/message`) | Private message |
| `/reply text` (or `/r`) | Reply to the last private message |
| `/radio text` | Text on your current transmit frequency |
| `/wilco` `/roger` `/standby` `/unable` | One-tap radio replies |
| `/info CALLSIGN` | Request a controller's info text |

### Aircraft radio
| Command | What it does |
| --- | --- |
| `/ptt on` / `/ptt off` | Push-to-talk (auto-releases after `PttMaxSeconds`) |
| `/ident` | Squawk ident |
| `/modec on` / `/modec off` | Transponder Mode C |

### Alerts
| Command | What it does |
| --- | --- |
| `/watch PREFIX...` | Alert when a controller with that callsign start comes online (`/watch EGLL KLAX`) |
| `/unwatch PREFIX` / `/unwatch all` / `/watchlist` | Manage the list |
| `/listen on` / `/listen off` | Forward every text radio message on your frequencies |
| `/mute [minutes]` / `/unmute` | Pause automatic alerts (default 60 min) |

### Filing a flight plan
```
/fileplan KLAX RKSI RKSS FL350 480 1200 1230 1500 L heavy | DCT SYRAH J1 | PBN/A1
          DEP  ARR  ALT  CRZALT SPD DEP  ETE  FUEL SFX flags  route         remarks
```
`ALT` is the alternate airport (`-` for none). `CRZALT` is `FL350` or `35000`. Speed is knots.
`DEP` time is zulu HHMM. ETE and FUEL are hours+minutes as HHMM (`1230` = 12 h 30 min).
Optional flags: `heavy`, `vfr`. Route and remarks are optional.

## Not possible (vPilot's plugin SDK has no such function)

* **Tuning COM frequencies** and **setting the squawk code**. Only ident and Mode C are exposed.
  `/atc` and `/freq` tell you what to tune, but you must tune it in the sim.
* **Reading your aircraft's position from the sim.** `/where` uses the public VATSIM data feed,
  so it is about 15 seconds behind and a new connection can take up to a minute to appear.
* Flight controls (altitude, heading) need a separate simulator connection.

## Settings (`[TelegramControl]` in `VBuddy.ini`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch |
| `RelayBroadcast` | `true` | Forward network broadcast messages |
| `NotifyConnect` | `true` | Message when you connect |
| `NotifyDisconnect` | `false` | Message when you disconnect |
| `ConfirmDisconnect` | `true` | `/disconnect` needs `/confirm` |
| `ListenAllRadio` | `false` | Forward every text radio message |
| `PttMaxSeconds` | `30` | PTT auto-release (5-120) |
| `Watch` | empty | Controller callsign starts to alert on, comma separated |

## Security

* Commands are only accepted from the configured `ChatId`. Anyone else is ignored (the vPilot debug window logs it).
* Treat the bot token like a password. If it leaks, revoke it in @BotFather with `/revoke`.
* Commands queued while vPilot was closed are discarded at startup.
* `/radio`, `/wilco` etc. transmit on whatever frequency vPilot is currently set to transmit on.

## Notes

* Telegram allows only one program to poll a bot at a time. A "409" message in the debug window means
  another app (or a webhook) uses the same bot; use a dedicated bot.
* Saved next to `VBuddy.dll`: `VBuddy.identity` (last callsign/aircraft) and `VBuddy.watch` (watch list).
