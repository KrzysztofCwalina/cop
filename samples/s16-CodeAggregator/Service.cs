namespace SampleApp;

public class UserService
{
    public string GetUser(string id) => id;
    public void CreateUser(string name) { }
}

internal class UserRepository
{
    public void Save() { }
}
