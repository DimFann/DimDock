import io
import json
import os
import re
import tempfile
import warnings
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

from PIL import Image, UnidentifiedImageError

THUMB_DIR = Path("/img_db_thumbs")
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
                    thumbnail = source.convert("RGB")
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
        )

        THUMB_DIR.mkdir(parents=True, exist_ok=True)
        thumbnail_path = THUMB_DIR / f"{thumbnail_key}.png"
        temporary_path = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="wb",
                suffix=".png",
                prefix=".thumbnail-",
                dir=THUMB_DIR,
                delete=False,
            ) as temporary_file:
                temporary_path = Path(temporary_file.name)
                canvas.save(temporary_file, format="PNG", optimize=True)
                temporary_file.flush()
                os.fsync(temporary_file.fileno())
            os.replace(temporary_path, thumbnail_path)
        finally:
            if temporary_path is not None and temporary_path.exists():
                temporary_path.unlink()

        self.send_response(201)
        self.send_header("Content-Length", "0")
        self.end_headers()

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
        print(f"{self.address_string()} - {format_string % args}", flush=True)


def main():
    try:
        port = int(os.environ.get("THUMBNAIL_SERVICE_PORT", "8080"))
    except ValueError as error:
        raise ValueError("THUMBNAIL_SERVICE_PORT must be an integer between 1 and 65535.") from error
    if not 1 <= port <= 65535:
        raise ValueError("THUMBNAIL_SERVICE_PORT must be an integer between 1 and 65535.")

    THUMB_DIR.mkdir(parents=True, exist_ok=True)
    server = ThreadingHTTPServer(("0.0.0.0", port), ThumbnailHandler)
    print(f"Thumbnail service listening on port {port}.", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
