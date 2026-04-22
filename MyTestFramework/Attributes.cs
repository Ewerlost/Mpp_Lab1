using System;
using System.Collections.Generic;
using System.Linq;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestCaseSourceAttribute : Attribute
{
    public string MemberName { get; }
    public Type? SourceType { get; }

    public TestCaseSourceAttribute(string memberName)
    {
        MemberName = memberName;
    }

    public TestCaseSourceAttribute(Type sourceType, string memberName)
    {
        SourceType = sourceType;
        MemberName = memberName;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class CategoryAttribute : Attribute
{
    public string Name { get; }
    public CategoryAttribute(string name) => Name = name;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuthorAttribute : Attribute
{
    public string Name { get; }
    public AuthorAttribute(string name) => Name = name;
}

public sealed class TestCaseData
{
    public object[] Arguments { get; }
    public int Priority { get; set; }
    public bool Skip { get; set; }
    public string? Reason { get; set; }
    public string? Author { get; set; }
    public string? Name { get; set; }
    public List<string> Categories { get; } = new();

    public TestCaseData(params object[] args)
    {
        Arguments = args;
    }

    public TestCaseData Category(params string[] categories)
    {
        Categories.AddRange(categories.Where(c => !string.IsNullOrWhiteSpace(c)));
        return this;
    }

    public TestCaseData WithAuthor(string author)
    {
        Author = author;
        return this;
    }

    public TestCaseData Named(string name)
    {
        Name = name;
        return this;
    }
}

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

