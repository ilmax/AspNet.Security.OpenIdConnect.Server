/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Protocols.WSTrust;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Owin.Security.OpenIdConnect.Extensions;

namespace Owin.Security.OpenIdConnect.Server {
    internal class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        // Implementing AuthenticateCoreAsync allows the inner application
        // to retrieve the identity extracted from the optional id_token_hint.
        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync() {
            var notification = new MatchEndpointContext(Context, Options);

            if (Options.AuthorizationEndpointPath.HasValue &&
                Options.AuthorizationEndpointPath == Request.Path) {
                notification.MatchesAuthorizationEndpoint();
            }

            else if (Options.LogoutEndpointPath.HasValue &&
                     Options.LogoutEndpointPath == Request.Path) {
                notification.MatchesLogoutEndpoint();
            }

            await Options.Provider.MatchEndpoint(notification);

            if (notification.IsAuthorizationEndpoint || notification.IsLogoutEndpoint) {
                // Try to retrieve the current OpenID Connect request from the OWIN context.
                // If the request cannot be found, this means that this middleware was configured
                // to use the automatic authentication mode and that AuthenticateCoreAsync
                // was invoked before Invoke*EndpointAsync: in this case, the OpenID Connect
                // request is directly extracted from the query string or the request form.
                var request = Context.GetOpenIdConnectRequest();
                if (request == null) {
                    if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                        request = new OpenIdConnectMessage(Request.Query);
                    }

                    else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                        if (string.IsNullOrEmpty(Request.ContentType)) {
                            return null;
                        }

                        else if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                            return null;
                        }

                        request = new OpenIdConnectMessage(await Request.ReadFormAsync());
                    }
                }

                // Missing or invalid requests are ignored in AuthenticateCoreAsync:
                // in this case, null is always returned to indicate authentication failed.
                if (request == null) {
                    return null;
                }

                if (string.IsNullOrEmpty(request.IdTokenHint)) {
                    return null;
                }

                var ticket = await ReceiveIdentityTokenAsync(request.IdTokenHint, request);
                if (ticket == null) {
                    Options.Logger.WriteVerbose("Invalid id_token_hint");

                    return null;
                }

                // Tickets are returned even if they
                // are considered invalid (e.g expired).
                return ticket;
            }

            return null;
        }

        public override async Task<bool> InvokeAsync() {
            var notification = new MatchEndpointContext(Context, Options);

            if (Options.AuthorizationEndpointPath.HasValue &&
                Options.AuthorizationEndpointPath == Request.Path) {
                notification.MatchesAuthorizationEndpoint();
            }

            else if (Options.TokenEndpointPath.HasValue &&
                     Options.TokenEndpointPath == Request.Path) {
                notification.MatchesTokenEndpoint();
            }

            else if (Options.ValidationEndpointPath.HasValue &&
                     Options.ValidationEndpointPath == Request.Path) {
                notification.MatchesValidationEndpoint();
            }

            else if (Options.LogoutEndpointPath.HasValue &&
                     Options.LogoutEndpointPath == Request.Path) {
                notification.MatchesLogoutEndpoint();
            }

            else if (Options.ConfigurationEndpointPath.HasValue &&
                     Options.ConfigurationEndpointPath == Request.Path) {
                notification.MatchesConfigurationEndpoint();
            }

            else if (Options.CryptographyEndpointPath.HasValue &&
                     Options.CryptographyEndpointPath == Request.Path) {
                notification.MatchesCryptographyEndpoint();
            }

            await Options.Provider.MatchEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }
            
            if (!Options.AllowInsecureHttp && string.Equals(Request.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.WriteWarning("Authorization server rejecting http request because AllowInsecureHttp is false.");
                return true;
            }

            else if (notification.IsAuthorizationEndpoint) {
                return await InvokeAuthorizationEndpointAsync();
            }

            else if (notification.IsLogoutEndpoint) {
                return await InvokeLogoutEndpointAsync();
            }

            else if (notification.IsTokenEndpoint) {
                await InvokeTokenEndpointAsync();
                return true;
            }

            else if (notification.IsValidationEndpoint) {
                await InvokeValidationEndpointAsync();
                return true;
            }

            else if (notification.IsConfigurationEndpoint) {
                await InvokeConfigurationEndpointAsync();
                return true;
            }

            else if (notification.IsCryptographyEndpoint) {
                await InvokeCryptographyEndpointAsync();
                return true;
            }

            return false;
        }

        private async Task<bool> InvokeAuthorizationEndpointAsync() {
            OpenIdConnectMessage request;

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                // Create a new authorization request using the
                // parameters retrieved from the query string.
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                // Create a new authorization request using the
                // parameters retrieved from the request form.
                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed authorization request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            // Re-assemble the authorization request using the cache if
            // a 'unique_id' parameter has been extracted from the received message.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier)) {
                var item = Options.Cache.Get(identifier) as string;
                if (item == null) {
                    Options.Logger.WriteInformation("A unique_id has been provided but no corresponding " +
                                            "OpenID Connect request has been found in the cache.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "Invalid request: timeout expired."
                    });
                }

                using (var stream = new MemoryStream(Convert.FromBase64String(item)))
                using (var reader = new BinaryReader(stream)) {
                    // Make sure the stored authorization request
                    // has been serialized using the same method.
                    var version = reader.ReadInt32();
                    if (version != 1) {
                        Options.Cache.Remove(identifier);

                        Options.Logger.WriteError("An invalid OpenID Connect request has been found in the cache.");

                        return await SendErrorPageAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "Invalid request: timeout expired."
                        });
                    }

                    for (int index = 0, length = reader.ReadInt32(); index < length; index++) {
                        var name = reader.ReadString();
                        var value = reader.ReadString();

                        // Skip restoring the parameter retrieved from the stored request
                        // if the OpenID Connect message extracted from the query string
                        // or the request form defined the same parameter.
                        if (!request.Parameters.ContainsKey(name)) {
                            request.SetParameter(name, value);
                        }
                    }
                }
            }
            
            // Store the authorization request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // client_id is mandatory parameter and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            if (string.IsNullOrEmpty(request.ClientId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing"
                });
            }

            // While redirect_uri was not mandatory in OAuth2, this parameter
            // is now declared as REQUIRED and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            // To keep AspNet.Security.OpenIdConnect.Server compatible with pure OAuth2 clients,
            // an error is only returned if the request was made by an OpenID Connect client.
            if (string.IsNullOrEmpty(request.RedirectUri) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "redirect_uri must be included when making an OpenID Connect request"
                });
            }

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                Uri uri;
                if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out uri)) {
                    // redirect_uri MUST be an absolute URI.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must be absolute"
                    });
                }

                else if (!string.IsNullOrEmpty(uri.Fragment)) {
                    // redirect_uri MUST NOT include a fragment component.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must not include a fragment"
                    });
                }

                else if (!Options.AllowInsecureHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
                    // redirect_uri SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri does not meet the security requirements"
                    });
                }
            }

            var clientNotification = new ValidateClientRedirectUriContext(Context, Options, request);
            await Options.Provider.ValidateClientRedirectUri(clientNotification);

            // Reject the authorization request if the redirect_uri was not validated.
            if (!clientNotification.IsValidated) {
                Options.Logger.WriteVerbose("Unable to validate client information");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });
            }

            if (string.IsNullOrEmpty(request.ResponseType)) {
                Options.Logger.WriteVerbose("Authorization request missing required response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!request.IsNoneFlow() && !request.IsAuthorizationCodeFlow() &&
                     !request.IsImplicitFlow() && !request.IsHybridFlow()) {
                Options.Logger.WriteVerbose("Authorization request contains unsupported response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!request.IsFormPostResponseMode() && !request.IsFragmentResponseMode() && !request.IsQueryResponseMode()) {
                Options.Logger.WriteVerbose("Authorization request contains unsupported response_mode parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_mode unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // response_mode=query (explicit or not) and a response_type containing id_token
            // or token are not considered as a safe combination and MUST be rejected.
            // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Security
            else if (request.IsQueryResponseMode() && (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) ||
                                                       request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token))) {
                Options.Logger.WriteVerbose("Authorization request contains unsafe response_type/response_mode combination");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type/response_mode combination unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject OpenID Connect implicit/hybrid requests missing the mandatory nonce parameter.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest,
            // http://openid.net/specs/openid-connect-implicit-1_0.html#RequestParameters
            // and http://openid.net/specs/openid-connect-core-1_0.html#HybridIDToken.
            else if (string.IsNullOrEmpty(request.Nonce) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) &&
                                                           (request.IsImplicitFlow() || request.IsHybridFlow())) {
                Options.Logger.WriteVerbose("The 'nonce' parameter was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "nonce parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the id_token response_mode if no openid scope has been received.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                    !request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                Options.Logger.WriteVerbose("The 'openid' scope part was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "openid scope missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the code response_mode if the token endpoint has been disabled.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code) &&
                    !Options.TokenEndpointPath.HasValue) {
                Options.Logger.WriteVerbose("Authorization request contains the disabled code response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=code is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the id_token response_mode if no signing credentials have been provided.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                     Options.SigningCredentials == null) {
                Options.Logger.WriteVerbose("Authorization request contains the disabled id_token response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=id_token is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var validationNotification = new ValidateAuthorizationRequestContext(Context, Options, request, clientNotification);
            await Options.Provider.ValidateAuthorizationRequest(validationNotification);

            // Stop processing the request if Validated was not called.
            if (!validationNotification.IsValidated) {
                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = validationNotification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = validationNotification.ErrorDescription,
                    ErrorUri = validationNotification.ErrorUri,
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            identifier = request.GetUniqueIdentifier();
            if (string.IsNullOrEmpty(identifier)) {
                // Generate a new 256-bits identifier and associate it with the authorization request.
                identifier = GenerateKey(length: 256 / 8);
                request.SetUniqueIdentifier(identifier);

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(/* version: */ 1);
                    writer.Write(request.Parameters.Count);

                    foreach (var parameter in request.Parameters) {
                        writer.Write(parameter.Key);
                        writer.Write(parameter.Value);
                    }

                    // Store the authorization request in the cache.
                    Options.Cache.Add(identifier, Convert.ToBase64String(stream.ToArray()), new CacheItemPolicy {
                        SlidingExpiration = TimeSpan.FromHours(1)
                    });
                }
            }

            var notification = new AuthorizationEndpointContext(Context, Options, request);
            await Options.Provider.AuthorizationEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }

        protected override async Task InitializeCoreAsync() {
            Response.OnSendingHeaders(state => {
                var handler = (OpenIdConnectServerHandler) state;

                // Add a unique key indicating response headers have been sent.
                handler.Context.Environment["app.HeadersSent"] = true;
            }, this);

            await base.InitializeCoreAsync();
        }

        /// <remarks>
        /// Authentication handlers cannot reliabily write to the response stream
        /// from ApplyResponseGrantAsync or ApplyResponseChallengeAsync because these methods
        /// are susceptible to be invoked from AuthenticationHandler.OnSendingHeaderCallback
        /// where calling Write or WriteAsync on the response stream may result in a deadlock
        /// on hosts using streamed responses. To work around this limitation, OpenIdConnectServerHandler
        /// doesn't implement ApplyResponseGrantAsync but TeardownCoreAsync,
        /// which is never called by AuthenticationHandler.OnSendingHeaderCallback.
        /// In theory, this would prevent OpenIdConnectServerHandler from both applying
        /// the response grant and allowing the next middleware in the pipeline to alter
        /// the response stream but in practice, the OpenIdConnectServerHandler is assumed to be
        /// the only middleware allowed to write to the response stream when a response grant has been applied.
        /// </remarks>
        protected override async Task TeardownCoreAsync() {
            // Stop processing the request if no OpenID Connect
            // message has been found in the current context.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return;
            }

            // Apply the default request processing if no OpenID Connect
            // response has been forged by the inner application.
            var response = Context.GetOpenIdConnectResponse();
            if (response == null) {
                if (await HandleAuthorizationResponseAsync()) {
                    return;
                }

                await HandleLogoutResponseAsync();
                return;
            }

            // Successful authorization responses are directly applied by
            // HandleAuthorizationResponseAsync: only error responses should be handled at this stage.
            if (string.IsNullOrEmpty(response.Error)) {
                return;
            }

            await SendErrorRedirectAsync(request, response);
        }

        private async Task<bool> HandleAuthorizationResponseAsync() {
            // request may be null when no authorization request has been received
            // or has been already handled by InvokeAuthorizationEndpointAsync.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return false;
            }

            // Stop processing the request if there's no response grant that matches
            // the authentication type associated with this middleware instance
            // or if the response status code doesn't indicate a successful response.
            var context = Helper.LookupSignIn(Options.AuthenticationType);
            if (context == null || Response.StatusCode != 200) {
                return false;
            }

            if (Context.Environment.ContainsKey("app.HeadersSent")) {
                Options.Logger.WriteCritical(
                    "OpenIdConnectServerHandler.TeardownCoreAsync cannot be called when " +
                    "the response headers have already been sent back to the user agent. " +
                    "Make sure the response body has not been altered and that no middleware " +
                    "has attempted to write to the response stream during this request.");

                return true;
            }

            if (!context.Principal.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier) &&
                !context.Principal.HasClaim(claim => claim.Type == JwtRegisteredClaimNames.Sub)) {
                Options.Logger.WriteError("The returned identity doesn't contain the mandatory ClaimTypes.NameIdentifier claim.");

                await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "no ClaimTypes.NameIdentifier or sub claim found"
                });

                return true;
            }

            // redirect_uri is added to the response message since it's not a mandatory parameter
            // in OAuth 2.0 and can be set or replaced from the ValidateClientRedirectUri event.
            var response = new OpenIdConnectMessage {
                RedirectUri = request.RedirectUri,
                State = request.State
            };

            // Associate client_id with all subsequent tickets.
            context.Properties.Dictionary[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;

            if (!string.IsNullOrEmpty(request.Nonce)) {
                // Keep the original nonce parameter for later comparison.
                context.Properties.Dictionary[OpenIdConnectConstants.Extra.Nonce] = request.Nonce;
            }

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                // Keep original the original redirect_uri for later comparison.
                context.Properties.Dictionary[OpenIdConnectConstants.Extra.RedirectUri] = request.RedirectUri;
            }

            if (!string.IsNullOrEmpty(request.Resource)) {
                // Keep the original resource parameter for later comparison.
                context.Properties.Dictionary[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            if (!string.IsNullOrEmpty(request.Scope)) {
                // Keep the original scope parameter for later comparison.
                context.Properties.Dictionary[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            // Determine whether an authorization code should be returned
            // and invoke CreateAuthorizationCodeAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = context.Properties.Copy();

                // properties.IssuedUtc and properties.ExpiresUtc are always
                // explicitly set to null to avoid aligning the expiration date
                // of the authorization code with the lifetime of the other tokens.
                properties.IssuedUtc = properties.ExpiresUtc = null;

                response.Code = await CreateAuthorizationCodeAsync(context.Identity, properties, request, response);

                // Ensure that an authorization code is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.Code)) {
                    Options.Logger.WriteError("CreateAuthorizationCodeAsync returned no authorization code");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid authorization code was issued"
                    });

                    return true;
                }
            }

            // Determine whether an access token should be returned
            // and invoke CreateAccessTokenAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = context.Properties.Copy();

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await CreateAccessTokenAsync(context.Identity, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken)) {
                    Options.Logger.WriteError("CreateAccessTokenAsync returned no access token.");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return true;
                }

                // properties.ExpiresUtc is automatically set by CreateAccessTokenAsync but the end user
                // is free to set a null value directly in the CreateAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow) {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long) (lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Determine whether an identity token should be returned
            // and invoke CreateIdentityTokenAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = context.Properties.Copy();

                response.IdToken = await CreateIdentityTokenAsync(context.Identity, properties, request, response);

                // Ensure that an identity token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.IdToken)) {
                    Options.Logger.WriteError("CreateIdentityTokenAsync returned no identity token.");

                    await SendNativeErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid identity token was issued"
                    });

                    return true;
                }
            }

            // Remove the OpenID Connect request from the cache.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier)) {
                Options.Cache.Remove(identifier);
            }

            var ticket = new AuthenticationTicket(context.Identity, context.Properties);

            var notification = new AuthorizationEndpointResponseContext(Context, Options, ticket, request, response);
            await Options.Provider.AuthorizationEndpointResponse(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return await ApplyAuthorizationResponseAsync(request, response);
        }

        private async Task<bool> HandleLogoutResponseAsync() {
            // request may be null when no logout request has been received
            // or has been already handled by InvokeLogoutEndpointAsync.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return false;
            }
            
            // Stop processing the request if there's no signout context that matches
            // the authentication type associated with this middleware instance
            // or if the response status code doesn't indicate a successful response.
            var context = Helper.LookupSignOut(Options.AuthenticationType, Options.AuthenticationMode);
            if (context == null || Response.StatusCode != 200) {
                return false;
            }

            if (Context.Environment.ContainsKey("app.HeadersSent")) {
                Options.Logger.WriteCritical(
                    "OpenIdConnectServerHandler.TeardownCoreAsync cannot be called when " +
                    "the response headers have already been sent back to the user agent. " +
                    "Make sure the response body has not been altered and that no middleware " +
                    "has attempted to write to the response stream during this request.");
                return false;
            }

            // post_logout_redirect_uri is added to the response message since it can be
            // set or replaced from the ValidateClientLogoutRedirectUri event.
            var response = new OpenIdConnectMessage {
                PostLogoutRedirectUri = request.PostLogoutRedirectUri,
                State = request.State
            };

            var notification = new LogoutEndpointResponseContext(Context, Options, request, response);
            await Options.Provider.LogoutEndpointResponse(notification);

            if (notification.HandledResponse) {
                return true;
            }

            // Stop processing the request if no explicit
            // post_logout_redirect_uri has been provided.
            if (string.IsNullOrEmpty(response.PostLogoutRedirectUri)) {
                return true;
            }

            var location = response.PostLogoutRedirectUri;

            foreach (var parameter in response.Parameters) {
                // Don't include post_logout_redirect_uri in the query string.
                if (string.Equals(parameter.Key, OpenIdConnectParameterNames.PostLogoutRedirectUri, StringComparison.Ordinal)) {
                    continue;
                }

                location = WebUtilities.AddQueryString(location, parameter.Key, parameter.Value);
            }

            Response.Redirect(location);

            return true;
        }

        private async Task<bool> ApplyAuthorizationResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            if (request.IsFormPostResponseMode()) {
                using (var buffer = new MemoryStream())
                using (var writer = new StreamWriter(buffer)) {
                    writer.WriteLine("<!doctype html>");
                    writer.WriteLine("<html>");
                    writer.WriteLine("<body>");

                    // While the redirect_uri parameter should be guarded against unknown values
                    // by IOpenIdConnectServerProvider.ValidateClientRedirectUri,
                    // it's still safer to encode it to avoid cross-site scripting attacks
                    // if the authorization server has a relaxed policy concerning redirect URIs.
                    writer.WriteLine("<form name='form' method='post' action='" + WebUtility.HtmlEncode(response.RedirectUri) + "'>");

                    foreach (var parameter in response.Parameters) {
                        // Don't include redirect_uri in the form.
                        if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                            continue;
                        }

                        var key = WebUtility.HtmlEncode(parameter.Key);
                        var value = WebUtility.HtmlEncode(parameter.Value);

                        writer.WriteLine("<input type='hidden' name='" + key + "' value='" + value + "' />");
                    }

                    writer.WriteLine("<noscript>Click here to finish the authorization process: <input type='submit' /></noscript>");
                    writer.WriteLine("</form>");
                    writer.WriteLine("<script>document.form.submit();</script>");
                    writer.WriteLine("</body>");
                    writer.WriteLine("</html>");
                    writer.Flush();

                    Response.ContentLength = buffer.Length;
                    Response.ContentType = "text/html;charset=UTF-8";

                    buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                    await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);

                    return true;
                }
            }

            else if (request.IsFragmentResponseMode()) {
                var location = response.RedirectUri;
                var appender = new Appender(location, '#');

                foreach (var parameter in response.Parameters) {
                    // Don't include redirect_uri in the fragment.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                        continue;
                    }

                    appender.Append(parameter.Key, parameter.Value);
                }

                Response.Redirect(appender.ToString());
                return true;
            }

            else if (request.IsQueryResponseMode()) {
                var location = response.RedirectUri;

                foreach (var parameter in response.Parameters) {
                    // Don't include redirect_uri in the query string.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal)) {
                        continue;
                    }

                    location = WebUtilities.AddQueryString(location, parameter.Key, parameter.Value);
                }

                Response.Redirect(location);
                return true;
            }

            return false;
        }

        private async Task InvokeConfigurationEndpointAsync() {
            var notification = new ConfigurationEndpointContext(Context, Options);
            notification.Issuer = Context.GetIssuer(Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Configuration endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.AuthorizationEndpoint = notification.Issuer.AddPath(Options.AuthorizationEndpointPath);
            }

            if (Options.CryptographyEndpointPath.HasValue) {
                notification.CryptographyEndpoint = notification.Issuer.AddPath(Options.CryptographyEndpointPath);
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.TokenEndpoint = notification.Issuer.AddPath(Options.TokenEndpointPath);
            }

            if (Options.LogoutEndpointPath.HasValue) {
                notification.LogoutEndpoint = notification.Issuer.AddPath(Options.LogoutEndpointPath);
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                // Only expose the implicit grant type if the token
                // endpoint has not been explicitly disabled.
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Implicit);

                if (Options.TokenEndpointPath.HasValue) {
                    // Only expose the authorization code and refresh token grant types
                    // if both the authorization and the token endpoints are enabled.
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.AuthorizationCode);
                }
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.RefreshToken);

                // If the authorization endpoint is disabled, assume the authorization server will
                // allow the client credentials and resource owner password credentials grant types.
                if (!Options.AuthorizationEndpointPath.HasValue) {
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.ClientCredentials);
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Password);
                }
            }

            // Only populate response_modes_supported and response_types_supported
            // if the authorization endpoint is available.
            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.FormPost);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Fragment);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Query);

                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing id_token when
                // signing credentials have been explicitly provided.
                if (Options.SigningCredentials != null) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.IdToken);
                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }

                // Only expose response types containing code when
                // the token endpoint has not been explicitly disabled.
                if (Options.TokenEndpointPath.HasValue) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Code);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);

                    // Only expose response types containing id_token when
                    // signing credentials have been explicitly provided.
                    if (Options.SigningCredentials != null) {
                        notification.ResponseTypes.Add(
                            OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                            OpenIdConnectConstants.ResponseTypes.IdToken);

                        notification.ResponseTypes.Add(
                            OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                            OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                            OpenIdConnectConstants.ResponseTypes.Token);
                    }
                }
            }

            notification.Scopes.Add(OpenIdConnectConstants.Scopes.OpenId);

            notification.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);

            notification.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RS256);

            await Options.Provider.ConfigurationEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }
            
            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Metadata.Issuer, notification.Issuer);

            if (!string.IsNullOrEmpty(notification.AuthorizationEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint, notification.AuthorizationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.TokenEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.TokenEndpoint, notification.TokenEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.LogoutEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.EndSessionEndpoint, notification.LogoutEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.CryptographyEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.JwksUri, notification.CryptographyEndpoint);
            }

            payload.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(notification.GrantTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(notification.ResponseModes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(notification.ResponseTypes));

            payload.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(notification.SubjectTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(notification.Scopes));

            payload.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(notification.SigningAlgorithms));

            var context = new ConfigurationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ConfigurationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeCryptographyEndpointAsync() {
            var notification = new CryptographyEndpointContext(Context, Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Cryptography endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            foreach (var credentials in Options.SigningCredentials) {
                // Skip processing the metadata request if the key is not supported.
                var asymmetricSecurityKey = credentials.SigningKey as AsymmetricSecurityKey;
                if (asymmetricSecurityKey == null) {
                    Options.Logger.WriteWarning(string.Format(CultureInfo.InvariantCulture,
                        "Cryptography endpoint: invalid signing key registered. " +
                        "Make sure to provide an asymmetric security key deriving from '{0}'.",
                        typeof(AsymmetricSecurityKey).FullName));

                    continue;
                }

                if (!asymmetricSecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                    Options.Logger.WriteWarning(string.Format(CultureInfo.InvariantCulture,
                        "Cryptography endpoint: invalid signing key registered. " +
                        "Make sure to provide a '{0}' instance exposing " +
                        "an asymmetric security key supporting the '{1}' algorithm.",
                        typeof(SigningCredentials).Name, SecurityAlgorithms.RsaSha256Signature));

                    continue;
                }

                X509Certificate2 x509Certificate = null;

                // Determine whether the signing credentials are directly based on a X.509 certificate.
                var x509SigningCredentials = credentials as X509SigningCredentials;
                if (x509SigningCredentials != null) {
                    x509Certificate = x509SigningCredentials.Certificate;
                }

                // Skip looking for a X509SecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in the SigningCredentials instance.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509SecurityKey = asymmetricSecurityKey as X509SecurityKey;
                    if (x509SecurityKey != null) {
                        x509Certificate = x509SecurityKey.Certificate;
                    }
                }

                // Skip looking for a X509AsymmetricSecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in SigningCredentials or SigningCredentials.SigningKey.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509AsymmetricSecurityKey = asymmetricSecurityKey as X509AsymmetricSecurityKey;
                    if (x509AsymmetricSecurityKey != null) {
                        // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                        // Reflection is the only way to get the certificate used to create the security key.
                        var field = typeof(X509AsymmetricSecurityKey).GetField(
                            name: "certificate",
                            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);

                        x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                    }
                }

                if (x509Certificate != null) {
                    // Create a new JSON Web Key exposing the
                    // certificate instead of its public RSA key.
                    notification.Keys.Add(new JsonWebKey {
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,
                        Alg = JwtAlgorithms.RSA_SHA256,
                        Use = JsonWebKeyUseNames.Sig,

                        // By default, use the hexadecimal representation of the
                        // certificate's SHA-1 hash as the unique key identifier.
                        Kid = x509Certificate.Thumbprint,

                        // x5t must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                        X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash()),

                        // Unlike E or N, the certificates contained in x5c
                        // must be base64-encoded and not base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                        X5c = { Convert.ToBase64String(x509Certificate.RawData) }
                    });
                }

                else {
                    // Create a new JSON Web Key exposing the exponent and the modulus of the RSA public key.
                    var asymmetricAlgorithm = (RSA) asymmetricSecurityKey.GetAsymmetricAlgorithm(
                        algorithm: SecurityAlgorithms.RsaSha256Signature, privateKey: false);

                    // Export the RSA public key.
                    var parameters = asymmetricAlgorithm.ExportParameters(includePrivateParameters: false);

                    notification.Keys.Add(new JsonWebKey {
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,
                        Alg = JwtAlgorithms.RSA_SHA256,
                        Use = JsonWebKeyUseNames.Sig,

                        // Create a unique identifier using the base64url-encoded representation of the modulus.
                        // Note: use the first 40 chars to avoid using a too long identifier.
                        Kid = Base64UrlEncoder.Encode(parameters.Modulus)
                                              .Substring(0, 40)
                                              .ToUpperInvariant(),

                        // Both E and N must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                        E = Base64UrlEncoder.Encode(parameters.Exponent),
                        N = Base64UrlEncoder.Encode(parameters.Modulus)
                    });
                }
            }

            await Options.Provider.CryptographyEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();
            var keys = new JArray();

            foreach (var key in notification.Keys) {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrEmpty(key.Kty)) {
                    Options.Logger.WriteWarning("Cryptography endpoint: a JSON Web Key didn't " +
                        "contain the mandatory 'Kty' parameter and has been ignored.");

                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string> {
                    { JsonWebKeyParameterNames.Kid, key.Kid },
                    { JsonWebKeyParameterNames.Use, key.Use },
                    { JsonWebKeyParameterNames.Kty, key.Kty },
                    { JsonWebKeyParameterNames.KeyOps, key.KeyOps },
                    { JsonWebKeyParameterNames.Alg, key.Alg },
                    { JsonWebKeyParameterNames.X5t, key.X5t },
                    { JsonWebKeyParameterNames.X5u, key.X5u },
                    { JsonWebKeyParameterNames.E, key.E },
                    { JsonWebKeyParameterNames.N, key.N }
                };

                foreach (var parameter in parameters) {
                    if (!string.IsNullOrEmpty(parameter.Value)) {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.X5c.Any()) {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            payload.Add(JsonWebKeyParameterNames.Keys, keys);

            var context = new CryptographyEndpointResponseContext(Context, Options, payload);
            await Options.Provider.CryptographyEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeTokenEndpointAsync() {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: make sure to use POST."
                });

                return;
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrEmpty(Request.ContentType)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });

                return;
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });

                return;
            }

            var request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                RequestType = OpenIdConnectRequestType.TokenRequest
            };

            // Reject token requests missing the mandatory grant_type parameter.
            if (string.IsNullOrEmpty(request.GrantType)) {
                Options.Logger.WriteError("grant_type missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory grant_type parameter is missing",
                });

                return;
            }

            // Note: client_id is mandatory when using the authorization code grant
            // and must be manually flowed by non-confidential client applications.
            // See https://tools.ietf.org/html/rfc6749#section-4.1.3
            if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.ClientId)) {
                Options.Logger.WriteError("client_id was missing from the token request");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing from the token request"
                });

                return;
            }

            // Reject grant_type=password requests missing username or password.
            // See https://tools.ietf.org/html/rfc6749#section-4.3.2
            if (request.IsPasswordGrantType() && (string.IsNullOrEmpty(request.Username) ||
                                                  string.IsNullOrEmpty(request.Password))) {
                Options.Logger.WriteError("resource owner credentials missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "username and/or password were missing from the request message"
                });

                return;
            }

            // When client_id and client_secret are both null, try to extract them from the Authorization header.
            // See http://tools.ietf.org/html/rfc6749#section-2.3.1 and
            // http://openid.net/specs/openid-connect-core-1_0.html#ClientAuthentication
            if (string.IsNullOrEmpty(request.ClientId) && string.IsNullOrEmpty(request.ClientSecret)) {
                var header = Request.Headers.Get("Authorization");
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var value = header.Substring("Basic ".Length).Trim();
                        var data = Encoding.UTF8.GetString(Convert.FromBase64String(value));

                        var index = data.IndexOf(':');
                        if (index >= 0) {
                            request.ClientId = data.Substring(0, index);
                            request.ClientSecret = data.Substring(index + 1);
                        }
                    }

                    catch (FormatException) { }
                    catch (ArgumentException) { }
                }
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (!clientNotification.IsValidated) {
                Options.Logger.WriteError("invalid client authentication.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });

                return;
            }

            // Reject grant_type=client_credentials requests if client authentication was skipped.
            if (clientNotification.IsSkipped && request.IsClientCredentialsGrantType()) {
                Options.Logger.WriteError("client authentication is required for client_credentials grant type.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "client authentication is required when using client_credentials"
                });

                return;
            }

            var validatingContext = new ValidateTokenRequestContext(Context, Options, request, clientNotification);

            // Validate the token request immediately if the grant type used by
            // the client application doesn't rely on a previously-issued token/code.
            if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) {
                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }
            }

            AuthenticationTicket ticket = null;

            // See http://tools.ietf.org/html/rfc6749#section-4.1
            // and http://tools.ietf.org/html/rfc6749#section-4.1.3 (authorization code grant).
            // See http://tools.ietf.org/html/rfc6749#section-6 (refresh token grant).
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()) {
                ticket = request.IsAuthorizationCodeGrantType() ?
                    await ReceiveAuthorizationCodeAsync(request.Code, request) :
                    await ReceiveRefreshTokenAsync(request.GetRefreshToken(), request);

                if (ticket == null) {
                    Options.Logger.WriteError("invalid ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Invalid ticket"
                    });

                    return;
                }

                if (!ticket.Properties.ExpiresUtc.HasValue ||
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                    Options.Logger.WriteError("expired ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Expired ticket"
                    });

                    return;
                }

                // Validate the redirect_uri flowed by the client application during this token request.
                // Note: for pure OAuth2 requests, redirect_uri is only mandatory if the authorization request
                // contained an explicit redirect_uri. OpenID Connect requests MUST include a redirect_uri
                // but the specifications allow proceeding the token request without returning an error
                // if the authorization request didn't contain an explicit redirect_uri.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                // and http://openid.net/specs/openid-connect-core-1_0.html#TokenRequestValidation
                string address;
                if (request.IsAuthorizationCodeGrantType() &&
                    ticket.Properties.Dictionary.TryGetValue(OpenIdConnectConstants.Extra.RedirectUri, out address)) {
                    ticket.Properties.Dictionary.Remove(OpenIdConnectConstants.Extra.RedirectUri);

                    if (!string.Equals(address, request.RedirectUri, StringComparison.Ordinal)) {
                        Options.Logger.WriteError("authorization code does not contain matching redirect_uri");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Authorization code does not contain matching redirect_uri"
                        });

                        return;
                    }
                }

                // If the client was fully authenticated when retrieving its refresh token,
                // the current request must be rejected if client authentication was not enforced.
                if (request.IsRefreshTokenGrantType() && !clientNotification.IsValidated &&
                    ticket.ContainsProperty(OpenIdConnectConstants.Extra.ClientAuthenticated)) {
                    Options.Logger.WriteError("client authentication is required to use this ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Client authentication is required to use this ticket"
                    });

                    return;
                }

                // Note: identifier may be null for non-confidential client applications
                // whose refresh token has been issued without requiring authentication.
                // When using the refresh token grant, client_id is optional but must validated if present.
                // See https://tools.ietf.org/html/rfc6749#section-6
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshingAccessToken
                var identifier = ticket.Properties.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (!string.IsNullOrEmpty(identifier) && !string.Equals(identifier, request.ClientId, StringComparison.Ordinal)) {
                    Options.Logger.WriteError("ticket does not contain matching client_id");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Ticket does not contain matching client_id"
                    });

                    return;
                }

                if (!string.IsNullOrEmpty(request.Resource)) {
                    // When an explicit resource parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST rejected.
                    var resources = ticket.Properties.GetResources();
                    if (!resources.Any()) {
                        Options.Logger.WriteError("token request cannot contain a resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a resource parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit resource parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain resources
                    // that were not allowed during the authorization request.
                    else if (!resources.ContainsSet(request.GetResources())) {
                        Options.Logger.WriteError("token request does not contain matching resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid resource parameter"
                        });

                        return;
                    }
                }

                if (!string.IsNullOrEmpty(request.Scope)) {
                    // When an explicit scope parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST rejected.
                    // See http://tools.ietf.org/html/rfc6749#section-6
                    var scopes = ticket.Properties.GetScopes();
                    if (!scopes.Any()) {
                        Options.Logger.WriteError("token request cannot contain a scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a scope parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit scope parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain scopes
                    // that were not allowed during the authorization request.
                    else if (!scopes.ContainsSet(request.GetScopes())) {
                        Options.Logger.WriteError("authorization code does not contain matching scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid scope parameter"
                        });

                        return;
                    }
                }

                // Expose the authentication ticket extracted from the authorization
                // code or the refresh token before invoking ValidateTokenRequest.
                validatingContext.AuthenticationTicket = ticket;

                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }

                if (request.IsAuthorizationCodeGrantType()) {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the authorization code.
                    var context = new GrantAuthorizationCodeContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantAuthorizationCode(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                else {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the refresh token.
                    var context = new GrantRefreshTokenContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantRefreshToken(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                // By default, when using the authorization code or the refresh token grants, the authentication ticket
                // extracted from the code/token is used as-is. If the developer didn't provide his own ticket
                // or didn't set an explicit expiration date, the ticket properties are reset to avoid aligning the
                // expiration date of the generated tokens with the lifetime of the authorization code/refresh token.
                if (ticket.Properties.IssuedUtc == validatingContext.AuthenticationTicket.Properties.IssuedUtc) {
                    ticket.Properties.IssuedUtc = null;
                }

                if (ticket.Properties.ExpiresUtc == validatingContext.AuthenticationTicket.Properties.ExpiresUtc) {
                    ticket.Properties.ExpiresUtc = null;
                }
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.3
            // and http://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType()) {
                var context = new GrantResourceOwnerCredentialsContext(Context, Options, request);
                await Options.Provider.GrantResourceOwnerCredentials(context);

                if (!context.IsValidated) {
                    // Note: use invalid_grant as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.4
            // and http://tools.ietf.org/html/rfc6749#section-4.4.2
            else if (request.IsClientCredentialsGrantType()) {
                var context = new GrantClientCredentialsContext(Context, Options, request);
                await Options.Provider.GrantClientCredentials(context);

                if (!context.IsValidated) {
                    // Note: use unauthorized_client as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnauthorizedClient,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-8.3
            else {
                var context = new GrantCustomExtensionContext(Context, Options, request);
                await Options.Provider.GrantCustomExtension(context);

                if (!context.IsValidated) {
                    // Note: use unsupported_grant_type as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnsupportedGrantType,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            var notification = new TokenEndpointContext(Context, Options, request, ticket);
            await Options.Provider.TokenEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            // Flow the changes made to the ticket.
            ticket = notification.Ticket;

            // Ensure an authentication ticket has been provided:
            // a null ticket MUST result in an internal server error.
            if (ticket == null) {
                Options.Logger.WriteError("authentication ticket missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError
                });

                return;
            }

            if (!string.IsNullOrEmpty(request.ClientId)) {
                // Keep the original client_id parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;
            }

            if (!string.IsNullOrEmpty(request.Resource)) {
                // Keep the original resource parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            if (!string.IsNullOrEmpty(request.Scope)) {
                // Keep the original scope parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            if (clientNotification.IsValidated) {
                // Store a boolean indicating the client has been fully authenticated.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.ClientAuthenticated] = "true";
            }

            var response = new OpenIdConnectMessage();

            // Determine whether an identity token should be returned and invoke CreateIdentityTokenAsync if necessary.
            // Note: by default, an identity token is always returned when the openid scope has been requested,
            // but the client application can use the response_type parameter to only include specific types of tokens.
            // When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken)) {
                // When receiving a grant_type=authorization_code or grant_type=refresh_token request,
                // only issue an id_token if the openid scope had been requested during the authorization request.
                if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() ?
                    validatingContext.AuthenticationTicket.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) :
                    request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                    // Make sure to create a copy of the authentication properties
                    // to avoid modifying the properties set on the original ticket.
                    var properties = ticket.Properties.Copy();

                    // When sliding expiration is disabled, the identity token added to the response
                    // cannot live longer than the refresh token that was used in the token request.
                    if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                            (Options.SystemClock.UtcNow + Options.IdentityTokenLifetime)) {
                        properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                    }

                    response.IdToken = await CreateIdentityTokenAsync(ticket.Identity, properties, request, response);

                    // Ensure that an identity token is issued to avoid returning an invalid response.
                    // See http://openid.net/specs/openid-connect-core-1_0.html#TokenResponse
                    // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshTokenResponse
                    if (string.IsNullOrEmpty(response.IdToken)) {
                        Options.Logger.WriteError("CreateIdentityTokenAsync returned no identity token.");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.ServerError,
                            ErrorDescription = "no valid identity token was issued"
                        });

                        return;
                    }
                }
            }

            // Determine whether an access token should be returned and invoke CreateAccessTokenAsync if necessary.
            // Note: by default, an access token is always returned, but the client application can use the response_type
            // parameter to only include specific types of tokens. When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the access token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.AccessTokenLifetime)) {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await CreateAccessTokenAsync(ticket.Identity, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken)) {
                    Options.Logger.WriteError("CreateAccessTokenAsync returned no access token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return;
                }

                // properties.ExpiresUtc is automatically set by CreateAccessTokenAsync but the end user
                // is free to set a null value directly in the CreateAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow) {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long) (lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Determine whether a refresh token should be returned and invoke CreateRefreshTokenAsync if necessary.
            // Note: by default, a refresh token is always returned when the offline_access scope has been requested,
            // but the client application can use the response_type parameter to only include specific types of tokens.
            // When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType("refresh_token")) {
                // When receiving a grant_type=authorization_code or grant_type=refresh_token request,
                // ensure the offline_access scope had been requested during the authorization request.
                // For other grant types, the offline_access scope must be present in the token request.
                if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() ?
                    validatingContext.AuthenticationTicket.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess) :
                    request.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess)) {
                    // Make sure to create a copy of the authentication properties
                    // to avoid modifying the properties set on the original ticket.
                    var properties = ticket.Properties.Copy();

                    // When sliding expiration is disabled, the refresh token added to the response
                    // cannot live longer than the refresh token that was used in the token request.
                    if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                            (Options.SystemClock.UtcNow + Options.RefreshTokenLifetime)) {
                        properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                    }

                    response.SetRefreshToken(await CreateRefreshTokenAsync(ticket.Identity, properties, request, response));
                }
            }

            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload.Add(parameter.Key, parameter.Value);
            }

            var responseNotification = new TokenEndpointResponseContext(Context, Options, ticket, request, payload);
            await Options.Provider.TokenEndpointResponse(responseNotification);

            if (responseNotification.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeValidationEndpointAsync() {
            OpenIdConnectMessage request;

            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "make sure to use either GET or POST."
                });

                return;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return;
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }
            
            AuthenticationTicket ticket;
            if (!string.IsNullOrEmpty(request.AccessToken)) {
                ticket = await ReceiveAccessTokenAsync(request.AccessToken, request);
            }

            else if (!string.IsNullOrEmpty(request.IdToken)) {
                ticket = await ReceiveIdentityTokenAsync(request.IdToken, request);
            }

            else if (!string.IsNullOrEmpty(request.GetRefreshToken())) {
                ticket = await ReceiveRefreshTokenAsync(request.GetRefreshToken(), request);
            }

            else {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "either an identity token, an access token or a refresh token must be provided."
                });

                return;
            }

            if (ticket == null) {
                Options.Logger.WriteError("invalid token");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid access token received"
                });

                return;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue || ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Options.Logger.WriteError("expired token");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired access token received"
                });

                return;
            }

            // Client applications and resource servers are strongly encouraged
            // to provide an audience parameter to mitigate confused deputy attacks.
            // See http://en.wikipedia.org/wiki/Confused_deputy_problem.
            var audiences = ticket.Properties.GetAudiences();
            if (audiences.Any() && !audiences.ContainsSet(request.GetAudiences())) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid access token received: " +
                        "the audience doesn't correspond to the registered value"
                });

                return;
            }

            var notification = new ValidationEndpointContext(Context, Options, request, ticket);

            // Add the claims extracted from the access token.
            foreach (var claim in ticket.Identity.Claims) {
                notification.Claims.Add(claim);
            }

            await Options.Provider.ValidationEndpoint(notification);

            // Flow the changes made to the authentication ticket.
            ticket = notification.AuthenticationTicket;

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();

            payload.Add("audiences", JArray.FromObject(ticket.Properties.GetAudiences()));
            payload.Add("expires_in", ticket.Properties.ExpiresUtc.Value);
            
            payload.Add("claims", JArray.FromObject(
                from claim in notification.Claims
                select new { type = claim.Type, value = claim.Value }
            ));

            var context = new ValidationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ValidationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();
                
                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task<bool> InvokeLogoutEndpointAsync() {
            OpenIdConnectMessage request = null;

            // In principle, logout requests must be made via GET. Nevertheless,
            // POST requests are also allowed so that the inner application can display a logout form.
            // See https://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed logout request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            // Store the logout request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // Note: post_logout_redirect_uri is not a mandatory parameter.
            // See http://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri)) {
                var clientNotification = new ValidateClientLogoutRedirectUriContext(Context, Options, request);
                await Options.Provider.ValidateClientLogoutRedirectUri(clientNotification);

                if (clientNotification.IsRejected) {
                    Options.Logger.WriteVerbose("Unable to validate client information");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = clientNotification.Error,
                        ErrorDescription = clientNotification.ErrorDescription,
                        ErrorUri = clientNotification.ErrorUri
                    });
                }
            }

            var notification = new LogoutEndpointContext(Context, Options, request);
            await Options.Provider.LogoutEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }

        private async Task<string> CreateAuthorizationCodeAsync(
            ClaimsIdentity identity, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response) {
            try {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null) {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null) {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.AuthorizationCodeLifetime;
                }

                // Claims in authorization codes are never filtered as they are supposed to be opaque:
                // CreateAccessTokenAsync and CreateIdentityTokenAsync are responsible of ensuring
                // that subsequent access and identity tokens are correctly filtered.
                var ticket = new AuthenticationTicket(identity, properties);

                var notification = new CreateAuthorizationCodeContext(Context, Options, request, response, ticket) {
                    DataFormat = Options.AuthorizationCodeFormat
                };

                // Sets the default authorization code serializer.
                notification.Serializer = payload => {
                    if (notification.DataFormat == null) {
                        return Task.FromResult<string>(null);
                    }

                    return Task.FromResult(notification.DataFormat.Protect(payload));
                };

                await Options.Provider.CreateAuthorizationCode(notification);

                // Treat a non-null authorization code like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.AuthorizationCode)) {
                    return notification.AuthorizationCode;
                }

                else if (notification.Skipped) {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the CreateAuthorizationCode event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                var key = GenerateKey(256 / 8);

                Options.Cache.Set(key,
                    value: await notification.SerializeTicketAsync(),
                    absoluteExpiration: ticket.Properties.ExpiresUtc.Value);

                return key;
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when serializing an authorization code.", exception);

                return null;
            }
        }

        private async Task<string> CreateAccessTokenAsync(
            ClaimsIdentity identity, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response) {
            try {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null) {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null) {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.AccessTokenLifetime;
                }

                // Create a new identity containing only the filtered claims.
                // Actors identities are also filtered (delegation scenarios).
                identity = identity.Clone(claim => {
                    // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                    if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                        string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal)) {
                        return true;
                    }

                    // Claims whose destination is not explicitly referenced or
                    // doesn't contain "token" are not included in the access token.
                    return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.Token);
                });

                var resources = request.GetResources();
                if (!resources.Any()) {
                    // When no explicit resource parameter has been included in the token request,
                    // the optional resource received during the authorization request is used instead
                    // to help reducing cases where access tokens are issued for unknown resources.
                    resources = properties.GetResources();
                }

                // Note: when used as an access token, a JWT token doesn't have to expose a "sub" claim
                // but the name identifier claim is used as a substitute when it has been explicitly added.
                // See https://tools.ietf.org/html/rfc7519#section-4.1.2
                var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub);
                if (subject == null) {
                    var identifier = identity.FindFirst(ClaimTypes.NameIdentifier);
                    if (identifier != null) {
                        identity.AddClaim(JwtRegisteredClaimNames.Sub, identifier.Value);
                    }
                }

                // Remove the ClaimTypes.NameIdentifier claims to avoid getting duplicate claims.
                // Note: the "sub" claim is automatically mapped by JwtSecurityTokenHandler
                // to ClaimTypes.NameIdentifier when validating a JWT token.
                foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier)) {
                    identity.RemoveClaim(claim);
                }

                // Create a new ticket containing the updated properties and the filtered identity.
                var ticket = new AuthenticationTicket(identity, properties);

                var notification = new CreateAccessTokenContext(Context, Options, request, response, ticket) {
                    DataFormat = Options.AccessTokenFormat,
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.AccessTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningCredentials = Options.SigningCredentials.FirstOrDefault()
                };

                foreach (var audience in resources) {
                    notification.Audiences.Add(audience);
                }

                // Sets the default access token serializer.
                notification.Serializer = payload => {
                    if (notification.SecurityTokenHandler == null) {
                        if (notification.DataFormat == null) {
                            return null;
                        }

                        return Task.FromResult(notification.DataFormat.Protect(payload));
                    }

                    var handler = notification.SecurityTokenHandler as JwtSecurityTokenHandler;
                    if (handler != null) {
                        // When creating an access token intended for a single audience, it's usually better
                        // to format the "aud" claim as a string, but CreateToken doesn't support multiple audiences:
                        // to work around this limitation, audience is initialized with a single resource and
                        // JwtPayload.Aud is replaced with an array containing the multiple resources if necessary.
                        // See https://tools.ietf.org/html/draft-ietf-oauth-json-web-token-32#section-4.1.3
                        var token = handler.CreateToken(
                            subject: payload.Identity,
                            issuer: notification.Issuer,
                            audience: notification.Audiences.ElementAtOrDefault(0),
                            signatureProvider: notification.SignatureProvider,
                            signingCredentials: notification.SigningCredentials,
                            notBefore: notification.AuthenticationTicket.Properties.IssuedUtc.Value.UtcDateTime,
                            expires: notification.AuthenticationTicket.Properties.ExpiresUtc.Value.UtcDateTime);

                        if (notification.Audiences.Count() > 1) {
                            token.Payload[JwtRegisteredClaimNames.Aud] = notification.Audiences.ToArray();
                        }

                        if (notification.SigningCredentials != null) {
                            var x509SecurityKey = notification.SigningCredentials.SigningKey as X509SecurityKey;
                            if (x509SecurityKey != null) {
                                // Note: "x5t" is only added by JwtHeader's constructor if SigningCredentials is a X509SigningCredentials instance.
                                // To work around this limitation, "x5t" is manually added if a certificate can be extracted from a X509SecurityKey
                                token.Header[JwtHeaderParameterNames.X5t] = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash());
                            }

                            object identifier;
                            if (!token.Header.TryGetValue(JwtHeaderParameterNames.Kid, out identifier) || identifier == null) {
                                // When no key identifier has been explicitly added, a "kid" is automatically
                                // inferred from the hexadecimal representation of the certificate thumbprint.
                                if (x509SecurityKey != null) {
                                    identifier = x509SecurityKey.Certificate.Thumbprint;
                                }

                                // When no key identifier has been explicitly added by the developer, a "kid"
                                // is automatically inferred from the modulus if the signing key is a RSA key.
                                var rsaSecurityKey = notification.SigningCredentials.SigningKey as RsaSecurityKey;
                                if (rsaSecurityKey != null) {
                                    var algorithm = (RSA) rsaSecurityKey.GetAsymmetricAlgorithm(
                                        SecurityAlgorithms.RsaSha256Signature, false);

                                    // Export the RSA public key.
                                    var parameters = algorithm.ExportParameters(includePrivateParameters: false);

                                    // Only use the 40 first chars to match the identifier used by the JWKS endpoint.
                                    identifier = Base64UrlEncoder.Encode(parameters.Modulus)
                                                                 .Substring(0, 40)
                                                                 .ToUpperInvariant();
                                }

                                token.Header[JwtHeaderParameterNames.Kid] = identifier;
                            }
                        }

                        return Task.FromResult(handler.WriteToken(token));
                    }

                    else {
                        var token = notification.SecurityTokenHandler.CreateToken(new SecurityTokenDescriptor {
                            Subject = notification.AuthenticationTicket.Identity,
                            AppliesToAddress = notification.Audiences.ElementAtOrDefault(0),
                            TokenIssuerName = notification.Issuer,
                            SigningCredentials = notification.SigningCredentials,
                            Lifetime = new Lifetime(
                                notification.AuthenticationTicket.Properties.IssuedUtc.Value.UtcDateTime,
                                notification.AuthenticationTicket.Properties.ExpiresUtc.Value.UtcDateTime)
                        });

                        return Task.FromResult(notification.SecurityTokenHandler.WriteToken(token));
                    }
                };

                await Options.Provider.CreateAccessToken(notification);

                // Treat a non-null access token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.AccessToken)) {
                    return notification.AccessToken;
                }

                else if (notification.Skipped) {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the CreateAccessTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when serializing an access token.", exception);

                return null;
            }
        }

        private async Task<string> CreateIdentityTokenAsync(
            ClaimsIdentity identity, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response) {
            try {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null) {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null) {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.IdentityTokenLifetime;
                }

                // Replace the identity by a new one containing only the filtered claims.
                // Actors identities are also filtered (delegation scenarios).
                identity = identity.Clone(claim => {
                    // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                    if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                        string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal)) {
                        return true;
                    }

                    // Claims whose destination is not explicitly referenced or
                    // doesn't contain "id_token" are not included in the identity token.
                    return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.IdToken);
                });

                identity.AddClaim(JwtRegisteredClaimNames.Iat,
                    EpochTime.GetIntDate(properties.IssuedUtc.Value.UtcDateTime).ToString());

                if (!string.IsNullOrEmpty(response.Code)) {
                    // Create the c_hash using the authorization code returned by CreateAuthorizationCodeAsync.
                    var hash = GenerateHash(response.Code, SecurityAlgorithms.Sha256Digest);

                    identity.AddClaim(JwtRegisteredClaimNames.CHash, hash);
                }

                if (!string.IsNullOrEmpty(response.AccessToken)) {
                    // Create the at_hash using the access token returned by CreateAccessTokenAsync.
                    var hash = GenerateHash(response.AccessToken, SecurityAlgorithms.Sha256Digest);

                    identity.AddClaim("at_hash", hash);
                }

                var nonce = request.Nonce;

                // If a nonce was present in the authorization request, it MUST
                // be included in the id_token generated by the token endpoint.
                // See http://openid.net/specs/openid-connect-core-1_0.html#IDTokenValidation
                if (request.IsAuthorizationCodeGrantType()) {
                    // Restore the nonce stored in the authentication
                    // ticket extracted from the authorization code.
                    nonce = properties.GetNonce();
                }

                if (!string.IsNullOrEmpty(nonce)) {
                    identity.AddClaim(JwtRegisteredClaimNames.Nonce, nonce);
                }

                // While the 'sub' claim is declared mandatory by the OIDC specs,
                // it is not always issued as-is by the authorization servers.
                // When missing, the name identifier claim is used as a substitute.
                // See http://openid.net/specs/openid-connect-core-1_0.html#IDToken
                var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub);
                if (subject == null) {
                    var identifier = identity.FindFirst(ClaimTypes.NameIdentifier);
                    if (identifier == null) {
                        throw new InvalidOperationException(
                            "A unique identifier cannot be found to generate a 'sub' claim. " +
                            "Make sure to either add a 'sub' or a 'ClaimTypes.NameIdentifier' claim " +
                            "in the returned ClaimsIdentity before calling SignIn.");
                    }

                    identity.AddClaim(JwtRegisteredClaimNames.Sub, identifier.Value);
                }

                // Remove the ClaimTypes.NameIdentifier claims to avoid getting duplicate claims.
                // Note: the "sub" claim is automatically mapped by JwtSecurityTokenHandler
                // to ClaimTypes.NameIdentifier when validating a JWT token.
                foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier)) {
                    identity.RemoveClaim(claim);
                }

                // Create a new ticket containing the updated properties and the filtered identity.
                var ticket = new AuthenticationTicket(identity, properties);

                var notification = new CreateIdentityTokenContext(Context, Options, request, response, ticket) {
                    Audiences = { request.ClientId },
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.IdentityTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningCredentials = Options.SigningCredentials.FirstOrDefault()
                };

                // Sets the default identity token serializer.
                notification.Serializer = payload => {
                    if (notification.SecurityTokenHandler == null) {
                        return Task.FromResult<string>(null);
                    }

                    // When creating an identity token intended for a single audience, it's usually better
                    // to format the "aud" claim as a string, but CreateToken doesn't support multiple audiences:
                    // to work around this limitation, audience is initialized with a single resource and
                    // JwtPayload.Aud is replaced with an array containing the multiple resources if necessary.
                    // See http://openid.net/specs/openid-connect-core-1_0.html#IDToken
                    var token = notification.SecurityTokenHandler.CreateToken(
                        subject: notification.AuthenticationTicket.Identity,
                        issuer: notification.Issuer,
                        audience: notification.Audiences.ElementAtOrDefault(0),
                        signatureProvider: notification.SignatureProvider,
                        signingCredentials: notification.SigningCredentials,
                        notBefore: notification.AuthenticationTicket.Properties.IssuedUtc.Value.UtcDateTime,
                        expires: notification.AuthenticationTicket.Properties.ExpiresUtc.Value.UtcDateTime);

                    if (notification.Audiences.Count() > 1) {
                        token.Payload[JwtRegisteredClaimNames.Aud] = notification.Audiences.ToArray();
                    }

                    if (notification.SigningCredentials != null) {
                        var x509SecurityKey = notification.SigningCredentials.SigningKey as X509SecurityKey;
                        if (x509SecurityKey != null) {
                            // Note: "x5t" is only added by JwtHeader's constructor if SigningCredentials is a X509SigningCredentials instance.
                            // To work around this limitation, "x5t" is manually added if a certificate can be extracted from a X509SecurityKey
                            token.Header[JwtHeaderParameterNames.X5t] = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash());
                        }

                        object identifier;
                        if (!token.Header.TryGetValue(JwtHeaderParameterNames.Kid, out identifier) || identifier == null) {
                            // When no key identifier has been explicitly added, a "kid" is automatically
                            // inferred from the hexadecimal representation of the certificate thumbprint.
                            if (x509SecurityKey != null) {
                                identifier = x509SecurityKey.Certificate.Thumbprint;
                            }

                            // When no key identifier has been explicitly added by the developer, a "kid"
                            // is automatically inferred from the modulus if the signing key is a RSA key.
                            var rsaSecurityKey = notification.SigningCredentials.SigningKey as RsaSecurityKey;
                            if (rsaSecurityKey != null) {
                                var algorithm = (RSA) rsaSecurityKey.GetAsymmetricAlgorithm(
                                    SecurityAlgorithms.RsaSha256Signature, false);

                                // Export the RSA public key.
                                var parameters = algorithm.ExportParameters(includePrivateParameters: false);

                                // Only use the 40 first chars to match the identifier used by the JWKS endpoint.
                                identifier = Base64UrlEncoder.Encode(parameters.Modulus)
                                                             .Substring(0, 40)
                                                             .ToUpperInvariant();
                            }

                            token.Header[JwtHeaderParameterNames.Kid] = identifier;
                        }
                    }

                    return Task.FromResult(notification.SecurityTokenHandler.WriteToken(token));
                };

                await Options.Provider.CreateIdentityToken(notification);

                // Treat a non-null identity token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.IdentityToken)) {
                    return notification.IdentityToken;
                }

                else if (notification.Skipped) {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the CreateIdentityTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when serializing an identity token.", exception);

                return null;
            }
        }

        private async Task<string> CreateRefreshTokenAsync(
            ClaimsIdentity identity, AuthenticationProperties properties,
            OpenIdConnectMessage request, OpenIdConnectMessage response) {
            try {
                // properties.IssuedUtc and properties.ExpiresUtc
                // should always be preferred when explicitly set.
                if (properties.IssuedUtc == null) {
                    properties.IssuedUtc = Options.SystemClock.UtcNow;
                }

                if (properties.ExpiresUtc == null) {
                    properties.ExpiresUtc = properties.IssuedUtc + Options.RefreshTokenLifetime;
                }

                // Claims in refresh tokens are never filtered as they are supposed to be opaque:
                // CreateAccessTokenAsync and CreateIdentityTokenAsync are responsible of ensuring
                // that subsequent access and identity tokens are correctly filtered.
                var ticket = new AuthenticationTicket(identity, properties);

                var notification = new CreateRefreshTokenContext(Context, Options, request, response, ticket) {
                    DataFormat = Options.RefreshTokenFormat
                };

                // Sets the default refresh token serializer.
                notification.Serializer = payload => {
                    if (notification.DataFormat == null) {
                        return Task.FromResult<string>(null);
                    }

                    return Task.FromResult(notification.DataFormat.Protect(payload));
                };

                await Options.Provider.CreateRefreshToken(notification);

                // Treat a non-null refresh token like an implicit HandleResponse call.
                if (notification.HandledResponse || !string.IsNullOrEmpty(notification.RefreshToken)) {
                    return notification.RefreshToken;
                }

                else if (notification.Skipped) {
                    return null;
                }

                // Allow the application to change the authentication
                // ticket from the CreateRefreshTokenAsync event.
                ticket = notification.AuthenticationTicket;
                ticket.Properties.CopyTo(properties);

                return await notification.SerializeTicketAsync();
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when serializing a refresh token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> ReceiveAuthorizationCodeAsync(string code, OpenIdConnectMessage request) {
            try {
                var notification = new ReceiveAuthorizationCodeContext(Context, Options, request, code) {
                    DataFormat = Options.AuthorizationCodeFormat
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the authorization code.
                notification.Deserializer = ticket => {
                    if (notification.DataFormat == null) {
                        return Task.FromResult<AuthenticationTicket>(null);
                    }

                    return Task.FromResult(notification.DataFormat.Unprotect(ticket));
                };

                await Options.Provider.ReceiveAuthorizationCode(notification);

                // Directly return the authentication ticket if one
                // has been provided by ReceiveAuthorizationCode.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null) {
                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped) {
                    return null;
                }

                var payload = (string) Options.Cache.Get(code);
                if (payload == null) {
                    return null;
                }

                // Because authorization codes are guaranteed to be unique, make sure
                // to remove the current code from the global store before using it.
                Options.Cache.Remove(code);

                return await notification.DeserializeTicketAsync(payload);
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when deserializing an authorization code.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> ReceiveAccessTokenAsync(string token, OpenIdConnectMessage request) {
            try {
                var notification = new ReceiveAccessTokenContext(Context, Options, request, token) {
                    DataFormat = Options.AccessTokenFormat,
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.AccessTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningKey = Options.SigningCredentials.Select(credentials => credentials.SigningKey)
                                                           .FirstOrDefault()
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the access token.
                notification.Deserializer = ticket => {
                    var handler = notification.SecurityTokenHandler as ISecurityTokenValidator;
                    if (handler == null) {
                        if (notification.DataFormat == null) {
                            return null;
                        }

                        return Task.FromResult(notification.DataFormat.Unprotect(ticket));
                    }

                    // Create new validation parameters to validate the security token.
                    // ValidateAudience and ValidateLifetime are always set to false:
                    // if necessary, the audience and the expiration can be validated
                    // in InvokeValidationEndpointAsync or InvokeTokenEndpointAsync.
                    var parameters = new TokenValidationParameters {
                        IssuerSigningKey = notification.SigningKey,
                        ValidIssuer = notification.Issuer,
                        ValidateAudience = false,
                        ValidateLifetime = false
                    };

                    SecurityToken securityToken;
                    var principal = handler.ValidateToken(ticket, parameters, out securityToken);

                    // Parameters stored in AuthenticationProperties are lost
                    // when the identity token is serialized using a security token handler.
                    // To mitigate that, they are inferred from the claims or the security token.
                    var properties = new AuthenticationProperties {
                        ExpiresUtc = securityToken.ValidTo,
                        IssuedUtc = securityToken.ValidFrom
                    };

                    var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud);
                    if (audiences.Any()) {
                        properties.SetAudiences(audiences.Select(claim => claim.Value));
                    }

                    return Task.FromResult(new AuthenticationTicket((ClaimsIdentity) principal.Identity, properties));
                };

                await Options.Provider.ReceiveAccessToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by ReceiveAccessToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null) {
                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped) {
                    return null;
                }

                return await notification.DeserializeTicketAsync(token);
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when deserializing an access token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> ReceiveIdentityTokenAsync(string token, OpenIdConnectMessage request) {
            try {
                var notification = new ReceiveIdentityTokenContext(Context, Options, request, token) {
                    Issuer = Context.GetIssuer(Options),
                    SecurityTokenHandler = Options.IdentityTokenHandler,
                    SignatureProvider = Options.SignatureProvider,
                    SigningKey = Options.SigningCredentials.Select(credentials => credentials.SigningKey)
                                                           .FirstOrDefault()
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the identity token.
                notification.Deserializer = ticket => {
                    if (notification.SecurityTokenHandler == null) {
                        return Task.FromResult<AuthenticationTicket>(null);
                    }

                    // Create new validation parameters to validate the security token.
                    // ValidateAudience and ValidateLifetime are always set to false:
                    // if necessary, the audience and the expiration can be validated
                    // in InvokeValidationEndpointAsync or InvokeTokenEndpointAsync.
                    var parameters = new TokenValidationParameters {
                        IssuerSigningKey = notification.SigningKey,
                        ValidIssuer = notification.Issuer,
                        ValidateAudience = false,
                        ValidateLifetime = false
                    };

                    SecurityToken securityToken;
                    var principal = notification.SecurityTokenHandler.ValidateToken(ticket, parameters, out securityToken);

                    // Parameters stored in AuthenticationProperties are lost
                    // when the identity token is serialized using a security token handler.
                    // To mitigate that, they are inferred from the claims or the security token.
                    var properties = new AuthenticationProperties {
                        ExpiresUtc = securityToken.ValidTo,
                        IssuedUtc = securityToken.ValidFrom
                    };

                    var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud);
                    if (audiences.Any()) {
                        properties.SetAudiences(audiences.Select(claim => claim.Value));
                    }

                    return Task.FromResult(new AuthenticationTicket((ClaimsIdentity) principal.Identity, properties));
                };

                await Options.Provider.ReceiveIdentityToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by ReceiveIdentityToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null) {
                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped) {
                    return null;
                }

                return await notification.DeserializeTicketAsync(token);
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when deserializing an identity token.", exception);

                return null;
            }
        }

        private async Task<AuthenticationTicket> ReceiveRefreshTokenAsync(string token, OpenIdConnectMessage request) {
            try {
                var notification = new ReceiveRefreshTokenContext(Context, Options, request, token) {
                    DataFormat = Options.RefreshTokenFormat
                };

                // Sets the default deserializer used to resolve the
                // authentication ticket corresponding to the refresh token.
                notification.Deserializer = ticket => {
                    if (notification.DataFormat == null) {
                        return Task.FromResult<AuthenticationTicket>(null);
                    }

                    return Task.FromResult(notification.DataFormat.Unprotect(ticket));
                };

                await Options.Provider.ReceiveRefreshToken(notification);

                // Directly return the authentication ticket if one
                // has been provided by ReceiveRefreshToken.
                // Treat a non-null ticket like an implicit HandleResponse call.
                if (notification.HandledResponse || notification.AuthenticationTicket != null) {
                    return notification.AuthenticationTicket;
                }

                else if (notification.Skipped) {
                    return null;
                }

                return await notification.DeserializeTicketAsync(token);
            }

            catch (Exception exception) {
                Options.Logger.WriteWarning("An exception occured when deserializing a refresh token.", exception);

                return null;
            }
        }

        private async Task<bool> SendErrorRedirectAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Remove the authorization request from the OWIN context to inform
            // TeardownCoreAsync that there's nothing more to handle.
            Context.SetOpenIdConnectRequest(request: null);

            // Use a generic error if none has been explicitly provided.
            if (string.IsNullOrEmpty(response.Error)) {
                response.Error = OpenIdConnectConstants.Errors.InvalidRequest;
            }

            // Directly display an error page if redirect_uri cannot be used.
            if (string.IsNullOrEmpty(response.RedirectUri)) {
                return await SendErrorPageAsync(response);
            }

            // Try redirecting the user agent to the client
            // application or display a default error page.
            if (!await ApplyAuthorizationResponseAsync(request, response)) {
                return await SendErrorPageAsync(response);
            }

            // Stop processing the request.
            return true;
        }

        private async Task<bool> SendErrorPageAsync(OpenIdConnectMessage response) {
            // Use a generic error if none has been explicitly provided.
            if (string.IsNullOrEmpty(response.Error)) {
                response.Error = OpenIdConnectConstants.Errors.InvalidRequest;
            }

            if (Options.ApplicationCanDisplayErrors) {
                Context.SetOpenIdConnectResponse(response);

                // Request is not handled - pass through to application for rendering.
                return false;
            }

            await SendNativeErrorPageAsync(response);

            // Request is always handled when rendering the default error page.
            return true;
        }

        private async Task SendNativeErrorPageAsync(OpenIdConnectMessage response) {
            using (var buffer = new MemoryStream())
            using (var writer = new StreamWriter(buffer)) {
                foreach (var parameter in response.Parameters) {
                    writer.WriteLine("{0}: {1}", parameter.Key, parameter.Value);
                }

                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "text/plain;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task SendErrorPayloadAsync(OpenIdConnectMessage response) {
            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                var payload = new JObject();

                payload.Add(OpenIdConnectConstants.Parameters.Error, response.Error);

                if (!string.IsNullOrEmpty(response.ErrorDescription)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorDescription, response.ErrorDescription);
                }

                if (!string.IsNullOrEmpty(response.ErrorUri)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorUri, response.ErrorUri);
                }

                payload.WriteTo(writer);
                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private static string GenerateHash(string value, string algorithm = null) {
            using (var hashAlgorithm = HashAlgorithm.Create(algorithm)) {
                byte[] hashBytes = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(value));

                var hashString = Convert.ToBase64String(hashBytes, 0, hashBytes.Length / 2);
                hashString = hashString.Split('=')[0]; // Remove any trailing padding
                hashString = hashString.Replace('+', '-'); // 62nd char of encoding
                return hashString.Replace('/', '_'); // 63rd char of encoding
            }
        }

        private string GenerateKey(int length) {
            var bytes = new byte[length];
            Options.RandomNumberGenerator.GetBytes(bytes);
            return Base64UrlEncoder.Encode(bytes);
        }

        private class Appender {
            private readonly char _delimiter;
            private readonly StringBuilder _sb;
            private bool _hasDelimiter;

            public Appender(string value, char delimiter) {
                _sb = new StringBuilder(value);
                _delimiter = delimiter;
                _hasDelimiter = value.IndexOf(delimiter) != -1;
            }

            public Appender Append(string name, string value) {
                _sb.Append(_hasDelimiter ? '&' : _delimiter)
                   .Append(Uri.EscapeDataString(name))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value));
                _hasDelimiter = true;
                return this;
            }

            public override string ToString() {
                return _sb.ToString();
            }
        }
    }
}
