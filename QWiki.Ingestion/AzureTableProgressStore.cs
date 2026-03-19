using System.Text.Json;
using Azure.Data.Tables;

namespace QWiki.Ingestion;

public class IngestionProgressSnapshot
{
    public bool IsRunning { get; set; }
    public string CurrentSource { get; set; } = "";
    public string CurrentFile { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string Phase { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<SourceProgress> SourceResults { get; set; } = [];
    public List<RecentFile> RecentFiles { get; set; } = [];
}

/// <summary>
/// Persists ingestion progress to Azure Table Storage so the UI process
/// can read real-time progress written by the Worker process.
/// Single row: PartitionKey="Progress", RowKey="current".
/// </summary>
public class AzureTableProgressStore
{
    private readonly TableClient _tableClient;

    public AzureTableProgressStore(string connectionString)
    {
        var serviceClient = new TableServiceClient(connectionString);
        _tableClient = serviceClient.GetTableClient("IngestionProgress");
        _tableClient.CreateIfNotExists();
    }

    public async Task SaveAsync(IngestionProgressSnapshot snapshot)
    {
        var entity = new TableEntity("Progress", "current")
        {
            ["IsRunning"] = snapshot.IsRunning,
            ["CurrentSource"] = snapshot.CurrentSource,
            ["CurrentFile"] = snapshot.CurrentFile,
            ["TotalFiles"] = snapshot.TotalFiles,
            ["ProcessedFiles"] = snapshot.ProcessedFiles,
            ["Phase"] = snapshot.Phase,
            ["StartedAt"] = snapshot.StartedAt,
            ["CompletedAt"] = snapshot.CompletedAt,
            ["SourceResultsJson"] = JsonSerializer.Serialize(snapshot.SourceResults),
            ["RecentFilesJson"] = JsonSerializer.Serialize(snapshot.RecentFiles),
            ["LastUpdated"] = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<IngestionProgressSnapshot?> LoadAsync()
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>("Progress", "current");
            var entity = response.Value;

            var sourceResultsJson = entity.GetString("SourceResultsJson") ?? "[]";
            var sourceResults = JsonSerializer.Deserialize<List<SourceProgress>>(sourceResultsJson) ?? [];
            var recentFilesJson = entity.GetString("RecentFilesJson") ?? "[]";
            var recentFiles = JsonSerializer.Deserialize<List<RecentFile>>(recentFilesJson) ?? [];

            return new IngestionProgressSnapshot
            {
                IsRunning = entity.GetBoolean("IsRunning") ?? false,
                CurrentSource = entity.GetString("CurrentSource") ?? "",
                CurrentFile = entity.GetString("CurrentFile") ?? "",
                TotalFiles = entity.GetInt32("TotalFiles") ?? 0,
                ProcessedFiles = entity.GetInt32("ProcessedFiles") ?? 0,
                Phase = entity.GetString("Phase") ?? "",
                StartedAt = entity.GetDateTimeOffset("StartedAt"),
                CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
                SourceResults = sourceResults,
                RecentFiles = recentFiles
            };
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
