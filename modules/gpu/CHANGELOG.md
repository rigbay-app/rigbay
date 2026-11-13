# Changelog

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
