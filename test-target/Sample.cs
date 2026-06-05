class Foo
{
    void Bar()
    {
        if (true) DoSomething();
        if (true) { DoSomething(); }
        while (true) Spin();
        foreach (var x in items) Process(x);
        for (int i = 0; i < 10; i++) Step();
    }
    void DoSomething() {}
    void Spin() {}
    void Process(object x) {}
    void Step() {}
}
