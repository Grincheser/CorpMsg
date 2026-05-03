using CorpMsg.AppData;
using CorpMsg.Hubs;
using CorpMsg.Models;
using CorpMsg.Service;
using CorpMsg.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class MessageController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IBannedWordsService _bannedWordsService;
        private readonly IFileStorageService _fileStorageService;

        public MessageController(
            ApplicationDbContext context,
            IHubContext<ChatHub> hubContext,
            IBannedWordsService bannedWordsService,
            IFileStorageService fileStorageService)
        {
            _context = context;
            _hubContext = hubContext;
            _bannedWordsService = bannedWordsService;
            _fileStorageService = fileStorageService;
        }

        /// <summary>
        /// Отправка сообщения (с поддержкой файлов)
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<MessageResponse>> SendMessage([FromForm] SendMessageRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            // Проверка доступа к чату
            if (!await CanAccessChat(request.ChatId, userId))
                return Forbid();

            // Проверка на заморозку
            var user = await _context.Users.FindAsync(userId);
            if (user.IsFrozen)
                return BadRequest("Аккаунт заморожен. Невозможно отправить сообщение");

            // Обработка файла, если есть
            string? mediaUrl = null;
            string? fileName = null;
            long? fileSize = null;
            bool isMedia = false;
            MessageType messageType = MessageType.Text;

            if (request.File != null && request.File.Length > 0)
            {
                // Загружаем файл в MinIO
                var uploadResult = await _fileStorageService.UploadFileAsync(
                    request.File,
                    request.ChatId,
                    userId,
                    "messages"); // Тип "messages" для медиа в сообщениях

                if (!uploadResult.IsSuccess)
                {
                    return StatusCode(uploadResult.Error.StatusCode, uploadResult.Error);
                }

                mediaUrl = uploadResult.Data.FileUrl;
                fileName = uploadResult.Data.FileName;
                fileSize = uploadResult.Data.FileSize;
                isMedia = true;
                messageType = MessageType.Media;
            }

            // Проверка на запрещенные слова (только для текстовых сообщений)
            string filteredContent = request.Content;
            bool hasBannedWords = false;

            if (!string.IsNullOrEmpty(request.Content))
            {
                var bannedWords = await _context.BannedWords
                    .Where(b => b.CompanyId == companyId)
                    .Select(b => b.Word)
                    .ToListAsync();

                filteredContent = _bannedWordsService.FilterContent(request.Content, bannedWords);
                hasBannedWords = filteredContent != request.Content;
            }

            var message = new Message
            {
                Content = filteredContent,
                Type = messageType,
                MediaUrl = mediaUrl,
                MediaFileName = fileName,
                MediaFileSize = fileSize,
                IsMedia = isMedia,
                ChatId = request.ChatId,
                SenderId = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(message);
            await _context.SaveChangesAsync();

            // Если были запрещенные слова, логируем
            if (hasBannedWords)
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    CompanyId = companyId,
                    Action = AuditAction.Create,
                    EntityType = "MessageWithBannedWords",
                    EntityId = message.Id.ToString(),
                    OldValue = JsonSerializer.Serialize(new { Content = request.Content }),
                    NewValue = JsonSerializer.Serialize(new { Content = filteredContent })
                });
                await _context.SaveChangesAsync();
            }

            // Отправка через SignalR
            var messageResponse = await GetMessageResponse(message.Id);
            await _hubContext.Clients.Group(request.ChatId.ToString())
                .SendAsync("ReceiveMessage", messageResponse);

            // Создание уведомлений для участников чата
            await CreateMessageNotifications(message, userId);

            return Ok(messageResponse);
        }

        /// <summary>
        /// Получение истории чата (с пагинацией)
        /// </summary>
        [HttpGet("chat/{chatId}")]
        public async Task<ActionResult<PagedResult<MessageResponse>>> GetChatHistory(
            Guid chatId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка доступа к чату
            if (!await CanViewChatHistory(chatId, userId))
                return Forbid();

            var query = _context.Messages
                .Include(m => m.Sender)
                    .ThenInclude(s => s.Status)
                .Include(m => m.ForwardedFrom)
                    .ThenInclude(f => f.Sender)
                .Where(m => m.ChatId == chatId && !m.IsDeleted)
                .OrderByDescending(m => m.CreatedAt);

            var totalCount = await query.CountAsync();
            var messages = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var response = new PagedResult<MessageResponse>
            {
                Items = messages.Select(m => MapToResponse(m)).ToList(),
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            };

            // Логируем просмотр истории (для аудита)
            await LogHistoryView(chatId, userId);

            return Ok(response);
        }

        /// <summary>
        /// Редактирование сообщения
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> EditMessage(Guid id, EditMessageRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var message = await _context.Messages
                .Include(m => m.Chat)
                .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

            if (message == null)
                return NotFound();

            // Проверка прав на редактирование
            if (!await CanEditMessage(message, userId))
                return Forbid();

            // Проверка на запрещенные слова
            var bannedWords = await _context.BannedWords
                .Where(b => b.CompanyId == companyId)
                .Select(b => b.Word)
                .ToListAsync();

            var oldContent = message.Content;
            var filteredContent = _bannedWordsService.FilterContent(request.Content, bannedWords);

            message.Content = filteredContent;
            message.IsEdited = true;
            await _context.SaveChangesAsync();

            // Логируем редактирование
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = "Message",
                EntityId = message.Id.ToString(),
                OldValue = JsonSerializer.Serialize(new { Content = oldContent }),
                NewValue = JsonSerializer.Serialize(new { Content = filteredContent })
            });
            await _context.SaveChangesAsync();

            // Уведомление через SignalR
            var messageResponse = await GetMessageResponse(message.Id);
            await _hubContext.Clients.Group(message.ChatId.ToString())
                .SendAsync("MessageEdited", messageResponse);

            return Ok(messageResponse);
        }

        /// <summary>
        /// Удаление сообщения (soft delete)
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMessage(Guid id)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var message = await _context.Messages
                .Include(m => m.Chat)
                .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

            if (message == null)
                return NotFound();

            // Проверка прав на удаление
            if (!await CanDeleteMessage(message, userId))
                return Forbid();

            // Удаляем файл из MinIO, если это медиа
            if (message.IsMedia && !string.IsNullOrEmpty(message.MediaUrl))
            {
                var deleteResult = await _fileStorageService.DeleteFileAsync(message.MediaUrl);
                if (!deleteResult.IsSuccess)
                {
                    // Логируем, но не прерываем удаление сообщения
                    Console.WriteLine($"Failed to delete file: {message.MediaUrl}");
                }
            }

            message.IsDeleted = true;
            await _context.SaveChangesAsync();

            // Уведомление через SignalR
            await _hubContext.Clients.Group(message.ChatId.ToString())
                .SendAsync("MessageDeleted", new { messageId = id, chatId = message.ChatId });

            return Ok();
        }

        /// <summary>
        /// Пересылка сообщения
        /// </summary>
        [HttpPost("{id}/forward")]
        public async Task<ActionResult<MessageResponse>> ForwardMessage(Guid id, ForwardMessageRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var originalMessage = await _context.Messages
                .Include(m => m.Sender)
                .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

            if (originalMessage == null)
                return NotFound("Исходное сообщение не найдено");

            // Проверка доступа к целевому чату
            if (!await CanAccessChat(request.TargetChatId, userId))
                return Forbid();

            var forwardedMessage = new Message
            {
                Content = originalMessage.Content,
                Type = originalMessage.Type,
                MediaUrl = originalMessage.MediaUrl,
                MediaFileName = originalMessage.MediaFileName,
                MediaFileSize = originalMessage.MediaFileSize,
                IsMedia = originalMessage.IsMedia,
                ChatId = request.TargetChatId,
                SenderId = userId,
                ForwardedFromMessageId = originalMessage.Id,
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(forwardedMessage);
            await _context.SaveChangesAsync();

            var messageResponse = await GetMessageResponse(forwardedMessage.Id);

            // Уведомление через SignalR
            await _hubContext.Clients.Group(request.TargetChatId.ToString())
                .SendAsync("ReceiveMessage", messageResponse);

            return Ok(messageResponse);
        }

        /// <summary>
        /// Поиск сообщений в чате
        /// </summary>
        [HttpGet("search")]
        public async Task<ActionResult<List<MessageResponse>>> SearchMessages([FromQuery] MessageSearchRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка доступа к чату
            if (!await CanViewChatHistory(request.ChatId, userId))
                return Forbid();

            var query = _context.Messages
                .Include(m => m.Sender)
                .Include(m => m.ForwardedFrom)
                .Where(m => m.ChatId == request.ChatId &&
                           !m.IsDeleted &&
                           EF.Functions.ILike(m.Content, $"%{request.SearchTerm}%"));

            if (request.FromDate.HasValue)
                query = query.Where(m => m.CreatedAt >= request.FromDate);

            if (request.ToDate.HasValue)
                query = query.Where(m => m.CreatedAt <= request.ToDate);

            if (request.SenderId.HasValue)
                query = query.Where(m => m.SenderId == request.SenderId);

            var messages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Take(100)
                .ToListAsync();

            return Ok(messages.Select(MapToResponse));
        }

        // ... (остальные вспомогательные методы остаются без изменений)

        private async Task<bool> CanAccessChat(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            return await _context.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
        }

        private async Task<bool> CanViewChatHistory(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            var chat = await _context.Chats
                .FirstOrDefaultAsync(c => c.Id == chatId);

            if (chat != null && await IsDepartmentHead(userId, chat.DepartmentId))
                return true;

            return await _context.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
        }

        private async Task<bool> CanEditMessage(Message message, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            return message.SenderId == userId;
        }

        private async Task<bool> CanDeleteMessage(Message message, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            if (await IsDepartmentHead(userId, message.Chat.DepartmentId))
                return true;

            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == message.ChatId && cm.UserId == userId);

            if (membership != null && membership.Role == ChatMemberRole.Moderator)
                return true;

            return message.SenderId == userId;
        }

        private async Task<bool> IsDepartmentHead(Guid userId, Guid departmentId)
        {
            return await _context.Departments
                .AnyAsync(d => d.Id == departmentId && d.HeadId == userId && !d.IsDeleted);
        }

        private async Task CreateMessageNotifications(Message message, Guid senderId)
        {
            var chatMembers = await _context.ChatMembers
                .Where(cm => cm.ChatId == message.ChatId && cm.UserId != senderId)
                .Select(cm => cm.UserId)
                .ToListAsync();

            var notifications = chatMembers.Select(userId => new Notification
            {
                UserId = userId,
                Title = "Новое сообщение",
                Content = message.IsMedia ? "Отправлен файл" : message.Content.Length > 50 ? message.Content.Substring(0, 50) + "..." : message.Content,
                Type = NotificationType.NewMessage,
                ReferenceId = message.ChatId
            }).ToList();

            _context.Notifications.AddRange(notifications);
            await _context.SaveChangesAsync();
        }

        private async Task LogHistoryView(Guid chatId, Guid userId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin || await IsDepartmentHead(userId,
                (await _context.Chats.FindAsync(chatId)).DepartmentId))
            {
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userId,
                    CompanyId = user.CompanyId,
                    Action = AuditAction.ViewHistory,
                    EntityType = "Chat",
                    EntityId = chatId.ToString()
                });
                await _context.SaveChangesAsync();
            }
        }

        private async Task<MessageResponse?> GetMessageResponse(Guid messageId)
        {
            var message = await _context.Messages
                .Include(m => m.Sender)
                    .ThenInclude(s => s.Status)
                .Include(m => m.ForwardedFrom)
                    .ThenInclude(f => f.Sender)
                .FirstOrDefaultAsync(m => m.Id == messageId && !m.IsDeleted);

            return message != null ? MapToResponse(message) : null;
        }

        private MessageResponse MapToResponse(Message message)
        {
            return new MessageResponse
            {
                Id = message.Id,
                Content = message.Content,
                Type = message.Type,
                MediaUrl = message.MediaUrl,
                MediaFileName = message.MediaFileName,
                MediaFileSize = message.MediaFileSize,
                IsMedia = message.IsMedia,
                CreatedAt = message.CreatedAt,
                IsEdited = message.IsEdited,
                SenderId = message.SenderId,
                SenderName = message.Sender.FullName,
                SenderIsOnline = message.Sender.Status?.IsOnline ?? false,
                ChatId = message.ChatId,
                ForwardedFrom = message.ForwardedFrom != null ? new ForwardedInfo
                {
                    MessageId = message.ForwardedFrom.Id,
                    Content = message.ForwardedFrom.Content,
                    SenderName = message.ForwardedFrom.Sender.FullName,
                    SentAt = message.ForwardedFrom.CreatedAt
                } : null
            };
        }
    }

    // Обновленные DTO
    public class SendMessageRequest
    {
        public Guid ChatId { get; set; }
        public string? Content { get; set; } = string.Empty;
        public IFormFile? File { get; set; } // Добавляем файл
    }

    public class EditMessageRequest
    {
        public string Content { get; set; } = string.Empty;
    }

    public class ForwardMessageRequest
    {
        public Guid TargetChatId { get; set; }
    }

    public class MessageSearchRequest
    {
        public Guid ChatId { get; set; }
        public string SearchTerm { get; set; } = string.Empty;
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public Guid? SenderId { get; set; }
    }

    public class MessageResponse
    {
        public Guid Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public MessageType Type { get; set; }
        public string? MediaUrl { get; set; }
        public string? MediaFileName { get; set; }
        public long? MediaFileSize { get; set; }
        public bool IsMedia { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsEdited { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public bool SenderIsOnline { get; set; }
        public Guid ChatId { get; set; }
        public ForwardedInfo? ForwardedFrom { get; set; }
    }

    public class ForwardedInfo
    {
        public Guid MessageId { get; set; }
        public string Content { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
    }

    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public object? Metadata { get; set; }
    }
}