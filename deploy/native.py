"""Native Ubuntu deployment; credentials are read as data, never sourced by a shell."""

import argparse
from contextlib import contextmanager
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from clean import read_settings

NATIVE = ROOT / ".native"
PYTHON = NATIVE / "venv" / "bin" / "python"
PUBLISH = NATIVE / "website"
BASE_TABLES = {
    "LegacyIdMap", "Items", "Shortcuts", "Paths", "Tags", "ItemTags", "PathTags",
    "PendingPathDeletions", "PendingItemDeletions", "AdminOnlyTagSettings", "Blogs", "About",
}


def configuration():
    settings = read_settings(ROOT / ".env")
    settings.update(os.environ)
    for key in ("DATA_DIR", "DB_NAME", "DB_USER", "DB_PASSWORD", "ADMIN_USERNAME", "ADMIN_PASSWORD"):
        if not settings.get(key):
            raise ValueError(f"Set {key} in the repository .env.")
    if len(settings["ADMIN_PASSWORD"]) < 12:
        raise ValueError("ADMIN_PASSWORD must contain at least 12 characters.")
    for key in ("DB_NAME", "DB_USER"):
        if not re.fullmatch(r"[A-Za-z0-9_]+", settings[key]) or len(settings[key]) > 32:
            raise ValueError(f"{key} must contain 1-32 ASCII letters, digits or underscores for native setup.")
    if settings["DB_USER"].lower() in ("root", "mysql", "mariadb"):
        raise ValueError("DB_USER must be a dedicated application account, not a database administrator.")
    data = Path(settings["DATA_DIR"])
    if not data.is_absolute() or data == Path(data.anchor) or re.match(r"^[A-Za-z]:", str(data)):
        raise ValueError("For native Linux, DATA_DIR must be an absolute Linux data-directory path.")
    provider = settings.get("FILE_STORAGE_PROVIDER", "Local")
    if provider.lower() not in ("local", "linode"):
        raise ValueError("FILE_STORAGE_PROVIDER must be Local or Linode.")
    if provider.lower() == "linode":
        for key in ("ENDPOINT", "REGION", "BUCKET", "ACCESS_KEY", "SECRET_KEY"):
            if not settings.get(f"LINODE_STORAGE_{key}"):
                raise ValueError(f"Set LINODE_STORAGE_{key} for Linode storage.")
    for key, default in (("WEBSITE_PORT", "18080"), ("THUMBNAIL_SERVICE_PORT", "8080")):
        value = settings.get(key, default)
        if not value.isascii() or not value.isdecimal() or not 1024 <= int(value) <= 65535:
            raise ValueError(f"{key} must be an unprivileged port between 1024 and 65535.")
        settings[key] = str(int(value))
    if settings["WEBSITE_PORT"] == settings["THUMBNAIL_SERVICE_PORT"]:
        raise ValueError("WEBSITE_PORT and THUMBNAIL_SERVICE_PORT must be different.")

    env = dict(os.environ)
    # Native paths/addresses deliberately replace container-only settings.
    env.update({
        "DB_HOST": "127.0.0.1", "DB_PORT": "3306",
        "DB_NAME": settings["DB_NAME"], "DB_USER": settings["DB_USER"],
        "DB_PASSWORD": settings["DB_PASSWORD"],
        "DIMDOCK_DATA_ROOT": str(data),
        "FILE_STORAGE_PROVIDER": provider,
        "ASPNETCORE_ENVIRONMENT": settings.get("ASPNETCORE_ENVIRONMENT", "Production"),
        "ASPNETCORE_URLS": f"http://127.0.0.1:{settings['WEBSITE_PORT']}",
        "Development__AllowAnyAdminPassword": settings.get("DEV_ALLOW_ANY_ADMIN_PASSWORD", "false"),
        "AdminAuth__Username": settings["ADMIN_USERNAME"],
        "AdminAuth__Password": settings["ADMIN_PASSWORD"],
        "DataProtection__KeysPath": str(data / "app_state" / "data-protection-keys"),
        "FileStorage__Provider": provider, "FileStorage__RootPath": str(data),
        "Database__Host": "127.0.0.1", "Database__Port": "3306",
        "Database__Name": settings["DB_NAME"], "Database__User": settings["DB_USER"],
        "Database__Password": settings["DB_PASSWORD"],
        "THUMBNAIL_SERVICE_HOST": "127.0.0.1",
        "THUMBNAIL_SERVICE_PORT": settings["THUMBNAIL_SERVICE_PORT"],
        "ThumbnailService__BaseAddress": f"http://127.0.0.1:{settings['THUMBNAIL_SERVICE_PORT']}/",
        "PYTHONUNBUFFERED": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    })
    for name in ("ENDPOINT", "REGION", "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY"):
        value = settings.get(f"LINODE_STORAGE_{name}", "")
        env[f"LINODE_STORAGE_{name}"] = value
        property_name = {"ACCESS_KEY": "AccessKey", "SECRET_KEY": "SecretKey"}.get(name, name.title())
        env[f"LinodeStorage__{property_name}"] = value
    return settings, env


def execute(command, **kwargs):
    subprocess.run([str(part) for part in command], check=True, **kwargs)


def build():
    if os.geteuid() == 0:
        raise ValueError("Build/run as the non-root deployment user; use sudo only for setup.")
    configuration()
    NATIVE.mkdir(exist_ok=True)
    execute(["python3", "-m", "venv", NATIVE / "venv"])
    execute([PYTHON, "-m", "pip", "install", "-r", ROOT / "path_loader" / "requirements.txt"])
    execute(["npm", "ci", "--ignore-scripts"], cwd=ROOT / "website")
    execute(["npm", "run", "build"], cwd=ROOT / "website")
    execute(["dotnet", "publish", ROOT / "website" / "website.csproj",
             "-c", "Release", "-o", PUBLISH, "--disable-build-servers", "-m:1"])
    print("Native build ready. Run bash run.sh (or start dimdock.service).", flush=True)


def initialize_schema(connection, data_root=None):
    cursor = connection.cursor()
    try:
        cursor.execute("SHOW TABLES")
        existing = {row[0] for row in cursor.fetchall()}
        if existing:
            missing = BASE_TABLES - existing
            if missing:
                raise ValueError("Existing database is incomplete; restore/migrate it before starting. "
                                 f"Missing tables: {', '.join(sorted(missing))}. No tables were reset.")
            print("Existing DimDock schema retained; no reset/import performed.", flush=True)
            return
        if data_root is not None:
            state = Path(data_root) / "app_state"
            if any((state / marker).exists() for marker in
                   (".initialized", ".blogs_initialized", ".shortcuts_initialized")):
                raise ValueError("Database is empty but old initialization markers exist in DATA_DIR/app_state. "
                                 "Restore the matching database, or remove only those three markers to rebuild "
                                 "from content. Data Protection keys must be preserved.")
        # This repository-owned schema has no semicolons inside SQL strings.
        for statement in (ROOT / "init" / "01-create-tables.sql").read_text(encoding="utf-8").split(";"):
            if statement.strip():
                cursor.execute(statement)
        connection.commit()
        print("Initialized empty database from init/01-create-tables.sql.", flush=True)
    finally:
        cursor.close()


def init_db():
    import mysql.connector

    settings, _ = configuration()
    connection = mysql.connector.connect(user="root", unix_socket="/run/mysqld/mysqld.sock")
    try:
        cursor = connection.cursor()
        try:
            name, user = settings["DB_NAME"], settings["DB_USER"]
            cursor.execute(f"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4")
            for host in ("localhost", "127.0.0.1"):
                cursor.execute("CREATE USER IF NOT EXISTS %s@%s IDENTIFIED BY %s",
                               (user, host, settings["DB_PASSWORD"]))
                cursor.execute("ALTER USER %s@%s IDENTIFIED BY %s", (user, host, settings["DB_PASSWORD"]))
                cursor.execute(f"GRANT ALL PRIVILEGES ON `{name}`.* TO %s@%s", (user, host))
            cursor.execute(f"USE `{name}`")
        finally:
            cursor.close()
        initialize_schema(connection, settings["DATA_DIR"])
    finally:
        connection.close()


def service_unit(user):
    if any(ord(char) < 32 for char in str(ROOT)):
        raise ValueError("The repository path cannot contain control characters.")

    def quote(path):
        return '"' + str(path).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%") + '"'

    return f"""[Unit]
Description=DimDock native gallery
Requires=mariadb.service
After=network-online.target mariadb.service
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=3

[Service]
Type=simple
User={user}
WorkingDirectory={str(ROOT).replace('%', '%%')}
ExecStart={quote(PYTHON).replace('$', '$$')} {quote(Path(__file__).resolve()).replace('$', '$$')} run
Restart=on-failure
RestartSec=10
TimeoutStopSec=45
KillMode=control-group
UMask=0077
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
"""


def setup(user):
    import pwd

    if os.geteuid() != 0:
        raise ValueError("Run setup with sudo: sudo bash run.sh setup --user YOUR_DEPLOY_USER")
    release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines() if "=" in line)
    if release.get("ID", "").strip('"') != "ubuntu" or release.get("VERSION_ID", "").strip('"') != "24.04":
        raise ValueError("Automatic setup supports Ubuntu 24.04 only.")
    account = pwd.getpwnam(user)
    if account.pw_uid == 0 or not re.fullmatch(r"[a-z_][a-z0-9_-]*", user):
        raise ValueError("Choose a non-root deployment user.")
    settings, _ = configuration()
    if subprocess.run(["systemctl", "is-active", "--quiet", "dimdock.service"]).returncode == 0:
        raise ValueError("Stop dimdock.service before setup/build to avoid changing a running deployment.")
    apt_env = dict(os.environ, DEBIAN_FRONTEND="noninteractive")
    execute(["apt-get", "update"], env=apt_env)
    execute(["apt-get", "install", "-y", "python3", "python3-venv", "mariadb-server",
             "dotnet-sdk-10.0", "nodejs", "npm", "ca-certificates"], env=apt_env)
    execute(["systemctl", "enable", "--now", "mariadb.service"])
    # Create data/build files as the app user, never recursively chown existing media.
    execute(["runuser", "-u", user, "--", "mkdir", "-p", settings["DATA_DIR"]])
    execute(["runuser", "-u", user, "--", "bash", ROOT / "run.sh", "build"])
    execute([PYTHON, Path(__file__).resolve(), "init-db"])
    Path("/etc/systemd/system/dimdock.service").write_text(service_unit(user), encoding="utf-8")
    execute(["systemctl", "daemon-reload"])
    execute(["systemctl", "enable", "dimdock.service"])
    print("Setup complete. Start with: sudo systemctl start dimdock\n"
          "Logs: sudo journalctl -u dimdock -f\n"
          "Both HTTP services bind to loopback. Configure an HTTPS reverse proxy.", flush=True)


class ShutdownRequested(Exception):
    pass


@contextmanager
def deployment_lock():
    import fcntl

    if os.geteuid() == 0:
        raise ValueError("Build/run as the non-root deployment user.")
    NATIVE.mkdir(exist_ok=True)
    with (NATIVE / "deployment.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("Another native build/run is active. Stop it before continuing.") from error
        yield


class Supervisor:
    def __init__(self, env):
        self.env = env
        self.children = []
        self.stopping = threading.Event()

    def start(self, command, cwd):
        self.check()
        process = subprocess.Popen([str(part) for part in command], cwd=cwd, env=self.env)
        self.children.append(process)
        return process

    def check(self):
        if self.stopping.is_set():
            raise ShutdownRequested()
        for process in self.children:
            if process.poll() is not None:
                raise RuntimeError(f"Child process {process.pid} exited unexpectedly (status {process.returncode}).")

    def once(self, script, *args):
        print(f"Running {script}...", flush=True)
        process = self.start([PYTHON, ROOT / "path_loader" / script, *args], ROOT / "path_loader")
        while process.poll() is None:
            # The indexing child is allowed to finish; long-lived services are not.
            if self.stopping.wait(0.2):
                raise ShutdownRequested()
            for other in self.children:
                if other is not process and other.poll() is not None:
                    raise RuntimeError(f"Service process {other.pid} exited during indexing.")
        self.children.remove(process)
        if process.returncode:
            raise RuntimeError(f"{script} failed (status {process.returncode}); website not started.")

    def ready(self, url):
        deadline = time.monotonic() + 60
        last_error = None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        while time.monotonic() < deadline:
            self.check()
            try:
                with opener.open(url, timeout=2) as response:
                    if response.status == 200:
                        self.check()
                        return
            except (OSError, urllib.error.URLError) as error:
                last_error = error
            self.stopping.wait(0.2)
        raise RuntimeError(f"Service did not become ready at {url}: {last_error}")

    def close(self):
        for process in reversed(self.children):
            if process.poll() is None:
                process.terminate()
        deadline = time.monotonic() + 30
        for process in self.children:
            try:
                process.wait(timeout=max(0.1, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def run():
    if os.geteuid() == 0:
        raise ValueError("Do not run the gallery as root.")
    settings, env = configuration()
    if not (PUBLISH / "website.dll").is_file() or not PYTHON.is_file():
        raise ValueError("Build first with bash run.sh build (or use sudo bash run.sh setup).")
    import mysql.connector
    from path_loader.file_store import LocalFileStore

    for port in (settings["WEBSITE_PORT"], settings["THUMBNAIL_SERVICE_PORT"]):
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                listener.bind(("127.0.0.1", int(port)))
            except OSError as error:
                raise RuntimeError(f"Cannot bind native service port {port}: {error}") from error
    connection = mysql.connector.connect(
        host=env["DB_HOST"], user=env["DB_USER"], password=env["DB_PASSWORD"],
        database=env["DB_NAME"], connection_timeout=10,
    )
    try:
        initialize_schema(connection, settings["DATA_DIR"])
    finally:
        connection.close()
    store = LocalFileStore(settings["DATA_DIR"])
    store.create_directory("state")
    supervisor = Supervisor(env)
    previous_handlers = {}
    for signum in (signal.SIGINT, signal.SIGTERM):
        previous_handlers[signum] = signal.signal(signum, lambda *_: supervisor.stopping.set())
    try:
        supervisor.start([PYTHON, ROOT / "thumbnail_service" / "server.py"], ROOT)
        supervisor.ready(env["ThumbnailService__BaseAddress"] + "health")
        supervisor.once("load_paths.py")
        store.write("state", ".blogs_initialized", b"")
        store.write("state", ".initialized", b"")
        supervisor.once("build_shortcuts.py", "--directories-only")
        if not store.exists("state", ".shortcuts_initialized"):
            supervisor.once("build_shortcuts.py")
            store.write("state", ".shortcuts_initialized", b"")
        supervisor.start(["dotnet", PUBLISH / "website.dll"], PUBLISH)
        # Static assets test Kestrel readiness without triggering a gallery DB query.
        supervisor.ready(f"http://127.0.0.1:{settings['WEBSITE_PORT']}/css/gallery.css")
        print(f"DimDock ready at http://127.0.0.1:{settings['WEBSITE_PORT']} (Ctrl+C to stop).", flush=True)
        while not supervisor.stopping.wait(0.5):
            supervisor.check()
    except ShutdownRequested:
        print("Stopping DimDock...", flush=True)
    finally:
        supervisor.close()
        for signum, handler in previous_handlers.items():
            signal.signal(signum, handler)


def main():
    parser = argparse.ArgumentParser(description="Docker-free Ubuntu 24.04 deployment.")
    parser.add_argument("command", nargs="?", choices=("setup", "build", "run", "init-db"), default="run")
    parser.add_argument("--user", default=os.environ.get("SUDO_USER"), help="Non-root service owner (setup only).")
    arguments = parser.parse_args()
    if sys.platform != "linux":
        parser.error("Native deployment requires Linux; use Compose for Windows development.")
    if arguments.command == "setup":
        if not arguments.user:
            parser.error("setup requires --user when not invoked by sudo from a non-root account.")
        setup(arguments.user)
    elif arguments.command == "init-db":
        if os.geteuid() != 0:
            parser.error("init-db is an internal setup step requiring root's MariaDB socket authentication.")
        init_db()
    elif arguments.command == "build":
        with deployment_lock():
            build()
    else:
        with deployment_lock():
            run()


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"Native deployment failed: {error}", file=sys.stderr)
        sys.exit(1)
