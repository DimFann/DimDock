import io
import json
import logging
import os
import re
import signal
import threading
import warnings
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

from PIL import Image, UnidentifiedImageError

logging.basicConfig(level=logging.ERROR, format="%(levelname)s: %(message)s")
MAX_THUMBNAIL_SIZE = (256, 256)
MAX_UPLOAD_BYTES = 50 * 1024 * 1024
MAX_IMAGE_PIXELS = 100_000_000
THUMBNAIL_KEY_PATTERN = re.compile(r"^[0-9a-f]{64}$")
Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS


class ThumbnailHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/health":
            self.send_response(200)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        self.send_error(404)

    def do_POST(self):
        path = urlsplit(self.path).path
        prefix = "/thumbnail/"
        if not path.startswith(prefix):
            self._send_error(404, "Unknown endpoint.")
            return

        thumbnail_key = path[len(prefix):]
        if not THUMBNAIL_KEY_PATTERN.fullmatch(thumbnail_key):
            self._send_error(400, "Invalid thumbnail key.")
            return

        try:
            content_length = int(self.headers.get("Content-Length", ""))
        except ValueError:
            self._send_error(400, "A valid Content-Length header is required.")
            return
        if content_length <= 0:
            self._send_error(400, "The image body is empty.")
            return
        if content_length > MAX_UPLOAD_BYTES:
            self._send_error(413, "Images cannot exceed 50 MB.")
            return

        image_bytes = self.rfile.read(content_length)
        if len(image_bytes) != content_length:
            self._send_error(400, "The image upload was incomplete.")
            return

        try:
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(io.BytesIO(image_bytes)) as source:
                    source.load()
                    thumbnail = source.convert("RGBA")
            thumbnail.thumbnail(MAX_THUMBNAIL_SIZE)
        except (Image.DecompressionBombError, Image.DecompressionBombWarning):
            self._send_error(400, "Image dimensions exceed the 100-megapixel limit.")
            return
        except (UnidentifiedImageError, OSError, ValueError):
            self._send_error(400, "The file is not a valid supported image.")
            return

        canvas = Image.new("RGB", MAX_THUMBNAIL_SIZE, (255, 255, 255))
        canvas.paste(
            thumbnail,
            (
                (MAX_THUMBNAIL_SIZE[0] - thumbnail.width) // 2,
                (MAX_THUMBNAIL_SIZE[1] - thumbnail.height) // 2,
            ),
            thumbnail,
        )

        thumbnail_bytes = io.BytesIO()
        canvas.save(thumbnail_bytes, format="PNG", optimize=True)
        thumbnail_content = thumbnail_bytes.getvalue()
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(thumbnail_content)))
        self.end_headers()
        self.wfile.write(thumbnail_content)

    def _send_error(self, status, message):
        body = message.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format_string, *args):
        if self.path == "/health":
            return
        if len(args) > 1 and str(args[1]).isdigit() and int(args[1]) >= 400:
            logging.error("%s - %s", self.address_string(), format_string % args)


def main():
    try:
        port = int(os.environ.get("THUMBNAIL_SERVICE_PORT", "8080"))
    except ValueError as error:
        raise ValueError("THUMBNAIL_SERVICE_PORT must be an integer between 1 and 65535.") from error
    if not 1 <= port <= 65535:
        raise ValueError("THUMBNAIL_SERVICE_PORT must be an integer between 1 and 65535.")

    server = ThreadingHTTPServer(("0.0.0.0", port), ThumbnailHandler)
    server.daemon_threads = True
    shutdown_requested = threading.Event()

    def request_shutdown(_signum, _frame):
        shutdown_requested.set()

    signal.signal(signal.SIGINT, request_shutdown)
    signal.signal(signal.SIGTERM, request_shutdown)
    server_thread = threading.Thread(
        target=server.serve_forever,
        kwargs={"poll_interval": 0.1},
        name="thumbnail-http-server",
    )
    try:
        server_thread.start()
        shutdown_requested.wait()
    finally:
        server.shutdown()
        server.server_close()
        server_thread.join()


if __name__ == "__main__":
    main()
