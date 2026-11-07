using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.RecordForge;
using System;
using System.Collections.Generic;
using System.Text;

namespace Subro.Generators
{

    partial class RecordForger
    {


        /// <summary>
        /// All available options of <see cref="RecordKind"/>, expressed through <see cref="GenerateTypeInfo"/>. 
        /// This is used to convert from the enum value to the needed information for generation
        /// </summary>
        internal static readonly GenerateTypeInfo[] generateTypeInfos =
           [
                new( (int)RecordKind.Record, "record", true),
                new( (int)RecordKind.RecordStruct, "record struct", true),
                new((int)RecordKind.ReadOnlyRecordStruct, "record struct", true, true ),
                new( (int)RecordKind.Struct, "struct", false),
                new((int)RecordKind.ReadonlyStruct, "struct", false,true),
                new((int)RecordKind.Class, "class", false),
           ];


        internal static GenerateTypeInfo GetGenerateTypeInfo(AttributeData attributeData, int Index)
        {
            var kind  = attributeData.ConstructorArguments.Length > Index
                    && attributeData.ConstructorArguments[Index].Value is int kindValue
                        ? kindValue
                        : (int)GenerateRecordAttributeBase.DefaultKind;
    
            foreach(var info in generateTypeInfos)
                if (info.KindValue == kind)
                    return info;
            return new(); //should not happen, exception path, so not optimized for performance
        }

        internal static DiagnosticInfoProvider CreateInvalidKindDiagnostic(AttributeData attributeData)
            => Diagnostics.Error("AIRINVKIND", "Invalid value for " + nameof(RecordKind))
                    .CreateDiagnosticInfo(LocationInfo.From(attributeData));
    }

    /// <summary>
    /// Information on what a <see cref="RecordKind"/> entails, and how to convert it
    /// </summary>
    internal readonly record struct GenerateTypeInfo(int KindValue, string Keywords, bool OnlyNeedsConstructor, bool IsReadOnly = false)
    {
        public bool IsNull => Keywords is null;
    }

    readonly record struct CreationSettings()
    {
        public bool AsPartial { get; init; } = GenerateRecordAttribute.DefaultAsPartial;

        public bool AsAbstract { get; init; } = false;
        public bool AlwaysCreateSetters { get; init; }

        public CtorUsage ConstructorUsage { get; init; }

        public bool Metadata { get; init; } = GenerateRecordAttribute.DefaultMetadata;
    }

    /// <summary>
    /// What the consuming compilation allows the generated code to use: <see cref="RecordField"/> (any version of the
    /// runtime that has it), and <c>IForgedRecord&lt;TSelf&gt;</c> (the runtime's net8.0 build, with C# 11 or later for its
    /// static abstract members).
    /// </summary>
    internal readonly record struct ForgeCapabilities(bool Fields, bool ForgedInterface)
    {
        public static ForgeCapabilities None => default;

        public static ForgeCapabilities From(Compilation compilation) => new(
            compilation.GetTypeByMetadataName("Subro.RecordForge.RecordField") is not null,
            compilation.GetTypeByMetadataName("Subro.RecordForge.IForgedRecord`1") is not null
                && compilation is CSharpCompilation { LanguageVersion: >= LanguageVersion.CSharp11 });
    }

    /// <summary>The attributes on an interface, as code creating them, for <see cref="RecordField.InterfaceAttributes"/>.</summary>
    internal sealed record InterfaceAttributeInfo(string Interface, EquatableArray<string> Attributes);

    [Flags]
    enum CtorUsage
    {
        Automatic = 0,
        Empty = ConstructorUsage.Empty,
        ReadonlyProperties = ConstructorUsage.ReadonlyProperties,
        ReadonlyAndInitProperties = ConstructorUsage.ReadonlyAndInitProperties,
        AllProperties = ConstructorUsage.AllProperties,
    }

    record CreationInfo(
        GenerateTypeInfo TypeInfo,
        string InterfaceName,
        string InterfaceNameSpace,
        string RecordName,
        string RecordNameSpace,
        EquatableArray<PropertyCreationInfo> Properties,
        CreationSettings Settings)
    {
        /// <summary>The interface, fully qualified, for code that does not rely on the usings.</summary>
        public string QualifiedInterface { get; init; } = InterfaceName;

        /// <summary>The record generated for a base interface that this one derives from, fully qualified; null for none.</summary>
        public string? BaseRecord { get; init; }

        /// <summary>The parameters of <see cref="BaseRecord"/>'s primary constructor, in order: this record's come first.</summary>
        public EquatableArray<string> BasePrimaryConstructor { get; init; } = GeneratorFunctions.EmptyStrings;

        /// <summary>
        /// How the metadata members that name the interface are declared: <c>public</c>, or <c>internal</c> for an interface
        /// that is not public, which a public member cannot expose.
        /// </summary>
        public string InterfaceAccess { get; init; } = "public";

        /// <summary>Whether <see cref="BaseRecord"/> describes itself, so this record's static members hide its.</summary>
        public bool BaseHasMetadata { get; init; }

        /// <summary>The attributes of each interface that declares a property, for the metadata.</summary>
        public EquatableArray<InterfaceAttributeInfo> InterfaceAttributes { get; init; } = new(ImmutableArray<InterfaceAttributeInfo>.Empty);

        /*
         * In loving memory of Smokey. 
         * Smokes, you were such an awesome kitty cat. You were my buddy.
         * I miss your presence and I miss you jumping on my desk while I was coding. 
         * I miss it even more how you crawled on my chest to get your cuddles.
         * When I was working on the last bits of this generator, you were too sick to still do that,
         * but we never imagined those would be the last days we would have you in our lives.
         */


        public IEnumerable<CtorUsage> GetConstructorTypes()
        {
            var ctors = Settings.ConstructorUsage;
            if (ctors == CtorUsage.Automatic)
            {
                //Default value is automatic, behavior should correspond with  ConstructorUsage.Automatic description
                if (TypeInfo.IsReadOnly)
                    yield return CtorUsage.AllProperties;
                else if (TypeInfo.OnlyNeedsConstructor)
                    yield return CtorUsage.ReadonlyProperties;
                else
                    yield return CtorUsage.Empty;
            }
            else if ((ctors & (ctors - 1)) == 0) // single flag, common case
                yield return ctors;
            else
            {
                //multiple flags — check each one
                if (ctors.HasFlag(CtorUsage.Empty)) yield return CtorUsage.Empty;
                if (ctors.HasFlag(CtorUsage.ReadonlyProperties)) yield return CtorUsage.ReadonlyProperties;
                if (ctors.HasFlag(CtorUsage.ReadonlyAndInitProperties)) yield return CtorUsage.ReadonlyAndInitProperties; 
                if (ctors.HasFlag(CtorUsage.AllProperties)) yield return CtorUsage.AllProperties;
            }
        }

        /// <summary>
        /// The parameters of each constructor. A record deriving from <see cref="BaseRecord"/> starts every one with the base
        /// record's primary constructor's, which it has to pass on; the properties the base record has are not its own.
        /// </summary>
        public IEnumerable<IReadOnlyList<PropertyCreationInfo>> GetConstructors()
        {
            var basePrimary = BasePrimaryConstructor.Select(name => Properties.First(p => p.Name == name)).ToList();
            var own = Properties.Where(static p => !p.FromBase);

            foreach (var ctor in GetConstructorTypes())
                yield return ctor switch
                {
                    CtorUsage.Empty => basePrimary,
                    CtorUsage.ReadonlyProperties => [.. basePrimary, .. own.Where(static p => !p.HasSetter)],
                    CtorUsage.ReadonlyAndInitProperties => [.. basePrimary, .. own.Where(static p => p.HasInit || !p.HasSetter)],
                    CtorUsage.AllProperties => [.. basePrimary, .. own],
                    _ => throw new InvalidOperationException("Invalid constructor usage")
                };
        }

        /// <summary>Whether this type's own declaration of <paramref name="property"/> has a setter or init.</summary>
        public bool CreatesSetter(PropertyCreationInfo property) => property.HasSetter || Settings.AlwaysCreateSetters;

        /// <summary>Whether the type has a constructor and so can be made: not abstract.</summary>
        public bool CanCreate => !Settings.AsAbstract;

        public bool IsReadOnly => TypeInfo.IsReadOnly;
        public bool IsPartial => Settings.AsPartial;
    }

    internal record PropertyCreationInfo : PropertyInfo
    {
        public PropertyCreationInfo(IPropertySymbol prop) : base(prop)
        {
            HasInit = prop.SetMethod?.IsInitOnly == true;
        }

        public readonly bool HasInit;

        /// <summary>The type, fully qualified, with its nullable annotation.</summary>
        public string QualifiedType { get; init; } = "";

        /// <summary>The interface that declares the property, fully qualified.</summary>
        public string DeclaringInterface { get; init; } = "";

        public bool AllowsNull { get; init; }

        /// <summary>The attributes on the interface's property, as code creating them.</summary>
        public EquatableArray<string> Attributes { get; init; } = GeneratorFunctions.EmptyStrings;

        /// <summary>Provided by the base record rather than declared by this type.</summary>
        public bool FromBase { get; init; }

        /// <summary>For a property from the base record: whether it can be set after construction (set or init).</summary>
        public bool BaseSettable { get; init; }

    }
}
