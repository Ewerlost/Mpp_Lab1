using System;
public class AssertionFailedException : Exception { public AssertionFailedException(string msg) : base(msg) { } }
public class TestSkippedException : Exception { public TestSkippedException(string msg = null) : base(msg) { } }
public class TestSetupException : Exception { public TestSetupException(string msg = null) : base(msg) { } }
public class TestExecutionException : Exception { public TestExecutionException(string msg = null) : base(msg) { } }
