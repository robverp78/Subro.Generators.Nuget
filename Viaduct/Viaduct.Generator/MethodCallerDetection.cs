using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Generation
{
    partial class ViaductGeneratorFunctions
    {
        // Method names that trigger client proxy generation
        private static readonly HashSet<string> ClientRegistrationNames =
        [
            "CreateAndAddHttpClient"
        ];

        // Combined for detection
        private static readonly HashSet<string> AllRegistrationNames =
            [.. ClientRegistrationNames, .. Server.Methods.Keys];

        public static bool IsCandidateInvocation(SyntaxNode node, CancellationToken ct)
        {
            // Pure syntax checks only - no semantic model
            if (node is not InvocationExpressionSyntax invocation)
                return false;

            // Extract the method name from either:
            // services.AddScopedAndMap<T,U>()  → MemberAccessExpression
            // AddScopedAndMap<T,U>()           → IdentifierName
            var methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                GenericNameSyntax g => g.Identifier.Text,
                _ => null
            };

            if (methodName is null || !AllRegistrationNames.Contains(methodName))
                return false;

            // Check generic args exist - cheap syntax check
            // No need to validate count here, transform will catch malformed calls
            var genericName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
                GenericNameSyntax g => g,
                _ => null
            };
            return genericName?.TypeArgumentList.Arguments.Count > 0;
        }

        internal static TransformResult<CreationInfo> GetMethodCallInfo(GeneratorSyntaxContext context, CancellationToken ct)
        {        
            if (context.Node is not InvocationExpressionSyntax invocation)
                return default;
            try
            {                
                return GetMethodCallInfo(invocation, context.SemanticModel, ct);
            }
            catch(Exception ex)
            {
                return
                    ViaductErrors.CouldNotCreateMetaData.CreateWarning(
                        LocationInfo.From(invocation), "Exception during method call analysis: {0}" , ex.Message);
   
            }
        }

        public static TransformResult<CreationInfo> GetMethodCallInfo(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken ct)
        {            
            // Semantic model to verify it's actually a method
            if (semanticModel.GetSymbolInfo(invocation, ct).Symbol
                is not IMethodSymbol methodSymbol)
                return default;

            // Get interceptable location - new Roslyn API replacing file/line/column
            var interceptableLocation = semanticModel.GetInterceptableLocation(invocation, ct);
            if (interceptableLocation is null)
                return default;

            // Determine server vs client
            var isServer = Server.Methods.ContainsKey(methodSymbol.Name);

            var containingType = isServer ? "Server.ServerMappings" : "Client.ClientMappings";
            // Verify it's in your class
            if (!methodSymbol.ContainingType.ToDisplayString().StartsWith($"Viaduct.{containingType}"))
                return default;

            // Extract interface type from generic arguments.
            // For C# 14 extension members (e.g. AddScopedAndMap<TImplementation> inside extension<TInterface>),
            // the interface type parameter TInterface is on the containing extension type,
            // not on the method itself. For non-extension methods (e.g. AddEndpoints<TInterface>),
            // the interface type is the method's own first type argument.
            var typeArgs = methodSymbol.ContainingType.TypeArguments.Length > 0
                ? methodSymbol.ContainingType.TypeArguments
                : methodSymbol.TypeArguments;

            if (typeArgs.Length == 0 || typeArgs[0] is not INamedTypeSymbol interfaceSymbol)
                return default;

            var interfaceData = CreateInterfaceMetaData(interfaceSymbol);
            if (interfaceData.IsEmpty) return default;

            var res = new TransformResultBuilder<CreationInfo>();
            res.Diagnostics.AddRange(interfaceData.Diagnostics);

            if(interfaceData.Result is InterfaceMetaData iData)
            res.Result = new(new MethodCallerInfo(
                methodSymbol.Name,
                LocationInfo.FromCaller(invocation),
                interceptableLocation.GetInterceptsLocationAttributeSyntax(),
                isServer,
                iData),
                null);

            return res;
        }

        
    }
}
