"""kubectl for the shared T4 namespace, from Python, without PowerShell quoting.

Mirrors the cluster's kubectl wrapper: a temporary kubeconfig built from the user's own (their authorisation and CAs,
TLS verification on), with the server, the local proxy tunnel and the namespace pinned. Those, and the image, live in
train/cluster/cluster.local.json (not in git; see cluster.example.json). The user's active context is never changed. Arguments go to kubectl as a list, scripts travel as base64, big outputs come back as
gzip+base64 with a SHA-256 (exec output through the proxy is cut at 32–64 KiB), uploads are chunked and hash-checked
(kubectl cp through the tunnel can exit 0 with a partial file).
"""
import atexit
import base64
import gzip
import hashlib
import json
import os
import socket
import subprocess
import tempfile
import time

_LOCAL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cluster.local.json")
if not os.path.exists(_LOCAL):
    raise SystemExit(f"{_LOCAL} is missing: copy cluster.example.json and fill in the cluster")
with open(_LOCAL) as _f:
    CLUSTER = json.load(_f)
NAMESPACE, SERVER, PORT, IMAGE = CLUSTER["namespace"], CLUSTER["server"], int(CLUSTER["proxy_port"]), CLUSTER["image"]
PROXY = f"http://127.0.0.1:{PORT}"
FORBIDDEN = ("--kubeconfig", "--context", "--server", "--namespace", "--all-namespaces", "--insecure-skip-tls-verify")

_config = None


class KubeError(RuntimeError):
    pass


def tunnel_up():
    try:
        with socket.create_connection(("127.0.0.1", PORT), timeout=3):
            return True
    except OSError:
        return False


def _env():
    env = dict(os.environ)
    env.update(HTTP_PROXY=PROXY, HTTPS_PROXY=PROXY, NO_PROXY="localhost,127.0.0.1")
    return env


def _kubeconfig():
    global _config
    if _config and os.path.exists(_config):
        return _config
    raw = subprocess.run(["kubectl", "config", "view", "--minify", "--raw", "-o", "json"], capture_output=True,
                         text=True, timeout=30)
    if raw.returncode != 0:
        raise KubeError("cannot read the source kubeconfig")
    cfg = json.loads(raw.stdout)
    if len(cfg.get("clusters", [])) != 1 or len(cfg.get("contexts", [])) != 1 or len(cfg.get("users", [])) != 1:
        raise KubeError("expected one cluster, context and user in the minified kubeconfig")
    cluster = cfg["clusters"][0]["cluster"]
    if cluster.get("insecure-skip-tls-verify"):
        raise KubeError("TLS verification must be enabled")
    cluster["server"] = SERVER
    cluster["proxy-url"] = PROXY
    cfg["contexts"][0]["context"]["namespace"] = NAMESPACE
    user = cfg["users"][0]["user"]
    if not user.get("exec"):
        raise KubeError("expected the exec authentication provider")
    args, src = [], list(user["exec"].get("args") or [])
    skip = False
    for a in src:
        if skip:
            skip = False
            continue
        if a == "--authentication-timeout-sec":
            skip = True
            continue
        if a.startswith("--skip-open-browser") or a.startswith("--authentication-timeout-sec="):
            continue
        args.append(a)
    user["exec"]["args"] = args + ["--skip-open-browser", "--authentication-timeout-sec=15"]
    fd, path = tempfile.mkstemp(prefix="squad-kube-", suffix=".json")
    with os.fdopen(fd, "w") as f:
        json.dump(cfg, f)
    atexit.register(lambda: os.path.exists(path) and os.remove(path))
    _config = path
    return path


def kubectl(*args, input=None, timeout=60, check=True, request_timeout="20s"):
    """Run kubectl in the pinned namespace; returns stdout bytes."""
    for a in args:
        if a in ("-n", "-A", "-s") or any(a == f or a.startswith(f + "=") for f in FORBIDDEN):
            raise KubeError(f"connection and namespace overrides are not accepted: {a}")
    if not tunnel_up():
        raise KubeError(f"proxy tunnel 127.0.0.1:{PORT} is not listening")
    cmd = ["kubectl", "--kubeconfig", _kubeconfig(), f"--request-timeout={request_timeout}", "--namespace", NAMESPACE, *args]
    try:
        r = subprocess.run(cmd, input=input, capture_output=True, timeout=timeout, env=_env())
    except subprocess.TimeoutExpired:
        raise KubeError(f"kubectl {' '.join(args[:2])}: timed out after {timeout} s")
    if check and r.returncode != 0:
        raise KubeError(f"kubectl {' '.join(args[:2])}: {r.stderr.decode('utf-8', 'replace').strip()[-600:]}")
    return r.stdout


def get_json(*what):
    return json.loads(kubectl("get", *what, "-o", "json"))


def apply(obj):
    return kubectl("apply", "-f", "-", input=json.dumps(obj).encode()).decode().strip()


def sh(pod, script, timeout=90, container=None, attempts=4):
    """Run a POSIX shell script in the pod; returns stdout text (short outputs only: the proxy cuts long ones).
    The tunnel drops connections now and then, so a failed exec is retried: scripts given here must be idempotent."""
    enc = base64.b64encode(script.replace("\r\n", "\n").encode()).decode()
    extra = ["-c", container] if container else []
    for a in range(1, attempts + 1):
        try:
            return kubectl("exec", pod, *extra, "--", "sh", "-c", f"echo {enc} | base64 -d | sh", timeout=timeout,
                           request_timeout="0").decode("utf-8", "replace")
        except KubeError:
            if a == attempts:
                raise
            time.sleep(3 * a)


def sh_big(pod, script, timeout=120):
    """Like sh, but the script's stdout comes back gzip+base64 with a SHA-256 and is verified here."""
    wrapped = ("set -e\nout=$(mktemp)\n( " + script + "\n) > \"$out\" 2>&1 || true\n"
               "gzip -c \"$out\" | base64 -w0 > \"$out.b64\"\n"
               "printf '%s\\n' \"$(sha256sum \"$out.b64\" | cut -c1-64)\"\ncat \"$out.b64\"\nrm -f \"$out\" \"$out.b64\"\n")
    text = sh(pod, wrapped, timeout)
    lines = text.strip().split("\n", 1)
    if len(lines) != 2:
        raise KubeError("empty answer")
    digest, payload = lines[0].strip(), lines[1].strip()
    if hashlib.sha256(payload.encode()).hexdigest() != digest:
        raise KubeError(f"answer cut in transit ({len(payload)} chars)")
    return gzip.decompress(base64.b64decode(payload)).decode("utf-8", "replace")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def upload(pod, local, remote, chunk=1 << 20, attempts=5, log=print):
    """Copy a local file into the pod in hash-checked chunks, then check the whole file and rename it into place."""
    with open(local, "rb") as f:
        data = f.read()
    whole = hashlib.sha256(data).hexdigest()
    have = sh(pod, f"[ -f '{remote}' ] && sha256sum '{remote}' | cut -c1-64 || true").strip()
    if have == whole:
        log(f"{remote}: already there")
        return whole
    part = f"{remote}.part"
    sh(pod, f"mkdir -p \"$(dirname '{remote}')\" && rm -rf '{part}.d' && mkdir -p '{part}.d'")
    n = (len(data) + chunk - 1) // chunk
    for i in range(n):
        piece = data[i * chunk:(i + 1) * chunk]
        want = hashlib.sha256(piece).hexdigest()
        name = f"{part}.d/{i:05d}"
        for a in range(1, attempts + 1):
            try:
                kubectl("exec", "-i", pod, "--", "sh", "-c", f"cat > '{name}'", input=piece, timeout=120,
                        request_timeout="0")
                got = sh(pod, f"sha256sum '{name}' | cut -c1-64").strip()
                if got == want:
                    break
                log(f"chunk {i + 1}/{n}: hash mismatch, attempt {a}")
            except KubeError as e:
                log(f"chunk {i + 1}/{n}: {e}, attempt {a}")
                time.sleep(2 * a)
        else:
            raise KubeError(f"upload of {local}: chunk {i} failed {attempts} times")
        log(f"  {local}: chunk {i + 1}/{n}")
    got = sh(pod, f"cat '{part}.d'/* > '{part}' && rm -rf '{part}.d' && sha256sum '{part}' | cut -c1-64").strip()
    if got != whole:
        raise KubeError(f"upload of {local}: whole-file hash mismatch")
    sh(pod, f"mv -f '{part}' '{remote}'")
    log(f"uploaded {local} -> {remote} {whole[:16]}")
    return whole


def download(pod, remote, local, chunk=32000, log=print):
    """Fetch a file from the pod in base64 slices small enough for the proxy, then check its SHA-256."""
    info = sh(pod, f"stat -c %s '{remote}' && sha256sum '{remote}' | cut -c1-64").split()
    size, whole = int(info[0]), info[1]
    raw_chunk = chunk * 3 // 4
    out = bytearray()
    failures = 0
    while len(out) < size:
        off = len(out)
        try:
            text = sh(pod, f"tail -c +{off + 1} '{remote}' | head -c {raw_chunk} | base64 -w0; echo; "
                           f"tail -c +{off + 1} '{remote}' | head -c {raw_chunk} | sha256sum | cut -c1-64")
            payload, digest = text.strip().split("\n")
            piece = base64.b64decode(payload)
            ok = hashlib.sha256(piece).hexdigest() == digest.strip()
        except (ValueError, KubeError) as e:   # an answer cut short by the tunnel
            ok, e_text = False, str(e)
        else:
            e_text = "hash mismatch"
        if not ok:
            failures += 1
            if failures > 10:
                raise KubeError(f"download of {remote}: 10 broken slices in a row")
            log(f"slice at {off}: {e_text[:80]}, retrying")
            time.sleep(min(10, failures))
            continue
        out += piece
        failures = 0
        if size > 4 * raw_chunk and (len(out) // raw_chunk) % 20 == 0:
            log(f"  {remote}: {len(out) * 100 // size}%")
    if hashlib.sha256(out).hexdigest() != whole:
        raise KubeError(f"download of {remote}: whole-file hash mismatch")
    os.makedirs(os.path.dirname(os.path.abspath(local)), exist_ok=True)
    with open(local, "wb") as f:
        f.write(out)
    log(f"downloaded {remote} -> {local} {whole[:16]}")
    return whole
