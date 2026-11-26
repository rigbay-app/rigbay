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
| HTTP `Host` filter, port 80, store name | reset; `HoSt:` passes | timeout for `Host:`; **`HoSt:` passes** (302) with a raw socket, 3 of 3 (a first `curl` probe said otherwise; see the correction below); a neutral `Host: example.com` answers 400 |
| Game content over HTTP (`cache8-hkg1.steamcontent.com`) | blocked (DNS + Host) | **not blocked** (403 for `/depot/`, 200 for `/`) |

## What it means

- **The core of the module (DoH for the names plus the 2-byte ClientHello split) works on Viettel too.** Viettel blocks less: only the store and
  the community site, by SNI, plus the ISP resolver.
- The `Host` case trick works on Viettel too (corrected, see below). Game downloads over plain HTTP are not blocked there at all.
- For a phone (Android) on Viettel, an app that splits the first TLS bytes and uses encrypted or public DNS should be enough for the store and the
  community pages; login, API and downloads were not blocked at all on this line. Mobile data (Viettel 4G) was not measured and may differ.
- Steam's login servers (CM websockets, `*.steamserver.net`) answered normally on both lines (`steam-check` section 4).
- Not measured: Viettel mobile, VNPT, Remote Play relays, IPv6.

## Repeat

The scripts are `probe.sh` and `probe2.sh` plus `tls_split.py` from the coordinator's scratch folder of 2026-11-26 (not kept); the steps are those of
`blocking-evidence.md` "Re-measuring", with `dig @<router>`, `dig @1.1.1.1`, DoH through `curl https://1.1.1.1/dns-query`, `curl --resolve`, and a
Python `ssl.MemoryBIO` client that controls how the ClientHello is cut.

## Corrections from `tools/steam-check` (same day, 2026-11-26)

- **`HoSt:` passes on Viettel.** The first probe sent the header through `curl -H "HoSt: ..."` and timed out; `steam-check.py` sends the request
  itself over a raw socket and got `302` in both runs. The curl result is not trusted (how curl writes a custom Host header was not checked).
- **One plain, blocked ClientHello does not make the filter drop later split ones** to the same address: split, split, split, plain (timeout),
  split right after, then after 15, 30 and 60 s: every split answered 200. Single timeouts do happen on this line (a split to the store once timed
  out, `login.steampowered.com` once needed a second try), so `steam-check` tries each test up to 3 times before it calls it blocked.
- **FPT: the UDP answer `118.68.82.144` from 8.8.8.8 is genuine**, not a fake address as the standalone app's `blocking-evidence.md` says: a split
  handshake to it with SNI `store.steampowered.com` gets a valid certificate (an Akamai edge inside FPT's network). On 2026-11-26 FPT's
  resolver still answers `127.0.0.1` for the store and the community, and api/login/content are no longer blocked on FPT either.
