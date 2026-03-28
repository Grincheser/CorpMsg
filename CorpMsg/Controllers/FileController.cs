using CorpMsg.AppData;
using CorpMsg.DTOs;
using CorpMsg.Service;
using CorpMsg.Services;
using CorpMsg.SupportClasses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FileController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<FileController> _logger;

        public FileController(
            IFileStorageService fileStorageService,
            ApplicationDbContext context,
            ILogger<FileController> logger)
        {
            _fileStorageService = fileStorageService;
            _context = context;
            _logger = logger;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile([FromForm] UploadFileRequest request)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var result = await _fileStorageService.UploadFileAsync(request.File, request.ChatId, currentUserId);

            if (result.IsSuccess)
                return Ok(result.Data);

            return StatusCode(result.Error.StatusCode, result.Error);
        }

        [HttpGet("download")]
        public async Task<IActionResult> DownloadFile([FromQuery] string fileUrl)
        {
            try
            {
                var currentUserId = GetCurrentUserId();
                if (currentUserId == Guid.Empty)
                    return Unauthorized("Пользователь не аутентифицирован");

                // Извлекаем objectName из URL
                var objectName = ExtractObjectName(fileUrl);

                // ПРОВЕРКА ПРАВ ДОСТУПА!
                if (!await CanAccessFile(objectName, currentUserId))
                    return Forbid("У вас нет доступа к этому файлу");

                // Генерируем временную ссылку (действительна 1 час)
                var presignedUrl = await _fileStorageService.GeneratePresignedUrlAsync(objectName, TimeSpan.FromHours(1));

                // Перенаправляем на временную ссылку
                return Redirect(presignedUrl);
            }
            catch (FileNotFoundException)
            {
                return NotFound("Файл не найден");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {FileUrl}", fileUrl);
                return StatusCode(500, new ApiError
                {
                    Message = "Ошибка при загрузке файла",
                    StatusCode = 500
                });
            }
        }

        [HttpGet("presigned-url")]
        public async Task<IActionResult> GetPresignedUrl([FromQuery] string fileUrl, [FromQuery] int expirySeconds = 3600)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var objectName = ExtractObjectName(fileUrl);

            if (!await CanAccessFile(objectName, currentUserId))
                return Forbid("У вас нет доступа к этому файлу");

            var url = await _fileStorageService.GeneratePresignedUrlAsync(objectName, TimeSpan.FromSeconds(expirySeconds));
            return Ok(new { Url = url, ExpiresIn = expirySeconds });
        }

        private async Task<bool> CanAccessFile(string objectName, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user.IsGlobalAdmin) return true;

            // Аватары пользователей - только свои
            if (objectName.StartsWith($"users/{userId}/"))
                return true;

            // Аватары чатов - проверка членства в чате
            if (objectName.StartsWith("chats/"))
            {
                var parts = objectName.Split('/');
                if (parts.Length >= 2 && Guid.TryParse(parts[1], out var chatId))
                {
                    return await _context.ChatMembers
                        .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
                }
            }

            // Медиа в сообщениях - проверка членства в чате
            if (objectName.StartsWith("chats/") && objectName.Contains("/messages/"))
            {
                var parts = objectName.Split('/');
                if (parts.Length >= 2 && Guid.TryParse(parts[1], out var chatId))
                {
                    return await _context.ChatMembers
                        .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
                }
            }

            return false;
        }

        private string ExtractObjectName(string fileUrl)
        {
            if (fileUrl.Contains("?fileUrl="))
            {
                var encoded = fileUrl.Split("?fileUrl=")[1];
                return Uri.UnescapeDataString(encoded);
            }
            return fileUrl;
        }

        private Guid GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            return userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId) ? userId : Guid.Empty;
        }

        private string GetContentType(string fileUrl)
        {
            var extension = Path.GetExtension(fileUrl).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }
    }
}