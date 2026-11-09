import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

from deploy import native


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.settings = {
            "DATA_DIR": "/srv/dimdock-data", "DB_NAME": "gallery", "DB_USER": "dimdock",
            "DB_PASSWORD": "db-password", "ADMIN_USERNAME": "admin",
            "ADMIN_PASSWORD": "long-admin-password", "FILE_STORAGE_ROOT": "/data",
            "WSL_DATA_DIR": "/mnt/h/not-native",
        }

    def configure(self):
        with patch.object(native, "read_settings", return_value=self.settings), patch.dict(os.environ, {}, clear=True):
            return native.configuration()

    def test_native_paths_addresses_and_credentials(self):
        _, env = self.configure()
        self.assertEqual(env["FileStorage__RootPath"], "/srv/dimdock-data")
        self.assertEqual(env["DIMDOCK_DATA_ROOT"], "/srv/dimdock-data")
        self.assertEqual(env["Database__Host"], "127.0.0.1")
        self.assertEqual(env["DB_PASSWORD"], self.settings["DB_PASSWORD"])
        self.assertEqual(env["AdminAuth__Password"], self.settings["ADMIN_PASSWORD"])
        self.assertEqual(env["THUMBNAIL_SERVICE_HOST"], "127.0.0.1")
        self.assertEqual(env["ASPNETCORE_URLS"], "http://127.0.0.1:18080")
        self.assertEqual(env["DataProtection__KeysPath"], "/srv/dimdock-data/app_state/data-protection-keys")

    def test_environment_overrides_dotenv(self):
        with patch.object(native, "read_settings", return_value=self.settings), patch.dict(
            os.environ, {"DATA_DIR": "/other/data", "WEBSITE_PORT": "9000"}, clear=True
        ):
            _, env = native.configuration()
        self.assertEqual(env["FileStorage__RootPath"], "/other/data")
        self.assertEqual(env["ASPNETCORE_URLS"], "http://127.0.0.1:9000")

    def test_reject_invalid_settings(self):
        for key, value in (
            ("DATA_DIR", "C:/windows/data"), ("DATA_DIR", "/"), ("DATA_DIR", "relative"),
            ("DB_USER", "root"), ("DB_NAME", "db`; DROP DATABASE other"),
            ("DB_PASSWORD", ""), ("ADMIN_PASSWORD", "short"),
            ("WEBSITE_PORT", "80"), ("WEBSITE_PORT", "8080"), ("WEBSITE_PORT", "not-a-port"),
            ("FILE_STORAGE_PROVIDER", "unknown"), ("FILE_STORAGE_PROVIDER", "Linode"),
        ):
            with self.subTest(key=key, value=value), patch.dict(self.settings, {key: value}):
                with self.assertRaises(ValueError):
                    self.configure()

    def test_linode_mapping(self):
        self.settings["FILE_STORAGE_PROVIDER"] = "Linode"
        for key in ("ENDPOINT", "REGION", "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY"):
            self.settings[f"LINODE_STORAGE_{key}"] = key.lower()
        _, env = self.configure()
        self.assertEqual(env["LinodeStorage__AccessKey"], "access_key")
        self.assertEqual(env["LinodeStorage__SecretKey"], "secret_key")
        self.assertEqual(env["LINODE_STORAGE_PREFIX"], "prefix")

    def test_ports_are_compared_numerically(self):
        self.settings["WEBSITE_PORT"] = "08080"
        with self.assertRaisesRegex(ValueError, "must be different"):
            self.configure()

    def test_dotenv_is_data_not_shell(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_text("DB_PASSWORD='$(touch not-executed); $HOME ! &'\n", encoding="utf-8")
            self.assertEqual(native.read_settings(path)["DB_PASSWORD"], "$(touch not-executed); $HOME ! &")
            self.assertFalse((Path(directory) / "not-executed").exists())


class SchemaTests(unittest.TestCase):
    def connection(self, tables):
        connection = Mock()
        connection.cursor.return_value.fetchall.return_value = [(table,) for table in tables]
        return connection

    def test_existing_database_is_not_recreated(self):
        connection = self.connection(native.BASE_TABLES)
        native.initialize_schema(connection)
        connection.cursor.return_value.execute.assert_called_once_with("SHOW TABLES")
        connection.commit.assert_not_called()

    def test_incomplete_database_fails_without_ddl(self):
        connection = self.connection({"Items"})
        with self.assertRaisesRegex(ValueError, "No tables were reset"):
            native.initialize_schema(connection)
        connection.cursor.return_value.execute.assert_called_once_with("SHOW TABLES")

    def test_empty_database_initializes_schema(self):
        connection = self.connection(set())
        native.initialize_schema(connection)
        sql = " ".join(call.args[0] for call in connection.cursor.return_value.execute.call_args_list)
        for name in native.BASE_TABLES:
            self.assertIn(f"CREATE TABLE {name}", sql)
        connection.commit.assert_called_once()

    def test_stale_markers_fail_instead_of_skipping_content(self):
        connection = self.connection(set())
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / "app_state"
            state.mkdir()
            (state / ".blogs_initialized").touch()
            with self.assertRaisesRegex(ValueError, "old initialization markers"):
                native.initialize_schema(connection, directory)
        connection.cursor.return_value.execute.assert_called_once_with("SHOW TABLES")

    def test_provisioning_grants_tcp_loopback_and_socket_accounts(self):
        connector = Mock()
        connection = connector.connect.return_value
        settings = {"DB_NAME": "gallery", "DB_USER": "dimdock", "DB_PASSWORD": "safe'quote;value",
                    "DATA_DIR": "/srv/gallery"}
        with patch.dict(sys.modules, {"mysql": Mock(connector=connector), "mysql.connector": connector}), \
                patch.object(native, "configuration", return_value=(settings, {})), \
                patch.object(native, "initialize_schema") as schema:
            native.init_db()
        connector.connect.assert_called_once_with(user="root", unix_socket="/run/mysqld/mysqld.sock")
        statements = connection.cursor.return_value.execute.call_args_list
        for host in ("localhost", "127.0.0.1"):
            self.assertTrue(any(
                item.args == ("ALTER USER %s@%s IDENTIFIED BY %s",
                              ("dimdock", host, "safe'quote;value")) for item in statements
            ))
            self.assertTrue(any(
                item.args == ("GRANT ALL PRIVILEGES ON `gallery`.* TO %s@%s", ("dimdock", host))
                for item in statements
            ))
        schema.assert_called_once_with(connection, "/srv/gallery")
        connection.close.assert_called_once()


class SupervisorTests(unittest.TestCase):
    def test_completed_service_is_failure_even_if_zero_exit(self):
        supervisor = native.Supervisor(dict(os.environ))
        process = subprocess.Popen([sys.executable, "-c", "pass"])
        process.wait(timeout=10)
        supervisor.children.append(process)
        with self.assertRaisesRegex(RuntimeError, "unexpectedly"):
            supervisor.check()
        supervisor.close()

    def test_close_terminates_only_owned_children(self):
        supervisor = native.Supervisor(dict(os.environ))
        process = supervisor.start([sys.executable, "-c", "import time; time.sleep(120)"], native.ROOT)
        supervisor.close()
        self.assertIsNotNone(process.poll())

    def test_failed_index_prevents_follow_on_startup(self):
        supervisor = native.Supervisor(dict(os.environ))
        with patch.object(native, "PYTHON", Path(sys.executable)):
            with self.assertRaisesRegex(RuntimeError, "website not started"):
                supervisor.once("does-not-exist.py")
        self.assertEqual(supervisor.children, [])

    def test_signal_prevents_new_children(self):
        supervisor = native.Supervisor({})
        supervisor.stopping.set()
        with self.assertRaises(native.ShutdownRequested):
            supervisor.start([sys.executable, "-c", "pass"], native.ROOT)
        self.assertEqual(supervisor.children, [])

    def test_unit_is_non_root_and_keeps_secrets_out(self):
        unit = native.service_unit("gallery")
        self.assertIn("User=gallery", unit)
        self.assertIn("Requires=mariadb.service", unit)
        self.assertIn("KillMode=control-group", unit)
        self.assertIn("Restart=on-failure", unit)
        self.assertNotIn("PASSWORD", unit)

    def test_systemd_working_directory_is_not_shell_quoted(self):
        with patch.object(native, "ROOT", Path("/srv/gallery app")):
            unit = native.service_unit("gallery")
        self.assertIn("WorkingDirectory=/srv/gallery app\n", unit)


if __name__ == "__main__":
    unittest.main()
