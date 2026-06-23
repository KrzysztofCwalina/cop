namespace App;

public class Report
{
    public void Build()      // 7 statements -> flagged
    {
        var a = 1;
        var b = 2;
        var c = 3;
        var d = 4;
        var e = 5;
        var f = 6;
        var total = a + b + c + d + e + f;
    }

    public void Noop() { }   // 0 statements -> ok
}
