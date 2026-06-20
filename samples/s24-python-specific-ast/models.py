from dataclasses import dataclass
from enum import Enum


# A data-transfer type done right — an immutable dataclass. Not flagged.
@dataclass
class CustomerDto:
    name: str
    email: str


# A data-transfer type that should be a @dataclass but isn't — flagged.
class OrderDto:
    def __init__(self, id):
        self.id = id


# An enum — recovered from the Python-specific AST (the common model marks it as a class).
class Status(Enum):
    ACTIVE = 1
    INACTIVE = 2


# A plain domain class — neither a DTO nor an enum.
class Inventory:
    pass
