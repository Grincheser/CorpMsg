using CorpMsg.AppData;
using CorpMsg.Models;
using CorpMsg.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IPasswordHasher _passwordHasher;

        public AuthController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IPasswordHasher passwordHasher)
        {
            _context = context;
            _configuration = configuration;
            _passwordHasher = passwordHasher;
        }

        /// <summary>
        /// Регистрация новой компании (шаг 1-3 из ТЗ)
        /// </summary>
        [HttpPost("register-company")]
        public async Task<ActionResult<AuthResponse>> RegisterCompany(RegisterCompanyRequest request)
        {
            // Проверка уникальности названия компании
            if (await _context.Companies.AnyAsync(c => c.Name == request.CompanyName))
                return BadRequest("Компания с таким названием уже существует");

            // Проверка уникальности логина
            if (await _context.Users.AnyAsync(u => u.Username == request.Username))
                return BadRequest("Пользователь с таким логином уже существует");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Шаг 1: Создание компании
                var company = new Company
                {
                    Name = request.CompanyName,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Companies.Add(company);
                await _context.SaveChangesAsync();

                // Шаг 2: Создание первого администратора
                var admin = new User
                {
                    FullName = request.FullName,
                    Username = request.Username,
                    PasswordHash = _passwordHasher.Hash(request.Password),
                    IsGlobalAdmin = true,
                    CompanyId = company.Id
                };
                _context.Users.Add(admin);
                await _context.SaveChangesAsync();

                // Шаг 3: Создание отдела "Администрация"
                var adminDepartment = new Department
                {
                    Name = "Администрация",
                    CompanyId = company.Id,
                    HeadId = admin.Id
                };
                _context.Departments.Add(adminDepartment);
                await _context.SaveChangesAsync();

                // Назначаем администратора в отдел
                admin.DepartmentId = adminDepartment.Id;
                await _context.SaveChangesAsync();

                // Шаг 4: Создание системного чата
                var systemChat = new Chat
                {
                    Name = "Чат администрации",
                    Description = "Системный чат отдела администрации",
                    IsSystemChat = true,
                    DepartmentId = adminDepartment.Id,
                    CreatedByUserId = admin.Id
                };
                _context.Chats.Add(systemChat);
                await _context.SaveChangesAsync();

                // Добавляем администратора в чат
                _context.ChatMembers.Add(new ChatMember
                {
                    ChatId = systemChat.Id,
                    UserId = admin.Id,
                    Role = ChatMemberRole.Head,
                    JoinedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                // Создаем запись в аудите
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = admin.Id,
                    CompanyId = company.Id,
                    Action = AuditAction.Create,
                    EntityType = "Company",
                    EntityId = company.Id.ToString(), // Используем строку
                    NewValue = JsonSerializer.Serialize(new
                    {
                        company.Id,
                        company.Name,
                        company.CreatedAt
                    }), // Сериализуем только нужные поля, без навигационных свойств
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                });
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                // Генерируем JWT токен
                var token = GenerateJwtToken(admin);

                return Ok(new AuthResponse
                {
                    Token = token,
                    UserId = admin.Id,
                    Username = admin.Username,
                    FullName = admin.FullName,
                    IsGlobalAdmin = admin.IsGlobalAdmin,
                    CompanyId = company.Id
                });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Вход в систему
        /// </summary>
        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
        {
            var user = await _context.Users
                .Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            if (user == null)
                return Unauthorized("Неверный логин или пароль");

            if (user.IsFrozen)
                return Unauthorized("Аккаунт заморожен. Обратитесь к администратору");

            if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
                return Unauthorized("Неверный логин или пароль");

            // Обновляем статус
            var status = await _context.UserStatuses
                .FirstOrDefaultAsync(s => s.UserId == user.Id);

            if (status == null)
            {
                status = new UserStatus { UserId = user.Id };
                _context.UserStatuses.Add(status);
            }
            status.IsOnline = true;
            status.LastSeenAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Логируем вход
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = user.Id,
                CompanyId = user.CompanyId,
                Action = AuditAction.Login,
                EntityType = "User",
                EntityId = user.Id.ToString(),
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _context.SaveChangesAsync();

            var token = GenerateJwtToken(user);

            return Ok(new AuthResponse
            {
                Token = token,
                UserId = user.Id,
                Username = user.Username,
                FullName = user.FullName,
                IsGlobalAdmin = user.IsGlobalAdmin,
                CompanyId = user.CompanyId
            });
        }

        /// <summary>
        /// Выход из системы
        /// </summary>
        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userId, out var userGuid))
            {
                var status = await _context.UserStatuses
                    .FirstOrDefaultAsync(s => s.UserId == userGuid);

                if (status != null)
                {
                    status.IsOnline = false;
                    status.LastSeenAt = DateTime.UtcNow;
                    status.ConnectionId = null;
                    await _context.SaveChangesAsync();
                }

                // Логируем выход
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = userGuid,
                    Action = AuditAction.Logout,
                    EntityType = "User",
                    EntityId = userGuid.ToString(),
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                });
                await _context.SaveChangesAsync();
            }

            return Ok();
        }

        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.IsGlobalAdmin ? "GlobalAdmin" : "User"),
            new Claim("CompanyId", user.CompanyId.ToString())
        };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(8),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class RegisterCompanyRequest
    {
        public string CompanyName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class AuthResponse
    {
        public string Token { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsGlobalAdmin { get; set; }
        public Guid CompanyId { get; set; }
    }
}
