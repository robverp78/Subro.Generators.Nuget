using System;
using System.Collections.Generic;
using System.Text;

namespace Viaduct
{
    /// <summary>
    /// Base or generic exception for all exceptions thrown by Viaduct.
    /// </summary>
    public class ViaductException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the ViaductException class with a specified error message.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public ViaductException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the ViaductException class with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="baseException">The exception that is the cause of the current exception.</param>
        public ViaductException(string message, Exception baseException) : base(message, baseException) { }
    }

    /// <summary>
    /// Exception thrown when a method expected to be intercepted by the Viaduct generator is not intercepted in runtime.
    /// </summary>
    /// <remarks>Occurs when code generation or interception is disabled or fails; verify that generators and
    /// interceptors are allowed to run and that they completed without errors.</remarks>
    public class ViaductMethodNotInterceptedException()
        : ViaductException("This method should have been intercepted by the Viaduct generator. Please make sure generators and interceptors are allowed to run and that they finished without errors.")
    {

    }

    /// <summary>
    /// Represents an error raised when a named parameter required by a route cannot be resolved.
    /// </summary>
    /// <remarks>The exception message includes the parameter name and the route path.</remarks>
    /// <param name="parameterName">Name of the route parameter that could not be resolved.</param>
    /// <param name="path">Route template or path that expects the parameter.</param>
    public class MissingRouteParameterException(string parameterName, string path)
        : ViaductException($"Could not resolve parameter '{parameterName}', which is expected in the route '{path}'")
    {
    }
}
