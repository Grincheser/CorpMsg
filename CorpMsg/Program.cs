using CorpMsg.AppData;
using CorpMsg.Hubs;
using CorpMsg.Service;
using CorpMsg.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Добавление сервисов в контейнер
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "CorpMsg API",
        Version = "v1",
        Description = "Корпоративный мессенджер"
    });

    // Добавление JWT авторизации в Swagger
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Введите JWT токен"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// Настройка SignalR
builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 102400;
    options.EnableDetailedErrors = true;
});

// Настройка CORS 
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
                   .WithOrigins(
                  "http://localhost:3000",
                  "http://127.0.0.1:5500",
                  "https://ravenapp.ru",
                  "http://ravenapp.ru"
              )
              .AllowCredentials();
    });
});

// Настройка PostgreSQL
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Настройка JWT аутентификации
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured in appsettings.json");

if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters long");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"]
                ?? throw new InvalidOperationException("Jwt:Issuer is not configured"),
            ValidAudience = builder.Configuration["Jwt:Audience"]
                ?? throw new InvalidOperationException("Jwt:Audience is not configured"),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        // Настройка для SignalR
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

// Настройка MinIO клиента
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["Minio:Endpoint"] ?? "localhost:9000";
    var accessKey = configuration["Minio:AccessKey"] ?? "minioadmin";
    var secretKey = configuration["Minio:SecretKey"] ?? "minioadmin";
    var useSsl = bool.Parse(configuration["Minio:UseSsl"] ?? "false");
    var region = configuration["Minio:Region"] ?? "us-east-1";

    return new MinioClient()
        .WithEndpoint(endpoint)
        .WithCredentials(accessKey, secretKey)
        .WithSSL(useSsl)
        .WithRegion(region)
        .Build();
});

// Регистрация сервисов
builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddScoped<IBannedWordsService, BannedWordsService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();



var app = builder.Build();

// ==========================================
// ПРАВИЛЬНЫЙ ПОРЯДОК MIDDLEWARE
// ==========================================

// 1. СНАЧАЛА: Обработка OPTIONS запросов (preflight) ДО CORS


// 2. Security Headers (кроме CSP для SignalR)


// 3. Swagger (может быть до или после, не важно)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CorpMsg API V1");
        c.RoutePrefix = "swagger";
    });
}
else
{
    // Опционально: Swagger в продакшене (лучше отключить или защитить)
    // app.UseSwagger();
    // app.UseSwaggerUI(c =>
    // {
    //     c.SwaggerEndpoint("/swagger/v1/swagger.json", "CorpMsg API V1");
    //     c.RoutePrefix = "swagger";
    // });
}

// 4. HTTPS редирект


// 5. CORS - ТЕПЕРЬ ПОСЛЕ ОБРАБОТКИOPTIONS
app.UseCors("AllowAll");

// 6. Аутентификация и авторизация
app.UseAuthentication();
app.UseAuthorization();

// 7. Rate limiting

// 8. Маршрутизация
app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

// 9. Автоматическая миграция при запуске
using (var scope = app.Services.CreateScope())
{
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Database.Migrate();
        Console.WriteLine("Database migrations completed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed: {ex.Message}");
        throw;
    }
}

app.Run();