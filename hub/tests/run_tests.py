import asyncio, json, subprocess, time, urllib.request, urllib.error
import websockets

HUB = "http://127.0.0.1:8090"
CRED = "username=home&password=tvpass"
results = []

def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS " if cond else "FAIL ") + name + (f"  [{detail}]" if detail else ""), flush=True)

def get(path, method="GET", body=None):
    req = urllib.request.Request(HUB + path, method=method, data=body,
                                 headers={"Content-Type": "application/json"} if body else {})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()

def prov():
    with urllib.request.urlopen("http://127.0.0.1:9900/stats") as r:
        return json.loads(r.read())

class Tv:
    """Reads a live stream in the background like a TV player would."""
    def __init__(self, name, sid):
        self.name, self.sid, self.data, self.ended = name, sid, bytearray(), False
    async def run(self, seconds):
        reader, writer = await asyncio.open_connection("127.0.0.1", 8090)
        writer.write(f"GET /live/home/tvpass/{self.sid}.ts?device={self.name} HTTP/1.1\r\nHost: 127.0.0.1:8090\r\n\r\n".encode())
        await writer.drain()
        head = await reader.readuntil(b"\r\n\r\n")
        self.status = int(head.split()[1])
        chunked = b"chunked" in head.lower()
        end = time.time() + seconds
        try:
            while time.time() < end:
                try:
                    b = await asyncio.wait_for(reader.read(65536), timeout=max(0.1, end - time.time()))
                except asyncio.TimeoutError:
                    break
                if not b:
                    self.ended = True
                    break
                self.data += b
        finally:
            writer.close()
        if chunked:  # de-chunk
            raw, out, i = bytes(self.data), bytearray(), 0
            while i < len(raw):
                j = raw.find(b"\r\n", i)
                if j < 0: break
                try: n = int(raw[i:j], 16)
                except ValueError: break
                if n == 0: break
                out += raw[j + 2:j + 2 + n]; i = j + 2 + n + 2
            self.data = out

def ts_info(data):
    pid = ((data[1] & 0x1F) << 8) | data[2]
    p2 = ((data[189] & 0x1F) << 8) | data[190]
    p3 = data[376:]
    afc = (p3[3] >> 4) & 3
    rai = afc & 2 and p3[4] > 0 and (p3[5] & 0x40) != 0
    return pid, p2, bool(rai)

def ffprobe_frames(data, fname):
    open(fname, "wb").write(data)
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames",
                          "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", fname],
                         capture_output=True, text=True)
    first = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1",
                            "-show_entries", "frame=pict_type,key_frame", "-of", "csv=p=0", fname],
                           capture_output=True, text=True)
    return out.stdout.strip(), first.stdout.strip(), out.stderr.strip()[:300]

async def ws_client(name, inbox):
    ws = await websockets.connect(f"ws://127.0.0.1:8090/sync?{CRED}&device={name}")
    async def pump():
        try:
            async for m in ws:
                inbox.append(json.loads(m))
        except Exception:
            pass
    asyncio.create_task(pump())
    return ws

def last_state(inbox):
    s = [m for m in inbox if m.get("type") == "state"]
    return s[-1] if s else None

async def main():
    # ---- API ----
    st, body = get(f"/player_api.php?{CRED}")
    j = json.loads(body)
    check("login rewritten to hub", st == 200 and j["server_info"]["url"] == "127.0.0.1" and j["server_info"]["port"] == "8090"
          and j["user_info"]["username"] == "home" and "hub" in j, j.get("server_info"))
    check("provider password not leaked", b"provpass" not in body)
    st, _ = get("/player_api.php?username=home&password=wrong")
    check("wrong password rejected", st == 401)
    before = prov()["api_calls"]
    for _ in range(5):
        st, body = get(f"/player_api.php?{CRED}&action=get_live_streams")
    check("live streams served", st == 200 and len(json.loads(body)) == 2)
    check("live streams cached (no extra provider calls)", prov()["api_calls"] == before, prov()["api_calls"] - before)
    st, body = get(f"/xmltv.php?{CRED}")
    check("xmltv served", st == 200 and b"<tv>" in body)
    st, body = get(f"/player_api.php?{CRED}&action=get_short_epg&stream_id=101")
    check("short epg passthrough", st == 200 and b"epg_listings" in body)

    # ---- EPG worked out on the hub ----
    st, body = get(f"/api/epg/now?{CRED}")
    chans = json.loads(body).get("channels", {}) if st == 200 else {}
    now_ms = time.time() * 1000
    ok = st == 200 and set(chans) == {"101", "102"} and all(
        c["now"] and c["now"]["start"] <= now_ms < c["now"]["end"] and c["next"]["start"] == c["now"]["end"] for c in chans.values())
    check("EPG now/next for every channel", ok, {k: v["now"]["title"] for k, v in chans.items()} if chans else body[:200])
    st, body = get(f"/api/epg/101?{CRED}&hours=3")
    progs = json.loads(body).get("programmes", []) if st == 200 else []
    check("EPG listings for one channel", st == 200 and len(progs) >= 6 and progs[0]["title"].startswith("one.za"), len(progs))
    st, _ = get("/api/epg/now?username=home&password=wrong")
    check("EPG needs the TV password", st == 401)

    # ---- sync + live ----
    inA, inB = [], []
    wsA = await ws_client("LoungeTV", inA)
    wsB = await ws_client("BedroomTV", inB)
    await asyncio.sleep(0.5)
    check("sync sends initial idle state", last_state(inA) and last_state(inA)["mode"] == "idle")
    check("sync sends favourites on connect", any(m.get("type") == "favorites" for m in inB))

    tv1 = Tv("LoungeTV", 101)
    t1 = asyncio.create_task(tv1.run(14))
    await asyncio.sleep(4)
    s = last_state(inB)
    check("other TV told what is playing", s and s["mode"] == "live" and s["streamId"] == 101 and s["name"] == "Test One", s)
    tv2 = Tv("BedroomTV", 101)
    await tv2.run(3)
    p = prov()
    check("provider sees ONE connection for 2 TVs", p["active"] == 1 and p["max_seen"] == 1, p)
    pid0, pid1, rai = ts_info(tv2.data)
    check("late joiner starts with PAT, PMT, keyframe", pid0 == 0 and rai, (pid0, pid1, rai))
    frames, first, err = ffprobe_frames(bytes(tv2.data), "tv2.ts")
    check("late joiner stream decodes from first frame", first.startswith("1") or ",I" in first or first.startswith("I"), f"frames={frames} first={first} {err}")
    check("late joiner received ~live data quickly", len(tv2.data) > 200_000, len(tv2.data))

    # ---- switch channel from TV2 ----
    tv3 = Tv("BedroomTV", 102)
    t3 = asyncio.create_task(tv3.run(6))
    await asyncio.sleep(4)
    await t1
    check("TV1's old stream ended when TV2 switched", tv1.ended, len(tv1.data))
    s = last_state(inA)
    check("TV1 told new channel", s and s["streamId"] == 102 and s["name"] == "Test Two", s)
    p = prov()
    check("still never more than 1 provider connection", p["max_seen"] == 1, p)
    check("switch succeeded after provider released slot", len(tv3.data) > 100_000, f"bytes={len(tv3.data)} rejected={p['rejected']}")

    # ---- stop from any TV ----
    tv4 = Tv("LoungeTV", 102)
    t4 = asyncio.create_task(tv4.run(8))
    await asyncio.sleep(2)
    await wsA.send(json.dumps({"cmd": "stop"}))
    await t3; await t4
    await asyncio.sleep(1)
    check("stop ends every TV's stream", tv4.ended, len(tv4.data))
    s = last_state(inB)
    check("all TVs told idle after stop", s and s["mode"] == "idle" and "LoungeTV" in (s.get("reason") or ""), s)
    check("provider connection closed after stop", prov()["active"] == 0, prov())

    # ---- WS play pre-warm + idle timeout ----
    await wsB.send(json.dumps({"cmd": "play", "streamId": 101}))
    await asyncio.sleep(2)
    check("ws play starts channel for everyone", last_state(inA)["mode"] == "live" and last_state(inA)["streamId"] == 101)
    await asyncio.sleep(8)
    s = last_state(inA)
    check("idle timeout closes unwatched stream", s["mode"] == "idle" and s["reason"] == "no_viewers" and prov()["active"] == 0, (s, prov()))

    # ---- favourites ----
    st, _ = get(f"/api/favorites/live?{CRED}", "PUT", b"[101, 102]")
    st2, _ = get(f"/api/favorites/live/555?{CRED}", "POST")
    st3, _ = get(f"/api/favorites/live/101?{CRED}", "DELETE")
    await asyncio.sleep(0.5)
    favs = [m for m in inA if m.get("type") == "favorites"][-1]
    check("favourites synced to all TVs", st == st2 == st3 == 200 and favs["live"] == [102, 555], favs)

    # ---- CPU with 4 TVs ----
    pid = subprocess.run(["pgrep", "-f", "BertsIptvHub.dll"], capture_output=True, text=True).stdout.split()[0]
    def cpu():
        f = open(f"/proc/{pid}/stat").read().split(")")[1].split()
        return (int(f[11]) + int(f[12])) / 100.0
    tvs = [Tv(f"TV{i}", 101) for i in range(4)]
    tasks = [asyncio.create_task(t.run(20)) for t in tvs]
    await asyncio.sleep(5)
    c0, w0 = cpu(), time.time()
    await asyncio.gather(*tasks)
    c1, w1 = cpu(), time.time()
    pct = (c1 - c0) / (w1 - w0) * 100
    check("4 TVs got data", all(len(t.data) > 1_000_000 for t in tvs), [len(t.data) for t in tvs])
    check("hub CPU low with 4 TVs", pct < 10, f"{pct:.2f}% of one core")
    print("status:", json.dumps(json.loads(get('/api/status')[1])["state"]))

    await wsA.close(); await wsB.close()
    print(f"\n{sum(r for _, r in results)}/{len(results)} passed")

asyncio.run(main())
