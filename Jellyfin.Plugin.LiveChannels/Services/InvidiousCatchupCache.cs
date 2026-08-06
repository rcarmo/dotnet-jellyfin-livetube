using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>Materializes Invidious video and original audio representations as finite seekable MP4 files.</summary>
public sealed class InvidiousCatchupCache
{
    internal const long MaximumBytes = 20L * 1024 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly InvidiousFeedClient _invidious;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<InvidiousCatchupCache> _logger;
    private readonly string _root;

    /// <summary>Initializes the finite-media cache.</summary>
    public InvidiousCatchupCache(InvidiousFeedClient invidious, IMediaEncoder encoder, IApplicationPaths paths, ILogger<InvidiousCatchupCache> logger)
    {
        ArgumentNullException.ThrowIfNull(invidious);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _invidious = invidious;
        _encoder = encoder;
        _logger = logger;
        _root = Path.Combine(paths.CachePath, "livechannels-assets", "catchup");
    }

    /// <summary>Returns a validated finite MP4 for one stable Invidious video ID.</summary>
    public async Task<string> MaterializeAsync(string instanceUrl, string videoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        var key = SanitizeVideoId(videoId);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var destination = Path.Combine(_root, key + ".mp4");
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                File.SetLastAccessTimeUtc(destination, DateTime.UtcNow);
                return destination;
            }

            var ffmpeg = _encoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                throw new InvalidOperationException("Jellyfin has no configured ffmpeg executable.");
            }

            var media = await _invidious.ResolveOriginalPlaybackMediaAsync(instanceUrl, videoId, cancellationToken).ConfigureAwait(false);
            var temporary = destination + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp.mp4";
            try
            {
                var arguments = BuildRemuxArguments(media, temporary);
                await RunAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
                await ValidateAsync(ffmpeg, temporary, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
                File.SetLastAccessTimeUtc(destination, DateTime.UtcNow);
                Evict(_root, MaximumBytes, destination, _logger);
                return destination;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static IReadOnlyList<string> BuildRemuxArguments(InvidiousPlaybackMedia media, string destination)
        => new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", media.VideoUrl,
            "-i", media.AudioUrl,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c", "copy", "-movflags", "+faststart", "-shortest", destination
        };

    internal static void Evict(string root, long maximumBytes, string protectedPath, ILogger logger)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var files = new DirectoryInfo(root).EnumerateFiles("*.mp4")
            .OrderBy(file => file.LastAccessTimeUtc)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= maximumBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, protectedPath, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                // Jellyfin opens playback sources for reading. An exclusive open fails while a file is active,
                // so eviction never unlinks media currently in use (and works the same on Windows and Unix).
                using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException)
            {
                // Active or otherwise busy: leave it for the next pass.
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogDebug(ex, "Live Channels: could not evict catch-up file {Path}", file.FullName);
            }
        }
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffmpeg exited with code {process.ExitCode}: {error.Trim()}");
        }
    }

    private static async Task ValidateAsync(string ffmpeg, string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ffmpeg) ?? string.Empty;
        var ffprobe = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobe))
        {
            throw new FileNotFoundException("Jellyfin ffprobe was not found beside ffmpeg.", ffprobe);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-show_entries", "stream=codec_type", "-of", "json", path })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var json = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe exited with code {process.ExitCode}: {error.Trim()}");
        }

        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray()
            .Select(stream => stream.GetProperty("codec_type").GetString())
            .ToList();
        var durationText = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (!streams.Contains("video", StringComparer.Ordinal) || !streams.Contains("audio", StringComparer.Ordinal)
            || !double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
        {
            throw new InvalidDataException("Materialized catch-up media does not contain finite video and audio streams.");
        }
    }

    private static string SanitizeVideoId(string videoId)
    {
        var value = videoId.Trim();
        if (value.Length is < 1 or > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The Invidious video ID contains invalid characters.", nameof(videoId));
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
