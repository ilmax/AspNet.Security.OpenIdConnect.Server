﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.IdentityModel.Tokens;
using System.Runtime.Caching;
using System.Security.Cryptography;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Notifications;

namespace Owin.Security.OpenIdConnect.Server {
    using System.Collections.Generic;
    using Microsoft.IdentityModel.Tokens;

    /// <summary>
    /// Options class provides information needed to control Authorization Server middleware behavior
    /// </summary>
    public class OpenIdConnectServerOptions : AuthenticationOptions {
        /// <summary>
        /// Creates an instance of authorization server options with default values.
        /// </summary>
        public OpenIdConnectServerOptions()
            : base(OpenIdConnectServerDefaults.AuthenticationType) {
            AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);
            AccessTokenLifetime = TimeSpan.FromHours(1);
            IdentityTokenLifetime = TimeSpan.FromMinutes(20);
            RefreshTokenLifetime =  TimeSpan.FromHours(6);

            UseSlidingExpiration = true;

            AuthorizationEndpointPath = new PathString(OpenIdConnectServerDefaults.AuthorizationEndpointPath);
            ConfigurationEndpointPath = new PathString(OpenIdConnectServerDefaults.ConfigurationEndpointPath);
            CryptographyEndpointPath = new PathString(OpenIdConnectServerDefaults.CryptographyEndpointPath);
            TokenEndpointPath = new PathString(OpenIdConnectServerDefaults.TokenEndpointPath);
            ValidationEndpointPath = new PathString(OpenIdConnectServerDefaults.ValidationEndpointPath);
            LogoutEndpointPath = new PathString(OpenIdConnectServerDefaults.LogoutEndpointPath);

            Provider = new OpenIdConnectServerProvider();
            SystemClock = new SystemClock();
            Cache = new MemoryCache(typeof(OpenIdConnectServerMiddleware).Name);
            RandomNumberGenerator = RandomNumberGenerator.Create();

            SigningCredentials = new List<SigningCredentials>();
            AccessTokenHandler = new JwtSecurityTokenHandler();
            IdentityTokenHandler = new JwtSecurityTokenHandler();
        }

        /// <summary>
        /// The base address used to uniquely identify the authorization server.
        /// The URI must be absolute and may contain a path, but no query string or fragment part.
        /// Unless AllowInsecureHttp has been set to true, an HTTPS address must be provided.
        /// </summary>
        public Uri Issuer { get; set; }

        /// <summary>
        /// Gets the list of the credentials used to sign tokens. You can provide any symmetric (e.g <see cref="InMemorySymmetricSecurityKey"/>)
        /// or asymmetric (e.g <see cref="RsaSecurityKey"/>, <see cref="X509AsymmetricSecurityKey"/> or <see cref="X509SecurityKey"/>)
        /// security key, but you're strongly encouraged to use a 2048 or 4096-bits RSA asymmetric key in production.
        /// Note that only keys supporting the <see cref="SecurityAlgorithms.RsaSha256Signature"/> algorithm can be exposed
        /// on the configuration metadata endpoint. A <see cref="X509SigningCredentials"/> instance may also be provided.
        /// </summary>
        public IList<SigningCredentials> SigningCredentials { get; set; }

        /// <summary>
        /// The request path where client applications will redirect the user-agent in order to 
        /// obtain user consent to issue a token. Must begin with a leading slash, like "/connect/authorize".
        /// This setting can be set to <see cref="PathString.Empty"/> to disable the authorization endpoint.
        /// </summary>
        public PathString AuthorizationEndpointPath { get; set; }

        /// <summary>
        /// The request path where client applications will be able to retrieve the configuration metadata associated
        /// with this instance. Must begin with a leading slash, like "/.well-known/openid-configuration".
        /// This setting can be set to <see cref="PathString.Empty"/> to disable the configuration endpoint.
        /// </summary>
        public PathString ConfigurationEndpointPath { get; set; }

        /// <summary>
        /// The request path where client applications will be able to retrieve the JSON Web Key Set
        /// associated with this instance. Must begin with a leading slash, like "/.well-known/jwks".
        /// This setting can be set to <see cref="PathString.Empty"/> to disable the cryptography endpoint.
        /// </summary>
        public PathString CryptographyEndpointPath { get; set; }

        /// <summary>
        /// The request path client applications communicate with directly as part of the OpenID Connect protocol. 
        /// Must begin with a leading slash, like "/connect/token". If the client is issued a client_secret, it must
        /// be provided to this endpoint. You can set it to <see cref="PathString.Empty"/> to disable the token endpoint.
        /// </summary>
        public PathString TokenEndpointPath { get; set; }

        /// <summary>
        /// The request path client applications communicate with to validate identity or access tokens. 
        /// Must begin with a leading slash, like "/connect/token_validation".
        /// You can set it to <see cref="PathString.Empty"/> to disable the validation endpoint.
        /// </summary>
        public PathString ValidationEndpointPath { get; set; }

        /// <summary>
        /// The request path client applications communicate with to log out. 
        /// Must begin with a leading slash, like "/connect/logout".
        /// You can set it to <see cref="PathString.Empty"/> to disable the logout endpoint.
        /// </summary>
        public PathString LogoutEndpointPath { get; set; }

        /// <summary>
        /// Specifies a <see cref="IOpenIdConnectServerProvider"/> that the <see cref="OpenIdConnectServerMiddleware" /> invokes
        /// to enable developer control over the while authentication/authorization process.
        /// If not specified, a <see cref="OpenIdConnectServerProvider" /> is automatically instanciated.
        /// </summary>
        public IOpenIdConnectServerProvider Provider { get; set; }

        /// <summary>
        /// The data format used to protect and unprotect the information contained in the authorization code. 
        /// If not provided by the application the default data protection provider depends on the host server. 
        /// The SystemWeb host on IIS will use ASP.NET machine key data protection, and HttpListener and other self-hosted
        /// servers will use DPAPI data protection.
        /// </summary>
        public ISecureDataFormat<AuthenticationTicket> AuthorizationCodeFormat { get; set; }

        /// <summary>
        /// The data format used to protect the information contained in the access token. 
        /// If not provided by the application the default data protection provider depends on the host server. 
        /// The SystemWeb host on IIS will use ASP.NET machine key data protection, and HttpListener and other self-hosted
        /// servers will use DPAPI data protection.
        /// This property is only used when <see cref="AccessTokenHandler"/> is explicitly set to <value>null</value>
        /// and when <see cref="IOpenIdConnectServerProvider.CreateAccessToken"/> doesn't call
        /// <see cref="BaseNotification{OpenIdConnectServerOptions}.HandleResponse"/>.
        /// </summary>
        public ISecureDataFormat<AuthenticationTicket> AccessTokenFormat { get; set; }

        /// <summary>
        /// The data format used to protect and unprotect the information contained in the refresh token. 
        /// If not provided by the application the default data protection provider depends on the host server. 
        /// The SystemWeb host on IIS will use ASP.NET machine key data protection, and HttpListener and other self-hosted
        /// servers will use DPAPI data protection.
        /// This property is only used when <see cref="IOpenIdConnectServerProvider.CreateRefreshToken"/> doesn't call
        /// <see cref="BaseNotification{OpenIdConnectServerOptions}.HandleResponse"/>.
        /// </summary>
        public ISecureDataFormat<AuthenticationTicket> RefreshTokenFormat { get; set; }

        /// <summary>
        /// The <see cref="SecurityTokenHandler"/> instance used to forge access tokens.
        /// The default instance (<see cref="JwtSecurityTokenHandler"/>) creates JWT security tokens.
        /// You can set it to null to produce opaque tokens serialized by the data protector subsytem.
        /// This property is only used when <see cref="IOpenIdConnectServerProvider.CreateAccessToken"/> doesn't call
        /// <see cref="BaseNotification{OpenIdConnectServerOptions}.HandleResponse"/>.
        /// </summary>
        public SecurityTokenHandler AccessTokenHandler { get; set; }

        /// <summary>
        /// The <see cref="JwtSecurityTokenHandler"/> instance used to forge identity tokens.
        /// You can replace the default instance to change the way id_tokens are serialized.
        /// This property is only used when <see cref="IOpenIdConnectServerProvider.CreateIdentityToken"/> doesn't call
        /// <see cref="BaseNotification{OpenIdConnectServerOptions}.HandleResponse"/>.
        /// </summary>
        public JwtSecurityTokenHandler IdentityTokenHandler { get; set; }

        /// <summary>
        /// The period of time the authorization code remains valid after being issued. The default is 5 minutes.
        /// This time span must also take into account clock synchronization between servers in a web farm, so a very 
        /// brief value could result in unexpectedly expired tokens.
        /// </summary>
        public TimeSpan AuthorizationCodeLifetime { get; set; }

        /// <summary>
        /// The period of time the access token remains valid after being issued. The default is 1 hour.
        /// The client application is expected to refresh or acquire a new access token after the token has expired. 
        /// </summary>
        public TimeSpan AccessTokenLifetime { get; set; }

        /// <summary>
        /// The period of time the identity token remains valid after being issued. The default is 20 minutes.
        /// The client application is expected to refresh or acquire a new identity token after the token has expired. 
        /// </summary>
        public TimeSpan IdentityTokenLifetime { get; set; }

        /// <summary>
        /// The period of time the refresh token remains valid after being issued. The default is 6 hours.
        /// The client application is expected to start a whole new authentication flow after the refresh token has expired. 
        /// </summary>
        public TimeSpan RefreshTokenLifetime { get; set; }

        /// <summary>
        /// Determines whether refresh tokens issued during a grant_type=refresh_token request should be generated
        /// with a new expiration date or should re-use the same expiration date as the original refresh token.
        /// Set this property to <c>true</c> to assign a new expiration date each time a refresh token is issued,
        /// <c>false</c> to use the expiration date of the original refresh token. When set to <c>false</c>,
        /// access and identity tokens' lifetime cannot exceed the expiration date of the refresh token.
        /// </summary>
        public bool UseSlidingExpiration { get; set; }

        /// <summary>
        /// Set to true if the web application is able to render error messages on the authorization endpoint. This is only needed for cases where
        /// the browser is not redirected back to the client application, for example, when the client_id or redirect_uri are incorrect. The 
        /// authorization endpoint should expect to see "oauth.Error", "oauth.ErrorDescription", "oauth.ErrorUri" properties added to the owin environment.
        /// </summary>
        public bool ApplicationCanDisplayErrors { get; set; }

        /// <summary>
        /// Used to know what the current clock time is when calculating or validating token expiration. When not assigned default is based on
        /// DateTimeOffset.UtcNow. This is typically needed only for unit testing.
        /// </summary>
        public ISystemClock SystemClock { get; set; }

        /// <summary>
        /// Gets or sets the logger used by <see cref="OpenIdConnectServerMiddleware"/>.
        /// When unassigned, a default instance is created using the logger factory.
        /// </summary>
        public ILogger Logger { get; set; }

        /// <summary>
        /// True to allow incoming requests to arrive on HTTP and to allow redirect_uri parameters to have HTTP URI addresses.
        /// Setting this option to false in production is strongly encouraged to mitigate man-in-the-middle attacks.
        /// </summary>
        public bool AllowInsecureHttp { get; set; }

        /// <summary>
        /// The cache instance used to store short-lived items like authorization codes or authorization requests.
        /// You can replace the default instance by a distributed implementation to support Web farm environments.
        /// </summary>
        public ObjectCache Cache { get; set; }

        /// <summary>
        /// The random number generator used for cryptographic operations.
        /// Replacing the default instance is usually not necessary.
        /// </summary>
        public RandomNumberGenerator RandomNumberGenerator { get; set; }

        /// <summary>
        /// The signature provider used by the OpenID Connect server
        /// middleware to sign tokens and to verify their authenticity.
        /// </summary>
        public SignatureProvider SignatureProvider { get; set; }
    }
}
