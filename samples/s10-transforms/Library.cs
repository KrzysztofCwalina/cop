namespace SampleApp;

public class UserRepository
{
    public void Create(string name) { }
    public void Update(int id, string name) { }
    public void Delete(int id) { }
    public string GetById(int id) => "";
    public string[] GetAll() => [];
}

public class Logger
{
    public void Info(string msg) { }
    public void Warn(string msg) { }
    public void Error(string msg) { }
    public void Debug(string msg) { }
}

public class Config
{
    public string Get(string key) => "";
    public void Set(string key, string value) { }
}
