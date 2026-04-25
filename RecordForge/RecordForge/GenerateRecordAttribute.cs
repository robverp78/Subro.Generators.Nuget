

namespace Subro.RecordForge;

using System;

/// <summary>
/// The kind of type to generate from an interface.
/// </summary>
public enum RecordKind
{
    Record = 0,
    RecordStruct = 1,
    ReadOnlyRecordStruct = 2,
    Struct = 3,
    ReadonlyStruct = 4,
    Class = 5
}

/// <summary>
/// Attribute for generating default record or class implementations from interfaces.
/// </summary>
/// <remarks>
/// Apply directly to an interface to create the implementation in the same assembly as the interface
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class GenerateRecordAttributeBase(RecordKind kind) : Attribute
{
    public const RecordKind DefaultKind = RecordKind.Record;

    /// <summary>
    /// The kind of type to generate.
    /// </summary>
    public RecordKind Kind { get; } = kind;

    /// <summary>
    /// Optional. If not given, the interface name (without "I") is used
    /// </summary>
    public string? RecordName { get; init; }

    /// <summary>
    /// Optional. If not given, the namespace of the interface is used
    /// </summary>
    public string? NameSpace { get; init; }

    public const bool DefaultAsPartial = true;
    /// <summary>
    /// Indicates if the created record should be indicated as partial (default true)
    /// </summary>
    public bool AsPartial { get; init; } = DefaultAsPartial;

    /// <summary>
    /// Indicates if the created record should be created as abstract (default false)
    /// </summary>
    public bool AsAbstract { get; init; } = false;

    /// <summary>
    /// The created record will contain setters for all properties, even if the interface only requires a {get;}
    /// </summary>
    /// <remarks>
    /// If the created record is readonly, the properties become 'init'.
    /// If the property is already an init, it remains an init (it does not become a set).
    /// </remarks>
    public bool AlwaysCreateSetters { get; init; } = false;

    /// <summary>
    /// What kind of constructor(s) to create.
    /// Either <see cref="ConstructorUsage.Automatic"/> (default) or one
    /// of the <see cref="ConstructorUsage"/> values or a combination of those
    /// </summary>
    /// <remarks>
    /// When adding multipe constructors, the primary constructor (if applicable) will be the one
    /// with the least properties 
    /// </remarks>
    public ConstructorUsage ConstructorUsage { get; set; }
}

/// <summary>
/// Attribute for generating default record or class implementations from interfaces.
/// </summary>
/// <remarks>
/// Apply directly to an interface to create the implementation in the same assembly as the interface
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public class GenerateRecordAttribute(RecordKind kind) : GenerateRecordAttributeBase(kind) 
{
    /// <summary>
    /// Generate a default record implementation for the given interface. 
    /// </summary>
    public GenerateRecordAttribute():this(DefaultKind)
    {
        
    }
}

/// <summary>
/// Generate a record or other type for the specified interface type. Use this on assembly level to either keep the implementation disconnected, or in another assembly
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class GenerateRecordFromInterfaceAttribute(Type[] interfaceTypes,RecordKind kind) : GenerateRecordAttributeBase(kind) 
{

    /// <summary>
    /// Generate a record or other type for the specified interface type. Use this on assembly level to either keep the implementation disconnected, or in another assembly
    /// </summary>
    /// <param name="interfaceType">The type of the interface to create the record for</param>
    public GenerateRecordFromInterfaceAttribute(Type[] interfaceTypes) : this(interfaceTypes, DefaultKind)
    {

    }

    /// <summary>
    /// Generate a record or other type for the specified interface type. Use this on assembly level to either keep the implementation disconnected, or in another assembly
    /// </summary>
    /// <param name="interfaceType">The type of the interface to create the record for</param>
    public GenerateRecordFromInterfaceAttribute(Type interfaceType):this([interfaceType], DefaultKind)
    {
        
    }

    /// <summary>
    /// Generate a record or other type for the specified interface type. Use this on assembly level to either keep the implementation disconnected, or in another assembly
    /// </summary>
    /// <param name="interfaceType">The type of the interface to create the record for</param>
    public GenerateRecordFromInterfaceAttribute(Type interfaceType, RecordKind kind) : this([interfaceType], kind)
    {

    }

    public Type[] InterfaceTypes { get; } = interfaceTypes;
}

/// <summary>
/// The type of constructor, or constructors to be added to the generated type.
/// </summary>
[Flags]
public enum ConstructorUsage
{
    /// <summary>
    /// For records:  Readonly and init properties are put in the ctor, 
    /// For readonly records and structs: all properties are put in the ctor (inits are fine, setter throws readonly exception)
    /// For classes and classic structs: parameterless constructor
    ///  other properties are put as get set properties
    /// </summary>
    Automatic,
    /// <summary>
    /// Parameterless constructor
    /// </summary>
    Empty = 1,
    /// <summary>
    /// Only the readonly properties (set and init properties are added as properties only)
    /// </summary>
    ReadonlyProperties = 2,
    /// <summary>
    /// Readonly and init properties (set properties are added as properties only)
    /// </summary>
    ReadonlyAndInitProperties = 4,
    /// <summary>
    /// All properties go in the constructor
    /// </summary>
    AllProperties = 8,

}

