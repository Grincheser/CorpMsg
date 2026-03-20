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

            // Проверка уникальности названия в рамках компании
            if (await _context.Departments.AnyAsync(d =>
                d.CompanyId == companyId && d.Name == request.Name && !d.IsDeleted))
                return BadRequest("Отдел с таким названием уже существует");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var department = new Department
                {
                    Name = request.Name,
                    CompanyId = companyId
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
                    CreatedByUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier))
                };
                _context.Chats.Add(systemChat);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return Ok(new DepartmentResponse
                {
                    Id = department.Id,
                    Name = department.Name,
                    EmployeeCount = 0,
                    SystemChatId = systemChat.Id
                });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

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
                    u.DepartmentId == department.Id);

            if (user == null)
                return BadRequest("Пользователь не найден в этом отделе");

            department.HeadId = user.Id;

            // Обновляем роль в системном чате
            var systemChat = await _context.Chats
                .FirstOrDefaultAsync(c => c.DepartmentId == department.Id && c.IsSystemChat);

            if (systemChat != null)
            {
                var membership = await _context.ChatMembers
                    .FirstOrDefaultAsync(cm => cm.ChatId == systemChat.Id && cm.UserId == user.Id);

                if (membership != null)
                {
                    membership.Role = ChatMemberRole.Head;
                }
            }

            await _context.SaveChangesAsync();

            return Ok();
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
