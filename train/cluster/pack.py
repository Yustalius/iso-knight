"""Pack what a training pod needs into one reproducible tar.gz: training code, configs, scenarios, maps, the trial
checkpoints and the Linux squad.so (build it first: train/cluster/build_native.sh).

    python train/cluster/pack.py            # → train/cluster/out/bundle-<sha12>.tar.gz, prints the path and SHA-256
"""
import glob
import gzip
import hashlib
import io
import os
import sys
import tarfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PATTERNS = ["train/*.py", "train/squad/*.py", "train/tests/*.py", "train/configs/*.json", "train/cluster/*.sh",
            "train/cluster/eval_all.py", "train/native/squad.so", "train/checkpoints/trial-cpu/stage-*.pt",
            "sim/scenarios/*.json", "sim/maps/*.json"]


def files():
    out = set()
    for p in PATTERNS:
        out.update(os.path.relpath(f, ROOT).replace(os.sep, "/") for f in glob.glob(os.path.join(ROOT, p)))
    if "train/native/squad.so" not in out:
        sys.exit("train/native/squad.so is missing: run train/cluster/build_native.sh")
    return sorted(out)


def pack():
    buf = io.BytesIO()
    manifest = []
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0) as gz, tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for rel in files():
            with open(os.path.join(ROOT, rel), "rb") as f:
                data = f.read()
            if rel.endswith((".sh", ".py", ".json")):
                data = data.replace(b"\r\n", b"\n")
            manifest.append(f"{hashlib.sha256(data).hexdigest()}  {rel}")
            info = tarfile.TarInfo(rel)
            info.size, info.mtime, info.mode = len(data), 0, 0o755 if rel.endswith(".sh") else 0o644
            tar.addfile(info, io.BytesIO(data))
        data = ("\n".join(manifest) + "\n").encode()
        info = tarfile.TarInfo("MANIFEST.sha256")
        info.size, info.mtime, info.mode = len(data), 0, 0o644
        tar.addfile(info, io.BytesIO(data))
    blob = buf.getvalue()
    sha = hashlib.sha256(blob).hexdigest()
    out_dir = os.path.join(ROOT, "train", "cluster", "out")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, f"bundle-{sha[:12]}.tar.gz")
    with open(path, "wb") as f:
        f.write(blob)
    return path, sha, len(manifest)


if __name__ == "__main__":
    path, sha, n = pack()
    print(f"{path}\n{sha}\n{n} files, {os.path.getsize(path) / 1e6:.1f} MB")
