using System;
using System.Collections.Generic;
using System.Text;

namespace Viaduct
{
    public class ViaductException : Exception
    {
        public ViaductException(string message) : base(message) { }

        public ViaductException(string message, Exception baseException) : base(message, baseException) { }
    }

    public class ViaductMethodNotInterceptedException()
        : ViaductException("This method should have been intercepted by the Viaduct generator. Please make sure generators and interceptors are allowed to run and that they finished without errors.")
    {

    }

    public class MissingRouteParameterException(string parameterName, string path)
        : ViaductException($"Could not resolve parameter '{parameterName}', which is expected in the route '{path}'")
    {
    }
}
