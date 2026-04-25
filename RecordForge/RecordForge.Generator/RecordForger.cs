    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;
    using Subro.RecordForge;
using System;
    using System.Collections.Immutable;
    using System.Data;
    using System.Dynamic;
    using System.Linq;
using System.Runtime.CompilerServices;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
using System.Xml.Linq;


[assembly:InternalsVisibleTo("Subro.Generator.Tests")]
[assembly: InternalsVisibleTo("RecordForgeWeb")]


namespace Subro.Generators
{


    [Generator]
    public partial class RecordForger : IIncrementalGenerator
    {
        const string attributeNamespace = "Subro.RecordForge";


        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            RegisterAttribute(context, nameof(GenerateRecordAttribute),TransformInterfaceAttribute );
            RegisterAttribute(context, nameof(GenerateRecordFromInterfaceAttribute), TransformAssemblyAttribute);
        }

        static void RegisterAttribute(IncrementalGeneratorInitializationContext context , string attributeName,
            Func<GeneratorAttributeSyntaxContext , CancellationToken, IEnumerable<TransformResult<CreationInfo>>> Transform
            )
        {            
            var interfaceDeclarations = context
                .ValuesForAttributeWithMetadataName(
                $"{attributeNamespace}.{attributeName}",
                static (node, _) => true, //attribute has an interface target flag, so no extra checks needed
                (context, token) => Transform(context, token));

            context.RegisterSourceOutput(interfaceDeclarations, createRecord);
        }


        static void createRecord(SourceProductionContext spc, CreationInfo info)
        {
            var code = BuildCode(info);
            spc.AddSource($"{info.RecordNameSpace}.{info.RecordName}.g.cs", code);
        }

        internal static string BuildCode(CreationInfo info)
        {
            var sb = new StringBuilder();

            // usings
            sb.Append(@"
using System;
using System.Threading;
using System.Threading.Tasks;
");
            if (!string.IsNullOrEmpty(info.InterfaceNameSpace))
                sb.Append("using ").Append(info.InterfaceNameSpace).Append(";\n"); //in case another namespace is used

            // namespace
            bool hasNamespace = !string.IsNullOrEmpty(info.RecordNameSpace);
            sb.Append('\n');
            if (hasNamespace)
                sb.Append("\nnamespace ").Append(info.RecordNameSpace).Append(@"
{");

            sb.Append(@"
    public ");

            if (info.IsReadOnly)
                sb.Append("readonly ");

            if (info.IsPartial)
                sb.Append("partial ");

            if (info.Settings.AsAbstract)
                sb.Append("abstract ");

            sb.Append(info.TypeInfo.Keywords)
                .Append(' ').Append(info.RecordName);

            var ctors = info.GetConstructors().ToList();



            //Primary constructor
            bool AllowPrimaryCtor = info.TypeInfo.OnlyNeedsConstructor; //for now, do not presume primary ctor for classes and classical structs (maybe make configurable later)
                                                                        //It creates more code than needed, but it lowers the required language version
                                                                        //Records and records structs already support primary ctors, so it is safe to assume they can be used.
            IReadOnlyList<PropertyCreationInfo>? primaryCtor;
            int startCtorIndex = 0;
            if (AllowPrimaryCtor)
            {
                primaryCtor = ctors[startCtorIndex++];

                if (primaryCtor.Count > 0 || ctors.Count > 1)
                {
                    //add parameters for primary constructor
                    sb.AppendCtorParameters(primaryCtor);
                }
            }
            else primaryCtor = null;

            HashSet<string> primaryCtorNames = [.. primaryCtor?.Select(static p => p.Name) ?? []];

            //interface and opening bracket for class
            sb.Append(':').Append(info.InterfaceName).Append(@"
    {");

            //add additional constructors if needed
            for (int ci = startCtorIndex; ci < ctors.Count; ci++)
            {
                var props = ctors[ci];

                if (ci > 0 && props.Count == ctors[ci - 1].Count) continue; //same amount of properties as the previous ctor, do not create 
                sb.Append(@"
        public ").Append(info.RecordName).AppendCtorParameters(props);

                if (primaryCtor is not null)
                {
                    sb.Append(@"
            : this(");
                    sb.Append('(');
                    for (int pi = 0; pi < primaryCtor.Count; pi++)
                    {
                        if (pi > 0) sb.Append(", ");
                        sb.Append(primaryCtor[pi].Name);
                    }
                    sb.Append(')');
                }


                //start ctor body
                sb.Append(@"
        {");
                //assign properties that are not in the primary ctor
                foreach (var prop in props)
                {
                    if (primaryCtorNames.Contains(prop.Name)) continue; //already assigned in primary ctor call
                    sb.Append(@"
            ").Append("this.").Append(prop.Name).Append(" = ").Append(prop.Name).Append(';');
                }
                sb.Append(@"
        }
");
            }

            //add properties
            foreach (var prop in info.Properties)
            {
                bool isInPrimaryCtor = primaryCtorNames.Contains(prop.Name);

                bool createSetter = prop.HasSetter || info.Settings.AlwaysCreateSetters;

                if (info.TypeInfo.OnlyNeedsConstructor && isInPrimaryCtor && 
                    //(!createSetter || prop.HasInit)) //this would be better, but is is init for record, and set for record struct, easier to always create the property
                    !createSetter)
                    continue; //already created as a property via the primary constructor, do not create a property for it

                sb.AppendLine().AppendTabs().Append("public ")
                    .Append(prop.Type.FullName).Append(' ').Append(prop.Name);


                if (createSetter)
                {
                    // get;set or init
                    sb.Append("{get;");
                    if (prop.HasInit || info.IsReadOnly)
                        //setters on readonly types can only be init, so even if the property has a setter, we will make it an init-only setter.
                        //in the (wrong) case where the property should have a setter, but a readonly type is used, an explicit implementation
                        //is added further down.
                        sb.Append("init");
                    else
                        sb.Append("set");
                    sb.Append(";}");
                }
                else
                    sb.Append("{get;}"); 

                if (isInPrimaryCtor)
                    sb.Append(" = ").Append(prop.Name).Append(';');

                if (!prop.HasInit && prop.HasSetter && info.TypeInfo.IsReadOnly)
                {
                    //special circumstance, this really should not happen: the choice is for a readonly type, 
                    //but the property has a setter. Several choices here to have the user fix the construction, but chosen to
                    //silently make this work, by creating an implicit interface property
                    sb.AppendLine().AppendTabs().Append(prop.Type.FullName).Append(' ')
                        .Append(info.InterfaceName).Append('.').Append(prop.Name).Append(" { get => this.").Append(prop.Name)
                        .Append("; set => throw new NotSupportedException(\"This instance is read-only.\"); }");
                }
            }


            //end of class and namespace
            sb.Append(@"
    }
");
            if (hasNamespace) sb.Append("}\n");

            return sb.ToString();
        }
    }

    internal static class RecordForgerFunctions
    {
        public static StringBuilder AppendCtorParameters(this StringBuilder sb, IReadOnlyList<PropertyCreationInfo> props)
        {
            sb.Append('(');
            for (int i = 0; i < props.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.AppendTypeAndName(props[i]);
            }
            sb.Append(')');
            return sb;
        }
    }
}
