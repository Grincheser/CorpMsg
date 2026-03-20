.PHONY: help up down build logs clean migrate

help:
	@echo "Доступные команды:"
	@echo "  make up         - Запустить все сервисы"
	@echo "  make down       - Остановить все сервисы"
	@echo "  make build      - Собрать Docker образы"
	@echo "  make logs       - Показать логи"
	@echo "  make clean      - Очистить volumes и остановить сервисы"
	@echo "  make migrate    - Выполнить миграции базы данных"
	@echo "  make dev        - Запустить в режиме разработки (с pgAdmin)"

up:
	docker-compose up -d

down:
	docker-compose down

build:
	docker-compose build --no-cache

logs:
	docker-compose logs -f

clean:
	docker-compose down -v
	rm -rf ./postgres-data
	rm -rf ./minio-data

migrate:
	docker-compose exec api dotnet ef database update

dev:
	docker-compose --profile dev up -d

restart: down up

status:
	docker-compose ps