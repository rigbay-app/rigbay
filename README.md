# Rigbay

A small Windows app that manages **modules** for your own machine: power limits, fan and performance
modes, monitoring and logging, network fixes, and anything else a module can do. Each module lives in its
own repository and can be installed, updated or removed from inside the app.

Status: **design stage.** No code yet; see [docs/DESIGN.md](docs/DESIGN.md) for what is decided and what is still open.

## Why

Tools like Intel XTU, Acer PredatorSense and one-off fixes each have their own window, their own tray icon and
their own way of starting with Windows. Rigbay gives them one place, one look, one settings store, one set of
hotkeys and one log, and lets people write a module for *their* machine (other brands, other CPUs and GPUs,
other features) without forking the app.

## Planned modules

| Module | Source | State |
| --- | --- | --- |
| XTU profiles (Intel power limits, hotkeys, auto-switch, monitor and CSV recording) | [xtu-profile-switcher](https://github.com/minhdevtree/xtu-profile-switcher) | working as a standalone app |
| Steam access (DNS / DPI workaround for networks that block Steam) | [steam-bypass](https://github.com/minhdevtree/steam-bypass) | working as a standalone app |
| Acer PredatorSense (fan, performance mode, charge limit for the Predator Triton 500 SE) | to be written | idea; the interface still has to be read with REA |

## License

MIT, see [LICENSE](LICENSE).
