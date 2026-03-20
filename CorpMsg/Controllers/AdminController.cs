using CorpMsg.AppData;
using CorpMsg.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "GlobalAdmin")]
    public class AdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ========== УПРАВЛЕНИЕ ЗАПРЕЩЕННЫМИ СЛОВАМИ ==========

        /// <summary>
        /// Получение списка запрещенных слов
        /// </summary>
        [HttpGet("banned-words")]
        public async Task<ActionResult<List<BannedWordResponse>>> GetBannedWords()
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var words = await _context.BannedWords
                .Where(b => b.CompanyId == companyId)
                .OrderBy(b => b.Word)
                .ToListAsync();

            return Ok(words.Select(w => new BannedWordResponse
            {
                Id = w.Id,
                Word = w.Word
            }));
        }

        /// <summary>
        /// Добавление запрещенного слова
        /// </summary>
        [HttpPost("banned-words")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<BannedWordResponse>> AddBannedWord(AddBannedWordRequest request)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // Проверка на пустое слово
            if (string.IsNullOrWhiteSpace(request.Word))
                return BadRequest("Слово не может быть пустым");

            // Приводим к нижнему регистру и удаляем лишние пробелы
            var normalizedWord = request.Word.ToLower().Trim();

            // Проверка на дубликат
            if (await _context.BannedWords.AnyAsync(b =>
                b.CompanyId == companyId && b.Word == normalizedWord))
                return BadRequest("Такое слово уже в списке");

            var bannedWord = new BannedWord
            {
                Word = normalizedWord,
                CompanyId = companyId
            };

            _context.BannedWords.Add(bannedWord);
            await _context.SaveChangesAsync();

            // Логируем - исправлено: сериализуем в JSON объект
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = currentUserId,
                CompanyId = companyId,
                Action = AuditAction.Create,
                EntityType = "BannedWord",
                EntityId = bannedWord.Id.ToString(),
                NewValue = JsonSerializer.Serialize(new { Word = bannedWord.Word }) // Обернуть в JSON объект
            });
            await _context.SaveChangesAsync();

            return Ok(new BannedWordResponse
            {
                Id = bannedWord.Id,
                Word = bannedWord.Word
            });
        }

        /// <summary>
        /// Массовое добавление запрещенных слов
        /// </summary>
        [HttpPost("banned-words/bulk")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<BulkAddResult>> AddBannedWordsBulk(AddBannedWordsBulkRequest request)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            if (request.Words == null || !request.Words.Any())
                return BadRequest("Список слов не может быть пустым");

            var words = request.Words
                .Select(w => w.ToLower().Trim())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct()
                .ToList();

            if (!words.Any())
                return BadRequest("Нет валидных слов для добавления");

            // Получаем существующие слова
            var existingWords = await _context.BannedWords
                .Where(b => b.CompanyId == companyId && words.Contains(b.Word))
                .Select(b => b.Word)
                .ToListAsync();

            var newWords = words.Except(existingWords).ToList();

            if (!newWords.Any())
                return Ok(new BulkAddResult
                {
                    Added = 0,
                    Skipped = existingWords.Count,
                    Total = words.Count
                });

            var bannedWords = newWords.Select(w => new BannedWord
            {
                Word = w,
                CompanyId = companyId
            }).ToList();

            _context.BannedWords.AddRange(bannedWords);
            await _context.SaveChangesAsync();

            // Логируем - исправлено: сериализуем в JSON объект
            _context.AuditLogs.Add(new AuditLog
            {
                UserId = currentUserId,
                CompanyId = companyId,
                Action = AuditAction.Create,
                EntityType = "BannedWordsBulk",
                NewValue = JsonSerializer.Serialize(new { AddedWords = newWords, SkippedCount = existingWords.Count })
            });
            await _context.SaveChangesAsync();

            return Ok(new BulkAddResult
            {
                Added = newWords.Count,
                Skipped = existingWords.Count,
                Total = words.Count
            });
        }

        /// <summary>
        /// Удаление запрещенного слова
        /// </summary>
        [HttpDelete("banned-words/{id}")]
        public async Task<IActionResult> DeleteBannedWord(int id)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var word = await _context.BannedWords
                .FirstOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId);

            if (word == null)
                return NotFound();

            _context.BannedWords.Remove(word);
            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Импорт списка слов из файла
        /// </summary>
        [HttpPost("banned-words/import")]
        [Authorize(Roles = "GlobalAdmin")]
        public async Task<ActionResult<BulkAddResult>> ImportBannedWords(IFormFile file)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            if (file == null || file.Length == 0)
                return BadRequest("Файл не выбран или пуст");

            var words = new List<string>();

            using var reader = new StreamReader(file.OpenReadStream());
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var word = line.Trim().ToLower();
                if (!string.IsNullOrWhiteSpace(word))
                    words.Add(word);
            }

            if (!words.Any())
                return BadRequest("Файл не содержит валидных слов");

            words = words.Distinct().ToList();

            var existingWords = await _context.BannedWords
                .Where(b => b.CompanyId == companyId && words.Contains(b.Word))
                .Select(b => b.Word)
                .ToListAsync();

            var newWords = words.Except(existingWords).Select(w => new BannedWord
            {
                Word = w,
                CompanyId = companyId
            }).ToList();

            if (newWords.Any())
            {
                _context.BannedWords.AddRange(newWords);
                await _context.SaveChangesAsync();

                // Логируем - исправлено
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = currentUserId,
                    CompanyId = companyId,
                    Action = AuditAction.Create,
                    EntityType = "BannedWordsImport",
                    NewValue = JsonSerializer.Serialize(new { ImportedCount = newWords.Count, TotalWords = words.Count })
                });
                await _context.SaveChangesAsync();
            }

            return Ok(new BulkAddResult
            {
                Added = newWords.Count,
                Skipped = existingWords.Count,
                Total = words.Count
            });
        }

        // ========== УПРАВЛЕНИЕ КОМПАНИЕЙ ==========

        /// <summary>
        /// Получение статистики компании
        /// </summary>
        [HttpGet("statistics")]
        public async Task<ActionResult<CompanyStatistics>> GetStatistics()
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var totalUsers = await _context.Users
                .CountAsync(u => u.CompanyId == companyId);

            var activeUsers = await _context.Users
                .CountAsync(u => u.CompanyId == companyId && !u.IsFrozen);

            var frozenUsers = await _context.Users
                .CountAsync(u => u.CompanyId == companyId && u.IsFrozen);

            var onlineNow = await _context.UserStatuses
                .CountAsync(us => us.User.CompanyId == companyId && us.IsOnline);

            var totalDepartments = await _context.Departments
                .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted);

            var totalChats = await _context.Chats
                .CountAsync(c => c.Department.CompanyId == companyId && !c.IsDeleted);

            var messagesToday = await _context.Messages
                .CountAsync(m => m.Sender.CompanyId == companyId &&
                    m.CreatedAt.Date == DateTime.UtcNow.Date);

            var bannedWordsCount = await _context.BannedWords
                .CountAsync(b => b.CompanyId == companyId);

            return Ok(new CompanyStatistics
            {
                TotalUsers = totalUsers,
                ActiveUsers = activeUsers,
                FrozenUsers = frozenUsers,
                OnlineNow = onlineNow,
                TotalDepartments = totalDepartments,
                TotalChats = totalChats,
                MessagesToday = messagesToday,
                BannedWordsCount = bannedWordsCount
            });
        }

        /// <summary>
        /// Получение логов аудита
        /// </summary>
        [HttpGet("audit-logs")]
        public async Task<ActionResult<PagedResult<AuditLogResponse>>> GetAuditLogs(
            [FromQuery] AuditLogFilter filter)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var query = _context.AuditLogs
                .Include(a => a.User)
                .Where(a => a.CompanyId == companyId);

            if (filter.FromDate.HasValue)
                query = query.Where(a => a.CreatedAt >= filter.FromDate);

            if (filter.ToDate.HasValue)
                query = query.Where(a => a.CreatedAt <= filter.ToDate);

            if (filter.UserId.HasValue)
                query = query.Where(a => a.UserId == filter.UserId);

            if (filter.Action.HasValue)
                query = query.Where(a => a.Action == filter.Action);

            if (!string.IsNullOrEmpty(filter.EntityType))
                query = query.Where(a => a.EntityType == filter.EntityType);

            var totalCount = await query.CountAsync();
            var logs = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((filter.Page - 1) * filter.PageSize)
                .Take(filter.PageSize)
                .ToListAsync();

            var result = new PagedResult<AuditLogResponse>
            {
                Items = logs.Select(l => new AuditLogResponse
                {
                    Id = l.Id,
                    UserName = l.User.FullName,
                    Action = l.Action.ToString(),
                    EntityType = l.EntityType,
                    EntityId = l.EntityId,
                    OldValue = l.OldValue,
                    NewValue = l.NewValue,
                    CreatedAt = l.CreatedAt,
                    IpAddress = l.IpAddress
                }).ToList(),
                TotalCount = totalCount,
                Page = filter.Page,
                PageSize = filter.PageSize
            };

            return Ok(result);
        }

        /// <summary>
        /// Экспорт логов аудита в CSV
        /// </summary>
        [HttpGet("audit-logs/export")]
        public async Task<IActionResult> ExportAuditLogs([FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
        {
            var companyId = Guid.Parse(User.FindFirstValue("CompanyId"));

            var query = _context.AuditLogs
                .Include(a => a.User)
                .Where(a => a.CompanyId == companyId);

            if (fromDate.HasValue)
                query = query.Where(a => a.CreatedAt >= fromDate);

            if (toDate.HasValue)
                query = query.Where(a => a.CreatedAt <= toDate);

            var logs = await query
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            var csv = new StringBuilder();
            csv.AppendLine("Дата,Пользователь,Действие,Тип сущности,ID сущности,IP адрес");

            foreach (var log in logs)
            {
                csv.AppendLine($"{log.CreatedAt:yyyy-MM-dd HH:mm:ss}," +
                    $"{log.User.FullName}," +
                    $"{log.Action}," +
                    $"{log.EntityType}," +
                    $"{log.EntityId}," +
                    $"{log.IpAddress}");
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", $"audit_logs_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }

    public class AddBannedWordRequest
    {
        public string Word { get; set; } = string.Empty;
    }

    public class AddBannedWordsBulkRequest
    {
        public List<string> Words { get; set; } = new();
    }

    public class BulkAddResult
    {
        public int Added { get; set; }
        public int Skipped { get; set; }
        public int Total { get; set; }
    }

    public class BannedWordResponse
    {
        public int Id { get; set; }
        public string Word { get; set; } = string.Empty;
    }

    public class CompanyStatistics
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int FrozenUsers { get; set; }
        public int OnlineNow { get; set; }
        public int TotalDepartments { get; set; }
        public int TotalChats { get; set; }
        public int MessagesToday { get; set; }
        public int BannedWordsCount { get; set; }
    }

    public class AuditLogFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public Guid? UserId { get; set; }
        public AuditAction? Action { get; set; }
        public string? EntityType { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public class AuditLogResponse
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string? EntityId { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? IpAddress { get; set; }
    }
}
