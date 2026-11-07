# HeatingScheduler

A .NET 10 worker service that runs underfloor heating at the lowest [Octopus Agile](https://octopus.energy/smart/agile/) cost, while making up heat lost during off periods, by setting room setpoints on a **Wavin Sentio** system.

It runs two background services in one process:

| Service | What it does |
|---|---|
| `PriceFetchService` | Every 30 minutes it fetches all published Agile prices. Octopus publishes the next day's prices at about 16:00, so new prices arrive about once a day. When they do (and at startup) it re-tunes the rules from room temperatures, then emails the plan if it has changed. |
| `HeatingControlService` | Just after every half-hour boundary, and at least every 5 minutes, it reads each room's setpoint from the Wavin Sentio cloud. Any room that doesn't match `OnTemperature` or `OffTemperature` for the current slot is set. |

The two services share the schedule in memory.

### How the on/off schedule is chosen

- **The rules:**
  - An off period lasts at most `MaxOffMinutes`, so heat loss is limited.
  - After an off period of length *F*, the heating must stay on for at least `OnToOffRatio` × *F* (rounded up to whole half-hours) to make up the heat lost. It may stay on longer.
  - Every on-block lasts at least `MinOnMinutes`.
  - For example, with a ratio of 0.5 and a 3-hour maximum, 2 hours off must be followed by at least 1 hour on.
- **The solver:** finds the on/off pattern over every known price (7–31 hours ahead, depending on the time of day) with the **lowest total price** that obeys the rule. It uses dynamic programming over the half-hour slots, so the result is exactly optimal, not an approximation. With positive prices it keeps the heating off through expensive periods and does the make-up heating in the cheapest slots. Any make-up heating still owed when the plan ends is charged at the plan's average price, so the plan doesn't treat a long final off period as free. When prices are negative, the heating comes on.
- **Re-planning:** when each half-hour slot starts, the solver re-plans from what actually happened so far, and that slot's decision is then fixed. Because the plan always runs to the end of the known prices, it only changes when new prices arrive, when tuning changes the rules, or after a restart. The log shows the plan whenever it changes.
- **After a restart:** the history is lost, so the solver assumes the heating has been on with no heat left to make up. It is free to switch off straight away, even if the heating had already been off for a while before the restart.

### Tuning from room temperatures

The make-up ratio and maximum off period can be worked out from your rooms instead of fixed by hand. This is on by default (`Tuning:Enabled`).

- **Readings:** the Sentio keeps months of room history (air and floor temperature, humidity and setpoint). The app reads the last 60 hours for each room it tunes against and counts the rises and falls that start in the last 48; the 12 hours before that are only used to confirm the first turning points, at 5-minute resolution, using the same unofficial API call as the Wavin Sentio portal (`GetSentioRoomTimeSeries`). A reading counts as on or off when its setpoint is the on or off temperature; readings with any other setpoint (for example, set by hand) are left out. Nothing needs to be learnt after a restart.
- **Recalculation:** whenever new prices arrive (about once a day) and at startup, for each room. If it fails, it is retried at the next price check (30 minutes later). Rates are measured between the temperature's **turning points** (peaks and troughs), not between switching times. With underfloor heating the air keeps falling for a while after the heating comes on, and keeps rising after it goes off; measuring from the turning points leaves that lag out.
  - **Turning points:** the readings are smoothed with a 30-minute median, and a peak or trough is confirmed once the temperature has moved at least 0.2°C back from it. Rises or falls shorter than an hour (an oven, a shower, the sun) are removed, and the movement either side of them is merged.
  - **Which rises and falls count:** a rise counts as heating if the heating was on for at least 70% of it, and a fall counts as cooling if the heating was on for at most 30% of it. Rises with the heating off (such as the sun) and movements with the heating mixed are left out, as is the movement still in progress. A turning point at the very start of the readings only counts if the temperature moved at least 0.2°C into it, so a movement cut off by the start isn't measured over part of its length.
  - **Rates:** each rise or fall is measured with a straight-line fit through its readings. The **heating rate** and **cooling rate** are the medians.
  - **Maximum off** = (median temperature at the start of the falls − `Tuning:MinTemperature`) ÷ cooling rate.
  - **Make-up ratio** = cooling rate ÷ heating rate.
- **Combining rooms:** only the rooms in `Tuning:Rooms` are used, if set. Ideally pick a room whose temperature mostly reflects the heating. Among those rooms, the most demanding one decides, using the shortest maximum off and the highest ratio. The maximum off is rounded down to whole half-hours. Both values are kept within safety bounds (max off 1–24 hours, ratio 0.05–3.0). A new ratio is only applied if it differs from the current one by more than 10%. The values then replace the `Schedule` values, and the plan is recalculated.
- **When a value can't be measured,** the current value is kept. For example, there may be no finished rise with the heating on yet. The values in `Schedule` are the starting point.

### Email notifications

When `Email:Enabled` is true, the app emails the plan whenever it changes: new prices, a tuning change or a restart. Slots dropping off the front as time passes don't count as a change, so that's usually one email a day, at about 16:00. The email shows:

- why the plan changed
- the current slot's state
- each on/off block with its times and average price
- the rules in force, with their previous values when tuning changed them
- what tuning measured for each room

Mail is sent over SMTP. For Gmail, use `smtp.gmail.com`, port 587, your Gmail address as `Username`, and an [app password](https://myaccount.google.com/apppasswords) (not your normal password) as `Password`. Outbound port 25 is blocked on EC2, so use 587 or 465. If sending fails, it's retried at the next price check.

> **Notes**
> - The Wavin Sentio cloud API is **unofficial**. It is ported from the community library [djerik/wavinsentio](https://github.com/djerik/wavinsentio). Wavin could change it without notice.
> - **The schedule always wins.** If you change a room's setpoint in the Wavin app, the service puts it back at the next check.

## Configuration (`src/HeatingScheduler/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Octopus:ProductCode` | `AGILE-24-10-01` | Your Agile product. Check your Octopus account or `https://api.octopus.energy/v1/products/?brand=OCTOPUS_ENERGY`. |
| `Octopus:RegionCode` | `H` | Region letter A–P: the last letter of your tariff code (`E-1R-AGILE-24-10-01-C`). |
| `Schedule:OnToOffRatio` | `0.25` | Starting value when tuning is on. Minimum on-time after an off period, as a multiple of that off period (rounded up to whole slots). For example, 0.25 means 2 h off requires at least 30 min on. |
| `Schedule:MaxOffMinutes` | `600` | Starting value when tuning is on. Longest allowed off period (a multiple of 30). |
| `Schedule:MinOnMinutes` | `60` | Shortest on-block (a multiple of 30). Use `30` for no minimum beyond the make-up time. |
| `Schedule:FallbackState` | `On` | State used when there's no price for the current slot, e.g. if the Octopus API is down. |
| `Tuning:Enabled` | `true` | Work out `OnToOffRatio` and `MaxOffMinutes` from room temperatures. |
| `Tuning:MinTemperature` | `21.0` | Air temperature every room should stay above during off periods. Must be between the off and on temperatures. |
| `Tuning:Rooms` | `["AT3 Lobby"]` | Room ids or names to tune against, e.g. a room away from windows, ovens and showers. When empty, all controlled rooms are used. |
| `Wavin:Email` / `Wavin:Password` | — | Wavin Sentio app login. **Secret, so don't put it in appsettings.json.** |
| `Wavin:Rooms` | `[]` | Room ids or names to control. When empty, every room is controlled. Unknown names are logged together with the list of available rooms. |
| `Heating:OnTemperature` | `23.0` | Setpoint (°C) during on slots. |
| `Heating:OffTemperature` | `16.0` | Setpoint (°C) during off slots. Must be below `OnTemperature`. |
| `Email:Enabled` | `false` | Email the plan whenever it changes. |
| `Email:To` | `[]` | Addresses that receive the emails. |
| `Email:From` | `""` | Sender address. Defaults to `Email:Username`. |
| `Email:SmtpHost` / `Email:SmtpPort` | `smtp.gmail.com` / `587` | SMTP server. Port 587 uses STARTTLS, 465 uses TLS. |
| `Email:Username` / `Email:Password` | — | SMTP login. For Gmail, an app password. **Secret, so don't commit it.** |

Rooms in Wavin vacation mode and devices that haven't been migrated to the current Wavin platform are skipped.

## Run locally

Put your Wavin login in `Wavin:Email` and `Wavin:Password` in `src/HeatingScheduler/appsettings.json`, then:

```bash
dotnet run --project src/HeatingScheduler
```

> `appsettings.json` is copied into the publish output. Don't commit it with real credentials. On the server you can leave the password blank in `appsettings.json` and put it in the systemd `env` file instead, as plain text with no quotes or backslash escapes.

Run the tests from the repo root with `dotnet test`.

## Deploy to AWS (EC2 t4g.nano or Lightsail)

The instance needs outbound HTTPS only. Allow no inbound ports apart from SSH.

```bash
# On your machine (use linux-x64 for an x86 instance)
dotnet publish src/HeatingScheduler -c Release -r linux-arm64 --self-contained -o out
rsync -av out/ ec2-user@<host>:/tmp/heating-scheduler/
scp deploy/heating-scheduler.service deploy/heating-scheduler.env.example ec2-user@<host>:/tmp/

# On the instance
sudo useradd --system --no-create-home --shell /usr/sbin/nologin heating
sudo mkdir -p /opt/heating-scheduler /etc/heating-scheduler
sudo cp -r /tmp/heating-scheduler/* /opt/heating-scheduler/
sudo cp /tmp/heating-scheduler.env.example /etc/heating-scheduler/env
sudo chmod 600 /etc/heating-scheduler/env && sudo nano /etc/heating-scheduler/env   # set Wavin credentials
sudo cp /tmp/heating-scheduler.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now heating-scheduler

journalctl -u heating-scheduler -f
```

To upgrade, publish again, copy the new files over `/opt/heating-scheduler`, then run `sudo systemctl restart heating-scheduler`.
