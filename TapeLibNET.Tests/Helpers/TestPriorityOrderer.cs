using Xunit.Abstractions;
using Xunit.Sdk;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Marks a test method with an execution priority for <see cref="PriorityOrderer"/>.
/// Lower values run first. Tests without this attribute default to <c>int.MaxValue</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}

/// <summary>
/// xUnit test case orderer that sorts by <see cref="TestPriorityAttribute"/> value.
/// Usage: <c>[TestCaseOrderer("TapeLibNET.Tests.Helpers.PriorityOrderer", "TapeLibNET.Tests")]</c>
/// </summary>
public sealed class PriorityOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        return testCases.OrderBy(GetPriority);
    }

    private static int GetPriority<TTestCase>(TTestCase testCase) where TTestCase : ITestCase
    {
        var attr = testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestPriorityAttribute))
            .FirstOrDefault();

        return attr?.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority)) ?? int.MaxValue;
    }
}
