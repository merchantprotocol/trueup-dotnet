// Integration tests against the live TrueUp API. Need TRUEUP_API_KEY (and optionally TRUEUP_BASE_URL).
// Each full run uses 10 analyses. Run in Docker: `just test` (or `docker compose run --rm test`).
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

    [Fact]
    public async Task StoredFilesRunsAndModels()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        var files = await tu.UploadFilesAsync(new[] { Table.File(Path.Combine(Fixtures, "statement.csv")), Table.File(Path.Combine(Fixtures, "receiving.csv")) });
        var (statement, receiving) = (files[0], files[1]);
        try
        {
            Assert.Equal(8, statement.Rows);
            Assert.Equal("number", statement.Roles!["Qty"]);
            Assert.Equal("receiving.csv", (await tu.GetFileAsync(receiving.Id)).Name);
            Assert.Contains(await tu.ListFilesAsync(), f => f.Id == statement.Id);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Fixtures, "statement.csv")), await tu.FileContentAsync(statement.Id));

            var result = await tu.ReconcileStoredAsync(statement.Id, receiving.Id);
            Assert.Equal(7, result.Stats["paired"]);
            Assert.StartsWith("run_", result.RunId);
            var run = await tu.GetRunAsync(result.RunId!);
            Assert.Equal("done", run.Run.Status);
            Assert.Equal(7, run.Result!.Stats["paired"]);
            var page = await tu.ListRunsAsync(1);
            Assert.Single(page.Runs);
            if (page.HasMore) Assert.NotEqual(page.Runs[0].Id, (await tu.ListRunsAsync(1, page.Runs[0].Id)).Runs[0].Id);

            var modelId = await tu.CreateModelAsync(result.RunId!, "sdk test");
            try
            {
                Assert.Equal("trueup.match-weights", (await tu.GetModelAsync(modelId)).Weights!["format"]!.GetValue<string>());
                var again = await tu.ReconcileStoredAsync(new[] { statement.Id, receiving.Id }, new StoredOptions { Model = modelId });
                Assert.False(again.Details.Model!["learned"]!.GetValue<bool>());
            }
            finally
            {
                await tu.DeleteModelAsync(modelId);
            }
            await Assert.ThrowsAsync<NotFoundException>(() => tu.GetModelAsync(modelId));
        }
        finally
        {
            await tu.DeleteFileAsync(statement.Id);
            await tu.DeleteFileAsync(receiving.Id);
        }
        await Assert.ThrowsAsync<NotFoundException>(() => tu.GetFileAsync(statement.Id));
    }

    [Fact]
    public async Task MatchTwoListsThenReuseTheLearning()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        var want = new[] { "1~1", "2~2", "3~3", "4~5" };
        static string[] Pairs(MatchResult r) => r.Details.Pairs.Select(p => $"{p[0]!.GetValue<string>()}~{p[1]!.GetValue<string>()}").ToArray();
        var result = await tu.MatchAsync(Table.File(Path.Combine(Fixtures, "invoice.csv")), Table.File(Path.Combine(Fixtures, "catalog.csv")));
        Assert.Equal("match", result.Analysis);
        Assert.Equal(want, Pairs(result));
        Assert.Equal(new[] { "5" }, result.Findings.Where(f => f.Kind == "only_left").Select(f => f.Subject).ToArray());
        var again = await tu.MatchAsync(Table.Rows("invoice.csv", Rows("invoice.csv")), Table.Rows("catalog.csv", Rows("catalog.csv")), result.Details.Weights);
        Assert.Equal(want, Pairs(again));
        Assert.False(again.Details.Model!["learned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AuditSixInvoicesThenOneAgainstTheSavedLaws()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        var files = Enumerable.Range(1, 6).Select(i => Table.File(Path.Combine(Fixtures, "invoices", $"inv-104{i}.txt")));
        var result = await tu.AuditAsync(files);
        Assert.Equal("audit", result.Analysis);
        var f = Assert.Single(result.Findings);
        Assert.Equal(("inv-1045.txt", "yes", 200.0), (f.Subject, f.Status, f.Amount!.Value));
        Assert.Contains(result.Details.Laws, l => l.Text == "subtotal + tax amount = total");
        var one = await tu.AuditAsync(new[] { Table.File(Path.Combine(Fixtures, "invoices", "inv-1045.txt")) }, result.Details.Weights);
        Assert.False(one.Details.Model!["learned"]!.GetValue<bool>());
        Assert.Equal("inv-1045.txt", Assert.Single(one.Findings).Subject);
    }

    [Fact]
    public async Task EstimateANewJobThenTheNextWithTheSavedModel()
    {
        if (!Live) return;
        var tu = new TrueUpClient();
        var names = new[] { "barndo.tu", "01_anderson.csv", "02_brooks.csv", "03_carter.md", "04_dalton.txt", "05_ellis.json",
            "06_foster.tsv", "07_garrison.txt", "08_hayes.csv", "09_iverson.csv", "10_jensen.md", "job_a.txt" };
        var result = await tu.EstimateAsync(names.Select(n => Table.File(Path.Combine(Fixtures, "barndo", n))));
        Assert.Equal("estimate", result.Analysis);
        Assert.Equal(10, result.Stats["past estimates"]);
        var total = result.Stats["total"];
        Assert.True(Math.Abs(total - 292267) / 292267 < 0.05, $"total {total}");
        Assert.True(result.Stats["low"] < total);
        var next = await tu.EstimateAsync(new[] { Table.File(Path.Combine(Fixtures, "barndo", "job_b.txt")) }, result.Details.Weights);
        Assert.False(next.Details.Model!["learned"]!.GetValue<bool>());
    }
}
