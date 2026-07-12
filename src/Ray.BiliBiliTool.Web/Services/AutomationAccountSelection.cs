using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;

namespace Ray.BiliBiliTool.Web.Services;

public static class AutomationAccountSelection
{
    public static IReadOnlyList<ContentAutomationAccount> ResolveSelected(
        IEnumerable<ContentAutomationAccount> accounts,
        IEnumerable<string> selectedAccountIds
    )
    {
        var selected = Normalize(selectedAccountIds);
        return accounts
            .Where(account => IsAvailable(account) && Matches(account, selected))
            .ToList();
    }

    public static IReadOnlyList<ContentAutomationAccount> ResolveWorkflowParticipants(
        IEnumerable<ContentAutomationAccount> accounts,
        IEnumerable<string> selectedAccountIds
    )
    {
        var available = accounts.Where(IsAvailable).ToList();
        var selected = Normalize(selectedAccountIds);
        return selected.Count == 0
            ? available
            : available.Where(account => Matches(account, selected)).ToList();
    }

    public static bool IsAvailable(ContentAutomationAccount account) =>
        account.Enabled
        && !string.IsNullOrWhiteSpace(account.Uid)
        && !string.IsNullOrWhiteSpace(account.Cookie)
        && account.LoginStatus?.Trim().ToLowerInvariant() is "ok" or "logged_in" or "success";

    public static string FormatSelectionText(
        IEnumerable<ContentAutomationAccount> accounts,
        IEnumerable<string?> selectedAccountIds
    )
    {
        var selected = Normalize(selectedAccountIds);
        return string.Join(
            "、",
            accounts.Where(account => Matches(account, selected)).Select(DisplayName)
        );
    }

    private static HashSet<string> Normalize(IEnumerable<string?> accountIds) =>
        accountIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Matches(ContentAutomationAccount account, HashSet<string> selected) =>
        selected.Contains(account.Id)
        || (!string.IsNullOrWhiteSpace(account.Uid) && selected.Contains(account.Uid));

    private static string DisplayName(ContentAutomationAccount account) =>
        string.IsNullOrWhiteSpace(account.Nickname) ? account.Name : account.Nickname;
}
