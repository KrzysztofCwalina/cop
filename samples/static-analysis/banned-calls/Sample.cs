using System;
using System.Threading;

namespace App;

public class Service
{
    public void Run()
    {
        Console.WriteLine("debug");   // banned in libraries -> flagged
        Thread.Sleep(500);            // blocking wait -> flagged
    }
}
