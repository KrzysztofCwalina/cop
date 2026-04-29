import logging

class BlobClient:
    """A client for blob storage."""

    def __init__(self, endpoint, credential, **kwargs):
        self._endpoint = endpoint
        self._credential = credential

    def get_blob(self, name):
        try:
            return self._do_request(name)
        except Exception:
            pass  # swallows exception

    def delete_blob(self, name):
        try:
            return self._do_request(name)
        except ValueError:
            raise

    def _do_request(self, path):
        print(f"requesting {path}")
        return None


class QueueClient:
    def send_message(self, message):
        logging.info(f"sending {message}")
