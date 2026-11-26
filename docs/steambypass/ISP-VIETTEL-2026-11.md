# Steam blocking on a second ISP: Viettel (2026-11-26)

The Steam access module was built from measurements on one network: FPT Telecom home fibre (October 2026, `blocking-evidence.md` of the
standalone app). On 2026-11-26 the owner asked whether the method works on another ISP. The same probes ran once, read only, on the owner's
Mac mini (macOS 26.6.2) on **Viettel** home internet (resolver on the router `192.168.2.1`; the Mac itself is set to `8.8.8.8`), over SSH.
Nothing was installed; the probe scripts lived in `/tmp` and were deleted afterwards. `tls_split.py` did one HTTPS GET with the ClientHello sent
whole ("plain") or as 2 bytes, 20 ms, the rest ("split"), which is what the module does.

## Result

| Layer | FPT (PC, October) | Viettel (Mac mini, 2026-11-26) |
| --- | --- | --- |
| DNS | ISP resolver answers `127.0.0.1`; UDP to `8.8.8.8` intercepted | ISP resolver answers `127.0.0.1` / `::1` for `store.steampowered.com` and `steamcommunity.com`; `8.8.8.8`, `1.1.1.1` (UDP) and DoH give the real addresses |
| TLS SNI filter | reset for store, community, api, content, CM names | **store.steampowered.com and steamcommunity.com only**: the handshake gets no answer (timeout, not a reset). help, login, api, checkout, the `*.steamstatic.com` / `steamcdn-a` CDNs and `*.steamcontent.com` answer normally |
| SNI with `example.com` to the store IP | completes | completes (HTTP 400): the IP path is open |
| ClientHello split at byte 2 | passes | **passes**: store 200, community 200, help 302, checkout 302 |
| HTTP `Host` filter, port 80, store name | reset; `HoSt:` passes | timeout for `Host:` **and `HoSt:`**: the case trick does not work here; a neutral `Host: example.com` answers 400 |
| Game content over HTTP (`cache8-hkg1.steamcontent.com`) | blocked (DNS + Host) | **not blocked** (403 for `/depot/`, 200 for `/`) |

## What it means

- **The core of the module (DoH for the names plus the 2-byte ClientHello split) works on Viettel too.** Viettel blocks less: only the store and
  the community site, by SNI, plus the ISP resolver.
- The `Host` case trick is FPT-specific. On Viettel it does not get through, but nothing that Steam needs is blocked there over plain HTTP (game
  downloads answer normally), so the module does not depend on it on this network.
- For a phone (Android) on Viettel, an app that splits the first TLS bytes and uses encrypted or public DNS should be enough for the store and the
  community pages; login, API and downloads were not blocked at all on this line. Mobile data (Viettel 4G) was not measured and may differ.
- Not measured: Viettel mobile, VNPT, Steam's CM servers (`*.steamserver.net`) and Remote Play relays, IPv6.

## Repeat

The scripts are `probe.sh` and `probe2.sh` plus `tls_split.py` from the coordinator's scratch folder of 2026-11-26 (not kept); the steps are those of
`blocking-evidence.md` "Re-measuring", with `dig @<router>`, `dig @1.1.1.1`, DoH through `curl https://1.1.1.1/dns-query`, `curl --resolve`, and a
Python `ssl.MemoryBIO` client that controls how the ClientHello is cut.
