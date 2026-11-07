```sh
docker exec -it console mariadb -h db -u myuser -p mypassword
docker compose build --no-cache path-loader
docker-compose down -v
docker-compose up --build -d
```


### Data directory

Copy `.env.example` to `.env` and set `DATA_DIR` to the absolute path of the
directory containing `app_state`, `img_src`, and `img_db_thumbs`. Docker Compose
uses this path for the bind mounts; `.env` is local and is not tracked.
Set `WEBSITE_PORT` to choose the host port for the website (the container
continues to listen on port 80).

Run `bash clean.sh` from any directory to clear `app_state`, `img_db_thumbs`,
and the Compose `db_data` directory. This permanently deletes their contents.
