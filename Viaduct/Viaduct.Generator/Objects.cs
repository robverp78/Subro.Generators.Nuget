using Microsoft.CodeAnalysis;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using static Viaduct.Generation.ViaductGenerator;

namespace Viaduct.Generation
{
    public enum ViaductErrors
    {
        //general errors
        CouldNotCreateMetaData=1,
        CouldNotCreateInterfaceData = 2,
        CouldNotCreateMethodData,

        //critical problems
        MoreThanOneBodyParameter = 200,
        MoreThanOneCancellationTokenArgument,

        //warnings
        NotAnAsyncMethod = 300,
        ParameterIsNotSafeForRoute ,
        RouteParameterIsMissingInMethodSignature ,
        NoMethodsFound
    }

   

    public record InterfaceMetaData(
        string Name, string Namespace, [StringSyntax("Route")] string? BasePath,
        ImmutableArray<MethodCreationInfo> Methods)
    {

        public override string ToString() => Name;

        /// <summary>Empty <see cref="Namespace"/> means the interface is in the global namespace.</summary>
        public readonly string FullName = string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";

        /// <summary>True when this metadata describes a closed (constructed) generic interface, e.g. <c>ICrudGuid&lt;AddressRecord&gt;</c>.</summary>
        public bool IsGeneric { get; init; }

        /// <summary>
        /// Fully-qualified type reference including type arguments (and the <c>global::</c> prefix for generics),
        /// e.g. <c>global::MyApp.ICrudGuid&lt;global::MyApp.AddressRecord&gt;</c>. Used wherever the interface type is
        /// referenced in generated code (implemented-interface clause, <c>[FromServices]</c>, DI). Defaults to <see cref="FullName"/>.
        /// </summary>
        public string FullyQualifiedName { get => field ?? FullName; init; }

        /// <summary>
        /// Collision-free identifier safe for use in generated class / file names. For closed generics this encodes the type
        /// arguments (e.g. <c>ICrudGuid_AddressRecord_ab12cd</c>) so distinct closures never clash. Defaults to <see cref="Name"/>.
        /// </summary>
        public string Identifier { get => field ?? Name; init; }

        /// <summary>
        /// Route discriminator derived from the type arguments of a closed generic (e.g. <c>AddressRecord</c>), or <c>null</c>
        /// for non-generic interfaces. Used to make endpoint/client routes unique per closure.
        /// </summary>
        public string? TypeArgSegment { get; init; }
    }


    public record CreationInfo(MethodCallerInfo? Method, TypeReference[]? AotTypes) 
    {
        
    }



    public record MethodCallerInfo(string Name, LocationInfo Location,string Interceptor, bool IsServerCall, InterfaceMetaData InterfaceMetaData) 
    {
        public override string ToString() => $"{Name} <{InterfaceMetaData}>";        
    }




    public record MethodCreationInfo(
        string Name,
        string RouteTemplate,
        string HttpMethod,
        ParameterCreationInfo[] Parameters,        
        ParameterCreationInfo? Body,
        ParameterCreationInfo? CancellationToken,
        TypeReference ReturnType,      
        bool IsAsync, bool ReturnsValue,
        TypeReference? InnerReturnType) 
    {

        public bool IgnoreForClientGeneration { get; init; } = false;
        public bool IgnoreForServerGeneration { get; init; } = false;

        /// <summary>
        /// One-line summary for the endpoint, from <c>[ViaductSummary]</c> or the method's XML
        /// <c>&lt;summary&gt;</c> comment. Null when it has neither.
        /// </summary>
        public string? Summary { get; init; }

        /// <summary>The longer description, from <c>[ViaductDescription]</c> or an XML <c>&lt;remarks&gt;</c> comment.</summary>
        public string? Description { get; init; }

        /// <summary>
        /// The endpoint name, which becomes the OpenAPI <c>operationId</c>. Null for an overload, where any
        /// generated name would be a guess at which of them is meant.
        /// </summary>
        public string? EndpointName { get; init; }

        /// <summary>
        /// The method's documentation comment id, e.g. <c>M:MyApp.IUserService.GetUserAsync(System.Int32)</c>.
        /// How a summary is found for an interface that lives in a referenced assembly, whose XML comments
        /// reach the generator as an additional file rather than as syntax.
        /// </summary>
        public string? DocumentationId { get; init; }

        public IEnumerable<ParameterCreationInfo> GetQueryParameters() => Parameters.Where(static p => p.QueryParameter);
    }


    public record ParameterCreationInfo(
    string Name,
    TypeReference Type,
    int Index,
    string? PredefinedTemplateId,
    bool QueryParameter,
    bool IsSimpleType,
    bool IsOptional) 
    {

    }


}
