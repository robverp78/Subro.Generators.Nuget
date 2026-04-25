using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Viaduct
{

    internal static class ViaductFunctions
    {
        internal const string RouteSyntax = "Route";
        internal static string? CheckRoutePart([StringSyntax(RouteSyntax)] this string? field)
        {
            if (string.IsNullOrEmpty(field)) return null;
            if (field![0] != '/')
                return "/" + field;
            return field;
        }

        //[GeneratedRegex(@"\{[^}]+\}")]private static partial Regex UriParameterCheck();  static bool HasRouteParameters(string url) => UriParameterCheck().IsMatch(url);

        [StringSyntax("Regex")]
        internal const string HasRouteParametersPattern = @"\{[^}]+\}",
            GetRouteParametersPattern = @"\{(?<name>[^}:?]+)[^}]*\}";




        internal static OptionsType GetOptionsInstance<OptionsType>(this Action<OptionsType>? configure, OptionsType Default)
where OptionsType : ViaductOptions
        {
            var def = Default with { }; //always clone to create the snapshot
            configure?.Invoke(def);
            return def;
        }
    }


}
