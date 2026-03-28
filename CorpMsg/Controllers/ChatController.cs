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
    public class ChatController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ChatController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Создание чата
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<ChatResponse>> CreateChat(CreateChatRequest request)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            // Проверка прав на создание чата
            if (!await CanCreateChat(currentUserId, request.DepartmentId))
                return Forbid();

            // Проверяем существование отдела
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == request.DepartmentId &&
                    d.CompanyId == companyId && !d.IsDeleted);

            if (department == null)
                return BadRequest("Отдел не найден");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var chat = new Chat
                {
                    Name = request.Name,
                    Description = request.Description,
                    DepartmentId = request.DepartmentId,
                    CreatedByUserId = currentUserId,
                    IsUserCreated = !User.IsInRole("GlobalAdmin") &&
                        !await IsDepartmentHead(currentUserId, request.DepartmentId)
                };

                _context.Chats.Add(chat);
                await _context.SaveChangesAsync();

                // Добавляем создателя в чат (с проверкой на дубликат)
                var creatorMembership = new ChatMember
                {
                    ChatId = chat.Id,
                    UserId = currentUserId,
                    Role = ChatMemberRole.Moderator,
                    JoinedAt = DateTime.UtcNow
                };
                _context.ChatMembers.Add(creatorMembership);

                // Добавляем указанных участников (удаляем дубликаты)
                if (request.MemberIds != null && request.MemberIds.Any())
                {
                    // Убираем дубликаты и исключаем создателя, если он уже есть в списке
                    var uniqueMemberIds = request.MemberIds
                        .Distinct()
                        .Where(id => id != currentUserId) // Исключаем создателя
                        .ToList();

                    foreach (var memberId in uniqueMemberIds)
                    {
                        // Проверяем, что пользователь из той же компании и не заблокирован
                        var user = await _context.Users
                            .FirstOrDefaultAsync(u => u.Id == memberId &&
                                u.CompanyId == companyId &&
                                !u.IsFrozen);

                        if (user != null)
                        {
                            // Проверяем, не добавлен ли уже пользователь
                            var existingMember = await _context.ChatMembers
                                .FirstOrDefaultAsync(cm => cm.ChatId == chat.Id && cm.UserId == memberId);

                            if (existingMember == null)
                            {
                                _context.ChatMembers.Add(new ChatMember
                                {
                                    ChatId = chat.Id,
                                    UserId = memberId,
                                    Role = ChatMemberRole.Member,
                                    JoinedAt = DateTime.UtcNow
                                });

                                // Создаем уведомление
                                _context.Notifications.Add(new Notification
                                {
                                    UserId = memberId,
                                    Title = "Новый чат",
                                    Content = $"Вас добавили в чат {chat.Name}",
                                    Type = NotificationType.AddedToChat,
                                    ReferenceId = chat.Id
                                });
                            }
                        }
                    }
                }

                // Если это межотдельный чат, добавляем дополнительные отделы
                if (request.ParticipatingDepartmentIds != null && request.ParticipatingDepartmentIds.Any())
                {
                    var uniqueDepartmentIds = request.ParticipatingDepartmentIds.Distinct().ToList();

                    foreach (var deptId in uniqueDepartmentIds)
                    {
                        // Проверяем существование отдела
                        var dept = await _context.Departments
                            .FirstOrDefaultAsync(d => d.Id == deptId &&
                                d.CompanyId == companyId && !d.IsDeleted);

                        if (dept != null && dept.Id != request.DepartmentId) // Не добавляем основной отдел
                        {
                            var existingChatDept = await _context.ChatDepartments
                                .FirstOrDefaultAsync(cd => cd.ChatId == chat.Id && cd.DepartmentId == deptId);

                            if (existingChatDept == null)
                            {
                                _context.ChatDepartments.Add(new ChatDepartment
                                {
                                    ChatId = chat.Id,
                                    DepartmentId = deptId,
                                    AddedByUserId = currentUserId,
                                    AddedAt = DateTime.UtcNow
                                });
                            }
                        }
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Возвращаем DTO
                var chatResponse = await GetChatResponse(chat.Id);
                return Ok(chatResponse);
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();
                // Логируем ошибку
                Console.WriteLine($"Error creating chat: {ex.Message}");
                return StatusCode(500, "Ошибка при создании чата");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Unexpected error: {ex.Message}");
                throw;
            }
        }


        /// <summary>
        /// Редактирование чата
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateChat(Guid id, UpdateChatRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var chat = await _context.Chats
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted);

            if (chat == null)
                return NotFound();

            // Проверка прав (админ, руководитель отдела, модератор)
            if (!await CanManageChat(id, userId))
                return Forbid();

            chat.Name = request.Name;
            chat.Description = request.Description;
            await _context.SaveChangesAsync();

            return Ok(new { chat.Id, chat.Name, chat.Description });
        }
        public class UpdateChatRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
        }
        /// <summary>
        /// Удаление чата
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteChat(Guid id)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var chat = await _context.Chats
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted);

            if (chat == null)
                return NotFound();

            // Проверка прав (только админ или создатель)
            var user = await _context.Users.FindAsync(userId);
            if (!user.IsGlobalAdmin && chat.CreatedByUserId != userId)
                return Forbid();

            chat.IsDeleted = true;
            chat.DeletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Чат удален" });
        }

        /// <summary>
        /// Получение списка чатов пользователя
        /// </summary>
        [HttpGet("my")]
        public async Task<ActionResult<List<ChatListResponse>>> GetMyChats()
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var isAdmin = User.IsInRole("GlobalAdmin");

            IQueryable<Chat> query;

            if (isAdmin)
            {
                // Админ видит все чаты компании
                query = _context.Chats
                    .Include(c => c.Department)
                    .Include(c => c.Members)
                    .Where(c => c.Department.CompanyId == companyId && !c.IsDeleted);
            }
            else
            {
                // Обычный пользователь видит чаты, где он участник
                query = _context.Chats
                    .Include(c => c.Department)
                    .Include(c => c.Members)
                    .Where(c => c.Members.Any(m => m.UserId == userId) && !c.IsDeleted);
            }

            var chats = await query
                .OrderByDescending(c => c.Messages.Max(m => m.CreatedAt))
                .ToListAsync();

            var response = new List<ChatListResponse>();

            foreach (var chat in chats)
            {
                var lastMessage = await _context.Messages
                    .Where(m => m.ChatId == chat.Id && !m.IsDeleted)
                    .OrderByDescending(m => m.CreatedAt)
                    .FirstOrDefaultAsync();

                var userMembership = chat.Members.FirstOrDefault(m => m.UserId == userId);
                var joinedAt = userMembership?.JoinedAt ?? DateTime.MinValue;

                var unreadCount = await _context.Messages
                    .CountAsync(m => m.ChatId == chat.Id && m.CreatedAt > joinedAt);

                response.Add(new ChatListResponse
                {
                    Id = chat.Id,
                    Name = chat.Name,
                    Description = chat.Description,
                    IsSystemChat = chat.IsSystemChat,
                    DepartmentName = chat.Department.Name,
                    LastMessage = lastMessage?.Content,
                    LastMessageAt = lastMessage?.CreatedAt,
                    UnreadCount = unreadCount,
                    MemberCount = chat.Members.Count,
                    UserRole = chat.Members
                        .FirstOrDefault(m => m.UserId == userId)?.Role ?? ChatMemberRole.Member
                });
            }

            return Ok(response);
        }

        /// <summary>
        /// Получение информации о чате
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<ChatDetailResponse>> GetChat(Guid id)
        {
            if (!await CanViewChat(id))
                return Forbid();

            var response = await GetChatResponse(id);
            if (response == null)
                return NotFound();

            return Ok(response);
        }

        /// <summary>
        /// Добавление участников в чат
        /// </summary>
        [HttpPost("{id}/members")]
        public async Task<IActionResult> AddMembers(Guid id, AddMembersRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка прав - передаем userId
            if (!await CanManageChat(id, userId))
                return Forbid();

            var chat = await _context.Chats
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted);

            if (chat == null)
                return NotFound();

            foreach (var memberId in request.UserIds)
            {
                if (!chat.Members.Any(m => m.UserId == memberId))
                {
                    _context.ChatMembers.Add(new ChatMember
                    {
                        ChatId = id,
                        UserId = memberId,
                        Role = ChatMemberRole.Member,
                        JoinedAt = DateTime.UtcNow
                    });

                    _context.Notifications.Add(new Notification
                    {
                        UserId = memberId,
                        Title = "Новый чат",
                        Content = $"Вас добавили в чат {chat.Name}",
                        Type = NotificationType.AddedToChat,
                        ReferenceId = chat.Id
                    });
                }
            }

            await _context.SaveChangesAsync();
            return Ok();
        }


        /// <summary>
        /// Изменение роли участника
        /// </summary>
        [HttpPut("{id}/members/{userId}/role")]
        public async Task<IActionResult> UpdateMemberRole(Guid id, Guid targetUserId, UpdateRoleRequest request)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка прав - передаем currentUserId
            if (!await CanManageChat(id, currentUserId))
                return Forbid();

            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == id && cm.UserId == targetUserId);

            if (membership == null)
                return NotFound();

            membership.Role = request.Role;
            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Удаление участника из чата
        /// </summary>
        [HttpDelete("{id}/members/{userId}")]
        public async Task<IActionResult> RemoveMember(Guid id, Guid targetUserId)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка прав - передаем currentUserId
            if (!await CanManageChat(id, currentUserId))
                return Forbid();

            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == id && cm.UserId == targetUserId);

            if (membership == null)
                return NotFound();

            _context.ChatMembers.Remove(membership);
            await _context.SaveChangesAsync();

            return Ok();
        }

        private async Task<bool> CanCreateChat(Guid userId, Guid departmentId)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            if (await IsDepartmentHead(userId, departmentId))
                return true;

            // Обычные сотрудники могут создавать только внутриотдельные чаты
            return user.DepartmentId == departmentId;
        }

        private async Task<bool> CanViewChat(Guid chatId)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            // Руководитель отдела может видеть чаты своего отдела
            var chat = await _context.Chats
                .Include(c => c.Department)
                .FirstOrDefaultAsync(c => c.Id == chatId);

            if (chat == null)
                return false;

            if (await IsDepartmentHead(userId, chat.DepartmentId))
                return true;

            // Проверяем, является ли пользователь участником
            return await _context.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
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

            // Модератор и руководитель могут управлять чатом
            return membership.Role == ChatMemberRole.Moderator ||
                   membership.Role == ChatMemberRole.Head;
        }
        private async Task<bool> IsDepartmentHead(Guid userId, Guid departmentId)
        {
            return await _context.Departments
                .AnyAsync(d => d.Id == departmentId && d.HeadId == userId && !d.IsDeleted);
        }

        private async Task<ChatDetailResponse?> GetChatResponse(Guid chatId)
        {
            return await _context.Chats
                .Include(c => c.Department)
                .Include(c => c.Members)
                    .ThenInclude(m => m.User)
                        .ThenInclude(u => u.Status)
                .Include(c => c.ParticipatingDepartments)
                    .ThenInclude(pd => pd.Department)
                .Where(c => c.Id == chatId && !c.IsDeleted)
                .Select(c => new ChatDetailResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    Description = c.Description,
                    IsSystemChat = c.IsSystemChat,
                    DepartmentId = c.DepartmentId,
                    DepartmentName = c.Department.Name,
                    CreatedByUserId = c.CreatedByUserId,
                    CreatedAt = c.Messages.Any() ? c.Messages.Max(m => m.CreatedAt) : c.Members.Max(m => m.JoinedAt),
                    Members = c.Members.Select(m => new ChatMemberResponse
                    {
                        UserId = m.UserId,
                        FullName = m.User.FullName,
                        Username = m.User.Username,
                        Role = m.Role,
                        IsOnline = m.User.Status != null && m.User.Status.IsOnline,
                        JoinedAt = m.JoinedAt
                    }).ToList(),
                    ParticipatingDepartments = c.ParticipatingDepartments
                        .Select(pd => pd.Department.Name).ToList()
                })
                .FirstOrDefaultAsync();
        }
    }
    public class ChatResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemChat { get; set; }
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateChatRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Guid DepartmentId { get; set; }
        public List<Guid>? MemberIds { get; set; }
        public List<Guid>? ParticipatingDepartmentIds { get; set; }
    }

    public class AddMembersRequest
    {
        public List<Guid> UserIds { get; set; } = new();
    }

    public class UpdateRoleRequest
    {
        public ChatMemberRole Role { get; set; }
    }

    public class ChatListResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemChat { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public string? LastMessage { get; set; }
        public DateTime? LastMessageAt { get; set; }
        public int UnreadCount { get; set; }
        public int MemberCount { get; set; }
        public ChatMemberRole UserRole { get; set; }
    }

    public class ChatDetailResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemChat { get; set; }
        public Guid DepartmentId { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<ChatMemberResponse> Members { get; set; } = new();
        public List<string> ParticipatingDepartments { get; set; } = new();
    }
    public class ChatMemberResponse
    {
        public Guid UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public ChatMemberRole Role { get; set; }
        public bool IsOnline { get; set; }
        public DateTime JoinedAt { get; set; }
    }
}
