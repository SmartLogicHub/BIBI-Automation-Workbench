using Ray.BiliBiliTool.Application.Contracts.Runtime;

namespace Ray.BiliBiliTool.Application;

public class SystemRuntimeStateStore : ISystemRuntimeStateStore
{
    private readonly object _lock = new();
    private SystemRuntimeState _current = new();

    public event Action<SystemRuntimeState>? Changed;

    public SystemRuntimeState Current
    {
        get
        {
            lock (_lock)
                return Clone(_current);
        }
    }

    public void Update(SystemRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SystemRuntimeState snapshot;
        lock (_lock)
        {
            _current = Clone(state);
            _current.UpdatedAt = DateTimeOffset.Now;
            snapshot = Clone(_current);
        }

        Changed?.Invoke(snapshot);
    }

    private static SystemRuntimeState Clone(SystemRuntimeState source)
    {
        return new SystemRuntimeState
        {
            Status = source.Status,
            Message = source.Message,
            AvailableAccounts = source.AvailableAccounts,
            TotalAccounts = source.TotalAccounts,
            TodayComments = source.TodayComments,
            TodayMaintenanceRuns = source.TodayMaintenanceRuns,
            ActiveOperation = source.ActiveOperation,
            UpdatedAt = source.UpdatedAt,
        };
    }
}
