"""ctypes wrapper of libsquad (sim/Squad.Train, Native AOT).

Many matches step together inside the library on all CPU cores; the learner's soldiers take actions from here,
everyone else is a rule bot run inside the library. Buffers are numpy arrays owned by this object and are
overwritten by every call: copy what you keep.
"""
import ctypes
import json
import os

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_LIB = os.path.join(HERE, "..", "native", "squad.so")

OBS_VERSION = 1   # must match Squad.Train.Obs.Version / Act.Version (squad_version() = obs * 100 + act)
ACT_VERSION = 1

_lib = None


def load_library(path=None):
    global _lib
    if _lib is not None:
        return _lib
    path = path or os.environ.get("SQUAD_LIB") or DEFAULT_LIB
    if not os.path.exists(path):
        raise FileNotFoundError(f"{path} not found: build it with train/build_native.sh (or set SQUAD_LIB)")
    lib = ctypes.CDLL(os.path.abspath(path))
    P = ctypes.c_void_p
    lib.squad_version.restype = ctypes.c_int
    lib.squad_error.argtypes = [ctypes.c_char_p, ctypes.c_int]
    lib.squad_create.argtypes = [ctypes.c_char_p, ctypes.c_char_p, P]
    lib.squad_reset.argtypes = [ctypes.c_int, P, P, P]
    lib.squad_step.argtypes = [ctypes.c_int, P, P, P, P, P, P, P]
    lib.squad_set_shaping.argtypes = [ctypes.c_int, ctypes.c_double]
    lib.squad_record.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_char_p]
    lib.squad_destroy.argtypes = [ctypes.c_int]
    for f in ("squad_create", "squad_reset", "squad_step", "squad_set_shaping", "squad_record", "squad_destroy"):
        getattr(lib, f).restype = ctypes.c_int
    ver = lib.squad_version()
    if ver != OBS_VERSION * 100 + ACT_VERSION:
        raise RuntimeError(f"libsquad has observation/action version {ver}, this code expects {OBS_VERSION * 100 + ACT_VERSION}")
    _lib = lib
    return lib


def _error(lib):
    buf = ctypes.create_string_buffer(8192)
    lib.squad_error(buf, len(buf))
    return buf.value.decode("utf-8", "replace")


def _ptr(a):
    return a.ctypes.data_as(ctypes.c_void_p)


class SquadEnv:
    """config: dict with scenarios (paths relative to base_dir), envs, seed, learner ("team0" | "all"), threads, shaping, mapPool."""

    INFO_FIELDS = ("ended", "outcome", "seconds", "kills", "deaths", "damage_dealt", "damage_taken", "scenario")

    def __init__(self, config, base_dir=".", lib_path=None):
        self.lib = load_library(lib_path)
        sizes = np.zeros(16, dtype=np.int32)
        h = self.lib.squad_create(json.dumps(config).encode(), os.path.abspath(base_dir).encode(), _ptr(sizes))
        if h < 0:
            raise RuntimeError("squad_create failed:\n" + _error(self.lib))
        self.handle = h
        self.slots, self.obs_size, self.mask_size, n_heads = (int(x) for x in sizes[:4])
        self.heads = [int(x) for x in sizes[4:4 + n_heads]]
        self.info_size, self.num_envs, self.learners = int(sizes[10]), int(sizes[11]), int(sizes[12])
        self.obs = np.zeros((self.slots, self.obs_size), dtype=np.float32)
        self.masks = np.zeros((self.slots, self.mask_size), dtype=np.uint8)
        self.rewards = np.zeros(self.slots, dtype=np.float32)
        self.dones = np.zeros(self.slots, dtype=np.uint8)
        self.alive = np.zeros(self.slots, dtype=np.uint8)
        self.info = np.zeros((self.num_envs, self.info_size), dtype=np.float32)
        self.actions = np.zeros((self.slots, n_heads), dtype=np.int32)

    def _check(self, rc, what):
        if rc != 0:
            raise RuntimeError(f"{what} failed:\n" + _error(self.lib))

    def reset(self):
        self._check(self.lib.squad_reset(self.handle, _ptr(self.obs), _ptr(self.masks), _ptr(self.alive)), "squad_reset")
        return self.obs, self.masks, self.alive

    def step(self, actions):
        np.copyto(self.actions, actions, casting="unsafe")
        self._check(self.lib.squad_step(self.handle, _ptr(self.actions), _ptr(self.obs), _ptr(self.masks), _ptr(self.rewards),
                                        _ptr(self.dones), _ptr(self.alive), _ptr(self.info)), "squad_step")
        return self.obs, self.masks, self.rewards, self.dones, self.alive, self.info

    def set_shaping(self, value):
        self._check(self.lib.squad_set_shaping(self.handle, float(value)), "squad_set_shaping")

    def record(self, env_index, path):
        """Write env_index's next whole episode to a replay (view: Squad.Tools render --replay PATH)."""
        self._check(self.lib.squad_record(self.handle, int(env_index), os.path.abspath(path).encode()), "squad_record")

    def close(self):
        if getattr(self, "handle", 0) > 0:
            self.lib.squad_destroy(self.handle)
            self.handle = 0

    def __del__(self):
        try:
            self.close()
        except Exception:
            pass


def random_actions(masks, heads, rng):
    """Uniformly random valid options for every head (for smoke tests)."""
    out = np.zeros((masks.shape[0], len(heads)), dtype=np.int32)
    at = 0
    for h, n in enumerate(heads):
        m = masks[:, at:at + n].astype(np.float64)
        p = m / m.sum(axis=1, keepdims=True)
        c = p.cumsum(axis=1)
        u = rng.random((masks.shape[0], 1))
        out[:, h] = (u > c).sum(axis=1).clip(0, n - 1)
        at += n
    return out
