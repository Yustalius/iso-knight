using Squad.Bots;
using Squad.Sim;

/// <summary>Runs the match on a worker thread a few ticks ahead of the picture. The bots' thinking costs up to tens of
/// milliseconds in some ticks; done on the render thread that showed as dropped frames. The viewer only reads the
/// snapshots (truth, events, shot counters) this produces; the match itself is touched by the worker alone, except for
/// immutable parts (world, balance, team sizes). The result is the same match: the simulation does not depend on timing.</summary>
sealed class SimFeed : IDisposable
{
    public sealed class Frame
    {
        public SoldierTruth[] Truth = Array.Empty<SoldierTruth>();
        public GameEvent[] Events = Array.Empty<GameEvent>();
        public int[] Shots = Array.Empty<int>();
        public double Time;
        public bool Over;
        public MatchResult Result;
        public readonly int[] Alive = new int[2];
    }

    const int Ahead = 8;   // ticks computed in advance (0.27 s)
    readonly BotRunner _runner;
    readonly Match _m;
    readonly Queue<Frame> _q = new();
    readonly Thread _thread;
    volatile bool _stop;
    public Exception? Error { get; private set; }

    public SimFeed(BotRunner runner)
    {
        _runner = runner; _m = runner.Match;
        First = Capture(false);
        _thread = new Thread(Run) { IsBackground = true, Name = "SimFeed" };
        _thread.Start();
    }

    /// <summary>The state before the first tick.</summary>
    public readonly Frame First;

    Frame Capture(bool events)
    {
        var f = new Frame { Truth = new SoldierTruth[_m.Count], Shots = new int[_m.Count], Time = _m.Time, Over = _m.Over, Result = _m.Result };
        for (int i = 0; i < _m.Count; i++) { f.Truth[i] = _m.Truth(i); f.Shots[i] = _m.Stats(i).Shots; }
        if (events) f.Events = _m.Events.ToArray();
        f.Alive[0] = _m.AliveCount(0); f.Alive[1] = _m.AliveCount(1);
        return f;
    }

    void Run()
    {
        try
        {
            while (!_stop && !_m.Over)
            {
                lock (_q) { while (_q.Count >= Ahead && !_stop) Monitor.Wait(_q); }
                if (_stop) break;
                _runner.Step();
                var f = Capture(true);
                lock (_q) { _q.Enqueue(f); Monitor.PulseAll(_q); }
            }
        }
        catch (Exception e) { Error = e; }
    }

    /// <summary>The next tick, or null when the worker has not finished it yet (wait: block until it has).</summary>
    public Frame? Next(bool wait)
    {
        lock (_q)
        {
            while (_q.Count == 0)
            {
                if (!wait || !_thread.IsAlive) return null;
                Monitor.Wait(_q, 50);
            }
            var f = _q.Dequeue();
            Monitor.PulseAll(_q);
            return f;
        }
    }

    public void Dispose()
    {
        _stop = true;
        lock (_q) Monitor.PulseAll(_q);
        _thread.Join();
    }
}
