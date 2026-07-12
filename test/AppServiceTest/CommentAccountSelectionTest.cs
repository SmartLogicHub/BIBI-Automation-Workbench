using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Web.Services;

namespace AppServiceTest;

public class AutomationAccountSelectionTest
{
    [Fact]
    public void ResolveParticipating_ShouldOnlyReturnSelectedUsableAccounts()
    {
        var accounts = new List<ContentAutomationAccount>
        {
            Account("1", "1001", "ok", "cookie-1"),
            Account("2", "1002", "login_required", ""),
            Account("3", "1003", "ok", "cookie-3", enabled: false),
            Account("4", "", "ok", "cookie-4"),
        };

        var result = AutomationAccountSelection.ResolveSelected(accounts, ["1", "2", "3", "4"]);

        Assert.Single(result);
        Assert.Equal("1", result[0].Id);
    }

    [Fact]
    public void ResolveParticipating_ShouldRespectExplicitSelection()
    {
        var accounts = new List<ContentAutomationAccount>
        {
            Account("1", "1001", "ok", "cookie-1"),
            Account("2", "1002", "logged_in", "cookie-2"),
        };

        var result = AutomationAccountSelection.ResolveSelected(accounts, ["2"]);

        Assert.Single(result);
        Assert.Equal("2", result[0].Id);
    }

    [Fact]
    public void ResolveWorkflowParticipants_ShouldUseAllAvailableAccountsWhenSelectionIsEmpty()
    {
        var accounts = new List<ContentAutomationAccount>
        {
            Account("1", "1001", "ok", "cookie-1"),
            Account("2", "1002", "logged_in", "cookie-2"),
            Account("3", "1003", "login_required", ""),
        };

        var result = AutomationAccountSelection.ResolveWorkflowParticipants(accounts, []);

        Assert.Equal(["1", "2"], result.Select(account => account.Id));
    }

    [Fact]
    public void ResolveWorkflowParticipants_ShouldMatchStoredUidOrInternalId()
    {
        var accounts = new List<ContentAutomationAccount>
        {
            Account("1", "1001", "ok", "cookie-1"),
            Account("2", "1002", "logged_in", "cookie-2"),
        };

        var byUid = AutomationAccountSelection.ResolveWorkflowParticipants(accounts, ["1002"]);
        var byInternalId = AutomationAccountSelection.ResolveWorkflowParticipants(accounts, ["1"]);

        Assert.Equal("2", Assert.Single(byUid).Id);
        Assert.Equal("1", Assert.Single(byInternalId).Id);
    }

    [Fact]
    public void FormatSelectionText_ShouldDisplayAccountNamesInsteadOfInternalIds()
    {
        var accounts = new List<ContentAutomationAccount>
        {
            Account("10", "1001", "ok", "cookie-1", name: "账号一", nickname: "残血1点反杀"),
            Account("11", "1002", "logged_in", "cookie-2", name: "账号二", nickname: "鸽王本王の"),
        };

        var text = AutomationAccountSelection.FormatSelectionText(accounts, ["10", "11"]);

        Assert.Equal("残血1点反杀、鸽王本王の", text);
        Assert.DoesNotContain("10", text);
        Assert.DoesNotContain("11", text);
    }

    private static ContentAutomationAccount Account(
        string id,
        string uid,
        string loginStatus,
        string cookie,
        bool enabled = true,
        string name = "",
        string nickname = ""
    ) =>
        new()
        {
            Id = id,
            Uid = uid,
            LoginStatus = loginStatus,
            Cookie = cookie,
            Enabled = enabled,
            Name = name,
            Nickname = nickname,
        };
}
