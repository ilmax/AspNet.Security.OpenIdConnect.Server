/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Notifications;

namespace Owin.Security.OpenIdConnect.Server {
    /// <summary>
    /// Provides context information used when receiving a refresh token.
    /// </summary>
    public sealed class ReceiveRefreshTokenContext : BaseNotification<OpenIdConnectServerOptions> {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReceiveRefreshTokenContext"/> class
        /// </summary>
        /// <param name="context"></param>
        /// <param name="options"></param>
        /// <param name="request"></param>
        /// <param name="token"></param>
        internal ReceiveRefreshTokenContext(
            IOwinContext context,
            OpenIdConnectServerOptions options,
            OpenIdConnectMessage request,
            string token)
            : base(context, options) {
            Request = request;
            RefreshToken = token;
        }

        /// <summary>
        /// Gets the authorization or token request.
        /// </summary>
        public new OpenIdConnectMessage Request { get; private set; }

        /// <summary>
        /// Gets or sets the authentication ticket.
        /// </summary>
        public AuthenticationTicket AuthenticationTicket { get; set; }

        /// <summary>
        /// Gets or sets the deserializer used to resolve the authentication ticket.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public Func<string, Task<AuthenticationTicket>> Deserializer { get; set; }

        /// <summary>
        /// Gets or sets the data format used to deserialize the authentication ticket.
        /// </summary>
        public ISecureDataFormat<AuthenticationTicket> DataFormat { get; set; }

        /// <summary>
        /// Gets the refresh code used by the client application.
        /// </summary>
        public string RefreshToken { get; private set; }

        /// <summary>
        /// Deserialize and verify the authentication ticket.
        /// Note: the <see cref="AuthenticationTicket"/> property
        /// is automatically set when this method completes.
        /// </summary>
        /// <returns>The authentication ticket.</returns>
        public Task<AuthenticationTicket> DeserializeTicketAsync() {
            return DeserializeTicketAsync(RefreshToken);
        }

        /// <summary>
        /// Deserialize and verify the authentication ticket.
        /// Note: the <see cref="AuthenticationTicket"/> property
        /// is automatically set when this method completes.
        /// </summary>
        /// <param name="ticket">The serialized ticket.</param>
        /// <returns>The authentication ticket.</returns>
        public async Task<AuthenticationTicket> DeserializeTicketAsync(string ticket) {
            return AuthenticationTicket = await Deserializer(ticket);
        }
    }
}
