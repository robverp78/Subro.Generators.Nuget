using Microsoft.CodeAnalysis;
using Subro.Generators;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Viaduct.Generation
{
    partial class ViaductGeneratorFunctions
    {
        extension(IMethodSymbol method)
        {

            public string GetSignature()
            {
                var parameters = method.Parameters
                    .Select(p =>
                    (
                        type: GetTypeName(p.Type),
                        refKind: p.RefKind
                    ));

                return GetMethodKey(
                    method.Name,
                    method.TypeParameters.Length,
                    parameters);
            }

            private (string? HttpMethod, string RouteTemplate, bool IgnoreInClient, bool IgnoreInServer)? ExtractInformationFromAttributes()
            {
                string? routeTemplate = null;
                string? httpMethod = null;
                bool IgnoreInClient = false, IgnoreInServer = false;

                // Check for HTTP method attributes
                foreach (var attribute in method.GetAttributes())
                {
                    var attributeClass = attribute.AttributeClass;
                    if (attributeClass == null) continue;

                    var attributeName = attributeClass.Name;
                    if (attributeName == nameof(ViaductIgnoreAttribute))
                        return null;
                    /* pointless to exclude for client at the moment (interface method needs to be implemented anyway),
                     * but variables kept in place in case they are needed in the future
                     * else if (attributeName == nameof(ViaductIgnoreForClientAttribute))
                        IgnoreInClient = true;*/
                    else if (attributeName == nameof(ViaductIgnoreForServerAttribute))
                        IgnoreInServer = true;
                    // Check for HttpMethodTypeAttribute
                    else if (attributeName == nameof(HttpMethodTypeAttribute))
                    {
                        if (attribute.ConstructorArguments.Length > 0)
                        {
                            var arg = attribute.ConstructorArguments[0];
                            // The argument could be either an HttpMethod or a string
                            if (arg.Value is string methodStr)
                            {
                                httpMethod = methodStr;
                            }
                            else if (arg.Value is int methodInt)
                            {
                                // HttpMethod enum value - need to extract the string representation
                                httpMethod = arg.Type?.ToDisplayString() ?? "POST";
                            }
                        }
                    }
                    // Check for ASP.NET Core HTTP method attributes (HttpGet, HttpPost, etc.)
                    else if (attributeName.StartsWith("Http") &&
                             (attributeClass.BaseType?.Name == "HttpMethodAttribute" || attributeClass.BaseType?.Name == "HttpMethod"))
                    {
                        // Extract method from attribute name (e.g., HttpGet -> GET or HttpGetAttribute -> GET)
                        var methodPart = attributeName.Substring(4);
                        if (methodPart.EndsWith("Attribute"))
                            methodPart = methodPart.Substring(0, methodPart.Length - 9);

                        httpMethod = methodPart.ToUpperInvariant();

                        // Check for template in constructor
                        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string template)
                        {
                            routeTemplate = template;
                        }
                    }
                    // Check for CustomRouteAttribute
                    else if (attributeName == nameof(CustomRouteAttribute))
                    {
                        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string route)
                        {
                            routeTemplate = route;
                        }
                    }
                    else if(attributeName == "HttpMethodTypeAttribute")
                    {
                        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string methodType)
                        {
                            httpMethod = methodType;
                        }
                    }
                }



                if (string.IsNullOrEmpty(routeTemplate))
                    routeTemplate = method.Name;

                return (httpMethod, routeTemplate!, IgnoreInClient,IgnoreInServer);
            }

            /// <summary>
            /// If no HTTP method attribute is found, try to determine the HTTP method from the method name.
            /// </summary>        
            internal static string DetermineHttpMethodFromName(string methodName)
            {
                bool StartsWith(params string[] values) =>
                    values.Any(value => methodName.StartsWith(value, StringComparison.OrdinalIgnoreCase));

                if (StartsWith("Get", "List", "Find", "Search")) return "GET";
                if (StartsWith("Query")) return "QUERY";
                if (StartsWith("Create", "Add", "Post")) return "POST";
                if (StartsWith("Update", "Modify", "Put")) return "PUT";
                if (StartsWith("Delete", "Remove")) return "DELETE";
                if (StartsWith("Patch")) return "PATCH";
                if (StartsWith("Connect")) return "CONNECT";
                // Default to POST if unsure
                return "POST";
            }



        }

        static string GetMethodKey(
            string name,
            int genericArity,
            IEnumerable<(string type, RefKind refKind)> parameters)
        {
            var generic = genericArity > 0 ? $"`{genericArity}" : "";

            var paramList = parameters.Select(p =>
                p.refKind switch
                {
                    RefKind.Ref => $"ref {p.type}",
                    RefKind.Out => $"out {p.type}",
                    RefKind.In => $"in {p.type}",
                    _ => p.type
                });

            return $"{name}{generic}({string.Join(",", paramList)})";
        }

        public const string ViaductErrorCodePrefix = "VIADUCT";

        extension(ViaductErrors code)
        {
            public string GetCode() => $"{ViaductErrorCodePrefix}{(int)code:000}";

            public DiagnosticInfo CreateError(LocationInfo Location, string MessageFormat, params object[] MessageArgs)
                => Diagnostics.Error(code.GetCode(), MessageFormat).CreateDiagnosticInfo(Location, MessageArgs);

            public DiagnosticInfo CreateWarning(LocationInfo Location, string MessageFormat, params object[] MessageArgs)
                => Diagnostics.Warning(code.GetCode(), MessageFormat).CreateDiagnosticInfo(Location, MessageArgs);
        }

        internal static string? CheckRoutePart([StringSyntax("Route")]this string? field)
        {
            if (string.IsNullOrEmpty(field)) return field;
            if (field![0] != '/')
                return "/" + field;
            return field;
        }

        extension(ITypeSymbol type)
        {

            string GetTypeName()
            {
                if (type is IArrayTypeSymbol array)
                    return $"{GetTypeName(array.ElementType)}[]";

                if (type is INamedTypeSymbol named && named.IsGenericType)
                {
                    var name = named.ConstructedFrom.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat);

                    name = name.Replace("global::", "");
                    name = name.Substring(0, name.IndexOf('<'));

                    var args = named.TypeArguments.Select(GetTypeName);

                    return $"{name}<{string.Join(",", args)}>";
                }

                return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                           .Replace("global::", "");
            }

            private ITypeSymbol UnwrapNullable()
            {
                if (type is INamedTypeSymbol
                    {
                        OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                        TypeArguments.Length: 1
                    } named)
                {
                    return named.TypeArguments[0];
                }

                return type;
            }


            internal bool IsTaskLike(out TypeReference? innerType)
            {
                innerType = null;

                if (type is not INamedTypeSymbol named)
                    return false;

                var original = named.IsGenericType ? named.ConstructedFrom : named;

                if (original.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
                    return false;

                if (original.Name is not ("Task" or "ValueTask"))
                    return false;

                if (named.IsGenericType)
                    innerType = new ( named.TypeArguments[0]);

                return true;
            }



            private bool IsWellKnownScalar()
            {
                if (type.SpecialType == SpecialType.System_DateTime) return true;
                if (type is not INamedTypeSymbol
                    {
                        ContainingNamespace:
                        {
                            Name: "System",
                            ContainingNamespace.IsGlobalNamespace: true
                        }
                    } named)
                    return false;

                return named.Name is "Guid" or "DateTimeOffset"
                or "TimeSpan" or "DateOnly" or "TimeOnly" or "Version";
            }


            internal bool IsPrimitive =>
                type.SpecialType
                    is SpecialType.System_Boolean
                    or SpecialType.System_Byte
                    or SpecialType.System_SByte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_Int32
                    or SpecialType.System_UInt32
                    or SpecialType.System_Int64
                    or SpecialType.System_UInt64
                    or SpecialType.System_Single
                    or SpecialType.System_Double
                    or SpecialType.System_Decimal
                    or SpecialType.System_Char
                    or SpecialType.System_String;





            /// <summary>
            /// Basically determines if a parameter type can be used in the url
            /// </summary>        
            public bool CanBindFromString(out bool IsSimpleType)
            {
                type = UnwrapNullable(type);


                static bool ImplementsIParsable(ITypeSymbol type)
                    => type.AllInterfaces.Any(i =>
                        i.Name == "IParsable" &&
                        i.ContainingNamespace.ToDisplayString() == "System");

                static bool HasTypeConverter(ITypeSymbol type) => type.GetAttributes()
                    .Any(a =>
                        a.AttributeClass?.Name == "TypeConverterAttribute" &&
                        a.AttributeClass.ContainingNamespace.ToDisplayString() == "System.ComponentModel");

                IsSimpleType =
                    type.TypeKind == TypeKind.Enum ||
                    type.IsPrimitive ||
                    IsWellKnownScalar(type);

                return
                    IsSimpleType ||
                    HasTypeConverter(type) ||
                    ImplementsIParsable(type);
            }
        }
    }
}
