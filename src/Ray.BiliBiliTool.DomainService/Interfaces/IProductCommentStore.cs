using Ray.BiliBiliTool.DomainService.Dtos;

namespace Ray.BiliBiliTool.DomainService.Interfaces;

public interface IProductCommentStore : IDomainService
{
    Task<bool> AddCandidateAsync(ProductCommentCandidate candidate);

    Task<IReadOnlyList<ProductCommentCandidate>> GetCandidatesAsync();

    Task<IReadOnlyList<ProductCommentCandidate>> GetPendingCandidatesAsync();

    Task<bool> UpdateCandidateStatusAsync(string bvid, string status, string lastError = "");

    Task AddLedgerRecordAsync(ProductCommentLedgerRecord record);

    Task<IReadOnlyList<ProductCommentLedgerRecord>> GetLedgerAsync();

    Task SaveAccountStateAsync(ProductCommentAccountState state);

    Task<ProductCommentAccountState?> GetAccountStateAsync(string accountUserId);

    Task<IReadOnlyList<ProductCommentAccountState>> GetAccountStatesAsync();

    Task SaveTemplateAsync(ProductCommentTemplate template);

    Task<IReadOnlyList<ProductCommentTemplate>> GetTemplatesAsync();
}
