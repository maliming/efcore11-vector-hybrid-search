using HelpCenterSearch.Models;
using HelpCenterSearch.Services;
using Microsoft.EntityFrameworkCore;

namespace HelpCenterSearch.Data;

public static class SeedData
{
    public static async Task EnsureSeededAsync(HelpCenterDbContext db, IEmbeddingGenerator embeddings, ILogger logger)
    {
        if (!await db.Articles.AnyAsync())
        {
            var id = 0;
            foreach (var (topic, articles) in Corpus)
            {
                foreach (var (title, body) in articles)
                {
                    id++;
                    db.Articles.Add(new Article
                    {
                        Id = id,
                        Title = title,
                        Content = body,
                        Topic = topic,
                        Embedding = embeddings.Generate($"{title} {body}")
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        await WaitForFullTextPopulationAsync(db, await db.Articles.CountAsync(), logger);
    }

    /// <summary>
    ///     A full-text index populates asynchronously, so a search right after seeding finds nothing.
    ///     The deadline matters: rows that fail to index keep the count below the total forever.
    /// </summary>
    private static async Task WaitForFullTextPopulationAsync(HelpCenterDbContext db, int expected, ILogger logger)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline)
        {
            var indexed = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('Articles'), 'TableFulltextItemCount') AS int) AS [Value]")
                .SingleAsync();

            var failed = await db.Database
                .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('Articles'), 'TableFulltextFailCount') AS int) AS [Value]")
                .SingleAsync();

            if (failed > 0)
            {
                logger.LogWarning(
                    "Full-text population reported {Failed} failed rows; keyword results will be incomplete", failed);
                return;
            }

            if (indexed >= expected)
            {
                logger.LogInformation("Full-text index holds all {Expected} rows", expected);
                return;
            }

            logger.LogInformation("Waiting for full-text population: {Indexed} of {Expected} rows", indexed, expected);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        logger.LogWarning("Full-text population did not finish in time; keyword results may be incomplete");
    }

    /// <summary>
    ///     The "reporting" topic deliberately uses authentication words ("session", "sign in") in an
    ///     unrelated sense: those are the false friends keyword search returns and vectors veto.
    /// </summary>
    private static readonly (string Topic, (string Title, string Body)[] Articles)[] Corpus =
    [
        ("authentication",
        [
            ("Session expires too quickly", "Your session token expires after the idle timeout configured for the workspace. Increase the session lifetime or enable sliding expiration so people stay signed in."),
            ("Signed out after 30 minutes of inactivity", "The authentication cookie has a fixed lifetime. Users are returned to the sign in page once that lifetime is reached."),
            ("Password reset email never arrives", "Password reset messages are queued through the outbound mail provider. Check the provider logs before changing authentication settings."),
            ("Two factor codes are rejected", "Time based codes fail when the server clock drifts. Synchronise the clock before regenerating the authentication secret."),
            ("Users cannot sign in after a workspace rename", "The sign in flow resolves the workspace from the host name. Update the mapping so authentication keeps working."),
            ("Single sign on loops back to the login page", "A redirect loop happens when the token audience does not match. Align the audience and the loop stops."),
            ("Token refresh fails silently", "A refresh token that has already been used is rejected. The client must handle the failure and send the person back to sign in."),
            ("Remember me does not keep me logged in", "Persistent cookies are dropped when the browser blocks third party storage, so the session ends when the tab closes."),
        ]),
        ("billing",
        [
            ("Invoice shows the wrong billing period", "Invoices are generated from the subscription anchor date. Changing the plan mid cycle moves the billing period."),
            ("Refund has not appeared on the card", "Refunds settle through the payment provider and can take several working days to reach the card statement."),
            ("Subscription renewal failed", "Renewal fails when the stored card has expired. Update the card and the subscription renews on the next attempt."),
            ("Adding seats in the middle of a cycle", "Extra seats are prorated for the remainder of the billing period and appear as a separate line on the next invoice."),
            ("Changing the billing contact", "The billing contact receives every invoice and payment notice. It is independent of the account owner."),
            ("VAT number is missing from the invoice", "Add the VAT number to the billing profile before the invoice is issued; issued invoices cannot be edited."),
            ("Payment retries after a declined card", "A declined payment is retried three times over six days before the subscription is suspended."),
            ("Downgrade takes effect at renewal", "Plan downgrades apply at the start of the next billing period so the paid period is not lost."),
        ]),
        ("reporting",
        [
            ("Scheduling a photo session export", "Set up a recurring export of photo session metadata. Each session becomes one row in the generated CSV file."),
            ("Sign in sheet export template", "The sign in sheet template exports attendee names and arrival times. It has nothing to do with account authentication."),
            ("Export times out on large reports", "Large exports are streamed in batches. Raise the export timeout when a report covers more than a year of data."),
            ("CSV columns are in the wrong order", "Column order follows the report template. Edit the template to reorder columns in every future export."),
            ("Scheduled report did not run", "Scheduled reports are skipped while the previous run is still in progress. Check the run history before rescheduling."),
            ("Downloading a report as PDF", "Any report can be downloaded as PDF from the report toolbar. The layout follows the current column selection."),
            ("Filtering a report by custom fields", "Custom fields become available as report filters once they are marked reportable in the field settings."),
            ("Export file is empty", "An empty export usually means the filter matched no rows. Widen the date range and run the export again."),
        ]),
        ("api",
        [
            ("Webhook deliveries are being retried", "Failed webhook deliveries are retried with exponential backoff for up to 24 hours before they are dropped."),
            ("Rate limit returns 429", "The API applies a per token rate limit. Back off and retry using the interval in the Retry-After header."),
            ("Verifying the webhook signature", "Every webhook payload is signed. Compare the signature header against an HMAC of the raw request body."),
            ("Pagination cursors expire", "Cursors stay valid for fifteen minutes. Request a fresh first page when a cursor is rejected."),
            ("Sandbox endpoint returns stale data", "Sandbox data is refreshed nightly. Use the production endpoint when current data matters."),
            ("Bulk endpoint payload size limit", "Bulk requests are limited to one megabyte. Split larger payloads across several requests."),
            ("Rotating an API token without downtime", "Create the new token, deploy it, then revoke the old one. Both are accepted during the overlap."),
            ("Idempotency keys for retried requests", "Send an idempotency key so a retried request does not create a duplicate record."),
        ]),
    ];
}
