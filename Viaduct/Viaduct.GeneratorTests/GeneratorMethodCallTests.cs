using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Viaduct.Generation;

namespace Viaduct.Tests
{
    public class GeneratorMethodCallTests:GeneratorTestsBase
    {
        [Test]
        [Arguments("ServerMappings.RegisterEndpoints<ITestApi>()")]
        [Arguments("ServerMappings.AddEndpoints<ITestApi>(null)")]
        [Arguments("services.AddSingletonAndMap<ITestApi,ITestApi>()")]
        [Arguments("var serv  = ServerMappings.AddSingletonAndMap<ITestApi, ITestApi>(services)")]
        [Arguments("dynamic obj = null;obj.RegisterEndpoints<ITestApi>()", false)]
        [Arguments("services.CreateAndAddHttpClient<ITestApi>()")]
        public Task MethodIsFound(string Method, bool? IsValidMethod = null) => checkMethodFoundPredicate(Method, true, IsValidMethod);

        [Test]
        [Arguments("dynamic obj = null;obj.MethodThatDoesNotExist()")]
        [Arguments("services.AddScoped<ITestApi>()")]
        [Arguments("ServerMappings.AddRegisteredEndpoints((IEndpointRouteBuilder)null)")]
        public Task OtherMethodsAreNotFound(string Method) => checkMethodFoundPredicate(Method, false);


        async Task checkMethodFoundPredicate(string Method, bool ShouldBeFoundByPredicate, bool? IsValidMethod = null)
        {
            var code = $$"""
public interface ITestApi
{
    Task Foo();
}

class TestClass
{
    IServiceCollection services = null;
    public void Test()
    {
        {{Method}};
    }
}
""";
            var compilation = CreateModel(code);
            var tree = compilation.FirstTree;
            await Assert.That(tree).IsNotNull();
            var MethodSymbol  = (InvocationExpressionSyntax?)tree!.Tree.GetRoot()
                .DescendantNodes().FirstOrDefault(s => ViaductGeneratorFunctions.IsCandidateInvocation(s, CancellationToken.None));
            bool hasMethod = MethodSymbol is not null;
            await Assert.That(hasMethod).IsEqualTo(ShouldBeFoundByPredicate);

            if (hasMethod)
            {
                var methodInfo = ViaductGeneratorFunctions.GetMethodCallInfo(MethodSymbol!, tree.Model, CancellationToken.None).Result?.Method;
                if (IsValidMethod ?? ShouldBeFoundByPredicate)
                {
                    await Assert.That(methodInfo).IsNotNull();
                    await Assert.That(methodInfo!.InterfaceMetaData.Name).IsEqualTo("ITestApi");
                }
                else
                    await Assert.That(methodInfo).IsNull();
            }

        }
    }
}
