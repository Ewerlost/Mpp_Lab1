using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace TestRunnerApp
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("Enter full path to the test assembly (.dll), e.g.:");
            Console.WriteLine(@"  ..\TargetProject.Tests\bin\Debug\net8.0\TargetProject.Tests.dll");
            Console.Write("> ");
            string? assemblyPath = Console.ReadLine()?.Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                Console.WriteLine("No path provided. Exiting.");
                return 1;
            }

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"ERROR: assembly not found: {assemblyPath}");
                return 2;
            }

            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: failed to load assembly: {ex.GetBaseException().Message}");
                return 3;
            }

            Console.WriteLine($"Loaded assembly: {assemblyPath}");

            var fixtureTypes = asm.GetTypes()
                .Where(t => t.GetCustomAttribute(typeof(TestFixtureAttribute)) != null)
                .ToArray();

            if (!fixtureTypes.Any())
            {
                Console.WriteLine("No test fixtures found.");
                return 0;
            }

            var allTestCases = new List<TestCaseInfo>();
            foreach (var type in fixtureTypes)
            {
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  .Where(m => m.GetCustomAttribute(typeof(TestAttribute)) != null
                                           || m.GetCustomAttributes(typeof(TestCaseAttribute), false).Any());

                foreach (var m in methods)
                {
                    var testAttr = m.GetCustomAttribute<TestAttribute>();
                    var testCases = m.GetCustomAttributes(typeof(TestCaseAttribute), false).Cast<TestCaseAttribute>().ToArray();

                    if (testCases.Length == 0)
                    {
                        var info = new TestCaseInfo
                        {
                            FixtureType = type,
                            Method = m,
                            Arguments = Array.Empty<object>(),
                            EffectivePriority = testAttr?.Priority ?? 0,
                            IsSkipped = testAttr?.Skip ?? false,
                            SkipReason = testAttr?.Reason
                        };
                        allTestCases.Add(info);
                    }
                    else
                    {
                        foreach (var tc in testCases)
                        {
                            var info = new TestCaseInfo
                            {
                                FixtureType = type,
                                Method = m,
                                Arguments = tc.Arguments ?? Array.Empty<object>(),
                                EffectivePriority = (tc.Priority != 0) ? tc.Priority : (testAttr?.Priority ?? 0),
                                IsSkipped = tc.Skip || (testAttr?.Skip ?? false),
                                SkipReason = tc.Reason ?? testAttr?.Reason
                            };
                            allTestCases.Add(info);
                        }
                    }
                }
            }

            var grouped = allTestCases.GroupBy(t => t.FixtureType);

            var summary = new List<TestResultRecord>();

            foreach (var group in grouped)
            {
                var fixtureType = group.Key;
                Console.WriteLine($"\n=== Fixture: {fixtureType.FullName} ===");

                var ordered = group.OrderBy(t => t.EffectivePriority)
                                   .ThenBy(t => t.Method.Name)
                                   .ThenBy(t => t.TestName)
                                   .ToList();

                object fixtureInstance;
                try
                {
                    fixtureInstance = Activator.CreateInstance(fixtureType) ?? throw new Exception("Activator returned null");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: cannot create fixture instance: {ex.Message}");
                    foreach (var tc in ordered)
                    {
                        summary.Add(new TestResultRecord(tc, TestStatus.Error, $"Cannot create fixture instance: {ex.Message}"));
                        Console.WriteLine($"[ERROR] {tc.TestName} - Cannot create fixture instance");
                    }
                    continue;
                }

                var oneTimeSet = FindSingleMethodWithAttribute<OneTimeSetUpAttribute>(fixtureType);
                var oneTimeTear = FindSingleMethodWithAttribute<OneTimeTearDownAttribute>(fixtureType);
                var setUp = FindSingleMethodWithAttribute<SetUpAttribute>(fixtureType);
                var tearDown = FindSingleMethodWithAttribute<TearDownAttribute>(fixtureType);

                bool oneTimeSetupSucceeded = true;
                if (oneTimeSet != null)
                {
                    try
                    {
                        await InvokeMaybeAsync(oneTimeSet, fixtureInstance);
                    }
                    catch (Exception ex)
                    {
                        oneTimeSetupSucceeded = false;
                        Console.WriteLine($"[ERROR] OneTimeSetUp failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");
                        foreach (var tc in ordered)
                        {
                            summary.Add(new TestResultRecord(tc, TestStatus.Error, $"OneTimeSetUp failed: {ex.GetBaseException().Message}"));
                            Console.WriteLine($"[ERROR] {tc.TestName} - OneTimeSetUp failed");
                        }
                    }
                }

                if (!oneTimeSetupSucceeded)
                {
                    if (oneTimeTear != null)
                    {
                        try
                        {
                            await InvokeMaybeAsync(oneTimeTear, fixtureInstance);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] OneTimeTearDown failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");
                        }
                    }
                    continue;
                }

                foreach (var tc in ordered)
                {
                    if (tc.IsSkipped)
                    {
                        summary.Add(new TestResultRecord(tc, TestStatus.Skipped, tc.SkipReason));
                        Console.WriteLine($"[SKIP] {tc.TestName} - {tc.SkipReason ?? "no reason"} (priority: {tc.EffectivePriority})");
                        continue;
                    }

                    var result = await RunTestCase(fixtureInstance, tc, setUp, tearDown);
                    summary.Add(result);
                }

                if (oneTimeTear != null)
                {
                    try
                    {
                        await InvokeMaybeAsync(oneTimeTear, fixtureInstance);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] OneTimeTearDown failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");
                    }
                }
            }

            Console.WriteLine("\n=== Summary ===");
            var passCount = summary.Count(s => s.Status == TestStatus.Passed);
            var failCount = summary.Count(s => s.Status == TestStatus.Failed);
            var skipCount = summary.Count(s => s.Status == TestStatus.Skipped);
            var errorCount = summary.Count(s => s.Status == TestStatus.Error);

            Console.Write("Total: ");
            Console.Write(summary.Count);

            Console.Write(", Passed: ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(passCount);
            Console.ResetColor();

            Console.Write(", Failed: ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(failCount);
            Console.ResetColor();

            Console.Write(", Skipped: ");
            Console.Write(skipCount);

            Console.Write(", Error: ");
            Console.WriteLine(errorCount);

            return 0;
        }

        private static MethodInfo? FindSingleMethodWithAttribute<TAttr>(Type fixtureType) where TAttr : Attribute
        {
            return fixtureType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .FirstOrDefault(m => m.GetCustomAttribute(typeof(TAttr)) != null);
        }

        private static async Task<TestResultRecord> RunTestCase(object fixtureInstance, TestCaseInfo tc, MethodInfo? setUp, MethodInfo? tearDown)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool setUpCalled = false;

            try
            {
                if (setUp != null)
                {
                    setUpCalled = true;
                    try
                    {
                        await InvokeMaybeAsync(setUp, fixtureInstance);
                    }
                    catch (Exception ex)
                    {
                        if (IsOrHasInner<TestSkippedException>(ex))
                        {
                            sw.Stop();
                            var reason = ex.GetBaseException().Message;
                            Console.WriteLine($"[SKIP] {tc.TestName} - {reason} (priority: {tc.EffectivePriority})");
                            if (tearDown != null)
                            {
                                try { await InvokeMaybeAsync(tearDown, fixtureInstance); }
                                catch (Exception tdEx)
                                {
                                    Console.WriteLine($"[ERROR] TearDown failed after SetUp-skip for {tc.TestName}: {tdEx.GetBaseException().Message}");
                                }
                            }
                            return new TestResultRecord(tc, TestStatus.Skipped, reason);
                        }
                        else
                        {
                            sw.Stop();
                            var msg = ex.GetBaseException().Message;
                            Console.WriteLine($"[ERROR] {tc.TestName} - SetUp failed: {msg} (priority: {tc.EffectivePriority})");
                            if (tearDown != null)
                            {
                                try { await InvokeMaybeAsync(tearDown, fixtureInstance); }
                                catch (Exception tdEx)
                                {
                                    Console.WriteLine($"[ERROR] TearDown failed after SetUp-error for {tc.TestName}: {tdEx.GetBaseException().Message}");
                                }
                            }
                            return new TestResultRecord(tc, TestStatus.Error, $"SetUp failed: {msg}");
                        }
                    }
                }

                try
                {
                    await InvokeMaybeAsync(tc.Method, fixtureInstance, PrepareArguments(tc.Method, tc.Arguments));
                    sw.Stop();
                    Console.WriteLine($"[PASS] {tc.TestName} (priority: {tc.EffectivePriority})");
                    return new TestResultRecord(tc, TestStatus.Passed, null, sw.Elapsed);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    return await HandleTestExceptionDuringRun(inner, tc, tearDown, fixtureInstance, setUpCalled, sw);
                }
                catch (Exception ex)
                {
                    return await HandleTestExceptionDuringRun(ex, tc, tearDown, fixtureInstance, setUpCalled, sw);
                }
            }
            finally
            {
                if (tearDown != null && setUpCalled)
                {
                    try
                    {
                        await InvokeMaybeAsync(tearDown, fixtureInstance);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] TearDown failed for {tc.TestName}: {ex.GetBaseException().Message}");
                    }
                }
            }
        }

        private static async Task<TestResultRecord> HandleTestExceptionDuringRun(Exception ex, TestCaseInfo tc, MethodInfo? tearDown, object fixtureInstance, bool setUpCalled, System.Diagnostics.Stopwatch sw)
        {
            sw.Stop();
            var baseEx = ex.GetBaseException();

            if (baseEx is AssertionFailedException)
            {
                Console.WriteLine($"[FAIL] {tc.TestName} - {baseEx.Message} (priority: {tc.EffectivePriority})");
                return new TestResultRecord(tc, TestStatus.Failed, baseEx.Message, sw.Elapsed);
            }
            else if (baseEx is TestSkippedException)
            {
                Console.WriteLine($"[SKIP] {tc.TestName} - {baseEx.Message} (priority: {tc.EffectivePriority})");
                return new TestResultRecord(tc, TestStatus.Skipped, baseEx.Message, sw.Elapsed);
            }
            else
            {
                Console.WriteLine($"[ERROR] {tc.TestName} - {baseEx.GetType().Name}: {baseEx.Message} (priority: {tc.EffectivePriority})");
                return new TestResultRecord(tc, TestStatus.Error, $"{baseEx.GetType().Name}: {baseEx.Message}", sw.Elapsed);
            }
        }

        private static object?[] PrepareArguments(MethodInfo method, object[] providedArgs)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0) return Array.Empty<object>();
            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var scAttr = param.GetCustomAttribute<SharedContextAttribute>();
                if (scAttr != null)
                {
                    if (SharedContextStore.TryGet(scAttr.Name, param.ParameterType, out var ctxObj))
                    {
                        args[i] = ctxObj;
                        continue;
                    }
                    else
                    {
                        args[i] = null;
                        continue;
                    }
                }
                if (i >= providedArgs.Length)
                {
                    args[i] = Type.Missing;
                    continue;
                }

                var targetType = param.ParameterType;
                var provided = providedArgs[i];
                if (provided == null) { args[i] = null; continue; }

                try
                {
                    if (targetType.IsInstanceOfType(provided)) { args[i] = provided; continue; }
                    args[i] = Convert.ChangeType(provided, targetType);
                }
                catch
                {
                    if (targetType.IsEnum && provided is string s)
                    {
                        try { args[i] = Enum.Parse(targetType, s); continue; } catch { }
                    }
                    args[i] = provided; 
                }
            }

            return args;
        }

        private static async Task InvokeMaybeAsync(MethodInfo method, object instance, object[]? args = null)
        {
            args ??= Array.Empty<object>();
            var ret = method.Invoke(instance, args);

            if (ret == null) return;

            if (ret is Task task)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var retType = ret.GetType();
            if (retType.IsGenericType && retType.GetGenericTypeDefinition() == typeof(Task<>))
            {

                await ((dynamic)ret).ConfigureAwait(false);
                return;
            }

            if (ret is ValueTask vt)
            {
                await vt.ConfigureAwait(false);
                return;
            }

            if (retType.IsGenericType && retType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                await ((dynamic)ret).ConfigureAwait(false);
                return;
            }

        }

        private static bool IsOrHasInner<TException>(Exception ex) where TException : Exception
        {
            var be = ex.GetBaseException();
            return be is TException;
        }

        private class TestCaseInfo
        {
            public Type FixtureType { get; init; } = null!;
            public MethodInfo Method { get; init; } = null!;
            public object[] Arguments { get; init; } = Array.Empty<object>();
            public int EffectivePriority { get; init; } = 0;
            public bool IsSkipped { get; init; } = false;
            public string? SkipReason { get; init; }
            public string TestName => $"{FixtureType.Name}.{Method.Name}({string.Join(", ", Arguments.Select(a => a?.ToString() ?? "null"))})";
        }

        private enum TestStatus { Passed, Failed, Skipped, Error }

        private class TestResultRecord
        {
            public TestCaseInfo TestCase { get; }
            public TestStatus Status { get; }
            public string? Message { get; }
            public TimeSpan Duration { get; }
            public TestResultRecord(TestCaseInfo tc, TestStatus status, string? message = null, TimeSpan? duration = null)
            {
                TestCase = tc;
                Status = status;
                Message = message;
                Duration = duration ?? TimeSpan.Zero;
            }
        }
    }
}
