using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Viaduct
{
    public partial record ViaductOptions
    {
        /// <summary>
        /// This overrides whichever basepath is set on the interface, and can be used to set a global base name for all interfaces. 
        /// If not set, the base name specified on the interface will be used if that is set (<see cref="BasePathAttribute"/>)
        /// </summary>
        [StringSyntax(ViaductFunctions.RouteSyntax)]
        public string? InterfaceBasePath
        {
            get;
            set => field = value.CheckRoutePart();
        }

        /// <summary>
        /// If <see cref="InterfaceBasePath"/> is not set (and the interface does not contain a <see cref="BasePathAttribute"/>), setting
        /// this value to <c>true</c> will use the interface name as a base name (without the leading 'I')
        /// For example IWeatherService will become (BasePath/)WeatherService/MethodName
        /// </summary>
        public bool UseInterfaceNameForPath { get; set; }

        /// <summary>
        /// An 'extra' base name, this is prepended to all paths
        /// </summary>
        [StringSyntax(ViaductFunctions.RouteSyntax)]
        public string? BasePath
        {
            get;
            set => field = value.CheckRoutePart();
        }

        /// <summary>
        /// Determines whether the client should include an authorization header in requests by default and if servers should add an authorization requirement to generated routes.
        /// </summary>
        public bool RequiresAuthorization { get; set; }

        /// <summary>
        /// Determines whether (string bindable) method parameters should be included in the path as route parameters by default, or if they should be sent as query parameters (for GET) or in the body (for POST/PUT).        /// 
        /// </summary>
        /// <remarks>
        /// If a method has a pre defined route template with parameters, this setting is ignored.
        /// 
        /// Only parameters that can be bound from strings will be used as route parameters
        /// </remarks>
        public bool UseRoutePathParameters { get; set; } = true;


        /// <summary>
        /// Determines if route parameters (both automatic and created depending on <see cref="UseRoutePathParameters"/>)) are forced to lower case.
        /// It also influences how string parameters are compared when determining if they match a predefined route in the client.
        /// Default value is <c>true</c>
        /// </summary>
        public bool ForceLowerCaseRouteParameters { get; set; } = true;
    }
}
