# Changelog

## 0.1.1 (not released)

Needs Rigbay 0.1.5 or newer (host API 0.1.4, `hostApi` `>=0.1.4 <0.2`).

- **Background demand.** The decision "page visible or reader => fast, else not at all" is now `MetricDemand.Evaluate` (idle = do not poll). A reader that asks through
  `SubscribeBackground` (Watch) makes the module read at max(poll rate, asked interval) while the page is closed; a subscriber or the page still means the poll rate.
  The change is re-evaluated when the page is shown or left, a subscriber or a background demand comes or goes, and when the poll rate setting changes. With
  nobody asking nothing changes. A background demand keeps the NVIDIA GPU awake (README, "When it reads").
- **`rigbay gpu status` is declared read-only** (`CommandInfo`, `VerbAccess.ReadOnly`), so another module (Game scenes) can list it and call it. Behaviour unchanged.

## 0.1.0 (not released)

First version. Reads the first NVIDIA GPU through NVML (`nvml.dll` from the Windows system folder) and publishes eleven readings on the metrics bus: GPU
load, memory controller load, board power and the power limit, graphics and memory clock, memory in use and total, the core temperature, the
performance state and a bit mask of the reasons the clocks are held down. The page has four tiles, one chart per kind of reading, the card's name and
the clock limit in words; the state line reads like "P0 · 72 W · power cap". `rigbay gpu status` reads once and prints.

- **Read only, enforced in code.** One list of 15 NVML function names (`NvmlAllowList`) is the whole reach of the module into the driver; a function is
  looked up by name only after the list accepted it. Tests scan the source and the compiled assembly for any other NVML entry point.
- **Reads only while somebody looks.** The page is on screen, or a reader (Dashboard, recorder, tray tooltip, another module) subscribes to one of its
  metrics. Otherwise NVML is not called, so a laptop's NVIDIA GPU can sleep. NVML itself is opened once when the module is turned on and shut down when
  it is turned off.
- **A reading the GPU does not have is withdrawn, never shown as 0.** `NVML_ERROR_NOT_SUPPORTED` at once, any other failure after three polls in a row.
- Host API `>=0.1.3 <0.2`. No administrator needed.
