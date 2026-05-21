class BadClient:
    """A poorly-designed Python client."""

    def __init__(self, endpoint):
        self._endpoint = endpoint

    def fetch_data(self, query):
        print("fetching...")
        return {}

    def process(self, data):
        try:
            result = data
        except:
            pass

        try:
            result = data
        except Exception as e:
            pass

        return data

    def _internal(self):
        pass
