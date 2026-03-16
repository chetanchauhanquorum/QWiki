using Azure.Data.Tables;
using System.Text.Json;

namespace QWiki.Shared;

public class ChatHistoryMessage
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
}

public class ConversationSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset LastModified { get; set; }
    public int MessageCount { get; set; }
}

public class ConversationDetail : ConversationSummary
{
    public List<ChatHistoryMessage> Messages { get; set; } = [];
}

public class ChatHistoryService
{
    private readonly TableClient _tableClient;

    public ChatHistoryService(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);
        _tableClient = serviceClient.GetTableClient("ChatHistory");
        _tableClient.CreateIfNotExists();
    }

    public async Task SaveConversationAsync(string userId, string conversationId, string title, List<ChatHistoryMessage> messages)
    {
        var messagesJson = JsonSerializer.Serialize(messages);

        var entity = new TableEntity($"Chat-{userId}", conversationId)
        {
            ["Title"] = Truncate(title, 100),
            ["MessagesJson"] = messagesJson,
            ["MessageCount"] = messages.Count,
            ["LastModified"] = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<List<ConversationSummary>> GetRecentConversationsAsync(string userId, int count = 20)
    {
        var results = new List<ConversationSummary>();

        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq 'Chat-{EscapeFilter(userId)}'",
            select: new[] { "RowKey", "Title", "LastModified", "MessageCount" }))
        {
            results.Add(new ConversationSummary
            {
                Id = entity.RowKey!,
                Title = entity.GetString("Title") ?? "",
                LastModified = entity.GetDateTimeOffset("LastModified") ?? DateTimeOffset.MinValue,
                MessageCount = entity.GetInt32("MessageCount") ?? 0
            });
        }

        return results
            .OrderByDescending(c => c.LastModified)
            .Take(count)
            .ToList();
    }

    public async Task<ConversationDetail?> GetConversationAsync(string userId, string conversationId)
    {
        try
        {
            var entity = await _tableClient.GetEntityAsync<TableEntity>($"Chat-{userId}", conversationId);

            var messagesJson = entity.Value.GetString("MessagesJson") ?? "[]";
            var messages = JsonSerializer.Deserialize<List<ChatHistoryMessage>>(messagesJson) ?? [];

            return new ConversationDetail
            {
                Id = conversationId,
                Title = entity.Value.GetString("Title") ?? "",
                LastModified = entity.Value.GetDateTimeOffset("LastModified") ?? DateTimeOffset.MinValue,
                MessageCount = entity.Value.GetInt32("MessageCount") ?? 0,
                Messages = messages
            };
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteConversationAsync(string userId, string conversationId)
    {
        try
        {
            await _tableClient.DeleteEntityAsync($"Chat-{userId}", conversationId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static string EscapeFilter(string value)
        => value.Replace("'", "''");
}
