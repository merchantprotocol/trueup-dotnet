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
export TRUEUP_API_KEY=tu_live_...   # a key for a test team (each run uses 2 analyses)
just test                            # or: docker compose run --rm test
```

## License

MIT
