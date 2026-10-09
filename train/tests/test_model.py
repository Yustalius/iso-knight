"""Network checks that need torch: shapes, masks, and that the pointer heads follow the entities, not the slot numbers.

    python train/tests/test_model.py
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import torch  # noqa: E402

from squad import model as M  # noqa: E402


def main():
    torch.manual_seed(0)
    pol = M.Policy()
    B = 4
    obs = torch.randn(B, M.OBS_SIZE)
    obs[:, M.FLAGS_AT:] = 0
    obs[:, M.FLAGS_AT + M.N_ALLY:M.FLAGS_AT + M.N_ALLY + 3] = 1          # 3 contacts
    obs[:, M.FLAGS_AT + M.N_ALLY + M.N_CONTACT:M.FLAGS_AT + M.N_ALLY + M.N_CONTACT + 5] = 1   # 5 cover points
    masks = torch.ones(B, sum(M.HEADS), dtype=torch.uint8)
    logits, value, _ = pol(obs)
    assert [l.shape[1] for l in logits] == M.HEADS and value.shape == (B,)

    # swap contacts 0 and 2 and cover 1 and 4: value unchanged, pointer logits swapped
    sw = obs.clone()
    c0, c2 = (slice(M.CONTACTS_AT + k * M.CONTACT, M.CONTACTS_AT + (k + 1) * M.CONTACT) for k in (0, 2))
    sw[:, c0], sw[:, c2] = obs[:, c2], obs[:, c0]
    v1, v4 = (slice(M.COVER_AT + k * M.COVER, M.COVER_AT + (k + 1) * M.COVER) for k in (1, 4))
    sw[:, v1], sw[:, v4] = obs[:, v4], obs[:, v1]
    l2, value2, _ = pol(sw)
    assert torch.allclose(value, value2, atol=1e-5)
    aim, aim2 = logits[3], l2[3]
    assert torch.allclose(aim[:, 1], aim2[:, 3], atol=1e-5) and torch.allclose(aim[:, 3], aim2[:, 1], atol=1e-5)
    mv, mv2 = logits[0], l2[0]
    assert torch.allclose(mv[:, M.MOVE_FIXED + 1], mv2[:, M.MOVE_FIXED + 4], atol=1e-5)

    # masked options are never sampled
    masks[:, 19 + 3 + 2 + 4:19 + 3 + 2 + 11] = 0      # only "lowered" + 3 contacts
    for _ in range(50):
        a, lp, v, _ = M.act(pol, obs, masks)
        assert (a[:, 3] <= 3).all() and torch.isfinite(lp).all()
    logp, ent, v = M.evaluate(pol, obs, masks, a)
    assert torch.isfinite(ent).all()
    # recurrent variant runs
    rp = M.Policy(recurrent=True)
    a, lp, v, h = M.act(rp, obs, masks, None)
    assert h.shape == (B, 256)
    recurrent_update_matches_step_by_step(rp, obs, masks)
    print("model OK:", sum(p.numel() for p in pol.parameters()), "parameters")


def recurrent_update_matches_step_by_step(rp, obs, masks):
    """The PPO update encodes all time steps at once and steps only the GRU cell: same numbers as the whole network
    run step by step, with the hidden state cut at deaths and episode ends."""
    from squad.ppo import Trainer
    T, n = 7, obs.shape[0]
    g = torch.Generator().manual_seed(5)
    O = obs[torch.randint(0, n, (T * n,), generator=g)].reshape(T, n, -1)
    Mk = masks.unsqueeze(0).expand(T, n, -1).contiguous()
    A = torch.stack([M.act(rp, O[t], Mk[t])[0] for t in range(T)])
    DN = (torch.rand(T, n, generator=g) < 0.2).float()
    AL = (torch.rand(T, n, generator=g) < 0.9).float()
    h0 = torch.randn(n, 256, generator=g) * 0.1
    ref_lp, ref_ent, ref_v, h = [], [], [], h0
    for t in range(T):
        logits, v, h = rp(O[t], h)
        d = M.masked_dists(logits, Mk[t])
        ref_lp.append(sum(x.log_prob(A[t][:, i]) for i, x in enumerate(d)))
        ref_ent.append(sum(x.entropy() for x in d))
        ref_v.append(v)
        h = h * ((1 - DN[t]) * AL[t]).unsqueeze(-1)
    got = {}
    fake = Trainer.__new__(Trainer)
    fake.policy = rp
    fake.apply_loss = lambda logp, ent, v, *rest: got.update(logp=logp, ent=ent, v=v)
    Trainer.recurrent_loss(fake, O, Mk, A, torch.zeros(T, n), torch.zeros(T, n), torch.zeros(T, n), AL, DN, h0, 0.0, None)
    for k, ref in (("logp", ref_lp), ("ent", ref_ent), ("v", ref_v)):
        assert torch.allclose(got[k], torch.stack(ref).reshape(-1), atol=1e-5), k


if __name__ == "__main__":
    main()
