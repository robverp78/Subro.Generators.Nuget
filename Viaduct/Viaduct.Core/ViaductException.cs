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
    /// Thrown when the server answered, but with a failure status.
    /// </summary>
    /// <remarks>
    /// The status is what a caller needs to tell one failure from another — 401 is a sign-in problem, 403 a
    /// permission decision, 404 a missing record, 409 a conflict to resolve — and the response body usually
    /// says which. Both are lost if the response is only reported as "the call failed".
    /// <para>
    /// It derives from <see cref="ViaductException"/>, so code that catches that keeps working; catch this
    /// one instead where the status matters. A request that never reached the server, or an answer that
    /// could not be read, is still a plain <see cref="ViaductException"/>: there is no status to report.
    /// </para>
    /// </remarks>
    public class ViaductHttpException : ViaductException
    {
        /// <summary>The HTTP status code the server answered with, e.g. 403.</summary>
        public int StatusCode { get; }

        /// <summary>The reason phrase, when the server sent one.</summary>
        public string? ReasonPhrase { get; }

        /// <summary>
        /// The response body, truncated. Null when there was none or it could not be read. Diagnostic
        /// text: it may be a problem-details document, an HTML error page, or anything else the server
        /// decided to send, so treat it as a message to a human rather than something to parse.
        /// </summary>
        public string? ResponseBody { get; }

        /// <summary>The request that was answered this way.</summary>
        public string? RequestUri { get; }

        /// <summary>The HTTP method of that request.</summary>
        public string? HttpMethod { get; }

        /// <summary>
        /// Initializes the exception from what the response carried.
        /// </summary>
        public ViaductHttpException(
            int statusCode,
            string? reasonPhrase,
            string? responseBody,
            string? httpMethod,
            string? requestUri)
            : base(BuildMessage(statusCode, reasonPhrase, responseBody, httpMethod, requestUri))
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            ResponseBody = responseBody;
            HttpMethod = httpMethod;
            RequestUri = requestUri;
        }

        static string BuildMessage(
            int statusCode,
            string? reasonPhrase,
            string? responseBody,
            string? httpMethod,
            string? requestUri)
        {
            var message = new StringBuilder()
                .Append(httpMethod).Append(' ').Append(requestUri)
                .Append(" answered ").Append(statusCode);

            if (!string.IsNullOrWhiteSpace(reasonPhrase))
                message.Append(" (").Append(reasonPhrase).Append(')');

            if (!string.IsNullOrWhiteSpace(responseBody))
                message.Append(": ").Append(responseBody);

            return message.ToString();
        }
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
