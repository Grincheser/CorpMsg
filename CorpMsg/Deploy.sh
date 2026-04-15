echo "1. Остановка и удаление всех контейнеров..."

docker rm -f $(docker ps -aq) 2>/dev/null || echo "Нет контейнеров для удаления"

echo "2. Удаление всех образов..."

docker rmi -f $(docker images -q) 2>/dev/null || echo "Нет образов для удаления"

echo "3. Очистка Docker system..."

docker system prune -a -f --volumes

echo "4. Сборка и запуск контейнеров..."

docker-compose up -d --build

echo "5. Статус контейнеров:"

docker-compose ps

echo "6. Логи (последние 20 строк):"

docker-compose logs --tail=20