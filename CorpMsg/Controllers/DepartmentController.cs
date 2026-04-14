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
    public class DepartmentController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public DepartmentController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Создание отдела (только глобальный админ)
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<DepartmentResponse>> CreateDepartment(CreateDepartmentRequest request)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка уникальности названия в рамках компании
            if (await _context.Departments.AnyAsync(d =>
                d.CompanyId == companyId && d.Name == request.Name && !d.IsDeleted))
                return BadRequest("Отдел с таким названием уже существует");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Создаем отдел и сразу назначаем создателя руководителем
                var department = new Department
                {
                    Name = request.Name,
                    CompanyId = companyId,
                    HeadId = currentUserId  // 👈 Назначаем создателя руководителем
                };

                _context.Departments.Add(department);
                await _context.SaveChangesAsync();

                // Создаем системный чат для отдела
                var systemChat = new Chat
                {
                    Name = $"Чат {request.Name}",
                    Description = $"Системный чат отдела {request.Name}",
                    IsSystemChat = true,
                    DepartmentId = department.Id,
                    CreatedByUserId = currentUserId
                };
                _context.Chats.Add(systemChat);
                await _context.SaveChangesAsync();

                // Добавляем создателя (руководителя) в системный чат с ролью Head
                _context.ChatMembers.Add(new ChatMember
                {
                    ChatId = systemChat.Id,
                    UserId = currentUserId,
                    Role = ChatMemberRole.Head,  // 👈 Руководитель получает роль Head
                    JoinedAt = DateTime.UtcNow
                });

                // Создаем уведомление для руководителя
                _context.Notifications.Add(new Notification
                {
                    UserId = currentUserId,
                    Title = "Отдел создан",
                    Content = $"Вы создали отдел '{department.Name}' и назначены его руководителем",
                    Type = NotificationType.System,
                    ReferenceId = department.Id
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new DepartmentResponse
                {
                    Id = department.Id,
                    Name = department.Name,
                    HeadId = department.HeadId,
                    HeadName = User.FindFirstValue(ClaimTypes.Name), // Или получите из БД
                    EmployeeCount = 1, // Создатель - первый сотрудник
                    SystemChatId = systemChat.Id
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                // Логируем ошибку
                Console.WriteLine($"Error creating department: {ex.Message}");
                return StatusCode(500, "Ошибка при создании отдела");
            }
        }

        /// <summary>
        /// Редактирование отдела (только глобальный админ)
        /// </summary>
        [HttpPut("{id}")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> UpdateDepartment(Guid id, UpdateDepartmentRequest request)
        {
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

            if (department == null)
                return NotFound();

            department.Name = request.Name;
            await _context.SaveChangesAsync();

            return Ok(new { department.Id, department.Name });
        }
        public class UpdateDepartmentRequest
        {
            public string Name { get; set; } = string.Empty;
        }
        /// <summary>
        /// Редактирование отдела (только глобальный админ)
        /// </summary>

        [HttpDelete("{id}")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> DeleteDepartment(Guid id)
        {
            var department = await _context.Departments
                .Include(d => d.Employees)
                .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

            if (department == null)
                return NotFound();

            // Проверка, есть ли сотрудники в отделе
            if (department.Employees.Any(e => !e.IsFrozen))
            {
                return BadRequest("Нельзя удалить отдел с активными сотрудниками");
            }

            department.IsDeleted = true;
            department.DeletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Отдел удален" });
        }

        // В DepartmentController.cs

        /// <summary>
        /// Назначение руководителя отдела (только глобальный админ)
        /// </summary>
        [HttpPost("{id}/set-head")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<IActionResult> SetDepartmentHead(Guid id, SetHeadRequest request)
        {
            var department = await _context.Departments
                .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

            if (department == null)
                return NotFound();

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == request.UserId &&
                    u.DepartmentId == department.Id && !u.IsFrozen);

            if (user == null)
                return BadRequest("Пользователь не найден в этом отделе");

            // Сохраняем старого руководителя для логирования
            var oldHeadId = department.HeadId;

            // Назначаем нового руководителя
            department.HeadId = user.Id;

            // 🔧 НОВОЕ: Обновляем роль в системном чате
            var systemChat = await _context.Chats
                .FirstOrDefaultAsync(c => c.DepartmentId == department.Id && c.IsSystemChat && !c.IsDeleted);

            if (systemChat != null)
            {
                // Если у старого руководителя была роль Head в системном чате, понижаем до Member
                if (oldHeadId.HasValue)
                {
                    var oldHeadMembership = await _context.ChatMembers
                        .FirstOrDefaultAsync(cm => cm.ChatId == systemChat.Id && cm.UserId == oldHeadId.Value);

                    if (oldHeadMembership != null && oldHeadMembership.Role == ChatMemberRole.Head)
                    {
                        oldHeadMembership.Role = ChatMemberRole.Member;
                    }
                }

                // Находим или создаем членство для нового руководителя
                var membership = await _context.ChatMembers
                    .FirstOrDefaultAsync(cm => cm.ChatId == systemChat.Id && cm.UserId == user.Id);

                if (membership != null)
                {
                    // Обновляем существующее членство
                    membership.Role = ChatMemberRole.Head;
                }
                else
                {
                    // Создаем новое членство с ролью Head
                    _context.ChatMembers.Add(new ChatMember
                    {
                        ChatId = systemChat.Id,
                        UserId = user.Id,
                        Role = ChatMemberRole.Head,
                        JoinedAt = DateTime.UtcNow
                    });
                }

                // Создаем уведомление для нового руководителя
                _context.Notifications.Add(new Notification
                {
                    UserId = user.Id,
                    Title = "Назначение руководителем",
                    Content = $"Вы назначены руководителем отдела '{department.Name}'",
                    Type = NotificationType.RoleChanged,
                    ReferenceId = department.Id
                });
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Руководитель отдела назначен",
                departmentId = department.Id,
                headId = user.Id,
                headName = user.FullName
            });
        }

        /// <summary>
        /// Получение списка отделов
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<DepartmentResponse>>> GetDepartments()
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var departments = await _context.Departments
                .Include(d => d.Employees)
                    .ThenInclude(e => e.Status)
                .Include(d => d.Head)
                .Where(d => d.CompanyId == companyId && !d.IsDeleted)
                .OrderBy(d => d.Name)
                .ToListAsync();

            var response = departments.Select(d => new DepartmentResponse
            {
                Id = d.Id,
                Name = d.Name,
                HeadId = d.HeadId,
                HeadName = d.Head?.FullName,
                EmployeeCount = d.Employees.Count(e => !e.IsFrozen),
                SystemChatId = _context.Chats
                    .FirstOrDefault(c => c.DepartmentId == d.Id && c.IsSystemChat)?.Id
            }).ToList();

            return Ok(response);
        }

        /// <summary>
        /// Получение информации об отделе
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<DepartmentDetailResponse>> GetDepartment(Guid id)
        {
            var department = await _context.Departments
                .Include(d => d.Employees)
                    .ThenInclude(e => e.Status)
                .Include(d => d.Head)
                .Include(d => d.OwnedChats)
                    .ThenInclude(c => c.Members)
                .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

            if (department == null)
                return NotFound();

            // Проверка доступа
            if (!await CanViewDepartment(id))
                return Forbid();

            var response = new DepartmentDetailResponse
            {
                Id = department.Id,
                Name = department.Name,
                HeadId = department.HeadId,
                HeadName = department.Head?.FullName,
                Employees = department.Employees
                    .Where(e => !e.IsFrozen)
                    .Select(e => new UserResponse
                    {
                        Id = e.Id,
                        FullName = e.FullName,
                        Username = e.Username,
                        Position = e.Position,
                        IsOnline = e.Status?.IsOnline ?? false
                    }).ToList(),
                Chats = department.OwnedChats
                    .Where(c => !c.IsDeleted)
                    .Select(c => new ChatInfoResponse
                    {
                        Id = c.Id,
                        Name = c.Name,
                        IsSystemChat = c.IsSystemChat,
                        MemberCount = c.Members.Count
                    }).ToList()
            };

            return Ok(response);
        }

        private async Task<bool> CanViewDepartment(Guid departmentId)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var user = await _context.Users.FindAsync(userId);

            if (user.IsGlobalAdmin)
                return true;

            // Руководитель отдела может видеть свой отдел
            if (await _context.Departments.AnyAsync(d => d.Id == departmentId && d.HeadId == userId))
                return true;

            // Сотрудник может видеть только свой отдел
            return user.DepartmentId == departmentId;
        }
    }

    public class CreateDepartmentRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class SetHeadRequest
    {
        public Guid UserId { get; set; }
    }

    public class DepartmentResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid? HeadId { get; set; }
        public string? HeadName { get; set; }
        public int EmployeeCount { get; set; }
        public Guid? SystemChatId { get; set; }
    }

    public class DepartmentDetailResponse : DepartmentResponse
    {
        public List<UserResponse> Employees { get; set; } = new();
        public List<ChatInfoResponse> Chats { get; set; } = new();
    }

    public class ChatInfoResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsSystemChat { get; set; }
        public int MemberCount { get; set; }
    }
}
