"""Policy network for the squad soldier.

Input layout (Squad.Train.Obs, version 1): self (42) | allies 9×10 | contacts 10×21 | cover 8×12 | presence flags 9+10+8.
Entity sets get shared per-entity MLPs and masked mean+max pooling, so any number of allies/contacts works.
The heads that pick an entity ("go to cover k", "aim at contact k") are pointers: a query from the state scored against
each entity's embedding. Slots are re-sorted every decision, so their index must carry no meaning.
"""
import torch
import torch.nn as nn

SELF, ALLY, CONTACT, COVER = 42, 10, 21, 12
N_ALLY, N_CONTACT, N_COVER = 9, 10, 8
ALLIES_AT = SELF
CONTACTS_AT = ALLIES_AT + N_ALLY * ALLY
COVER_AT = CONTACTS_AT + N_CONTACT * CONTACT
FLAGS_AT = COVER_AT + N_COVER * COVER
OBS_SIZE = FLAGS_AT + N_ALLY + N_CONTACT + N_COVER
HEADS = [19, 3, 2, 11, 4, 2]          # move, mode, stance, aim, trigger, reload
MOVE_FIXED = 11                       # stop, 8 directions, toward first contact, objective; then 8 cover pointers
NEG = -1e8


def mlp(i, h, o):
    return nn.Sequential(nn.Linear(i, h), nn.ReLU(), nn.Linear(h, o), nn.ReLU())


def masked_pool(x, m):
    """x [B, N, D], m [B, N] in {0,1} → [B, 2D]: mean and max over present entities (zeros when none)."""
    mf = m.unsqueeze(-1)
    cnt = mf.sum(1).clamp(min=1)
    mean = (x * mf).sum(1) / cnt
    mx = x.masked_fill(mf == 0, -1e4).max(1).values
    mx = torch.where(m.sum(1, keepdim=True) > 0, mx, torch.zeros_like(mx))
    return torch.cat([mean, mx], -1)


class Policy(nn.Module):
    def __init__(self, hidden=256, recurrent=False):
        super().__init__()
        self.recurrent = recurrent
        self.self_net = mlp(SELF, 128, 128)
        self.ally_net = mlp(ALLY, 64, 64)
        self.contact_net = mlp(CONTACT, 128, 128)
        self.cover_net = mlp(COVER, 64, 64)
        self.trunk = mlp(128 + 2 * 64 + 2 * 128 + 2 * 64, hidden, hidden)
        if recurrent:
            self.gru = nn.GRUCell(hidden, hidden)
        self.move_fixed = nn.Linear(hidden, MOVE_FIXED)
        self.cover_q = nn.Linear(hidden, 64)
        self.mode = nn.Linear(hidden, HEADS[1])
        self.stance = nn.Linear(hidden, HEADS[2])
        self.aim_none = nn.Linear(hidden, 1)
        self.contact_q = nn.Linear(hidden, 128)
        self.trigger = nn.Linear(hidden, HEADS[4])
        self.reload = nn.Linear(hidden, HEADS[5])
        self.value = nn.Linear(hidden, 1)
        for m in self.modules():
            if isinstance(m, nn.Linear):
                nn.init.orthogonal_(m.weight, 2 ** 0.5)
                nn.init.zeros_(m.bias)
        for head in (self.move_fixed, self.mode, self.stance, self.aim_none, self.trigger, self.reload):
            nn.init.orthogonal_(head.weight, 0.01)
        nn.init.orthogonal_(self.value.weight, 1.0)

    def forward(self, obs, h=None):
        """obs [B, OBS_SIZE] → (list of logits per head, value [B], new hidden or None)."""
        B = obs.shape[0]
        s = obs[:, :SELF]
        al = obs[:, ALLIES_AT:CONTACTS_AT].reshape(B, N_ALLY, ALLY)
        ct = obs[:, CONTACTS_AT:COVER_AT].reshape(B, N_CONTACT, CONTACT)
        cv = obs[:, COVER_AT:FLAGS_AT].reshape(B, N_COVER, COVER)
        f = obs[:, FLAGS_AT:]
        m_al, m_ct, m_cv = f[:, :N_ALLY], f[:, N_ALLY:N_ALLY + N_CONTACT], f[:, N_ALLY + N_CONTACT:]

        es = self.self_net(s)
        ea, ec, ev = self.ally_net(al), self.contact_net(ct), self.cover_net(cv)
        x = torch.cat([es, masked_pool(ea, m_al), masked_pool(ec, m_ct), masked_pool(ev, m_cv)], -1)
        x = self.trunk(x)
        if self.recurrent:
            h = self.gru(x, h if h is not None else torch.zeros_like(x))
            x = h

        cover_logits = torch.einsum("bd,bnd->bn", self.cover_q(x), ev) / 8.0
        contact_logits = torch.einsum("bd,bnd->bn", self.contact_q(x), ec) / 11.3
        logits = [
            torch.cat([self.move_fixed(x), cover_logits], -1),
            self.mode(x),
            self.stance(x),
            torch.cat([self.aim_none(x), contact_logits], -1),
            self.trigger(x),
            self.reload(x),
        ]
        return logits, self.value(x).squeeze(-1), h


def split_masks(masks):
    """[B, 41] uint8 → list of bool tensors per head."""
    out, at = [], 0
    for n in HEADS:
        out.append(masks[:, at:at + n].bool())
        at += n
    return out


def masked_dists(logits, masks):
    return [torch.distributions.Categorical(logits=l.masked_fill(~m, NEG)) for l, m in zip(logits, split_masks(masks))]


def act(policy, obs, masks, h=None, greedy=False):
    """Sample (or argmax) actions. Returns actions [B, 6] long, log-prob [B], value [B], hidden."""
    logits, value, h = policy(obs, h)
    dists = masked_dists(logits, masks)
    if greedy:
        actions = torch.stack([d.probs.argmax(-1) for d in dists], -1)
    else:
        actions = torch.stack([d.sample() for d in dists], -1)
    logp = sum(d.log_prob(actions[:, i]) for i, d in enumerate(dists))
    return actions, logp, value, h


def evaluate(policy, obs, masks, actions, h=None):
    """Log-prob, entropy and value of given actions (for the PPO update)."""
    logits, value, _ = policy(obs, h)
    dists = masked_dists(logits, masks)
    logp = sum(d.log_prob(actions[:, i]) for i, d in enumerate(dists))
    ent = sum(d.entropy() for d in dists)
    return logp, ent, value
