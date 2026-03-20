FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Устанавливаем curl для healthcheck
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем csproj и восстанавливаем зависимости
COPY ["CorpMsg.csproj", "./"]
RUN dotnet restore "CorpMsg.csproj"

# Копируем остальные файлы
COPY . .

# Собираем проект
RUN dotnet build "CorpMsg.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CorpMsg.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Создаем пользователя для запуска приложения (без прав root)
RUN groupadd -r corpmsg && useradd -r -g corpmsg corpmsg
RUN chown -R corpmsg:corpmsg /app
USER corpmsg

ENTRYPOINT ["dotnet", "CorpMsg.dll"]