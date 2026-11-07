```sh
docker exec -it console mariadb -h db -u myuser -p mypassword
docker compose build --no-cache path-loader
docker-compose down -v
docker-compose up --build -d
```


### Data directory

Copy `.env.example` to `.env` and set `DATA_DIR` to the absolute path of the
directory containing `app_state`, `img_src`, `img_db_thumbs`, and `db_data`.
Docker Compose uses this path for the bind mounts; `.env` is local and is not tracked.
Set `WEBSITE_PORT` to choose the host port for the website (the container
continues to listen on port 80).

Run `bash clean.sh` from any directory to clear `app_state`, `img_db_thumbs`,
and `db_data` under `DATA_DIR`. This permanently deletes their contents.

The database was previously stored in the project's `db_data` directory. If
you have an existing database there, stop the Compose services and copy its
contents to `DATA_DIR/db_data` before starting the updated configuration.
