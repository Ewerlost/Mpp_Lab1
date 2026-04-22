#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DynamicThreadPoolModule;

namespace TestRunnerApp
{
    internal class Program
    {
        private static readonly object _consoleLock = new();
        private static readonly Stopwatch _globalClock = Stopwatch.StartNew();

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("Введите полный путь до тестируемой сборки:");
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

            Console.WriteLine("Введите MaxDegreeOfParallelism (Enter = число потоков):");
            Console.Write("> ");
            string? parallelInput = Console.ReadLine();
            int maxParallel = int.TryParse(parallelInput, out var parsed) && parsed > 0
                ? parsed
                : Environment.ProcessorCount;

            Console.WriteLine("Введите количество повторов всей выборки тестов");
            Console.Write("> ");
            string? repeatInput = Console.ReadLine();
            int repetitions = int.TryParse(repeatInput, out var repeatsParsed) && repeatsParsed > 0 ? repeatsParsed : 1;

            Console.WriteLine("Введите категорию для фильтрации (Enter = без фильтра):");
            Console.Write("> ");
            string? categoryFilter = Console.ReadLine()?.Trim();

            Console.WriteLine("Введите автора для фильтрации (Enter = без фильтра):");
            Console.Write("> ");
            string? authorFilter = Console.ReadLine()?.Trim();

            Console.WriteLine("Введите минимальный приоритет (Enter = без фильтра):");
            Console.Write("> ");
            string? minPriorityInput = Console.ReadLine();
            int? minPriorityFilter = int.TryParse(minPriorityInput, out var mp) ? mp : null;

            Func<TestCaseInfo, bool> filter = BuildFilter(categoryFilter, authorFilter, minPriorityFilter);

            WriteInfoLine($"MaxDegreeOfParallelism = {maxParallel}");
            WriteInfoLine($"Repetitions = {repetitions}");

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

            WriteInfoLine($"Loaded assembly: {assemblyPath}");

            var fixtureTypes = GetTypesSafe(asm)
                .Where(t => t.GetCustomAttribute(typeof(TestFixtureAttribute)) != null)
                .ToArray();

            if (!fixtureTypes.Any())
            {
                Console.WriteLine("No test fixtures found.");
                return 0;
            }

            var allTestCases = BuildTestCases(fixtureTypes).Where(filter).ToList();

            if (!allTestCases.Any())
            {
                Console.WriteLine("No test cases found.");
                return 0;
            }

            var grouped = allTestCases
                .GroupBy(t => t.FixtureType)
                .ToList();

            var summary = new ConcurrentBag<TestResultRecord>();

            int minWorkers = Math.Max(1, Math.Min(2, maxParallel));

            using var pool = new DynamicThreadPool<TestResultRecord>(
                minWorkers: minWorkers,
                maxWorkers: maxParallel,
                idleTimeout: TimeSpan.FromSeconds(3),
                queuePressureTimeout: TimeSpan.FromMilliseconds(300),
                hungWorkerTimeout: TimeSpan.FromSeconds(5),
                supervisorInterval: TimeSpan.FromMilliseconds(200));

            pool.StateChanged += snapshot =>
            {
                lock (_consoleLock)
                {
                    Console.WriteLine(
                        $"[POOL] workers={snapshot.TotalWorkers}, busy={snapshot.BusyWorkers}, idle={snapshot.IdleWorkers}, queue={snapshot.QueuedItems}");
                }
            };

            pool.Log += msg =>
            {
                lock (_consoleLock)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[POOL] {msg}");
                    Console.ResetColor();
                }
            };

            var fixtureTasks = grouped
                .Select(group =>
                    RunFixtureAsync(
                        group.Key,
                        ExpandCases(group.ToList(), repetitions),
                        pool,
                        summary))
                .ToList();

            await Task.WhenAll(fixtureTasks);

            PrintSummary(summary);

            return 0;
        }

        private static List<TestCaseInfo> BuildTestCases(Type[] fixtureTypes)
        {
            var allTestCases = new List<TestCaseInfo>();

            foreach (var type in fixtureTypes)
            {
                var classCategories = type
                    .GetCustomAttributes(true)
                    .OfType<CategoryAttribute>()
                    .Select(a => a.Name)
                    .ToArray();

                var classAuthor = type.GetCustomAttribute<AuthorAttribute>()?.Name;

                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m =>
                        m.GetCustomAttribute<TestAttribute>() != null ||
                        m.GetCustomAttributes(typeof(TestCaseAttribute), false).Any() ||
                        m.GetCustomAttribute<TestCaseSourceAttribute>() != null);

                foreach (var m in methods)
                {
                    var testAttr = m.GetCustomAttribute<TestAttribute>();
                    var timeoutAttr = m.GetCustomAttribute<TestTimeoutAttribute>();

                    var methodCategories = m
                        .GetCustomAttributes(true)
                        .OfType<CategoryAttribute>()
                        .Select(a => a.Name)
                        .ToArray();

                    var categories = classCategories
                        .Concat(methodCategories)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    var methodAuthor = m.GetCustomAttribute<AuthorAttribute>()?.Name ?? classAuthor;
                    int timeoutMs = timeoutAttr?.Milliseconds ?? Timeout.Infinite;

                    var sourceAttr = m.GetCustomAttribute<TestCaseSourceAttribute>();
                    if (sourceAttr != null)
                    {
                        foreach (var srcCase in EnumerateSourceCases(type, m, sourceAttr))
                        {
                            AddCase(allTestCases, type, m, srcCase, testAttr, timeoutMs, categories, methodAuthor);
                        }

                        continue;
                    }

                    var testCases = m.GetCustomAttributes(typeof(TestCaseAttribute), false)
                                     .Cast<TestCaseAttribute>()
                                     .ToArray();

                    if (testCases.Length == 0)
                    {
                        allTestCases.Add(new TestCaseInfo
                        {
                            FixtureType = type,
                            Method = m,
                            Arguments = Array.Empty<object>(),
                            EffectivePriority = testAttr?.Priority ?? 0,
                            IsSkipped = testAttr?.Skip ?? false,
                            SkipReason = testAttr?.Reason,
                            TimeoutMs = timeoutMs,
                            Categories = categories,
                            Author = methodAuthor,
                            DisplayName = null
                        });
                    }
                    else
                    {
                        foreach (var tc in testCases)
                        {
                            allTestCases.Add(new TestCaseInfo
                            {
                                FixtureType = type,
                                Method = m,
                                Arguments = tc.Arguments ?? Array.Empty<object>(),
                                EffectivePriority = tc.Priority != 0 ? tc.Priority : (testAttr?.Priority ?? 0),
                                IsSkipped = tc.Skip || (testAttr?.Skip ?? false),
                                SkipReason = tc.Reason ?? testAttr?.Reason,
                                TimeoutMs = timeoutMs,
                                Categories = categories,
                                Author = methodAuthor,
                                DisplayName = null
                            });
                        }
                    }
                }
            }

            return allTestCases;
        }

        private static string[] GetCategories(Type fixtureType, MethodInfo method)
        {
            return fixtureType.GetCustomAttributes(true).OfType<CategoryAttribute>().Select(a => a.Name)
                .Concat(method.GetCustomAttributes(true).OfType<CategoryAttribute>().Select(a => a.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string? GetAuthor(Type fixtureType, MethodInfo method)
        {
            return method.GetCustomAttribute<AuthorAttribute>()?.Name
                ?? fixtureType.GetCustomAttribute<AuthorAttribute>()?.Name;
        }

        private static void AddCase(List<TestCaseInfo> allTestCases, Type type, MethodInfo method, TestCaseData srcCase, TestAttribute? testAttr, int timeoutMs, string[] baseCategories, string? baseAuthor)
        {
            var mergedCategories = baseCategories
                .Concat(srcCase.Categories ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            allTestCases.Add(new TestCaseInfo
            {
                FixtureType = type,
                Method = method,
                Arguments = srcCase.Arguments ?? Array.Empty<object>(),
                EffectivePriority = srcCase.Priority != 0 ? srcCase.Priority : (testAttr?.Priority ?? 0),
                IsSkipped = srcCase.Skip || (testAttr?.Skip ?? false),
                SkipReason = srcCase.Reason ?? testAttr?.Reason,
                TimeoutMs = timeoutMs,
                Categories = mergedCategories,
                Author = srcCase.Author ?? baseAuthor,
                DisplayName = srcCase.Name
            });
        }

        private static IEnumerable<TestCaseData> EnumerateSourceCases(Type fixtureType, MethodInfo testMethod, TestCaseSourceAttribute sourceAttr)
        {
            Type sourceType = sourceAttr.SourceType ?? fixtureType;

            const BindingFlags flags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance;

            var member = sourceType.GetMember(sourceAttr.MemberName, flags).FirstOrDefault();
            if (member == null)
                yield break;

            object? raw = null;

            try
            {
                switch (member)
                {
                    case MethodInfo mi:
                        raw = mi.Invoke(mi.IsStatic ? null : Activator.CreateInstance(sourceType), Array.Empty<object>());
                        break;

                    case PropertyInfo pi:
                        raw = pi.GetValue(pi.GetGetMethod(true)?.IsStatic == true ? null : Activator.CreateInstance(sourceType));
                        break;

                    case FieldInfo fi:
                        raw = fi.GetValue(fi.IsStatic ? null : Activator.CreateInstance(sourceType));
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SOURCE ERROR] {testMethod.Name}: {ex.GetBaseException().Message}");
                yield break;
            }

            if (raw == null)
                yield break;

            if (raw is IEnumerable<TestCaseData> typed)
            {
                foreach (var item in typed)
                    yield return item;

                yield break;
            }

            if (raw is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is TestCaseData tcd)
                        yield return tcd;
                    else if (item is object[] args)
                        yield return new TestCaseData(args);
                }
            }
        }

        private static List<TestCaseInfo> ExpandCases(List<TestCaseInfo> baseCases, int repetitions)
        {
            if (repetitions <= 1)
                return baseCases;

            var expanded = new List<TestCaseInfo>(baseCases.Count * repetitions);

            for (int i = 0; i < repetitions; i++)
            {
                expanded.AddRange(baseCases);
            }

            return expanded;
        }

        private static async Task RunFixtureAsync(
            Type fixtureType,
            List<TestCaseInfo> cases,
            DynamicThreadPool<TestResultRecord> pool,
            ConcurrentBag<TestResultRecord> summary)
        {
            WriteInfoLine($"\n=== Fixture: {fixtureType.FullName} ===");

            object lifecycleInstance;
            try
            {
                lifecycleInstance = Activator.CreateInstance(fixtureType)
                    ?? throw new Exception("Activator returned null");
            }
            catch (Exception ex)
            {
                foreach (var tc in cases)
                {
                    summary.Add(new TestResultRecord(
                        tc,
                        TestStatus.Error,
                        $"Cannot create fixture instance: {ex.GetBaseException().Message}",
                        TimeSpan.Zero,
                        _globalClock.Elapsed,
                        Thread.CurrentThread.ManagedThreadId));
                }

                WriteErrorLine($"ERROR: cannot create fixture instance for {fixtureType.Name}: {ex.GetBaseException().Message}");
                return;
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
                    await InvokeMaybeAsync(oneTimeSet, lifecycleInstance);
                }
                catch (Exception ex)
                {
                    oneTimeSetupSucceeded = false;
                    WriteErrorLine($"[ERROR] OneTimeSetUp failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");

                    foreach (var tc in cases)
                    {
                        summary.Add(new TestResultRecord(
                            tc,
                            TestStatus.Error,
                            $"OneTimeSetUp failed: {ex.GetBaseException().Message}",
                            TimeSpan.Zero,
                            _globalClock.Elapsed,
                            Thread.CurrentThread.ManagedThreadId));
                    }
                }
            }

            if (!oneTimeSetupSucceeded)
            {
                if (oneTimeTear != null)
                {
                    try
                    {
                        await InvokeMaybeAsync(oneTimeTear, lifecycleInstance);
                    }
                    catch (Exception ex)
                    {
                        WriteErrorLine($"[ERROR] OneTimeTearDown failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");
                    }
                }

                return;
            }

            var ordered = cases
                .OrderBy(t => t.EffectivePriority)
                .ThenBy(t => t.Method.Name)
                .ThenBy(t => t.TestName)
                .ToList();

            var handles = new List<PoolHandle<TestResultRecord>>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var tc = ordered[i];

                if (tc.IsSkipped)
                {
                    summary.Add(new TestResultRecord(
                        tc,
                        TestStatus.Skipped,
                        tc.SkipReason,
                        TimeSpan.Zero,
                        _globalClock.Elapsed,
                        Thread.CurrentThread.ManagedThreadId));

                    Console.WriteLine($"[SKIP] {tc.TestName} - {tc.SkipReason ?? "no reason"} (priority: {tc.EffectivePriority})");
                    continue;
                }

                int delayMs = GetProducerDelayMs(i);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                var handle = pool.Enqueue(
                    () => RunTestCaseBlocking(fixtureType, tc, setUp, tearDown),
                    tc.EffectivePriority,
                    $"{tc.TestName}#{i + 1}");

                handles.Add(handle);
            }

            foreach (var handle in handles)
            {
                try
                {
                    var result = await handle.Completion;
                    summary.Add(result);
                }
                catch (Exception ex)
                {
                    WriteErrorLine($"[ERROR] pooled test failed: {ex.GetBaseException().Message}");
                }
            }

            if (oneTimeTear != null)
            {
                try
                {
                    await InvokeMaybeAsync(oneTimeTear, lifecycleInstance);
                }
                catch (Exception ex)
                {
                    WriteErrorLine($"[ERROR] OneTimeTearDown failed for fixture {fixtureType.Name}: {ex.GetBaseException().Message}");
                }
            }
        }

        private static int GetProducerDelayMs(int index)
        {
            int slot = index % 40;

            if (slot < 10) return 20;       
            if (slot == 10) return 1200;    
            if (slot < 18) return 350;      
            if (slot < 35) return 0;        
            if (slot == 35) return 800;     
            return 100;                   
        }

        private static TestResultRecord RunTestCaseBlocking(
            Type fixtureType,
            TestCaseInfo tc,
            MethodInfo? setUp,
            MethodInfo? tearDown)
        {
            var sw = Stopwatch.StartNew();
            var startOffset = _globalClock.Elapsed;
            var threadId = Thread.CurrentThread.ManagedThreadId;

            object fixtureInstance;
            try
            {
                fixtureInstance = Activator.CreateInstance(fixtureType)
                    ?? throw new Exception("Activator returned null");
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TestResultRecord(
                    tc,
                    TestStatus.Error,
                    $"Cannot create fixture instance: {ex.GetBaseException().Message}",
                    sw.Elapsed,
                    startOffset,
                    threadId);
            }

            bool shouldRunTearDown = false;

            try
            {
                if (setUp != null)
                {
                    shouldRunTearDown = true;
                    try
                    {
                        InvokeMaybeAsync(setUp, fixtureInstance).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        var baseEx = ex.GetBaseException();

                        if (baseEx is TestSkippedException)
                        {
                            Console.WriteLine($"[SKIP] {tc.TestName} - {baseEx.Message} (priority: {tc.EffectivePriority})");
                            return new TestResultRecord(
                                tc,
                                TestStatus.Skipped,
                                baseEx.Message,
                                sw.Elapsed,
                                startOffset,
                                threadId);
                        }

                        Console.WriteLine($"[ERROR] {tc.TestName} - SetUp failed: {baseEx.Message} (priority: {tc.EffectivePriority})");
                        return new TestResultRecord(
                            tc,
                            TestStatus.Error,
                            $"SetUp failed: {baseEx.Message}",
                            sw.Elapsed,
                            startOffset,
                            threadId);
                    }
                }

                int timeoutMs = tc.TimeoutMs;
                var args = PrepareArguments(tc.Method, tc.Arguments);

                var testTask = InvokeMaybeAsync(tc.Method, fixtureInstance, args);

                if (timeoutMs != Timeout.Infinite)
                {
                    if (!testTask.Wait(timeoutMs))
                    {
                        sw.Stop();

                        _ = testTask.ContinueWith(t =>
                        {
                            var _ = t.Exception;
                        }, TaskContinuationOptions.OnlyOnFaulted);

                        Console.WriteLine($"[TIMEOUT] {tc.TestName} | Timeout = {timeoutMs} ms | Priority: {tc.EffectivePriority}");

                        return new TestResultRecord(
                            tc,
                            TestStatus.Failed,
                            $"Timeout after {timeoutMs} ms",
                            sw.Elapsed,
                            startOffset,
                            threadId,
                            true);
                    }
                }
                else
                {
                    testTask.GetAwaiter().GetResult();
                }

                sw.Stop();
                Console.WriteLine($"[PASS] {tc.TestName} (priority: {tc.EffectivePriority})");

                return new TestResultRecord(
                    tc,
                    TestStatus.Passed,
                    null,
                    sw.Elapsed,
                    startOffset,
                    threadId);
            }
            catch (TargetInvocationException tie)
            {
                sw.Stop();
                var inner = tie.InnerException ?? tie;
                return BuildResultFromException(tc, inner, sw.Elapsed, startOffset, threadId);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return BuildResultFromException(tc, ex, sw.Elapsed, startOffset, threadId);
            }
            finally
            {
                if (tearDown != null && shouldRunTearDown)
                {
                    try
                    {
                        InvokeMaybeAsync(tearDown, fixtureInstance).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        WriteErrorLine($"[ERROR] TearDown failed for {tc.TestName}: {ex.GetBaseException().Message}");
                    }
                }
            }
        }

        private static TestResultRecord BuildResultFromException(
            TestCaseInfo tc,
            Exception ex,
            TimeSpan duration,
            TimeSpan startOffset,
            int threadId)
        {
            var baseEx = ex.GetBaseException();

            if (baseEx is AssertionFailedException)
            {
                Console.WriteLine($"[FAIL] {tc.TestName} - {baseEx.Message} (priority: {tc.EffectivePriority})");
                return new TestResultRecord(tc, TestStatus.Failed, baseEx.Message, duration, startOffset, threadId);
            }

            if (baseEx is TestSkippedException)
            {
                Console.WriteLine($"[SKIP] {tc.TestName} - {baseEx.Message} (priority: {tc.EffectivePriority})");
                return new TestResultRecord(tc, TestStatus.Skipped, baseEx.Message, duration, startOffset, threadId);
            }

            Console.WriteLine($"[ERROR] {tc.TestName} - {baseEx.GetType().Name}: {baseEx.Message} (priority: {tc.EffectivePriority})");
            return new TestResultRecord(tc, TestStatus.Error, $"{baseEx.GetType().Name}: {baseEx.Message}", duration, startOffset, threadId);
        }

        private static MethodInfo? FindSingleMethodWithAttribute<TAttr>(Type fixtureType) where TAttr : Attribute
        {
            return fixtureType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .FirstOrDefault(m => m.GetCustomAttribute(typeof(TAttr)) != null);
        }

        private static object?[] PrepareArguments(MethodInfo method, object[] providedArgs)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
                return Array.Empty<object>();

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
                    }
                    else
                    {
                        args[i] = null;
                    }

                    continue;
                }

                if (i >= providedArgs.Length)
                {
                    args[i] = Type.Missing;
                    continue;
                }

                var targetType = param.ParameterType;
                var provided = providedArgs[i];

                if (provided == null)
                {
                    args[i] = null;
                    continue;
                }

                try
                {
                    if (targetType.IsInstanceOfType(provided))
                    {
                        args[i] = provided;
                        continue;
                    }

                    args[i] = Convert.ChangeType(provided, targetType);
                }
                catch
                {
                    if (targetType.IsEnum && provided is string s)
                    {
                        try
                        {
                            args[i] = Enum.Parse(targetType, s);
                            continue;
                        }
                        catch
                        {
                           
                        }
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

            if (ret == null)
                return;

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

        private static Func<TestCaseInfo, bool> BuildFilter(string? category, string? author, int? minPriority)
        {
            return tc =>
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    if (!tc.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
                        return false;
                }

                if (!string.IsNullOrWhiteSpace(author))
                {
                    if (!string.Equals(tc.Author, author, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (minPriority.HasValue && tc.EffectivePriority < minPriority.Value)
                    return false;

                return true;
            };
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly asm)
        {
            try
            {
                return asm.GetTypes().Where(t => t != null)!;
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        }

        private static void PrintSummary(ConcurrentBag<TestResultRecord> summary)
        {
            Console.WriteLine("\n=== Summary ===");

            var ordered = summary.OrderBy(s => s.StartOffset).ToList();

            var passCount = ordered.Count(s => s.Status == TestStatus.Passed);
            var failCount = ordered.Count(s => s.Status == TestStatus.Failed);
            var skipCount = ordered.Count(s => s.Status == TestStatus.Skipped);
            var errorCount = ordered.Count(s => s.Status == TestStatus.Error);

            Console.Write("Total: ");
            Console.Write(ordered.Count);

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

            Console.WriteLine($"Total duration: {_globalClock.Elapsed}");
        }

        private static void WriteInfoLine(string msg)
        {
            lock (_consoleLock)
            {
                Console.WriteLine(msg);
            }
        }

        private static void WriteErrorLine(string msg)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }

        private class TestCaseInfo
        {
            public Type FixtureType { get; init; } = null!;
            public MethodInfo Method { get; init; } = null!;
            public object[] Arguments { get; init; } = Array.Empty<object>();
            public int EffectivePriority { get; init; } = 0;
            public bool IsSkipped { get; init; } = false;
            public string? SkipReason { get; init; }
            public int TimeoutMs { get; init; } = Timeout.Infinite;

            public string[] Categories { get; init; } = Array.Empty<string>();
            public string? Author { get; init; }
            public string? DisplayName { get; init; }

            public string TestName =>
                DisplayName ?? $"{FixtureType.Name}.{Method.Name}({string.Join(", ", Arguments.Select(a => a?.ToString() ?? "null"))})";
        }

        private enum TestStatus
        {
            Passed,
            Failed,
            Skipped,
            Error
        }

        private class TestResultRecord
        {
            public TestCaseInfo TestCase { get; }
            public TestStatus Status { get; }
            public string? Message { get; }
            public TimeSpan Duration { get; }
            public TimeSpan StartOffset { get; }
            public int ThreadId { get; }
            public bool TimedOut { get; }

            public TestResultRecord(
                TestCaseInfo tc,
                TestStatus status,
                string? message = null,
                TimeSpan? duration = null,
                TimeSpan? startOffset = null,
                int threadId = 0,
                bool timedOut = false)
            {
                TestCase = tc;
                Status = status;
                Message = message;
                Duration = duration ?? TimeSpan.Zero;
                StartOffset = startOffset ?? TimeSpan.Zero;
                ThreadId = threadId;
                TimedOut = timedOut;
            }
        }
    }
}