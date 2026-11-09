"""Allow browsers on the website's origin(s) to upload gallery files straight to the bucket.

Replaces the bucket's CORS configuration. Usage: storage_cors.py https://example.com [https://other.origin ...]
"""
import sys
from urllib.parse import urlsplit

from linode_file_store import LinodeFileStore


def cors_rules(origins):
    for origin in origins:
        parts = urlsplit(origin)
        if parts.scheme not in ('http', 'https') or not parts.netloc or parts.path not in ('', '/') or parts.query:
            raise ValueError(f'Not a website origin (scheme://host[:port]): {origin}')
    return {'CORSRules': [{
        'AllowedOrigins': [origin.rstrip('/') for origin in origins],
        'AllowedMethods': ['PUT'],
        'AllowedHeaders': ['content-type'],
        'MaxAgeSeconds': 3600,
    }]}


def main(argv=None):
    origins = sys.argv[1:] if argv is None else argv
    if not origins:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    store = LinodeFileStore.from_environment(None)
    rules = cors_rules(origins)
    store.client.put_bucket_cors(Bucket=store.bucket, CORSConfiguration=rules)
    print(f'Bucket {store.bucket} now accepts direct uploads from: {", ".join(rules["CORSRules"][0]["AllowedOrigins"])}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
