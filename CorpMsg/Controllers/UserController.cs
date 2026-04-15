using CorpMsg.AppData;
using CorpMsg.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public UserController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Создание сотрудника (только для администратора)
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<UserResponse>> CreateUser(CreateUserRequest request)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка уникальности логина в рамках компании
            if (await _context.Users.AnyAsync(u =>
                u.CompanyId == companyId && u.Username == request.Username))
                return BadRequest("Пользователь с таким логином уже существует");

            // Проверка существования отдела
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == request.DepartmentId &&
                    d.CompanyId == companyId && !d.IsDeleted);

            if (department == null)
                return BadRequest("Отдел не найден");

            var user = new User
            {
                FullName = request.FullName,
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Position = request.Position,
                CompanyId = companyId,
                DepartmentId = request.DepartmentId
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Автоматически добавляем в системный чат отдела
            var systemChat = await _context.Chats
                .FirstOrDefaultAsync(c => c.DepartmentId == request.DepartmentId &&
                    c.IsSystemChat);

            if (systemChat != null)
            {
                var isHead = department.HeadId == user.Id;

                _context.ChatMembers.Add(new ChatMember
                {
                    ChatId = systemChat.Id,
                    UserId = user.Id,
                    Role = isHead ? ChatMemberRole.Head : ChatMemberRole.Member,
                    JoinedAt = DateTime.UtcNow
                });
            }

            // Создаем уведомление
            _context.Notifications.Add(new Notification
            {
                UserId = user.Id,
                Title = "Добро пожаловать!",
                Content = $"Вы были добавлены в отдел {department.Name}",
                Type = NotificationType.AddedToDepartment
            });

            // Логируем - используем DTO для сериализации
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = currentUserId,
                CompanyId = companyId,
                Action = AuditAction.Create,
                EntityType = "User",
                EntityId = user.Id.ToString(),
                NewValue = JsonSerializer.Serialize(new
                {
                    user.Id,
                    user.FullName,
                    user.Username,
                    user.Position,
                    DepartmentId = user.DepartmentId
                })
            });

            await _context.SaveChangesAsync();

            // Возвращаем DTO, а не сущность User
            return Ok(new UserResponse
            {
                Id = user.Id,
                FullName = user.FullName,
                Username = user.Username,
                Position = user.Position,
                DepartmentId = user.DepartmentId,
                DepartmentName = department.Name,
                IsGlobalAdmin = user.IsGlobalAdmin,
                IsFrozen = user.IsFrozen,
                IsOnline = false,
                LastSeenAt = null
            });
        }

        /// <summary>
        /// Получение списка сотрудников (с фильтрацией по правам)
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<UserResponse>>> GetUsers([FromQuery] UserFilter filter)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var currentUser = await _context.Users.FindAsync(currentUserId);
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            IQueryable<User> query = _context.Users
                .Include(u => u.Department)
                .Include(u => u.Status)
                .Where(u => u.CompanyId == companyId && !u.IsFrozen);

            // Ограничение доступа в зависимости от роли
            if (!currentUser.IsGlobalAdmin)
            {
                if (await IsDepartmentHead(currentUserId))
                {
                    // Руководитель отдела видит только сотрудников своего отдела
                    var departmentId = currentUser.DepartmentId;
                    query = query.Where(u => u.DepartmentId == departmentId);
                }
                else
                {
                    // Обычный сотрудник видит только участников своих чатов
                    var userChatIds = await _context.ChatMembers
                        .Where(cm => cm.UserId == currentUserId)
                        .Select(cm => cm.ChatId)
                        .ToListAsync();

                    var chatUserIds = await _context.ChatMembers
                        .Where(cm => userChatIds.Contains(cm.ChatId))
                        .Select(cm => cm.UserId)
                        .Distinct()
                        .ToListAsync();

                    query = query.Where(u => chatUserIds.Contains(u.Id));
                }
            }

            // Применяем фильтры поиска
            if (!string.IsNullOrEmpty(filter.SearchTerm))
            {
                query = query.Where(u =>
                    u.FullName.Contains(filter.SearchTerm) ||
                    u.Username.Contains(filter.SearchTerm) ||
                    (u.Position != null && u.Position.Contains(filter.SearchTerm)));
            }

            if (filter.DepartmentId.HasValue)
            {
                query = query.Where(u => u.DepartmentId == filter.DepartmentId);
            }

            var users = await query
                .OrderBy(u => u.FullName)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync();

            return Ok(users.Select(MapToResponse));
        }

        /// <summary>
        /// Редактирование сотрудника (админ или руководитель отдела)
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(Guid id, UpdateUserRequest request)
        {
            var user = await _context.Users
                .Include(u => u.Department)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
                return NotFound();

            // Проверка прав
            if (!await CanEditUser(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)), user))
                return Forbid();

            // Обновляем поля
            if (!string.IsNullOrEmpty(request.FullName))
                user.FullName = request.FullName;

            if (!string.IsNullOrEmpty(request.Position))
                user.Position = request.Position;

            if (request.DepartmentId.HasValue)
            {
                var department = await _context.Departments
                    .FirstOrDefaultAsync(d => d.Id == request.DepartmentId &&
                        d.CompanyId == user.CompanyId);

                if (department == null)
                    return BadRequest("Отдел не найден");

                // Если меняется отдел, нужно обновить членство в системных чатах
                if (user.DepartmentId != request.DepartmentId)
                {
                    await UpdateDepartmentChatMembership(user, request.DepartmentId.Value);
                    user.DepartmentId = request.DepartmentId;
                }
            }

            await _context.SaveChangesAsync();
            return Ok(MapToResponse(user));
        }

        /// <summary>
        /// Заморозка/разморозка аккаунта (только глобальный админ)
        /// </summary>
        [HttpPost("{id}/toggle-freeze")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> ToggleFreeze(Guid id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound();

            user.IsFrozen = !user.IsFrozen;

            // Если заморозили - меняем статус на оффлайн
            if (user.IsFrozen)
            {
                var status = await _context.UserStatuses
                    .FirstOrDefaultAsync(s => s.UserId == user.Id);
                if (status != null)
                {
                    status.IsOnline = false;
                    status.ConnectionId = null;
                }
            }

            // Создаем уведомление
            _context.Notifications.Add(new Notification
            {
                UserId = user.Id,
                Title = user.IsFrozen ? "Аккаунт заморожен" : "Аккаунт разморожен",
                Content = user.IsFrozen ?
                    "Ваш аккаунт был заморожен администратором" :
                    "Ваш аккаунт был разморожен",
                Type = NotificationType.UserFrozen
            });

            // Логируем
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)),
                CompanyId = user.CompanyId,
                Action = user.IsFrozen ? AuditAction.Freeze : AuditAction.Unfreeze,
                EntityType = "User",
                EntityId = user.Id.ToString()
            });

            await _context.SaveChangesAsync();

            return Ok(new { user.IsFrozen });
        }

        private async Task<bool> CanEditUser(Guid editorId, User targetUser)
        {
            var editor = await _context.Users.FindAsync(editorId);
            if (editor.IsGlobalAdmin)
                return true;

            // Руководитель отдела может редактировать сотрудников своего отдела
            if (await IsDepartmentHead(editorId))
            {
                return editor.DepartmentId == targetUser.DepartmentId;
            }

            return false;
        }

        private async Task<bool> IsDepartmentHead(Guid userId)
        {
            return await _context.Departments
                .AnyAsync(d => d.HeadId == userId && !d.IsDeleted);
        }

        private async Task UpdateDepartmentChatMembership(User user, Guid newDepartmentId)
        {
            // Удаляем из старого системного чата
            var oldSystemChat = await _context.Chats
                .FirstOrDefaultAsync(c => c.DepartmentId == user.DepartmentId && c.IsSystemChat);

            if (oldSystemChat != null)
            {
                var membership = await _context.ChatMembers
                    .FirstOrDefaultAsync(cm => cm.ChatId == oldSystemChat.Id && cm.UserId == user.Id);

                if (membership != null)
                    _context.ChatMembers.Remove(membership);
            }

            // Добавляем в новый системный чат
            var newSystemChat = await _context.Chats
                .FirstOrDefaultAsync(c => c.DepartmentId == newDepartmentId && c.IsSystemChat);

            if (newSystemChat != null)
            {
                _context.ChatMembers.Add(new ChatMember
                {
                    ChatId = newSystemChat.Id,
                    UserId = user.Id,
                    Role = ChatMemberRole.Member,
                    JoinedAt = DateTime.UtcNow
                });
            }
        }

        private UserResponse MapToResponse(User user)
        {
            return new UserResponse
            {
                Id = user.Id,
                FullName = user.FullName,
                Username = user.Username,
                Position = user.Position,
                DepartmentId = user.DepartmentId,
                DepartmentName = user.Department?.Name,
                IsGlobalAdmin = user.IsGlobalAdmin,
                IsFrozen = user.IsFrozen,
                IsOnline = user.Status?.IsOnline ?? false,
                LastSeenAt = user.Status?.LastSeenAt
            };
        }

        /// <summary>
        /// Удаление сотрудника (только глобальный администратор)
        /// </summary>
        [HttpDelete("{id}")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            // Нельзя удалить самого себя
            if (currentUserId == id)
                return BadRequest("Нельзя удалить свой собственный аккаунт");

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == id && u.CompanyId == companyId && !u.IsDeleted);

            if (user == null)
                return NotFound("Пользователь не найден");

            // Проверка, является ли пользователь руководителем отдела
            var isDepartmentHead = await _context.Departments
                .AnyAsync(d => d.HeadId == id && !d.IsDeleted);

            if (isDepartmentHead)
            {
                return BadRequest("Нельзя удалить руководителя отдела. Сначала назначьте нового руководителя.");
            }

            // Сохраняем информацию для лога
            var userInfo = new { user.FullName, user.Username, user.Position };

            // Мягкое удаление
            user.IsDeleted = true;
            user.DeletedAt = DateTime.UtcNow;
            user.DeletedByUserId = currentUserId;
            user.IsFrozen = true;
            user.Username = $"deleted_{user.Id}";
            user.FullName = "Пользователь удален";
            user.Position = null;
            user.AvatarUrl = null;

            await _context.SaveChangesAsync();

            // Логируем удаление
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = currentUserId,
                CompanyId = companyId,
                Action = AuditAction.Delete,
                EntityType = "User",
                EntityId = user.Id.ToString(),
                OldValue = JsonSerializer.Serialize(userInfo),
                NewValue = JsonSerializer.Serialize(new { Deleted = true })
            });
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Пользователь успешно удален",
                userId = id
            });
        }
    }

    public class CreateUserRequest
    {
        public string FullName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? Position { get; set; }
        public Guid DepartmentId { get; set; }
    }

    public class UpdateUserRequest
    {
        public string? FullName { get; set; }
        public string? Position { get; set; }
        public Guid? DepartmentId { get; set; }
    }

    public class UserFilter
    {
        public string? SearchTerm { get; set; }
        public Guid? DepartmentId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class UserResponse
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? Position { get; set; }
        public Guid? DepartmentId { get; set; }
        public string? DepartmentName { get; set; }
        public bool IsGlobalAdmin { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsOnline { get; set; }
        public DateTime? LastSeenAt { get; set; }
    }
}
