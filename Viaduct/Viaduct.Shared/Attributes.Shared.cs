using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Viaduct
{
    /// <summary>
    /// Base class for Viaduct attributes. Has no function of itself.
    /// </summary>
    public abstract class ViaductAttributeBase: Attribute
    {
    }


    /// <summary>    
    /// Can be applied at interface level to set base path
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public class BasePathAttribute([StringSyntax(ViaductFunctions.RouteSyntax)] string path) : ViaductAttributeBase
    {
        /// <summary>
        /// Gets the route template path.
        /// </summary>
        [StringSyntax(ViaductFunctions.RouteSyntax)] public string Path { get; } = path;
    }



    /// <summary>    
    /// Can be applied at method level for custom routes. NB, default attributes such as HttpGet, HttpPost, etc. can also be used.
    /// </summary>    
    [AttributeUsage(AttributeTargets.Method)]
    public class CustomRouteAttribute([StringSyntax(ViaductFunctions.RouteSyntax)] string path) : ViaductAttributeBase
    {
        /// <summary>
        /// Gets the route template path.
        /// </summary>
        public string Path { get; } = path;
    }

    /// <summary>
    /// The normal attributes System.Net.Http Attributes such as [HttpGet], [HttpPost] etc. can also be used, but alternatively this attribute can be used in service mappings.
    /// </summary>    
    [AttributeUsage(AttributeTargets.Method)]
    public class HttpMethodTypeAttribute(string Type) : ViaductAttributeBase
    {
        /// <summary>
        /// The http method type (GET/POST/etc)
        /// </summary>
        public string Type { get; } = Type;
    }

    /// <summary>
    /// Tells the Viaduct generator to ignore this method (for both client and server side)
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ViaductIgnoreAttribute : ViaductAttributeBase
    {

    }

    /// <summary>
    /// Tells the Viaduct generator to ignore this method for server endpoint generation
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ViaductIgnoreForServerAttribute : ViaductAttributeBase
    {

    }

}
