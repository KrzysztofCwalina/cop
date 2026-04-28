# Test fixture: Python types and statements.
# Types: Calculator, AdvancedCalculator (2)
# print calls: 1

class Calculator:
    def add(self, a, b):
        return a + b

    def subtract(self, a, b):
        return a - b

class AdvancedCalculator(Calculator):
    def multiply(self, a, b):
        return a * b

def helper():
    print("debug output")
