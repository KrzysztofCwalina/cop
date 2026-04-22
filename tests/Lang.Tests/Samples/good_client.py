class GoodClient:
    """A well-designed Python client."""

    def __init__(self, endpoint: str, credential, **kwargs):
        self._endpoint = endpoint
        self._credential = credential

    @classmethod
    def from_connection_string(cls, conn_str: str, **kwargs) -> "GoodClient":
        return cls(conn_str, None, **kwargs)

    def get_item(self, item_id: str, **kwargs) -> dict:
        return {}

    def list_items(self, **kwargs) -> list:
        return []

    def create_item(self, item: dict, **kwargs) -> dict:
        return item

    def delete_item(self, item_id: str, **kwargs) -> None:
        pass

    def close(self, **kwargs) -> None:
        pass
