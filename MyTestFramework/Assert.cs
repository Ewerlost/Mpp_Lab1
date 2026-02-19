using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public static class Assert
{
    public static void AreEqual<T>(T expected, T actual, string? msg = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailedException(msg ?? $"AreEqual failed. Expected={expected}, Actual={actual}");
    }
    public static void AreNotEqual<T>(T notExpected, T actual, string? msg = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertionFailedException(msg ?? $"AreNotEqual failed. NotExpected={notExpected}, Actual={actual}");
    }
    public static void IsTrue(bool cond, string? msg = null)
    {
        if (!cond) throw new AssertionFailedException(msg ?? "IsTrue failed.");
    }
    public static void IsFalse(bool cond, string? msg = null)
    {
        if (cond) throw new AssertionFailedException(msg ?? "IsFalse failed.");
    }
    public static void IsNull(object? obj, string? msg = null)
    {
        if (obj != null) throw new AssertionFailedException(msg ?? "IsNull failed.");
    }
    public static void IsNotNull(object? obj, string? msg = null)
    {
        if (obj == null) throw new AssertionFailedException(msg ?? "IsNotNull failed.");
    }
    public static void Contains<T>(IEnumerable<T> collection, T item, string? msg = null)
    {
        if (!collection.Contains(item)) throw new AssertionFailedException(msg ?? "Contains failed.");
    }
    public static void DoesNotContain<T>(IEnumerable<T> collection, T item, string? msg = null)
    {
        if (collection.Contains(item)) throw new AssertionFailedException(msg ?? "DoesNotContain failed.");
    }
    public static void GreaterThan<T>(T a, T b, string? msg = null) where T : IComparable<T>
    {
        if (a.CompareTo(b) <= 0) throw new AssertionFailedException(msg ?? $"GreaterThan failed: {a} <= {b}");
    }
    public static void LessThan<T>(T a, T b, string? msg = null) where T : IComparable<T>
    {
        if (a.CompareTo(b) >= 0) throw new AssertionFailedException(msg ?? $"LessThan failed: {a} >= {b}");
    }
    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? msg = null)
    {
        if (!expected.SequenceEqual(actual))
            throw new AssertionFailedException(msg ?? "SequenceEqual failed.");
    }
    public static void Throws<TException>(Action action, string? msg = null) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new AssertionFailedException(msg ?? $"Throws failed: expected {typeof(TException)}, got {ex.GetType()}"); }
        throw new AssertionFailedException(msg ?? $"Throws failed: expected {typeof(TException)} was not thrown.");
    }
    public static async Task ThrowsAsync<TException>(Func<Task> action, string? msg = null) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new AssertionFailedException(msg ?? $"ThrowsAsync failed: expected {typeof(TException)}, got {ex.GetType()}"); }
        throw new AssertionFailedException(msg ?? $"ThrowsAsync failed: expected {typeof(TException)} was not thrown.");
    }
}
