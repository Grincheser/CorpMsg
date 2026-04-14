using CorpMsg.AppData;
using CorpMsg.Controllers;
using CorpMsg.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CorpMsg.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private static readonly Dictionary<string, UserConnection> _connections = new();

        public ChatHub(ApplicationDbContext context)
        {
            _context = context;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            var connectionId = Context.ConnectionId;

            if (userId != null)
            {
                // Обновляем статус пользователя
                var userStatus = await _context.UserStatuses
                    .FirstOrDefaultAsync(s => s.UserId.ToString() == userId);

                if (userStatus == null)
                {
                    userStatus = new UserStatus
                    {
                        UserId = Guid.Parse(userId),
                        IsOnline = true,
                        LastSeenAt = DateTime.UtcNow,
                        ConnectionId = connectionId
                    };
                    _context.UserStatuses.Add(userStatus);
                }
                else
                {
                    userStatus.IsOnline = true;
                    userStatus.LastSeenAt = DateTime.UtcNow;
                    userStatus.ConnectionId = connectionId;
                }

                await _context.SaveChangesAsync();

                // Сохраняем соединение
                _connections[connectionId] = new UserConnection
                {
                    UserId = Guid.Parse(userId),
                    ConnectionId = connectionId
                };

                // Добавляем пользователя в группы его чатов
                var userChats = await _context.ChatMembers
                    .Where(cm => cm.UserId.ToString() == userId)
                    .Select(cm => cm.ChatId.ToString())
                    .ToListAsync();

                foreach (var chatId in userChats)
                {
                    await Groups.AddToGroupAsync(connectionId, chatId);
                }

                // Оповещаем всех о смене статуса
                await Clients.All.SendAsync("UserStatusChanged", new
                {
                    UserId = userId,
                    IsOnline = true,
                    LastSeenAt = DateTime.UtcNow
                });
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connectionId = Context.ConnectionId;

            if (_connections.TryGetValue(connectionId, out var connection))
            {
                // Обновляем статус
                var userStatus = await _context.UserStatuses
                    .FirstOrDefaultAsync(s => s.UserId == connection.UserId);

                if (userStatus != null)
                {
                    userStatus.IsOnline = false;
                    userStatus.LastSeenAt = DateTime.UtcNow;
                    userStatus.ConnectionId = null;
                    await _context.SaveChangesAsync();
                }

                _connections.Remove(connectionId);

                // Оповещаем всех о смене статуса
                await Clients.All.SendAsync("UserStatusChanged", new
                {
                    UserId = connection.UserId.ToString(),
                    IsOnline = false,
                    LastSeenAt = DateTime.UtcNow
                });
            }

            await base.OnDisconnectedAsync(exception);
        }


        /// <summary>
        /// Редактирование сообщения
        /// </summary>
        public async Task EditMessageInChat(Guid chatId, MessageResponse message)
        {
            await Clients.Group(chatId.ToString()).SendAsync("MessageEdited", message);
        }

        /// <summary>
        /// Удаление сообщения
        /// </summary>
        public async Task DeleteMessageFromChat(Guid chatId, Guid messageId)
        {
            await Clients.Group(chatId.ToString()).SendAsync("MessageDeleted", new
            {
                ChatId = chatId,
                MessageId = messageId
            });
        }

        /// <summary>
        /// Пользователь печатает
        /// </summary>
        public async Task UserTyping(Guid chatId, bool isTyping)
        {
            var userId = Context.UserIdentifier;
            await Clients.OthersInGroup(chatId.ToString()).SendAsync("UserTyping", new
            {
                UserId = userId,
                ChatId = chatId,
                IsTyping = isTyping
            });
        }

        /// <summary>
        /// Отметка о прочтении сообщений
        /// </summary>
        public async Task MarkMessagesAsRead(Guid chatId, List<Guid> messageIds)
        {
            var userId = Context.UserIdentifier;
            await Clients.Group(chatId.ToString()).SendAsync("MessagesRead", new
            {
                UserId = userId,
                ChatId = chatId,
                MessageIds = messageIds,
                ReadAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Добавление пользователя в чат
        /// </summary>
        public async Task UserAddedToChat(Guid userId, Guid chatId)
        {
            var connection = _connections.Values.FirstOrDefault(c => c.UserId == userId);
            if (connection != null)
            {
                await Groups.AddToGroupAsync(connection.ConnectionId, chatId.ToString());
            }
        }

        /// <summary>
        /// Удаление пользователя из чата
        /// </summary>
        public async Task UserRemovedFromChat(Guid userId, Guid chatId)
        {
            var connection = _connections.Values.FirstOrDefault(c => c.UserId == userId);
            if (connection != null)
            {
                await Groups.RemoveFromGroupAsync(connection.ConnectionId, chatId.ToString());
            }
        }

        /// <summary>
        /// Отправка уведомления пользователю
        /// </summary>
        public async Task SendNotificationToUser(Guid userId, Notification notification)
        {
            var connection = _connections.Values.FirstOrDefault(c => c.UserId == userId);
            if (connection != null)
            {
                await Clients.Client(connection.ConnectionId).SendAsync("ReceiveNotification", notification);
            }
        }
        /// <summary>
        /// Отправка сообщения в чат
        /// </summary>
        private static readonly Dictionary<string, DateTime> _lastMessageTime = new();
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public async Task SendMessageToChat(Guid chatId, MessageResponse message)
        {
            var userId = Context.UserIdentifier;

            // Rate limiting для сообщений
            var now = DateTime.UtcNow;
            await _semaphore.WaitAsync();
            try
            {
                if (_lastMessageTime.TryGetValue(userId, out var lastTime) &&
                    (now - lastTime).TotalSeconds < 1) // 1 сообщение в секунду
                {
                    throw new HubException("Слишком много сообщений. Подождите немного.");
                }
                _lastMessageTime[userId] = now;
            }
            finally
            {
                _semaphore.Release();
            }

            // Оригинальный код
            await Clients.Group(chatId.ToString()).SendAsync("ReceiveMessage", message);
        }
    }

    public class UserConnection
    {
        public Guid UserId { get; set; }
        public string ConnectionId { get; set; } = string.Empty;
    }
}