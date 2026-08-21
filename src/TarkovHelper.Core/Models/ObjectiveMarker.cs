namespace TarkovHelper.Core.Models;

public class ObjectiveMarker
{
    public required string QuestName { get; init; }
    public required string ObjectiveDescription { get; init; }
    public required float X { get; init; }
    public required float Z { get; init; }
}

public static class ObjectiveMarkerFactory
{
    // Zones within this world-space distance of each other are treated as
    // "the same spot" and collapsed into a single marker, rather than
    // showing overlapping/redundant markers for what's practically one
    // location (e.g. a findQuestItem objective's multiple candidate spawn
    // positions clustered around the same room). Chosen as a few times a
    // typical room/building footprint (~7-15 world units per the real
    // "size" fields seen in objective zone data) without merging genuinely
    // distinct spawn areas on opposite sides of a map.
    private const float ClusterDistance = 40f;

    // Active, incomplete quests only - a completed quest's objectives are
    // no longer relevant to show on the map.
    public static List<ObjectiveMarker> BuildForMap(IEnumerable<QuestTask> tasks, string mapNormalizedName)
    {
        var markers = new List<ObjectiveMarker>();

        foreach (var task in tasks)
        {
            if (!task.IsActive || task.IsComplete)
            {
                continue;
            }

            foreach (var objective in task.Objectives)
            {
                var zonesOnMap = objective.Zones
                    .Where(z => z.MapNormalizedName == mapNormalizedName)
                    .ToList();

                if (zonesOnMap.Count == 0)
                {
                    continue;
                }

                foreach (var cluster in ClusterZones(zonesOnMap))
                {
                    var (centerX, centerZ) = ClusterCenter(cluster);
                    markers.Add(new ObjectiveMarker
                    {
                        QuestName = task.Name,
                        ObjectiveDescription = objective.Description,
                        X = centerX,
                        Z = centerZ,
                    });
                }
            }
        }

        return markers;
    }

    // Simple greedy clustering: each zone joins the first existing cluster
    // within ClusterDistance of ANY of its members, else starts a new one.
    // Not globally optimal, but objective zone counts per task are small
    // (single digits) so this is more than fast enough, and greedy behavior
    // is easy to reason about for this data shape (a handful of tight
    // groups, not a dense scatter needing real spatial clustering).
    internal static List<List<ObjectiveZone>> ClusterZones(List<ObjectiveZone> zones)
    {
        var clusters = new List<List<ObjectiveZone>>();

        foreach (var zone in zones)
        {
            var joined = false;
            foreach (var cluster in clusters)
            {
                if (cluster.Any(member => Distance(member, zone) <= ClusterDistance))
                {
                    cluster.Add(zone);
                    joined = true;
                    break;
                }
            }

            if (!joined)
            {
                clusters.Add(new List<ObjectiveZone> { zone });
            }
        }

        return clusters;
    }

    private static float Distance(ObjectiveZone a, ObjectiveZone b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static (float X, float Z) ClusterCenter(List<ObjectiveZone> cluster) =>
        (cluster.Average(z => z.X), cluster.Average(z => z.Z));
}
