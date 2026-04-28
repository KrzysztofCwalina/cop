// Test fixture: known types with predictable properties.
// Types: Animal, Dog, Cat, FishClient, HiddenHelper, AbstractService (6 total)
//   Public: Animal, Dog, Cat, FishClient, AbstractService (5)
//   Non-public: HiddenHelper (1)
//   Sealed: FishClient (1)
//   Abstract: AbstractService (1)

public class Animal
{
    public string Name { get; set; }
    public int Age { get; set; }
}

public class Dog : Animal
{
    public void Bark() { }
}

public class Cat : Animal
{
    public void Meow() { }
}

public sealed class FishClient
{
    public void Swim() { }
}

internal class HiddenHelper
{
    private void DoWork() { }
}

public abstract class AbstractService
{
    public abstract void Process();
    public virtual void Initialize() { }
}
