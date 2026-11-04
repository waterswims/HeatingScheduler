# HeatingScheduler

A .NET 10 worker service that runs underfloor heating at the lowest [Octopus Agile](https://octopus.energy/smart/agile/) cost, while making up heat lost during off periods, by setting room setpoints on a **Wavin Sentio** system.

It runs two background services in one process:

| Service | What it does |
|---|---|
| `PriceFetchService` | Every 30 minutes it fetches Agile prices for the next 24 hours (rolling), until prices are known for that whole period. Octopus publishes the next day's prices at about 16:00, so before then the horizon is shorter. |
| `HeatingControlService` | Just after every half-hour boundary, and at least every 5 minutes, it reads each room's setpoint from the Wavin Sentio cloud. Any room that doesn't match `OnTemperature` or `OffTemperature` for the current slot is set. |

The two services share the schedule in memory.

### How the on/off schedule is chosen

- **The rules:**
  - An off period lasts at most `MaxOffMinutes`, so heat loss is limited.
  - After an off period of length *F*, the heating must stay on for at least `OnToOffRatio` × *F* (rounded up to whole half-hours) to make up the heat lost. It may stay on longer.
  - Every on-block lasts at least `MinOnMinutes`.
  - For example, with a ratio of 0.5 and a 3-hour maximum, 2 hours off must be followed by at least 1 hour on.
- **The solver:** finds the on/off pattern over the next 24 hours (or as far as prices are known) with the **lowest total price** that obeys the rule. It uses dynamic programming over the half-hour slots, so the result is exactly optimal, not an approximation. With positive prices it keeps the heating off through expensive periods and does the make-up heating in the cheapest slots. When prices are negative, the heating comes on.
- **Rolling horizon:** when each half-hour slot starts, the solver re-plans from what actually happened so far. That slot's decision is then fixed. The rest of the plan is re-optimised as time passes and new prices arrive, and the log shows the plan whenever it changes.
- **After a restart:** the history is lost, so the solver assumes the heating has been on with no heat left to make up. It is free to switch off straight away, even if the heating had already been off for a while before the restart.

### Tuning from room temperatures

The make-up ratio and maximum off period can be worked out from your rooms instead of fixed by hand. This is on by default (`Tuning:Enabled`).

- **Readings:** the Sentio keeps months of room history (air and floor temperature, humidity and setpoint). The app reads the last 24 hours for each room it tunes against, at 5-minute resolution, using the same unofficial API call as the Wavin Sentio portal (`GetSentioRoomTimeSeries`). A reading counts as on or off when its setpoint is the on or off temperature; readings with any other setpoint (for example, set by hand) are left out. Nothing needs to be learnt after a restart.
- **Recalculation:** at startup, then every 30 minutes, for each room:
  - **Cooling rate:** the temperature drop per hour over the **longest off period** in the last 24 hours, fitted across all of its readings. Off periods shorter than an hour are ignored.
  - **Maximum off** = (temperature when that off period started − `Tuning:MinTemperature`) ÷ cooling rate.
  - **Heating rate:** the temperature rise per hour during the **on period after the most recent off period**. It is measured from switch-on until the room reached the on temperature, or until the on period ended if it never got there.
  - **Make-up ratio** = cooling rate ÷ heating rate.
- **Combining rooms:** only the rooms in `Tuning:Rooms` are used, if set. Ideally pick a room whose temperature mostly reflects the heating. Among those rooms, the most demanding one decides, using the shortest maximum off and the highest ratio. The maximum off is rounded down to whole half-hours. Both values are kept within safety bounds (max off 60–720 minutes, ratio 0.05–3.0), then replace the `Schedule` values, and the plan is recalculated.
- **When a value can't be measured,** the current value is kept. For example, a short on period may show no measurable warming because of underfloor lag. The values in `Schedule` are the starting point.

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
