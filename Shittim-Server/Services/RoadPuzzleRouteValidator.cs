using Schale.FlatData;
using Schale.MX.GameLogic.DBModel;

namespace Shittim_Server.Services;

internal static partial class RoadPuzzleRouteValidator
{
    private sealed record Layout(
        (int X, int Y, int Z) Start,
        (int X, int Y, int Z, int Type, int Rotation)[] FixedRails,
        HashSet<(int X, int Y, int Z)> Buildable);

    // Client HexLocation.Directions, ordered by entrance/exit port index.
    private static readonly (int X, int Y, int Z)[] Directions =
        [(1, -1, 0), (1, 0, -1), (0, 1, -1), (-1, 1, 0), (-1, 0, 1), (0, -1, 1)];

    // Null means this authored layout is not yet available, so callers retain
    // their existing behavior for other maps rather than inventing fixed rails.
    internal static bool? IsReady(long eventId, long mapId, IReadOnlyCollection<RoadPuzzleRailTileData>? placed)
    {
        if (eventId != 10846 || !Layouts.TryGetValue(mapId, out var layout)) return null;
        var fixedRails = layout.FixedRails;
        var buildable = layout.Buildable;
        var start = layout.Start;
        var rails = fixedRails.ToDictionary(x => (x.X, x.Y, x.Z), x => Ports(x.Type, x.Rotation));
        foreach (var tile in placed ?? [])
        {
            var location = (tile.Location.x, tile.Location.y, tile.Location.z);
            if (!buildable.Contains(location) || tile.rotation is < 0 or > 5
                || tile.Type is not (RoadPuzzleRailTileType.Straight or RoadPuzzleRailTileType.CurveBig or RoadPuzzleRailTileType.CurveSmall)
                || !rails.TryAdd(location, Ports((int)tile.Type, tile.rotation))) return false;
        }

        var seen = new HashSet<(int X, int Y, int Z)>();
        var pending = new Stack<(int X, int Y, int Z)>();
        pending.Push(start);
        while (pending.TryPop(out var location))
        {
            if (!seen.Add(location)) continue;
            foreach (var port in rails[location])
            {
                var direction = Directions[port];
                var next = (location.X + direction.X, location.Y + direction.Y, location.Z + direction.Z);
                if (rails.TryGetValue(next, out var nextPorts) && nextPorts.Contains((port + 3) % 6))
                    pending.Push(next);
            }
        }
        return fixedRails.All(x => seen.Contains((x.X, x.Y, x.Z)));
    }

    private static int[] Ports(int type, int rotation) =>
        [(4 - rotation + 6) % 6, ((type == 1 ? 1 : type == 2 ? 0 : 5) - rotation + 6) % 6];
}
