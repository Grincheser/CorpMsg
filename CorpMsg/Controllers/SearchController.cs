using CorpMsg.AppData;
using CorpMsg.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SearchController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public SearchController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Глобальный поиск сотрудников
        /// </summary>
        [HttpGet("users")]
        public async Task<ActionResult<SearchResult<UserSearchResponse>>> SearchUsers(
            [FromQuery] UserSearchRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var currentUser = await _context.Users
                .Include(u => u.Department)
                .FirstOrDefaultAsync(u => u.Id == userId);

            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            // Базовый запрос
            var query = _context.Users
                .Include(u => u.Department)
                .Include(u => u.Status)
                .Where(u => u.CompanyId == companyId && !u.IsFrozen);

            // Применяем фильтры поиска
            if (!string.IsNullOrEmpty(request.SearchTerm))
            {
                var searchTerm = request.SearchTerm.ToLower();
                query = query.Where(u =>
                    u.FullName.ToLower().Contains(searchTerm) ||
                    u.Username.ToLower().Contains(searchTerm) ||
                    (u.Position != null && u.Position.ToLower().Contains(searchTerm)));
            }

            if (request.DepartmentId.HasValue)
            {
                query = query.Where(u => u.DepartmentId == request.DepartmentId);
            }

            // Ограничиваем область видимости в зависимости от роли
            query = await ApplySearchVisibility(query, currentUser, userId);

            // Пагинация
            var totalCount = await query.CountAsync();
            var users = await query
                .OrderBy(u => u.FullName)
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            var result = new SearchResult<UserSearchResponse>
            {
                Items = users.Select(u => new UserSearchResponse
                {
                    Id = u.Id,
                    FullName = u.FullName,
                    Username = u.Username,
                    Position = u.Position,
                    DepartmentId = u.DepartmentId,
                    DepartmentName = u.Department?.Name,
                    IsOnline = u.Status?.IsOnline ?? false,
                    LastSeenAt = u.Status?.LastSeenAt
                }).ToList(),
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = request.PageSize
            };

            return Ok(result);
        }

        /// <summary>
        /// Поиск по чатам
        /// </summary>
        [HttpGet("chats")]
        public async Task<ActionResult<SearchResult<ChatSearchResponse>>> SearchChats(
            [FromQuery] ChatSearchRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var currentUser = await _context.Users.FindAsync(userId);
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var query = _context.Chats
                .Include(c => c.Department)
                .Include(c => c.Members)
                .Where(c => c.Department.CompanyId == companyId && !c.IsDeleted);

            // Поиск по названию
            if (!string.IsNullOrEmpty(request.SearchTerm))
            {
                var searchTerm = request.SearchTerm.ToLower();
                query = query.Where(c =>
                    c.Name.ToLower().Contains(searchTerm) ||
                    (c.Description != null && c.Description.ToLower().Contains(searchTerm)));
            }

            // Фильтр по типу чата
            if (request.IsSystemChat.HasValue)
            {
                query = query.Where(c => c.IsSystemChat == request.IsSystemChat);
            }

            // Ограничиваем видимость
            if (!currentUser.IsGlobalAdmin)
            {
                // Не админы видят только чаты, где они участники
                query = query.Where(c => c.Members.Any(m => m.UserId == userId));
            }

            var totalCount = await query.CountAsync();
            var chats = await query
                .OrderBy(c => c.Name)
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            var result = new SearchResult<ChatSearchResponse>
            {
                Items = chats.Select(c => new ChatSearchResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    Description = c.Description,
                    IsSystemChat = c.IsSystemChat,
                    DepartmentName = c.Department.Name,
                    MemberCount = c.Members.Count
                }).ToList(),
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = request.PageSize
            };

            return Ok(result);
        }

        /// <summary>
        /// Поиск по сообщениям (глобальный для админов)
        /// </summary>
        [HttpGet("messages")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<SearchResult<MessageSearchResponse>>> SearchMessages(
            [FromQuery] GlobalMessageSearchRequest request)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var query = _context.Messages
                .Include(m => m.Sender)
                .Include(m => m.Chat)
                .Where(m => m.Sender.CompanyId == companyId && !m.IsDeleted);

            if (!string.IsNullOrEmpty(request.SearchTerm))
            {
                query = query.Where(m => EF.Functions.ILike(m.Content, $"%{request.SearchTerm}%"));
            }

            if (request.FromDate.HasValue)
                query = query.Where(m => m.CreatedAt >= request.FromDate);

            if (request.ToDate.HasValue)
                query = query.Where(m => m.CreatedAt <= request.ToDate);

            if (request.SenderId.HasValue)
                query = query.Where(m => m.SenderId == request.SenderId);

            if (request.ChatId.HasValue)
                query = query.Where(m => m.ChatId == request.ChatId);

            var totalCount = await query.CountAsync();
            var messages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            var result = new SearchResult<MessageSearchResponse>
            {
                Items = messages.Select(m => new MessageSearchResponse
                {
                    Id = m.Id,
                    Content = m.Content,
                    CreatedAt = m.CreatedAt,
                    SenderId = m.SenderId,
                    SenderName = m.Sender.FullName,
                    ChatId = m.ChatId,
                    ChatName = m.Chat.Name
                }).ToList(),
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = request.PageSize
            };

            return Ok(result);
        }

        private async Task<IQueryable<User>> ApplySearchVisibility(
            IQueryable<User> query,
            User currentUser,
            Guid userId)
        {
            if (currentUser.IsGlobalAdmin)
            {
                // Админ видит всех
                return query;
            }

            // Проверяем, является ли пользователь руководителем отдела
            var isHead = await _context.Departments
                .AnyAsync(d => d.HeadId == userId && !d.IsDeleted);

            if (isHead && currentUser.DepartmentId.HasValue)
            {
                // Руководитель видит сотрудников своего отдела
                return query.Where(u => u.DepartmentId == currentUser.DepartmentId);
            }

            // Обычный сотрудник видит только участников своих чатов
            var userChatIds = await _context.ChatMembers
                .Where(cm => cm.UserId == userId)
                .Select(cm => cm.ChatId)
                .ToListAsync();

            var chatUserIds = await _context.ChatMembers
                .Where(cm => userChatIds.Contains(cm.ChatId))
                .Select(cm => cm.UserId)
                .Distinct()
                .ToListAsync();

            return query.Where(u => chatUserIds.Contains(u.Id));
        }
    }

    public class UserSearchRequest
    {
        public string? SearchTerm { get; set; }
        public Guid? DepartmentId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class ChatSearchRequest
    {
        public string? SearchTerm { get; set; }
        public bool? IsSystemChat { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class GlobalMessageSearchRequest
    {
        public string? SearchTerm { get; set; }
        public Guid? SenderId { get; set; }
        public Guid? ChatId { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class SearchResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    public class UserSearchResponse
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? Position { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
        public bool IsOnline { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }

    public class ChatSearchResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemChat { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public int MemberCount { get; set; }
    }

    public class MessageSearchResponse
    {
        public Guid Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public Guid ChatId { get; set; }
        public string ChatName { get; set; } = string.Empty;
    }
}
