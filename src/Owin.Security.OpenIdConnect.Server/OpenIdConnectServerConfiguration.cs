﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System.Collections.Generic;
using System.ComponentModel;

namespace Owin.Security.OpenIdConnect.Server {
    /// <summary>
    /// Holds various properties allowing to configure the OpenID Connect server middleware.
    /// </summary>
    public class OpenIdConnectServerConfiguration {
        internal OpenIdConnectServerConfiguration(IAppBuilder builder) {
            Builder = builder;
            Options = new OpenIdConnectServerOptions();
            Properties = new Dictionary<string, object>();
        }

        /// <summary>
        /// Gets the ASP.NET application builder used by this instance.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IAppBuilder Builder { get; private set; }

        /// <summary>
        /// Gets the options used by the OpenID Connect server middleware.
        /// </summary>
        public OpenIdConnectServerOptions Options { get; private set; }

        /// <summary>
        /// Sets the <see cref="OpenIdConnectServerProvider"/> used to control the authorization process.
        /// Implementing <see cref="OpenIdConnectServerProvider.ValidateClientRedirectUri"/> and
        /// <see cref="OpenIdConnectServerProvider.ValidateClientAuthentication"/> is recommended.
        /// </summary>
        public OpenIdConnectServerProvider Provider {
            set { Options.Provider = value; }
        }

        /// <summary>
        /// Gets the properties dictionary associated with this instance.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IDictionary<string, object> Properties { get; private set; }
    }
}
