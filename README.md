# HeatingScheduler

A .NET 10 worker service that runs underfloor heating in the cheapest [Octopus Agile](https://octopus.energy/smart/agile/) half-hour slots by setting room setpoints on a **Wavin Sentio** system.

It runs two background services in one process:

| Service | What it does |
|---|---|
| `PriceSchedulerService` | Every `FetchIntervalMinutes` it fetches Agile prices. When every slot of an Agile day (23:00 → 23:00 UK time) is priced, it picks the cheapest `OnPercentage`% of that day's half-hour slots to be **on**. A schedule is fixed once worked out. Tomorrow's is normally ready shortly after Octopus publishes prices at about 16:00. |
| `HeatingControlService` | Just after every half-hour boundary, and at least every `MaxCheckIntervalMinutes`, it reads each room's setpoint from the Wavin Sentio cloud. Any room that doesn't match `OnTemperature` or `OffTemperature` for the current slot is set. |

The two services share the schedule in memory. After a restart the current day is rebuilt from the Octopus API, which also returns past prices.

> **Notes**
> - The Wavin Sentio cloud API is **unofficial**. It is ported from the community library [djerik/wavinsentio](https://github.com/djerik/wavinsentio). Wavin could change it without notice.
> - **The schedule always wins.** If you change a room's setpoint in the Wavin app, the service puts it back at the next check.

## Configuration (`src/HeatingScheduler/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Octopus:ProductCode` | `AGILE-24-10-01` | Your Agile product. Check your Octopus account or `https://api.octopus.energy/v1/products/?brand=OCTOPUS_ENERGY`. |
| `Octopus:RegionCode` | `C` | Region letter A–P: the last letter of your tariff code (`E-1R-AGILE-24-10-01-C`). |
| `Octopus:FetchIntervalMinutes` | `30` | How often prices are checked. |
| `Schedule:OnPercentage` | `40` | Percentage of each Agile day's slots that are on (rounded to the nearest slot, e.g. 40% → 19 of 48). |
| `Schedule:FallbackState` | `On` | State used when no schedule exists for the current slot, e.g. if the Octopus API is down. |
| `Wavin:Email` / `Wavin:Password` | — | Wavin Sentio app login. **Secret, so don't put it in appsettings.json.** |
| `Wavin:DeviceName` | `null` | Limit control to one controller (e.g. `devices/xxxx`). When empty, every device on the account is used. |
| `Wavin:Rooms` | `[]` | Room ids or names to control. When empty, every room is controlled. Unknown names are logged together with the list of available rooms. |
| `Heating:OnTemperature` | `21.0` | Setpoint (°C) during on slots. |
| `Heating:OffTemperature` | `17.0` | Setpoint (°C) during off slots. Must be below `OnTemperature`. |
| `Heating:MaxCheckIntervalMinutes` | `5` | Longest gap between checks of the Wavin setpoints. |

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
