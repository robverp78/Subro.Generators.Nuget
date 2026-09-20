namespace Subro.RecordForge;

using System;

/// <summary>
/// Declares that the annotated partial type implements the given interface(s), and has the generator
/// add every interface property the type does not already have.
/// </summary>
/// <remarks>
/// <para>
/// This is the inverse of <see cref="GenerateRecordAttribute"/>: where that one turns an interface into a
/// brand new type, this one fills in an existing type. The type is left in charge — anything it already
/// declares itself, or inherits as a public/protected member from a base class, is never touched.
/// </para>
/// <para>
/// The type (and every type it is nested in) must be declared <c>partial</c>. The interface is added to the
/// base list of the generated part, so it does not have to be written at the declaration site;
/// set <see cref="DeclareInterface"/> to <see langword="false"/> to suppress that.
/// </para>
/// <para>
/// Only properties are generated. Methods, events and indexers are deliberately left alone, so that a
/// missing implementation stays a compile error instead of becoming a runtime surprise.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Implements(typeof(IAudited))]
/// public partial class Session
/// {
///     // CreatedAt and CreatedBy are generated; UpdatedAt is not, because it is declared here.
///     public DateTimeOffset UpdatedAt { get => field; set { field = value; MarkDirty(); } }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public class ImplementsAttribute(params Type[] interfaceTypes) : Attribute
{
    /// <summary>
    /// The interfaces to implement. Must be interface types, and closed (an unbound generic such as
    /// <c>typeof(IFoo&lt;&gt;)</c> cannot be implemented).
    /// </summary>
    public Type[] InterfaceTypes { get; } = interfaceTypes;

    /// <summary>
    /// The generated properties get setters, even where the interface only requires a <c>{ get; }</c>.
    /// </summary>
    /// <remarks>
    /// On a readonly struct the setter becomes an <c>init</c>. A property the interface already declares as
    /// <c>init</c> stays an <c>init</c> (it does not become a <c>set</c>).
    /// </remarks>
    public bool AlwaysCreateSetters { get; init; }

    public const bool DefaultDeclareInterface = true;

    /// <summary>
    /// The generated part adds the interface to the type's base list (default <see langword="true"/>).
    /// Set to <see langword="false"/> when the type already names the interface itself, or when the members
    /// are wanted without the type formally implementing the interface.
    /// </summary>
    public bool DeclareInterface { get; init; } = DefaultDeclareInterface;
}
