using Microsoft.CodeAnalysis;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TUnit.Core;
using Viaduct.Generation;

namespace Viaduct.Tests
{
    #region Check type/method/route/parameter information Tests

    public class GeneratorMetaDataTests : GeneratorTestsBase
    {
        public class TypeInformationTestCase
        {
            public string Description { get; }
            public string Code { get; }
            public string ExpectedHttpMethod { get; }
            public string ExpectedRoute { get; }
            public int ParameterCount { get; set; } = 0;
            public string[]? ExpectedParameterNames { get; set; }
            internal ViaductErrors[]? ExpectedWarnings { get; set; }
            public bool? HasReturnValue { get; set; }

            public TypeInformationTestCase(string description, string code, string expectedHttpMethod, string expectedRoute)
            {
                Description = description;
                Code = code;
                ExpectedHttpMethod = expectedHttpMethod;
                ExpectedRoute = expectedRoute;
            }

            public override string ToString() => Description;
        }

        public static IEnumerable<TypeInformationTestCase> TypeInformationTestCases()
        =>
        [
            new(
                "No attributes: defaults to information from methodname",
                """
                public interface ITestApi
                {    
                    Task<string> GetUsers();
                }
                """,
                "GET",
                "/GetUsers"
            ),

            new(
                "BasePath on interface + HttpGet on method",
                """
                [BasePath("users")]
                public interface ITestApi
                {
                    [HttpGet("get")]
                    Task<string> GetUsers();
                }
                """,
                "GET",
                "/users/get"
            ),

            new(
                "HttpPost attribute without BasePath uses interface name, but http attribute determines method route",
                """
                public interface ITestApi
                {
                    [HttpPost("get")]
                    Task<string> GetUsers();
                    [ViaductIgnore]
                    void IgnoreMe();
                }
                """,
                "POST",
                "/get"
            ) { HasReturnValue = true },

            new(
                "HttpPut with absolute route keeps leading slash and interface prefix. Fixed route template parameter, but with more parameters",
                """
                public interface ITestApi
                {
                    [HttpPut("/users/{id}/{missingParam}")]
                    Task UpdateUser(int id, string name);

                    bool IAmNotAMethod{get;}
                }
                """,
                "PUT",
                "/users/{id}/{missingParam}"
            ) { ParameterCount = 2, ExpectedParameterNames = ["id"], HasReturnValue = false, ExpectedWarnings = [ViaductErrors.RouteParameterIsMissingInMethodSignature] },

            new(
                "BasePath + HttpGet  appended as route segments",
                """
                [BasePath("users/{id}")]
                public interface ITestApi
                {
                    [CustomRoute("get")]
                    [HttpMethodType("GET")]
                    void LoadUser(int id, string name);
                }
                """,
                "GET",
                "/users/{id}/get"
            ) { ParameterCount = 2, ExpectedParameterNames = ["id", "name"], ExpectedWarnings = [ViaductErrors.NotAnAsyncMethod] },

            new(
                "Cancellation token is not included in route",
                """
                [BasePath("users/{id}")]
                public interface ITestApi
                {
                    [CustomRoute("get")]
                    [HttpMethodType("GET")]
                    Task LoadUserAsync(int id, string name, CancellationToken token);
                }
                """,
                "GET",
                "/users/{id}/get"
            ) { ParameterCount = 3, ExpectedParameterNames = ["id", "name", "token"] },
        ];

        /// <summary>
        /// Tests that the HTTP method and route template are correctly determined from method attributes and interface-level base paths.
        /// </summary>
        [Test]
        [MethodDataSource(nameof(TypeInformationTestCases))]
        public async Task TestIfTheTypeInformationFunctionsGiveCorrectMethodAndRouteInformation(TypeInformationTestCase testCase)
        {
            var testCode = "using System;using System.Threading; using System.Threading.Tasks;using Viaduct;using Microsoft.AspNetCore.Mvc; namespace Test{" + testCase.Code + "}";
            var compilation = CreateModel(testCode);

            var interfaceSymbol = compilation.GetSymbol("Test.ITestApi");
            await Assert.That(interfaceSymbol).IsNotNull();

            var transformResult = ViaductGeneratorFunctions.CreateInterfaceMetaData(interfaceSymbol);
            await Assert.That(transformResult.IsEmpty).IsFalse();

            var result = transformResult.Result;
            await Assert.That(result).IsNotNull();
            await Assert.That(result.Name).IsNotNull();
            await Assert.That(result.Namespace).IsNotNull();

            var methodInfo = result.Methods.Single();
            await Assert.That(methodInfo.HttpMethod).IsEqualTo(testCase.ExpectedHttpMethod).IgnoringCase();
            await Assert.That(result.BasePath + methodInfo.RouteTemplate).IsEqualTo(testCase.ExpectedRoute);
            await Assert.That(methodInfo.Parameters.Length).IsEqualTo(testCase.ParameterCount);

            if (testCase.ExpectedParameterNames is not null)
            {
                for (int i = 0; i < testCase.ExpectedParameterNames.Length; i++)
                    await Assert.That(methodInfo.Parameters[i].Name).IsEqualTo(testCase.ExpectedParameterNames[i]);
            }

            if (testCase.HasReturnValue is bool hasReturnValue)
                await Assert.That(methodInfo.ReturnsValue).IsEqualTo(hasReturnValue);

            var serverCode = ViaductGeneratorFunctions.CreateServerMappingCode(result, []);
            await Assert.That(serverCode).IsNotNull();

            if (testCase.ExpectedWarnings is not null and var warnings)
            {
                foreach (var warning in warnings)
                {
                    await Assert.That(transformResult.Diagnostics)
                        .Contains(w => w.Descriptor.Id == warning.GetCode());
                }
            }

            var serverCompilation = createCompiler().Create([serverCode, ViaductGeneratorFunctions.HelperFunctionsCode, testCode]);
            await Assert.That(serverCompilation).IsNotNull();
        }

        [Test]
        [Arguments("GetUsers", "GET")]
        [Arguments("ListUsers", "GET")]
        [Arguments("FindUsers", "GET")]
        [Arguments("SearchUsers", "GET")]
        [Arguments("QueryUsers", "QUERY")]
        [Arguments("CreateUser", "POST")]
        [Arguments("AddUser", "POST")]
        [Arguments("PostUser", "POST")]
        [Arguments("UpdateUser", "PUT")]
        [Arguments("ModifyUser", "PUT")]
        [Arguments("PutUser", "PUT")]
        [Arguments("DeleteUser", "DELETE")]
        [Arguments("RemoveUser", "DELETE")]
        [Arguments("PatchUser", "PATCH")]
        [Arguments("ConnectUser", "CONNECT")]
        [Arguments("DoSomething", "POST")] // Default fallback
        public async Task DetermineHttpMethodFromSymbol_WithoutAttribute_InfersFromMethodName(string methodName, string expectedHttpMethod)
        {
            var code = $$"""
public interface ITestApi
{
    Task {{methodName}}();
}
""";
            var compilation = CreateModel(code);
            var type = compilation.GetSymbol("ITestApi");

            await Assert.That(type).IsNotNull();

            var result = ViaductGeneratorFunctions.CreateInterfaceMetaData(type);

            await Assert.That(result.Diagnostics).IsEmpty();
            await Assert.That(result.Result).IsNotNull();

            var methodInfo = result.Result.Methods.Single();
            await Assert.That(methodInfo.HttpMethod).IsEqualTo(expectedHttpMethod);
        }

        #endregion

        #region CanBindFromString Tests

        async Task TestCanBind(string code, bool expected)
        {
            var compilation = CreateModel(code);
            var method = compilation.GetMethodSymbol("Method");
            await Assert.That(method).IsNotNull();

            var paramType = method.Parameters[0].Type;
            var result = ViaductGeneratorFunctions.CanBindFromString(paramType, out _);
            await Assert.That(result).IsEqualTo(expected);
        }

        [Test]
        [Arguments("int")]
        [Arguments("string")]
        [Arguments("bool")]
        [Arguments("byte")]
        [Arguments("short")]
        [Arguments("long")]
        [Arguments("float")]
        [Arguments("double")]
        [Arguments("decimal")]
        [Arguments("char")]
        public async Task CanBindFromString_WithPrimitiveTypes_ReturnsTrue(string typeName)
        {
            var code = $$"""
public interface ITestApi
{
    Task Method({{typeName}} param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        public async Task CanBindFromString_WithDateTime_ReturnsTrue()
        {
            const string code = """
public interface ITestApi
{
    Task Method(System.DateTime param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        [Arguments("System.Guid")]
        [Arguments("System.DateTimeOffset")]
        [Arguments("System.TimeSpan")]
        [Arguments("System.DateOnly")]
        [Arguments("System.TimeOnly")]
        [Arguments("System.Version")]
        public async Task CanBindFromString_WithWellKnownScalars_ReturnsTrue(string typeName)
        {
            var code = $$"""
public interface ITestApi
{
    Task Method({{typeName}} param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        public async Task CanBindFromString_WithEnum_ReturnsTrue()
        {
            const string code = """
public enum TestEnum { Value1, Value2 }

public interface ITestApi
{
    Task Method(TestEnum param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        public async Task CanBindFromString_WithNullablePrimitive_ReturnsTrue()
        {
            const string code = """
public interface ITestApi
{
    Task Method(int? param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        public async Task CanBindFromString_WithNullableGuid_ReturnsTrue()
        {
            const string code = """
public interface ITestApi
{
    Task Method(System.Guid? param);
}
""";
            await TestCanBind(code, true);
        }

        [Test]
        public async Task CanBindFromString_WithComplexType_ReturnsFalse()
        {
            const string code = """
public class UserDto 
{
    public string Name { get; set; }
    public int Age { get; set; }
}

public interface ITestApi
{
    Task Method(UserDto param);
}
""";
            await TestCanBind(code, false);
        }

        [Test]
        public async Task CanBindFromString_WithArray_ReturnsFalse()
        {
            const string code = """
public interface ITestApi
{
    Task Method(int[] param);
}
""";
            await TestCanBind(code, false);
        }

        [Test]
        public async Task CanBindFromString_WithList_ReturnsFalse()
        {
            const string code = """
public interface ITestApi
{
    Task Method(System.Collections.Generic.List<int> param);
}
""";
            await TestCanBind(code, false);
        }

        #endregion

        #region warning and errors

        [Test]
        public async Task NotATaskReturnsWarnings()
        {
            const string code = """
public interface ITestApi
{
    Task MethodWithTask(); //no warning
    Task<string> MethodReturnsStringTask(); //no warning
    ValueTask<string> MetodReturnsStringValueTask(); //no warning
    void MethodWithVoidReturn(); //warning: method should return a task
    int MethodWithIntReturn(); //warning: method should return a task
}
""";
            var compilation = CreateModel(code);
            var interfaceData = compilation.GetSymbol("ITestApi");
            await Assert.That(interfaceData).IsNotNull();

            var result = ViaductGeneratorFunctions.CreateInterfaceMetaData(interfaceData);

            await Assert.That(result.Diagnostics).IsNotEmpty();
            await Assert.That(result.Result).IsNotNull();
            await Assert.That(result.Result.Methods.Length).IsEqualTo(5);

            var notAsyncWarnings = result.Diagnostics
                .Where(static w => w.Descriptor.Id == ViaductErrors.NotAnAsyncMethod.GetCode())
                .ToList();
            var notAsyncMethods = result.Result.Methods
                .Where(static m => !m.Name.EndsWith("Task"))
                .ToList();

            await Assert.That(notAsyncWarnings.Count).IsEqualTo(notAsyncMethods.Count);
        }

        #endregion
    }
}