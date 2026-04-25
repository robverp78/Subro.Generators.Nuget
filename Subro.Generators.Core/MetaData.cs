using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text;

namespace Subro.Generators
{
    public record TypeReference(string TypeName, string FullName) 
    {
        internal TypeReference(ITypeSymbol type) : this(type.Name, type.ToDisplayString())
        {
        }
    }

    internal record NameAndType(string Name, TypeReference Type);

    internal record PropertyInfo(string Name, TypeReference Type, bool HasGetter, bool HasSetter): NameAndType(Name, Type)
    {
        public PropertyInfo(IPropertySymbol property):this(property.Name, new(property.Type), property.GetMethod is not null, property.SetMethod is not null)
        {
            
        }
    }

    internal static partial class GeneratorFunctions
    {   
        const int defaultTabs = 2;
        extension(StringBuilder sb)
        {
            public StringBuilder AppendTypeAndName(NameAndType nameAndType) => sb.Append(nameAndType.Type.FullName).Append(' ').Append(nameAndType.Name);

         
            /// <summary>
            /// Adds a  summary section. Make sure to only add a one liner when using this function
            /// </summary>
            public StringBuilder AppendSummary(string summary, int tabs = defaultTabs) =>
                sb.AppendTabs(tabs).AppendLine("/// <summary>").AppendTabs(tabs).Append("/// ").AppendLine(summary).AppendTabs(tabs).AppendLine("/// </summary>");

            public StringBuilder AppendTabs(int count = defaultTabs) => sb.Append('\t', count);
        }

        extension(IPropertySymbol prop)
        {
            public NameAndType CreateInfo() => new(prop.Name, new(prop.Type));
        }

        extension(ISymbol symbol)
        {
            public string GetNamespace() =>
                symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : string.Empty;
        }
    }

    
}
