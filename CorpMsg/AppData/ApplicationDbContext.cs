using Microsoft.EntityFrameworkCore;
using CorpMsg.Models;

namespace CorpMsg.AppData
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // DbSet для всех сущностей
        public DbSet<Company> Companies { get; set; } = null!;
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Department> Departments { get; set; } = null!;
        public DbSet<Chat> Chats { get; set; } = null!;
        public DbSet<ChatMember> ChatMembers { get; set; } = null!;
        public DbSet<ChatDepartment> ChatDepartments { get; set; } = null!;
        public DbSet<Message> Messages { get; set; } = null!;
        public DbSet<BannedWord> BannedWords { get; set; } = null!;
        public DbSet<Notification> Notifications { get; set; } = null!;
        public DbSet<UserStatus> UserStatuses { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

     
            // Составной первичный ключ для ChatMember
            modelBuilder.Entity<ChatMember>()
                .HasKey(cm => new { cm.ChatId, cm.UserId });

            // Составной первичный ключ для ChatDepartment
            modelBuilder.Entity<ChatDepartment>()
                .HasKey(cd => new { cd.ChatId, cd.DepartmentId });

            // Индексы для поиска
            modelBuilder.Entity<User>()
                .HasIndex(u => u.FullName)
                .HasDatabaseName("IX_User_FullName");

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Username)
                .IsUnique()
                .HasDatabaseName("IX_User_Username");

            modelBuilder.Entity<User>()
                .HasIndex(u => u.DepartmentId)
                .HasDatabaseName("IX_User_DepartmentId");

            modelBuilder.Entity<User>()
                .HasIndex(u => u.CompanyId)
                .HasDatabaseName("IX_User_CompanyId");

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.ChatId)
                .HasDatabaseName("IX_Message_ChatId");

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.SenderId)
                .HasDatabaseName("IX_Message_SenderId");

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.CreatedAt)
                .HasDatabaseName("IX_Message_CreatedAt");

            modelBuilder.Entity<Chat>()
                .HasIndex(c => c.DepartmentId)
                .HasDatabaseName("IX_Chat_DepartmentId");

            modelBuilder.Entity<Chat>()
                .HasIndex(c => c.IsSystemChat)
                .HasDatabaseName("IX_Chat_IsSystemChat");

            modelBuilder.Entity<Notification>()
                .HasIndex(n => n.UserId)
                .HasDatabaseName("IX_Notification_UserId");

            modelBuilder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.IsRead })
                .HasDatabaseName("IX_Notification_User_IsRead");

            modelBuilder.Entity<AuditLog>()
                .HasIndex(a => a.CompanyId)
                .HasDatabaseName("IX_AuditLog_CompanyId");

            modelBuilder.Entity<AuditLog>()
                .HasIndex(a => new { a.CompanyId, a.CreatedAt })
                .HasDatabaseName("IX_AuditLog_Company_CreatedAt");

            modelBuilder.Entity<Department>()
                .HasIndex(d => d.CompanyId)
                .HasDatabaseName("IX_Department_CompanyId");

            modelBuilder.Entity<BannedWord>()
                .HasIndex(b => b.CompanyId)
                .HasDatabaseName("IX_BannedWord_CompanyId");

            modelBuilder.Entity<BannedWord>()
                .HasIndex(b => b.Word)
                .HasDatabaseName("IX_BannedWord_Word");

            // Уникальность имени отдела в рамках компании
            modelBuilder.Entity<Department>()
                .HasIndex(d => new { d.CompanyId, d.Name })
                .IsUnique()
                .HasDatabaseName("IX_Department_Company_Name");

            // Уникальность логина пользователя в рамках компании
            modelBuilder.Entity<User>()
                .HasIndex(u => new { u.CompanyId, u.Username })
                .IsUnique()
                .HasDatabaseName("IX_User_Company_Username");

            // Связь User-Status один-к-одному
            modelBuilder.Entity<User>()
                .HasOne(u => u.Status)
                .WithOne(s => s.User)
                .HasForeignKey<UserStatus>(s => s.UserId);

            // Настройка связей и каскадного удаления
            modelBuilder.Entity<ChatMember>()
                .HasOne(cm => cm.Chat)
                .WithMany(c => c.Members)
                .HasForeignKey(cm => cm.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatMember>()
                .HasOne(cm => cm.User)
                .WithMany(u => u.ChatMemberships)
                .HasForeignKey(cm => cm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatDepartment>()
                .HasOne(cd => cd.Chat)
                .WithMany(c => c.ParticipatingDepartments)
                .HasForeignKey(cd => cd.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatDepartment>()
                .HasOne(cd => cd.Department)
                .WithMany(d => d.ParticipatingChats)
                .HasForeignKey(cd => cd.DepartmentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Message>()
                .HasOne(m => m.Chat)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Message>()
                .HasOne(m => m.Sender)
                .WithMany(u => u.Messages)
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Restrict); // Не удаляем сообщения при удалении пользователя

            modelBuilder.Entity<Message>()
                .HasOne(m => m.ForwardedFrom)
                .WithMany()
                .HasForeignKey(m => m.ForwardedFromMessageId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User)
                .WithMany(u => u.Notifications)
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AuditLog>()
                .HasOne(a => a.User)
                .WithMany(u => u.AuditLogs)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Department>()
                .HasOne(d => d.Head)
                .WithOne()
                .HasForeignKey<Department>(d => d.HeadId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Department>()
                .HasOne(d => d.Company)
                .WithMany(c => c.Departments)
                .HasForeignKey(d => d.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<User>()
                .HasOne(u => u.Company)
                .WithMany(c => c.Users)
                .HasForeignKey(u => u.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<User>()
                .HasOne(u => u.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(u => u.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull); // При удалении отдела, сотрудники остаются без отдела

            modelBuilder.Entity<Chat>()
                .HasOne(c => c.Department)
                .WithMany(d => d.OwnedChats)
                .HasForeignKey(c => c.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict); // Запрещаем удаление отдела, если есть чаты

            modelBuilder.Entity<Chat>()
                .HasOne(c => c.CreatedBy)
                .WithMany()
                .HasForeignKey(c => c.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<BannedWord>()
                .HasOne(b => b.Company)
                .WithMany()
                .HasForeignKey(b => b.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            // Настройка для JSON полей
            modelBuilder.Entity<AuditLog>()
                .Property(a => a.OldValue)
                .HasColumnType("jsonb");

            modelBuilder.Entity<AuditLog>()
                .Property(a => a.NewValue)
                .HasColumnType("jsonb");

            // Глобальные фильтры для мягкого удаления
            modelBuilder.Entity<Department>()
                .HasQueryFilter(d => !d.IsDeleted);

            modelBuilder.Entity<Chat>()
                .HasQueryFilter(c => !c.IsDeleted);

            modelBuilder.Entity<Message>()
                .HasQueryFilter(m => !m.IsDeleted);

            // Индекс для быстрого поиска по статусу
            modelBuilder.Entity<UserStatus>()
                .HasIndex(us => us.IsOnline)
                .HasDatabaseName("IX_UserStatus_IsOnline");

            modelBuilder.Entity<UserStatus>()
                .HasIndex(us => us.LastSeenAt)
                .HasDatabaseName("IX_UserStatus_LastSeenAt");

            // Составной индекс для поиска сообщений по чату и дате
            modelBuilder.Entity<Message>()
                .HasIndex(m => new { m.ChatId, m.CreatedAt })
                .HasDatabaseName("IX_Message_Chat_CreatedAt");

            // Индекс для поиска участников чата по роли
            modelBuilder.Entity<ChatMember>()
                .HasIndex(cm => new { cm.ChatId, cm.Role })
                .HasDatabaseName("IX_ChatMember_Chat_Role");
        }
    }
}