# Background Jobs

Long operations started by a user run as **background jobs**, so the user can keep working or close the page. Examples are importing a large file and submitting or deleting thousands of documents. **Background jobs** in the sidebar (`/jobs`) lists them with live progress, and the bell notifies the user when a job ends.

## For users

- **Imports.** In the import wizard (step 3), *Run in the background* is ticked automatically for files of 500 rows or more; untick it to import while you wait. The job reads the uploaded file again and uses the column mapping you chose. When rows fail, the job's **Download** gives the failure report (the same file as in the wizard).
- **Progress.** `/jobs` shows the progress bar and step ("1,250 of 10,000 rows") and updates by itself while a job runs.
- **Cancel.** A queued job is cancelled at once. A running job stops at its next step; what was saved before stays saved (for an "all or nothing" import, nothing is saved).
- **Run again.** Finished jobs that did not succeed can be queued again with the same parameters and file.
- Administrators can tick **All users** to see every job of the company.

## Behaviour

| | |
|---|---|
| Permissions | A job runs as the user who queued it, with their roles **at run time**: a user who lost a role meanwhile cannot finish the job. Checks are the same as in the UI (import permission, submit, delete, row scope). |
| Workers | Each application instance runs `Tasks:Workers` jobs at once (default 2). It is woken when a job is queued and polls every `Tasks:PollIntervalSeconds` (default 3) for jobs queued on other instances. In PostgreSQL a job is claimed with `FOR UPDATE SKIP LOCKED`, so with several instances each job runs exactly once. |
| Progress | Saved at most about once a second. The worker also renews its lease every 20 seconds. |
| Server stopped | A job whose worker stopped (lease of 2 minutes expired) becomes **Interrupted**, and its user is notified. It is **not** re-run automatically, because the work may be half done (an import may have created some documents). Check the results, then use *Run again* if needed. |
| Errors | Validation and permission errors are shown as they are. Unexpected errors show a general message; the details are in the server log. |
| Limits | 5 queued or running jobs per user; inputs up to 20 MB. Bulk actions handle up to 50,000 documents. |
| Retention | Finished jobs, including their input and output files, are deleted after 14 days. |

## API

| Method | Path | |
|---|---|---|
| GET | `/api/jobs/me[?all=true]` | your jobs (all users for administrators with `all=true`) |
| GET | `/api/jobs/{id}` | one job: status, progress, result, error |
| POST | `/api/jobs/{id}/cancel` | cancel |
| POST | `/api/jobs/{id}/run-again` | queue again; returns 202 with the new job |
| GET | `/api/jobs/{id}/output` | the output file, if any |
| POST | `/api/data/{doctype}/bulk` | body `{"action": "submit" \| "cancel" \| "delete", "names": [...], "comment": "..."}`; returns 202 with the job; output lists failures (CSV) |

## For developers: adding a kind of job

```csharp
public sealed class RecalculatePricesHandler(DocumentService docs) : IBackgroundTaskHandler
{
    public string Kind => "recalculate-prices";

    public async Task<BackgroundTaskOutcome> RunAsync(BackgroundTaskContext ctx, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<MyPayload>(ctx.Task.Payload)!;
        for (var i = 0; i < p.Items.Count; i++)
        {
            // ... work as ctx.User (permissions are checked by DocumentService) ...
            await ctx.ReportAsync((i + 1) * 100 / p.Items.Count, $"{i + 1} of {p.Items.Count}", ct); // also stops on cancel
        }
        return new BackgroundTaskOutcome($"{p.Items.Count} prices recalculated.");
    }
}

// Registration:
services.AddSingleton<IBackgroundTaskHandler, RecalculatePricesHandler>();

// Queue it for the current user (e.g. from a page or a form action):
await backgroundTasks.EnqueueAsync(user, "recalculate-prices", "Recalculate prices", JsonSerializer.Serialize(payload));
```

Write handlers so that a second run does no harm, and report progress regularly: cancel and the progress bar depend on it.

Storage: table `frogframe_background_tasks` (migration `041_background_tasks.sql`).
