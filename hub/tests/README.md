# Hub tests

End-to-end tests against a fake Xtream provider that allows only one connection.

```bash
pip install websockets
cd hub/tests
for i in 1 2; do ffmpeg -y -f lavfi -i testsrc=size=1280x720:rate=25 -f lavfi -i sine=frequency=$((400*i)) \
  -t 40 -c:v libx264 -b:v 2M -g 50 -c:a aac -f mpegts ch$i.ts; done
python3 fakeprov.py &          # fake provider on :9900 (provuser / provpass)
# start the hub with Provider.Url=http://127.0.0.1:9900, LocalPassword=tvpass, IdleTimeoutSeconds=6
python3 run_tests.py
```
