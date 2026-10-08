using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MoogleMap.Services;

/// <summary>
/// How long each part of the plugin takes per frame: the average and the worst over the last
/// couple of seconds, for the Diagnostics page. A part that takes long enough to show as a hitch
/// is logged, so a stutter can be put down to what caused it.
/// </summary>
public sealed class FrameTimes
{
    /// <summary>Seconds each average and worst cover.</summary>
    private const double Window = 2.0;
    /// <summary>Longer than this in one go is a hitch worth logging.</summary>
    private const double SlowMs = 8.0;

    private sealed class Entry
    {
        public double Sum;
        public int Count;
        public double Worst;
        public double ShownAverage;
        public double ShownWorst;
    }

    private readonly Dictionary<string, Entry> entries = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double windowStart;
    private double loggedAt = -1.0;

    /// <summary>When a part started, and how many garbage collections there had been by then.</summary>
    public readonly record struct Mark(long At, int Gen0, int Gen1, int Gen2);

    /// <summary>A start for <see cref="Add"/>.</summary>
    public static Mark Start()
        => new(Stopwatch.GetTimestamp(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>Counts the time since <paramref name="start"/> against <paramref name="part"/>.</summary>
    public void Add(string part, Mark start)
    {
        var ms = Stopwatch.GetElapsedTime(start.At).TotalMilliseconds;
        if (!entries.TryGetValue(part, out var entry))
            entries[part] = entry = new Entry();
        entry.Sum += ms;
        entry.Count++;
        if (ms > entry.Worst) entry.Worst = ms;

        var now = clock.Elapsed.TotalSeconds;
        if (ms >= SlowMs && now - loggedAt >= 1.0)
        {
            loggedAt = now;
            // A collection in the middle of a part stalls it however little the part itself does.
            Plugin.Log.Debug("Slow frame: {Part} took {Ms:0.0} ms (collections during it: gen0 {Gen0}, gen1 {Gen1}, gen2 {Gen2})",
                part, ms, GC.CollectionCount(0) - start.Gen0, GC.CollectionCount(1) - start.Gen1, GC.CollectionCount(2) - start.Gen2);
        }

        if (now - windowStart < Window) return;
        windowStart = now;
        foreach (var e in entries.Values)
        {
            e.ShownAverage = e.Count > 0 ? e.Sum / e.Count : 0.0;
            e.ShownWorst = e.Worst;
            e.Sum = 0.0;
            e.Count = 0;
            e.Worst = 0.0;
        }
    }

    /// <summary>Each part's average and worst, in milliseconds, slowest first.</summary>
    public string Summary => entries.Count == 0
        ? "-"
        : string.Join(", ", entries.OrderByDescending(e => e.Value.ShownWorst)
            .Select(e => $"{e.Key} {e.Value.ShownAverage:0.00} / {e.Value.ShownWorst:0.0}"));
}
