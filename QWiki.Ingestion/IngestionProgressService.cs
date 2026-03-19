namespace QWiki.Ingestion;

public class SourceProgress
{
    public string SourceName { get; set; } = "";
    public int Processed { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public bool IsComplete { get; set; }
}

public class RecentFile
{
    public string FileName { get; set; } = "";
    public bool Success { get; set; }
}

/// <summary>
/// Thread-safe singleton that holds current ingestion state.
/// Written to by the ingestion pipeline, read by the admin page via polling.
/// When an AzureTableProgressStore is attached, state is persisted to Azure Table Storage
/// so that a separate UI process can read it (production deployment).
/// </summary>
public class IngestionProgressService : IDisposable
{
    private readonly object _lock = new();
    private AzureTableProgressStore? _store;
    private Timer? _flushTimer;
    private bool _dirty;

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

    private readonly List<RecentFile> _recentFiles = [];
    public IReadOnlyList<RecentFile> RecentFiles
    {
        get { lock (_lock) { return _recentFiles.ToList(); } }
    }

    /// <summary>
    /// Attaches an Azure Table backing store for cross-process progress sharing.
    /// When enableWriteThrough is true, state changes are periodically flushed to the table.
    /// When false, the store is available for reading only (UI side).
    /// </summary>
    public void AttachStore(AzureTableProgressStore store, bool enableWriteThrough = false)
    {
        if (_store != null) return;
        _store = store;
        if (enableWriteThrough)
        {
            _flushTimer = new Timer(_ => FlushIfDirty(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
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
            _recentFiles.Clear();
            _dirty = true;
        }
        FlushImmediate();
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
            _dirty = true;
        }
        FlushImmediate();
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
            _dirty = true;
        }
        FlushImmediate();
    }

    public void FileStarted(string fileName)
    {
        lock (_lock)
        {
            CurrentFile = fileName;
            _dirty = true;
        }
    }

    public void FileCompleted(bool success = true)
    {
        lock (_lock)
        {
            _recentFiles.Add(new RecentFile { FileName = CurrentFile, Success = success });
            if (_recentFiles.Count > 20) _recentFiles.RemoveAt(0);
            ProcessedFiles++;
            _dirty = true;
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
            _dirty = true;
        }
        FlushImmediate();
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
            _dirty = true;
        }
        FlushImmediate();
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
            _dirty = true;
        }
        FlushImmediate();
    }

    /// <summary>
    /// Loads state from the table store into local properties (UI-side polling).
    /// No-op if write-through is enabled (we are the writer, local state is authoritative).
    /// </summary>
    public async Task LoadFromStoreAsync()
    {
        if (_store == null || _flushTimer != null) return;

        var snapshot = await _store.LoadAsync();
        if (snapshot == null) return;

        lock (_lock)
        {
            IsRunning = snapshot.IsRunning;
            CurrentSource = snapshot.CurrentSource;
            CurrentFile = snapshot.CurrentFile;
            TotalFiles = snapshot.TotalFiles;
            ProcessedFiles = snapshot.ProcessedFiles;
            Phase = snapshot.Phase;
            StartedAt = snapshot.StartedAt;
            CompletedAt = snapshot.CompletedAt;
            _sourceResults.Clear();
            _sourceResults.AddRange(snapshot.SourceResults);
            _recentFiles.Clear();
            _recentFiles.AddRange(snapshot.RecentFiles);
        }
    }

    private void FlushIfDirty()
    {
        IngestionProgressSnapshot? snapshot = null;
        lock (_lock)
        {
            if (!_dirty || _store == null) return;
            _dirty = false;
            snapshot = TakeSnapshot();
        }
        if (snapshot != null)
        {
            _ = FlushAsync(snapshot);
        }
    }

    private void FlushImmediate()
    {
        if (_store == null) return;
        IngestionProgressSnapshot snapshot;
        lock (_lock)
        {
            _dirty = false;
            snapshot = TakeSnapshot();
        }
        _ = FlushAsync(snapshot);
    }

    private async Task FlushAsync(IngestionProgressSnapshot snapshot)
    {
        try
        {
            await _store!.SaveAsync(snapshot);
        }
        catch
        {
            // Best-effort — next flush will retry
        }
    }

    private IngestionProgressSnapshot TakeSnapshot()
    {
        return new IngestionProgressSnapshot
        {
            IsRunning = IsRunning,
            CurrentSource = CurrentSource,
            CurrentFile = CurrentFile,
            TotalFiles = TotalFiles,
            ProcessedFiles = ProcessedFiles,
            Phase = Phase,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            SourceResults = _sourceResults.ToList(),
            RecentFiles = _recentFiles.ToList()
        };
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }
}
