namespace QWiki.Ingestion;

public class SourceProgress
{
    public string SourceName { get; set; } = "";
    public int Processed { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public bool IsComplete { get; set; }
}

/// <summary>
/// Thread-safe singleton that holds current ingestion state.
/// Written to by the ingestion pipeline, read by the admin page via polling.
/// </summary>
public class IngestionProgressService
{
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public string CurrentSource { get; private set; } = "";
    public string CurrentFile { get; private set; } = "";
    public int TotalFiles { get; private set; }
    public int ProcessedFiles { get; private set; }
    public string Phase { get; private set; } = "";
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private readonly List<SourceProgress> _sourceResults = [];
    public IReadOnlyList<SourceProgress> SourceResults
    {
        get { lock (_lock) { return _sourceResults.ToList(); } }
    }

    public void StartIngestion()
    {
        lock (_lock)
        {
            IsRunning = true;
            StartedAt = DateTimeOffset.UtcNow;
            CompletedAt = null;
            CurrentSource = "";
            CurrentFile = "";
            TotalFiles = 0;
            ProcessedFiles = 0;
            Phase = "Starting";
            _sourceResults.Clear();
        }
    }

    public void SetDiscovering(string sourceName)
    {
        lock (_lock)
        {
            CurrentSource = sourceName;
            CurrentFile = "";
            TotalFiles = 0;
            ProcessedFiles = 0;
            Phase = "Discovering";
        }
    }

    public void SetProcessing(string sourceName, int totalFiles)
    {
        lock (_lock)
        {
            CurrentSource = sourceName;
            TotalFiles = totalFiles;
            ProcessedFiles = 0;
            CurrentFile = "";
            Phase = totalFiles > 0 ? "Processing" : "Up to date";
        }
    }

    public void FileStarted(string fileName)
    {
        lock (_lock)
        {
            CurrentFile = fileName;
        }
    }

    public void FileCompleted()
    {
        lock (_lock)
        {
            ProcessedFiles++;
        }
    }

    public void SourceCompleted(string sourceName, int processed, int skipped, int errors)
    {
        lock (_lock)
        {
            _sourceResults.Add(new SourceProgress
            {
                SourceName = sourceName,
                Processed = processed,
                Skipped = skipped,
                Errors = errors,
                IsComplete = true
            });
            CurrentFile = "";
            Phase = "Complete";
        }
    }

    public void SourceFailed(string sourceName, string error)
    {
        lock (_lock)
        {
            _sourceResults.Add(new SourceProgress
            {
                SourceName = sourceName,
                Errors = 1,
                IsComplete = true
            });
            CurrentFile = "";
            Phase = "Error";
        }
    }

    public void IngestionCompleted()
    {
        lock (_lock)
        {
            IsRunning = false;
            CompletedAt = DateTimeOffset.UtcNow;
            CurrentSource = "";
            CurrentFile = "";
            Phase = "Complete";
        }
    }
}
