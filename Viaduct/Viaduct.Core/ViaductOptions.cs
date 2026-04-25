using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Viaduct;

[assembly: InternalsVisibleTo("Viaduct.Tests")]
[assembly: InternalsVisibleTo("Viaduct.Client")]
[assembly: InternalsVisibleTo("Viaduct.Server")]

namespace Viaduct
{
    /// <summary>
    /// Represents the general configuration options for the Viaduct minimal api mappings and client calls. Server and client options inherit from this base class
    /// requirements.
    /// </summary>
    public partial record ViaductOptions
    {





        
    }


}



