using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xabe.FFmpeg;

namespace QWiki.Ingestion;

public record TranscriptSegment(TimeSpan Offset, string Text);

/// <summary>
/// Transcribes video files to text using Azure AI Speech SDK.
/// Extracts audio via FFmpeg, then runs continuous speech recognition.
/// Returns timestamped segments for each recognized phrase.
/// Caches transcripts in Azure Blob Storage to avoid re-transcription on crash/restart.
/// </summary>
public class AudioTranscriber
{
    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly ILogger<AudioTranscriber> _logger;
    private readonly BlobContainerClient? _cacheContainer;
    private bool _ffmpegInitialized;

    public AudioTranscriber(IConfiguration configuration, ILogger<AudioTranscriber> logger)
    {
        _logger = logger;
        _speechKey = configuration["AzureSpeech:Key"]
            ?? throw new InvalidOperationException(
                "Missing AzureSpeech:Key. Use 'dotnet user-secrets set AzureSpeech:Key YOUR-KEY'.");
        _speechRegion = configuration["AzureSpeech:Region"]
            ?? throw new InvalidOperationException("Missing AzureSpeech:Region in appsettings.json.");

        var storageCs = configuration["AzureStorage:ConnectionString"];
        if (!string.IsNullOrEmpty(storageCs))
        {
            var blobServiceClient = new BlobServiceClient(storageCs);
            _cacheContainer = blobServiceClient.GetBlobContainerClient("transcript-cache");
            _cacheContainer.CreateIfNotExists();
        }
    }

    /// <summary>
    /// Transcribes a video file (.mp4/.mkv) to timestamped text segments.
    /// If cacheKey and version are provided, checks Azure Blob Storage for a cached transcript first.
    /// Returns empty list if transcription fails or produces no output.
    /// </summary>
    public async Task<List<TranscriptSegment>> TranscribeVideoAsync(
        string videoFilePath, string? cacheKey = null, string? version = null)
    {
        // Check transcript cache first
        if (cacheKey != null && version != null)
        {
            var cached = await TryLoadCachedTranscriptAsync(cacheKey, version);
            if (cached != null)
            {
                _logger.LogInformation("Using cached transcript for {Video}: {Segments} segments, {Chars} characters",
                    Path.GetFileName(videoFilePath), cached.Count, cached.Sum(s => s.Text.Length));
                return cached;
            }
        }

        await EnsureFfmpegAsync();

        var wavPath = Path.Combine(Path.GetTempPath(), $"qwiki-audio-{Guid.NewGuid()}.wav");

        try
        {
            // Step 1: Extract audio from video → WAV (16kHz mono PCM)
            _logger.LogInformation("Extracting audio from {Video}...", Path.GetFileName(videoFilePath));
            await ExtractAudioAsync(videoFilePath, wavPath);

            if (!File.Exists(wavPath) || new FileInfo(wavPath).Length == 0)
            {
                _logger.LogWarning("Audio extraction produced empty file for {Video}", videoFilePath);
                return [];
            }

            // Step 2: Transcribe WAV using Azure Speech SDK
            _logger.LogInformation("Transcribing audio from {Video}...", Path.GetFileName(videoFilePath));
            var segments = await RecognizeSpeechAsync(wavPath);

            var totalChars = segments.Sum(s => s.Text.Length);
            _logger.LogInformation("Transcription complete for {Video}: {Segments} segments, {Length} characters",
                Path.GetFileName(videoFilePath), segments.Count, totalChars);

            // Step 3: Save to cache immediately after successful transcription
            if (cacheKey != null && version != null && segments.Count > 0)
            {
                await SaveTranscriptToCacheAsync(cacheKey, version, segments);
            }

            return segments;
        }
        finally
        {
            // Clean up temp WAV file
            if (File.Exists(wavPath))
            {
                try { File.Delete(wavPath); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    // --- Transcript cache ---

    private record TranscriptCacheEntry(string Version, List<CachedSegment> Segments);
    private record CachedSegment(long OffsetTicks, string Text);

    private static string SanitizeBlobName(string key)
        => key.Replace("/", "-").Replace("\\", "-").Replace("#", "-").Replace("?", "-");

    private async Task<List<TranscriptSegment>?> TryLoadCachedTranscriptAsync(string cacheKey, string version)
    {
        if (_cacheContainer is null) return null;

        try
        {
            var blob = _cacheContainer.GetBlobClient(SanitizeBlobName(cacheKey) + ".json");
            if (!await blob.ExistsAsync()) return null;

            var response = await blob.DownloadContentAsync();
            var entry = response.Value.Content.ToObjectFromJson<TranscriptCacheEntry>();

            if (entry?.Version != version)
            {
                _logger.LogInformation("Transcript cache stale for {Key} (version mismatch), will re-transcribe", cacheKey);
                return null;
            }

            var segments = entry.Segments
                .Select(s => new TranscriptSegment(TimeSpan.FromTicks(s.OffsetTicks), s.Text))
                .ToList();

            _logger.LogInformation("Loaded transcript cache for {Key}: {Segments} segments", cacheKey, segments.Count);
            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read transcript cache for {Key}, will re-transcribe", cacheKey);
            return null;
        }
    }

    private async Task SaveTranscriptToCacheAsync(string cacheKey, string version, List<TranscriptSegment> segments)
    {
        if (_cacheContainer is null) return;

        try
        {
            var entry = new TranscriptCacheEntry(version,
                segments.Select(s => new CachedSegment(s.Offset.Ticks, s.Text)).ToList());

            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            var blob = _cacheContainer.GetBlobClient(SanitizeBlobName(cacheKey) + ".json");
            await blob.UploadAsync(BinaryData.FromString(json), overwrite: true);

            _logger.LogInformation("Saved transcript cache for {Key} ({Segments} segments)", cacheKey, segments.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save transcript cache for {Key}", cacheKey);
        }
    }

    // --- FFmpeg + Speech SDK ---

    private Task EnsureFfmpegAsync()
    {
        if (_ffmpegInitialized) return Task.CompletedTask;

        // FFmpeg must be installed on the system (via Dockerfile or locally).
        // Xabe.FFmpeg finds it on PATH automatically.
        _logger.LogInformation("FFmpeg expected on system PATH (installed via Dockerfile or locally)");
        _ffmpegInitialized = true;
        return Task.CompletedTask;
    }

    private static async Task ExtractAudioAsync(string videoPath, string wavPath)
    {
        var conversion = await FFmpeg.Conversions.FromSnippet.ExtractAudio(videoPath, wavPath);
        // Override to ensure 16kHz mono PCM WAV (required by Speech SDK)
        conversion.SetOverwriteOutput(true);
        conversion.AddParameter("-ar 16000 -ac 1", ParameterPosition.PostInput);
        await conversion.Start();
    }

    private async Task<List<TranscriptSegment>> RecognizeSpeechAsync(string wavPath)
    {
        var speechConfig = SpeechConfig.FromSubscription(_speechKey, _speechRegion);
        speechConfig.SpeechRecognitionLanguage = "en-US";

        using var audioConfig = AudioConfig.FromWavFileInput(wavPath);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var segments = new List<TranscriptSegment>();
        var sessionStopped = new TaskCompletionSource<bool>();

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                segments.Add(new TranscriptSegment(
                    TimeSpan.FromTicks((long)e.Result.OffsetInTicks),
                    e.Result.Text));
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogWarning("Speech recognition error: {Code} — {Details}", e.ErrorCode, e.ErrorDetails);
            }
            sessionStopped.TrySetResult(true);
        };

        recognizer.SessionStopped += (_, _) =>
        {
            sessionStopped.TrySetResult(true);
        };

        await recognizer.StartContinuousRecognitionAsync();

        // Calculate timeout: WAV duration + 2 min buffer.
        // 16kHz × 16-bit × mono = 32,000 bytes/sec
        var wavFileSize = new FileInfo(wavPath).Length;
        var audioDurationSeconds = wavFileSize / 32_000.0;
        var timeout = TimeSpan.FromSeconds(audioDurationSeconds) + TimeSpan.FromMinutes(2);

        var completed = await Task.WhenAny(sessionStopped.Task, Task.Delay(timeout));
        if (completed != sessionStopped.Task)
        {
            _logger.LogWarning("Speech recognition timed out after {Timeout} for {File}. Returning {Count} segments collected so far.",
                timeout, Path.GetFileName(wavPath), segments.Count);
        }

        await recognizer.StopContinuousRecognitionAsync();

        return segments;
    }
}
