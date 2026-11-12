import http.client
import io
import threading
import unittest
from http.server import ThreadingHTTPServer

from PIL import Image

from server import ThumbnailHandler


class ThumbnailTransparencyTests(unittest.TestCase):
    def test_transparent_and_opaque_images(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), ThumbnailHandler)
        thread = threading.Thread(target=server.serve_forever)
        thread.start()
        try:
            for mode, color, expected in (
                ('RGBA', (0, 0, 0, 0), (255, 255, 255)),
                ('RGBA', (0, 0, 0, 128), (127, 127, 127)),
                ('RGB', (0, 0, 0), (0, 0, 0)),
                ('P', 0, (255, 255, 255)),
            ):
                with self.subTest(mode=mode, color=color):
                    source = io.BytesIO()
                    image = Image.new(mode, (16, 16), color)
                    if mode == 'P':
                        image.info['transparency'] = 0
                    image.save(source, format='PNG')
                    connection = http.client.HTTPConnection(*server.server_address)
                    try:
                        connection.request('POST', '/thumbnail/' + 'a' * 64, source.getvalue())
                        response = connection.getresponse()
                        self.assertEqual(response.status, 200)
                        with Image.open(io.BytesIO(response.read())) as thumbnail:
                            self.assertEqual(thumbnail.size, (256, 256))
                            self.assertEqual(thumbnail.getpixel((128, 128)), expected)
                            self.assertEqual(thumbnail.getpixel((0, 0)), (255, 255, 255))
                    finally:
                        connection.close()
        finally:
            server.shutdown()
            thread.join()
            server.server_close()


if __name__ == '__main__':
    unittest.main()
