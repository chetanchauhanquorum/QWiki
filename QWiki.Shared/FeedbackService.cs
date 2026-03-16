using Azure.Data.Tables;

namespace QWiki.Shared;

public class FeedbackEntry
{
    public string ConversationId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserQuery { get; set; } = "";
    public string AssistantResponse { get; set; } = "";
    public bool IsPositive { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class FeedbackService
{
    private readonly TableClient _tableClient;

    public FeedbackService(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);
        _tableClient = serviceClient.GetTableClient("Feedback");
        _tableClient.CreateIfNotExists();
    }

    public async Task SaveFeedbackAsync(FeedbackEntry entry)
    {
        // Reverse-ticks RowKey for recent-first ordering
        var reverseTicks = (DateTimeOffset.MaxValue.Ticks - entry.Timestamp.Ticks).ToString("D19");

        var entity = new TableEntity("Feedback", reverseTicks)
        {
            ["ConversationId"] = entry.ConversationId,
            ["UserId"] = entry.UserId,
            ["UserQuery"] = Truncate(entry.UserQuery, 500),
            ["AssistantResponse"] = Truncate(entry.AssistantResponse, 500),
            ["IsPositive"] = entry.IsPositive,
            ["Comment"] = entry.Comment ?? "",
            ["Timestamp"] = entry.Timestamp
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<List<FeedbackEntry>> GetRecentFeedbackAsync(int count = 50)
    {
        var results = new List<FeedbackEntry>();

        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: "PartitionKey eq 'Feedback'",
            maxPerPage: count))
        {
            results.Add(new FeedbackEntry
            {
                ConversationId = entity.GetString("ConversationId") ?? "",
                UserId = entity.GetString("UserId") ?? "",
                UserQuery = entity.GetString("UserQuery") ?? "",
                AssistantResponse = entity.GetString("AssistantResponse") ?? "",
                IsPositive = entity.GetBoolean("IsPositive") ?? false,
                Comment = entity.GetString("Comment"),
                Timestamp = entity.GetDateTimeOffset("Timestamp") ?? DateTimeOffset.MinValue
            });

            if (results.Count >= count) break;
        }

        return results;
    }

    public async Task<(int Positive, int Negative)> GetFeedbackCountsAsync()
    {
        int positive = 0, negative = 0;

        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: "PartitionKey eq 'Feedback'",
            select: new[] { "IsPositive" }))
        {
            if (entity.GetBoolean("IsPositive") == true)
                positive++;
            else
                negative++;
        }

        return (positive, negative);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
