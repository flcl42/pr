using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed class GitHubApiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _userGate = new(1, 1);
    private string? _token;
    private string? _currentUserLogin;
    private DateTimeOffset _currentUserExpiresAt;

    private GitHubApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 2,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(20),
            PooledConnectionLifetime = TimeSpan.FromHours(1),
            UseCookies = false,
        };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/", UriKind.Absolute),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("pr-cli", "1.0"));
    }

    public static GitHubApiClient Shared { get; } = new();

    public async Task<string> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_currentUserLogin)
            && _currentUserExpiresAt > DateTimeOffset.UtcNow)
        {
            return _currentUserLogin;
        }

        await _userGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_currentUserLogin)
                && _currentUserExpiresAt > DateTimeOffset.UtcNow)
            {
                return _currentUserLogin;
            }

            using var document = await GetJsonAsync("user", cancellationToken);
            _currentUserLogin = document.RootElement.TryGetProperty("login", out var login)
                ? login.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(_currentUserLogin))
            {
                throw new InvalidOperationException("GitHub returned an empty current-user login");
            }

            _currentUserExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            return _currentUserLogin;
        }
        finally
        {
            _userGate.Release();
        }
    }

    public async Task<JsonDocument> GetJsonAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, endpoint, body: null, cancellationToken);
        return ParseJson(response.Body, endpoint);
    }

    public async Task<IReadOnlyList<JsonElement>> GetPagedArrayAsync(
        string endpoint,
        CancellationToken cancellationToken,
        int pageSize = 100)
    {
        var items = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var pageEndpoint = $"{endpoint}{separator}per_page={pageSize}&page={page}";
            using var document = await GetJsonAsync(pageEndpoint, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"GitHub API did not return an array for {endpoint}");
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                items.Add(item.Clone());
                count++;
            }

            if (count < pageSize)
            {
                return items;
            }
        }
    }

    public async Task<JsonDocument> GraphQlAsync(
        string query,
        IReadOnlyDictionary<string, object?>? variables,
        CancellationToken cancellationToken,
        bool retryTransientFailures = true)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            variables = variables ?? new Dictionary<string, object?>(),
        });
        var response = await SendAsync(
            HttpMethod.Post,
            "graphql",
            payload,
            cancellationToken,
            retryTransientFailures);
        var document = ParseJson(response.Body, "graphql");
        if (document.RootElement.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = errors
                .EnumerateArray()
                .Select(error => error.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null)
                .Where(message => !string.IsNullOrWhiteSpace(message));
            var detail = string.Join("; ", messages);
            document.Dispose();
            throw new InvalidOperationException(
                detail.Length > 0 ? $"GitHub GraphQL failed: {detail}" : "GitHub GraphQL failed");
        }

        return document;
    }

    public async Task PostJsonAsync(
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload);
        await SendAsync(
            HttpMethod.Post,
            endpoint,
            body,
            cancellationToken,
            retryTransientFailures: false);
    }

    public async Task PatchAsync(string endpoint, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Patch, endpoint, body: null, cancellationToken);
    }

    private async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string endpoint,
        string? body,
        CancellationToken cancellationToken,
        bool retryTransientFailures = true)
    {
        var token = await GetTokenAsync(cancellationToken);
        var retryCount = retryTransientFailures ? RetryDelays.Length : 0;
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);
                    var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return new ApiResponse(response.StatusCode, responseBody);
                    }

                    if (attempt < retryCount && IsTransient(response))
                    {
                        await Task.Delay(RetryDelay(response, attempt), cancellationToken);
                        continue;
                    }

                    throw new GitHubApiException(
                        response.StatusCode,
                        response.ReasonPhrase,
                        responseBody);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= retryCount)
                    {
                        throw new TimeoutException(
                            $"GitHub API request timed out after {RequestTimeout.TotalMinutes:0} minutes: {endpoint}");
                    }
                }
                catch (HttpRequestException) when (attempt < retryCount)
                {
                }
                catch (IOException) when (attempt < retryCount)
                {
                }

                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            return _token;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token))
            {
                return _token;
            }

            _token = (Environment.GetEnvironmentVariable("GH_TOKEN")
                ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"))?.Trim();
            if (string.IsNullOrWhiteSpace(_token))
            {
                var result = await GhCommand.RunAsync(
                    cancellationToken,
                    TimeSpan.FromSeconds(20),
                    "auth",
                    "token",
                    "--hostname",
                    "github.com");
                if (result.ExitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError;
                    throw new InvalidOperationException(
                        $"failed to read GitHub authentication from gh: {SingleLine(detail)}");
                }

                _token = result.StandardOutput.Trim();
            }

            return !string.IsNullOrWhiteSpace(_token)
                ? _token
                : throw new InvalidOperationException("gh returned an empty GitHub authentication token");
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static bool IsTransient(HttpResponseMessage response)
    {
        var statusCode = response.StatusCode;
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (statusCode == HttpStatusCode.Forbidden && response.Headers.RetryAfter is not null);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter;
        }

        if (response.Headers.RetryAfter?.Date is { } retryAt)
        {
            return retryAt > DateTimeOffset.UtcNow
                ? retryAt - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
        }

        return RetryDelays[attempt];
    }

    private static JsonDocument ParseJson(string value, string endpoint)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"GitHub API returned invalid JSON for {endpoint}: {SingleLine(value)}",
                ex);
        }
    }

    private static string SingleLine(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 300 ? line : line[..297] + "...";
    }

    private sealed record ApiResponse(HttpStatusCode StatusCode, string Body);
}

internal sealed class GitHubApiException : InvalidOperationException
{
    public GitHubApiException(HttpStatusCode statusCode, string? reasonPhrase, string responseBody)
        : base($"GitHub API {(int)statusCode} {reasonPhrase}: {SingleLine(responseBody)}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }

    private static string SingleLine(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 300 ? line : line[..297] + "...";
    }
}
