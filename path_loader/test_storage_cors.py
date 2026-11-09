import unittest

from storage_cors import cors_rules


class CorsRulesTests(unittest.TestCase):
    def test_allows_signed_put_from_origins(self):
        rules = cors_rules(['https://example.com/', 'http://localhost:5000'])['CORSRules']
        self.assertEqual(rules, [{
            'AllowedOrigins': ['https://example.com', 'http://localhost:5000'],
            'AllowedMethods': ['PUT'],
            'AllowedHeaders': ['content-type'],
            'MaxAgeSeconds': 3600,
        }])

    def test_rejects_non_origins(self):
        for origin in ['example.com', 'ftp://example.com', 'https://example.com/gallery', 'https://example.com/?a=1']:
            with self.subTest(origin=origin), self.assertRaises(ValueError):
                cors_rules([origin])


if __name__ == '__main__':
    unittest.main()
