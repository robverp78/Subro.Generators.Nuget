using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using static Viaduct.ViaductFunctions;

namespace Viaduct.Generation
{

    partial class ViaductGeneratorFunctions
    {


        /// <summary>
        /// Generates <see cref="InterfaceMetaData"/> from the given interface type symbol, extracting method information and route templates based on attributes and conventions.
        /// </summary>        
        /// <returns></returns>
        public static TransformResult< InterfaceMetaData> CreateInterfaceMetaData(ITypeSymbol type)
        {
            var builder = new InterfaceMetadataBuilder(type);
            try
            {
                builder.Create();
                return builder;
            }
            catch(Exception ex)
            {
                var err = ViaductErrors.CouldNotCreateInterfaceData.CreateError(LocationInfo.From(type), "Failed to create interface metadata: {0}", ex.Message);
                return builder.FatalError(err);
            }
        }

        class InterfaceMetadataBuilder(ITypeSymbol type):TransformResultBuilder<InterfaceMetaData>
        {
            readonly ITypeSymbol type = type;
            readonly string? BasePath  = GetBasePath(type).CheckRoutePart();

            bool BasePathHasRouteParameters;


        public void Create()
            {
                BasePathHasRouteParameters = BasePath is not null && ViaductGeneratorFunctions.HasRouteParameters(BasePath);
                List<MethodCreationInfo> methodInfoList = [];
                // Get all methods from the interface, including those inherited from base interfaces.
                // GetMembers() only returns directly-declared members, so base-interface methods (e.g. the
                // members of ICrud<T,Guid> behind ICrudGuid<T>) must be pulled from AllInterfaces. For a
                // constructed generic, AllInterfaces already has the type arguments substituted.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var methods = type.GetMembers().OfType<IMethodSymbol>()
                    .Concat(type.AllInterfaces.SelectMany(static i => i.GetMembers().OfType<IMethodSymbol>()))
                    .Where(m => m.MethodKind == MethodKind.Ordinary // Exclude property accessors and the likes
                        && seen.Add(MethodSignatureKey(m)));

                foreach (var method in methods)
                {
                    try
                    {
                        var methodInfo = GenerateMethodInfo(method);
                        if(methodInfo is not null)
                            methodInfoList.Add(methodInfo);            
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Add(
                            ViaductErrors.CouldNotCreateMethodData.CreateError(
                                LocationInfo.From(method),
                            "Failed to generate method info for {0}: {1}",
                            method.Name, ex.Message));
                    }
                }

                if(methodInfoList.Count == 0)
                {
                    Diagnostics.Add(
                        ViaductErrors.NoMethodsFound
                            .CreateWarning(LocationInfo.From(type), "No methods found on {0}", type.Name));
                }
                else
                {
                    var methodArray = methodInfoList.ToImmutableArray();
                    var ns = type.ContainingNamespace.ToDisplayString();

                    // A closed (constructed) generic interface, e.g. ICrudGuid<AddressRecord>. Open generics
                    // (with unresolved type parameters) are never reached here because the registration call site
                    // always supplies concrete type arguments.
                    if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: > 0 } named
                        && named.TypeArguments.All(static t => t.TypeKind != TypeKind.TypeParameter))
                    {
                        var fqn = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        Result = new(type.Name, ns, BasePath, methodArray)
                        {
                            IsGeneric = true,
                            FullyQualifiedName = fqn,
                            Identifier = BuildGenericIdentifier(named, fqn),
                            TypeArgSegment = BuildTypeArgSegment(named),
                        };
                    }
                    else
                        Result = new(type.Name, ns, BasePath, methodArray);
                }
            }

            /// <summary>Stable key for de-duplicating methods that appear via multiple inherited interfaces.</summary>
            static string MethodSignatureKey(IMethodSymbol m)
                => m.Name + "(" + string.Join(",", m.Parameters.Select(static p => p.Type.ToDisplayString())) + ")";

            /// <summary>Route discriminator from the type arguments' short names, e.g. <c>AddressRecord</c> (single arg) or <c>AddressRecord_Guid</c> (multiple).</summary>
            static string BuildTypeArgSegment(INamedTypeSymbol named)
                => string.Join("_", named.TypeArguments.Select(static t => Sanitize(t.Name)));

            /// <summary>Class/file-name-safe identifier that encodes the closure, e.g. <c>ICrudGuid_AddressRecord_ab12cd</c>.</summary>
            static string BuildGenericIdentifier(INamedTypeSymbol named, string fullyQualified)
            {
                var mangled = Sanitize(named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                return $"{mangled}_{StableShortHash(fullyQualified)}";
            }

            static string Sanitize(string s)
            {
                var chars = s.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                    if (!char.IsLetterOrDigit(chars[i]))
                        chars[i] = '_';
                return new string(chars);
            }

            /// <summary>Deterministic 6-hex-char FNV-1a hash (GetHashCode is randomized per run and unusable for stable names).</summary>
            static string StableShortHash(string s)
            {
                uint hash = 2166136261u;
                foreach (var c in s)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
                return hash.ToString("x8").Substring(0, 6);
            }


            static readonly Regex GetRouteParametersRegex = new(GetRouteParametersPattern, RegexOptions.Compiled);
            static IEnumerable<(string name, string text)> GetRouteParameters(string url)
            {
                var matches = GetRouteParametersRegex.Matches(url);
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    yield return (m.Groups["name"].Value, m.Value);
                }
            }



            private MethodCreationInfo? GenerateMethodInfo(IMethodSymbol method)
            {
                var attInfo = ExtractInformationFromAttributes(method);
                if (attInfo == null) return null;
                var (httpMethod, routeTemplate, _, _) = attInfo.Value;

                // If no HTTP method attribute found, determine from method name
                if (string.IsNullOrEmpty(httpMethod))
                {
                    httpMethod = DetermineHttpMethodFromName(method.Name);
                }

                // Process parameters
                var parameters = new List<ParameterCreationInfo>();                
                ParameterCreationInfo? bodyParameter = null, cancellationToken = null;

                bool hasPreDefinedRouteParams = HasRouteParameters(routeTemplate);
                bool checkRouteParams = hasPreDefinedRouteParams || BasePathHasRouteParameters;                

                HashSet<string>? routeParams;
                if (checkRouteParams)
                {
                    //test template with pre defined route parameters.
                    //Can only give a warning if a route does not contain all parameters because runtime
                    //path (and parameter resolving) can change. The final check can only be done in runtime
                    string routeToCheck = BasePathHasRouteParameters ? BasePath! : string.Empty;
                    if (hasPreDefinedRouteParams) routeToCheck += routeTemplate;
                    routeParams = new HashSet<string>(GetRouteParameters(routeToCheck).Select(n => n.name), StringComparer.OrdinalIgnoreCase);
                }
                else
                    routeParams = null;

                // Get return type
                var returnType = method.ReturnType;
                bool IsAsync = returnType.IsTaskLike(out var innerType);
                var returnTypeRef = new TypeReference(returnType);
                
                int index = 0;                
                
                foreach (var parameter in method.Parameters)
                {
                    var paramType = new TypeReference(parameter.Type);
                    bool isCancellationToken = parameter.IsCancellationToken();
                    bool IsSimpleType = false;
                    var querySafe = !isCancellationToken && CanBindFromString(parameter.Type, out IsSimpleType);


                    if (checkRouteParams)
                        routeParams!.Remove(parameter.Name);

                    string? getTemplateId()
                    {                        
                        if (hasPreDefinedRouteParams)
                        {
                            // Try to find a matching parameter in the route template
                            var templateId = $"{{{parameter.Name}}}";
                            if (routeTemplate.Contains(templateId))
                            {
                                if (!querySafe)
                                    Diagnostics.Add(
                                        ViaductErrors.ParameterIsNotSafeForRoute.CreateWarning(
                                            LocationInfo.From(parameter),
                                            "Parameter {0} is in the route template, but is unsafe for binding", parameter.Name));
                                return templateId;
                            }                            
                        }
                        return null;
                    }

                    var paramInfo = new ParameterCreationInfo(
                        parameter.Name,
                        paramType,
                        index++,
                        getTemplateId(),
                        querySafe,
                        IsSimpleType,
                        parameter.IsOptional);

                    parameters.Add(paramInfo);

                    if(isCancellationToken)
                    {
                        if (cancellationToken == null)
                            cancellationToken = paramInfo;
                        else
                            Diagnostics.Add(
                            ViaductErrors.MoreThanOneCancellationTokenArgument.CreateWarning(
                            LocationInfo.From(parameter),
                            "Multiple cancellation token arguments found."));                            ;
                    }
                    else if (!querySafe && paramInfo.PredefinedTemplateId == null)
                    {
                        if (bodyParameter != null)
                            Diagnostics.Add(
                                ViaductErrors.MoreThanOneBodyParameter.CreateError(
                                    LocationInfo.From(parameter),
                                $"Multiple complex parameters found. Only one complex parameter can be used as a body for web calls."));
                        else
                            bodyParameter = paramInfo;
                    }
                }


                var res = new MethodCreationInfo(
                    method.Name,
                    routeTemplate.CheckRoutePart()!,
                    httpMethod!,
                    [.. parameters],
                    bodyParameter,
                    cancellationToken,
                    returnTypeRef,
                    IsAsync,
                    IsAsync ? ((INamedTypeSymbol)returnType).IsGenericType : returnType.SpecialType != SpecialType.System_Void,
                    innerType
                    )
                {IgnoreForClientGeneration = attInfo.Value.IgnoreInClient, IgnoreForServerGeneration = attInfo.Value.IgnoreInServer };

                if (!IsAsync)
                {

                    Diagnostics.Add(
                        ViaductErrors.NotAnAsyncMethod.CreateWarning(
                           LocationInfo.From(method),
                           "Return type '{0}' on method {1} is not asynchronous.",
                           returnType.Name, method.Name));
                }

                if(hasPreDefinedRouteParams && routeParams!.Count > 0)
                {
                    string missingParams = string.Join(", ", routeParams);
                    Diagnostics.Add(
                        ViaductErrors.RouteParameterIsMissingInMethodSignature.CreateWarning(
                            LocationInfo.From( method),
                           "Method {0} is missing the parameters: {1} which are defined in the route {2}{3}",
                             method.Name, missingParams, BasePath ?? string.Empty, routeTemplate));
                }

                return res; 
            }
        }

        static bool IsCancellationToken(this IParameterSymbol parameter)
        {
            return parameter.Type.Name == "CancellationToken"
                && parameter.Type.ContainingNamespace.ToDisplayString() == "System.Threading";
        }

        private static readonly Regex HasRouteParametersRegex = new(HasRouteParametersPattern, RegexOptions.Compiled);
        internal static bool HasRouteParameters(string url) => HasRouteParametersRegex.IsMatch(url);

        /// <summary>
        /// Extract base path from attributes or determine from the interface name
        /// </summary>                
        static string? GetBasePath(ITypeSymbol type)
        {
            foreach (var attribute in type.GetAttributes())
            {
                var attrName = attribute.AttributeClass?.Name;
                if (attrName == nameof(BasePathAttribute))
                {
                    if (attribute.ConstructorArguments.Length > 0 &&
                        attribute.ConstructorArguments[0].Value is string basePath)
                    {
                        return basePath;
                    }
                    break;
                }
            }
            return null;
           
        }

    }
}
