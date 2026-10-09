"""Run training on the shared T4 namespace (docs/train-t4.md). Only touches objects named squad-*.

    python train/cluster/squadctl.py status                    # quota, jobs and pods of the namespace
    python train/cluster/squadctl.py start smoke               # pack, create the Job, upload the bundle into its pod
    python train/cluster/squadctl.py start night --hours 10    # branches A and B side by side
    python train/cluster/squadctl.py sh "tail -n 20 /work/squad/runs/A/log.txt"
    python train/cluster/squadctl.py fetch /work/squad/runs/A/latest.pt out/A-latest.pt
    python train/cluster/squadctl.py stop squad-night-1009      # delete one of OUR jobs
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import kube  # noqa: E402
import pack  # noqa: E402

IMAGE = kube.IMAGE
PART_OF = "gpu-workspace"
PREFIX = "squad-"

# The pod waits for the bundle, checks its hash, unpacks it once per bundle and runs the entry script.
# On a container restart the bundle is already on the volume, so it goes straight to the entry (which continues).
BOOTSTRAP = r"""
set -eu
mkdir -p /work/squad/bundles /work/squad/src
B=/work/squad/bundles/$BUNDLE.tar.gz
i=0
while [ ! -f "$B" ]; do i=$((i+1)); [ $i -gt 720 ] && { echo "no bundle after 1 h"; exit 3; }; sleep 5; done
echo "$BUNDLE_SHA  $B" | sha256sum -c -
S=/work/squad/src/$BUNDLE
if [ ! -f "$S/.ok" ]; then rm -rf "$S.tmp"; mkdir -p "$S.tmp"; tar -xzf "$B" -C "$S.tmp"; rm -rf "$S"; mv "$S.tmp" "$S"; touch "$S/.ok"; fi
cd "$S"
C=/work/squad/restarts-$JOB; n=$(cat "$C" 2>/dev/null || echo 0); n=$((n+1)); echo $n > "$C"; export RESTART=$n
exec sh "train/cluster/$ENTRY"
"""


def job_spec(name, entry, bundle, sha, gpus, hours, env):
    labels = {"app.kubernetes.io/part-of": PART_OF, "app.kubernetes.io/name": name, "squad/role": entry}
    envs = [{"name": "HOME", "value": "/work"}, {"name": "JOB", "value": name}, {"name": "ENTRY", "value": entry + ".sh"},
            {"name": "BUNDLE", "value": bundle}, {"name": "BUNDLE_SHA", "value": sha},
            {"name": "OMP_NUM_THREADS", "value": "2"}, {"name": "PYTHONUNBUFFERED", "value": "1"}]
    envs += [{"name": k, "value": str(v)} for k, v in env.items()]
    return {
        "apiVersion": "batch/v1", "kind": "Job",
        "metadata": {"name": name, "labels": labels},
        "spec": {
            "backoffLimit": 6 if entry == "night" else 1,
            "activeDeadlineSeconds": int(hours * 3600),
            "ttlSecondsAfterFinished": 3 * 86400,
            "template": {
                "metadata": {"labels": labels},
                "spec": {
                    "restartPolicy": "OnFailure",
                    "serviceAccountName": "gpu-workload",
                    "automountServiceAccountToken": False,
                    "imagePullSecrets": [{"name": "gpu-registry"}],
                    "nodeSelector": {"nvidia.com/gpu.product": "Tesla-T4"},
                    "securityContext": {"runAsUser": 1000, "runAsGroup": 1000, "fsGroup": 1000},
                    "containers": [{
                        "name": "train", "image": IMAGE, "workingDir": "/work",
                        "command": ["sh", "-c", BOOTSTRAP], "env": envs,
                        "resources": {
                            "requests": {"cpu": "3", "memory": "12Gi", "nvidia.com/gpu": str(gpus)},
                            "limits": {"cpu": "20", "memory": "48Gi", "nvidia.com/gpu": str(gpus)}},
                        "securityContext": {"allowPrivilegeEscalation": False, "capabilities": {"drop": ["ALL"]}},
                        "volumeMounts": [{"name": "work", "mountPath": "/work"}, {"name": "shm", "mountPath": "/dev/shm"}],
                    }],
                    "volumes": [{"name": "work", "persistentVolumeClaim": {"claimName": "gpu-model-work"}},
                                {"name": "shm", "emptyDir": {"medium": "Memory", "sizeLimit": "1Gi"}}],
                },
            },
        },
    }


def quota():
    q = {}
    for item in kube.get_json("resourcequota")["items"]:
        for k, v in item["status"].get("used", {}).items():
            q[k] = (v, item["status"]["hard"].get(k))
    return q


def cpu(v):
    return float(v[:-1]) / 1000 if v.endswith("m") else float(v)


def our_pod(job=None):
    pods = kube.get_json("pods", "-l", f"app.kubernetes.io/part-of={PART_OF}")["items"]
    pods = [p for p in pods if p["metadata"]["name"].startswith(PREFIX) and p["status"].get("phase") == "Running"
            and (job is None or p["metadata"].get("labels", {}).get("job-name") == job)]
    if not pods:
        raise SystemExit("no running squad-* pod")
    return sorted(pods, key=lambda p: p["metadata"]["creationTimestamp"])[-1]["metadata"]["name"]


def cmd_status(_):
    for k, (used, hard) in sorted(quota().items()):
        if any(s in k for s in ("gpu", "cpu", "memory", "pods")):
            print(f"{k:28s} {used} / {hard}")
    print(kube.kubectl("get", "jobs,pods", "-o", "wide").decode())


def cmd_start(a):
    q = quota()
    gpus = 1 if a.entry == "smoke1" else 2
    entry = "smoke" if a.entry.startswith("smoke") else a.entry
    g_used, g_hard = q.get("requests.nvidia.com/gpu", ("0", "2"))
    c_used, c_hard = q.get("requests.cpu", ("0", "4"))
    l_used, l_hard = q.get("limits.cpu", ("0", "20"))
    if int(g_used) + gpus > int(g_hard) or cpu(c_used) + 3 > cpu(c_hard) or cpu(l_used) + 20 > cpu(l_hard):
        raise SystemExit(f"quota is busy (GPU {g_used}/{g_hard}, requests.cpu {c_used}/{c_hard}, limits.cpu {l_used}/{l_hard}): "
                         "someone else is training — wait, do not free it")
    path, sha, n = pack.pack()
    bundle = sha[:12]
    print(f"bundle {bundle}: {n} files, {os.path.getsize(path) / 1e6:.1f} MB")
    name = a.name or f"{PREFIX}{entry}-{time.strftime('%m%d-%H%M')}"
    env = dict(e.split("=", 1) for e in a.env)
    spec = job_spec(name, entry, bundle, sha, gpus, a.hours, env)
    print(kube.apply(spec))
    pod = None
    for _ in range(120):
        pods = kube.get_json("pods", "-l", f"job-name={name}")["items"]
        running = [p for p in pods if p["status"].get("phase") == "Running"]
        if running:
            pod = running[0]["metadata"]["name"]
            break
        if pods:
            st = pods[0]["status"]
            waiting = [c["state"].get("waiting", {}).get("reason") for c in st.get("containerStatuses", []) if "waiting" in c["state"]]
            print(f"  {pods[0]['metadata']['name']}: {st.get('phase')} {waiting or ''}")
        time.sleep(10)
    if not pod:
        raise SystemExit(f"{name}: no running pod after 20 min — check `status`")
    kube.upload(pod, path, f"/work/squad/bundles/{bundle}.tar.gz")
    print(f"{name} is running in {pod}")


def cmd_readout(a):
    """A pod without GPU on our volume, to read and fetch results after a job has ended. Delete it with `stop`."""
    q = quota()
    c_used, c_hard = q.get("requests.cpu", ("0", "4"))
    if cpu(c_used) + 1 > cpu(c_hard):
        raise SystemExit(f"requests.cpu {c_used}/{c_hard}: no room even for a 1-CPU pod — wait")
    name = f"{PREFIX}readout-{time.strftime('%m%d-%H%M')}"
    spec = job_spec(name, "readout", "-", "-", 0, a.hours, {})
    spec["spec"]["backoffLimit"] = 0
    pod = spec["spec"]["template"]["spec"]
    c = pod["containers"][0]
    c["command"] = ["sleep", str(int(a.hours * 3600))]
    c["resources"] = {"requests": {"cpu": "1", "memory": "2Gi"}, "limits": {"cpu": "2", "memory": "4Gi"}}
    c["volumeMounts"][0]["readOnly"] = True
    pod["volumes"][0]["persistentVolumeClaim"]["readOnly"] = True
    print(kube.apply(spec))
    for _ in range(60):
        pods = [p for p in kube.get_json("pods", "-l", f"job-name={name}")["items"] if p["status"].get("phase") == "Running"]
        if pods:
            print(f"{name}: {pods[0]['metadata']['name']} is running; `stop {name}` when done")
            return
        time.sleep(5)
    raise SystemExit(f"{name}: not running after 5 min — check `status`")


def cmd_push(a):
    """Upload the bundle a job waits for (when the upload in `start` was cut off)."""
    job = kube.get_json("job", a.job)
    env = {e["name"]: e.get("value") for e in job["spec"]["template"]["spec"]["containers"][0]["env"]}
    path = os.path.join(os.path.dirname(pack.__file__), "out", f"bundle-{env['BUNDLE']}.tar.gz")
    if kube.sha256_file(path) != env["BUNDLE_SHA"]:
        raise SystemExit(f"{path} does not match the job's bundle")
    kube.upload(our_pod(a.job), path, f"/work/squad/bundles/{env['BUNDLE']}.tar.gz")


def cmd_sh(a):
    print(kube.sh(a.pod or our_pod(), a.script, timeout=a.timeout), end="")


def cmd_fetch(a):
    kube.download(a.pod or our_pod(), a.remote, a.local)


def cmd_stop(a):
    if not a.job.startswith(PREFIX):
        raise SystemExit(f"{a.job} is not ours (ours start with {PREFIX})")
    print(kube.kubectl("delete", "job", a.job, "--ignore-not-found=true", "--wait=false").decode())


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    s = sub.add_parser("start")
    s.add_argument("entry", choices=["smoke", "smoke1", "night"])
    s.add_argument("--hours", type=float, default=1.5, help="activeDeadlineSeconds of the job")
    s.add_argument("--name", default=None)
    s.add_argument("--env", nargs="*", default=[], help="KEY=VALUE for the entry script (THREADS_A, RUN_A, ...)")
    s.set_defaults(fn=cmd_start)
    s = sub.add_parser("readout")
    s.add_argument("--hours", type=float, default=2)
    s.set_defaults(fn=cmd_readout)
    s = sub.add_parser("push")
    s.add_argument("job")
    s.set_defaults(fn=cmd_push)
    s = sub.add_parser("sh")
    s.add_argument("script")
    s.add_argument("--pod", default=None)
    s.add_argument("--timeout", type=int, default=90)
    s.set_defaults(fn=cmd_sh)
    s = sub.add_parser("fetch")
    s.add_argument("remote")
    s.add_argument("local")
    s.add_argument("--pod", default=None)
    s.set_defaults(fn=cmd_fetch)
    s = sub.add_parser("stop")
    s.add_argument("job")
    s.set_defaults(fn=cmd_stop)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
