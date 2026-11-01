# Notes for the future recorder module (XTU columns and meta of the old recorder)

The XTU module does **not** record. It publishes readings on the metrics bus with source `xtu` (the host stamps it). A separate
recorder module reads the bus and writes the CSV. This file is what that recorder needs to know to reproduce the CSV of the standalone
app (`xtu-profile-switcher`, `Monitor.cs`: `LogColumns`, `Recorder`) for an XTU machine, and where the bus is not enough yet.

Facts below come from the old app's source and from the bus ids in `modules/xtu/Rigbay.Module.Xtu/Monitoring.cs`.

## What the old CSV looked like

- One file `xtu-log-<date>_<time>.csv` plus `<same name>.meta.json`, UTF-8 without BOM, `FileShare.Read`, one flush per row.
- Numbers: `.` as decimal separator, no thousands separator, at most 3 decimals (`0.###`). A missing reading is an **empty cell**.
- Timestamps: local time, ISO 8601 with offset (`2026-11-01T20:35:06.848+07:00`); `elapsed_s` is seconds since the first row.
- Rows are on a fixed schedule (`nextDue += interval`, no drift), a sample within 200 ms before the slot still counts for it; if more
  than one interval late the slot resets instead of bursting. The clock of a timed recording starts at the first row.
- `status` was `ok`, or `xtu_unavailable` when the XTU service did not answer (all XTU cells then empty).

## Column to bus id

`unit` is the unit in the old CSV; the bus unit is what the module publishes (`MetricUnit`). Where they differ, convert in the recorder.

| Old column | Unit | Bus id | Bus unit | Note |
| --- | --- | --- | --- | --- |
| `pl1_w` | W | `cpu.power.pl1` | Watt | refreshed every 5 s, as before |
| `pl2_w` | W | `cpu.power.pl2` | Watt | refreshed every 5 s |
| `pkg_temp_c` | °C | `cpu.temp` | Celsius | XTU "CPU Core Temperature" (package) |
| `core_temp_max_c` | °C | `xtu.cpu.temp.core-max` | Celsius | hottest of the 8 core temperatures |
| `cpu_util_pct` | % | `cpu.usage` | Percent | **differs**: the old column was the Windows counter `% Processor Utility`; the bus value is the mean of XTU's per-core utilisation. A live check (see `README.md`) gave 50 % against 49.656 % at the same second, but they are not the same measurement |
| `pkg_power_w` | W | `cpu.power` | Watt | XTU "Package TDP" |
| `core_power_w` | W | `xtu.cpu.power.cores` | Watt | |
| `igpu_power_w` | W | `xtu.igpu.power` | Watt | |
| `cpu_freq_max_ghz` | GHz | `cpu.clock` | Megahertz | **divide by 1000** for the old unit; highest performance-core frequency |
| `uncore_ghz` | GHz | `xtu.cpu.uncore` | Megahertz | divide by 1000 |
| `active_cores` | | `xtu.cpu.active-cores` | Count | |
| `ref_clock_mhz` | MHz | `xtu.cpu.ref-clock` | Megahertz | |
| `mem_speed_mts` | MT/s | `xtu.memory.speed` | None | the unit list has no MT/s |
| `igpu_freq_mhz` | MHz | `xtu.igpu.clock` | Megahertz | |
| `igpu_temp_c` | °C | `xtu.igpu.temp` | Celsius | |
| `igpu_volt_v` | V | `xtu.igpu.volt` | Volt | |
| `cache_volt_v` | V | `xtu.cpu.volt.cache` | Volt | |
| `sa_volt_v` | V | `xtu.cpu.volt.system-agent` | Volt | |
| `thermal_throttle_pct` | % | `xtu.throttle.thermal` | Percent | XTU indicator 0 to 100 |
| `power_throttle_pct` | % | `xtu.throttle.power` | Percent | |
| `current_throttle_pct` | % | `xtu.throttle.current` | Percent | |
| `vr_throttle_pct` | % | `xtu.throttle.vr` | Percent | |
| `core<N>_temp_c` (N = 0..7) | °C | `xtu.core<N>.temp` | Celsius | only when the module's "Publish per-core readings" is on |
| `core<N>_freq_ghz` | GHz | `xtu.core<N>.clock` | Megahertz | divide by 1000; per-core option |
| `core<N>_volt_v` | V | `xtu.core<N>.volt` | Volt | per-core option |
| `core<N>_util_pct` | % | `xtu.core<N>.usage` | Percent | per-core option |

Not from XTU and not published by this module (the old app read them itself): `mem_util_pct` (use `memory.usage` from whichever module
offers it), `battery_pct` (`battery.level`), `battery_discharge_w` and `battery_charge_w` (`battery.rate`, signed on the bus: positive
charging, negative discharging, so two old columns become one), `power_source` (`ac` or `battery`; no bus id yet), the five NVIDIA
columns `gpu_temp_c`, `gpu_power_w`, `gpu_util_pct`, `gpu_clock_mhz`, `gpu_mem_clock_mhz` (`gpu.*` from an NVIDIA module).

## What the bus cannot carry yet

- **`profile`** (name of the saved profile that matches the active limits). The bus is numeric. Options for the recorder: call the
  command `rigbay xtu status` (the `matching profile:` line) when a recording starts and when `cpu.power.pl1` or `cpu.power.pl2`
  changes; or ask for a text channel in the contract. The module does not publish a number for it on purpose.
- **`power_source`**: same, no id for it. `GetSystemPowerStatus` is cheap and the recorder can call it itself.
- **Sample status.** The module publishes nothing while XTU is unreachable and nothing while it warms up after a (re)connection, so
  an unavailable XTU shows as *no new sample*. A recorder should treat "latest `cpu.temp` older than about 2.5 s" as
  `xtu_unavailable` and write empty XTU cells, as the old recorder did.
- **Warm-up.** Right after the module connects to the monitoring pipe, XTU returns what it measured last time. The old recorder
  skipped samples until a probe value moved (power, temperature, frequency) or 4 s passed. This module does that before publishing,
  so the recorder needs no warm-up logic of its own.
- **Sampling only while somebody looks.** The module reads XTU only while its page is visible or one of its monitor metrics has a subscriber.
  A recorder must **subscribe** (any of the ids it records, for example `cpu.temp`; `Subscribe` counts, `TryGetLatest` alone does not), otherwise
  the module is idle and the cached value gets older than 2.5 s: write empty XTU cells then, as the old recorder did for `xtu_unavailable`. The bus
  keeps the last value of an idle module (nothing is withdrawn for idling), so `TryGetLatest` still answers and its timestamp tells the age. After
  the module wakes (a first subscriber, the page shown) the first sample takes one to four seconds (warm-up). `cpu.power.pl1` and `.pl2` keep their 5 s refresh
  either way.
- **Withdrawn readings.** After three failed monitor reads in a row (about 10 to 15 s) the module withdraws all its monitor ids: `TryGetLatest`
  finds nothing and a subscriber gets one sample with `IsAvailable == false` (value NaN). That is `xtu_unavailable` without the age guess. Same for
  PL1 and PL2 after three failed state reads. Publishing resumes with the first good sample.
- **Sampling period.** XTU refreshes about once a second; sampling faster only repeats values. The module publishes at about 1 Hz
  with a fixed schedule (it resets the slot when more than a second behind). A recorder that writes every N seconds should take the
  latest cached value of each id (`TryGetLatest`) at its own slot.

## `.meta.json`

Written next to the CSV, rewritten at the end with `ended`. Fields:

```
tool          "XTU Profile Switcher"            (the recorder module names itself)
csv           file name of the CSV
started       ISO 8601 with offset, time of the first row
ended         same, or null while recording
interval_s    seconds between rows
rows          number of rows written
machine       manufacturer, model (WMI Win32_ComputerSystem), cpu (Win32_Processor Name), os, xtu_service
notes         three plain sentences (see below)
columns       [{ "name", "unit", "description" }] in CSV order; header, rows and this list come from one definition
```

`xtu_service` is the file version of `XtuService.exe` in the Intel folder (for example `7.14.2.135`); the XTU module's page shows
the same value. The three notes of the old file, which stay true for the bus:

1. XTU columns come from the XTU service, which polls about once a second; sampling faster only repeats values.
2. `pl1_w`, `pl2_w` and `profile` are re-read every 5 s, so a change shows up in the next row after that.
3. An empty cell means the reading was not available at that moment.

Column descriptions (the `description` strings) are in the old `LogColumns.Build`; a recorder can keep them next to its own
id-to-column table.
