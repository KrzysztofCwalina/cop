class GoodClient:
    """A well-designed Python client."""

    def __init__(self, endpoint: str, credential, **kwargs):
        """Initialize the client."""
        self._endpoint = endpoint
        self._credential = credential

    @classmethod
    def from_connection_string(cls, conn_str: str, **kwargs) -> "GoodClient":
        """Create a client from a connection string."""
        return cls(conn_str, None, **kwargs)

    def get_item(self, item_id: str, **kwargs) -> dict:
        """Get an item by ID."""
        return {}

    def list_items(self, **kwargs) -> list:
        """List all items."""
        return []

    def create_item(self, item: dict, **kwargs) -> dict:
        """Create a new item."""
        return item

    def delete_item(self, item_id: str, **kwargs) -> None:
        """Delete an item."""
        pass

    def close(self, **kwargs) -> None:
        """Close the client."""
        pass
