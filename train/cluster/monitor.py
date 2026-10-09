"""Local dashboard for the cluster training: http://127.0.0.1:8790

    python train/cluster/monitor.py [--port 8790] [--every 30]

Every poll: quota, jobs and pods of the namespace (read-only), and — while one of our squad-* pods runs — one short exec
that returns status.json, new metrics.jsonl lines, eval.json, log tails, GPU load as gzip+base64 with a SHA-256.
Everything fetched is mirrored under train/cluster/out/monitor/, so the charts survive a restart of the dashboard and
the end of the job. Reads only; never creates or deletes anything on the cluster.
"""
import argparse
import json
import os
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kube  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.join(HERE, "out", "monitor")
PART_OF = "gpu-workspace"

READER = r'''
import glob, json, os, subprocess, time
REQ = json.loads(%r)
LIMIT = 60000
out = {"time": time.time(), "runs": {}, "smoke": {}}
def tail(p, n=40):
    try:
        with open(p, "rb") as f:
            f.seek(0, 2); size = f.tell(); f.seek(max(0, size - 12000))
            return f.read().decode("utf-8", "replace").splitlines()[-n:]
    except OSError:
        return None
def load(p):
    try:
        return json.load(open(p))
    except Exception:
        return None
budget = LIMIT
for d in sorted(glob.glob("/work/squad/runs/*") + glob.glob("/work/squad/smoke/*/speed/*")):
    if not os.path.isdir(d):
        continue
    r = {"status": load(d + "/status.json"), "eval": load(d + "/eval.json"),
         "log": tail(d + "/log.txt", 25), "out": tail(d + "/job.out", 25)}
    m = d + "/metrics.jsonl"
    if os.path.exists(m):
        size = os.path.getsize(m)
        off = REQ.get(m, 0)
        if off > size:
            off = 0
        with open(m, "rb") as f:
            f.seek(off)
            data = f.read(max(0, budget))
        cut = data.rfind(b"\n") + 1
        budget -= cut
        r["metrics"] = {"offset": off, "next": off + cut, "size": size, "text": data[:cut].decode("utf-8", "replace")}
    r["files"] = sorted(os.path.basename(p) for p in glob.glob(d + "/*.pt"))
    out["runs"][d] = r
for d in sorted(glob.glob("/work/squad/smoke/*")):
    out["smoke"][d] = {"log": tail(d + "/log.txt", 60), "summary": load(d + "/summary.json")}
try:
    q = subprocess.run(["nvidia-smi", "--query-gpu=index,utilization.gpu,memory.used,memory.total,temperature.gpu",
                        "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=10).stdout
    out["gpu"] = [[float(x) for x in l.split(",")] for l in q.strip().splitlines()]
except Exception as e:
    out["gpu"] = str(e)
try:
    st = dict(l.split() for l in open("/sys/fs/cgroup/cpu.stat"))
    out["cpu_usec"] = int(st["usage_usec"])
    out["cpu_max"] = open("/sys/fs/cgroup/cpu.max").read().strip()
except Exception:
    try:   # cgroup v1, as on this cluster's nodes
        out["cpu_usec"] = int(open("/sys/fs/cgroup/cpuacct/cpuacct.usage").read()) // 1000
        out["cpu_max"] = open("/sys/fs/cgroup/cpu/cpu.cfs_quota_us").read().strip()
    except Exception:
        pass
out["load"] = open("/proc/loadavg").read().split()[:3]
print(json.dumps(out))
'''


class Monitor:
    def __init__(self, every):
        self.every = every
        self.lock = threading.Lock()
        os.makedirs(os.path.join(STORE, "metrics"), exist_ok=True)
        self.state_path = os.path.join(STORE, "state.json")
        try:
            with open(self.state_path) as f:
                self.state = json.load(f)
        except (OSError, ValueError):
            self.state = {}
        self.state.setdefault("offsets", {})
        self.state.setdefault("runs", {})
        self.state.setdefault("smoke", {})
        self.state["errors"] = []
        self.poke = threading.Event()

    def save(self):
        tmp = self.state_path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(self.state, f)
        os.replace(tmp, self.state_path)

    @staticmethod
    def metrics_file(remote_dir):
        name = remote_dir.replace("/work/squad/", "").replace("/", "__")
        return os.path.join(STORE, "metrics", name + ".jsonl")

    def poll_cluster(self):
        s = {"tunnel": kube.tunnel_up(), "polled": time.time()}
        if not s["tunnel"]:
            raise kube.KubeError(f"proxy tunnel 127.0.0.1:{kube.PORT} is not listening")
        quota = {}
        for item in kube.get_json("resourcequota")["items"]:
            for k, v in item["status"].get("used", {}).items():
                quota[k] = [v, item["status"]["hard"].get(k)]
        s["quota"] = quota
        jobs = kube.get_json("jobs")["items"]
        s["jobs"] = [{"name": j["metadata"]["name"], "ours": j["metadata"]["name"].startswith("squad-"),
                      "created": j["metadata"]["creationTimestamp"],
                      "active": j["status"].get("active", 0), "succeeded": j["status"].get("succeeded", 0),
                      "failed": j["status"].get("failed", 0),
                      "deadline": j["spec"].get("activeDeadlineSeconds"),
                      "conditions": [c["type"] for c in j["status"].get("conditions", []) if c.get("status") == "True"]}
                     for j in jobs]
        s["jobs"].sort(key=lambda j: j["created"], reverse=True)
        pods = kube.get_json("pods")["items"]
        s["pods"] = []
        for p in pods:
            cs = p["status"].get("containerStatuses", [{}])
            res = p["spec"]["containers"][0].get("resources", {}).get("requests", {})
            s["pods"].append({"name": p["metadata"]["name"], "ours": p["metadata"]["name"].startswith("squad-"),
                              "phase": p["status"].get("phase"), "node": p["spec"].get("nodeName"),
                              "restarts": sum(c.get("restartCount", 0) for c in cs),
                              "created": p["metadata"]["creationTimestamp"],
                              "gpu": res.get("nvidia.com/gpu", "0"), "cpu": res.get("cpu", "0"),
                              "waiting": [c["state"]["waiting"].get("reason") for c in cs if "waiting" in c.get("state", {})]})
        s["pods"].sort(key=lambda p: p["created"], reverse=True)
        return s

    def poll_pod(self, pod):
        with self.lock:
            offsets = dict(self.state["offsets"])
        text = kube.sh_big(pod, "python3 - <<'PYEOF'\n" + (READER % json.dumps(offsets)) + "\nPYEOF", timeout=120)
        data = json.loads(text)
        with self.lock:
            prev = self.state.get("pod_sample")
            if prev and prev.get("pod") == pod and "cpu_usec" in data and data["time"] > prev["time"]:
                data["cpu_cores"] = (data["cpu_usec"] - prev["cpu_usec"]) / 1e6 / (data["time"] - prev["time"])
            self.state["pod_sample"] = {"pod": pod, "time": data["time"], "cpu_usec": data.get("cpu_usec", 0)}
            for d, r in data["runs"].items():
                m = r.pop("metrics", None)
                if m:
                    path = self.metrics_file(d)
                    if m["offset"] == 0 and os.path.exists(path):
                        os.remove(path)   # the remote file started over
                    if m["text"]:
                        with open(path, "a", encoding="utf-8") as f:
                            f.write(m["text"])
                    self.state["offsets"][d + "/metrics.jsonl"] = m["next"]
                    r["metrics_size"], r["metrics_have"] = m["size"], m["next"]
                r["seen"] = data["time"]
                self.state["runs"][d] = r
            self.state["smoke"].update(data["smoke"])
            self.state["pod"] = {k: data.get(k) for k in ("gpu", "load", "cpu_cores", "cpu_max", "time")} | {"name": pod}
            behind = any(r.get("metrics_have", 0) < r.get("metrics_size", 0) for r in data["runs"].values())
        return behind

    def loop(self):
        while True:
            errors = []
            behind = False
            try:
                s = self.poll_cluster()
                with self.lock:
                    self.state["cluster"] = s
                running = [p["name"] for p in s["pods"] if p["ours"] and p["phase"] == "Running"]
                if running:
                    try:
                        behind = self.poll_pod(running[0])
                    except Exception as e:  # noqa: BLE001
                        errors.append(f"pod read: {e}")
                else:
                    with self.lock:
                        self.state["pod"] = None
            except Exception as e:  # noqa: BLE001
                errors.append(str(e))
                traceback.print_exc()
            with self.lock:
                self.state["errors"] = errors
                self.state["last_poll"] = time.time()
                self.save()
            self.poke.wait(3 if behind else self.every)
            self.poke.clear()

    def snapshot(self):
        with self.lock:
            st = json.loads(json.dumps({k: v for k, v in self.state.items() if k not in ("offsets",)}))
        metrics = {}
        for d in st.get("runs", {}):
            path = self.metrics_file(d)
            rows = []
            if os.path.exists(path):
                with open(path, encoding="utf-8") as f:
                    for line in f:
                        try:
                            rows.append(json.loads(line))
                        except ValueError:
                            pass
            # a continued run re-logs the updates after its last checkpoint: keep the latest copy of each step
            clean = []
            for r in rows:
                while clean and clean[-1].get("global_steps", 0) >= r.get("global_steps", 0):
                    clean.pop()
                clean.append(r)
            keep = ("global_steps", "steps", "stage", "sps", "rollout_sps", "win", "draw", "ep_seconds", "entropy", "kl",
                    "vloss", "ev", "shaping", "lr", "clipfrac", "reward_per_step", "by_scenario")
            metrics[d] = [{k: r.get(k) for k in keep} for r in clean]
        st["metrics"] = metrics
        st["now"] = time.time()
        return st


def make_handler(mon):
    page = os.path.join(HERE, "monitor.html")

    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def send(self, code, body, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path == "/api/state":
                self.send(200, json.dumps(mon.snapshot()).encode(), "application/json")
            elif self.path == "/api/poll":
                mon.poke.set()
                self.send(200, b"{}", "application/json")
            elif self.path in ("/", "/index.html"):
                with open(page, "rb") as f:
                    self.send(200, f.read(), "text/html; charset=utf-8")
            else:
                self.send(404, b"not found", "text/plain")

    return H


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8790)
    ap.add_argument("--every", type=int, default=30, help="seconds between polls of the cluster")
    a = ap.parse_args()
    mon = Monitor(a.every)
    threading.Thread(target=mon.loop, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), make_handler(mon))
    print(f"dashboard: http://127.0.0.1:{a.port}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
