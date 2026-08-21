using TarkovHelper.Core;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

public class QuestAvailabilityTests
{
    private static QuestTask MakeTask(string id, bool isComplete, params string[] prerequisiteIds)
    {
        return new QuestTask
        {
            Id = id,
            Name = id,
            Trader = new Trader { Name = "Prapor" },
            IsComplete = isComplete,
            TaskRequirements = prerequisiteIds
                .Select(p => new TaskStatusRequirement { Task = new TaskRef { Id = p }, Status = new List<string> { "complete" } })
                .ToList(),
        };
    }

    [Fact]
    public void TaskWithNoPrerequisites_IsAvailable()
    {
        var task = MakeTask("t1", isComplete: false);
        var index = QuestAvailability.IndexById(new[] { task });

        Assert.True(QuestAvailability.IsAvailable(task, index));
    }

    [Fact]
    public void CompletedTask_IsNeverAvailable()
    {
        var task = MakeTask("t1", isComplete: true);
        var index = QuestAvailability.IndexById(new[] { task });

        Assert.False(QuestAvailability.IsAvailable(task, index));
    }

    [Fact]
    public void TaskWithIncompletePrerequisite_IsNotAvailable()
    {
        var prereq = MakeTask("t1", isComplete: false);
        var dependent = MakeTask("t2", isComplete: false, "t1");
        var index = QuestAvailability.IndexById(new[] { prereq, dependent });

        Assert.False(QuestAvailability.IsAvailable(dependent, index));
    }

    [Fact]
    public void TaskWithCompletedPrerequisite_BecomesAvailable()
    {
        var prereq = MakeTask("t1", isComplete: true);
        var dependent = MakeTask("t2", isComplete: false, "t1");
        var index = QuestAvailability.IndexById(new[] { prereq, dependent });

        Assert.True(QuestAvailability.IsAvailable(dependent, index));
    }

    [Fact]
    public void TaskWithMultiplePrerequisites_RequiresAllComplete()
    {
        var prereq1 = MakeTask("t1", isComplete: true);
        var prereq2 = MakeTask("t2", isComplete: false);
        var dependent = MakeTask("t3", isComplete: false, "t1", "t2");
        var index = QuestAvailability.IndexById(new[] { prereq1, prereq2, dependent });

        Assert.False(QuestAvailability.IsAvailable(dependent, index));
    }

    [Fact]
    public void UnknownPrerequisiteId_TreatedAsIncomplete()
    {
        // Defends against a prerequisite task missing from the fetched set
        // (e.g. a faction-specific task on the other faction's tree).
        var dependent = MakeTask("t2", isComplete: false, "missing-task-id");
        var index = QuestAvailability.IndexById(new[] { dependent });

        Assert.False(QuestAvailability.IsAvailable(dependent, index));
    }
}
