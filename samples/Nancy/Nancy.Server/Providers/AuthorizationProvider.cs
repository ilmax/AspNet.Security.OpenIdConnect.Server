﻿using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Nancy.Server.Models;
using Owin.Security.OpenIdConnect.Server;

namespace Nancy.Server.Providers {
    public class AuthorizationProvider : OpenIdConnectServerProvider {
        public override async Task ValidateClientAuthentication(ValidateClientAuthenticationContext notification) {
            if (string.IsNullOrEmpty(notification.ClientId) || string.IsNullOrEmpty(notification.ClientSecret)) {
                notification.Rejected(
                    error: "invalid_request",
                    description: "Missing credentials: ensure that your credentials were correctly " +
                                      "flowed in the request body or in the authorization header");

                return;
            }

            using (var context = new ApplicationContext()) {
                // Retrieve the application details corresponding to the requested client_id.
                var application = await (from entity in context.Applications
                                         where entity.ApplicationID == notification.ClientId
                                         select entity).SingleOrDefaultAsync(notification.OwinContext.Request.CallCancelled);

                if (application == null) {
                    notification.Rejected(
                        error: "invalid_client",
                        description: "Application not found in the database: " +
                                          "ensure that your client_id is correct");
                    return;
                }

                if (!string.Equals(notification.ClientSecret, application.Secret, StringComparison.Ordinal)) {
                    notification.Rejected(
                        error: "invalid_client",
                        description: "Invalid credentials: ensure that you " +
                                          "specified a correct client_secret");

                    return;
                }

                notification.Validated();
            }
        }

        public override async Task ValidateClientRedirectUri(ValidateClientRedirectUriContext notification) {
            using (var context = new ApplicationContext()) {
                // Retrieve the application details corresponding to the requested client_id.
                var application = await (from entity in context.Applications
                                         where entity.ApplicationID == notification.ClientId
                                         select entity).SingleOrDefaultAsync(notification.OwinContext.Request.CallCancelled);

                if (application == null) {
                    notification.Rejected(
                        error: "invalid_client",
                        description: "Application not found in the database: " +
                                          "ensure that your client_id is correct");
                    return;
                }

                if (!string.IsNullOrEmpty(notification.RedirectUri)) {
                    if (!string.Equals(notification.RedirectUri, application.RedirectUri, StringComparison.Ordinal)) {
                        notification.Rejected(
                            error: "invalid_client",
                            description: "Invalid redirect_uri");

                        return;
                    }
                }

                notification.Validated(application.RedirectUri);
            }
        }

        public override async Task ValidateClientLogoutRedirectUri(ValidateClientLogoutRedirectUriContext notification) {
            using (var context = new ApplicationContext()) {
                if (!await context.Applications.AnyAsync(application => application.LogoutRedirectUri == notification.PostLogoutRedirectUri)) {
                    notification.Rejected(
                            error: "invalid_client",
                            description: "Invalid post_logout_redirect_uri");

                    return;
                }

                notification.Validated();
            }
        }

        public override Task MatchEndpoint(MatchEndpointContext notification) {
            // Note: by default, OpenIdConnectServerHandler only handles authorization requests made to the authorization endpoint.
            // This notification handler uses a more relaxed policy that allows extracting authorization requests received at
            // /connect/authorize/accept and /connect/authorize/deny (see AuthorizationController.cs for more information).
            if (notification.Request.Path.StartsWithSegments(notification.Options.AuthorizationEndpointPath)) {
                notification.MatchesAuthorizationEndpoint();
            }

            return Task.FromResult<object>(null);
        }
    }
}