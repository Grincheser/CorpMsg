using CorpMsg.AppData;
using CorpMsg.DTOs;
using CorpMsg.Models;
using Minio.DataModel.Args;
using Minio;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using CorpMsg.SupportClasses;
using Minio.Exceptions;

namespace CorpMsg.Service
{
    public interface IFileStorageService
    {
        Task<Result<UploadFileResponse>> UploadFileAsync(IFormFile file, Guid chatId, Guid userId, string mediaType = "avatars");
        Task<Result<bool>> DeleteFileAsync(string fileUrl);
        Task<Stream> DownloadFileAsync(string fileUrl);
        Task<string> GeneratePresignedUrlAsync(string fileUrl, TimeSpan expiry);
        Task<string> GetSafeImageUrlAsync(string fileUrl, string imageType);
    }

    public class UploadFileResponse
    {
        public string FileUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ContentType { get; set; } = string.Empty;
    }
    public class FileStorageService : IFileStorageService
    {
        private readonly IMinioClient _minioClient;
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FileStorageService> _logger;
        private readonly string _bucketName;
        private readonly string _publicUrl;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _isInitialized;

        public FileStorageService(
            IMinioClient minioClient,
            ApplicationDbContext context,
            IConfiguration configuration,
            ILogger<FileStorageService> logger)
        {
            _minioClient = minioClient;
            _context = context;
            _configuration = configuration;
            _logger = logger;
            _bucketName = configuration["Minio:BucketName"] ?? "corpmsg-files";
            _publicUrl = configuration["PublicUrl"] ?? "http://localhost:8080";
        }

        private async Task EnsureInitializedAsync()
        {
            if (_isInitialized) return;

            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                var exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName));
                if (!exists)
                {
                    await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName));
                    _logger.LogInformation($"Bucket '{_bucketName}' created");
                }

                _isInitialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<Result<UploadFileResponse>> UploadFileAsync(
       IFormFile file,
       Guid chatId,
       Guid userId,
       string mediaType = "avatars")
        {
            try
            {
                await EnsureInitializedAsync();

                if (file == null || file.Length == 0)
                    return Result<UploadFileResponse>.Failure(new ApiError
                    {
                        Message = "Файл не выбран",
                        StatusCode = 400
                    });

                // Определяем максимальный размер в зависимости от типа
                long maxSize = mediaType.ToLower() switch
                {
                    "avatars" => 5 * 1024 * 1024,      // 5 MB
                    "messages" => 100 * 1024 * 1024,   // 100 MB
                    _ => 50 * 1024 * 1024              // 50 MB
                };

                if (file.Length > maxSize)
                    return Result<UploadFileResponse>.Failure(new ApiError
                    {
                        Message = $"Размер файла не должен превышать {maxSize / (1024 * 1024)}MB",
                        StatusCode = 400
                    });

                // Определяем разрешенные типы в зависимости от mediaType
                string[] allowedTypes;
                if (mediaType.ToLower() == "avatars")
                {
                    allowedTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/webp" };
                }
                else // messages
                {
                    allowedTypes = new[]
                    {
                "image/jpeg", "image/png", "image/gif", "image/webp",
                "video/mp4", "video/quicktime",
                "application/pdf", "application/msword",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "text/plain", "application/zip"
            };
                }

                if (!allowedTypes.Contains(file.ContentType.ToLower()))
                    return Result<UploadFileResponse>.Failure(new ApiError
                    {
                        Message = $"Недопустимый тип файла для {mediaType}",
                        StatusCode = 400
                    });

                // Формируем путь в зависимости от типа
                string objectName;
                var fileId = Guid.NewGuid().ToString();
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                var safeFileName = $"{fileId}{extension}";

                if (mediaType.ToLower() == "avatars")
                {
                    objectName = chatId == Guid.Empty
                        ? $"users/{userId}/avatar{safeFileName}"
                        : $"chats/{chatId}/avatar{safeFileName}";
                }
                else // messages
                {
                    objectName = $"chats/{chatId}/messages/{safeFileName}";
                }

                // Загружаем в MinIO
                using var stream = file.OpenReadStream();
                await _minioClient.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(file.ContentType));

                var fileUrl = $"{_publicUrl}/api/file/download?fileUrl={Uri.EscapeDataString(objectName)}";

                return Result<UploadFileResponse>.Success(new UploadFileResponse
                {
                    FileUrl = fileUrl,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    ContentType = file.ContentType
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file");
                return Result<UploadFileResponse>.Failure(new ApiError
                {
                    Message = "Ошибка при загрузке файла",
                    StatusCode = 500
                });
            }
        }
        public async Task<Result<bool>> DeleteFileAsync(string fileUrl)
        {
            try
            {
                await EnsureInitializedAsync();

                if (string.IsNullOrEmpty(fileUrl))
                    return Result<bool>.Failure(new ApiError
                    {
                        Message = "URL файла не указан",
                        StatusCode = 400
                    });

                // Извлекаем objectName из URL
                string objectName;

                // Если URL содержит параметры (как в нашем случае)
                if (fileUrl.Contains("?fileUrl="))
                {
                    // Извлекаем закодированный параметр
                    var encodedPath = fileUrl.Split("?fileUrl=")[1];
                    // Декодируем
                    objectName = Uri.UnescapeDataString(encodedPath);
                }
                else if (fileUrl.Contains(_publicUrl))
                {
                    // Если это полный URL без параметров
                    objectName = fileUrl.Replace($"{_publicUrl}/api/file/download?fileUrl=", "");
                    objectName = Uri.UnescapeDataString(objectName);
                }
                else
                {
                    // Если передан уже objectName
                    objectName = fileUrl;
                }

                _logger.LogInformation($"Deleting file: {objectName}");

                // Проверяем, существует ли файл
                var statArgs = new StatObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName);

                try
                {
                    await _minioClient.StatObjectAsync(statArgs);
                }
                catch (ObjectNotFoundException)
                {
                    _logger.LogWarning($"File not found: {objectName}");
                    return Result<bool>.Success(true); // Файла нет - считаем что удалено
                }

                // Удаляем файл из MinIO
                await _minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName));

                _logger.LogInformation($"File deleted from MinIO: {objectName}");
                return Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting file: {fileUrl}");
                return Result<bool>.Failure(new ApiError
                {
                    Message = "Ошибка при удалении файла",
                    StatusCode = 500
                });
            }
        }

        public async Task<Stream> DownloadFileAsync(string fileUrl)
        {
            try
            {
                await EnsureInitializedAsync();

                var objectName = fileUrl.Contains("?")
                    ? Uri.UnescapeDataString(fileUrl.Split('?')[0].Split("fileUrl=").Last())
                    : fileUrl.Replace($"{_publicUrl}/api/file/download?fileUrl=", "");

                var memoryStream = new MemoryStream();
                await _minioClient.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream(async (stream, token) =>
                    {
                        await stream.CopyToAsync(memoryStream, token);
                    }));

                memoryStream.Position = 0;
                return memoryStream;
            }
            catch (ObjectNotFoundException)
            {
                _logger.LogWarning($"File not found: {fileUrl}");
                throw new FileNotFoundException("Файл не найден");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file");
                throw;
            }
        }

        public async Task<string> GeneratePresignedUrlAsync(string fileUrl, TimeSpan expiry)
        {
            try
            {
                await EnsureInitializedAsync();

                var objectName = fileUrl.Contains("?")
                    ? Uri.UnescapeDataString(fileUrl.Split('?')[0].Split("fileUrl=").Last())
                    : fileUrl.Replace($"{_publicUrl}/api/file/download?fileUrl=", "");

                var presignedUrl = await _minioClient.PresignedGetObjectAsync(
                    new PresignedGetObjectArgs()
                        .WithBucket(_bucketName)
                        .WithObject(objectName)
                        .WithExpiry((int)expiry.TotalSeconds));

                return presignedUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating presigned URL");
                return fileUrl;
            }
        }

        public async Task<string> GetSafeImageUrlAsync(string fileUrl, string imageType)
        {
            if (string.IsNullOrEmpty(fileUrl))
                return null;

            try
            {
                // Если это уже публичный URL, возвращаем как есть
                if (fileUrl.StartsWith("https://ravenapp.ru") || fileUrl.StartsWith("http://localhost"))
                    return fileUrl;

                // Генерируем временную подписанную ссылку
                return await GeneratePresignedUrlAsync(fileUrl, TimeSpan.FromDays(7));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting safe image URL");
                return null;
            }
        }
    }
}

