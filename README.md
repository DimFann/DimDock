docker exec -it console mariadb -h db -u myuser -p mypassword
docker compose build --no-cache path-loader


docker-compose down -v
docker-compose up --build -d
