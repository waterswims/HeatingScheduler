# HeatingScheduler

A .NET 10 worker service that runs underfloor heating at the lowest [Octopus Agile](https://octopus.energy/smart/agile/) cost, within an off:on ratio, by setting room setpoints on a **Wavin Sentio** system.

It runs two background services in one process:

| Service | What it does |
|---|---|
| `PriceFetchService` | Every `FetchIntervalMinutes` it fetches Agile prices for the next `HorizonHours` (rolling), until prices are known for that whole period. Octopus publishes the next day's prices at about 16:00, so before then the horizon is shorter. |
| `HeatingControlService` | Just after every half-hour boundary, and at least every `MaxCheckIntervalMinutes`, it reads each room's setpoint from the Wavin Sentio cloud. Any room that doesn't match `OnTemperature` or `OffTemperature` for the current slot is set. |

The two services share the schedule in memory.

### How the on/off schedule is chosen

- **The rule:** after the heating has been on for a block of time *L*, it may then be off for at most `OffToOnRatio` × *L*. It may be off for less. On-blocks last at least `MinOnMinutes`. For example, with a ratio of 2, one hour on allows up to two hours off.
- **The solver:** finds the on/off pattern over the next `HorizonHours` (or as far as prices are known) with the **lowest total price** that obeys the rule. It uses dynamic programming over the half-hour slots, so the result is exactly optimal, not an approximation. With positive prices this means as little on-time as the ratio allows, placed in the cheapest slots. When prices are negative, the heating comes on.
- **Rolling horizon:** when each half-hour slot starts, the solver re-plans from what actually happened so far. That slot's decision is then fixed. The rest of the plan is re-optimised as time passes and new prices arrive, and the log shows the plan whenever it changes.
- **After a restart:** the history is lost, so the solver assumes the heating has just finished a `StartupAssumedOnMinutes` on-block.

> **Notes**
> - The Wavin Sentio cloud API is **unofficial**. It is ported from the community library [djerik/wavinsentio](https://github.com/djerik/wavinsentio). Wavin could change it without notice.
> - **The schedule always wins.** If you change a room's setpoint in the Wavin app, the service puts it back at the next check.

## Configuration (`src/HeatingScheduler/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Octopus:ProductCode` | `AGILE-24-10-01` | Your Agile product. Check your Octopus account or `https://api.octopus.energy/v1/products/?brand=OCTOPUS_ENERGY`. |
| `Octopus:RegionCode` | `C` | Region letter A–P: the last letter of your tariff code (`E-1R-AGILE-24-10-01-C`). |
| `Octopus:FetchIntervalMinutes` | `30` | How often prices are checked. |
| `Schedule:OffToOnRatio` | `1.0` | Maximum off time as a multiple of the preceding on-block. The allowance is rounded down to whole slots, e.g. 1.5 × 30 min allows 30 min. |
| `Schedule:MinOnMinutes` | `60` | Shortest on-block (a multiple of 30). Use `30` for no minimum. |
| `Schedule:StartupAssumedOnMinutes` | `60` | After a restart, assume an on-block this long has just ended (a multiple of 30). `0` forces the heating on straight away. |
| `Schedule:HorizonHours` | `24` | How far ahead the solver plans (rolling). |
| `Schedule:FallbackState` | `On` | State used when there's no price for the current slot, e.g. if the Octopus API is down. |
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
