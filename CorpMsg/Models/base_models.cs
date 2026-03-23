using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CorpMsg.Models
{
    // ==========================================
    // ПЕРЕЧИСЛЕНИЯ (Enums)
    // ==========================================

    public enum ChatMemberRole
    {
        Member = 0,        // Обычный участник
        Moderator = 1,     // Модератор чата
        Head = 2           // Руководитель отдела (автоматически для системных чатов)
    }

    public enum MessageType
    {
        Text = 0,
        Media = 1       // Изображения, файлы (ссылка на MinIO)
    }

    public enum NotificationType
    {
        NewMessage = 0,
        AddedToChat = 1,
        AddedToDepartment = 2,
        UserFrozen = 3,
        System = 4
    }

    public enum AuditAction
    {
        Create = 0,
        Update = 1,
        Delete = 2,
        Freeze = 3,
        Unfreeze = 4,
        Login = 5,
        Logout = 6,
        ViewHistory = 7 // Просмотр истории чата администратором
    }

    // ==========================================
    // СУЩНОСТИ (Entities)
    // ==========================================

    /// <summary>
    /// Компания (мультитенантность)
    /// </summary>
    public class Company
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? AvatarUrl { get; set; } // Ссылка на аватар компании в MinIO
        public DateTime? AvatarUpdatedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Навигационные свойства
        public ICollection<User> Users { get; set; } = new List<User>();
        public ICollection<Department> Departments { get; set; } = new List<Department>();
    }

    /// <summary>
    /// Пользователь (Сотрудник / Глобальный администратор)
    /// </summary>
    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, MaxLength(100)]
        public string FullName { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string Username { get; set; } = string.Empty; // Логин для входа

        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? Position { get; set; } // Должность (только для информации)

        public bool IsGlobalAdmin { get; set; } = false; // Права глобального администратора

        public bool IsFrozen { get; set; } = false; // Заморозка аккаунта (декрет, увольнение)

        // Компания пользователя
        [MaxLength(500)]
        public string? AvatarUrl { get; set; }

        public DateTime? AvatarUpdatedAt { get; set; }

        [Required]
        public Guid CompanyId { get; set; }
        [ForeignKey(nameof(CompanyId))]
        public Company Company { get; set; } = null!;

        // Связь с отделом (может быть null, если админ еще не распределил)
        public Guid? DepartmentId { get; set; }
        [ForeignKey(nameof(DepartmentId))]
        public Department? Department { get; set; }

        // Навигационные свойства
        public ICollection<ChatMember> ChatMemberships { get; set; } = new List<ChatMember>();
        public ICollection<Message> Messages { get; set; } = new List<Message>();
        public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
        public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
        public UserStatus? Status { get; set; }
    }

    /// <summary>
    /// Отдел компании
    /// </summary>
    public class Department
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        // Руководитель отдела (связь 1 к 1 с User)
        public Guid? HeadId { get; set; }
        [ForeignKey(nameof(HeadId))]
        public User? Head { get; set; }

        // Компания-владелец отдела
        [Required]
        public Guid CompanyId { get; set; }
        [ForeignKey(nameof(CompanyId))]
        public Company Company { get; set; } = null!;

        // Мягкое удаление
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        public Guid? DeletedByUserId { get; set; }

        // Навигационные свойства
        public ICollection<User> Employees { get; set; } = new List<User>();

        // Чаты, для которых этот отдел является "владельцем" (обязательный отдел)
        public ICollection<Chat> OwnedChats { get; set; } = new List<Chat>();

        // Чаты, в которых этот отдел участвует (межотдельные чаты)
        public ICollection<ChatDepartment> ParticipatingChats { get; set; } = new List<ChatDepartment>();
    }

    /// <summary>
    /// Связь чата с дополнительными отделами (для межотдельных чатов)
    /// </summary>
    public class ChatDepartment
    {
        public Guid ChatId { get; set; }
        [ForeignKey(nameof(ChatId))]
        public Chat Chat { get; set; } = null!;

        public Guid DepartmentId { get; set; }
        [ForeignKey(nameof(DepartmentId))]
        public Department Department { get; set; } = null!;

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public Guid AddedByUserId { get; set; }
    }

    /// <summary>
    /// Чат (Системный, Внутриотдельный или Межотдельный)
    /// </summary>
    public class Chat
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(255)]
        public string? Description { get; set; }

        public bool IsSystemChat { get; set; } = false; // true - если это автоматически созданный чат отдела

        public bool IsUserCreated { get; set; } = false; // true - если создан обычным сотрудником

        [MaxLength(500)]
        public string? AvatarUrl { get; set; }

        public DateTime? AvatarUpdatedAt { get; set; }

        [Required]
        public Guid DepartmentId { get; set; }

        [ForeignKey(nameof(DepartmentId))]
        public Department Department { get; set; } = null!;

        // Кто создал чат
        [Required]
        public Guid CreatedByUserId { get; set; }
        [ForeignKey(nameof(CreatedByUserId))]
        public User CreatedBy { get; set; } = null!;

        // Мягкое удаление
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        public Guid? DeletedByUserId { get; set; }

        // Навигационные свойства
        public ICollection<ChatMember> Members { get; set; } = new List<ChatMember>();
        public ICollection<Message> Messages { get; set; } = new List<Message>();
        public ICollection<ChatDepartment> ParticipatingDepartments { get; set; } = new List<ChatDepartment>();
    }

    /// <summary>
    /// Участник чата (Связующая таблица Many-to-Many с ролями)
    /// </summary>
    public class ChatMember
    {
        public Guid ChatId { get; set; }
        [ForeignKey(nameof(ChatId))]
        public Chat Chat { get; set; } = null!;

        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;

        // Роль пользователя конкретно в ЭТОМ чате
        public ChatMemberRole Role { get; set; } = ChatMemberRole.Member;

        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

        // Составной первичный ключ
        public override bool Equals(object? obj) => obj is ChatMember cm && cm.ChatId == ChatId && cm.UserId == UserId;
        public override int GetHashCode() => HashCode.Combine(ChatId, UserId);
    }

    /// <summary>
    /// Сообщение
    /// </summary>
    public class Message
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string Content { get; set; } = string.Empty;

        public MessageType Type { get; set; } = MessageType.Text;

        [MaxLength(500)]
        public string? MediaUrl { get; set; } // URL для доступа к медиа

        [MaxLength(255)]
        public string? MediaFileName { get; set; } // Оригинальное имя файла

        public long? MediaFileSize { get; set; } // Размер файла в байтах

        [MaxLength(100)]
        public string? MediaContentType { get; set; } // MIME тип файла

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsEdited { get; set; } = false;

        public bool IsDeleted { get; set; } = false; // Soft-delete (помечаем как удаленное, но оставляем в БД для истории)

        // Внешние ключи
        [Required]
        public Guid ChatId { get; set; }
        [ForeignKey(nameof(ChatId))]
        public Chat Chat { get; set; } = null!;

        [Required]
        public Guid SenderId { get; set; }
        [ForeignKey(nameof(SenderId))]
        public User Sender { get; set; } = null!;

        // Самоссылающийся ключ для реализации функционала "Пересылка сообщения"
        public Guid? ForwardedFromMessageId { get; set; }
        [ForeignKey(nameof(ForwardedFromMessageId))]
        public Message? ForwardedFrom { get; set; }
    }

    /// <summary>
    /// Словарь запрещенных слов (для фильтрации)
    /// </summary>
    public class BannedWord
    {
        [Key]
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Word { get; set; } = string.Empty;

        // Привязка к конкретной компании (у каждой компании свой список)
        [Required]
        public Guid CompanyId { get; set; }
        [ForeignKey(nameof(CompanyId))]
        public Company Company { get; set; } = null!;
    }

    /// <summary>
    /// Уведомления пользователей
    /// </summary>
    public class Notification
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;

        [Required, MaxLength(255)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Content { get; set; } = string.Empty;

        public NotificationType Type { get; set; }

        public Guid? ReferenceId { get; set; } // ID чата, сообщения и т.д.

        public bool IsRead { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Статус пользователя (онлайн/оффлайн)
    /// </summary>
    public class UserStatus
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;

        public bool IsOnline { get; set; }

        public DateTime? LastSeenAt { get; set; }

        [MaxLength(255)]
        public string? ConnectionId { get; set; } // SignalR connection ID
    }

    /// <summary>
    /// Логи аудита действий пользователей
    /// </summary>
    public class AuditLog
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public User User { get; set; } = null!;

        [Required]
        public Guid CompanyId { get; set; }

        public AuditAction Action { get; set; }

        [Required, MaxLength(50)]
        public string EntityType { get; set; } = string.Empty; // "User", "Chat", "Message", "Department", "BannedWord"

        // ИЗМЕНЕНО: string вместо Guid? для поддержки разных типов ID
        public string? EntityId { get; set; }

        [Column(TypeName = "jsonb")]
        public string? OldValue { get; set; } // JSON

        [Column(TypeName = "jsonb")]
        public string? NewValue { get; set; } // JSON

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(45)]
        public string? IpAddress { get; set; }
    }
}