using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static Viaduct.ViaductFunctions;

namespace Viaduct.Generation
{

    partial class ViaductGeneratorFunctions
    {


        /// <summary>
        /// Generates <see cref="InterfaceMetaData"/> from the given interface type symbol, extracting method information and route templates based on attributes and conventions.
        /// </summary>        
        /// <returns></returns>
        public static TransformResult< InterfaceMetaData> CreateInterfaceMetaData(ITypeSymbol type, Compilation? compilation = null)
        {
            var builder = new InterfaceMetadataBuilder(type, compilation);
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

        class InterfaceMetadataBuilder(ITypeSymbol type, Compilation? compilation = null):TransformResultBuilder<InterfaceMetaData>
        {
            readonly ITypeSymbol type = type;

            /// <summary>
            /// Needed to find a referenced assembly's documentation file; null only where a caller has no
            /// compilation to give, which costs nothing but the documentation of interfaces declared elsewhere.
            /// </summary>
            readonly Compilation? compilation = compilation;
            readonly string? BasePath  = GetBasePath(type).CheckRoutePart();

            bool BasePathHasRouteParameters;

            /// <summary>
            /// The endpoint-name suffix per method name, or null for a method that cannot be given one. Worked
            /// out for the interface as a whole, because a name is only usable if nothing else claims it.
            /// </summary>
            Dictionary<string, string?>? endpointNameSuffixes;

            /// <summary>
            /// What every generated endpoint name on this interface starts with: the interface name without its
            /// leading I, and the type arguments for a closed generic, so two closures never share a name.
            /// </summary>
            readonly string namePrefix = BuildNamePrefix(type);

            static string BuildNamePrefix(ITypeSymbol type)
            {
                var name = type.Name;
                if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
                    name = name.Substring(1);

                if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: > 0 } named)
                    name += "_" + string.Join("_", named.TypeArguments.Select(static t => Sanitize(t.Name)));

                return Sanitize(name);
            }

            /// <summary>
            /// The endpoint name for a method, or null when it cannot be given one it would keep.
            /// </summary>
            string? GenerateEndpointName(IMethodSymbol method)
                => endpointNameSuffixes!.TryGetValue(method.Name, out var suffix) && suffix is not null
                    ? $"{namePrefix}_{Sanitize(suffix)}"
                    : null;

            /// <summary>
            /// Works out what each method on the interface may call itself.
            /// </summary>
            /// <remarks>
            /// "Async" is a convention of the C# method rather than part of the operation's name, so it is
            /// dropped — but only while that leaves the methods distinguishable. An interface offering both
            /// <c>Execute</c> and <c>ExecuteAsync</c> keeps both names in full, because dropping the suffix
            /// would give two endpoints one name, and ASP.NET Core refuses to start with a duplicate.
            /// <para>
            /// Genuine overloads get no name at all: which of them a name refers to would be a guess, and an
            /// operationId that silently moves to the other overload as the interface changes is worse for the
            /// clients generated from it than having none.
            /// </para>
            /// </remarks>
            static Dictionary<string, string?> BuildEndpointNameSuffixes(List<IMethodSymbol> methods)
            {
                var suffixes = new Dictionary<string, string?>(StringComparer.Ordinal);

                foreach (var group in methods.GroupBy(static m => m.Name, StringComparer.Ordinal))
                    suffixes[group.Key] = group.Count() > 1 ? null : StripAsync(group.Key);

                // Two different methods whose names differ only by the suffix just dropped: give both back
                // their full name rather than leaving them to collide.
                var collisions = suffixes
                    .Where(static entry => entry.Value is not null)
                    .GroupBy(static entry => entry.Value!, StringComparer.Ordinal)
                    .Where(static g => g.Count() > 1);

                foreach (var collision in collisions)
                    foreach (var entry in collision)
                        suffixes[entry.Key] = entry.Key;

                return suffixes;

                static string StripAsync(string name)
                    => name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal)
                        ? name.Substring(0, name.Length - 5)
                        : name;
            }


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
                        && seen.Add(MethodSignatureKey(m)))
                    .ToList();

                // Up front, because what one method may call itself depends on what the others are called.
                endpointNameSuffixes = BuildEndpointNameSuffixes(methods);

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
                    // ToDisplayString() spells the global namespace as "<global namespace>", which is not a
                    // name any generated `using` can carry. An interface declared there has no namespace at all.
                    var ns = type.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : type.ContainingNamespace.ToDisplayString();

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

                var documentation = GetEndpointDocumentation(method);

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
                {
                    IgnoreForClientGeneration = attInfo.Value.IgnoreInClient,
                    IgnoreForServerGeneration = attInfo.Value.IgnoreInServer,
                    Summary = documentation.Summary,
                    Description = documentation.Description,
                    EndpointName = documentation.Name ?? GenerateEndpointName(method),
                    DocumentationId = documentation.Summary is null || documentation.Description is null
                        ? method.GetDocumentationCommentId()
                        : null,
                };

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

        /// <summary>
        /// A method's endpoint documentation: what becomes summary, description and operationId in the
        /// OpenAPI document.
        /// </summary>
        /// <remarks>
        /// Attributes win over XML comments, so an endpoint can be worded differently from the method when the
        /// two audiences need different words.
        /// <para>
        /// XML comments are only there to be read when the compiler can see them: source in this compilation,
        /// or a referenced assembly shipped with its documentation file. Nothing breaks when they are missing —
        /// the endpoint simply has no summary, exactly as before this existed.
        /// </para>
        /// </remarks>
        static (string? Summary, string? Description, string? Name) GetEndpointDocumentation(IMethodSymbol method)
        {
            string? summary = null, description = null, name = null;

            foreach (var attribute in method.GetAttributes())
            {
                var attributeName = attribute.AttributeClass?.Name;
                if (attributeName is null
                    || attribute.ConstructorArguments.Length == 0
                    || attribute.ConstructorArguments[0].Value is not string value
                    || value.Length == 0)
                {
                    continue;
                }

                switch (attributeName)
                {
                    case nameof(ViaductSummaryAttribute):
                        summary = value;
                        break;
                    case nameof(ViaductDescriptionAttribute):
                        description = value;
                        break;
                    case nameof(ViaductEndpointNameAttribute):
                    // ASP.NET Core's own attributes, recognised by name so an interface project already using
                    // them needs no second set. Matching the name rather than the type is what keeps
                    // Viaduct.Core free of an ASP.NET reference.
                    case "EndpointNameAttribute":
                        name = value;
                        break;
                    case "EndpointSummaryAttribute":
                        summary ??= value;
                        break;
                    case "EndpointDescriptionAttribute":
                        description ??= value;
                        break;
                }
            }

            if (summary is null || description is null)
            {
                var (xmlSummary, xmlRemarks) = ReadXmlDocumentation(method);
                summary ??= xmlSummary;
                description ??= xmlRemarks;
            }

            return (summary, description, name);
        }

        /// <summary>Reads <c>&lt;summary&gt;</c> and <c>&lt;remarks&gt;</c> out of a method's documentation comment.</summary>
        static (string? Summary, string? Remarks) ReadXmlDocumentation(IMethodSymbol method)
        {
            var xml = method.GetDocumentationCommentXml();

            // Empty for a source symbol unless the project generates a documentation file: without /doc the
            // compiler keeps the comments as plain trivia and never builds the XML. Reading the trivia is what
            // makes this work in an ordinary project, which is nearly all of them.
            if (string.IsNullOrWhiteSpace(xml))
                xml = ReadDocumentationFromSource(method);

            // An interface from a referenced assembly has neither, and its documentation is filled in later
            // from the additional files — see ReferenceDocumentation.
            return xml is { Length: > 0 } ? ParseDocumentation(xml) : (null, null);
        }

        /// <summary>The summary and remarks in a <c>&lt;member&gt;</c> documentation element.</summary>
        internal static (string? Summary, string? Remarks) ParseDocumentation(string xml)
        {
            try
            {
                var root = XElement.Parse(xml);
                return (Text(root.Element("summary")), Text(root.Element("remarks")));
            }
            catch (XmlException)
            {
                // A malformed documentation comment is something the author sees in their own build. It is not
                // a reason to fail code generation.
                return (null, null);
            }

            // Documentation comments keep the line breaks and indentation of the source. Wherever this text is
            // displayed it is a single run of prose, so it is collapsed to one line.
            static string? Text(XElement? element)
            {
                if (element is null)
                    return null;

                var value = string.Join(" ", element.Value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                return value.Length == 0 ? null : value;
            }
        }

        /// <summary>
        /// Rebuilds a method's documentation comment from the source it was declared in.
        /// </summary>
        /// <remarks>
        /// Only reachable for an interface declared in this compilation. One in a referenced assembly has no
        /// syntax here, and its documentation is only available when that assembly ships its XML file.
        /// </remarks>
        static string? ReadDocumentationFromSource(IMethodSymbol method)
        {
            var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax is null)
                return null;

            var lines = syntax.GetLeadingTrivia().ToFullString()
                .Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("///", StringComparison.Ordinal))
                .Select(static line => line.Substring(3))
                .ToList();

            if (lines.Count == 0)
                return null;

            // The comment's elements are siblings with no root of their own — the same shape
            // GetDocumentationCommentXml wraps in <member>.
            return "<member>" + string.Join("\n", lines) + "</member>";
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
