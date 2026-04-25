using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Viaduct.Generation;
using Viaduct.Generation.Client;

namespace Viaduct.Tests
{
    public class GeneratorClientTests : GeneratorTestsBase
    {
        [Test]
        public async Task TestClientCodeGenerates()
        {
            var code = GenerateClientTestCode();
            await Assert.That(code).IsNotNull();
        }

        [Test]
        public async Task GeneratedClientCode_Compiles()
        {
            var meta = BuildMixedInterface();
            var generatedCode = GenerateClientTestCode(meta);
            var helperFunctions = ViaductGeneratorFunctions.HelperFunctionsCode;
            var stubSource = BuildStubInterfaceSource(meta, "public class UserDto { }");

            var compilation = CreateClientCompilation(generatedCode, helperFunctions, stubSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task GeneratedClientCode_NoParams_Compiles()
        {
            // Interface with a single parameterless async method
            var meta = new InterfaceMetaData("IPingApi", "TestApp", "/api/ping",
                [new MethodCreationInfo(
                "Ping", "Ping", "GET",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: new TypeReference("Task", "System.Threading.Tasks.Task<string>"),
                IsAsync: true, ReturnsValue: true,
                new ("string","string"))]);

            var generatedCode = GenerateClientTestCode(meta);
            var helperFunctions = ViaductGeneratorFunctions.HelperFunctionsCode;
            var stubSource = BuildStubInterfaceSource(meta);

            var compilation = CreateClientCompilation(generatedCode, helperFunctions, stubSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task GeneratedClientCode_SyncMethods_Compiles()
        {
            // Interface with only sync (non-async) methods — should compile with warnings, not errors
            var meta = new InterfaceMetaData("ISyncApi", "TestApp", "/api/sync", [
            new MethodCreationInfo(
                "GetValue", "GetValue", "GET",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: new TypeReference("Int32", "int"),
                IsAsync: false, ReturnsValue: true,
                null),
            new MethodCreationInfo(
                "DoWork", "DoWork", "POST",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: new TypeReference("Void", "void"),
                IsAsync: false, ReturnsValue: false,
                null)]);

            var generatedCode = GenerateClientTestCode(meta);
            var helperFunctions = ViaductGeneratorFunctions.HelperFunctionsCode;
            var stubSource = BuildStubInterfaceSource(meta);

            var compilation = CreateClientCompilation(generatedCode, helperFunctions, stubSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task GeneratedClientCode_NullBasePath_Compiles()
        {
            var meta = new InterfaceMetaData("IMinimalApi", "TestApp", null,[
                new MethodCreationInfo(
                "Health", "Health", "GET",
                Parameters: [],
                Body: null, CancellationToken: null,
                ReturnType: new TypeReference("Task", "System.Threading.Tasks.Task<string>"),
                IsAsync: true, ReturnsValue: true,
                new("string", "string"))]);

            var generatedCode = GenerateClientTestCode(meta);
            var helperFunctions = ViaductGeneratorFunctions.HelperFunctionsCode;
            var stubSource = BuildStubInterfaceSource(meta);

            var compilation = CreateClientCompilation(generatedCode, helperFunctions, stubSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task GeneratedClientCode_BodyAndRouteParams_Compiles()
        {
            // Method with a mix of simple route params and a body parameter
  

            var idParam = new ParameterCreationInfo("orderId", new TypeReference("Int32", "int"), 0,
                PredefinedTemplateId: null, QueryParameter: true, IsSimpleType: true, IsOptional: false);
            var bodyParam = new ParameterCreationInfo("order", new TypeReference("OrderDto", "TestApp.OrderDto"), 1,
                PredefinedTemplateId: null, QueryParameter: false, IsSimpleType: false, IsOptional: false);

            var method =new MethodCreationInfo(
                "UpdateOrder", "UpdateOrder", "PUT",
                Parameters: [idParam, bodyParam],
                Body: bodyParam, CancellationToken: null,
                ReturnType: new TypeReference("Task", "System.Threading.Tasks.Task"),
                IsAsync: true, ReturnsValue: false,
                null);

            var meta = new InterfaceMetaData("IOrderApi", "TestApp", "/api/orders", [method]);

            var generatedCode = GenerateClientTestCode(meta);
            var helperFunctions = ViaductGeneratorFunctions.HelperFunctionsCode;
            var stubSource = BuildStubInterfaceSource(meta, "public class OrderDto { }");

            var compilation = CreateClientCompilation(generatedCode, helperFunctions, stubSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        // ─── Helpers ────────────────────────────────────────────────────

        internal static string GenerateClientTestCode()
            => GenerateClientTestCode(BuildMixedInterface());

        internal static string GenerateClientTestCode(InterfaceMetaData interfaceData)
        {
            // Create one MethodCallerInfo per method (Location.None + dummy interceptor)
            var callers = interfaceData.Methods.Select(m =>
                new MethodCallerInfo(
                    m.Name,
                    LocationInfo.None,
                    Interceptor: "// test interceptor",
                    IsServerCall: false,
                    InterfaceMetaData: interfaceData));

            var creator = new ClientGeneratorFunctions.ClientCodeCreator(interfaceData, callers);
            return creator.Create();
        }
    }
}
