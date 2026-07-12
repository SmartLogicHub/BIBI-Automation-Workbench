namespace Ray.BiliBiliTool.Application.Contracts.Runtime;

public enum SystemRuntimeStatus
{
    Starting,
    Ready,
    NeedsAttention,
    Stopped,
}

public class SystemRuntimeState
{
    public SystemRuntimeStatus Status { get; set; } = SystemRuntimeStatus.Starting;

    public string Message { get; set; } = "正在准备运行环境";

    public int AvailableAccounts { get; set; }

    public int TotalAccounts { get; set; }

    public int TodayComments { get; set; }

    public int TodayMaintenanceRuns { get; set; }

    public string ActiveOperation { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public bool IsReady => Status == SystemRuntimeStatus.Ready;
}

public interface ISystemRuntimeStateStore
{
    SystemRuntimeState Current { get; }

    event Action<SystemRuntimeState>? Changed;

    void Update(SystemRuntimeState state);
}
