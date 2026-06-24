class BlobClient:
    def download(self, path):
        print(f"downloading {path}")
        try:
            self._process(path)
        except Exception:
            pass

    def _process(self, path):
        x = 1
        y = 2
        print(x + y)
