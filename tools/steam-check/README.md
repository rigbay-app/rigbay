# steam-check

Measures how Steam is blocked on the network a device is on, and whether the Steam access method (encrypted DNS plus a ClientHello cut after
2 bytes) gets through. Read only: normal DNS queries and HTTPS/HTTP connections to Steam and to public DNS services. One file, plain Python 3.8+,
no packages. Background: `docs/steambypass/ISP-VIETTEL-2026-11.md`.

| Section | What it does |
| --- | --- |
| 1 DNS | the device's resolver, UDP to 8.8.8.8 and 1.1.1.1, DNS-over-HTTPS (Cloudflare). An answer that is not the DoH one counts as genuine only if it completes a TLS handshake with a valid certificate for the name (CDNs answer differently per resolver) |
| 2 HTTPS | per Steam name, to its real address: ClientHello whole ("plain") and cut after 2 bytes ("split"); up to 3 tries each, a lost packet is not a block |
| 3 Port 80 | `Host:` vs `HoSt:` vs a neutral name to the store address |
| 4 Login servers | the CM websocket servers from Steam's own directory (what the Steam app and Steam Link connect to) |
| Verdict | what is rewritten, what is blocked, what the split fixes |

## Android (Termux)

1. Install **Termux** from F-Droid (`https://f-droid.org/packages/com.termux/`) or its GitHub releases. The Play Store build is old.
2. Turn off any VPN or DPI app (ByeByeDPI and the like). For a test of the carrier itself: Wi-Fi off, Settings > Network > Private DNS = Off or Automatic.
3. In Termux:
   ```
   pkg install -y python
   curl -fsSL https://art-raw.minhdevtree.tech/5MN1-NCJ1cPJwQd4bu43Zg/steam-check.py (expires 2026-12-03; republish with `art put` after that) -o steam-check.py && python steam-check.py
   ```
4. Long-press the output, Select all, Copy, and send it (or a screenshot).

## Elsewhere

`python3 steam-check.py` (macOS, Linux), `python steam-check.py` (Windows). About one to three minutes.

## Results so far

| Network | Date | Blocked over plain HTTPS | Split gets through | DNS rewritten | `HoSt:` |
| --- | --- | --- | --- | --- | --- |
| FPT home fibre (PC) | 2026-11-26 | store, community (reset) | yes | store, community -> 127.0.0.1 | works |
| Viettel home internet (Mac mini, DNS 8.8.8.8) | 2026-11-26 | store, community (timeout) | yes | not with 8.8.8.8 (the router's resolver gives 127.0.0.1) | works |

Both lines also reached Steam's login servers (section 4) without any trick.
