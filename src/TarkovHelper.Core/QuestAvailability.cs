using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

public static class QuestAvailability
{
    // A task is "available" once every prerequisite task referenced in
    // taskRequirements is complete. This ignores the requirement's Status
    // list (e.g. some prerequisites only require the task be "started" or
    // "failed" rather than "complete") since that finer-grained distinction
    // isn't needed for a first-pass "what can I do next" view.
    public static bool IsAvailable(QuestTask task, IReadOnlyDictionary<string, QuestTask> tasksById)
    {
        if (task.IsComplete)
        {
            return false;
        }

        foreach (var requirement in task.TaskRequirements)
        {
            var prereqId = requirement.Task.Id;
            if (prereqId is null)
            {
                continue;
            }

            if (!tasksById.TryGetValue(prereqId, out var prereq) || !prereq.IsComplete)
            {
                return false;
            }
        }

        return true;
    }

    public static Dictionary<string, QuestTask> IndexById(IEnumerable<QuestTask> tasks) =>
        tasks.Where(t => t.Id is not null).ToDictionary(t => t.Id!, t => t);
}
