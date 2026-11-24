"""Fake Xtream provider that enforces max_connections=1 and records concurrent stream connections."""
import json, os, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

USER, PASS = "provuser", "provpass"
FILES = {101: "ch1.ts", 102: "ch2.ts"}
lock = threading.Lock()
active = 0
max_seen = 0
api_calls = 0
stream_requests = 0
rejected = 0

def xmltv():
    """Guide with half-hour shows around 'now' for both test channels."""
    import datetime as dt
    now = dt.datetime.now(dt.timezone(dt.timedelta(hours=2)))
    base = now.replace(minute=0 if now.minute < 30 else 30, second=0, microsecond=0) - dt.timedelta(hours=2)
    fmt = lambda t: t.strftime("%Y%m%d%H%M%S %z")
    out = ['<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE tv SYSTEM "xmltv.dtd"><tv>',
           '<channel id="one.za"><display-name>One</display-name></channel>']
    for ch in ("one.za", "two.za"):
        for i in range(12):
            s, e = base + dt.timedelta(minutes=30 * i), base + dt.timedelta(minutes=30 * (i + 1))
            out.append(f'<programme start="{fmt(s)}" stop="{fmt(e)}" channel="{ch}"><title lang="en">{ch} show {i} &amp; co</title><desc>Episode {i}</desc></programme>')
    out.append('</tv>')
    return "".join(out).encode()

class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass

    def send_json(self, obj, code=200):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        global active, max_seen, api_calls, stream_requests, rejected
        u = urlparse(self.path); q = {k: v[0] for k, v in parse_qs(u.query).items()}
        if u.path == "/stats":
            return self.send_json(dict(active=active, max_seen=max_seen, api_calls=api_calls,
                                       stream_requests=stream_requests, rejected=rejected))
        if u.path == "/player_api.php":
            with lock: api_calls += 1
            time.sleep(float(os.environ.get("API_DELAY", "0")))  # simulate a slow provider
            if q.get("username") != USER or q.get("password") != PASS:
                return self.send_json({"user_info": {"auth": 0}})
            a = q.get("action")
            if not a:
                return self.send_json({"user_info": {"username": USER, "password": PASS, "auth": 1, "max_connections": "1", "status": "Active"},
                                       "server_info": {"url": "provider.example", "port": "8080", "server_protocol": "http"}})
            if a == "get_live_categories":
                return self.send_json([{"category_id": "1", "category_name": "Test"}])
            if a == "get_live_streams":
                return self.send_json([{"stream_id": 101, "name": "Test One", "stream_icon": "http://x/1.png", "epg_channel_id": "one.za", "category_id": "1"},
                                       {"stream_id": "102", "name": "Test Two", "stream_icon": "", "epg_channel_id": "two.za", "category_id": "1"}])
            if a == "get_short_epg":
                return self.send_json({"epg_listings": [{"title": "TmV3cw==", "stream_id": q.get("stream_id")}]})
            return self.send_json([])
        if u.path == "/xmltv.php":
            b = xmltv()
            self.send_response(200); self.send_header("Content-Type", "application/xml"); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b); return
        if u.path.startswith("/movie/"):
            data = open("ch1.ts","rb").read()
            rng = self.headers.get("Range")
            if rng:
                a = int(rng.split("=")[1].split("-")[0]); part = data[a:]
                self.send_response(206); self.send_header("Content-Range", f"bytes {a}-{len(data)-1}/{len(data)}")
            else:
                part = data; self.send_response(200)
            self.send_header("Content-Type","video/mp2t"); self.send_header("Accept-Ranges","bytes"); self.send_header("Content-Length", str(len(part))); self.end_headers()
            self.wfile.write(part); return
        if u.path.startswith("/live/"):
            parts = u.path.split("/")
            sid = int(parts[4].split(".")[0])
            with lock:
                stream_requests += 1
                if active >= 1:
                    rejected += 1
                    print(f"REJECT second connection for {sid}", flush=True)
                    self.send_response(458); self.send_header("Content-Length", "0"); self.end_headers(); return
                active += 1; max_seen = max(max_seen, active)
            print(f"OPEN {sid} active={active}", flush=True)
            try:
                data = open(FILES[sid], "rb").read()
                rate = len(data) / 40.0
                self.send_response(200); self.send_header("Content-Type", "video/mp2t"); self.end_headers()
                self.close_connection = True
                pos = 0; chunk = int(rate / 10) // 188 * 188
                while True:
                    end = pos + chunk
                    self.wfile.write(data[pos:end]); self.wfile.flush()
                    pos = end if end < len(data) else 0
                    time.sleep(0.1)
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                with lock: active -= 1
                print(f"CLOSE {sid} active={active}", flush=True)
            return
        self.send_json({"err": "not found"}, 404)

ThreadingHTTPServer(("127.0.0.1", 9900), H).serve_forever()
