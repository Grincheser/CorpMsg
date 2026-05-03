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
            var isGlobalAdmin = User.IsInRole("GlobalAdmin");

            Guid departmentId;

            // 1. Если админ и не указал DepartmentId - ошибка
            if (isGlobalAdmin && !request.DepartmentId.HasValue)
            {
                return BadRequest("Глобальный администратор должен указать DepartmentId");
            }

            // 2. Если DepartmentId указан - проверяем его
            if (request.DepartmentId.HasValue)
            {
                departmentId = request.DepartmentId.Value;

                var department = await _context.Departments
                    .FirstOrDefaultAsync(d => d.Id == departmentId &&
                        d.CompanyId == companyId && !d.IsDeleted);

                if (department == null)
                    return BadRequest("Указанный отдел не найден");

                // Проверяем, что пользователь имеет право создавать чат в этом отделе
                if (!await CanCreateChatInDepartment(currentUserId, departmentId))
                    return Forbid("У вас нет прав на создание чата в этом отделе");
            }
            else
            {
                // 3. Если DepartmentId не указан - определяем по пользователю
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == currentUserId);

                if (user == null)
                    return Unauthorized();

                if (!user.DepartmentId.HasValue)
                    return BadRequest("У вас нет отдела. Укажите DepartmentId явно.");

                departmentId = user.DepartmentId.Value;

                // Для обычных пользователей проверяем права на создание чата в их отделе
                if (!await CanCreateChatInDepartment(currentUserId, departmentId))
                    return Forbid("У вас нет прав на создание чата в вашем отделе");
            }

            // Проверяем существование отдела (еще раз для безопасности)
            var targetDepartment = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == departmentId &&
                    d.CompanyId == companyId && !d.IsDeleted);

            if (targetDepartment == null)
                return BadRequest("Отдел не найден");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var chat = new Chat
                {
                    Name = request.Name,
                    Description = request.Description,
                    DepartmentId = departmentId,
                    CreatedByUserId = currentUserId,
                    IsUserCreated = !isGlobalAdmin &&
                        !await IsDepartmentHead(currentUserId, departmentId)
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
                        .Where(id => id != currentUserId)
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

                        if (dept != null && dept.Id != departmentId) // Не добавляем основной отдел
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
                Console.WriteLine($"Error creating chat: {ex.Message}");
                return StatusCode(500, "Ошибка при создании чата");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Unexpected error: {ex.Message}");
                return StatusCode(500, "Внутренняя ошибка сервера");
            }
        }

        /// <summary>
        /// Проверка прав на создание чата в отделе
        /// </summary>
        private async Task<bool> CanCreateChatInDepartment(Guid userId, Guid departmentId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return false;

            // Глобальный администратор может всегда
            if (user.IsGlobalAdmin) return true;

            // Руководитель отдела может всегда
            if (await IsDepartmentHead(userId, departmentId)) return true;

            // Обычный пользователь - проверяем настройки отдела
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == departmentId && !d.IsDeleted);

            if (department == null) return false;

            // Проверяем, разрешено ли создавать чаты обычным сотрудникам
            if (!department.AllowRegularUsersToCreateChats) return false;

            // И что пользователь состоит в этом отделе
            return user.DepartmentId == departmentId;
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

            // Получаем информацию о пользователе
            var currentUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (currentUser == null)
                return NotFound("Пользователь не найден");

            // Проверяем, является ли пользователь руководителем какого-либо отдела
            var isDepartmentHead = await _context.Departments
                .AnyAsync(d => d.HeadId == userId && !d.IsDeleted);

            IQueryable<Chat> query;

            if (isAdmin)
            {
                // Админ видит все чаты компании
                query = _context.Chats
                    .Include(c => c.Department)
                    .Include(c => c.Members)
                    .Where(c => c.Department.CompanyId == companyId && !c.IsDeleted);
            }
            else if (isDepartmentHead && currentUser.DepartmentId.HasValue)
            {
                // 🔧 ИСПРАВЛЕНИЕ: Руководитель видит ВСЕ чаты своего отдела
                // Даже те, в которых он не состоит как участник
                query = _context.Chats
                    .Include(c => c.Department)
                    .Include(c => c.Members)
                    .Where(c => c.DepartmentId == currentUser.DepartmentId.Value && !c.IsDeleted);
            }
            else
            {
                // Обычный пользователь видит только чаты, где он участник
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

                // Для руководителя отдела, если он не участник чата, joinedAt = DateTime.MinValue
                // Это значит, что все сообщения будут считаться непрочитанными (что логично)
                var userMembership = chat.Members.FirstOrDefault(m => m.UserId == userId);
                var joinedAt = userMembership?.JoinedAt ?? DateTime.MinValue;

                var unreadCount = await _context.Messages
                    .CountAsync(m => m.ChatId == chat.Id && m.CreatedAt > joinedAt);

                // Определяем роль пользователя в чате
                ChatMemberRole userRole = ChatMemberRole.Member;
                if (userMembership != null)
                {
                    userRole = userMembership.Role;
                }
                else if (isDepartmentHead && chat.DepartmentId == currentUser.DepartmentId)
                {
                    // Руководитель отдела, не являющийся участником чата,
                    // все равно имеет права руководителя для целей мониторинга
                    userRole = ChatMemberRole.Head;
                }

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
                    // 🔧 ИСПРАВЛЕНИЕ: Правильно определяем роль пользователя в чате
                    UserRole = GetUserRoleInChat(chat, userId, isDepartmentHead, currentUser?.DepartmentId)
                });
            }

            return Ok(response);
        }
        private ChatMemberRole GetUserRoleInChat(Chat chat, Guid userId, bool isDepartmentHead, Guid? userDepartmentId)
        {
            var membership = chat.Members.FirstOrDefault(m => m.UserId == userId);

            if (membership != null)
            {
                return membership.Role;
            }

            // Если пользователь не участник, но является руководителем отдела-владельца чата
            if (isDepartmentHead && chat.DepartmentId == userDepartmentId)
            {
                return ChatMemberRole.Head;
            }

            return ChatMemberRole.Member;
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

            if (user == null) return false;

            // Глобальный администратор может всегда
            if (user.IsGlobalAdmin)
                return true;

            // Руководитель отдела может всегда
            if (await IsDepartmentHead(userId, departmentId))
                return true;

            // Получаем отдел и проверяем настройки
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == departmentId && !d.IsDeleted);

            if (department == null)
                return false;

            // Проверяем, разрешено ли обычным сотрудникам создавать чаты
            if (!department.AllowRegularUsersToCreateChats)
                return false;

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

        // Тестовые контроллеры
        /// <summary>
        /// Множественное удаление чатов (с каскадным удалением всех связанных данных)
        /// </summary>
        [HttpPost("bulk-delete")]
        public async Task<IActionResult> BulkDeleteChats(BulkDeleteChatsRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                return NotFound("Пользователь не найден");

            if (request.ChatIds == null || !request.ChatIds.Any())
                return BadRequest("Не указаны чаты для удаления");

            // Убираем дубликаты
            var uniqueChatIds = request.ChatIds.Distinct().ToList();

            // Получаем чаты с проверкой прав
            var chatsToDelete = new List<Chat>();
            var errors = new List<BulkDeleteError>();

            foreach (var chatId in uniqueChatIds)
            {
                var chat = await _context.Chats
                    .Include(c => c.Messages)
                    .Include(c => c.Members)
                    .Include(c => c.ParticipatingDepartments)
                    .FirstOrDefaultAsync(c => c.Id == chatId && !c.IsDeleted);

                if (chat == null)
                {
                    errors.Add(new BulkDeleteError
                    {
                        ChatId = chatId,
                        Reason = "Чат не найден или уже удален"
                    });
                    continue;
                }

                // Проверка прав на удаление
                if (!await CanDeleteChat(chat, user))
                {
                    errors.Add(new BulkDeleteError
                    {
                        ChatId = chatId,
                        Reason = "Недостаточно прав для удаления чата"
                    });
                    continue;
                }

                // Проверка, является ли чат системным
                if (chat.IsSystemChat && !user.IsGlobalAdmin)
                {
                    errors.Add(new BulkDeleteError
                    {
                        ChatId = chatId,
                        Reason = "Системные чаты может удалять только глобальный администратор"
                    });
                    continue;
                }

                chatsToDelete.Add(chat);
            }

            if (!chatsToDelete.Any())
            {
                return BadRequest(new
                {
                    message = "Нет чатов для удаления",
                    errors = errors
                });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var deletedCount = 0;
                var deletedMessagesCount = 0;
                var deletedMembersCount = 0;

                foreach (var chat in chatsToDelete)
                {
                    // Сохраняем информацию для лога
                    var chatInfo = new
                    {
                        chat.Id,
                        chat.Name,
                        chat.Description,
                        chat.IsSystemChat,
                        MessageCount = chat.Messages.Count,
                        MemberCount = chat.Members.Count
                    };

                    // 1. Помечаем все сообщения как удаленные (soft delete)
                    foreach (var message in chat.Messages)
                    {
                        message.IsDeleted = true;
                        deletedMessagesCount++;
                    }

                    // 2. Удаляем участников чата
                    _context.ChatMembers.RemoveRange(chat.Members);
                    deletedMembersCount += chat.Members.Count;

                    // 3. Удаляем связи с отделами
                    _context.ChatDepartments.RemoveRange(chat.ParticipatingDepartments);

                    // 4. Помечаем сам чат как удаленный
                    chat.IsDeleted = true;
                    chat.DeletedAt = DateTime.UtcNow;
                    chat.DeletedByUserId = userId;

                    deletedCount++;

                    // Логируем удаление
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        CompanyId = user.CompanyId,
                        Action = AuditAction.Delete,
                        EntityType = "Chat",
                        EntityId = chat.Id.ToString(),
                        OldValue = System.Text.Json.JsonSerializer.Serialize(chatInfo),
                        NewValue = System.Text.Json.JsonSerializer.Serialize(new { Deleted = true, DeletedAt = DateTime.UtcNow })
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Отправляем уведомления через SignalR
                await NotifyChatsDeleted(chatsToDelete.Select(c => c.Id).ToList());

                return Ok(new BulkDeleteResult
                {
                    DeletedCount = deletedCount,
                    DeletedMessagesCount = deletedMessagesCount,
                    DeletedMembersCount = deletedMembersCount,
                    Errors = errors,
                    ChatIds = chatsToDelete.Select(c => c.Id).ToList()
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Error in bulk delete: {ex.Message}");
                return StatusCode(500, new
                {
                    message = "Ошибка при массовом удалении чатов",
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Полное удаление чатов (hard delete) - только для администраторов
        /// </summary>
        [HttpPost("bulk-delete-permanent")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> BulkDeleteChatsPermanent(BulkDeleteChatsRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _context.Users.FindAsync(userId);

            if (request.ChatIds == null || !request.ChatIds.Any())
                return BadRequest("Не указаны чаты для удаления");

            var uniqueChatIds = request.ChatIds.Distinct().ToList();

            // Получаем чаты с полными данными
            var chatsToDelete = await _context.Chats
                .Include(c => c.Messages)
                    .ThenInclude(m => m.ForwardedFrom)
                .Include(c => c.Members)
                .Include(c => c.ParticipatingDepartments)
                .Where(c => uniqueChatIds.Contains(c.Id) && !c.IsDeleted)
                .ToListAsync();

            if (!chatsToDelete.Any())
                return BadRequest("Чаты для удаления не найдены");

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var result = new BulkDeleteResult
                {
                    DeletedCount = chatsToDelete.Count,
                    ChatIds = chatsToDelete.Select(c => c.Id).ToList()
                };

                // Полное удаление всех связанных данных
                foreach (var chat in chatsToDelete)
                {
                    // 1. Удаляем все сообщения (hard delete)
                    _context.Messages.RemoveRange(chat.Messages);
                    result.DeletedMessagesCount += chat.Messages.Count;

                    // 2. Удаляем участников
                    _context.ChatMembers.RemoveRange(chat.Members);
                    result.DeletedMembersCount += chat.Members.Count;

                    // 3. Удаляем связи с отделами
                    _context.ChatDepartments.RemoveRange(chat.ParticipatingDepartments);

                    // 4. Логируем перед удалением
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        CompanyId = user.CompanyId,
                        Action = AuditAction.Delete,
                        EntityType = "Chat",
                        EntityId = chat.Id.ToString(),
                        OldValue = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            chat.Id,
                            chat.Name,
                            chat.Description,
                            chat.IsSystemChat,
                            MessageCount = chat.Messages.Count,
                            MemberCount = chat.Members.Count
                        })
                    });

                    // 5. Удаляем сам чат
                    _context.Chats.Remove(chat);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Отправляем уведомления
                await NotifyChatsDeletedPermanent(chatsToDelete.Select(c => c.Id).ToList());

                return Ok(result);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "Ошибка при полном удалении чатов", error = ex.Message });
            }
        }

        /// <summary>
        /// Восстановление удаленных чатов (soft delete recovery)
        /// </summary>
        [HttpPost("bulk-restore")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> BulkRestoreChats(BulkDeleteChatsRequest request)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _context.Users.FindAsync(userId);

            if (request.ChatIds == null || !request.ChatIds.Any())
                return BadRequest("Не указаны чаты для восстановления");

            var uniqueChatIds = request.ChatIds.Distinct().ToList();

            // Получаем удаленные чаты
            var chatsToRestore = await _context.Chats
                .IgnoreQueryFilters()
                .Where(c => uniqueChatIds.Contains(c.Id) && c.IsDeleted)
                .ToListAsync();

            if (!chatsToRestore.Any())
                return BadRequest("Чаты для восстановления не найдены");

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                foreach (var chat in chatsToRestore)
                {
                    // Восстанавливаем чат
                    chat.IsDeleted = false;
                    chat.DeletedAt = null;
                    chat.DeletedByUserId = null;

                    // Восстанавливаем сообщения
                    var messages = await _context.Messages
                        .IgnoreQueryFilters()
                        .Where(m => m.ChatId == chat.Id && m.IsDeleted)
                        .ToListAsync();

                    foreach (var message in messages)
                    {
                        message.IsDeleted = false;
                    }

                    // Логируем восстановление
                    _context.AuditLogs.Add(new AuditLog
                    {
                        UserId = userId,
                        CompanyId = user.CompanyId,
                        Action = AuditAction.Update,
                        EntityType = "Chat",
                        EntityId = chat.Id.ToString(),
                        OldValue = System.Text.Json.JsonSerializer.Serialize(new { IsDeleted = true }),
                        NewValue = System.Text.Json.JsonSerializer.Serialize(new { IsDeleted = false, RestoredAt = DateTime.UtcNow })
                    });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new BulkDeleteResult
                {
                    DeletedCount = chatsToRestore.Count,
                    ChatIds = chatsToRestore.Select(c => c.Id).ToList()
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "Ошибка при восстановлении чатов", error = ex.Message });
            }
        }

        // Вспомогательные методы

        private async Task<bool> CanDeleteChat(Chat chat, User user)
        {
            if (user.IsGlobalAdmin)
                return true;

            // Руководитель отдела может удалять чаты своего отдела
            if (await IsDepartmentHead(user.Id, chat.DepartmentId))
                return true;

            // Создатель чата может удалить свой чат
            if (chat.CreatedByUserId == user.Id)
                return true;

            // Модератор может удалить чат, если он его создал
            var membership = await _context.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == chat.Id && cm.UserId == user.Id);

            return membership?.Role == ChatMemberRole.Moderator && chat.CreatedByUserId == user.Id;
        }

        private async Task NotifyChatsDeleted(List<Guid> chatIds)
        {
            // Получаем всех участников удаленных чатов
            var memberIds = await _context.ChatMembers
                .Where(cm => chatIds.Contains(cm.ChatId))
                .Select(cm => cm.UserId)
                .Distinct()
                .ToListAsync();

            foreach (var memberId in memberIds)
            {
                // Создаем уведомление
                _context.Notifications.Add(new Notification
                {
                    UserId = memberId,
                    Title = "Чаты удалены",
                    Content = $"Было удалено {chatIds.Count} чат(ов), в которых вы участвовали",
                    Type = NotificationType.System,
                    ReferenceId = null
                });
            }

            await _context.SaveChangesAsync();
        }

        private async Task NotifyChatsDeletedPermanent(List<Guid> chatIds)
        {
            await NotifyChatsDeleted(chatIds);
            // Здесь можно добавить дополнительную логику для permanent удаления
        }
    }
    /// <summary>
    /// Запрос на массовое удаление чатов
    /// </summary>
    public class BulkDeleteChatsRequest
    {
        public List<Guid> ChatIds { get; set; } = new();
        public bool DeleteMessagesOnly { get; set; } = false; // Удалить только сообщения, но не чат
    }

    /// <summary>
    /// Результат массового удаления
    /// </summary>
    public class BulkDeleteResult
    {
        public int DeletedCount { get; set; }
        public int DeletedMessagesCount { get; set; }
        public int DeletedMembersCount { get; set; }
        public List<BulkDeleteError> Errors { get; set; } = new();
        public List<Guid> ChatIds { get; set; } = new();
        public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Ошибка при массовом удалении
    /// </summary>
    public class BulkDeleteError
    {
        public Guid ChatId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
    // Тест ^
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
        public Guid? DepartmentId { get; set; }
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
