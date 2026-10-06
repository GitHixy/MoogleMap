using System.Collections.Generic;
using System.Numerics;

namespace MoogleMap.Map;

/// <summary>A point of the trail: where, when, and how far the player had walked in total by then.</summary>
public readonly record struct TrailPoint(Vector3 Position, double Time, float Walked);

/// <summary>Where the player has walked lately, as timed points, for a trail that fades behind them.</summary>
public sealed class Trail
{
    /// <summary>A new point is laid once the player is this far from the last one, in yalms.</summary>
    private const float Spacing = 1f;
    /// <summary>A jump bigger than this between points is a teleport; the trail breaks there.</summary>
    public const float Break = 30f;

    private readonly List<TrailPoint> points = [];
    private float walked;

    public IReadOnlyList<TrailPoint> Points => points;

    public void Update(Vector3 player, double now, double keep)
    {
        if (points.Count == 0)
        {
            points.Add(new TrailPoint(player, now, walked));
        }
        else
        {
            var step = Vector3.Distance(points[^1].Position, player);
            if (step >= Spacing)
            {
                // Distance only grows, so dashes keyed to it stay put on the ground as the trail ages.
                if (step <= Break) walked += step;
                points.Add(new TrailPoint(player, now, walked));
            }
        }

        // Oldest first, so expired points are always at the front.
        var expired = 0;
        while (expired < points.Count && now - points[expired].Time > keep)
            expired++;
        if (expired > 0)
            points.RemoveRange(0, expired);
    }

    public void Clear() => points.Clear();
}
