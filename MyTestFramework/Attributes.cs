using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestFixtureAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class TestAttribute : Attribute
{
    public int Priority { get; set; } = 0;
    public bool Skip { get; set; } = false;
    public string? Reason { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class TestCaseAttribute : Attribute
{
    public object[] Arguments { get; }
    public int Priority { get; set; } = 0;
    public bool Skip { get; set; } = false;
    public string? Reason { get; set; }
    public TestCaseAttribute(params object[] args) => Arguments = args;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class TestTimeoutAttribute : Attribute
{
    public int Milliseconds { get; }

    public TestTimeoutAttribute(int milliseconds)
    {
        Milliseconds = milliseconds;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class OneTimeSetUpAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class OneTimeTearDownAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class SetUpAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class TearDownAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public class SharedContextAttribute : Attribute
{
    public string Name { get; }
    public SharedContextAttribute(string name) => Name = name;
}

