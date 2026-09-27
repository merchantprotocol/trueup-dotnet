# TrueUp for .NET

The official client for the [TrueUp API](https://trueup-cloud.merchantprotocol.workers.dev/docs). Send TrueUp two ledgers (a supplier statement and your receiving log, your books and the bank feed, invoices and payments) and it pairs every row, then tells you what's only on one side, what was counted twice and where the numbers disagree.

.NET 6 and 8. No dependencies.

## Install

```bash
dotnet add package TrueUp
```

## Quickstart

Create an API key in the TrueUp dashboard (**API keys**), then set `TRUEUP_API_KEY`:

```csharp
using TrueUp;

var trueup = new TrueUpClient(); // reads TRUEUP_API_KEY

// left: the side that bills or claims; right: the other side
var result = await trueup.ReconcileAsync(Table.File("statement.csv"), Table.File("receiving.csv"));

Console.WriteLine(result.Headline);
// 7 of 8 rows of statement.csv paired with receiving.csv; 1 only in statement.csv, ...
foreach (var f in result.Findings)
    Console.WriteLine($"{f.Kind} {f.Subject} {f.Detail} {f.Amount}");
// qty_mismatch statement.csv:row 5 Qty 24 vs qty_received 20; ... 99.6
// phantom statement.csv:row 6 no match on the other side 43.2
```

## Reconcile

A table is a file, file contents, or rows:

```csharp
Table.File("books.csv");
Table.Content("books.csv", csvText);
Table.Rows("invoices", new[] {
    new Dictionary<string, object?> { ["Invoice #"] = "INV-10101", ["Date"] = "2026-06-09", ["Total"] = "$2,999.31" },
});
```

CSV, TSV, JSON and JSON Lines are read, and date and number formats are detected. Nothing about the columns is configured.

Not sure which file is which? `await trueup.ReconcileFilesAsync(new[] { Table.File("a.csv"), Table.File("b.csv") })` picks the pair and the sides.

**Reuse what was learned** by passing an earlier result's weights, and **answer the questions** TrueUp wasn't sure about:

```csharp
var march = await trueup.ReconcileAsync(Table.File("march-statement.csv"), Table.File("march-receiving.csv"));
var april = await trueup.ReconcileAsync(Table.File("april-statement.csv"), Table.File("april-receiving.csv"),
    new ReconcileOptions { Weights = march.Details.Weights });

await trueup.ReconcileAsync(Table.File("statement.csv"), Table.File("receiving.csv"), new ReconcileOptions
{
    Answers = new Answers
    {
        Same = new() { new[] { "statement.csv:row 12", "receiving.csv:row 11" } },
        Different = new() { new[] { "statement.csv:row 3", "receiving.csv:row 9" } },
    },
});
```

Each call to `ReconcileAsync` or `ReconcileFilesAsync` counts as one analysis on your plan.

## Match

Two lists that describe the same things in different words (two catalogs, a supplier's price book and your invoice, two vendor lists): every record on the left is paired with its counterpart on the right, or reported as having none. Nothing is configured; the columns can have different names.

```csharp
var result = await trueup.MatchAsync(Table.File("invoice.csv"), Table.File("catalog.csv"));
Console.WriteLine(result.Headline);
// 4 of 5 records in invoice.csv matched to catalog.csv (0 unsure); 1 have no counterpart.
foreach (var f in result.Findings) Console.WriteLine($"{f.Kind} {f.Subject} {f.Detail}");
// match 4 ~ 5 4 · cheese puffs jumbo 8oz · 3.30 · 10  ↔  C-105 · Cheese Puffs Jumbo 8 oz · 3.25
// only_left 5 5 · beef jerky teriyaki 2.5oz · 5.75 · 6
```

`Kind` is `match`, `unsure_match` (a person should check), `only_left` or `only_right`. `Details.Pairs` lists `[left id, right id, confidence]`. Like `ReconcileAsync`, it takes `Table.File`, `Table.Content` or `Table.Rows`; `MatchFilesAsync` picks the pair; `MatchStoredAsync` works on stored files (with a saved model); and `Details.Weights` can be passed back to `MatchAsync(left, right, weights)` to match next month's lists the same way. One analysis per call.

## Audit

Find what doesn't add up. Send text documents with labeled amounts (invoices, statements, schedules; about 4 or more of a kind) and TrueUp learns the arithmetic each kind obeys from the documents themselves, then flags the ones that break it. Send one table and it checks its rows the same way (qty × unit price = amount), and flags repeated rows.

```csharp
var files = Enumerable.Range(1, 6).Select(i => Table.File($"inv-104{i}.txt"));
var result = await trueup.AuditAsync(files);
Console.WriteLine(result.Headline);
// 1 of 6 documents don't add up; 0 more to review (5 laws learned).
foreach (var f in result.Findings) Console.WriteLine($"{f.Subject} {f.Amount} {f.Detail}");
// inv-1045.txt 200 subtotal + tax amount = total: 4,837.84 vs 5,037.84

// Next month, even one invoice at a time, against the same laws:
await trueup.AuditAsync(new[] { Table.File("inv-1050.txt") }, result.Details.Weights);
```

`AuditStoredAsync(fileIds, model)` audits stored files. One analysis per call.

## Stored files, runs and saved models

Files uploaded to your team stay there (you'll also see them in the dashboard). Runs on stored files are kept, and what a run learned can be saved as a model:

```csharp
var files = await trueup.UploadFilesAsync(new[] { Table.File("statement.csv"), Table.File("receiving.csv") });
var (statement, receiving) = (files[0], files[1]);   // .Rows, .Columns, .Roles ("Qty" -> "number", ...)

var result = await trueup.ReconcileStoredAsync(statement.Id, receiving.Id);
var modelId = await trueup.CreateModelAsync(result.RunId!, "Acme statements");

// Next month: apply what was learned.
await trueup.ReconcileStoredAsync(new[] { aprilStatement.Id, aprilReceiving.Id }, new StoredOptions { Model = modelId });
```

| Method | Returns |
|---|---|
| `UploadFilesAsync(tables)`, `ListFilesAsync()`, `GetFileAsync(id)` | `StoredFile`: `Id`, `Name`, `Rows`, `Columns`, `Roles` |
| `FileContentAsync(id)` | the bytes, exactly as uploaded |
| `DeleteFileAsync(id)` | |
| `ReconcileStoredAsync(leftId, rightId, options)`, `ReconcileStoredAsync(fileIds, options)` | a result with `RunId` (one analysis) |
| `ListRunsAsync(limit, before)` | `RunPage`: `Runs`, `HasMore`, newest first |
| `AllRunsAsync()` | every run (`await foreach`, pages for you) |
| `GetRunAsync(id)` | `RunDetail`: `Run`, `Result` |
| `CreateModelAsync(runId, name)`, `ListModelsAsync()`, `GetModelAsync(id)`, `DeleteModelAsync(id)` | `GetModelAsync` includes the `Weights` |

## Findings

| `Kind` | Meaning |
|---|---|
| `phantom` | Only on the left: billed or recorded, never matched |
| `unbilled` | Only on the right: received or paid, never billed |
| `duplicate`, `received_duplicate` | A copy of a row that's already paired |
| `qty_mismatch`, `price_change`, `amount_mismatch` | Paired rows whose numbers disagree |
| `unsure_pair` | A likely pair a person should confirm |

## Account and usage

```csharp
var account = await trueup.AccountAsync();
var usage = await trueup.UsageAsync();
var plans = await trueup.PlansAsync();
```

## Errors

Every error is a `TrueUpException` with `Status` and `Code` (the API's error code):

| Class | When |
|---|---|
| `AuthenticationException` | 401: missing, unknown or revoked key |
| `InvalidRequestException` | 400, 413, 415, 422: the request or the files need fixing (`unsupported_file`, `not_reconcilable`, ...) |
| `RateLimitException` | 429 `rate_limited`: retried automatically; `RetryAfter` |
| `QuotaExceededException` | 429 `quota_exceeded`: the plan's monthly allowance is used up |
| `ServerException` | 5xx: retried automatically |
| `ConnectionException` | the API couldn't be reached |

## Configuration

```csharp
new TrueUpClient(new TrueUpOptions
{
    ApiKey = "tu_live_...",                 // default: TRUEUP_API_KEY
    BaseUrl = "https://...",                // default: TRUEUP_BASE_URL, then the hosted API
    Timeout = TimeSpan.FromMinutes(5),      // per request
    MaxRetries = 2,                         // rate limits, 5xx and dropped connections
});
```

## Development

The tests run in Docker against the live API:

```bash
export TRUEUP_API_KEY=tu_live_...   # a key for a test team (each run uses 8 analyses)
just test                            # or: docker compose run --rm test
```

## License

MIT
