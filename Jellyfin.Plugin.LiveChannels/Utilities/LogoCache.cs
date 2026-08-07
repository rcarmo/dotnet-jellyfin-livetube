using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>Validates and atomically replaces cached channel-logo files.</summary>
public static class LogoCache
{
    /// <summary>Returns whether a cache path contains a non-empty regular file.</summary>
    public static bool IsUsable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes bytes beside the destination and atomically moves the completed file into place.</summary>
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("A cached logo cannot be empty.", nameof(bytes));
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A successful move already removed the temporary path; a failed cleanup is harmless cache debris.
            }
            catch (UnauthorizedAccessException)
            {
                // The caller reports the original write failure; do not replace it with a cleanup error.
            }
        }
    }
}
