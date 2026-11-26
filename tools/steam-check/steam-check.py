#!/usr/bin/env python3
"""steam-check: how is Steam blocked on THIS network, and does the Steam access method get through?

Read only. Plain Python 3 (3.8+), no packages: runs in Termux on Android, on macOS, Linux and Windows.
It makes normal DNS queries and HTTPS/HTTP connections to Steam and public DNS services, the same probes as
docs/steambypass/ISP-VIETTEL-2026-11.md and the standalone app's blocking-evidence.md:

  1. DNS: the device's own resolver vs UDP to 8.8.8.8 / 1.1.1.1 vs DNS-over-HTTPS (Cloudflare)
  2. HTTPS per Steam name, ClientHello sent whole ("plain") and cut after 2 bytes ("split", what the bypass does)
  3. plain HTTP on port 80 with "Host:" vs "HoSt:" (the case trick)
  4. Steam's login servers (CM, websockets) taken from Steam's own directory, which the Steam app and Steam Link need

Usage:  python steam-check.py            (about one minute)
"""
import json
import random
import socket
import ssl
import struct
import sys
import time
import urllib.request

NAMES = [
    "store.steampowered.com", "steamcommunity.com", "api.steampowered.com", "login.steampowered.com",
    "help.steampowered.com", "checkout.steampowered.com", "cdn.akamai.steamstatic.com", "cache8-hkg1.steamcontent.com",
]
TIMEOUT = 8


def dns_system(name):
    try:
        return sorted({a[4][0] for a in socket.getaddrinfo(name, 443, socket.AF_INET, socket.SOCK_STREAM)})
    except Exception as e:
        return ["error: %s" % type(e).__name__]


def dns_udp(name, server):
    tid = random.randint(0, 65535)
    q = struct.pack(">HHHHHH", tid, 0x0100, 1, 0, 0, 0)
    for part in name.split("."):
        q += bytes([len(part)]) + part.encode()
    q += b"\x00" + struct.pack(">HH", 1, 1)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(3)
    try:
        s.sendto(q, (server, 53))
        data, _ = s.recvfrom(4096)
    except Exception as e:
        return ["error: %s" % type(e).__name__]
    finally:
        s.close()
    count = struct.unpack(">H", data[6:8])[0]
    i = 12
    while data[i] != 0:
        i += data[i] + 1
    i += 5
    ips = []
    for _ in range(count):
        if data[i] & 0xC0 == 0xC0:
            i += 2
        else:
            while data[i] != 0:
                i += data[i] + 1
            i += 1
        typ, _cls, _ttl, rdlen = struct.unpack(">HHIH", data[i:i + 10])
        i += 10
        if typ == 1 and rdlen == 4:
            ips.append(".".join(str(b) for b in data[i:i + 4]))
        i += rdlen
    return ips or ["no A record"]


def dns_doh(name):
    url = "https://1.1.1.1/dns-query?name=%s&type=A" % name
    req = urllib.request.Request(url, headers={"accept": "application/dns-json"})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            data = json.loads(r.read().decode())
        return [a["data"] for a in data.get("Answer", []) if a.get("type") == 1] or ["no A record"]
    except Exception as e:
        return ["error: %s" % type(e).__name__]


def classify(e):
    if isinstance(e, socket.timeout) or "timed out" in str(e):
        return "TIMEOUT"
    if isinstance(e, ConnectionResetError) or "reset" in str(e).lower():
        return "RESET"
    if isinstance(e, ssl.SSLCertVerificationError):
        return "BADCERT"
    return type(e).__name__


def https_get(host, ip, mode, port=443, path="/", body=False):
    """One HTTPS GET to ip:port with SNI host. mode plain|split. Returns (True, status code) or (False, reason);
    with body=True, (True, whole response bytes) when it worked."""
    ctx = ssl.create_default_context()
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    inc, out = ssl.MemoryBIO(), ssl.MemoryBIO()
    obj = ctx.wrap_bio(inc, out, server_hostname=host)
    first = [True]
    s = None

    def flush():
        data = out.read()
        if not data:
            return
        if first[0] and mode == "split":
            s.sendall(data[:2])
            time.sleep(0.02)
            s.sendall(data[2:])
        else:
            s.sendall(data)
        first[0] = False

    try:
        s = socket.create_connection((ip, port), timeout=TIMEOUT)
        s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        while True:
            try:
                obj.do_handshake()
                flush()
                break
            except ssl.SSLWantReadError:
                flush()
                d = s.recv(65536)
                if not d:
                    return False, "CLOSED"
                inc.write(d)
        obj.write(("GET %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: Mozilla/5.0\r\nConnection: close\r\n\r\n" % (path, host)).encode())
        flush()
        buf = b""
        while body or b"\r\n" not in buf:
            try:
                chunk = obj.read(65536)
                if not chunk:
                    break
                buf += chunk
            except ssl.SSLWantReadError:
                d = s.recv(65536)
                if not d:
                    break
                inc.write(d)
            except (ssl.SSLZeroReturnError, ssl.SSLEOFError):
                break
        if body:
            return True, buf
        line = buf.split(b"\r\n")[0].decode(errors="replace")
        return True, " ".join(line.split(" ")[1:2]) or "empty"
    except Exception as e:
        return False, classify(e)
    finally:
        if s:
            s.close()


def http_get(ip, header, host):
    s = None
    try:
        s = socket.create_connection((ip, 80), timeout=TIMEOUT)
        s.sendall(("GET / HTTP/1.1\r\n%s: %s\r\nUser-Agent: Mozilla/5.0\r\nConnection: close\r\n\r\n" % (header, host)).encode())
        buf = b""
        while b"\r\n" not in buf:
            d = s.recv(4096)
            if not d:
                break
            buf += d
        line = buf.split(b"\r\n")[0].decode(errors="replace")
        return True, " ".join(line.split(" ")[1:2]) or "CLOSED"
    except Exception as e:
        return False, classify(e)
    finally:
        if s:
            s.close()


def retry(test, tries=3):
    """Runs test() until it works, at most `tries` times; the result says how many tries it took ("ok 200 (2/3)")."""
    why = "?"
    for i in range(tries):
        ok, why = test()
        if ok:
            return True, why if i == 0 else "%s (try %d)" % (why, i + 1)
        time.sleep(1)
    return False, "%s x%d" % (why, tries)


def fake(ips):
    return any(ip.startswith(("127.", "0.", "10.", "192.168.")) or ip == "::1" for ip in ips)


_genuine = {}


def genuine(name, ip, doh_ip):
    """True when ip really serves name: the DoH answer, or an address that completes a split handshake with a valid
    certificate for the name (CDNs answer differently per resolver). A loopback/private, missing or failing address is not."""
    if not ip or not ip[0].isdigit() or fake([ip]):
        return False
    if ip == doh_ip:
        return True
    if (name, ip) not in _genuine:
        ok, why = https_get(name, ip, "split")
        _genuine[(name, ip)] = ok
    return _genuine[(name, ip)]


def main():
    print("steam-check  %s  python %s  %s" % (time.strftime("%Y-%m-%d %H:%M"), sys.version.split()[0], sys.platform))
    print("\n== 1. DNS (device resolver | UDP 8.8.8.8 | UDP 1.1.1.1 | DoH)")
    real = {}
    dns_fake, udp_bad = [], []
    for n in NAMES:
        sysr, g, c, d = dns_system(n), dns_udp(n, "8.8.8.8"), dns_udp(n, "1.1.1.1"), dns_doh(n)
        real[n] = next((ip for ip in d if ip[0].isdigit()), None)
        if fake(sysr) or (real[n] and not genuine(n, sysr[0], real[n])):
            dns_fake.append(n)
        if real[n] and not (genuine(n, g[0], real[n]) and genuine(n, c[0], real[n])):
            udp_bad.append(n)
        print("%-30s %-16s | %-16s | %-16s | %s" % (n, sysr[0], g[0], c[0], d[0]))

    print("\n== 2. HTTPS per name to its real address: plain | split after 2 bytes")
    blocked, split_ok, split_bad = [], [], []
    for n in NAMES:
        ip = real.get(n)
        if not ip:
            print("%-30s no real address (DoH failed)" % n)
            continue
        # a single timeout can be a lost packet: each test gets up to 3 tries before it counts as blocked
        p_ok, p = retry(lambda: https_get(n, ip, "plain"))
        s_ok, sp = retry(lambda: https_get(n, ip, "split"))
        if not p_ok:
            blocked.append(n)
            (split_ok if s_ok else split_bad).append(n)
        print("%-30s %-10s | %s" % (n, ("ok " + p) if p_ok else p, ("ok " + sp) if s_ok else sp))
    if real.get("store.steampowered.com"):
        e_ok, e = https_get("example.com", real["store.steampowered.com"], "plain")
        opened = e_ok or e == "BADCERT"   # a certificate for another name still means the handshake got through
        print("%-30s %s   (another name to the same address)" % ("SNI example.com", "path open" if opened else "path closed: " + e))

    print("\n== 3. Port 80, store address: Host | HoSt | neutral")
    ip = real.get("store.steampowered.com")
    host_case = None
    if ip:
        a = retry(lambda: http_get(ip, "Host", "store.steampowered.com"))
        b = retry(lambda: http_get(ip, "HoSt", "store.steampowered.com"))
        c = retry(lambda: http_get(ip, "Host", "example.com"))
        host_case = (not a[0]) and b[0]
        print("Host: %-10s HoSt: %-10s neutral: %s" % (("ok " + a[1]) if a[0] else a[1], ("ok " + b[1]) if b[0] else b[1], ("ok " + c[1]) if c[0] else c[1]))

    print("\n== 4. Steam login servers (CM websockets from Steam's directory)")
    cm_line = "not tested"
    api_ip = real.get("api.steampowered.com")
    if api_ip:
        path = "/ISteamDirectory/GetCMListForConnect/v1/?cellid=0"
        body = None
        for mode in ("plain", "split"):
            ok, resp = https_get("api.steampowered.com", api_ip, mode, path=path, body=True)
            if ok and b"serverlist" in resp:
                body = resp
                break
        eps = []
        if body:
            try:
                head, _, rest = body.partition(b"\r\n\r\n")
                if b"chunked" in head.lower():
                    out = b""
                    while rest:
                        size_line, _, rest = rest.partition(b"\r\n")
                        size = int(size_line.split(b";")[0] or b"0", 16)
                        if size == 0:
                            break
                        out, rest = out + rest[:size], rest[size + 2:]
                    rest = out
                body = rest
                start = body.find(b"{")
                data = json.loads(body[start:body.rfind(b"}") + 1].decode())
                eps = [e["endpoint"] for e in data["response"]["serverlist"] if e.get("type") == "websockets"]
            except Exception:
                eps = []
        if not eps:
            cm_line = "could not read the CM list (api blocked or format changed)"
        else:
            results = []
            for ep in eps[:3]:
                h, _, pt = ep.rpartition(":")
                ips = dns_doh(h)
                if not ips or not ips[0][0].isdigit():
                    results.append("%s no address" % ep)
                    continue
                ok, r = https_get(h, ips[0], "plain", port=int(pt), path="/cmsocket/")
                results.append("%s %s" % (ep, ("ok " + r) if ok else r))
            cm_line = "; ".join(results)
    print(cm_line)

    print("\n== Verdict")
    print("DNS of this network rewrites: %s" % (", ".join(dns_fake) or "nothing"))
    print("UDP to public DNS tampered or blocked for: %s" % (", ".join(udp_bad) or "nothing"))
    print("Blocked over plain HTTPS: %s" % (", ".join(blocked) or "nothing"))
    if blocked:
        print("Split ClientHello gets through for: %s" % (", ".join(split_ok) or "nothing"))
        if split_bad:
            print("Split does NOT help for: %s" % ", ".join(split_bad))
    print("Host case trick (port 80): %s" % ("works" if host_case else "not needed or does not work" if host_case is False else "not tested"))
    ok_method = blocked and not split_bad
    print("=> The Steam access method (encrypted DNS + split ClientHello) %s on this network."
          % ("WORKS" if ok_method else "is NOT NEEDED" if not blocked else "does NOT fully work"))


if __name__ == "__main__":
    main()
