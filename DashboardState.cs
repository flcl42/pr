internal sealed record DashboardSnapshot(
    IReadOnlyList<DashboardPullRequest> Items,
    IReadOnlyList<RepositoryRef> Repositories,
    int RequiredApprovals,
    string RepositorySummary,
    string EmptyMessage,
    string? CurrentUserLogin,
    bool IsRefreshing,
    bool IsCleaning,
    bool ShowIgnoredPullRequests,
    int OpenNonDraftCount,
    int IgnoredPullRequestCount,
    int ApiCalls,
    DateTimeOffset? LastRefresh,
    DateTimeOffset? LastCleanup,
    int LastCleanupScanned,
    int LastCleanupMarkedRead,
    int LastCleanupRemovedIgnored,
    string? Error,
    string? CleanupError,
    bool CanCleanup,
    CodexReviewSnapshot Review,
    CodexReviewSettings ReviewSettings,
    string SettingsPath);

internal sealed record DashboardPullRequest(
    PullRequestInfo PullRequest,
    bool IsIgnored,
    bool IsTop,
    bool IsReviewed,
    bool IsReviewQueued,
    CodexReviewedPullRequest? ReviewResult)
{
    public string Section => IsReviewed ? "Reviewed" : IsTop ? "Top" : "Pull requests";

    public string DisplayTitle
    {
        get
        {
            var agentPrefix = ReviewResult?.AgentLetterPrefix;
            return string.IsNullOrEmpty(agentPrefix)
                ? PullRequest.Title
                : $"{agentPrefix} {PullRequest.Title}";
        }
    }
}
