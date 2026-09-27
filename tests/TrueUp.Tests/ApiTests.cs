// Integration tests against the live TrueUp API. Need TRUEUP_API_KEY (and optionally TRUEUP_BASE_URL).
// Each full run uses 2 analyses. Run in Docker: `just test` (or `docker compose run --rm test`).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TrueUp;
using Xunit;

public class ApiTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRUEUP_API_KEY"));

    private static List<IReadOnlyDictionary<string, object?>> Rows(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(Fixtures, name)).Where(l => l.Length > 0).ToList();
        var head = lines[0].Split(',');
        return lines.Skip(1).Select(l =>
        {
            var cells = l.Split(',');
            IReadOnlyDictionary<string, object?> row = head.Select((h, i) => (h, (object?)cells[i])).ToDictionary(x => x.h, x => x.Item2);
            return row;
        }).ToList();
    }

    [Fact]
    public void MissingApiKeyFailsBeforeAnyRequest()
    {
        var e = Assert.Throws<AuthenticationException>(() => new TrueUpClient(new TrueUpOptions { Environment = _ => null }));
        Assert.Equal("missing_api_key", e.Code);
    }

    [Fact]
    public async Task AccountUsagePlans()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        Assert.StartsWith("tu_live_", (await tu.AccountAsync()).Key.Prefix);
        Assert.Contains((await tu.UsageAsync()).Metrics, m => m.Metric == "analyses");
        Assert.Contains(await tu.PlansAsync(), p => p.Slug == "free");
    }

    [Fact]
    public async Task ReconcileFilesThenRowsWithSavedWeights()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        var result = await tu.ReconcileAsync(Table.File(Path.Combine(Fixtures, "statement.csv")), Table.File(Path.Combine(Fixtures, "receiving.csv")));
        Assert.Equal("reconcile", result.Analysis);
        Assert.Equal(7, result.Stats["paired"]);
        Assert.Equal(new[] { "qty_mismatch statement.csv:row 5", "phantom statement.csv:row 6" },
            result.Findings.Select(f => $"{f.Kind} {f.Subject}").ToArray());
        Assert.Equal(43.2, result.Findings[1].Amount!.Value, 9);
        Assert.Equal("trueup.match-weights", result.Details.Weights!["format"]!.GetValue<string>());

        var again = await tu.ReconcileAsync(Table.Rows("statement.csv", Rows("statement.csv")), Table.Rows("receiving.csv", Rows("receiving.csv")),
            new ReconcileOptions { Weights = result.Details.Weights });
        Assert.Equal(7, again.Stats["paired"]);
        Assert.False(again.Details.Model!["learned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ErrorsAreTyped()
    {
        if (!Live) return;
        var auth = await Assert.ThrowsAsync<AuthenticationException>(() =>
            new TrueUpClient(new TrueUpOptions { ApiKey = "tu_live_" + new string('x', 40) }).AccountAsync());
        Assert.Equal((401, "invalid_api_key"), (auth.Status, auth.Code));
        var bad = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            new TrueUpClient().ReconcileAsync(Table.File(Path.Combine(Fixtures, "statement.csv")), Table.Content("scan.pdf", "%PDF-1.4")));
        Assert.Equal((422, "unsupported_file"), (bad.Status, bad.Code));
    }
}
