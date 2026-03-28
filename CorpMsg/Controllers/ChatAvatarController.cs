using CorpMsg.AppData;
using CorpMsg.Models;
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
    public class ChatAvatarController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ChatAvatarController> _logger;

        public ChatAvatarController(
            IFileStorageService fileStorageService,
            ApplicationDbContext context,
            ILogger<ChatAvatarController> logger)
        {
            _fileStorageService = fileStorageService;
            _context = context;
            _logger = logger;
        }

        [HttpPost("{chatId}")]
        public async Task<IActionResult> UploadChatAvatar(Guid chatId, IFormFile avatar)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            // Проверка прав на редактирование чата
            if (!await CanManageChat(chatId, currentUserId))
                return Forbid("Недостаточно прав для изменения аватарки чата");

            var result = await _fileStorageService.UploadFileAsync(avatar, chatId, currentUserId);

            if (!result.IsSuccess)
                return StatusCode(result.Error.StatusCode, result.Error);

            // Обновляем чат
            var chat = await _context.Chats.FindAsync(chatId);
            if (chat != null)
            {
                chat.AvatarUrl = result.Data.FileUrl;
                await _context.SaveChangesAsync();
            }

            return Ok(new { avatarUrl = result.Data.FileUrl });
        }

        [HttpDelete("{chatId}")]
        public async Task<IActionResult> DeleteChatAvatar(Guid chatId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            if (!await CanManageChat(chatId, currentUserId))
                return Forbid("Недостаточно прав для удаления аватарки чата");

            var chat = await _context.Chats.FindAsync(chatId);
            if (chat == null || string.IsNullOrEmpty(chat.AvatarUrl))
                return NotFound("Аватар чата не найден");

            _logger.LogInformation($"Deleting chat avatar for chat {chatId}: {chat.AvatarUrl}");

            // Удаляем файл из MinIO
            var deleteResult = await _fileStorageService.DeleteFileAsync(chat.AvatarUrl);

            if (!deleteResult.IsSuccess)
            {
                _logger.LogWarning($"Failed to delete file from MinIO: {chat.AvatarUrl}");
            }

            // Очищаем URL в базе
            chat.AvatarUrl = null;
            chat.AvatarUpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Chat avatar deleted for chat {chatId}");

            return Ok(new { message = "Аватар чата удален" });
        }

        [HttpGet("{chatId}")]
        public async Task<IActionResult> GetChatAvatar(Guid chatId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            if (!await CanAccessChat(chatId, currentUserId))
                return Forbid("У вас нет доступа к этому чату");

            var chat = await _context.Chats.FindAsync(chatId);
            if (chat == null || string.IsNullOrEmpty(chat.AvatarUrl))
                return NotFound("Аватар чата не установлен");

            // Извлекаем objectName из сохраненного URL
            var objectName = ExtractObjectName(chat.AvatarUrl);

            // Генерируем временную ссылку (действительна 7 дней для аватаров)
            var presignedUrl = await _fileStorageService.GeneratePresignedUrlAsync(objectName, TimeSpan.FromDays(7));

            return Ok(new { avatarUrl = presignedUrl });
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

        private async Task<bool> CanManageChat(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user.IsGlobalAdmin) return true;

            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            return membership != null && (membership.Role == ChatMemberRole.Moderator || membership.Role == ChatMemberRole.Head);
        }

        private async Task<bool> CanAccessChat(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user.IsGlobalAdmin) return true;

            return await _context.ChatMembers.AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
        }

        private Guid GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            return userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId) ? userId : Guid.Empty;
        }
    }
}