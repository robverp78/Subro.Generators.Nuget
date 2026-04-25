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

        public readonly string FullName = $"{Namespace}.{Name}";

        //public readonly bool BasePathHasRouteParameters = BasePath is not null && ViaductGeneratorFunctions.HasRouteParameters(BasePath);
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
