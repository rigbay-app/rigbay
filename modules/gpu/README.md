# GPU (Rigbay module)

Reads the NVIDIA GPU of a PC through NVML, the management library that ships with the NVIDIA driver, and puts the readings on Rigbay's metrics bus: on
the module page (four tiles and a chart per kind of reading), on the Dashboard, in the recorder's CSV, and in `rigbay gpu status`. **It only reads.**
Nothing here sets a clock, a power limit, a fan or a mode.

Status: version 0.1.1, not released. Needs Rigbay 0.1.5 or newer (host API 0.1.4). Tried on one machine (below); everything else is **not verified**.

## What it shows

| Reading | Metric id | Unit | From NVML |
| --- | --- | --- | --- |
| GPU load | `gpu.usage` (shared id) | % | utilization rates, graphics engine |
| Memory controller load | `gpu.memctl.usage` | % | utilization rates, memory interface (how busy the link to video memory is, not how full the memory is) |
| GPU power | `gpu.power` (shared id) | W | board power usage (milliwatts / 1000) |
| Power limit | `gpu.power.limit` | W | enforced power limit; a dashed reference line in the power chart |
| GPU clock | `gpu.clock` (shared id) | MHz | graphics clock |
| Memory clock | `gpu.mem.clock` | MHz | memory clock |
| Memory in use, total | `gpu.mem.used`, `gpu.mem.total` | bytes | memory info; the total is a reference line in the memory chart |
| GPU core temperature | `gpu.nvml.temp` | °C | temperature, GPU core sensor |
| Performance state | `gpu.pstate` | 0 to 15 | performance state (P0 fastest, P8 and up idle) |
| Clock limit reasons | `gpu.throttle` | bit mask | clocks event reasons (older drivers: throttle reasons); 0 = nothing holds the clocks down |

The state line under the module's name reads like `P0 · 72 W · power cap`: the performance state, the board power, and at most two reasons the clocks are held
down (hardware power brake, hardware slowdown, thermal limit, power cap, sync boost, app clock setting, display clock setting; "idle" only when it is the only
one). The "Clock limit" row names all of them, or says "Not limited". The tiles are GPU load, GPU power, GPU core temperature and memory in use.

A reading the GPU says it does not have (`NVML_ERROR_NOT_SUPPORTED`, for example board power on some laptop GPUs) is **withdrawn**: its tile is not drawn
and the recorder leaves its column empty. It is never shown as 0. Any other failure withdraws a reading after three polls in a row.

**Temperature and PredatorSense.** The PredatorSense module publishes `gpu.temp` from the laptop's firmware. This module does not reuse that id: two
sources on one id would take turns in the chart and in the recorder. It publishes its own `gpu.nvml.temp` ("GPU core temperature") and gives the
Dashboard only GPU load and GPU power, so the Dashboard still shows one GPU temperature when both modules are on. A PC with an NVIDIA GPU and no PredatorSense
therefore has no `gpu.temp` for the Dashboard; the module's own page has the temperature.

## Read only, and how that is enforced

- One list, `Nvml/NvmlAllowList.cs`, spells the 15 NVML functions the module may call: init, shutdown, device count, handle by index, name, utilization rates,
  power usage, enforced power limit, clock info, memory info, temperature, performance state, clocks event reasons (and its older name), error string.
  A function is looked up by name only after that list accepted the name; there is no compile-time `DllImport`.
- The interface the rest of the module uses (`INvml`) has 14 methods and none of them changes anything.
- A test scans the source and the compiled assembly for every NVML entry point and fails on one that is not on the list (`nvmlDeviceSet*`, `nvmlDeviceReset*`,
  anything else).
- `nvml.dll` is loaded from the Windows system folder by its full path, nothing else is searched. No administrator is needed (the manifest says so).
- NVML is entered by one thread at a time (one lock around every call) and is initialised once when the module is turned on and shut down when it is turned off.

## When it reads (laptops with two GPUs)

On a laptop with Optimus or a similar switch, any program that talks to the NVIDIA GPU can keep it from going to sleep, which costs battery. So the module reads
**only while somebody looks**: its page is on screen, or a reader has subscribed to one of its metrics (the Dashboard, the recorder, the tray tooltip, another module).
When nobody looks it does not call NVML at all. The "Read every" setting (1 to 30 seconds, default 2) is the rate while somebody looks.

A reader that asks in the background (the Watch module, host API 0.1.4) is the one exception: it wants a reading every N seconds without the page being open. Then the module reads at the larger of the "Read every" setting and N (never faster than the setting) until the demand goes away. **A background demand keeps the NVIDIA GPU awake** (one read every N seconds is enough to stop a laptop's Optimus switch from putting it to sleep), so a Watch rule on a GPU reading costs battery for as long as the rule is on. NVML itself stays initialised while the
module is on; whether an initialised but silent NVML session keeps a sleeping GPU awake was **not measured** (see below).

`rigbay gpu status` reads once on request (it wakes the GPU for that one read) and prints every reading.

## Verified, not verified

Tried on 2026-11-13 on one machine: Acer Predator Triton 500 SE, NVIDIA GeForce RTX 3060 Laptop GPU (6 GB), Windows 11, on AC power with an external monitor on the NVIDIA GPU.

**Verified (ran it, saw it work):**

- 125 unit tests on a fake NVML, including the real marshalling code against managed stand-ins for the exports of `nvml.dll`: every reading, a reading that is not supported,
  failures, the old reasons-function name, losing and reopening the session, two threads at once, probe results, the page, the texts in English and Vietnamese, and the read-only scan of
  the source and of the compiled assembly. The strict language check passes.
- The probe on the real machine: `nvml.dll` loads from the system folder, NVML starts, one GPU, the name is read. Module turned on in its own Rigbay instance (own data and modules
  folder, never the owner's): the page shows four tiles, the charts, the card name, the clock limit and the state line `P8 · 14 W · idle`; it reads every 2 s while the page is open.
  Screenshot: https://art.minhdevtree.tech/a/ciM4P6Ei1_7aqdhPWZU1YA
- Against `nvidia-smi` (one query, 11:41:54.675 to .761, while the module's own read was at 11:41:54.596, GPU idle, external monitor on the GPU):

  | | module | nvidia-smi |
  | --- | --- | --- |
  | GPU load | 0 % | 0 % |
  | Memory controller load | 100 % | 100 % |
  | Power | 14.2 W | 14.18 W |
  | Graphics / memory clock | 210 / 405 MHz | 210 / 405 MHz |
  | Temperature | 52 °C | 52.19 °C |
  | Performance state | P8 | P8 |
  | Clock limit reasons | idle (0x1) | 0x0000000000000001 |
  | Memory in use | 803 MiB | 654 MiB |
  | Memory total | 6144 MiB | 6144 MiB |

  Memory in use differs by about 150 MiB. The module uses NVML's first memory-info structure, whose "used" probably includes memory the driver reserves; `nvidia-smi` shows a smaller figure. The cause was **not checked**.
- The power limit the module reads (the enforced limit) moved between 65 and 90 W from one read to the next while idle; `nvidia-smi` printed `[N/A]` for its own power-limit column. Treat the limit as what NVML says, not checked against another tool.

**Not verified:**

- Anything on battery, and whether the NVIDIA GPU sleeps while the module is on but nobody looks (this laptop's GPU was active the whole time: an external monitor is on it). Whether an
  initialised but silent NVML session keeps a sleeping GPU awake is **not measured**.
- Readings under load, and the clock limit reasons other than idle (power cap, thermal, ...): only the idle state was seen live. The words and the order are tested on fake values only.
- The first read after the page opened (11:41:40) caught P0, 1425 MHz and 25 W; two reads later the GPU was at P8, 210 MHz, 14 W. Whether the first NVML access wakes the clocks briefly was not investigated.
- Other cards, other drivers (the older reasons-function name is tested on the fake library only), a second GPU (only the first is shown), a 32-bit process, the Vietnamese page on screen (the texts are tested, the page was shot in English).
- A PC with only an NVIDIA GPU and no PredatorSense on the Dashboard.

## Install

Not published yet. For a local try: `dotnet build modules/gpu/src/Rigbay.Gpu -c Release`, copy the output folder (without `.pdb` files) to `<a scratch modules folder>\gpu`, start Rigbay with
`--data <scratch data> --modules <scratch modules> --allow-multi`, turn the module on. Never develop against the real modules folder.
