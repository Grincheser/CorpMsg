using CorpMsg.AppData;
using CorpMsg.Service;
using CorpMsg.Services;
using CorpMsg.SupportClasses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserAvatarController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<UserAvatarController> _logger;

        public UserAvatarController(
            IFileStorageService fileStorageService,
            ApplicationDbContext context,
            ILogger<UserAvatarController> logger)
        {
            _fileStorageService = fileStorageService;
            _context = context;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> UploadAvatar(IFormFile avatar)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var result = await _fileStorageService.UploadFileAsync(avatar, Guid.Empty, currentUserId);

            if (!result.IsSuccess)
                return StatusCode(result.Error.StatusCode, result.Error);

            // Обновляем пользователя
            var user = await _context.Users.FindAsync(currentUserId);
            if (user != null)
            {
                user.AvatarUrl = result.Data.FileUrl;
                await _context.SaveChangesAsync();
            }

            return Ok(new { avatarUrl = result.Data.FileUrl });
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteAvatar()
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var user = await _context.Users.FindAsync(currentUserId);
            if (user == null || string.IsNullOrEmpty(user.AvatarUrl))
                return NotFound("Аватар не найден");

            _logger.LogInformation($"Deleting avatar for user {currentUserId}: {user.AvatarUrl}");

            // Удаляем файл из MinIO
            var deleteResult = await _fileStorageService.DeleteFileAsync(user.AvatarUrl);

            if (!deleteResult.IsSuccess)
            {
                _logger.LogWarning($"Failed to delete file from MinIO: {user.AvatarUrl}");
                // Не возвращаем ошибку, продолжаем очищать базу
            }

            // Очищаем URL в базе
            user.AvatarUrl = null;
            user.AvatarUpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Avatar deleted for user {currentUserId}");

            return Ok(new { message = "Аватар удален" });
        }

        [HttpGet]
        public async Task<IActionResult> GetAvatar()
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var user = await _context.Users.FindAsync(currentUserId);
            if (user == null || string.IsNullOrEmpty(user.AvatarUrl))
                return NotFound("Аватар не установлен");

            // Извлекаем objectName из сохраненного URL
            var objectName = ExtractObjectName(user.AvatarUrl);

            // Генерируем временную ссылку (действительна 7 дней для аватаров)
            var presignedUrl = await _fileStorageService.GeneratePresignedUrlAsync(objectName, TimeSpan.FromDays(7));

            return Ok(new { avatarUrl = presignedUrl });
        }

        private string ExtractObjectName(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl))
                return string.Empty;

            // Если URL содержит параметр fileUrl
            if (fileUrl.Contains("?fileUrl="))
            {
                var encoded = fileUrl.Split("?fileUrl=")[1];
                return Uri.UnescapeDataString(encoded);
            }

            // Если это прямой путь
            if (fileUrl.Contains("/api/file/download?"))
            {
                var encoded = fileUrl.Split("?fileUrl=")[1];
                return Uri.UnescapeDataString(encoded);
            }

            // Если уже objectName
            return fileUrl;
        }

        private Guid GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            return userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId) ? userId : Guid.Empty;
        }
    }
}