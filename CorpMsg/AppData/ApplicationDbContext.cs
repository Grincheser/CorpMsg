using Microsoft.EntityFrameworkCore;
using CorpMsg.Models;
using CorpMsg.Service;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CorpMsg.AppData
{
    public class ApplicationDbContext : DbContext
    {
        private readonly IEncryptionService? _encryptionService;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IEncryptionService? encryptionService = null)
            : base(options)
        {
            _encryptionService = encryptionService;
        }

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

        /// <summary>
        /// Конвертер для шифрования строковых полей
        /// </summary>
        private class EncryptionConverter : ValueConverter<string, string>
        {
            public EncryptionConverter(IEncryptionService encryptionService)
                : base(
                    v => encryptionService.Encrypt(v),      // В БД - шифруем
                    v => encryptionService.Decrypt(v))      // Из БД - дешифруем
            {
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ==================== ПРИМЕНЯЕМ ШИФРОВАНИЕ ====================
            if (_encryptionService != null)
            {
                var encryptionConverter = new EncryptionConverter(_encryptionService);

                // 🔐 Шифрование для User
                modelBuilder.Entity<User>(entity =>
                {
                    entity.Property(u => u.FullName).HasConversion(encryptionConverter);
                    entity.Property(u => u.Username).HasConversion(encryptionConverter);
                    entity.Property(u => u.Position).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для Message
                modelBuilder.Entity<Message>(entity =>
                {
                    entity.Property(m => m.Content).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для Notification
                modelBuilder.Entity<Notification>(entity =>
                {
                    entity.Property(n => n.Title).HasConversion(encryptionConverter);
                    entity.Property(n => n.Content).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для Chat
                modelBuilder.Entity<Chat>(entity =>
                {
                    entity.Property(c => c.Name).HasConversion(encryptionConverter);
                    entity.Property(c => c.Description).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для Company
                modelBuilder.Entity<Company>(entity =>
                {
                    entity.Property(c => c.Name).HasConversion(encryptionConverter);
                 //   entity.Property(c => c.TaxId).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для Department
                modelBuilder.Entity<Department>(entity =>
                {
                    entity.Property(d => d.Name).HasConversion(encryptionConverter);
                });

                // 🔐 Шифрование для BannedWord
                modelBuilder.Entity<BannedWord>(entity =>
                {
                    entity.Property(b => b.Word).HasConversion(encryptionConverter);
                });
            }

            // ==================== QUERY FILTERS ====================
            modelBuilder.Entity<User>()
                .HasQueryFilter(u => !u.IsDeleted);

            modelBuilder.Entity<Department>()
                .HasQueryFilter(d => !d.IsDeleted);

            modelBuilder.Entity<Chat>()
                .HasQueryFilter(c => !c.IsDeleted);

            modelBuilder.Entity<Message>()
                .HasQueryFilter(m => !m.IsDeleted);

            // ==================== KEYS ====================
            modelBuilder.Entity<ChatMember>()
                .HasKey(cm => new { cm.ChatId, cm.UserId });

            modelBuilder.Entity<ChatDepartment>()
                .HasKey(cd => new { cd.ChatId, cd.DepartmentId });

            // ==================== ИНДЕКСЫ (только НЕ зашифрованные поля) ====================

            // User индексы (убраны индексы на зашифрованных полях FullName и Username)
            modelBuilder.Entity<User>()
                .HasIndex(u => u.DepartmentId)
                .HasDatabaseName("IX_User_DepartmentId");

            modelBuilder.Entity<User>()
                .HasIndex(u => u.CompanyId)
                .HasDatabaseName("IX_User_CompanyId");

            // Message индексы
            modelBuilder.Entity<Message>()
                .HasIndex(m => m.ChatId)
                .HasDatabaseName("IX_Message_ChatId");

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.SenderId)
                .HasDatabaseName("IX_Message_SenderId");

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.CreatedAt)
                .HasDatabaseName("IX_Message_CreatedAt");

            modelBuilder.Entity<Message>()
                .HasIndex(m => new { m.ChatId, m.CreatedAt })
                .HasDatabaseName("IX_Message_Chat_CreatedAt");

            // Chat индексы
            modelBuilder.Entity<Chat>()
                .HasIndex(c => c.DepartmentId)
                .HasDatabaseName("IX_Chat_DepartmentId");

            modelBuilder.Entity<Chat>()
                .HasIndex(c => c.IsSystemChat)
                .HasDatabaseName("IX_Chat_IsSystemChat");

            // Notification индексы
            modelBuilder.Entity<Notification>()
                .HasIndex(n => n.UserId)
                .HasDatabaseName("IX_Notification_UserId");

            modelBuilder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.IsRead })
                .HasDatabaseName("IX_Notification_User_IsRead");

            // AuditLog индексы
            modelBuilder.Entity<AuditLog>()
                .HasIndex(a => a.CompanyId)
                .HasDatabaseName("IX_AuditLog_CompanyId");

            modelBuilder.Entity<AuditLog>()
                .HasIndex(a => new { a.CompanyId, a.CreatedAt })
                .HasDatabaseName("IX_AuditLog_Company_CreatedAt");

            // Department индексы (убран уникальный индекс на Name)
            modelBuilder.Entity<Department>()
                .HasIndex(d => d.CompanyId)
                .HasDatabaseName("IX_Department_CompanyId");

            // BannedWord индексы (убран индекс на Word)
            modelBuilder.Entity<BannedWord>()
                .HasIndex(b => b.CompanyId)
                .HasDatabaseName("IX_BannedWord_CompanyId");

            // UserStatus индексы
            modelBuilder.Entity<UserStatus>()
                .HasIndex(us => us.IsOnline)
                .HasDatabaseName("IX_UserStatus_IsOnline");

            modelBuilder.Entity<UserStatus>()
                .HasIndex(us => us.LastSeenAt)
                .HasDatabaseName("IX_UserStatus_LastSeenAt");

            // ChatMember индексы
            modelBuilder.Entity<ChatMember>()
                .HasIndex(cm => new { cm.ChatId, cm.Role })
                .HasDatabaseName("IX_ChatMember_Chat_Role");

            // ==================== СВЯЗИ (RELATIONSHIPS) ====================

            // User - UserStatus (один к одному)
            modelBuilder.Entity<User>()
                .HasOne(u => u.Status)
                .WithOne(s => s.User)
                .HasForeignKey<UserStatus>(s => s.UserId);

            // User - Company
            modelBuilder.Entity<User>()
                .HasOne(u => u.Company)
                .WithMany(c => c.Users)
                .HasForeignKey(u => u.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            // User - Department
            modelBuilder.Entity<User>()
                .HasOne(u => u.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(u => u.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);

            // ChatMember - Chat
            modelBuilder.Entity<ChatMember>()
                .HasOne(cm => cm.Chat)
                .WithMany(c => c.Members)
                .HasForeignKey(cm => cm.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            // ChatMember - User
            modelBuilder.Entity<ChatMember>()
                .HasOne(cm => cm.User)
                .WithMany(u => u.ChatMemberships)
                .HasForeignKey(cm => cm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // ChatDepartment - Chat
            modelBuilder.Entity<ChatDepartment>()
                .HasOne(cd => cd.Chat)
                .WithMany(c => c.ParticipatingDepartments)
                .HasForeignKey(cd => cd.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            // ChatDepartment - Department
            modelBuilder.Entity<ChatDepartment>()
                .HasOne(cd => cd.Department)
                .WithMany(d => d.ParticipatingChats)
                .HasForeignKey(cd => cd.DepartmentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Message - Chat
            modelBuilder.Entity<Message>()
                .HasOne(m => m.Chat)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);

            // Message - Sender
            modelBuilder.Entity<Message>()
                .HasOne(m => m.Sender)
                .WithMany(u => u.Messages)
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            // Message - ForwardedFrom
            modelBuilder.Entity<Message>()
                .HasOne(m => m.ForwardedFrom)
                .WithMany()
                .HasForeignKey(m => m.ForwardedFromMessageId)
                .OnDelete(DeleteBehavior.Restrict);

            // Notification - User
            modelBuilder.Entity<Notification>()
                .HasOne(n => n.User)
                .WithMany(u => u.Notifications)
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // AuditLog - User
            modelBuilder.Entity<AuditLog>()
                .HasOne(a => a.User)
                .WithMany(u => u.AuditLogs)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Department - Head (User)
            modelBuilder.Entity<Department>()
                .HasOne(d => d.Head)
                .WithMany(u => u.DepartmentsWhereHead)
                .HasForeignKey(d => d.HeadId)
                .OnDelete(DeleteBehavior.Restrict);

            // Department - Company
            modelBuilder.Entity<Department>()
                .HasOne(d => d.Company)
                .WithMany(c => c.Departments)
                .HasForeignKey(d => d.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            // Chat - Department
            modelBuilder.Entity<Chat>()
                .HasOne(c => c.Department)
                .WithMany(d => d.OwnedChats)
                .HasForeignKey(c => c.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Chat - CreatedBy (User)
            modelBuilder.Entity<Chat>()
                .HasOne(c => c.CreatedBy)
                .WithMany()
                .HasForeignKey(c => c.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // BannedWord - Company
            modelBuilder.Entity<BannedWord>()
                .HasOne(b => b.Company)
                .WithMany()
                .HasForeignKey(b => b.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            // ==================== JSONB ПОЛЯ ====================
            modelBuilder.Entity<AuditLog>()
                .Property(a => a.OldValue)
                .HasColumnType("jsonb");

            modelBuilder.Entity<AuditLog>()
                .Property(a => a.NewValue)
                .HasColumnType("jsonb");
        }
    }
}