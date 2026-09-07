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

    public async Task<SensorAttachment> UploadFileAsync(string sensorId, Stream fileStream, string fileName, string contentType, long fileLength)
    {
        string ext = Path.GetExtension(fileName);

        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"File type '{ext}' is not permitted. Allowed types: {string.Join(", ", AllowedExtensions)}.");

        if (fileLength > MaxFileSizeBytes)
            throw new InvalidOperationException($"File exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");

        // Create sensor-specific directory
        string sensorDir = Path.Combine(_uploadBasePath, sensorId);
        Directory.CreateDirectory(sensorDir);

        // Sanitize filename and make unique
        string safeName = Path.GetFileNameWithoutExtension(fileName)
            .Replace(" ", "_")
            .Replace("..", "");
        string uniqueName = $"{safeName}_{Guid.NewGuid():N}{ext}";
        string fullPath = Path.Combine(sensorDir, uniqueName);

        // Stream file to disk (optimised for large files)
        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
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