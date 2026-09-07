using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SmartX.Models;

namespace SmartX.Services;

/// <summary>
/// Handles file uploads for sensor attachments (config files, deployment photos, hardware logs).
/// Uses optimised multipart file upload with streaming.
/// </summary>
public class FileUploadService
{
    private readonly string _uploadBasePath;

    // Server-side allow-list — must be checked independently of the client's
    // <input accept> attribute, which can be bypassed trivially.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".csv", ".log", ".txt", ".png", ".jpg", ".jpeg", ".pdf"
    };

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public FileUploadService(IWebHostEnvironment env)
    {
        _uploadBasePath = Path.Combine(env.WebRootPath ?? "wwwroot", "uploads");
        Directory.CreateDirectory(_uploadBasePath);
    }

/// <summary>
/// A small stream that first reads from a prefix stream then continues reading from the remainder stream.
/// Used to support non-seekable incoming request streams where we inspected an initial window.
/// </summary>
internal class ConcatenatedStream : Stream
{
    private readonly Stream _prefix;
    private readonly Stream _remainder;
    private bool _prefixExhausted;

    public ConcatenatedStream(Stream prefix, Stream remainder)
    {
        _prefix = prefix ?? Stream.Null;
        _remainder = remainder ?? Stream.Null;
        _prefixExhausted = _prefix == Stream.Null;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_prefixExhausted)
        {
            int r = _prefix.Read(buffer, offset, count);
            if (r == 0)
            {
                _prefixExhausted = true;
            }
            else
            {
                return r;
            }
        }
        return _remainder.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (!_prefixExhausted)
        {
            int r = await _prefix.ReadAsync(buffer, offset, count, cancellationToken);
            if (r == 0)
            {
                _prefixExhausted = true;
            }
            else
            {
                return r;
            }
        }
        return await _remainder.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

    public async Task<SensorAttachment> UploadFileAsync(string sensorId, Stream fileStream, string fileName, string contentType, long fileLength)
    {
        string ext = Path.GetExtension(fileName);

        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"File type '{ext}' is not permitted. Allowed types: {string.Join(", ", AllowedExtensions)}.");

        if (fileLength > MaxFileSizeBytes)
            throw new InvalidOperationException($"File exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
        // Read initial bytes for magic-byte validation. Be careful with non-seekable streams.
        byte[] header = new byte[8];
        int bytesRead = 0;
        if (fileStream.CanSeek)
        {
            long originalPos = fileStream.Position;
            bytesRead = await fileStream.ReadAsync(header, 0, header.Length);
            fileStream.Position = originalPos;
        }
        else
        {
            // Copy a small window to memory to inspect header, then prepend when saving
            using var ms = new MemoryStream();
            // copy up to header length + small margin (but do not load whole large files)
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0 && ms.Length < 16384)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length >= header.Length) break;
            }
            var tmp = ms.ToArray();
            bytesRead = Math.Min(tmp.Length, header.Length);
            Array.Copy(tmp, 0, header, 0, bytesRead);
            // Replace incoming stream with combined stream for subsequent save
            fileStream = new ConcatenatedStream(new MemoryStream(tmp), fileStream);
        }

        // Validate magic bytes according to extension/content-type
        bool signatureValid = ext.ToLower() switch
        {
            ".png" => bytesRead >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A,
            ".jpg" or ".jpeg" => bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".pdf" => bytesRead >= 4 && header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D' && header[3] == (byte)'F',
            _ => true
        };

        if (!signatureValid)
            throw new InvalidOperationException("Uploaded file does not match expected file signature for its extension.");

        // Basic content-type vs extension cross-check
        if (contentType is not null)
        {
            var expected = GetContentType(ext);
            if (!string.Equals(expected, contentType, StringComparison.OrdinalIgnoreCase) && expected != "application/octet-stream")
            {
                throw new InvalidOperationException($"Content-Type '{contentType}' does not match expected '{expected}' for extension '{ext}'.");
            }
        }

        // Create sensor-specific directory
        string sensorDir = Path.Combine(_uploadBasePath, sensorId);
        Directory.CreateDirectory(sensorDir);

        // Sanitize filename and make unique
        string safeName = Path.GetFileNameWithoutExtension(fileName)
            .Replace(" ", "_")
            .Replace("..", "")
            .Replace("/", "_")
            .Replace("\\", "_");
        string uniqueName = $"{safeName}_{Guid.NewGuid():N}{ext}";
        string fullPath = Path.Combine(sensorDir, uniqueName);

        // Stream file to disk (optimised for large files)
        using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await fileStream.CopyToAsync(fs);
        }

        var fileInfo = new FileInfo(fullPath);

        return new SensorAttachment
        {
            FileName = fileName,
            FilePath = $"/uploads/{sensorId}/{uniqueName}",
            ContentType = contentType,
            FileSize = fileInfo.Length,
            AttachedAt = DateTime.UtcNow
        };
    }

    public bool DeleteFile(string filePath)
    {
        string fullPath = Path.Combine(_uploadBasePath, filePath.TrimStart('/'));
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return true;
        }
        return false;
    }

    public List<SensorAttachment> GetAttachmentsForSensor(string sensorId)
    {
        string sensorDir = Path.Combine(_uploadBasePath, sensorId);
        if (!Directory.Exists(sensorDir))
            return new List<SensorAttachment>();

        return Directory.GetFiles(sensorDir)
            .Select(f => new FileInfo(f))
            .Select(f => new SensorAttachment
            {
                FileName = f.Name,
                FilePath = $"/uploads/{sensorId}/{f.Name}",
                ContentType = GetContentType(f.Extension),
                FileSize = f.Length,
                AttachedAt = f.CreationTimeUtc
            })
            .ToList();
    }

    private static string GetContentType(string extension) => extension.ToLower() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".log" or ".txt" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        _ => "application/octet-stream"
    };
}