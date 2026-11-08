# HeatingScheduler

A .NET 10 worker service that runs underfloor heating at the lowest [Octopus Agile](https://octopus.energy/smart/agile/) cost while keeping the rooms above a minimum temperature, by setting room setpoints on a **Wavin Sentio** system.

It runs two background services in one process:

| Service | What it does |
|---|---|
| `PriceFetchService` | Every 30 minutes it fetches all published Agile prices. Octopus publishes the next day's prices at about 16:00, so new prices arrive about once a day. Only when they do (and at startup) does it re-measure the room rates from the temperature history, re-make the plan and email it. |
| `HeatingControlService` | Just after every half-hour boundary, and at least every 5 minutes, it reads the rooms from the Wavin Sentio cloud, gives the schedule the tracked rooms' current temperatures, and sets any room whose setpoint doesn't match `OnTemperature` or `OffTemperature` for the current slot. |

The two services share the schedule in memory.

### How the on/off schedule is chosen

The goal is to keep every tracked room above `Schedule:MinTemperature` as cheaply as possible.

- **Room model:** for each tracked room the app knows its current temperature, how fast it cools with the heating off, and how fast it warms with the heating on. With the heating on, a room warms until it reaches `Heating:OnTemperature`, and the thermostat holds it there. With the heating off, it cools, but never below `Heating:OffTemperature`.
- **The plan:** the solver finds the cheapest on/off pattern over every known price (7–31 hours ahead, depending on the time of day) that keeps every tracked room's *predicted* temperature at or above `MinTemperature` + `SafetyMargin`. A room that is already below that is heated until it recovers. Every on-block lasts at least `MinOnMinutes`. The solver uses dynamic programming over the half-hour slots, merging temperatures to the nearest 0.01°C.
- **Heat left at the end:** the heat in the rooms when the plan ends is worth something, so each degree above the floor is credited at the average price of the known slots, per slot of heating it saves later (for the room that runs out first). The plan therefore stores heat when prices are cheaper than average and lets the rooms cool when they're dearer. Without this, every plan would end with the rooms cooled right down to the floor.
- **Following the plan:** when each half-hour slot starts, its planned decision is fixed. At the start of every hour the plan is re-made from the rooms' measured temperatures, so errors in the rates are corrected. It is also re-made when the prices or the rates change, and after a restart, when it starts from the rooms' current temperatures. If no recent temperatures are available (for example, Wavin is unreachable), the existing plan is followed.
- **No plan:** if there are no prices for the current slot, or no room temperature from the last hour, `FallbackState` is used.

### Tuning from room temperatures

Each tracked room's cooling and heating rates are measured from its temperature history, in place of `Schedule:CoolingRate` and `Schedule:HeatingRate`. This is on by default (`Tuning:Enabled`).

- **Readings:** the Sentio keeps months of room history (air and floor temperature, humidity and setpoint). The app reads the last 84 hours for each room it tunes against and counts the rises and falls that start in the last 72; the 12 hours before that are only used to confirm the first turning points, at 5-minute resolution, using the same unofficial API call as the Wavin Sentio portal (`GetSentioRoomTimeSeries`). Whether the heating was on comes from changes in the room's setpoint: a rise means it was turned on and a fall means it was turned off, whatever the temperatures. So changing `Heating:OnTemperature` or `Heating:OffTemperature` doesn't throw away the history. Readings before the first change take the opposite state to it; if the setpoint never changes, it is matched against the current on and off temperatures. Nothing needs to be learnt after a restart.
- **Recalculation:** whenever new prices arrive (about once a day) and at startup, for each tracked room. If it fails, it is retried at the next price check (30 minutes later). Rates are measured between the temperature's **turning points** (peaks and troughs), not between switching times. With underfloor heating the air keeps falling for a while after the heating comes on, and keeps rising after it goes off; measuring from the turning points leaves that lag out.
  - **Turning points:** the readings are smoothed with a 30-minute median, and a peak or trough is confirmed once the temperature has moved at least 0.2°C back from it. Rises or falls shorter than an hour (an oven, a shower, the sun) are removed, and the movement either side of them is merged.
  - **Which rises and falls count:** a rise counts as heating if the heating was on for at least 70% of it, and a fall counts as cooling if the heating was on for at most 30% of it. Rises with the heating off (such as the sun) and movements with the heating mixed are left out, as is the movement still in progress. A turning point at the very start of the readings only counts if the temperature moved at least 0.2°C into it, so a movement cut off by the start isn't measured over part of its length.
  - **Rates:** each rise or fall is measured with a straight-line fit through its readings. The **heating rate** and **cooling rate** are the medians.
- **Applying them:** every measured rate replaces the current one, and the plan is re-made. When a rate can't be measured (for example, there's no finished rise with the heating on yet), the current one is kept.

### Email notifications

When `Email:Enabled` is true, the app emails the plan when new prices arrive (about once a day, at about 16:00) and at startup. The hourly re-planning doesn't send emails. Each email has three parts:

1. **The plan in the last email**, including the slot that was current when it was sent.
2. **What happened since then:** the decision actually made for each slot, grouped into on/off blocks, with the average price and the lowest measured room temperature at the start and end of each block. It ends by comparing the hours on and average price with what the last email planned for the same period. Slots that used the fallback state are marked.
3. **The new plan:** each block with its average price and the lowest predicted room temperature at its end, and the lowest predicted temperature over the whole plan.

It also lists each tracked room's current temperature and rates (with the previous values when they changed) and what tuning measured.

Mail is sent over SMTP. For Gmail, use `smtp.gmail.com`, port 587, your Gmail address as `Username`, and an [app password](https://myaccount.google.com/apppasswords) (not your normal password) as `Password`. Outbound port 25 is blocked on EC2, so use 587 or 465. If sending fails, it's retried at the next price check (30 minutes later).

> **Notes**
> - The Wavin Sentio cloud API is **unofficial**. It is ported from the community library [djerik/wavinsentio](https://github.com/djerik/wavinsentio). Wavin could change it without notice.
> - **The schedule always wins.** If you change a room's setpoint in the Wavin app, the service puts it back at the next check.

## Configuration (`src/HeatingScheduler/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Octopus:ProductCode` | `AGILE-24-10-01` | Your Agile product. Check your Octopus account or `https://api.octopus.energy/v1/products/?brand=OCTOPUS_ENERGY`. |
| `Octopus:RegionCode` | `H` | Region letter A–P: the last letter of your tariff code (`E-1R-AGILE-24-10-01-C`). |
| `Schedule:MinTemperature` | `20.5` | Air temperature (°C) every tracked room should stay above. Must be above `Heating:OffTemperature`. |
| `Schedule:SafetyMargin` | `0.2` | How far above `MinTemperature` the plan keeps the predicted temperature, to allow for model error. `MinTemperature` + `SafetyMargin` must be below `Heating:OnTemperature`. |
| `Schedule:Rooms` | `["AT3 Lobby"]` | Room ids or names whose temperature the plan tracks (and that tuning measures), e.g. a room away from windows, ovens and showers. When empty, all controlled rooms are tracked and all must stay above the minimum. |
| `Schedule:CoolingRate` / `Schedule:HeatingRate` | `0.15` / `0.15` | Rates (°C per hour) used until tuning has measured them, or always if tuning is off. |
| `Schedule:MinOnMinutes` | `60` | Shortest on-block (a multiple of 30). Use `30` for no minimum. |
| `Schedule:FallbackState` | `On` | State used when there's no plan for the current slot: no prices, or no room temperature from the last hour. |
| `Tuning:Enabled` | `true` | Measure each tracked room's cooling and heating rates from its temperature history. |
| `Wavin:Email` / `Wavin:Password` | — | Wavin Sentio app login. **Secret, so don't put it in appsettings.json.** |
| `Wavin:Rooms` | `[]` | Room ids or names to control. When empty, every room is controlled. Unknown names are logged together with the list of available rooms. |
| `Heating:OnTemperature` | `22.5` | Setpoint (°C) during on slots. |
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
