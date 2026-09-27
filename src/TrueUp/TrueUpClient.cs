using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TrueUp;

/// <summary>Any error the API returned, or a failure to reach it. <see cref="Code"/> is the API's error code; branch on it.</summary>
public class TrueUpException : Exception
{
    public TrueUpException(string message, int status = 0, string code = "connection_error", string? body = null) : base(message)
    {
        Status = status;
        Code = code;
        Body = body;
    }

    /// <summary>HTTP status (0 when the API couldn't be reached).</summary>
    public int Status { get; }
    /// <summary>The API's error code: invalid_api_key, unsupported_file, quota_exceeded, ...</summary>
    public string Code { get; }
    public string? Body { get; }
}

/// <summary>401: missing, unknown or revoked API key.</summary>
public class AuthenticationException : TrueUpException
{
    public AuthenticationException(string m, int s = 0, string c = "missing_api_key", string? b = null) : base(m, s, c, b) { }
}

/// <summary>400, 413, 415, 422: the request or the files need fixing.</summary>
public class InvalidRequestException : TrueUpException
{
    public InvalidRequestException(string m, int s, string c, string? b) : base(m, s, c, b) { }
}

/// <summary>404, 405.</summary>
public class NotFoundException : TrueUpException
{
    public NotFoundException(string m, int s, string c, string? b) : base(m, s, c, b) { }
}

/// <summary>429 rate_limited: slow down.</summary>
public class RateLimitException : TrueUpException
{
    public RateLimitException(string m, int s, string c, string? b, TimeSpan? retryAfter) : base(m, s, c, b) => RetryAfter = retryAfter;
    public TimeSpan? RetryAfter { get; }
}

/// <summary>429 quota_exceeded: the team used its plan's allowance this month. Retrying won't help.</summary>
public class QuotaExceededException : TrueUpException
{
    public QuotaExceededException(string m, int s, string c, string? b) : base(m, s, c, b) { }
}

/// <summary>5xx.</summary>
public class ServerException : TrueUpException
{
    public ServerException(string m, int s, string c, string? b) : base(m, s, c, b) { }
}

/// <summary>The API couldn't be reached, or took too long.</summary>
public class ConnectionException : TrueUpException
{
    public ConnectionException(string m) : base(m) { }
}

/// <summary>
/// A table to reconcile: a file, file contents, or rows. <see cref="Name"/> is how findings refer to its rows
/// ("statement.csv:row 5").
/// </summary>
public sealed class Table
{
    private readonly byte[]? _content;
    private readonly IReadOnlyList<IReadOnlyDictionary<string, object?>>? _rows;

    private Table(string name, byte[]? content, IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows)
    {
        Name = name;
        _content = content;
        _rows = rows;
    }

    public string Name { get; }

    public static Table File(string path, string? name = null) => new(name ?? Path.GetFileName(path), System.IO.File.ReadAllBytes(path), null);
    public static Table Content(string name, byte[] content) => new(name, content, null);
    public static Table Content(string name, string content) => new(name, Encoding.UTF8.GetBytes(content), null);
    public static Table Rows(string name, IEnumerable<IReadOnlyDictionary<string, object?>> rows) => new(name, null, rows.ToList());

    internal bool IsRows => _rows != null;
    internal IReadOnlyList<IReadOnlyDictionary<string, object?>>? RowList => _rows;

    internal (string FileName, byte[] Bytes) ToFile()
    {
        if (_rows == null) return (Name, _content ?? Array.Empty<byte>());
        var stem = Path.GetFileNameWithoutExtension(Name);
        return (stem + ".json", JsonSerializer.SerializeToUtf8Bytes(_rows));
    }
}

/// <summary>Decisions a person made about pairs: [left row, right row].</summary>
public sealed class Answers
{
    [JsonPropertyName("same")] public List<string[]>? Same { get; set; }
    [JsonPropertyName("different")] public List<string[]>? Different { get; set; }
}

/// <summary>Optional settings for a reconcile call.</summary>
public sealed class ReconcileOptions
{
    /// <summary><c>Details.Weights</c> from an earlier result: apply what was learned instead of learning again.</summary>
    public JsonObject? Weights { get; set; }
    public Answers? Answers { get; set; }
}

public sealed class Finding
{
    /// <summary>phantom, unbilled, duplicate, received_duplicate, qty_mismatch, price_change, amount_mismatch, unsure_pair</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>The row it is about: "statement.csv:row 5".</summary>
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    /// <summary>How likely the pairing is right; null for rows with no pair.</summary>
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("amount")] public double? Amount { get; set; }
    [JsonPropertyName("provenance")] public List<string> Provenance { get; set; } = new();
    [JsonPropertyName("data")] public JsonObject? Data { get; set; }
}

public sealed class Pair
{
    [JsonPropertyName("left")] public string Left { get; set; } = "";
    [JsonPropertyName("right")] public string Right { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

public sealed class Details
{
    [JsonPropertyName("model")] public JsonObject? Model { get; set; }
    [JsonPropertyName("weights")] public JsonObject? Weights { get; set; }
    [JsonPropertyName("pairs")] public List<Pair> Pairs { get; set; } = new();
}

public sealed class ReconcileResult
{
    [JsonPropertyName("analysis")] public string Analysis { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("headline")] public string Headline { get; set; } = "";
    [JsonPropertyName("stats")] public Dictionary<string, double> Stats { get; set; } = new();
    [JsonPropertyName("findings")] public List<Finding> Findings { get; set; } = new();
    [JsonPropertyName("details")] public Details Details { get; set; } = new();
    [JsonPropertyName("inputs")] public List<string> Inputs { get; set; } = new();
    [JsonPropertyName("engine")] public string? Engine { get; set; }
}

public sealed class Account
{
    public sealed class TeamInfo { [JsonPropertyName("id")] public string Id { get; set; } = ""; [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    public sealed class PlanInfo { [JsonPropertyName("slug")] public string Slug { get; set; } = ""; [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    public sealed class KeyInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";
    }
    [JsonPropertyName("team")] public TeamInfo Team { get; set; } = new();
    [JsonPropertyName("plan")] public PlanInfo? Plan { get; set; }
    [JsonPropertyName("key")] public KeyInfo Key { get; set; } = new();
}

public sealed class UsageMetric
{
    [JsonPropertyName("metric")] public string Metric { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("used")] public long Used { get; set; }
    [JsonPropertyName("included")] public long Included { get; set; }
    [JsonPropertyName("remaining")] public long Remaining { get; set; }
    [JsonPropertyName("hard_cap")] public bool HardCap { get; set; }
    [JsonPropertyName("overage")] public long Overage { get; set; }
}

public sealed class Usage
{
    [JsonPropertyName("period")] public string Period { get; set; } = "";
    [JsonPropertyName("resets_at")] public string ResetsAt { get; set; } = "";
    [JsonPropertyName("metrics")] public List<UsageMetric> Metrics { get; set; } = new();
}

public sealed class Plan
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("price_cents")] public long PriceCents { get; set; }
    [JsonPropertyName("interval")] public string Interval { get; set; } = "";
    [JsonPropertyName("purchasable")] public bool Purchasable { get; set; }
    [JsonPropertyName("limits")] public List<JsonObject> Limits { get; set; } = new();
}

/// <summary>Client settings.</summary>
public sealed class TrueUpOptions
{
    /// <summary>Default: the TRUEUP_API_KEY environment variable.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Default: TRUEUP_BASE_URL, then the hosted API.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Per request. Default 300 s (big ledgers take a while).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
    /// <summary>Retries for rate limits, server errors and dropped connections. Default 2.</summary>
    public int MaxRetries { get; set; } = 2;
    public HttpClient? HttpClient { get; set; }
    internal Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;
}

/// <summary>
/// TrueUp API client. Thread-safe.
/// <code>
/// var trueup = new TrueUpClient();                     // reads TRUEUP_API_KEY
/// var result = await trueup.ReconcileAsync(Table.File("statement.csv"), Table.File("receiving.csv"));
/// foreach (var f in result.Findings) Console.WriteLine($"{f.Kind} {f.Subject} {f.Detail}");
/// </code>
/// </summary>
public sealed class TrueUpClient
{
    public const string Version = "0.1.0";
    public const string DefaultBaseUrl = "https://trueup-cloud.merchantprotocol.workers.dev";

    private static readonly Random Jitter = new();
    private readonly string _apiKey;
    private readonly int _maxRetries;
    private readonly HttpClient _http;

    public TrueUpClient(TrueUpOptions? options = null)
    {
        var o = options ?? new TrueUpOptions();
        var key = NonEmpty(o.ApiKey) ?? NonEmpty(o.Environment("TRUEUP_API_KEY"));
        _apiKey = key ?? throw new AuthenticationException(
            "No API key: set TrueUpOptions.ApiKey or TRUEUP_API_KEY. Create one in the TrueUp dashboard under API keys.");
        BaseUrl = (NonEmpty(o.BaseUrl) ?? NonEmpty(o.Environment("TRUEUP_BASE_URL")) ?? DefaultBaseUrl).TrimEnd('/');
        _maxRetries = o.MaxRetries;
        _http = o.HttpClient ?? new HttpClient { Timeout = o.Timeout };
    }

    public string BaseUrl { get; }

    /// <summary>The team, plan and key behind this client's API key.</summary>
    public async Task<Account> AccountAsync(CancellationToken ct = default) =>
        Deserialize<Account>(await SendAsync(HttpMethod.Get, "/v1/account", null, ct));

    /// <summary>This month's usage for the key's team.</summary>
    public async Task<Usage> UsageAsync(CancellationToken ct = default) =>
        Deserialize<Usage>(await SendAsync(HttpMethod.Get, "/v1/usage", null, ct));

    /// <summary>The plans a team can be on.</summary>
    public async Task<List<Plan>> PlansAsync(CancellationToken ct = default) =>
        Deserialize<PlansEnvelope>(await SendAsync(HttpMethod.Get, "/v1/plans", null, ct)).Plans;

    private sealed class PlansEnvelope { [JsonPropertyName("plans")] public List<Plan> Plans { get; set; } = new(); }

    /// <summary>
    /// Reconcile two tables. <paramref name="left"/> is the side that bills or claims (a statement, your books),
    /// <paramref name="right"/> the other side (receiving log, bank feed). Counts as one analysis.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(Table left, Table right, ReconcileOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new ReconcileOptions();
        if (left.IsRows && right.IsRows)
        {
            var body = new JsonObject
            {
                ["left"] = new JsonObject { ["name"] = left.Name, ["rows"] = JsonSerializer.SerializeToNode(left.RowList) },
                ["right"] = new JsonObject { ["name"] = right.Name, ["rows"] = JsonSerializer.SerializeToNode(right.RowList) },
            };
            if (o.Weights != null) body["weights"] = JsonNode.Parse(o.Weights.ToJsonString());
            if (o.Answers != null) body["answers"] = JsonSerializer.SerializeToNode(o.Answers);
            return Deserialize<ReconcileResult>(await SendAsync(HttpMethod.Post, "/v1/reconcile",
                () => new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct));
        }
        return await UploadAsync(new[] { ("left", left), ("right", right) }, o, ct);
    }

    /// <summary>Send two or more files; TrueUp picks the pair to reconcile and which side is which. One analysis.</summary>
    public Task<ReconcileResult> ReconcileFilesAsync(IEnumerable<Table> files, ReconcileOptions? options = null, CancellationToken ct = default) =>
        UploadAsync(files.Select(f => ("files", f)).ToArray(), options ?? new ReconcileOptions(), ct);

    private async Task<ReconcileResult> UploadAsync((string Field, Table Table)[] parts, ReconcileOptions o, CancellationToken ct)
    {
        HttpContent Build()
        {
            var form = new MultipartFormDataContent("----trueup" + Guid.NewGuid().ToString("N"));
            foreach (var (field, table) in parts)
            {
                var (name, bytes) = table.ToFile();
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                // Quoted parameters: some multipart parsers refuse .NET's bare `name=left`.
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = Quote(field),
                    FileName = Quote(name),
                };
                form.Add(content);
            }
            if (o.Weights != null) form.Add(Field("weights", o.Weights.ToJsonString()));
            if (o.Answers != null) form.Add(Field("answers", JsonSerializer.Serialize(o.Answers)));
            return form;
        }
        return Deserialize<ReconcileResult>(await SendAsync(HttpMethod.Post, "/v1/reconcile", Build, ct));
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "_").Replace("\r", "_").Replace("\n", "_") + "\"";

    private static StringContent Field(string name, string value)
    {
        var c = new StringContent(value, Encoding.UTF8);
        c.Headers.ContentType = null;
        c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
        return c;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd($"trueup-dotnet/{Version}");
            if (content != null) req.Content = content();
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt < _maxRetries)
                {
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new ConnectionException($"Couldn't reach TrueUp at {BaseUrl}: {e.Message}");
            }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var status = (int)res.StatusCode;
                if (status >= 200 && status < 300) return text;
                string code = $"http_{status}", message = $"HTTP {status}";
                try
                {
                    var err = JsonNode.Parse(text)?["error"];
                    code = err?["code"]?.GetValue<string>() ?? code;
                    message = err?["message"]?.GetValue<string>() ?? message;
                }
                catch (JsonException)
                {
                    // not a JSON error body
                }
                TimeSpan? retryAfter = res.Headers.RetryAfter?.Delta;
                var error = ErrorFor(status, code, message, text, retryAfter);
                if ((error is RateLimitException || error is ServerException) && attempt < _maxRetries)
                {
                    var wait = error is RateLimitException rl && rl.RetryAfter.HasValue ? rl.RetryAfter.Value : Backoff(attempt);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw error;
            }
        }
    }

    private static TrueUpException ErrorFor(int status, string code, string message, string body, TimeSpan? retryAfter) => status switch
    {
        401 => new AuthenticationException(message, status, code, body),
        429 when code == "quota_exceeded" => new QuotaExceededException(message, status, code, body),
        429 => new RateLimitException(message, status, code, body, retryAfter),
        404 or 405 => new NotFoundException(message, status, code, body),
        >= 500 => new ServerException(message, status, code, body),
        _ => new InvalidRequestException(message, status, code, body),
    };

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new TrueUpException("Empty response", 200, "empty_response");

    private static TimeSpan Backoff(int attempt)
    {
        double jitter;
        lock (Jitter) jitter = Jitter.NextDouble();
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)) * (0.5 + jitter / 2));
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
