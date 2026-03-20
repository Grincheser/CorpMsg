// Controllers/FileController.cs
using CorpMsg.AppData;
using CorpMsg.Models;
using CorpMsg.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using System.Security.Claims;
using System.Text.Json;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FileController : ControllerBase
    {
        private readonly IMinioService _minioService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<FileController> _logger;
        private readonly IMinioClient _minioClient;
        public FileController(
            IMinioService minioService,
            ApplicationDbContext context,
            ILogger<FileController> logger,
               IMinioClient minioClient)
        {
            _minioService = minioService;
            _context = context;
            _logger = logger;
            _minioClient = minioClient;
        }

        /// <summary>
        /// Загрузка аватара компании
        /// </summary>
        [HttpPost("company/avatar")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> UploadCompanyAvatar(IFormFile file)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var company = await _context.Companies.FindAsync(companyId);
            if (company == null)
                return NotFound("Company not found");

            try
            {
                // Удаляем старый аватар
                if (!string.IsNullOrEmpty(company.AvatarUrl))
                {
                    await _minioService.DeleteAvatarAsync("company", companyId);
                }

                // Загружаем новый аватар
                var avatarUrl = await _minioService.UploadAvatarAsync(file, "company", companyId);

                company.AvatarUrl = avatarUrl;
                company.AvatarUpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                // Логируем
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    CompanyId = companyId,
                    Action = AuditAction.Update,
                    EntityType = "Company",
                    EntityId = companyId.ToString(),
                    NewValue = JsonSerializer.Serialize(new { AvatarUpdated = true })
                });
                await _context.SaveChangesAsync();

                return Ok(new { avatarUrl });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading company avatar");
                return StatusCode(500, "Error uploading avatar");
            }
        }

        /// <summary>
        /// Загрузка аватара чата
        /// </summary>
        [HttpPost("chat/{chatId}/avatar")]
        public async Task<IActionResult> UploadChatAvatar(Guid chatId, IFormFile file)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            // Проверка прав на редактирование чата
            var chat = await _context.Chats
                .Include(c => c.Department)
                .FirstOrDefaultAsync(c => c.Id == chatId && !c.IsDeleted);

            if (chat == null)
                return NotFound("Chat not found");

            if (!await CanManageChat(chatId, userId))
                return Forbid();

            try
            {
                // Удаляем старый аватар
                if (!string.IsNullOrEmpty(chat.AvatarUrl))
                {
                    await _minioService.DeleteAvatarAsync("chat", chatId);
                }

                // Загружаем новый аватар
                var avatarUrl = await _minioService.UploadAvatarAsync(file, "chat", chatId);

                chat.AvatarUrl = avatarUrl;
                chat.AvatarUpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return Ok(new { avatarUrl });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading chat avatar");
                return StatusCode(500, "Error uploading avatar");
            }
        }

        /// <summary>
        /// Получение аватара по ссылке
        /// </summary>
        [HttpGet("avatar/{entityType}/{entityId}")]
        public async Task<IActionResult> GetAvatar(string entityType, Guid entityId)
        {
            try
            {
                var bucketName = $"{entityType.ToLower()}-avatars";
                var prefix = entityId.ToString();

                // Проверяем существование бакета
                var beArgs = new BucketExistsArgs().WithBucket(bucketName);
                bool bucketExists = await _minioClient.BucketExistsAsync(beArgs);

                if (!bucketExists)
                    return NotFound();

                // Ищем файл в бакете
                var objects = await _minioService.ListObjectsAsync(bucketName, prefix);

                if (objects == null || !objects.Any())
                    return NotFound();

                var objectName = objects.First();
                var fileData = await _minioService.DownloadFileAsync(bucketName, objectName);

                if (fileData == null)
                    return NotFound();

                var contentType = GetContentType(objectName);
                return File(fileData, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting avatar");
                return NotFound();
            }
        }


        /// <summary>
        /// Загрузка медиафайла для сообщения
        /// </summary>
        [HttpPost("message/media")]
        public async Task<IActionResult> UploadMessageMedia(IFormFile file, [FromQuery] Guid chatId)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка доступа к чату
            if (!await CanAccessChat(chatId, userId))
                return Forbid();

            try
            {
                var messageId = Guid.NewGuid();
                var objectName = await _minioService.UploadMediaAsync(file, messageId);

                return Ok(new
                {
                    mediaId = messageId,
                    mediaUrl = $"/api/files/media/{objectName}",
                    fileName = file.FileName,
                    fileSize = file.Length,
                    contentType = file.ContentType
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading media");
                return StatusCode(500, "Error uploading media");
            }
        }

        /// <summary>
        /// Получение медиафайла
        /// </summary>
        [HttpGet("media/{objectName}")]
        public async Task<IActionResult> GetMedia(string objectName)
        {
            try
            {
                var fileData = await _minioService.DownloadFileAsync("message-media", objectName);
                if (fileData == null)
                    return NotFound();

                var contentType = GetContentType(objectName);
                return File(fileData, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting media file");
                return NotFound();
            }
        }

        /// <summary>
        /// Получение временной ссылки на файл
        /// </summary>
        [HttpGet("link/{bucketName}/{objectName}")]
        public IActionResult GetFileLink(string bucketName, string objectName, [FromQuery] int expiryHours = 24)
        {
            try
            {
                var url = _minioService.GetFileUrl(bucketName, objectName, expiryHours);
                return Ok(new { url, expiresIn = expiryHours * 3600 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating file link");
                return StatusCode(500, "Error generating link");
            }
        }

        private async Task<bool> CanManageChat(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user.IsGlobalAdmin)
                return true;

            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (membership == null)
                return false;

            return membership.Role == ChatMemberRole.Moderator ||
                   membership.Role == ChatMemberRole.Head;
        }

        private async Task<bool> CanAccessChat(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user.IsGlobalAdmin)
                return true;

            return await _context.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
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