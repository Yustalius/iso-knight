using Squad.Bots;
using Squad.Sim;
using Squad.Train;
using Xunit;

namespace Squad.Tests;

public class TrainTests
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    static VecEnv Env(string scenario, int envs = 4, ulong seed = 1, string learner = "team0") =>
        new(EnvConfig.Parse($$"""{ "scenarios": [ "scenarios/{{scenario}}.json" ], "envs": {{envs}}, "seed": {{seed}}, "learner": "{{learner}}", "threads": 2 }""", Root));

    sealed class Bufs
    {
        public float[] Obs, Rew, Info; public byte[] Mask, Done, Alive; public int[] Act;
        public Bufs(VecEnv e)
        {
            Obs = new float[e.Slots * Squad.Train.Obs.Size]; Mask = new byte[e.Slots * Squad.Train.Act.MaskSize];
            Rew = new float[e.Slots]; Done = new byte[e.Slots]; Alive = new byte[e.Slots];
            Info = new float[e.Envs * VecEnv.InfoSize]; Act = new int[e.Slots * Squad.Train.Act.NumHeads];
        }
    }

    /// <summary>Random valid actions drawn from the masks.</summary>
    static void RandomActions(ref Rng r, Bufs b, int slots)
    {
        for (int s = 0; s < slots; s++)
        {
            int at = 0;
            for (int h = 0; h < Squad.Train.Act.NumHeads; h++)
            {
                int n = Squad.Train.Act.Heads[h], valid = 0;
                for (int k = 0; k < n; k++) valid += b.Mask[s * Squad.Train.Act.MaskSize + at + k];
                int pick = r.Int(Math.Max(1, valid));
                int choice = 0;
                for (int k = 0; k < n; k++) if (b.Mask[s * Squad.Train.Act.MaskSize + at + k] == 1 && pick-- == 0) { choice = k; break; }
                b.Act[s * Squad.Train.Act.NumHeads + h] = choice;
                at += n;
            }
        }
    }

    [Fact]
    public void LayoutIsConsistent()
    {
        Assert.Equal(Squad.Train.Act.MaskSize, Squad.Train.Act.Heads.Sum());
        Assert.Equal(Squad.Train.Obs.Size, Squad.Train.Obs.FlagsAt + 27);
        var env = Env("train-duel");
        var b = new Bufs(env);
        env.Reset(b.Obs, b.Mask, b.Alive);
        Assert.Equal(1, env.MaxLearners);
        Assert.All(b.Alive, a => Assert.Equal(1, a));
        Assert.All(b.Obs, x => Assert.True(float.IsFinite(x) && Math.Abs(x) <= 3.01f));
        // every head has at least one valid option
        for (int s = 0; s < env.Slots; s++)
        {
            int at = 0;
            foreach (int n in Squad.Train.Act.Heads) { Assert.Contains((byte)1, b.Mask.AsSpan(s * Squad.Train.Act.MaskSize + at, n).ToArray()); at += n; }
        }
    }

    [Fact]
    public void RandomPlayRunsEpisodesToTheEnd()
    {
        var env = Env("train-aim", envs: 8);
        var b = new Bufs(env);
        env.Reset(b.Obs, b.Mask, b.Alive);
        var r = new Rng(3);
        int ended = 0;
        for (int t = 0; t < 1200; t++)
        {
            RandomActions(ref r, b, env.Slots);
            env.Step(b.Act, b.Obs, b.Mask, b.Rew, b.Done, b.Alive, b.Info);
            Assert.All(b.Obs, x => Assert.True(float.IsFinite(x)));
            Assert.All(b.Rew, x => Assert.True(float.IsFinite(x) && Math.Abs(x) < 5));
            for (int e = 0; e < env.Envs; e++)
                if (b.Info[e * VecEnv.InfoSize] == 1)
                {
                    ended++;
                    Assert.Contains(b.Info[e * VecEnv.InfoSize + 1], new[] { -1f, 0f, 1f });
                    Assert.Equal(1, b.Done[e]);
                }
        }
        Assert.True(ended >= 8, $"ended {ended}");
    }

    [Fact]
    public void SameSeedAndActionsGiveTheSameObservations()
    {
        float[] Run()
        {
            var env = Env("train-duel-gen", envs: 6, seed: 5);
            var b = new Bufs(env);
            env.Reset(b.Obs, b.Mask, b.Alive);
            var r = new Rng(9);
            var sum = new float[b.Obs.Length];
            for (int t = 0; t < 400; t++)
            {
                RandomActions(ref r, b, env.Slots);
                env.Step(b.Act, b.Obs, b.Mask, b.Rew, b.Done, b.Alive, b.Info);
                for (int i = 0; i < sum.Length; i++) sum[i] += b.Obs[i] * (t % 7 + 1) + b.Rew[i / Squad.Train.Obs.Size];
            }
            return sum;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ObservationIsInTheSoldiersFrame()
    {
        // enemy straight ahead along the barrel, then off to the right
        foreach (var (enemy, right) in new[] { (new Vec2(10, 13), false), (new Vec2(16, 4), true) })
        {
            var m = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
            m.Place(0, new Vec2(10, 4), 0);
            m.Place(1, enemy, DMath.Pi);
            var acts = new AgentAction[2];
            acts[0] = TestUtil.AimAt(new Vec2(enemy.X, enemy.Y), 1.2);
            TestUtil.Run(m, acts, 1.5);
            var obs = new float[Squad.Train.Obs.Size]; var mask = new byte[Squad.Train.Act.MaskSize];
            Codec.Encode(m.View(0), default, 120, obs, mask);
            Assert.Equal(1, obs[Squad.Train.Obs.FlagsAt + Squad.Train.Obs.NAllies]);   // one contact
            float lx = obs[Squad.Train.Obs.ContactsAt], ly = obs[Squad.Train.Obs.ContactsAt + 1];
            // the barrel turned toward the enemy in both cases: he is ahead, never to the side
            Assert.True(Math.Abs(lx) < 0.05 && ly > 0.2, $"{lx} {ly} right={right}");
        }
        // not aiming: an enemy to the right of the facing shows up with x > 0
        var m2 = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
        m2.Place(0, new Vec2(10, 4), 0);
        m2.Place(1, new Vec2(14, 8), DMath.Pi);
        TestUtil.Run(m2, new AgentAction[2], 1.5);
        var o2 = new float[Squad.Train.Obs.Size]; var k2 = new byte[Squad.Train.Act.MaskSize];
        Codec.Encode(m2.View(0), default, 120, o2, k2);
        Assert.True(o2[Squad.Train.Obs.ContactsAt] > 0.1 && o2[Squad.Train.Obs.ContactsAt + 1] > 0.1);
    }

    [Fact]
    public void DecodeUsesTheSlotsOfTheSameView()
    {
        var m = Match.Create(TestUtil.Config(TestUtil.Yard(ObstacleKind.LowWall), a: 1, b: 2));
        m.Place(0, new Vec2(10, 4), 0);
        m.Place(1, new Vec2(8, 13), DMath.Pi);
        m.Place(2, new Vec2(12, 15), DMath.Pi);
        TestUtil.Run(m, new AgentAction[3], 1.5);
        var v = m.View(0);
        Assert.Equal(2, v.Contacts.Length);
        for (int k = 0; k < 2; k++)
        {
            var a = Codec.Decode(v, default, new[] { Squad.Train.Act.MoveCover0, 1, 1, 1 + k, 2, 0 });
            Assert.Equal(AimKind.Contact, a.Aim);
            Assert.Equal(v.Contacts[k].Id, a.AimAt);
            Assert.Equal(MoveKind.To, a.Move);
            Assert.Equal(v.Cover[0].Pos, a.Target);
            Assert.Equal(Stance.Crouch, a.Stance);
            Assert.Equal(Trigger.Burst, a.Trigger);
        }
        // the 8 directions are relative to the barrel, clockwise from straight ahead
        var right = Codec.Decode(v, default, new[] { Squad.Train.Act.MoveDir0 + 2, 0, 0, 0, 0, 0 });
        Assert.Equal(MoveKind.Dir, right.Move);
        Assert.True(right.Target.Dot(Vec2.FromYaw(v.Self.AimYaw + DMath.HalfPi)) > 0.99);
    }

    /// <summary>An enemy behind a wall does not change a single byte of the network input.</summary>
    [Fact]
    public void HiddenEnemyDoesNotLeakIntoTheInput()
    {
        byte[]? first = null;
        foreach (double x in new[] { 3.0, 9.0, 17.0 })
        {
            var m = Match.Create(TestUtil.Config(TestUtil.Yard(ObstacleKind.HighWall, -1, 21)));
            m.Place(0, new Vec2(10, 4), 0);
            m.Place(1, new Vec2(x, 15), DMath.Pi);
            TestUtil.Run(m, new AgentAction[2], 3);
            var obs = new float[Squad.Train.Obs.Size]; var mask = new byte[Squad.Train.Act.MaskSize];
            Codec.Encode(m.View(0), default, 120, obs, mask);
            var bytes = new byte[obs.Length * 4 + mask.Length];
            Buffer.BlockCopy(obs, 0, bytes, 0, obs.Length * 4);
            Buffer.BlockCopy(mask, 0, bytes, obs.Length * 4, mask.Length);
            if (first == null) first = bytes; else Assert.Equal(first, bytes);
        }
    }

    [Fact]
    public void LearnerDeathEndsItsEpisodeAndMasksTheSlot()
    {
        var env = Env("train-duel", envs: 4, seed: 2);
        var b = new Bufs(env);
        env.Reset(b.Obs, b.Mask, b.Alive);
        // stand still and never shoot: the bot opponent wins sooner or later
        int losses = 0;
        for (int t = 0; t < 900 && losses < 2; t++)
        {
            Array.Clear(b.Act);
            env.Step(b.Act, b.Obs, b.Mask, b.Rew, b.Done, b.Alive, b.Info);
            for (int e = 0; e < env.Envs; e++)
                if (b.Info[e * VecEnv.InfoSize] == 1 && b.Info[e * VecEnv.InfoSize + 1] == -1) { losses++; Assert.True(b.Rew[e] < -0.9f); }
        }
        Assert.True(losses >= 2);
    }

    [Fact]
    public void SelfPlayControlsEverySoldier()
    {
        var env = Env("team-4v4-mixed", envs: 2, learner: "all");
        Assert.Equal(8, env.MaxLearners);
        var b = new Bufs(env);
        env.Reset(b.Obs, b.Mask, b.Alive);
        Assert.Equal(16, b.Alive.Count(a => a == 1));
    }

    [Fact]
    public void RecordedTrainingEpisodeReplays()
    {
        var env = Env("train-aim", envs: 2, seed: 4);
        var b = new Bufs(env);
        string path = Path.Combine(Path.GetTempPath(), $"squad-train-{Environment.ProcessId}.rpl");
        env.RecordNext(0, path);
        env.Reset(b.Obs, b.Mask, b.Alive);
        var r = new Rng(1);
        for (int t = 0; t < 600 && !File.Exists(path); t++) { RandomActions(ref r, b, env.Slots); env.Step(b.Act, b.Obs, b.Mask, b.Rew, b.Done, b.Alive, b.Info); }
        Assert.True(File.Exists(path));
        var rp = Replay.Load(path);
        File.Delete(path);
        Assert.Equal(-1, rp.Verify(Match.Create(SimJson.ReadConfig(rp.Header))));
    }
}
