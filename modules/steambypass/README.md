# Steam access (Rigbay module `steam-bypass`)

A [Rigbay](https://github.com/rigbay-app/rigbay) module that restores access to Steam on networks that block it with DNS poisoning plus
SNI and HTTP `Host` header filtering. It runs a small proxy on `127.0.0.1`, looks up Steam names over DNS-over-HTTPS, and points the current
user's Windows proxy setting (`AutoConfigURL`) at a PAC file that sends only Steam addresses to it. It is the module port of the standalone app
[steam-bypass](https://github.com/minhdevtree/steam-bypass) (same technique, same domain list format, new code).

Status: **0.1.0, not released.** Built and tested; never turned on against the real network from this repository's automated checks. See
`docs/steambypass/MODULE.md` in the Rigbay repository for the design, the evidence and what is not verified.

## What it changes on your computer

Only while the bypass is **on**, and only after you confirm in Rigbay (the dialog shows the exact change):

- the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\AutoConfigURL` is set to `http://127.0.0.1:18081/proxy.pac`
  (the previous value is saved and put back when you turn it off, disable the module or close Rigbay);
- it listens on `127.0.0.1:18081` (proxy and PAC) and `127.0.0.1:80` (Steam's game downloader). Loopback only, never another address;
- Steam host names are sent to `1.1.1.1`, `8.8.8.8`, `cloudflare-dns.com` or `dns.google` over HTTPS. Nothing else is sent anywhere, there is
  no telemetry.

## Use

- Page: switch **Steam bypass**, status rows (listening, open connections, errors, whether Steam uses it), the four-row **connection check**
  with **Run test**, and **Turn on when Rigbay starts**.
- Command line: `rigbay steam-bypass status | on | off | test`. `on` and `off` ask for confirmation like any change; `status` and `test`
  only read.
- Restart Steam after turning the bypass on: it reads the proxy setting only when it starts.
- Do not run it together with the standalone Steam Bypass app: they use the same ports.

## Build

Inside the Rigbay repository: `dotnet build Rigbay.sln` and `dotnet test Rigbay.sln`. In its own repository the project reference to
`Rigbay.Abstractions` is the `external/rigbay` submodule or the NuGet package (see the comment in the csproj). The output folder
(`module.json`, `Rigbay.SteamBypass.dll`, `steam-domains.txt`, `lang/`) goes to `<modules root>\steam-bypass\`.

Tests use fakes for the Windows setting, DoH and the upstream servers; the registry code runs against a scratch key. They open loopback
sockets on free ports only, never 80 or 18081.

## Domain list

`steam-domains.txt` next to the module (or a file of the same name in the module's data folder, which wins): one domain per line, `#` comments,
a domain also covers its subdomains. Only these names go through the bypass. Lines that are not plain domain names are ignored.

## License

MIT. Not affiliated with Valve. For lawful personal use.
