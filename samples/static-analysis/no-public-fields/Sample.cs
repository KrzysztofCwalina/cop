namespace App;

public class Account
{
    public decimal Balance;            // public field -> flagged
    public const int MaxItems = 100;   // const -> ok
    private int _id;                   // private -> ok
}

public class Money
{
    public decimal Amount { get; set; }  // property -> ok
}
