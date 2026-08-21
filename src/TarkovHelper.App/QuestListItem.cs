using System.ComponentModel;
using System.Runtime.CompilerServices;
using TarkovHelper.Core.Models;

namespace TarkovHelper.App;

public class QuestListItem : INotifyPropertyChanged
{
    private bool _isComplete;

    public QuestListItem(QuestTask task)
    {
        Task = task;
        _isComplete = task.IsComplete;
    }

    public QuestTask Task { get; }

    public string Name => Task.Name;
    public string TraderName => Task.Trader.Name;
    public string MapName => Task.Map?.Name ?? "Any map";
    public int MinPlayerLevel => Task.MinPlayerLevel ?? 0;
    public bool IsActive => Task.IsActive;

    public string ItemsNeeded
    {
        get
        {
            var itemObjectives = Task.Objectives.Where(o => o.Items.Count > 0).ToList();
            if (itemObjectives.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("; ", itemObjectives.Select(o =>
                $"{o.Count ?? 1}x {string.Join("/", o.Items.Select(i => i.ShortName ?? i.Name))}" +
                (o.FoundInRaid == true ? " (FIR)" : string.Empty)));
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (_isComplete == value)
            {
                return;
            }

            _isComplete = value;
            Task.IsComplete = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
