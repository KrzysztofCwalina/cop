// FDG test violations
namespace SingleSegment
{
    // Naming: exception without suffix
    public class DatabaseError : Exception { }

    // Naming: attribute without suffix
    public class Mandatory : Attribute { }

    // Naming: EventArgs without suffix
    public class ClickInfo : EventArgs { }

    // Naming: Hungarian prefix
    public class CDocument { }

    // Naming: bool property without affirmative name
    public class Config
    {
        public bool Debug { get; set; }
    }

    // Type Design: enum with bad suffix
    public enum ColorEnum { Red, Green, Blue }

    // Type Design: Flags without plural name
    [Flags]
    public enum Permission { Read = 1, Write = 2, Execute = 4 }

    // Type Design: abstract with public ctor
    public abstract class BaseService
    {
        public BaseService() { }
    }

    // Type Design: marker interface
    public interface ISerializable { }

    // Member Design: too many params
    public class DataProcessor
    {
        public void Process(string a, string b, string c, string d, string e, string f) { }
    }

    // Member Design: write-only property
    public class Settings
    {
        public string ConnectionString { set { } }
    }

    // Exceptions: banned throw
    public class Worker
    {
        public void DoWork()
        {
            throw new Exception("bad");
        }
    }

    // Exceptions: Try method not returning bool
    public class Parser
    {
        public string TryParse(string input) => input;
    }
}
