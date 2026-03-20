using Minio.DataModel.Args;
using Minio.Exceptions;
using Minio;
using Minio.ApiEndpoints;
using System.Reactive.Linq;

namespace CorpMsg.Service
{
    public interface IMinioService
    {
        Task<string> UploadFileAsync(IFormFile file, string bucketName, string objectName);
        Task<string> UploadFileAsync(byte[] fileData, string fileName, string bucketName, string objectName);
        Task<byte[]> DownloadFileAsync(string bucketName, string objectName);
        Task DeleteFileAsync(string bucketName, string objectName);
        string GetFileUrl(string bucketName, string objectName, int expiryInHours = 24);
        Task EnsureBucketExistsAsync(string bucketName);
        Task<string> UploadAvatarAsync(IFormFile file, string entityType, Guid entityId);
        Task<string> UploadMediaAsync(IFormFile file, Guid messageId);
        Task DeleteAvatarAsync(string entityType, Guid entityId);
        Task<List<string>> ListObjectsAsync(string bucketName, string prefix);
        Task DeleteObjectsByPrefixAsync(string bucketName, string prefix);
        Task<bool> BucketExistsAsync(string bucketName);
    }
    public class MinioService : IMinioService
    {
        private readonly IMinioClient _minioClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MinioService> _logger;
        private readonly string _endpoint;
        private readonly string _accessKey;
        private readonly string _secretKey;
        private readonly bool _useSsl;

        public MinioService(IConfiguration configuration, ILogger<MinioService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _endpoint = _configuration["Minio:Endpoint"] ?? "localhost:9000";
            _accessKey = _configuration["Minio:AccessKey"] ?? "minioadmin";
            _secretKey = _configuration["Minio:SecretKey"] ?? "minioadmin";
            _useSsl = bool.Parse(_configuration["Minio:UseSsl"] ?? "false");

            _minioClient = new MinioClient()
                .WithEndpoint(_endpoint)
                .WithCredentials(_accessKey, _secretKey)
                .WithSSL(_useSsl)
                .Build();
        }
        // В MinioService.cs реализуйте:
        public async Task<bool> BucketExistsAsync(string bucketName)
        {
            try
            {
                var beArgs = new BucketExistsArgs().WithBucket(bucketName);
                return await _minioClient.BucketExistsAsync(beArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking bucket '{bucketName}' existence");
                return false;
            }
        }
        public async Task EnsureBucketExistsAsync(string bucketName)
        {
            try
            {
                var beArgs = new BucketExistsArgs().WithBucket(bucketName);
                bool found = await _minioClient.BucketExistsAsync(beArgs);

                if (!found)
                {
                    var mbArgs = new MakeBucketArgs().WithBucket(bucketName);
                    await _minioClient.MakeBucketAsync(mbArgs);
                    _logger.LogInformation($"Bucket '{bucketName}' created successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error ensuring bucket '{bucketName}' exists");
                throw;
            }
        }

        public async Task<string> UploadFileAsync(IFormFile file, string bucketName, string objectName)
        {
            await EnsureBucketExistsAsync(bucketName);

            using var stream = file.OpenReadStream();
            var contentType = file.ContentType;

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(file.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs);

            return objectName;
        }

        public async Task<string> UploadFileAsync(byte[] fileData, string fileName, string bucketName, string objectName)
        {
            await EnsureBucketExistsAsync(bucketName);

            using var stream = new MemoryStream(fileData);
            var contentType = GetContentType(fileName);

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(fileData.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs);

            return objectName;
        }

        public async Task<byte[]> DownloadFileAsync(string bucketName, string objectName)
        {
            try
            {
                using var memoryStream = new MemoryStream();

                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream(async (stream, token) =>
                    {
                        await stream.CopyToAsync(memoryStream);
                    });

                await _minioClient.GetObjectAsync(getObjectArgs);

                return memoryStream.ToArray();
            }
            catch (ObjectNotFoundException)
            {
                _logger.LogWarning($"File '{objectName}' not found in bucket '{bucketName}'");
                return null;
            }
        }

        public async Task DeleteFileAsync(string bucketName, string objectName)
        {
            try
            {
                var removeObjectArgs = new RemoveObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName);

                await _minioClient.RemoveObjectAsync(removeObjectArgs);
                _logger.LogInformation($"File '{objectName}' deleted from bucket '{bucketName}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting file '{objectName}' from bucket '{bucketName}'");
                throw;
            }
        }

        public string GetFileUrl(string bucketName, string objectName, int expiryInHours = 24)
        {
            try
            {
                var reqParams = new Dictionary<string, string>
                {
                    { "response-content-type", GetContentType(objectName) }
                };

                var presignedUrlArgs = new PresignedGetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithExpiry(expiryInHours * 60 * 60)
                    .WithHeaders(reqParams);

                return _minioClient.PresignedGetObjectAsync(presignedUrlArgs).Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating presigned URL for '{objectName}'");
                return null;
            }
        }

        public async Task<string> UploadAvatarAsync(IFormFile file, string entityType, Guid entityId)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("File is empty");

            // Проверка размера файла (макс 5MB)
            if (file.Length > 5 * 1024 * 1024)
                throw new ArgumentException("Avatar file size must be less than 5MB");

            // Проверка типа файла
            var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/webp" };
            if (!allowedTypes.Contains(file.ContentType.ToLower()))
                throw new ArgumentException("Only image files (JPEG, PNG, GIF, WEBP) are allowed");

            var bucketName = $"{entityType.ToLower()}-avatars";
            var extension = Path.GetExtension(file.FileName);
            var objectName = $"{entityId}{extension}";

            // Удаляем старые файлы с таким же префиксом
            await DeleteObjectsByPrefixAsync(bucketName, entityId.ToString());

            await UploadFileAsync(file, bucketName, objectName);

            return $"/api/file/avatar/{entityType}/{entityId}";
        }

        public async Task<string> UploadMediaAsync(IFormFile file, Guid messageId)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("File is empty");

            // Проверка размера файла (макс 100MB)
            if (file.Length > 100 * 1024 * 1024)
                throw new ArgumentException("File size must be less than 100MB");

            var bucketName = "message-media";
            var extension = Path.GetExtension(file.FileName);
            var objectName = $"{messageId}{extension}";

            await UploadFileAsync(file, bucketName, objectName);

            return objectName;
        }

        public async Task DeleteAvatarAsync(string entityType, Guid entityId)
        {
            var bucketName = $"{entityType.ToLower()}-avatars";
            var prefix = entityId.ToString();

            try
            {
                // Проверяем существование бакета
                var beArgs = new BucketExistsArgs().WithBucket(bucketName);
                bool bucketExists = await _minioClient.BucketExistsAsync(beArgs);

                if (!bucketExists)
                    return;

                // Получаем список объектов и удаляем их
                await DeleteObjectsByPrefixAsync(bucketName, prefix);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting avatar for {entityType}/{entityId}");
                throw;
            }
        }

        public async Task<List<string>> ListObjectsAsync(string bucketName, string prefix)
        {
            var objects = new List<string>();

            try
            {
                var listArgs = new ListObjectsArgs()
                    .WithBucket(bucketName)
                    .WithPrefix(prefix)
                    .WithRecursive(true);

                // Используем GetEnumerator для IObservable
                var observable = _minioClient.ListObjectsAsync(listArgs);

                // Получаем IEnumerator
                using (var enumerator = observable.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        var item = enumerator.Current;
                        if (item != null)
                        {
                            objects.Add(item.Key);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error listing objects in bucket '{bucketName}' with prefix '{prefix}'");
                throw;
            }

            return objects;
        }

        public async Task DeleteObjectsByPrefixAsync(string bucketName, string prefix)
        {
            try
            {
                var objects = await ListObjectsAsync(bucketName, prefix);

                foreach (var objectName in objects)
                {
                    await DeleteFileAsync(bucketName, objectName);
                }

                _logger.LogInformation($"Deleted {objects.Count} objects from bucket '{bucketName}' with prefix '{prefix}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting objects by prefix '{prefix}' from bucket '{bucketName}'");
                throw;
            }
        }
        private string GetContentType(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLower();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };
        }
    }
}
