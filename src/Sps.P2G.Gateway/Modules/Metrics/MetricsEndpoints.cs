using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sps.P2G.Gateway.Infrastructure.Database;

namespace Sps.P2G.Gateway.Modules.Metrics;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (GatewayDbContext db, CancellationToken cancellationToken) =>
        {
            var builder = new StringBuilder();
            await AppendGroupedCountAsync(builder, "sps_p2g_bills_total", "Bills by projected status.", "status",
                db.Bills.GroupBy(x => x.StatusProjection).Select(x => new MetricGroup(x.Key, x.Count())),
                cancellationToken);
            await AppendScalarAsync(builder, "sps_p2g_payments_total", "Accepted payments.", db.BillPayments.CountAsync(cancellationToken));
            await AppendGroupedCountAsync(builder, "sps_p2g_payment_rejections_total", "Rejected payment attempts by reason.", "reason",
                db.BillPaymentAttempts.Where(x => x.Status == "RJCT").GroupBy(x => x.Reason).Select(x => new MetricGroup(x.Key, x.Count())),
                cancellationToken);
            await AppendGroupedCountAsync(builder, "sps_p2g_callback_deliveries_total", "Callback deliveries by status.", "status",
                db.CallbackDeliveries.GroupBy(x => x.Status).Select(x => new MetricGroup(x.Key, x.Count())),
                cancellationToken);
            await AppendGroupedCountAsync(builder, "sps_p2g_outbox_messages_total", "Outbox messages by status.", "status",
                db.OutboxMessages.GroupBy(x => x.Status).Select(x => new MetricGroup(x.Key, x.Count())),
                cancellationToken);

            return Results.Text(builder.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        })
            .WithName("Metrics")
            .WithTags("Metrics");

        return app;
    }

    private static async Task AppendScalarAsync(StringBuilder builder, string name, string help, Task<int> valueTask)
    {
        AppendHelpAndType(builder, name, help);
        builder.Append(name).Append(' ').Append((await valueTask).ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static async Task AppendGroupedCountAsync(
        StringBuilder builder,
        string name,
        string help,
        string labelName,
        IQueryable<MetricGroup> query,
        CancellationToken cancellationToken)
    {
        AppendHelpAndType(builder, name, help);
        var rows = await query.ToListAsync(cancellationToken);
        foreach (var row in rows.OrderBy(x => x.Label, StringComparer.Ordinal))
        {
            builder
                .Append(name)
                .Append('{')
                .Append(labelName)
                .Append("=\"")
                .Append(EscapeLabel(row.Label))
                .Append("\"} ")
                .Append(row.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }

    private static void AppendHelpAndType(StringBuilder builder, string name, string help)
    {
        builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        builder.Append("# TYPE ").Append(name).Append(" gauge\n");
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record MetricGroup(string Label, int Count);
}
