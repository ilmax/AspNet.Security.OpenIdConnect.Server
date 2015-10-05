/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Notifications;

namespace Owin.Security.OpenIdConnect.Server {
    /// <summary>
    /// Provides context information used when processing an OpenIdConnect token request.
    /// </summary>
    public sealed class TokenEndpointContext : BaseNotification<OpenIdConnectServerOptions> {
        /// <summary>
        /// Initializes a new instance of the <see cref="TokenEndpointContext"/> class
        /// </summary>
        /// <param name="context"></param>
        /// <param name="options"></param>
        /// <param name="request"></param>
        /// <param name="ticket"></param>
        internal TokenEndpointContext(
            IOwinContext context,
            OpenIdConnectServerOptions options,
            OpenIdConnectMessage request,
            AuthenticationTicket ticket)
            : base(context, options) {
            Request = request;
            Ticket = ticket;
        }

        /// <summary>
        /// Gets or sets the authentication ticket.
        /// </summary>
        public AuthenticationTicket Ticket { get; set; }

        /// <summary>
        /// Gets information about the token endpoint request. 
        /// </summary>
        public new OpenIdConnectMessage Request { get; set; }
    }
}
