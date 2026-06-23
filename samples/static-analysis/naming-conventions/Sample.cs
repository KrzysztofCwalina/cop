using System;
using System.Threading.Tasks;

namespace App;

public interface Repository      // should be IRepository
{
    void Save();
}

public interface ICache          // ok
{
    void Clear();
}

public class TimeoutError : Exception { }    // should be *Exception

public class RetryException : Exception { }  // ok

public class Worker
{
    public async Task Process() { }          // should be ProcessAsync
    public async Task FetchAsync() { }       // ok
}
