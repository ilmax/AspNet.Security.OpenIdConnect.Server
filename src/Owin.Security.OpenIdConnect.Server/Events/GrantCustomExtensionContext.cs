/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin;

namespace Owin.Security.OpenIdConnect.Server {
    /// <summary>
    /// Provides context information used when handling OpenIdConnect extension grant types.
    /// </summary>
    public sealed class GrantCustomExtensionContext : BaseValidatingTicketContext<OpenIdConnectServerOptions> {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrantCustomExtensionContext"/> class
        /// </summary>
        /// <param name="context"></param>
        /// <param name="options"></param>
        /// <param name="request"></param>
        internal GrantCustomExtensionContext(
            IOwinContext context,
            OpenIdConnectServerOptions options,
            OpenIdConnectMessage request)
            : base(context, options, null) {
            Request = request;
        }

        /// <summary>
        /// Gets the client_id parameter.
        /// </summary>
        public string ClientId {
            get { return Request.ClientId; }
        }

        /// <summary>
        /// Gets the grant_type parameter.
        /// </summary>
        public string GrantType {
            get { return Request.GrantType; }
        }

        /// <summary>
        /// Gets the token request.
        /// </summary>
        public new OpenIdConnectMessage Request { get; private set; }
    }
}
