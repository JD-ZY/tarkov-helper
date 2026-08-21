using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

public class ObjectiveMarkerFactoryTests
{
    private static QuestTask MakeTask(string name, bool isActive, bool isComplete, params TaskObjective[] objectives) =>
        new()
        {
            Id = name,
            Name = name,
            IsActive = isActive,
            IsComplete = isComplete,
            Objectives = objectives.ToList(),
        };

    private static TaskObjective MakeObjective(string description, params (string map, float x, float z)[] zones) =>
        new()
        {
            Description = description,
            Zones = zones.Select(z => new ObjectiveZone { MapNormalizedName = z.map, X = z.x, Z = z.z }).ToList(),
        };

    [Fact]
    public void IncludesActiveIncompleteTaskWithZoneOnRequestedMap()
    {
        var task = MakeTask("Debut", isActive: true, isComplete: false,
            MakeObjective("Extract", ("customs", 100f, 200f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        var marker = Assert.Single(markers);
        Assert.Equal("Debut", marker.QuestName);
        Assert.Equal(100f, marker.X);
        Assert.Equal(200f, marker.Z);
    }

    [Fact]
    public void ExcludesInactiveTask()
    {
        var task = MakeTask("Debut", isActive: false, isComplete: false,
            MakeObjective("Extract", ("customs", 100f, 200f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        Assert.Empty(markers);
    }

    [Fact]
    public void ExcludesCompletedTask()
    {
        var task = MakeTask("Debut", isActive: true, isComplete: true,
            MakeObjective("Extract", ("customs", 100f, 200f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        Assert.Empty(markers);
    }

    [Fact]
    public void ExcludesZonesOnDifferentMap()
    {
        var task = MakeTask("Debut", isActive: true, isComplete: false,
            MakeObjective("Extract", ("woods", 100f, 200f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        Assert.Empty(markers);
    }

    [Fact]
    public void ObjectiveWithNoZones_ProducesNoMarkers()
    {
        var task = MakeTask("Debut", isActive: true, isComplete: false,
            MakeObjective("Hand over item to trader"));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        Assert.Empty(markers);
    }

    [Fact]
    public void MultipleZonesOnSameMap_ProducesOneMarkerPerZone()
    {
        var task = MakeTask("Debut", isActive: true, isComplete: false,
            MakeObjective("Visit either area", ("customs", 100f, 200f), ("customs", 300f, 400f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "customs");

        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public void MultipleActiveTasks_AllContributeMarkers()
    {
        var task1 = MakeTask("Debut", isActive: true, isComplete: false,
            MakeObjective("Extract", ("customs", 100f, 200f)));
        var task2 = MakeTask("Shortage", isActive: true, isComplete: false,
            MakeObjective("Find item", ("customs", 300f, 400f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task1, task2 }, "customs");

        Assert.Equal(2, markers.Count);
        Assert.Contains(markers, m => m.QuestName == "Debut");
        Assert.Contains(markers, m => m.QuestName == "Shortage");
    }

    [Fact]
    public void ClusteredNearbyZones_CollapseIntoSingleCenteredMarker()
    {
        var task = MakeTask("Find quest item", isActive: true, isComplete: false,
            MakeObjective("Find the item", ("groundzero", 10f, 10f), ("groundzero", 15f, 15f), ("groundzero", 5f, 5f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "groundzero");

        var marker = Assert.Single(markers);
        Assert.Equal(10f, marker.X, 3);
        Assert.Equal(10f, marker.Z, 3);
    }

    [Fact]
    public void DistantZones_ProduceSeparateMarkers()
    {
        var task = MakeTask("Find quest item", isActive: true, isComplete: false,
            MakeObjective("Find the item", ("groundzero", 0f, 0f), ("groundzero", 500f, 500f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "groundzero");

        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public void ShadyContractor_FindQuestItemClustersTogether_VisitObjectiveStaysSeparate()
    {
        // Real tarkov.dev data for the "Shady Contractor" quest on Ground
        // Zero: a findQuestItem objective with 3 possibleLocations positions
        // clustered together in the same building, plus an unrelated visit
        // objective's single position elsewhere on the map.
        var task = MakeTask("Shady Contractor", isActive: true, isComplete: false,
            MakeObjective("Find the contractor's documents",
                ("ground-zero", 87.7f, 225.3f), ("ground-zero", 89.1f, 224.6f), ("ground-zero", 88.2f, 226.1f)),
            MakeObjective("Visit the location marked on the map",
                ("ground-zero", 55.26f, 252.75f)));

        var markers = ObjectiveMarkerFactory.BuildForMap(new[] { task }, "ground-zero");

        Assert.Equal(2, markers.Count);
        Assert.Contains(markers, m => m.ObjectiveDescription.StartsWith("Find") && Math.Abs(m.X - 88.3f) < 2f && Math.Abs(m.Z - 225.3f) < 2f);
        Assert.Contains(markers, m => m.ObjectiveDescription.StartsWith("Visit") && m.X == 55.26f && m.Z == 252.75f);
    }
}
