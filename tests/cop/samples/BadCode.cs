// Test fixture: code with known bad patterns.
// var declarations: 2 (x, y)
// Console calls: 1 (Console.WriteLine)
// Thread.Sleep calls: 1
// dynamic declarations: 1

public class BadPatterns
{
    public void DoStuff()
    {
        var x = 1;
        var y = "hello";
        Console.WriteLine("debug output");
        Thread.Sleep(100);
    }

    public void MoreBad()
    {
        dynamic d = new object();
    }
}
